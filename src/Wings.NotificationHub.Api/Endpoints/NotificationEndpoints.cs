using System.Text;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Delivery;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record ResolveDeliveryRequest(string Action, string? Comment);

public static class NotificationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization("app-or-admin");

        g.MapPost("/send", async (SendNotificationRequest req, HttpContext ctx, NotificationService svc) =>
        {
            req.IdempotencyKey ??= ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            req.CorrelationId ??= ctx.Items[CorrelationIdMiddleware.Header] as string;
            var caller = await ctx.CallerAsync();
            var resp = await svc.AcceptAsync(caller, req, caller.Kind == CallerKind.AdminUser ? RequestSource.Admin : RequestSource.Api, null, ctx.RequestAborted);
            return resp.Duplicate || req.DryRun ? Results.Ok(resp) : Results.Accepted(resp.TrackingUrl, resp);
        }).WithSummary("Send a notification on one or more channels (202 = durably accepted, not delivered)")
          .Produces<SendNotificationResponse>(202).Produces<ApiError>(422);

        g.MapPost("/schedule", async (SendNotificationRequest req, HttpContext ctx, NotificationService svc) =>
        {
            if (req.ScheduleAt is null) throw HubException.Validation("scheduleAt is required.");
            req.IdempotencyKey ??= ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var caller = await ctx.CallerAsync();
            var resp = await svc.AcceptAsync(caller, req, RequestSource.Api, null, ctx.RequestAborted);
            return Results.Accepted(resp.TrackingUrl, resp);
        }).WithSummary("Schedule a notification for later delivery");

        g.MapPost("/batch", async (BatchSendRequest req, HttpContext ctx, IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<HubOptions> options) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.HasScope(Scopes.NotificationsBatch)) throw new AccessDeniedException("The credential lacks the notifications.batch scope.");
            if (caller.IsAiAgent) throw new HubException(403, "ai_not_permitted", "AI agents may not use batch messaging.");
            if (req.Notifications.Count == 0 || req.Notifications.Count > options.Value.MaxBatchSize)
                throw HubException.Validation($"A batch must contain 1–{options.Value.MaxBatchSize} notifications.");
            var result = new BatchSendResponse();
            for (var i = 0; i < req.Notifications.Count; i++)
            {
                // Each item is its own unit of work: one invalid item never blocks the rest.
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<NotificationService>();
                try
                {
                    var r = await svc.AcceptAsync(caller, req.Notifications[i], RequestSource.Batch, null, ctx.RequestAborted);
                    result.Results.Add(new BatchItemResult { Index = i, NotificationId = r.NotificationId, Status = r.Status, Duplicate = r.Duplicate });
                    result.Accepted++;
                }
                catch (HubException ex)
                {
                    result.Results.Add(new BatchItemResult { Index = i, Status = "rejected", Error = new ApiError { Code = ex.Code, Message = ex.Message, Errors = ex.Errors } });
                    result.Rejected++;
                }
                catch (AccessDeniedException ex)
                {
                    result.Results.Add(new BatchItemResult { Index = i, Status = "rejected", Error = new ApiError { Code = "forbidden", Message = ex.Message } });
                    result.Rejected++;
                }
            }
            return Results.Ok(result);
        }).WithSummary("Send up to 1000 notifications; each item succeeds or fails independently");

        g.MapGet("/{id:guid}/status", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var (req, deliveries) = await LoadAsync(ctx, db, id);
            var templateVersions = deliveries.Select(d => d.TemplateVersionId).Distinct().ToList();
            var providers = await ProviderNamesAsync(db, deliveries.Select(d => d.ProviderAccountId));
            return Results.Ok(new NotificationStatusDto
            {
                NotificationId = req.Id, TenantId = req.TenantId, CompanyId = req.CompanyId, ApplicationId = req.ApplicationId, NotificationType = req.NotificationTypeCode,
                TemplateCode = req.TemplateCode, Status = req.Status, Source = req.Source, Priority = req.Priority, IdempotencyKey = req.IdempotencyKey,
                CorrelationId = req.CorrelationId, CreatedAt = Utc(req.CreatedAt), ScheduleAt = Utc(req.ScheduleAt), CompletedAt = Utc(req.CompletedAt),
                Metadata = Json.Deserialize<Dictionary<string, string>>(req.MetadataJson),
                Deliveries = deliveries.Select(d => Mapper.Delivery(d, providers)).ToList(),
            });
        }).WithSummary("Aggregate and per-channel delivery status");

        g.MapGet("/{id:guid}/history", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var (req, deliveries) = await LoadAsync(ctx, db, id);
            var ids = deliveries.Select(d => d.Id).ToList();
            var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => ids.Contains(a.DeliveryId)).OrderBy(a => a.StartedAt).ToListAsync(ctx.RequestAborted);
            var providers = await ProviderNamesAsync(db, deliveries.Select(d => d.ProviderAccountId).Concat(attempts.Select(a => a.ProviderAccountId)));
            var events = await db.DeliveryEvents.AsNoTracking().Where(e => e.DeliveryId != null && ids.Contains(e.DeliveryId.Value)).OrderBy(e => e.RecordedAt).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new DeliveryTimelineDto
            {
                NotificationId = req.Id, Deliveries = deliveries.Select(d => Mapper.Delivery(d, providers)).ToList(),
                Attempts = attempts.Select(a => Mapper.Attempt(a, providers)).ToList(), Events = events.Select(Mapper.Event).ToList(),
            });
        }).WithSummary("Immutable delivery timeline: attempts and events");

        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext ctx, NotificationHubDbContext db, DeliveryStatusUpdater status, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var req = await db.NotificationRequests.FirstOrDefaultAsync(r => r.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Notification");
            if (!caller.Can(Permission.CancelNotifications, req.TenantId, req.CompanyId, req.ApplicationId)) throw HubException.NotFound("Notification");
            if (req.Status == RequestStatus.PendingApproval)
            {
                req.Status = RequestStatus.Cancelled;
                req.CompletedAt = DateTime.UtcNow;
                foreach (var a in await db.ApprovalRequests.Where(a => a.EntityId == id && a.Status == ApprovalStatus.Pending).ToListAsync(ctx.RequestAborted)) a.Status = ApprovalStatus.Cancelled;
            }
            var cancellable = await db.NotificationDeliveries.Where(d => d.RequestId == id &&
                (d.Status == DeliveryStatus.Scheduled || d.Status == DeliveryStatus.Queued || d.Status == DeliveryStatus.RetryPending || (d.Status == DeliveryStatus.Unknown && d.AwaitingOperator))).ToListAsync(ctx.RequestAborted);
            foreach (var d in cancellable) await status.SetAsync(d, DeliveryStatus.Cancelled, "api", $"Cancelled by {caller.ActorName ?? caller.ActorId}", ctx.RequestAborted);
            audit.Record(caller, "notification.cancelled", nameof(NotificationRequest), id.ToString(), $"{cancellable.Count} deliveries cancelled", req.TenantId, req.CompanyId, req.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            var remaining = await db.NotificationDeliveries.CountAsync(d => d.RequestId == id && (d.Status == DeliveryStatus.Processing || d.Status == DeliveryStatus.Submitted), ctx.RequestAborted);
            return Results.Ok(new { cancelled = cancellable.Count, inFlight = remaining, note = remaining > 0 ? "Deliveries already being submitted cannot be recalled." : null });
        }).WithSummary("Cancel pending (scheduled/queued/retrying) deliveries");

        g.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? companyId, Guid? applicationId, RequestStatus? status, string? notificationType,
            string? correlationId, DateTime? from, DateTime? to, int page = 1, int pageSize = 50) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.NotificationRequests.AsNoTracking().Visible(caller.FilterFor(Permission.ViewNotifications));
            if (companyId is { } c) q = q.Where(r => r.CompanyId == c);
            if (applicationId is { } a) q = q.Where(r => r.ApplicationId == a);
            if (status is { } s) q = q.Where(r => r.Status == s);
            if (!string.IsNullOrEmpty(notificationType)) q = q.Where(r => r.NotificationTypeCode == notificationType);
            if (!string.IsNullOrEmpty(correlationId)) q = q.Where(r => r.CorrelationId == correlationId);
            if (from is { } f) q = q.Where(r => r.CreatedAt >= f.ToUniversalTime());
            if (to is { } t) q = q.Where(r => r.CreatedAt < t.ToUniversalTime());
            pageSize = Math.Clamp(pageSize, 1, 200);
            var total = await q.CountAsync(ctx.RequestAborted);
            var items = await q.OrderByDescending(r => r.CreatedAt).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
                .Select(r => new
                {
                    notificationId = r.Id, r.CompanyId, r.ApplicationId, notificationType = r.NotificationTypeCode, r.TemplateCode, r.Status, r.Source, r.Priority,
                    r.CorrelationId, r.IdempotencyKey, r.CreatedAt, r.CompletedAt, deliveries = r.Deliveries.Count,
                }).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { items, page, pageSize, total });
        }).WithSummary("List notification requests (paged, filterable)");

        g.MapPost("/preview", async (TemplatePreviewRequest req, HttpContext ctx, TemplateService templates) =>
            Results.Ok(await templates.PreviewAsync(await ctx.CallerAsync(), req, ctx.RequestAborted))).WithSummary("Render a template with sample or supplied variables");

        // ── delivery explorer (admin) ──
        var d = api.MapGroup("/deliveries").WithTags("Delivery explorer").RequireAuthorization("app-or-admin");
        d.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, [AsParameters] DeliveryQuery query) =>
        {
            var caller = await ctx.CallerAsync();
            var q = Filter(db, db.NotificationDeliveries.AsNoTracking().Visible(caller.FilterFor(Permission.ViewNotifications)), query);
            var total = await q.CountAsync(ctx.RequestAborted);
            q = query.Sort switch
            {
                "createdAt" => q.OrderBy(x => x.CreatedAt),
                "attempts" => q.OrderByDescending(x => x.AttemptCount),
                "cost" => q.OrderByDescending(x => x.Cost),
                _ => q.OrderByDescending(x => x.CreatedAt),
            };
            var pageSize = Math.Clamp(query.PageSize ?? 50, 1, 500);
            var rows = await q.Skip((Math.Max(1, query.Page ?? 1) - 1) * pageSize).Take(pageSize).ToListAsync(ctx.RequestAborted);
            var lookups = await Lookups.LoadAsync(db, rows, ctx.RequestAborted);
            var canSeeAddresses = rows.Count == 0 || caller.Can(Permission.ViewMessageContent, rows[0].TenantId, rows[0].CompanyId, rows[0].ApplicationId);
            return Results.Ok(new { items = rows.Select(r => Mapper.ExplorerRow(r, lookups, canSeeAddresses && caller.Can(Permission.ViewMessageContent, r.TenantId, r.CompanyId, r.ApplicationId))), page = query.Page ?? 1, pageSize, total });
        }).WithSummary("Notification explorer: search deliveries");

        d.MapGet("/export", async (HttpContext ctx, NotificationHubDbContext db, [AsParameters] DeliveryQuery query, string format = "csv") =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await Filter(db, db.NotificationDeliveries.AsNoTracking().Visible(caller.FilterFor(Permission.ViewNotifications)), query)
                .OrderByDescending(x => x.CreatedAt).Take(50_000).ToListAsync(ctx.RequestAborted);
            var lookups = await Lookups.LoadAsync(db, rows, ctx.RequestAborted);
            var table = rows.Select(r => Mapper.ExplorerRow(r, lookups, false)).Select(x => new Dictionary<string, object?>
            {
                ["CreatedAt"] = x.CreatedAt, ["NotificationId"] = x.NotificationId, ["DeliveryId"] = x.DeliveryId, ["Application"] = x.Application, ["Company"] = x.Company,
                ["Recipient"] = x.Recipient, ["NotificationType"] = x.NotificationType, ["Channel"] = x.Channel, ["Provider"] = x.Provider, ["Status"] = x.Status,
                ["Attempts"] = x.Attempts, ["Cost"] = x.Cost, ["FailureCode"] = x.FailureCode,
            }).ToList();
            return Exporter.Export(table, "deliveries", format);
        }).WithSummary("Export explorer results as CSV or Excel (addresses masked)");

        d.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector) =>
        {
            var caller = await ctx.CallerAsync();
            var x = await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(z => z.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Delivery");
            if (!caller.Can(Permission.ViewNotifications, x.TenantId, x.CompanyId, x.ApplicationId) || (caller.Kind == CallerKind.Application && caller.ApplicationId != x.ApplicationId))
                throw HubException.NotFound("Delivery");
            var request = await db.NotificationRequests.AsNoTracking().FirstAsync(r => r.Id == x.RequestId, ctx.RequestAborted);
            var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.AttemptNumber).ToListAsync(ctx.RequestAborted);
            var providers = await ProviderNamesAsync(db, attempts.Select(a => a.ProviderAccountId).Append(x.ProviderAccountId));
            var events = await db.DeliveryEvents.AsNoTracking().Where(e => e.DeliveryId == id).OrderBy(e => e.RecordedAt).ToListAsync(ctx.RequestAborted);
            var canSee = caller.Can(Permission.ViewMessageContent, x.TenantId, x.CompanyId, x.ApplicationId);
            RenderedContent? content = null;
            if (canSee && x.ContentCipher is not null)
            {
                content = Json.Deserialize<RenderedContent>(protector.Unprotect(x.ContentCipher, Purposes.MessageContent));
                var sensitive = await db.NotificationTypes.AsNoTracking().AnyAsync(t => t.TenantId == x.TenantId && t.Code == request.NotificationTypeCode && t.IsSensitive, ctx.RequestAborted);
                if (sensitive && content is not null) { content.Body = Redact.MaskCodes(content.Body); content.TextBody = content.TextBody is null ? null : Redact.MaskCodes(content.TextBody); }
            }
            return Results.Ok(new
            {
                delivery = Mapper.Delivery(x, providers, unmasked: canSee), request = new { request.Id, request.NotificationTypeCode, request.TemplateCode, request.Source, request.ActorId, request.ActorName, request.BusinessEventId, request.RuleId, request.CampaignId, request.CorrelationId, request.TraceId, request.CreatedAt },
                content = content is null ? null : new { content.Subject, content.Title, content.Body, content.TextBody, content.Format, whatsAppTemplate = content.WhatsAppTemplate?.Name },
                contentAvailable = x.ContentCipher is not null, contentPurgedAt = x.ContentPurgedAt, awaitingOperator = x.AwaitingOperator, deadLettered = x.DeadLettered,
                attempts = attempts.Select(a => Mapper.Attempt(a, providers)), events = events.Select(Mapper.Event),
            });
        }).WithSummary("Delivery detail with sanitized provider responses");

        d.MapPost("/{id:guid}/retry", async (Guid id, HttpContext ctx, NotificationHubDbContext db, DeliveryStatusUpdater status, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var x = await db.NotificationDeliveries.FirstOrDefaultAsync(z => z.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Delivery");
            if (!caller.Can(Permission.RetryDeliveries, x.TenantId, x.CompanyId, x.ApplicationId)) throw HubException.NotFound("Delivery");
            if (x.Status is not (DeliveryStatus.Failed or DeliveryStatus.Expired or DeliveryStatus.RetryPending))
                throw HubException.Conflict("not_retryable", x.Status == DeliveryStatus.Unknown ? "Use /resolve for deliveries with an unknown outcome." : $"Deliveries in status {x.Status} cannot be retried.");
            x.MaxAttempts = x.AttemptCount + 3;
            x.DeadLettered = false;
            x.ExpiresAt = x.ExpiresAt < DateTime.UtcNow ? null : x.ExpiresAt;
            x.NextAttemptAt = DateTime.UtcNow;
            x.CompletedAt = null; x.FailedAt = null;
            await status.SetAsync(x, DeliveryStatus.RetryPending, "operator", $"Manual retry by {caller.ActorName ?? caller.ActorId}", ctx.RequestAborted);
            audit.Record(caller, "delivery.retried", nameof(NotificationDelivery), id.ToString(), null, x.TenantId, x.CompanyId, x.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { status = x.Status });
        }).WithSummary("Operator retry of a failed delivery (prior attempts remain on the timeline)");

        d.MapPost("/{id:guid}/resolve", async (Guid id, ResolveDeliveryRequest body, HttpContext ctx, NotificationHubDbContext db, DeliveryStatusUpdater status, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var x = await db.NotificationDeliveries.FirstOrDefaultAsync(z => z.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Delivery");
            if (!caller.Can(Permission.RetryDeliveries, x.TenantId, x.CompanyId, x.ApplicationId)) throw HubException.NotFound("Delivery");
            if (x.Status != DeliveryStatus.Unknown) throw HubException.Conflict("not_ambiguous", "Only deliveries with an unknown outcome can be resolved.");
            var who = caller.ActorName ?? caller.ActorId;
            switch (body.Action)
            {
                case "resend": // operator accepts the duplicate risk
                    x.AwaitingOperator = false; x.PinnedProviderAccountId = null; x.MaxAttempts = Math.Max(x.MaxAttempts, x.AttemptCount + 2); x.NextAttemptAt = DateTime.UtcNow;
                    await status.SetAsync(x, DeliveryStatus.RetryPending, "operator", $"Resend approved by {who} (duplicate risk accepted). {body.Comment}", ctx.RequestAborted);
                    break;
                case "mark_sent":
                    x.AwaitingOperator = false;
                    await status.SetAsync(x, DeliveryStatus.ProviderAccepted, "operator", $"Marked as accepted by {who}. {body.Comment}", ctx.RequestAborted);
                    break;
                case "mark_failed":
                    x.AwaitingOperator = false; x.FailureCode = "operator_failed";
                    await status.SetAsync(x, DeliveryStatus.Failed, "operator", $"Marked as failed by {who}. {body.Comment}", ctx.RequestAborted);
                    break;
                default: throw HubException.Validation("action must be resend, mark_sent or mark_failed.");
            }
            audit.Record(caller, "delivery.resolved", nameof(NotificationDelivery), id.ToString(), body.Action, x.TenantId, x.CompanyId, x.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { status = x.Status });
        }).WithSummary("Operator decision for an ambiguous (unknown-outcome) delivery");
    }

    public sealed record DeliveryQuery(Guid? TenantId, Guid? CompanyId, Guid? ApplicationId, Channel? Channel, Guid? ProviderAccountId, DeliveryStatus? Status,
        string? NotificationType, string? Recipient, Guid? NotificationId, Guid? CampaignId, DateTime? From, DateTime? To, bool? AwaitingOperator, int? Page, int? PageSize, string? Sort);

    private static IQueryable<NotificationDelivery> Filter(NotificationHubDbContext db, IQueryable<NotificationDelivery> q, DeliveryQuery f)
    {
        if (f.TenantId is { } t) q = q.Where(x => x.TenantId == t);
        if (f.CompanyId is { } c) q = q.Where(x => x.CompanyId == c);
        if (f.ApplicationId is { } a) q = q.Where(x => x.ApplicationId == a);
        if (f.Channel is { } ch) q = q.Where(x => x.Channel == ch);
        if (f.ProviderAccountId is { } p) q = q.Where(x => x.ProviderAccountId == p);
        if (f.Status is { } s) q = q.Where(x => x.Status == s);
        if (f.NotificationId is { } n) q = q.Where(x => x.RequestId == n);
        if (f.CampaignId is { } cid) q = q.Where(x => x.CampaignId == cid);
        if (f.AwaitingOperator == true) q = q.Where(x => x.AwaitingOperator);
        if (!string.IsNullOrWhiteSpace(f.NotificationType)) q = q.Where(x => db.NotificationRequests.Any(r => r.Id == x.RequestId && r.NotificationTypeCode == f.NotificationType));
        if (!string.IsNullOrWhiteSpace(f.Recipient))
        {
            var r = f.Recipient.Trim().ToLowerInvariant();
            var phone = Addressing.NormalizePhone(f.Recipient);
            q = q.Where(x => x.Address == r || (phone != null && x.Address == phone));
        }
        if (f.From is { } from) q = q.Where(x => x.CreatedAt >= from.ToUniversalTime());
        if (f.To is { } to) q = q.Where(x => x.CreatedAt < to.ToUniversalTime());
        return q;
    }

    private static async Task<(NotificationRequest, List<NotificationDelivery>)> LoadAsync(HttpContext ctx, NotificationHubDbContext db, Guid id)
    {
        var caller = await ctx.CallerAsync();
        var req = await db.NotificationRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ctx.RequestAborted);
        // Same 404 for "missing" and "not yours": no cross-tenant existence probing.
        if (req is null || !caller.Can(Permission.ViewNotifications, req.TenantId, req.CompanyId, req.ApplicationId)
                        || (caller.Kind == CallerKind.Application && (caller.ApplicationId != req.ApplicationId || !caller.HasScope(Scopes.NotificationsRead))))
            throw HubException.NotFound("Notification");
        var deliveries = await db.NotificationDeliveries.AsNoTracking().Where(d => d.RequestId == id).OrderBy(d => d.CreatedAt).ToListAsync(ctx.RequestAborted);
        return (req, deliveries);
    }

    public static async Task<Dictionary<Guid, string>> ProviderNamesAsync(NotificationHubDbContext db, IEnumerable<Guid?> ids)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.ProviderAccounts.AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
    }

    private static DateTimeOffset? Utc(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));
    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}

