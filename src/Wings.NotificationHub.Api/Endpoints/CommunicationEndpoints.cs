using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record ReplyRequest(string Body, string? TemplateCode, Dictionary<string, JsonElement>? Variables);
public sealed record WebhookSubscriptionRequest(Guid? ApplicationId, Guid? CompanyId, string Name, string Url, List<string> EventTypes);
public sealed record InAppActionRequest(string ActionId);
public sealed record PushInteractionRequest(Guid DeliveryId, string Interaction);
public sealed record ConversationMessage(string Direction, Guid Id, DateTime At, string? Body, string Type, string Status, string? Detail, Guid? InReplyTo, List<object> Attachments);

public static class CommunicationEndpoints
{
    public static void Map(WebApplication app, RouteGroupBuilder api)
    {
        // ── provider callbacks & inbound (public, authenticated by provider signatures) ──
        async Task<IResult> Callback(Guid accountId, HttpContext ctx, CallbackProcessor processor, IConfiguration config)
        {
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (body.Length > 1_000_000) return Results.StatusCode(413);
            ctx.Request.Body.Position = 0;
            var form = ctx.Request.HasFormContentType ? (await ctx.Request.ReadFormAsync(ctx.RequestAborted)).ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.Ordinal) : new Dictionary<string, string>();
            // Signature schemes that sign the URL (Twilio) need the public URL the provider called.
            var publicBase = config["Hub:PublicBaseUrl"]?.TrimEnd('/') ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var outcome = await processor.ProcessHttpAsync(accountId, new CallbackRequest
            {
                Method = ctx.Request.Method, Url = publicBase + ctx.Request.Path + ctx.Request.QueryString, Body = body, Form = form,
                Headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                Query = ctx.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            }, ctx.RequestAborted);
            return outcome.StatusCode switch
            {
                200 when outcome.Body is not null => Results.Text(outcome.Body),
                200 => Results.Ok(new { accepted = outcome.Accepted, duplicates = outcome.Duplicates }),
                var s => Results.StatusCode(s),
            };
        }
        app.MapPost("/api/v1/callbacks/{accountId:guid}", Callback).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RateLimiting.Callbacks)
            .WithTags("Provider callbacks").WithSummary("Provider delivery receipts and inbound messages (signature-verified, idempotent)");
        app.MapGet("/api/v1/callbacks/{accountId:guid}", Callback).AllowAnonymous().RequireRateLimiting(RateLimiting.Callbacks)
            .WithTags("Provider callbacks").WithSummary("Webhook verification handshake (e.g. Meta hub.challenge)");

        // ── conversations ──
        var conv = api.MapGroup("/conversations").WithTags("Inbound & conversations").RequireAuthorization("app-or-admin");
        conv.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Channel? channel, bool? unread, string? q, int page = 1) =>
        {
            var caller = await ctx.CallerAsync();
            var query = db.ConversationThreads.AsNoTracking().Visible(caller.FilterFor(Permission.ViewInbox));
            if (channel is { } ch) query = query.Where(t => t.Channel == ch);
            if (unread == true) query = query.Where(t => t.UnreadCount > 0);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => t.ExternalAddress.ToLower().Contains(q.ToLower()) || (t.ContactName != null && t.ContactName.ToLower().Contains(q.ToLower())));
            var rows = await query.OrderByDescending(t => t.LastMessageAt).Skip((page - 1) * 50).Take(50).ToListAsync(ctx.RequestAborted);
            var ids = rows.Select(r => r.Id).ToList();
            var last = await db.InboundMessages.AsNoTracking().Where(m => ids.Contains(m.ThreadId)).GroupBy(m => m.ThreadId)
                .Select(g => g.OrderByDescending(m => m.ReceivedAt).Select(m => new { m.ThreadId, m.Body, m.RoutingStatus }).First()).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(t => new
            {
                t.Id, t.CompanyId, t.ApplicationId, channel = ChannelNames.ToWire(t.Channel), t.ExternalAddress, t.ContactId, t.ContactName, t.LastMessageAt, t.LastInboundAt,
                t.UnreadCount, t.Closed, windowOpen = t.Channel == Channel.WhatsApp && t.LastInboundAt > DateTime.UtcNow.AddHours(-24),
                lastMessage = last.FirstOrDefault(l => l.ThreadId == t.Id)?.Body, routing = last.FirstOrDefault(l => l.ThreadId == t.Id)?.RoutingStatus,
            }));
        }).WithSummary("Two-way conversation threads (WhatsApp, SMS, email replies)");
        conv.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector) =>
        {
            var t = await LoadThread(ctx, db, id, Permission.ViewInbox);
            var inbound = await db.InboundMessages.AsNoTracking().Where(m => m.ThreadId == id).OrderBy(m => m.ReceivedAt).ToListAsync(ctx.RequestAborted);
            var outbound = await db.NotificationDeliveries.AsNoTracking()
                .Where(d => d.TenantId == t.TenantId && d.Channel == t.Channel && d.Address == t.ExternalAddress && (t.ProviderAccountId == null || d.ProviderAccountId == t.ProviderAccountId || d.ThreadId == id))
                .OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ctx.RequestAborted);
            var attachments = await db.MessageAttachments.AsNoTracking().Where(a => a.InboundMessageId != null && inbound.Select(i => i.Id).Contains(a.InboundMessageId.Value)).ToListAsync(ctx.RequestAborted);
            var messages = inbound.Select(m => new ConversationMessage("inbound", m.Id, m.ReceivedAt, m.Body, m.MessageType, m.RoutingStatus.ToString(), m.RoutingDetail, m.CorrelatedRequestId,
                    attachments.Where(a => a.InboundMessageId == m.Id).Select(a => (object)new { a.Id, a.FileName, a.ContentType, a.Size }).ToList()))
                .Concat(outbound.Select(d => new ConversationMessage("outbound", d.Id, d.CreatedAt,
                    d.ContentCipher is null ? "[content purged]" : Json.Deserialize<RenderedContent>(protector.Unprotect(d.ContentCipher, Purposes.MessageContent))?.Body,
                    d.Category ?? "notification", d.Status.ToString(), d.StatusReason, d.RequestId, [])))
                .OrderBy(x => x.At).ToList();
            return Results.Ok(new { thread = t, messages });
        }).WithSummary("Unified conversation history (inbound + outbound)");
        conv.MapPost("/{id:guid}/read", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var t = await LoadThread(ctx, db, id, Permission.ManageInbox, tracked: true);
            t.UnreadCount = 0;
            await db.InboundMessages.Where(m => m.ThreadId == id && m.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, DateTime.UtcNow), ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
        conv.MapPost("/{id:guid}/reply", async (Guid id, ReplyRequest req, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var t = await LoadThread(ctx, db, id, Permission.ManageInbox);
            if (t.ApplicationId is null || t.CompanyId is null) throw HubException.Validation("This conversation is not linked to an application/company yet.");
            if (string.IsNullOrWhiteSpace(req.Body) || req.Body.Length > 4096) throw HubException.Validation("Reply body is required (max 4096 characters).");
            if (t.Channel == Channel.WhatsApp && !(t.LastInboundAt > DateTime.UtcNow.AddHours(-24)))
                throw new HubException(422, "outside_customer_service_window", "The 24-hour WhatsApp customer-service window is closed; send an approved template instead.");
            // Free-form replies bypass templates but keep the full delivery pipeline (policy, routing, tracking).
            var request = new NotificationRequest
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId.Value, ApplicationId = t.ApplicationId.Value, Source = RequestSource.Admin, ActorId = caller.ActorId, ActorName = caller.ActorName,
                TemplateCode = "CONVERSATION_REPLY", Category = "Conversation", MessageClass = MessageClass.Transactional, Priority = NotificationPriority.High, Status = RequestStatus.Accepted,
                CorrelationId = caller.CorrelationId,
            };
            var recipient = new NotificationRecipient { RequestId = request.Id, ContactId = t.ContactId, Name = t.ContactName, Mobile = t.Channel != Channel.Email ? t.ExternalAddress : null, WhatsApp = t.Channel == Channel.WhatsApp ? t.ExternalAddress : null, Email = t.Channel == Channel.Email ? t.ExternalAddress : null };
            var content = new RenderedContent { Body = req.Body, Subject = t.Channel == Channel.Email ? "Re: your message" : null, Format = BodyFormat.Text, TextBody = req.Body };
            var d = new NotificationDelivery
            {
                RequestId = request.Id, RecipientId = recipient.Id, TenantId = t.TenantId, CompanyId = t.CompanyId.Value, ApplicationId = t.ApplicationId.Value, Channel = t.Channel,
                Address = t.ExternalAddress, ThreadId = t.Id, MessageClass = MessageClass.Transactional, Priority = NotificationPriority.High, Category = "Conversation",
                ContentCipher = protector.Protect(Json.Serialize(content), Purposes.MessageContent), Status = DeliveryStatus.Queued, NextAttemptAt = DateTime.UtcNow,
            };
            request.Recipients.Add(recipient);
            request.Deliveries.Add(d);
            db.NotificationRequests.Add(request);
            db.DeliveryEvents.Add(new DeliveryEvent { DeliveryId = d.Id, TenantId = d.TenantId, Type = "created", Status = d.Status, Source = "conversation_reply", Applied = true, Detail = $"reply by {caller.ActorName}" });
            Outbox.Add(db, MessageTypes.DispatchDelivery, new DispatchDeliveryMessage(d.Id), t.TenantId);
            audit.Record(caller, "conversation.replied", nameof(ConversationThread), id.ToString(), null, t.TenantId, t.CompanyId, t.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Accepted($"/api/v1/notifications/{request.Id}/status", new { notificationId = request.Id, deliveryId = d.Id });
        }).WithSummary("Reply inside an open conversation (WhatsApp 24-hour window enforced)");

        api.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, IAttachmentStore store) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.MessageAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (a is null || !caller.Can(Permission.ViewInbox, a.TenantId, a.CompanyId) || (caller.Kind == CallerKind.Application && caller.TenantId != a.TenantId)) throw HubException.NotFound("Attachment");
            return Results.File(await store.ReadAsync(a, ctx.RequestAborted), a.ContentType, a.FileName);
        }).WithTags("Attachments").RequireAuthorization("app-or-admin");
        api.MapPost("/attachments", async (HttpContext ctx, IAttachmentStore store, NotificationHubDbContext db, Guid companyId) =>
        {
            var caller = await ctx.CallerAsync();
            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ctx.RequestAborted) ?? throw HubException.NotFound("Company");
            if (!caller.Can(Permission.SendNotifications, company.TenantId, company.Id, caller.ApplicationId)) throw new AccessDeniedException();
            var file = (await ctx.Request.ReadFormAsync(ctx.RequestAborted)).Files["file"] ?? throw HubException.Validation("Missing multipart field 'file'.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            var a = await store.SaveAsync(company.TenantId, company.Id, "outbound", file.FileName, file.ContentType, ms.ToArray(), caller.ActorId, null, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/attachments/{a.Id}", new { attachmentId = a.Id, a.FileName, a.ContentType, a.Size, a.Sha256 });
        }).WithTags("Attachments").RequireAuthorization("app-or-admin").DisableAntiforgery().WithSummary("Upload an email attachment (encrypted at rest; allow-listed types)");

        // ── webhook subscriptions ──
        var wh = api.MapGroup("/webhooks").WithTags("Webhooks").RequireAuthorization("app-or-admin");
        wh.MapGet("", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.WebhookSubscriptions.AsNoTracking().Visible(caller.FilterFor(Permission.ManageWebhooks)).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(s => new { s.Id, s.ApplicationId, s.CompanyId, s.Name, s.Url, s.EventTypes, s.Enabled, s.ConsecutiveFailures, s.DisabledAt, s.CreatedAt }));
        });
        wh.MapPost("", async (WebhookSubscriptionRequest req, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector, UrlGuard guard, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var appId = caller.ApplicationId ?? req.ApplicationId ?? throw HubException.Validation("applicationId is required.");
            var application = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ctx.RequestAborted) ?? throw HubException.NotFound("Application");
            if (!caller.Can(Permission.ManageWebhooks, application.TenantId, req.CompanyId, appId) || (caller.Kind == CallerKind.Application && !caller.HasScope(Scopes.WebhooksManage))) throw new AccessDeniedException();
            if (guard.Validate(req.Url) is { } err) throw HubException.Validation($"Webhook URL rejected: {err}.");
            var unknown = req.EventTypes.Where(e => e != "*" && !WebhookEventTypes.All.Contains(e)).ToList();
            if (unknown.Count > 0 || req.EventTypes.Count == 0) throw HubException.Validation($"Event types must be from: {string.Join(", ", WebhookEventTypes.All)}.");
            var secret = Hashing.NewSecret("whsec_");
            var s = new WebhookSubscription { TenantId = application.TenantId, ApplicationId = appId, CompanyId = req.CompanyId, Name = req.Name, Url = req.Url, EventTypes = req.EventTypes, SecretCipher = protector.Protect(secret, Purposes.WebhookSecret) };
            db.WebhookSubscriptions.Add(s);
            audit.Record(caller, "webhook.created", nameof(WebhookSubscription), s.Id.ToString(), $"{s.Url} [{string.Join(",", s.EventTypes)}]", s.TenantId, s.CompanyId, s.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { s.Id, signingSecret = secret, signatureHeader = WebhookSignature.Header, note = "Verify HMAC-SHA256 over \"{t}.{body}\"; reject timestamps older than 5 minutes. The secret is shown once." });
        }).WithSummary("Register a delivery/reply callback webhook (signing secret returned once)");
        wh.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var s = await db.WebhookSubscriptions.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (s is null || !caller.Can(Permission.ManageWebhooks, s.TenantId, s.CompanyId, s.ApplicationId) || (caller.Kind == CallerKind.Application && caller.ApplicationId != s.ApplicationId)) throw HubException.NotFound("Webhook");
            db.WebhookSubscriptions.Remove(s);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
        wh.MapPost("/{id:guid}/enable", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var s = await db.WebhookSubscriptions.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (s is null || !caller.Can(Permission.ManageWebhooks, s.TenantId, s.CompanyId, s.ApplicationId)) throw HubException.NotFound("Webhook");
            s.Enabled = true; s.DisabledAt = null; s.ConsecutiveFailures = 0;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
        wh.MapGet("/{id:guid}/attempts", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var s = await db.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (s is null || !caller.Can(Permission.ManageWebhooks, s.TenantId, s.CompanyId, s.ApplicationId)) throw HubException.NotFound("Webhook");
            return Results.Ok(await db.WebhookDeliveryAttempts.AsNoTracking().Where(a => a.SubscriptionId == id).OrderByDescending(a => a.AttemptedAt).Take(200).ToListAsync(ctx.RequestAborted));
        });
        wh.MapPost("/{id:guid}/test", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var s = await db.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (s is null || !caller.Can(Permission.ManageWebhooks, s.TenantId, s.CompanyId, s.ApplicationId)) throw HubException.NotFound("Webhook");
            var eventId = Guid.NewGuid();
            WebhookFanout.Enqueue(db, [new WebhookSubscription { Id = s.Id, EventTypes = ["*"] }], s.EventTypes.FirstOrDefault() ?? WebhookEventTypes.DeliveryStatusChanged, eventId, s.TenantId, s.CompanyId, s.ApplicationId, new JsonObject { ["test"] = true, ["message"] = "Wings Notification Hub webhook test" });
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Accepted(value: new { eventId });
        });

        // ── in-app inbox (end users) ──
        api.MapPost("/inapp/tokens", async (InboxTokenRequest req, HttpContext ctx, TenantContextResolver resolver, TokenService tokens) =>
        {
            var caller = await ctx.CallerAsync();
            var tc = await resolver.ResolveAsync(caller, req.ApplicationId, req.CompanyId, Permission.SendNotifications, Scopes.InAppToken, ctx.RequestAborted, allowSuspended: true);
            if (string.IsNullOrWhiteSpace(req.UserId)) throw HubException.Validation("userId is required.");
            var (token, expires) = tokens.Issue([new("sub", req.UserId), new(HubClaims.Kind, "inbox"), new(HubClaims.Tenant, tc.Tenant.Id.ToString()),
                new(HubClaims.Company, tc.Company.Id.ToString()), new(HubClaims.Application, tc.Application.Id.ToString()), new(HubClaims.Scope, Scopes.InAppUser)],
                TimeSpan.FromMinutes(Math.Clamp(req.LifetimeMinutes ?? 60, 5, 24 * 60)));
            return Results.Ok(new InboxTokenResponse { AccessToken = token, ExpiresAt = expires, HubUrl = "/hubs/inapp" });
        }).WithTags("In-app inbox").RequireAuthorization("app-or-admin").WithSummary("Mint a short-lived inbox token for an end user (called by the application backend)");

        var inbox = api.MapGroup("/inapp").WithTags("In-app inbox").RequireAuthorization("inbox");
        inbox.MapGet("/messages", async (HttpContext ctx, NotificationHubDbContext db, bool? unreadOnly, bool? archived, string? category, int page = 1, int pageSize = 30) =>
        {
            var u = await ctx.CallerAsync();
            var now = DateTime.UtcNow;
            var q = db.InAppMessages.AsNoTracking().Where(m => m.ApplicationId == u.ApplicationId && m.CompanyId == u.CompanyId && m.UserId == u.UserId
                && m.DismissedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now));
            q = archived == true ? q.Where(m => m.ArchivedAt != null) : q.Where(m => m.ArchivedAt == null);
            if (unreadOnly == true) q = q.Where(m => m.ReadAt == null);
            if (!string.IsNullOrEmpty(category)) q = q.Where(m => m.Category == category);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var unread = await db.InAppMessages.CountAsync(m => m.ApplicationId == u.ApplicationId && m.CompanyId == u.CompanyId && m.UserId == u.UserId && m.ReadAt == null && m.ArchivedAt == null && m.DismissedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now), ctx.RequestAborted);
            var rows = await q.OrderByDescending(m => m.Priority == NotificationPriority.Critical).ThenByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { unread, items = rows.Select(InAppDto) });
        });
        inbox.MapPost("/messages/{id:guid}/{action}", async (Guid id, string action, HttpContext ctx, NotificationHubDbContext db, DeliveryStatusUpdater status) =>
        {
            // Only "action" carries a body; read/unread/archive/dismiss are body-less POSTs.
            InAppActionRequest? body = null;
            if (action == "action" && ctx.Request.ContentLength is not 0 && ctx.Request.HasJsonContentType())
                body = await ctx.Request.ReadFromJsonAsync<InAppActionRequest>(ctx.RequestAborted);
            var u = await ctx.CallerAsync();
            var m = await db.InAppMessages.FirstOrDefaultAsync(x => x.Id == id && x.ApplicationId == u.ApplicationId && x.CompanyId == u.CompanyId && x.UserId == u.UserId, ctx.RequestAborted)
                    ?? throw HubException.NotFound("Message");
            var now = DateTime.UtcNow;
            switch (action)
            {
                case "read": m.ReadAt ??= now; break;
                case "unread": m.ReadAt = null; break;
                case "archive": m.ArchivedAt ??= now; m.ReadAt ??= now; break;
                case "dismiss": m.DismissedAt ??= now; break;
                case "action":
                    var actions = Json.Deserialize<List<AppActionDto>>(m.ActionsJson) ?? [];
                    if (body is null || actions.All(a => a.Id != body.ActionId)) throw HubException.Validation("Unknown action.");
                    m.ActionTaken = body.ActionId; m.ActedAt = now; m.ReadAt ??= now;
                    var subs = await WebhookFanout.SubscriptionsAsync(db, m.ApplicationId, m.CompanyId, ctx.RequestAborted);
                    WebhookFanout.Enqueue(db, subs, WebhookEventTypes.InAppAction, Guid.NewGuid(), m.TenantId, m.CompanyId, m.ApplicationId,
                        new JsonObject { ["inAppMessageId"] = m.Id, ["deliveryId"] = m.DeliveryId, ["userId"] = m.UserId, ["actionId"] = body.ActionId });
                    break;
                default: throw HubException.Validation("action must be read, unread, archive, dismiss or action.");
            }
            if (m.ReadAt is not null && await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.Id == m.DeliveryId, ctx.RequestAborted) is { } d)
                await status.ApplyProviderStatusAsync(d, new ProviderCallbackEvent { EventId = $"inapp-read:{m.Id}", ProviderMessageId = d.ProviderMessageId, Status = DeliveryStatus.Read, Interaction = action, OccurredAt = m.ReadAt.Value }, "client", ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(InAppDto(m));
        });
        inbox.MapPost("/messages/read-all", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var u = await ctx.CallerAsync();
            var n = await db.InAppMessages.Where(m => m.ApplicationId == u.ApplicationId && m.CompanyId == u.CompanyId && m.UserId == u.UserId && m.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, DateTime.UtcNow), ctx.RequestAborted);
            return Results.Ok(new { marked = n });
        });
        inbox.MapGet("/branding", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var u = await ctx.CallerAsync();
            var c = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == u.CompanyId, ctx.RequestAborted);
            var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == u.TenantId, ctx.RequestAborted);
            return Results.Ok(Json.Deserialize<JsonObject>(c.BrandingJson) ?? Json.Deserialize<JsonObject>(t.BrandingJson) ?? new JsonObject());
        }).WithSummary("White-label inbox branding for the user's company/tenant");

        // ── push interaction events (open/click reported by client SDKs) ──
        api.MapPost("/push/events", async (PushInteractionRequest req, HttpContext ctx, NotificationHubDbContext db, DeliveryStatusUpdater status) =>
        {
            var caller = await ctx.CallerAsync();
            var d = await db.NotificationDeliveries.FirstOrDefaultAsync(x => x.Id == req.DeliveryId, ctx.RequestAborted);
            var allowed = d is not null && (caller.Kind == CallerKind.InboxUser
                ? d.ApplicationId == caller.ApplicationId && d.CompanyId == caller.CompanyId && await db.DeviceRegistrations.AnyAsync(x => x.Id == d.DeviceId && x.UserId == caller.UserId, ctx.RequestAborted)
                : caller.Can(Permission.SendNotifications, d.TenantId, d.CompanyId, d.ApplicationId) && (caller.Kind != CallerKind.Application || caller.ApplicationId == d.ApplicationId));
            if (!allowed || d is null) throw HubException.NotFound("Delivery");
            if (d.Channel is not (Channel.Push or Channel.WebPush)) throw HubException.Validation("Only push deliveries accept interaction events.");
            var interaction = req.Interaction.ToLowerInvariant() is "opened" or "open" ? "open" : req.Interaction.ToLowerInvariant() is "clicked" or "click" ? "click" : "received";
            // Device receipt/open is the only real evidence of push delivery; FCM/APNs acceptance alone is not.
            var st = interaction is "open" or "click" ? DeliveryStatus.Read : DeliveryStatus.Delivered;
            await status.ApplyProviderStatusAsync(d, new ProviderCallbackEvent { EventId = $"push:{d.Id}:{interaction}", ProviderMessageId = d.ProviderMessageId, Status = st, Interaction = interaction }, "client", ctx.RequestAborted);
            var subs = await WebhookFanout.SubscriptionsAsync(db, d.ApplicationId, d.CompanyId, ctx.RequestAborted);
            WebhookFanout.Enqueue(db, subs, WebhookEventTypes.PushInteraction, Guid.NewGuid(), d.TenantId, d.CompanyId, d.ApplicationId, new JsonObject { ["deliveryId"] = d.Id, ["interaction"] = interaction });
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        }).WithTags("Devices").RequireAuthorization().WithSummary("Report push receipt/open/click from a device or app backend");
    }

    private static async Task<ConversationThread> LoadThread(HttpContext ctx, NotificationHubDbContext db, Guid id, Permission p, bool tracked = false)
    {
        var caller = await ctx.CallerAsync();
        var t = await (tracked ? db.ConversationThreads : db.ConversationThreads.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
        if (t is null || !caller.Can(p, t.TenantId, t.CompanyId, t.ApplicationId) || (caller.Kind == CallerKind.Application && caller.ApplicationId != t.ApplicationId)) throw HubException.NotFound("Conversation");
        return t;
    }

    public static InAppMessageDto InAppDto(InAppMessage m) => new()
    {
        Id = m.Id, Title = m.Title, Body = m.Body, Category = m.Category, Priority = m.Priority, DeepLink = m.DeepLink, ImageUrl = m.ImageUrl,
        Actions = Json.Deserialize<List<AppActionDto>>(m.ActionsJson) ?? [], CreatedAt = m.CreatedAt, ExpiresAt = m.ExpiresAt, ReadAt = m.ReadAt, ArchivedAt = m.ArchivedAt, ActionTaken = m.ActionTaken,
    };
}
