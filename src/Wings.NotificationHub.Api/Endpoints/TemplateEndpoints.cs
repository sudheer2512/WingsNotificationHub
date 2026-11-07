using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record CreateTemplateRequest(TemplateUpsertRequest Template, TemplateVersionRequest Version);
public sealed record ReviewRequest(bool Approve, string? Comment, bool Activate = true);
public sealed record TestSendRequest(Guid CompanyId, Guid? ApplicationId, RecipientDto Recipient, Dictionary<string, JsonElement>? Variables, int? Version);
public sealed record WhatsAppSubmitRequest(Guid ProviderAccountId, int Version);

public sealed class NotificationTypeRequest
{
    public Guid TenantId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "General";
    public MessageClass MessageClass { get; set; }
    public List<Channel> DefaultChannels { get; set; } = [];
    public string? DefaultTemplateCode { get; set; }
    public NotificationPriority DefaultPriority { get; set; } = NotificationPriority.Normal;
    public bool Mandatory { get; set; }
    public bool AiAllowed { get; set; }
    public bool RequiresHumanApprovalForAi { get; set; } = true;
    public bool IsSensitive { get; set; }
}

public static class TemplateEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/templates").WithTags("Templates").RequireAuthorization("app-or-admin");

        g.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId, Guid? companyId, Guid? applicationId, Channel? channel, string? code, TemplateStatus? status, string? q) =>
        {
            var caller = await ctx.CallerAsync();
            if (caller.Kind == CallerKind.Application && !caller.HasScope(Scopes.TemplatesRead)) throw new AccessDeniedException();
            var query = db.NotificationTemplates.AsNoTracking().Visible(caller.FilterFor(Permission.ViewTemplates), includeBroader: true);
            if (tenantId is { } t) query = query.Where(x => x.TenantId == t);
            if (companyId is { } c) query = query.Where(x => x.CompanyId == c || x.CompanyId == null);
            if (applicationId is { } a) query = query.Where(x => x.ApplicationId == a || x.ApplicationId == null);
            if (channel is { } ch) query = query.Where(x => x.Channel == ch);
            if (!string.IsNullOrWhiteSpace(code)) query = query.Where(x => x.Code == code.ToUpper());
            if (status is { } st) query = query.Where(x => x.Status == st);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Code.Contains(q.ToUpper()) || x.Name.ToLower().Contains(q.ToLower()));
            var rows = await query.OrderBy(x => x.Code).ThenBy(x => x.Channel).Take(1000).ToListAsync(ctx.RequestAborted);
            var activeIds = rows.Where(r => r.ActiveVersionId != null).Select(r => r.ActiveVersionId!.Value).ToList();
            var vars = await db.TemplateVariables.AsNoTracking().Where(v => activeIds.Contains(v.TemplateVersionId)).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(r => Summary(r, vars.Where(v => v.TemplateVersionId == r.ActiveVersionId))));
        }).WithSummary("List templates visible to the caller");

        g.MapGet("/tree", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var filter = caller.FilterFor(Permission.ViewTemplates);
            var templates = await db.NotificationTemplates.AsNoTracking().Visible(filter, includeBroader: true).ToListAsync(ctx.RequestAborted);
            var tenantIds = templates.Select(t => t.TenantId).Concat(filter.IsUnrestricted ? await db.Tenants.Select(t => t.Id).ToListAsync(ctx.RequestAborted) : filter.TenantIds).Distinct().ToList();
            var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToListAsync(ctx.RequestAborted);
            var companies = await db.Companies.AsNoTracking().Where(c => tenantIds.Contains(c.TenantId)).ToListAsync(ctx.RequestAborted);
            var apps = await db.Applications.AsNoTracking().Where(a => tenantIds.Contains(a.TenantId)).ToListAsync(ctx.RequestAborted);
            object Leaf(NotificationTemplate t) => new { type = "template", t.Id, t.Code, t.Name, channel = ChannelNames.ToWire(t.Channel), t.Locale, t.Status, t.ActiveVersionNumber, t.LatestVersionNumber };
            object Categories(IEnumerable<NotificationTemplate> list) => list.GroupBy(t => t.Category ?? "Uncategorized").OrderBy(g => g.Key)
                .Select(g => new { type = "category", name = g.Key, children = g.OrderBy(t => t.Code).ThenBy(t => t.Channel).Select(Leaf) });
            object AppNodes(Guid tenantId, Guid? companyId) => apps.Where(a => a.TenantId == tenantId)
                .Select(a => new { type = "application", a.Id, a.Name, children = Categories(templates.Where(t => t.TenantId == tenantId && t.CompanyId == companyId && t.ApplicationId == a.Id)) })
                .Prepend(new { type = "application", Id = Guid.Empty, Name = "All applications", children = Categories(templates.Where(t => t.TenantId == tenantId && t.CompanyId == companyId && t.ApplicationId == null)) });
            return Results.Ok(tenants.OrderBy(t => t.Name).Select(t => new
            {
                type = "tenant", t.Id, t.Name,
                children = companies.Where(c => c.TenantId == t.Id).OrderBy(c => c.Name)
                    .Select(c => new { type = "company", c.Id, c.Name, children = AppNodes(t.Id, c.Id) })
                    .Prepend(new { type = "company", Id = Guid.Empty, Name = "All companies (tenant-wide)", children = AppNodes(t.Id, null) }),
            }));
        }).WithSummary("Tenant → company → application → category → template tree");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, TemplateService svc, NotificationHubDbContext db) =>
        {
            var t = await svc.LoadAsync(await ctx.CallerAsync(), id, Permission.ViewTemplates, ctx.RequestAborted);
            var userIds = t.Versions.SelectMany(v => new[] { v.CreatedBy, v.SubmittedBy, v.ApprovedBy }).Select(a => Guid.TryParse(a, out var u) ? u : (Guid?)null).OfType<Guid>().Distinct().ToList();
            var actors = await db.AdminUsers.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id.ToString(), u => u.DisplayName, ctx.RequestAborted);
            return Results.Ok(new
            {
                actors,
                template = Summary(t, t.Versions.FirstOrDefault(v => v.Id == t.ActiveVersionId)?.Variables ?? []),
                versions = t.Versions.OrderByDescending(v => v.VersionNumber).Select(v => new
                {
                    v.Id, v.VersionNumber, v.Status, v.Subject, v.Body, v.BodyFormat, v.TextBody, components = Json.Deserialize<TemplateComponents>(v.ComponentsJson),
                    v.ProviderTemplateName, v.ProviderTemplateLanguage, v.ProviderCategory, v.ProviderApprovalStatus, v.DltTemplateId, v.ChangeNote,
                    v.CreatedBy, v.CreatedAt, v.SubmittedBy, v.SubmittedAt, v.ApprovedBy, v.ApprovedAt, v.RejectionReason,
                    variables = v.Variables.OrderBy(x => x.Name).Select(x => new TemplateVariableDto { Name = x.Name, Required = x.Required, SampleValue = x.SampleValue, Description = x.Description }),
                }),
            });
        }).WithSummary("Template with all versions");

        g.MapPost("", async (CreateTemplateRequest req, HttpContext ctx, TemplateService svc) =>
        {
            var t = await svc.CreateAsync(await ctx.CallerAsync(), req.Template, req.Version, ctx.RequestAborted);
            return Results.Created($"/api/v1/templates/{t.Id}", new { t.Id, t.Code, version = 1 });
        }).WithSummary("Create a template (first version is a draft)");

        g.MapPut("/{id:guid}/draft", async (Guid id, TemplateVersionRequest req, HttpContext ctx, TemplateService svc) =>
        {
            var v = await svc.SaveDraftAsync(await ctx.CallerAsync(), id, req, ctx.RequestAborted);
            return Results.Ok(new { v.Id, v.VersionNumber, v.Status });
        }).WithSummary("Save the working draft (creates a new version when the latest is submitted)");

        g.MapPost("/{id:guid}/versions/{version:int}/submit", async (Guid id, int version, HttpContext ctx, TemplateService svc) =>
            Results.Ok(new { (await svc.SubmitAsync(await ctx.CallerAsync(), id, version, ctx.RequestAborted)).Status })).WithSummary("Submit a draft for review");

        g.MapPost("/{id:guid}/versions/{version:int}/review", async (Guid id, int version, ReviewRequest req, HttpContext ctx, TemplateService svc) =>
            Results.Ok(new { (await svc.ReviewAsync(await ctx.CallerAsync(), id, version, req.Approve, req.Comment, req.Activate, ctx.RequestAborted)).Status }))
            .WithSummary("Approve or reject a version (maker-checker)");

        g.MapPost("/{id:guid}/versions/{version:int}/activate", async (Guid id, int version, HttpContext ctx, TemplateService svc) =>
        {
            await svc.ActivateAsync(await ctx.CallerAsync(), id, version, ctx.RequestAborted);
            return Results.NoContent();
        }).WithSummary("Activate an approved version (or roll back to a retired one)");

        g.MapPost("/{id:guid}/retire", async (Guid id, HttpContext ctx, TemplateService svc) =>
        {
            await svc.RetireAsync(await ctx.CallerAsync(), id, ctx.RequestAborted);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/clone", async (Guid id, TemplateUpsertRequest target, HttpContext ctx, TemplateService svc) =>
        {
            var t = await svc.CloneAsync(await ctx.CallerAsync(), id, target, ctx.RequestAborted);
            return Results.Created($"/api/v1/templates/{t.Id}", new { t.Id });
        }).WithSummary("Clone into a new code, scope or locale (e.g. a translation)");

        g.MapPost("/{id:guid}/test-send", async (Guid id, TestSendRequest req, HttpContext ctx, TemplateService svc, NotificationService notifications, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var t = await svc.LoadAsync(caller, id, Permission.ManageTemplates, ctx.RequestAborted);
            var appId = req.ApplicationId ?? t.ApplicationId ?? caller.ApplicationId ?? throw HubException.Validation("applicationId is required for a test send.");
            // Test sends use the version under review through a temporary dry-run-free path: only active versions are deliverable,
            // so non-active versions are rendered and returned as a preview, while the active version is actually delivered.
            if (req.Version is { } n && n != t.ActiveVersionNumber)
            {
                var v = t.Versions.First(x => x.VersionNumber == n);
                var preview = await svc.PreviewAsync(caller, new TemplatePreviewRequest { TemplateId = id, TemplateVersionId = v.Id, Variables = req.Variables }, ctx.RequestAborted);
                return Results.Ok(new { delivered = false, note = "Only the active version can be delivered; rendered preview returned.", preview });
            }
            var resp = await notifications.AcceptAsync(caller, new SendNotificationRequest
            {
                ApplicationId = appId, CompanyId = req.CompanyId, TemplateCode = t.Code, Channels = [t.Channel], Recipient = req.Recipient, Variables = req.Variables,
                Metadata = new Dictionary<string, string> { ["testSend"] = "true" },
            }, RequestSource.TestSend, new AcceptOptions { RequiredPermission = Permission.ManageTemplates }, ctx.RequestAborted);
            return Results.Ok(new { delivered = true, resp });
        }).WithSummary("Send a test message using the template");

        g.MapGet("/{id:guid}/whatsapp-accounts", async (Guid id, HttpContext ctx, TemplateService svc, NotificationHubDbContext db) =>
        {
            var t = await svc.LoadAsync(await ctx.CallerAsync(), id, Permission.ManageTemplates, ctx.RequestAborted);
            return Results.Ok(await db.ProviderAccounts.AsNoTracking().Where(a => a.Channel == Channel.WhatsApp && (a.TenantId == null || a.TenantId == t.TenantId))
                .OrderBy(a => a.Name).Select(a => new { a.Id, a.Name, a.ProviderType }).ToListAsync(ctx.RequestAborted));
        }).WithSummary("WhatsApp accounts this template can be submitted through (names only; no provider access needed)");

        g.MapPost("/{id:guid}/whatsapp-submit", async (Guid id, WhatsAppSubmitRequest req, HttpContext ctx, TemplateService svc) =>
            Results.Ok(new { status = await svc.SubmitToWhatsAppAsync(await ctx.CallerAsync(), id, req.Version, req.ProviderAccountId, ctx.RequestAborted) }))
            .WithSummary("Submit a WhatsApp template to Meta for approval");

        api.MapPost("/providers/{id:guid}/whatsapp-templates/sync", async (Guid id, HttpContext ctx, TemplateService svc) =>
            Results.Ok(new { updated = await svc.SyncWhatsAppStatusesAsync(await ctx.CallerAsync(), id, ctx.RequestAborted) }))
            .WithTags("Templates").RequireAuthorization("admin").WithSummary("Refresh WhatsApp template approval statuses from Meta");

        // ── notification types ──
        var types = api.MapGroup("/notification-types").WithTags("Notification types").RequireAuthorization("app-or-admin");
        types.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId, Guid? applicationId) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.NotificationTypes.AsNoTracking().Visible(caller.FilterFor(Permission.ViewTemplates), includeBroader: true);
            if (tenantId is { } t) q = q.Where(x => x.TenantId == t);
            if (applicationId is { } a) q = q.Where(x => x.ApplicationId == a || x.ApplicationId == null);
            return Results.Ok(await q.OrderBy(x => x.Code).ToListAsync(ctx.RequestAborted));
        });
        types.MapPost("", async (NotificationTypeRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageTemplates, req.TenantId, null, req.ApplicationId)) throw new AccessDeniedException();
            var code = req.Code.Trim().ToUpperInvariant();
            var existing = await db.NotificationTypes.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.ApplicationId == req.ApplicationId && x.Code == code, ctx.RequestAborted);
            var t = existing ?? new NotificationType { TenantId = req.TenantId, ApplicationId = req.ApplicationId, Code = code };
            t.Name = req.Name; t.Description = req.Description; t.Category = req.Category; t.MessageClass = req.MessageClass; t.DefaultChannels = req.DefaultChannels;
            t.DefaultTemplateCode = req.DefaultTemplateCode?.ToUpperInvariant(); t.DefaultPriority = req.DefaultPriority; t.Mandatory = req.Mandatory;
            t.AiAllowed = req.AiAllowed; t.RequiresHumanApprovalForAi = req.RequiresHumanApprovalForAi; t.IsSensitive = req.IsSensitive;
            if (existing is null) db.NotificationTypes.Add(t);
            audit.Record(caller, existing is null ? "notification_type.created" : "notification_type.updated", nameof(NotificationType), t.Id.ToString(), code, t.TenantId, null, t.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(t);
        }).WithSummary("Create or update a notification type");
    }

    public static TemplateSummaryDto Summary(NotificationTemplate t, IEnumerable<TemplateVariable> vars) => new()
    {
        Id = t.Id, Code = t.Code, Name = t.Name, Description = t.Description, Category = t.Category, Channel = t.Channel, Locale = t.Locale, Status = t.Status,
        ActiveVersion = t.ActiveVersionNumber, LatestVersion = t.LatestVersionNumber, TenantId = t.TenantId, CompanyId = t.CompanyId, ApplicationId = t.ApplicationId,
        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc)),
        Variables = vars.OrderBy(v => v.Name).Select(v => new TemplateVariableDto { Name = v.Name, Required = v.Required, SampleValue = v.SampleValue, Description = v.Description }).ToList(),
    };
}