public sealed class Lookups
{
    public Dictionary<Guid, string> Apps { get; init; } = [];
    public Dictionary<Guid, string> Companies { get; init; } = [];
    public Dictionary<Guid, string> Providers { get; init; } = [];
    public Dictionary<Guid, string?> Types { get; init; } = [];

    public static async Task<Lookups> LoadAsync(NotificationHubDbContext db, List<NotificationDelivery> rows, CancellationToken ct)
    {
        var apps = rows.Select(r => r.ApplicationId).Distinct().ToList();
        var companies = rows.Select(r => r.CompanyId).Distinct().ToList();
        var providers = rows.Where(r => r.ProviderAccountId != null).Select(r => r.ProviderAccountId!.Value).Distinct().ToList();
        var requests = rows.Select(r => r.RequestId).Distinct().ToList();
        return new Lookups
        {
            Apps = await db.Applications.AsNoTracking().Where(a => apps.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct),
            Companies = await db.Companies.AsNoTracking().Where(c => companies.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct),
            Providers = await db.ProviderAccounts.AsNoTracking().Where(p => providers.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct),
            Types = await db.NotificationRequests.AsNoTracking().Where(r => requests.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.NotificationTypeCode, ct),
        };
    }
}

public sealed record ExplorerRow(DateTime CreatedAt, Guid NotificationId, Guid DeliveryId, string Application, string Company, string Recipient, string? NotificationType,
    string Channel, string? Provider, DeliveryStatus Status, int Attempts, decimal? Cost, string? Currency, string? FailureCode, string? StatusReason, bool AwaitingOperator, Guid? CampaignId);

