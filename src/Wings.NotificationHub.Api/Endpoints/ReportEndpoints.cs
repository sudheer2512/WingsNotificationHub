using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record ReportFilter(Guid? TenantId, Guid? CompanyId, Guid? ApplicationId, Channel? Channel, Guid? ProviderAccountId, DateTime? From, DateTime? To);

public static class ReportEndpoints
{
    public static readonly (string Code, string Name)[] Reports =
    [
        ("channel-volumes", "Channel-wise notification volumes"),
        ("application-consumption", "Application-wise communication consumption"),
        ("company-consumption", "Company-wise communication consumption"),
        ("provider-performance", "Provider performance"),
        ("delivery-success", "Delivery success rates"),
        ("failure-reasons", "Failure reasons"),
        ("delivery-latency", "Average delivery latency"),
        ("cost-by-channel-provider", "Cost by channel and provider"),
        ("cost-by-company-application", "Cost by company and application"),
        ("campaign-performance", "Campaign performance"),
        ("template-usage", "Template usage"),
        ("retry-statistics", "Retry statistics"),
        ("consent-optout", "Consent and opt-out statistics"),
        ("queue-backlog", "Queue backlog trends"),
        ("monthly-usage", "Monthly usage comparison"),
    ];

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Reports & monitoring").RequireAuthorization("app-or-admin");