public sealed class RuleRequest
{
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string EventType { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public bool StopProcessing { get; set; }
    public string? NotificationTypeCode { get; set; }
    public ConditionNode? Conditions { get; set; }
    public RecipientSelector Recipients { get; set; } = new();
    public List<RuleChannelAction> Actions { get; set; } = [];
    public NotificationPriority? Priority { get; set; }
    public int DelaySeconds { get; set; }
    public EscalationDto? Escalation { get; set; }
    public RetryPolicyDto? RetryPolicy { get; set; }
    public Core.Policies.QuietHoursPolicy? QuietHours { get; set; }
}

public sealed record SimulateRequest(BusinessEventRequest Event, Guid? RuleId);

public static class RuleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/events", async (BusinessEventRequest req, HttpContext ctx, RuleEngine engine) =>
        {
            req.IdempotencyKey ??= ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var resp = await engine.PublishAsync(await ctx.CallerAsync(), req, ctx.RequestAborted);
            return resp.Duplicate ? Results.Ok(resp) : Results.Accepted($"/api/v1/events/{resp.EventId}", resp);
        }).WithTags("Events").RequireAuthorization("app-or-admin").WithSummary("Raise a business event for rule-based notifications");

        api.MapGet("/events/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var e = await db.BusinessEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (e is null || !caller.Can(Permission.ViewNotifications, e.TenantId, e.CompanyId, e.ApplicationId) || (caller.Kind == CallerKind.Application && caller.ApplicationId != e.ApplicationId))
                throw HubException.NotFound("Event");
            var notifications = await db.NotificationRequests.AsNoTracking().Where(r => r.BusinessEventId == id).Select(r => new { r.Id, r.Status, r.RuleId, r.TemplateCode }).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { e.Id, e.EventType, e.ReceivedAt, e.ProcessedAt, e.MatchedRules, e.NotificationsCreated, e.ProcessingError, notifications });
        }).WithTags("Events").RequireAuthorization("app-or-admin");

        api.MapGet("/events", async (HttpContext ctx, NotificationHubDbContext db, string? eventType, int page = 1, int pageSize = 50) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.BusinessEvents.AsNoTracking().Visible(caller.FilterFor(Permission.ViewNotifications));
            if (!string.IsNullOrEmpty(eventType)) q = q.Where(e => e.EventType == eventType);
            pageSize = Math.Clamp(pageSize, 1, 200);
            return Results.Ok(new
            {
                total = await q.CountAsync(ctx.RequestAborted),
                items = await q.OrderByDescending(e => e.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize)
                    .Select(e => new { e.Id, e.EventType, e.CompanyId, e.ApplicationId, e.ReceivedAt, e.ProcessedAt, e.MatchedRules, e.NotificationsCreated, e.ProcessingError }).ToListAsync(ctx.RequestAborted),
            });
        }).WithTags("Events").RequireAuthorization("app-or-admin");

        var g = api.MapGroup("/rules").WithTags("Rules").RequireAuthorization("admin");
        g.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId, string? eventType) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.NotificationRules.AsNoTracking().Include(r => r.Conditions).Visible(caller.FilterFor(Permission.ViewRules), includeBroader: true);
            if (tenantId is { } t) q = q.Where(r => r.TenantId == t);
            if (!string.IsNullOrEmpty(eventType)) q = q.Where(r => r.EventType == eventType);
            return Results.Ok((await q.OrderBy(r => r.EventType).ThenBy(r => r.Order).ToListAsync(ctx.RequestAborted)).Select(Dto));
        });
        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) => Results.Ok(Dto(await LoadRule(ctx, db, id, Permission.ViewRules))));
        g.MapPost("", async (RuleRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageRules, req.TenantId, req.CompanyId, req.ApplicationId)) throw new AccessDeniedException();
            var rule = new NotificationRule { TenantId = req.TenantId, CompanyId = req.CompanyId, ApplicationId = req.ApplicationId, CreatedBy = caller.ActorId };
            Apply(rule, req);
            db.NotificationRules.Add(rule);
            audit.Record(caller, "rule.created", nameof(NotificationRule), rule.Id.ToString(), rule.Name, rule.TenantId, rule.CompanyId, rule.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/rules/{rule.Id}", Dto(rule));
        });
        g.MapPut("/{id:guid}", async (Guid id, RuleRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var rule = await LoadRule(ctx, db, id, Permission.ManageRules, tracked: true);
            db.RuleConditions.RemoveRange(rule.Conditions);
            rule.Conditions.Clear();
            Apply(rule, req);
            foreach (var c in rule.Conditions) db.RuleConditions.Add(c);
            rule.UpdatedAt = DateTime.UtcNow;
            audit.Record(caller, "rule.updated", nameof(NotificationRule), rule.Id.ToString(), rule.Name, rule.TenantId, rule.CompanyId, rule.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(Dto(rule));
        });
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var rule = await LoadRule(ctx, db, id, Permission.ManageRules, tracked: true);
            db.NotificationRules.Remove(rule);
            audit.Record(caller, "rule.deleted", nameof(NotificationRule), rule.Id.ToString(), rule.Name, rule.TenantId, rule.CompanyId, rule.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
        g.MapPost("/simulate", async (SimulateRequest req, HttpContext ctx, RuleEngine engine) =>
            Results.Ok(await engine.SimulateAsync(await ctx.CallerAsync(), req.Event, req.RuleId, ctx.RequestAborted)))
            .WithSummary("Evaluate a sample event: matched rules, recipients, rendered previews — nothing is sent");
    }

    private static void Apply(NotificationRule rule, RuleRequest req)
    {
        var errors = ConditionEvaluator.Validate(req.Conditions);
        if (string.IsNullOrWhiteSpace(req.Name)) errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(req.EventType)) errors.Add("Event type is required.");
        if (req.Actions.Count == 0) errors.Add("At least one channel action is required.");
        if (req.Actions.Any(a => string.IsNullOrWhiteSpace(a.TemplateCode))) errors.Add("Every action needs a template code.");
        if (errors.Count > 0) throw HubException.Validation("The rule is invalid.", new() { ["rule"] = errors.ToArray() });
        rule.Name = req.Name.Trim(); rule.Description = req.Description; rule.EventType = req.EventType.Trim(); rule.Enabled = req.Enabled; rule.Order = req.Order;
        rule.StopProcessing = req.StopProcessing; rule.NotificationTypeCode = req.NotificationTypeCode; rule.RecipientSelectorJson = Json.Serialize(req.Recipients);
        rule.ActionsJson = Json.Serialize(req.Actions.Select(a => { a.TemplateCode = a.TemplateCode.ToUpperInvariant(); return a; }).ToList());
        rule.Priority = req.Priority; rule.DelaySeconds = Math.Clamp(req.DelaySeconds, 0, 86400 * 30);
        rule.EscalationJson = req.Escalation is null ? null : Json.Serialize(req.Escalation);
        rule.RetryPolicyJson = req.RetryPolicy is null ? null : Json.Serialize(req.RetryPolicy);
        rule.QuietHoursJson = req.QuietHours is null ? null : Json.Serialize(req.QuietHours);
        rule.Conditions = ConditionTree.Flatten(rule.Id, req.Conditions);
    }

    private static async Task<NotificationRule> LoadRule(HttpContext ctx, NotificationHubDbContext db, Guid id, Permission p, bool tracked = false)
    {
        var caller = await ctx.CallerAsync();
        var q = tracked ? db.NotificationRules.Include(r => r.Conditions) : db.NotificationRules.AsNoTracking().Include(r => r.Conditions);
        var rule = await q.FirstOrDefaultAsync(r => r.Id == id, ctx.RequestAborted);
        if (rule is null || !caller.Can(p, rule.TenantId, rule.CompanyId, rule.ApplicationId)) throw HubException.NotFound("Rule");
        return rule;
    }

    private static object Dto(NotificationRule r) => new
    {
        r.Id, r.TenantId, r.CompanyId, r.ApplicationId, r.Name, r.Description, r.EventType, r.Enabled, r.Order, r.StopProcessing, r.NotificationTypeCode,
        conditions = ConditionTree.Build(r.Conditions), recipients = Json.Deserialize<RecipientSelector>(r.RecipientSelectorJson),
        actions = Json.Deserialize<List<RuleChannelAction>>(r.ActionsJson), r.Priority, r.DelaySeconds,
        escalation = Json.Deserialize<EscalationDto>(r.EscalationJson), retryPolicy = Json.Deserialize<RetryPolicyDto>(r.RetryPolicyJson),
        quietHours = Json.Deserialize<Core.Policies.QuietHoursPolicy>(r.QuietHoursJson), r.CreatedBy, r.CreatedAt, r.UpdatedAt,
    };
}