public static class Mapper
{
    public static DeliveryDto Delivery(NotificationDelivery d, IReadOnlyDictionary<Guid, string> providers, bool unmasked = false) => new()
    {
        DeliveryId = d.Id, Channel = d.Channel, Recipient = unmasked ? d.Address : Addressing.Mask(d.Channel, d.Address), Status = d.Status,
        Provider = d.ProviderAccountId is { } p && providers.TryGetValue(p, out var n) ? n : d.ProviderType, ProviderAccountId = d.ProviderAccountId,
        ProviderMessageId = d.ProviderMessageId, Attempts = d.AttemptCount, FailureCode = d.FailureCode, FailureReason = d.FailureReason ?? (d.Status == DeliveryStatus.Suppressed ? d.StatusReason : null),
        Cost = d.Cost, Currency = d.Currency, TemplateVersionId = d.TemplateVersionId, TemplateVersion = d.TemplateVersionNumber,
        FallbackOfDeliveryId = d.FallbackOfDeliveryId, EscalationOfDeliveryId = d.EscalationOfDeliveryId, CreatedAt = U(d.CreatedAt), NextAttemptAt = U(d.NextAttemptAt),
        SubmittedAt = U(d.SubmittedAt), AcceptedAt = U(d.AcceptedAt), SentAt = U(d.SentAt), DeliveredAt = U(d.DeliveredAt), ReadAt = U(d.ReadAt), FailedAt = U(d.FailedAt),
    };