        g.MapGet("/dashboard", async (HttpContext ctx, NotificationHubDbContext db, IMessageBus bus, [AsParameters] ReportFilter f) =>
        {
            var caller = await ctx.CallerAsync();
            // Everyone with dashboard access sees aggregate metrics for their own scope (not message content).
            var q = Deliveries(db, caller, f, Permission.ViewDashboard);
            var today = DateTime.UtcNow.Date;
            var byStatus = await q.GroupBy(d => d.Status).Select(x => new { x.Key, n = x.Count() }).ToListAsync(ctx.RequestAborted);
            int Count(params DeliveryStatus[] s) => byStatus.Where(x => s.Contains(x.Key)).Sum(x => x.n);
            var delivered = Count(DeliveryStatus.Delivered, DeliveryStatus.Read);
            var accepted = Count(DeliveryStatus.ProviderAccepted, DeliveryStatus.Sent);
            var failed = Count(DeliveryStatus.Failed, DeliveryStatus.Expired);
            var pending = Count(DeliveryStatus.Scheduled, DeliveryStatus.Queued, DeliveryStatus.Processing, DeliveryStatus.Submitted, DeliveryStatus.RetryPending);
            var byChannel = await q.GroupBy(d => d.Channel).Select(x => new { channel = x.Key, total = x.Count(), delivered = x.Count(d => d.Status == DeliveryStatus.Delivered || d.Status == DeliveryStatus.Read), failed = x.Count(d => d.Status == DeliveryStatus.Failed) }).ToListAsync(ctx.RequestAborted);
            var since = DateTime.UtcNow.AddHours(-24);
            var latency = await db.DeliveryAttempts.AsNoTracking().Where(a => a.StartedAt >= since && a.DurationMs != null && a.ProviderAccountId != null)
                .GroupBy(a => a.ProviderAccountId).Select(x => new { id = x.Key, avgMs = x.Average(a => a.DurationMs), attempts = x.Count() }).ToListAsync(ctx.RequestAborted);
            var providers = await ProviderEndpoints.VisibleAccounts(db, caller, Permission.ViewProviders).ToListAsync(ctx.RequestAborted);
            var queue = caller.Kind == CallerKind.Application ? null : await bus.GetStatsAsync(ctx.RequestAborted);
            var cost = await q.SumAsync(d => d.Cost ?? d.EstimatedCost ?? 0, ctx.RequestAborted);
            var hourly = await q.Where(d => d.CreatedAt >= since).Select(d => new { d.CreatedAt, d.Status }).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                totalNotifications = await Requests(db, caller, f, Permission.ViewDashboard).CountAsync(ctx.RequestAborted),
                totalDeliveries = byStatus.Sum(x => x.n),
                sentToday = await q.CountAsync(d => d.CreatedAt >= today, ctx.RequestAborted),
                delivered, accepted, failed, pending,
                suppressed = Count(DeliveryStatus.Suppressed), unknown = Count(DeliveryStatus.Unknown),
                successRate = delivered + accepted + failed == 0 ? (double?)null : Math.Round(100.0 * (delivered + accepted) / (delivered + accepted + failed), 2),
                byStatus = byStatus.ToDictionary(x => x.Key.ToString(), x => x.n),
                byChannel = byChannel.Select(x => new { channel = ChannelNames.ToWire(x.channel), x.total, x.delivered, x.failed }),
                providerHealth = providers.Select(p => new { p.Id, p.Name, channel = ChannelNames.ToWire(p.Channel), p.HealthStatus, p.Suspended, avgResponseMs = latency.FirstOrDefault(l => l.id == p.Id)?.avgMs, attempts24h = latency.FirstOrDefault(l => l.id == p.Id)?.attempts ?? 0 }),
                avgProviderResponseMs = latency.Count == 0 ? (double?)null : Math.Round(latency.Average(l => l.avgMs ?? 0), 1),
                queues = queue, retryBacklog = Count(DeliveryStatus.RetryPending), awaitingOperator = await q.CountAsync(d => d.AwaitingOperator, ctx.RequestAborted),
                estimatedCost = Math.Round(cost, 2),
                hourly = hourly.GroupBy(h => new DateTime(h.CreatedAt.Year, h.CreatedAt.Month, h.CreatedAt.Day, h.CreatedAt.Hour, 0, 0, DateTimeKind.Utc)).OrderBy(x => x.Key)
                    .Select(x => new { hour = x.Key, total = x.Count(), failed = x.Count(h => h.Status == DeliveryStatus.Failed) }),
            });
        }).WithSummary("Dashboard metrics (filter by company, application, channel, provider, date range)");

        g.MapGet("/reports", () => Results.Ok(Reports.Select(r => new { code = r.Code, name = r.Name }))).WithSummary("Available reports");

        g.MapGet("/reports/{code}", async (string code, HttpContext ctx, NotificationHubDbContext db, [AsParameters] ReportFilter f, string? format) =>
        {
            var caller = await ctx.CallerAsync();
            if (caller.Kind == CallerKind.Application && !caller.HasScope(Scopes.UsageRead)) throw new AccessDeniedException();
            var rows = await RunAsync(code, db, caller, f, ctx.RequestAborted);
            return string.IsNullOrEmpty(format) ? Results.Ok(new { code, name = Reports.FirstOrDefault(r => r.Code == code).Name, rows }) : Exporter.Export(rows, code, format);
        }).WithSummary("Run a report (add ?format=csv or ?format=xlsx to export)");

        g.MapGet("/usage", async (HttpContext ctx, NotificationHubDbContext db, [AsParameters] ReportFilter f, string groupBy = "channel") =>
        {
            var caller = await ctx.CallerAsync();
            if (caller.Kind == CallerKind.Application && !caller.HasScope(Scopes.UsageRead)) throw new AccessDeniedException();
            var q = Usage(db, caller, f);
            var rows = await q.ToListAsync(ctx.RequestAborted);
            var apps = await db.Applications.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ctx.RequestAborted);
            var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ctx.RequestAborted);
            var providers = await db.ProviderAccounts.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ctx.RequestAborted);
            Func<UsageRecord, string> key = groupBy switch
            {
                "application" => u => apps.GetValueOrDefault(u.ApplicationId, "?"),
                "company" => u => companies.GetValueOrDefault(u.CompanyId, "?"),
                "provider" => u => u.ProviderAccountId is { } p ? providers.GetValueOrDefault(p, "?") : "?",
                "tenant" => u => u.TenantId.ToString(),
                "month" => u => u.Period.ToString(),
                _ => u => ChannelNames.ToWire(u.Channel),
            };
            return Results.Ok(rows.GroupBy(key).OrderBy(x => x.Key).Select(x => new
            {
                key = x.Key, messages = x.Count(), units = x.Sum(u => u.Units), estimatedCost = Math.Round(x.Sum(u => u.EstimatedCost), 4),
                providerReportedCost = x.Any(u => u.ProviderReportedCost != null) ? Math.Round(x.Sum(u => u.ProviderReportedCost ?? 0), 4) : (decimal?)null,
                billedAmount = Math.Round(x.Sum(u => u.BilledAmount), 4), margin = Math.Round(x.Sum(u => u.BilledAmount - (u.ProviderReportedCost ?? u.EstimatedCost)), 4),
            }));
        }).WithSummary("Usage and billing (estimated vs provider-reported cost, billed amount, margin)");

        // ── monitoring (operators) ──
        var m = api.MapGroup("/monitoring").WithTags("Reports & monitoring").RequireAuthorization("admin");
        m.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, IMessageBus bus, CircuitBreakerRegistry circuits) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Grants.Any(g => RolePermissions.Has(g.Role, Permission.ViewMonitoring))) throw new AccessDeniedException();
            var heartbeats = await db.WorkerHeartbeats.AsNoTracking().OrderBy(h => h.Worker).ToListAsync(ctx.RequestAborted);
            var stale = DateTime.UtcNow.AddMinutes(-2);
            var dead = await db.QueueMessages.AsNoTracking().Where(q => q.DeadLetteredAt != null).OrderByDescending(q => q.DeadLetteredAt).Take(50)
                .Select(q => new { q.Id, q.Queue, q.Type, q.DequeueCount, q.LastError, q.DeadLetteredAt, q.EnqueuedAt }).ToListAsync(ctx.RequestAborted);
            var alerts = await db.SystemAlerts.AsNoTracking().Where(a => a.AcknowledgedAt == null).OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ctx.RequestAborted);
            var outboxBacklog = await db.OutboxMessages.CountAsync(o => o.PublishedAt == null, ctx.RequestAborted);
            var errors = await db.DeliveryAttempts.AsNoTracking().Where(a => a.Outcome != AttemptOutcome.Accepted && a.StartedAt >= DateTime.UtcNow.AddHours(-24))
                .GroupBy(a => new { a.ProviderAccountId, a.ErrorCode }).Select(x => new { x.Key.ProviderAccountId, x.Key.ErrorCode, n = x.Count() }).OrderByDescending(x => x.n).Take(30).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                transport = bus.Name, queues = await bus.GetStatsAsync(ctx.RequestAborted), outboxBacklog,
                workers = heartbeats.Select(h => new { h.Worker, h.Instance, status = h.LastHeartbeatAt < stale && h.Status == "running" ? "unresponsive" : h.Status, h.Processed, h.Failed, h.LastError, h.StartedAt, h.LastHeartbeatAt }),
                retries = await db.NotificationDeliveries.CountAsync(d => d.Status == DeliveryStatus.RetryPending, ctx.RequestAborted),
                awaitingOperator = await db.NotificationDeliveries.CountAsync(d => d.AwaitingOperator, ctx.RequestAborted),
                deadLetters = dead, providerErrors24h = errors,
                circuits = circuits.Snapshot().Select(kv => new { providerAccountId = kv.Key, kv.Value.Failures, kv.Value.OpenUntil }),
                alerts = alerts.Where(a => a.TenantId is null ? caller.IsPlatformAdmin : caller.Can(Permission.ViewMonitoring, a.TenantId)),
            });
        }).WithSummary("Queues, workers, retries, dead letters, provider errors and alerts");
        m.MapPost("/dead-letters/{id:guid}/requeue", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin && !caller.Grants.Any(g => g.Role == Roles.Operator)) throw new AccessDeniedException();
            var n = await db.QueueMessages.Where(q => q.Id == id && q.DeadLetteredAt != null).ExecuteUpdateAsync(s => s.SetProperty(q => q.DeadLetteredAt, (DateTime?)null)
                .SetProperty(q => q.DequeueCount, 0).SetProperty(q => q.VisibleAt, DateTime.UtcNow).SetProperty(q => q.LeaseExpiresAt, (DateTime?)null), ctx.RequestAborted);
            audit.Record(caller, "queue.requeued", nameof(QueueMessage), id.ToString(), null);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
        m.MapPost("/alerts/{id:guid}/ack", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.SystemAlerts.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Alert");
            if (a.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ViewMonitoring, a.TenantId)) throw HubException.NotFound("Alert");
            a.AcknowledgedAt = DateTime.UtcNow; a.AcknowledgedBy = caller.ActorId;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });

        api.MapGet("/audit", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId, string? entityType, string? entityId, string? action, DateTime? from, int page = 1) =>
        {
            var caller = await ctx.CallerAsync();
            var filter = caller.FilterFor(Permission.ViewAudit);
            if (filter.IsEmpty) throw new AccessDeniedException();
            var q = db.AuditLogs.AsNoTracking();
            if (!filter.IsUnrestricted) { var tenants = filter.TenantIds.ToList(); q = q.Where(a => a.TenantId != null && tenants.Contains(a.TenantId.Value)); }
            if (tenantId is { } t) q = q.Where(a => a.TenantId == t);
            if (!string.IsNullOrEmpty(entityType)) q = q.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrEmpty(entityId)) q = q.Where(a => a.EntityId == entityId);
            if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action.StartsWith(action));
            if (from is { } fr) q = q.Where(a => a.OccurredAt >= fr.ToUniversalTime());
            return Results.Ok(new { total = await q.CountAsync(ctx.RequestAborted), items = await q.OrderByDescending(a => a.OccurredAt).Skip((page - 1) * 100).Take(100).ToListAsync(ctx.RequestAborted) });
        }).WithTags("Reports & monitoring").RequireAuthorization("admin").WithSummary("Audit trail");
    }

    private static IQueryable<NotificationDelivery> Deliveries(NotificationHubDbContext db, CallerContext caller, ReportFilter f, Permission permission = Permission.ViewUsage)
    {
        var q = db.NotificationDeliveries.AsNoTracking().Visible(caller.FilterFor(permission));
        if (f.TenantId is { } t) q = q.Where(d => d.TenantId == t);
        if (f.CompanyId is { } c) q = q.Where(d => d.CompanyId == c);
        if (f.ApplicationId is { } a) q = q.Where(d => d.ApplicationId == a);
        if (f.Channel is { } ch) q = q.Where(d => d.Channel == ch);
        if (f.ProviderAccountId is { } p) q = q.Where(d => d.ProviderAccountId == p);
        if (f.From is { } from) q = q.Where(d => d.CreatedAt >= from.ToUniversalTime());
        if (f.To is { } to) q = q.Where(d => d.CreatedAt < to.ToUniversalTime());
        return q;
    }

    private static IQueryable<NotificationRequest> Requests(NotificationHubDbContext db, CallerContext caller, ReportFilter f, Permission permission = Permission.ViewUsage)
    {
        var q = db.NotificationRequests.AsNoTracking().Visible(caller.FilterFor(permission));
        if (f.TenantId is { } t) q = q.Where(d => d.TenantId == t);
        if (f.CompanyId is { } c) q = q.Where(d => d.CompanyId == c);
        if (f.ApplicationId is { } a) q = q.Where(d => d.ApplicationId == a);
        if (f.From is { } from) q = q.Where(d => d.CreatedAt >= from.ToUniversalTime());
        if (f.To is { } to) q = q.Where(d => d.CreatedAt < to.ToUniversalTime());
        return q;
    }

    private static IQueryable<UsageRecord> Usage(NotificationHubDbContext db, CallerContext caller, ReportFilter f)
    {
        var q = db.UsageRecords.AsNoTracking().Visible(caller.FilterFor(Permission.ViewUsage));
        if (f.TenantId is { } t) q = q.Where(d => d.TenantId == t);
        if (f.CompanyId is { } c) q = q.Where(d => d.CompanyId == c);
        if (f.ApplicationId is { } a) q = q.Where(d => d.ApplicationId == a);
        if (f.Channel is { } ch) q = q.Where(d => d.Channel == ch);
        if (f.ProviderAccountId is { } p) q = q.Where(d => d.ProviderAccountId == p);
        if (f.From is { } from) q = q.Where(d => d.OccurredAt >= from.ToUniversalTime());
        if (f.To is { } to) q = q.Where(d => d.OccurredAt < to.ToUniversalTime());
        return q;
    }

    public static async Task<List<Dictionary<string, object?>>> RunAsync(string code, NotificationHubDbContext db, CallerContext caller, ReportFilter f, CancellationToken ct)
    {
        var d = Deliveries(db, caller, f);
        var providers = await db.ProviderAccounts.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var apps = await db.Applications.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string P(Guid? id) => id is { } x ? providers.GetValueOrDefault(x, "?") : "(none)";
        static Dictionary<string, object?> R(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);
        static double? Pct(int a, int b) => b == 0 ? null : Math.Round(100.0 * a / b, 2);

        switch (code)
        {
            case "channel-volumes":
                return (await d.GroupBy(x => x.Channel).Select(g => new { g.Key, n = g.Count(), units = g.Sum(x => x.Units) }).ToListAsync(ct))
                    .Select(x => R(("Channel", ChannelNames.ToWire(x.Key)), ("Deliveries", x.n), ("Units", x.units))).ToList();
            case "application-consumption":
                return (await d.GroupBy(x => new { x.ApplicationId, x.Channel }).Select(g => new { g.Key.ApplicationId, g.Key.Channel, n = g.Count(), cost = g.Sum(x => x.Cost ?? x.EstimatedCost ?? 0) }).ToListAsync(ct))
                    .OrderBy(x => apps.GetValueOrDefault(x.ApplicationId)).Select(x => R(("Application", apps.GetValueOrDefault(x.ApplicationId)), ("Channel", ChannelNames.ToWire(x.Channel)), ("Deliveries", x.n), ("Cost", Math.Round(x.cost, 2)))).ToList();
            case "company-consumption":
                return (await d.GroupBy(x => new { x.CompanyId, x.Channel }).Select(g => new { g.Key.CompanyId, g.Key.Channel, n = g.Count(), cost = g.Sum(x => x.Cost ?? x.EstimatedCost ?? 0) }).ToListAsync(ct))
                    .OrderBy(x => companies.GetValueOrDefault(x.CompanyId)).Select(x => R(("Company", companies.GetValueOrDefault(x.CompanyId)), ("Channel", ChannelNames.ToWire(x.Channel)), ("Deliveries", x.n), ("Cost", Math.Round(x.cost, 2)))).ToList();
            case "provider-performance":
            {
                var attempts = db.DeliveryAttempts.AsNoTracking().Where(a => d.Select(x => x.Id).Contains(a.DeliveryId));
                return (await attempts.GroupBy(a => a.ProviderAccountId).Select(g => new
                {
                    g.Key, total = g.Count(), ok = g.Count(a => a.Outcome == AttemptOutcome.Accepted), transient = g.Count(a => a.Outcome == AttemptOutcome.TransientFailure),
                    permanent = g.Count(a => a.Outcome == AttemptOutcome.PermanentFailure), ambiguous = g.Count(a => a.Outcome == AttemptOutcome.Ambiguous), avg = g.Average(a => a.DurationMs),
                }).ToListAsync(ct)).Select(x => R(("Provider", P(x.Key)), ("Attempts", x.total), ("Accepted", x.ok), ("AcceptanceRate%", Pct(x.ok, x.total)), ("Transient", x.transient),
                    ("Permanent", x.permanent), ("Ambiguous", x.ambiguous), ("AvgResponseMs", x.avg is null ? null : Math.Round(x.avg.Value, 1)))).ToList();
            }
            case "delivery-success":
                return (await d.GroupBy(x => x.Channel).Select(g => new
                {
                    g.Key, total = g.Count(), delivered = g.Count(x => x.Status == DeliveryStatus.Delivered || x.Status == DeliveryStatus.Read),
                    accepted = g.Count(x => x.Status == DeliveryStatus.ProviderAccepted || x.Status == DeliveryStatus.Sent), failed = g.Count(x => x.Status == DeliveryStatus.Failed || x.Status == DeliveryStatus.Expired),
                    suppressed = g.Count(x => x.Status == DeliveryStatus.Suppressed), read = g.Count(x => x.Status == DeliveryStatus.Read),
                }).ToListAsync(ct)).Select(x => R(("Channel", ChannelNames.ToWire(x.Key)), ("Total", x.total), ("Delivered", x.delivered), ("AcceptedNotConfirmed", x.accepted), ("Failed", x.failed),
                    ("Suppressed", x.suppressed), ("Read", x.read), ("DeliveryRate%", Pct(x.delivered, x.total - x.suppressed)), ("SuccessRate%", Pct(x.delivered + x.accepted, x.delivered + x.accepted + x.failed)))).ToList();
            case "failure-reasons":
                return (await d.Where(x => x.Status == DeliveryStatus.Failed || x.Status == DeliveryStatus.Suppressed || x.Status == DeliveryStatus.Expired)
                    .GroupBy(x => new { x.Channel, x.Status, x.FailureCode }).Select(g => new { g.Key.Channel, g.Key.Status, g.Key.FailureCode, n = g.Count() }).ToListAsync(ct))
                    .OrderByDescending(x => x.n).Select(x => R(("Channel", ChannelNames.ToWire(x.Channel)), ("Status", x.Status.ToString()), ("Reason", x.FailureCode ?? "(unspecified)"), ("Count", x.n))).ToList();
            case "delivery-latency":
            {
                var rows = await d.Where(x => x.AcceptedAt != null).Select(x => new { x.Channel, x.ProviderAccountId, x.CreatedAt, x.AcceptedAt, x.DeliveredAt }).ToListAsync(ct);
                return rows.GroupBy(x => new { x.Channel, x.ProviderAccountId }).Select(g => R(("Channel", ChannelNames.ToWire(g.Key.Channel)), ("Provider", P(g.Key.ProviderAccountId)), ("Messages", g.Count()),
                    ("AvgAcceptanceSec", Math.Round(g.Average(x => (x.AcceptedAt!.Value - x.CreatedAt).TotalSeconds), 2)),
                    ("AvgDeliverySec", g.Any(x => x.DeliveredAt != null) ? Math.Round(g.Where(x => x.DeliveredAt != null).Average(x => (x.DeliveredAt!.Value - x.CreatedAt).TotalSeconds), 2) : null))).ToList();
            }
            case "cost-by-channel-provider":
                return (await Usage(db, caller, f).GroupBy(u => new { u.Channel, u.ProviderAccountId }).Select(g => new { g.Key.Channel, g.Key.ProviderAccountId, n = g.Count(), units = g.Sum(u => u.Units), est = g.Sum(u => u.EstimatedCost), rep = g.Sum(u => u.ProviderReportedCost ?? 0), billed = g.Sum(u => u.BilledAmount) }).ToListAsync(ct))
                    .Select(x => R(("Channel", ChannelNames.ToWire(x.Channel)), ("Provider", P(x.ProviderAccountId)), ("Messages", x.n), ("Units", x.units), ("EstimatedCost", Math.Round(x.est, 2)), ("ProviderReportedCost", Math.Round(x.rep, 2)), ("Billed", Math.Round(x.billed, 2)))).ToList();
            case "cost-by-company-application":
                return (await Usage(db, caller, f).GroupBy(u => new { u.CompanyId, u.ApplicationId }).Select(g => new { g.Key.CompanyId, g.Key.ApplicationId, n = g.Count(), est = g.Sum(u => u.EstimatedCost), billed = g.Sum(u => u.BilledAmount) }).ToListAsync(ct))
                    .Select(x => R(("Company", companies.GetValueOrDefault(x.CompanyId)), ("Application", apps.GetValueOrDefault(x.ApplicationId)), ("Messages", x.n), ("EstimatedCost", Math.Round(x.est, 2)), ("Billed", Math.Round(x.billed, 2)))).ToList();
            case "campaign-performance":
            {
                var campaigns = await db.Campaigns.AsNoTracking().Visible(caller.FilterFor(Permission.ViewCampaigns)).ToListAsync(ct);
                var ids = campaigns.Select(c => c.Id).ToList();
                var stats = await db.NotificationDeliveries.AsNoTracking().Where(x => x.CampaignId != null && ids.Contains(x.CampaignId.Value)).GroupBy(x => x.CampaignId).Select(g => new
                {
                    g.Key, total = g.Count(), delivered = g.Count(x => x.Status == DeliveryStatus.Delivered || x.Status == DeliveryStatus.Read), read = g.Count(x => x.Status == DeliveryStatus.Read),
                    failed = g.Count(x => x.Status == DeliveryStatus.Failed), suppressed = g.Count(x => x.Status == DeliveryStatus.Suppressed), cost = g.Sum(x => x.Cost ?? 0),
                }).ToListAsync(ct);
                return campaigns.Select(c => { var s = stats.FirstOrDefault(x => x.Key == c.Id); return R(("Campaign", c.Name), ("Status", c.Status.ToString()), ("Recipients", c.TotalRecipients), ("Deliveries", s?.total ?? 0), ("Delivered", s?.delivered ?? 0), ("Read", s?.read ?? 0), ("Failed", s?.failed ?? 0), ("Suppressed", s?.suppressed ?? 0), ("Cost", Math.Round(s?.cost ?? 0, 2))); }).ToList();
            }
            case "template-usage":
                return (await d.GroupBy(x => new { x.TemplateId, x.TemplateVersionNumber, x.Channel }).Select(g => new { g.Key.TemplateId, g.Key.TemplateVersionNumber, g.Key.Channel, n = g.Count() }).ToListAsync(ct))
                    .Join(await db.NotificationTemplates.AsNoTracking().Select(t => new { t.Id, t.Code, t.Locale }).ToListAsync(ct), x => x.TemplateId, t => t.Id, (x, t) => R(("Template", t.Code), ("Version", x.TemplateVersionNumber), ("Channel", ChannelNames.ToWire(x.Channel)), ("Locale", t.Locale), ("Uses", x.n)))
                    .OrderByDescending(x => (int)x["Uses"]!).ToList();
            case "retry-statistics":
                return (await d.GroupBy(x => new { x.Channel, x.AttemptCount }).Select(g => new { g.Key.Channel, g.Key.AttemptCount, n = g.Count(), ok = g.Count(x => x.Status == DeliveryStatus.Delivered || x.Status == DeliveryStatus.Read || x.Status == DeliveryStatus.ProviderAccepted || x.Status == DeliveryStatus.Sent) }).ToListAsync(ct))
                    .OrderBy(x => x.Channel).ThenBy(x => x.AttemptCount).Select(x => R(("Channel", ChannelNames.ToWire(x.Channel)), ("Attempts", x.AttemptCount), ("Deliveries", x.n), ("EventuallySucceeded", x.ok))).ToList();
            case "consent-optout":
            {
                var filter = caller.FilterFor(Permission.ViewContacts);
                var consents = await db.RecipientConsents.AsNoTracking().Visible(filter).GroupBy(c => new { c.Channel, c.Purpose, c.Status }).Select(g => new { g.Key.Channel, g.Key.Purpose, g.Key.Status, n = g.Count() }).ToListAsync(ct);
                var suppressions = await db.Suppressions.AsNoTracking().Visible(filter, includeBroader: true).GroupBy(s => new { s.Channel, s.Reason }).Select(g => new { g.Key.Channel, g.Key.Reason, n = g.Count() }).ToListAsync(ct);
                return consents.Select(x => R(("Type", "consent"), ("Channel", ChannelNames.ToWire(x.Channel)), ("Detail", $"{x.Purpose} {x.Status}"), ("Count", x.n)))
                    .Concat(suppressions.Select(x => R(("Type", "suppression"), ("Channel", ChannelNames.ToWire(x.Channel)), ("Detail", x.Reason.ToString()), ("Count", x.n)))).ToList();
            }
            case "queue-backlog":
            {
                var since = f.From?.ToUniversalTime() ?? DateTime.UtcNow.AddDays(-2);
                var rows = await d.Where(x => x.CreatedAt >= since).Select(x => new { x.CreatedAt, x.SubmittedAt, x.Status }).ToListAsync(ct);
                return rows.GroupBy(x => new DateTime(x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day, x.CreatedAt.Hour, 0, 0)).OrderBy(g => g.Key).Select(g => R(("Hour", g.Key),
                    ("Created", g.Count()), ("StillPending", g.Count(x => x.Status is DeliveryStatus.Queued or DeliveryStatus.RetryPending or DeliveryStatus.Scheduled)),
                    ("AvgQueueWaitSec", g.Any(x => x.SubmittedAt != null) ? Math.Round(g.Where(x => x.SubmittedAt != null).Average(x => (x.SubmittedAt!.Value - x.CreatedAt).TotalSeconds), 2) : null))).ToList();
            }
            case "monthly-usage":
                return (await Usage(db, caller, f with { From = f.From ?? DateTime.UtcNow.AddMonths(-12) }).GroupBy(u => new { u.Period, u.Channel }).Select(g => new { g.Key.Period, g.Key.Channel, n = g.Count(), billed = g.Sum(u => u.BilledAmount) }).ToListAsync(ct))
                    .OrderBy(x => x.Period).Select(x => R(("Month", $"{x.Period / 100}-{x.Period % 100:00}"), ("Channel", ChannelNames.ToWire(x.Channel)), ("Messages", x.n), ("Billed", Math.Round(x.billed, 2)))).ToList();
            default:
                throw HubException.NotFound($"Report '{code}'");
        }
    }
}