    public static DeliveryAttemptDto Attempt(DeliveryAttempt a, IReadOnlyDictionary<Guid, string> providers) => new()
    {
        Id = a.Id, DeliveryId = a.DeliveryId, AttemptNumber = a.AttemptNumber, Provider = a.ProviderAccountId is { } p && providers.TryGetValue(p, out var n) ? n : a.ProviderType,
        ProviderAccountId = a.ProviderAccountId, Outcome = a.Outcome, ProviderMessageId = a.ProviderMessageId, ResponseCode = a.ResponseCode, ResponseSummary = a.ResponseSummary,
        ErrorCode = a.ErrorCode, ErrorMessage = a.ErrorMessage, DurationMs = a.DurationMs, Cost = a.Cost, StartedAt = U(a.StartedAt), CompletedAt = U(a.CompletedAt),
    };

    public static DeliveryEventDto Event(DeliveryEvent e) => new()
    {
        Id = e.Id, DeliveryId = e.DeliveryId, Type = e.Type, Status = e.Status, Source = e.Source, Detail = e.Applied || e.Type != "provider_status" ? e.Detail : $"{e.Detail} (recorded, not applied: out of order or duplicate)".Trim(),
        OccurredAt = U(e.OccurredAt), RecordedAt = U(e.RecordedAt),
    };

    public static ExplorerRow ExplorerRow(NotificationDelivery r, Lookups l, bool unmasked) => new(
        r.CreatedAt, r.RequestId, r.Id, l.Apps.GetValueOrDefault(r.ApplicationId, "?"), l.Companies.GetValueOrDefault(r.CompanyId, "?"),
        unmasked ? r.Address : Addressing.Mask(r.Channel, r.Address), l.Types.GetValueOrDefault(r.RequestId), ChannelNames.ToWire(r.Channel),
        r.ProviderAccountId is { } p ? l.Providers.GetValueOrDefault(p, r.ProviderType ?? "") : null, r.Status, r.AttemptCount, r.Cost, r.Currency, r.FailureCode, r.StatusReason, r.AwaitingOperator, r.CampaignId);

    private static DateTimeOffset? U(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));
    private static DateTimeOffset U(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}

public static class Exporter
{
    public static IResult Export(List<Dictionary<string, object?>> rows, string name, string format)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
        if (format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add(name.Length > 31 ? name[..31] : name);
            var headers = rows.FirstOrDefault()?.Keys.ToList() ?? [];
            for (var i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i];
            for (var r = 0; r < rows.Count; r++)
                for (var c = 0; c < headers.Count; c++)
                    ws.Cell(r + 2, c + 1).Value = ClosedXML.Excel.XLCellValue.FromObject(rows[r][headers[c]] is Enum e ? e.ToString() : rows[r][headers[c]]);
            ws.Row(1).Style.Font.Bold = true;
            ws.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 200));
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}-{stamp}.xlsx");
        }
        var sb = new StringBuilder();
        var keys = rows.FirstOrDefault()?.Keys.ToList() ?? [];
        sb.AppendLine(string.Join(',', keys.Select(Csv)));
        foreach (var row in rows) sb.AppendLine(string.Join(',', keys.Select(k => Csv(Format(row[k])))));
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"{name}-{stamp}.csv");
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>CSV-escapes and neutralizes spreadsheet formula injection (=, +, -, @ prefixes).</summary>
    private static string Csv(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
