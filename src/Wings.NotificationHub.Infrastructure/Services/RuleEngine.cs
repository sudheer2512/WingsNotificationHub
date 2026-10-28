using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;

namespace Wings.NotificationHub.Infrastructure.Services;

public sealed class RuleSimulationResult
{
    public List<RuleMatchResult> Rules { get; set; } = [];
    public List<SendNotificationResponse> Notifications { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed class RuleMatchResult
{
    public Guid RuleId { get; set; }
    public string Name { get; set; } = "";
    public bool Matched { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Evaluates business events against notification rules and issues notification requests.</summary>
public sealed class RuleEngine(
    NotificationHubDbContext db,
    TenantContextResolver contexts,
    NotificationService notifications,
    ILogger<RuleEngine> logger) : IMessageHandler
{
    public string Type => MessageTypes.ProcessEvent;

    /// <summary>POST /events: durably records the event and queues it for evaluation.</summary>
    public async Task<BusinessEventResponse> PublishAsync(CallerContext caller, BusinessEventRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EventType) || req.EventType.Length > 200) throw HubException.Validation("eventType is required (max 200 characters).");
        var ctx = await contexts.ResolveAsync(caller, req.ApplicationId, req.CompanyId, Permission.SendNotifications, Scopes.EventsPublish, ct);
        var payload = req.Payload.ToJsonString();
        if (payload.Length > 256 * 1024) throw HubException.Validation("Event payload exceeds 256 KB.");
        if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            var existing = await db.BusinessEvents.AsNoTracking().FirstOrDefaultAsync(e => e.ApplicationId == ctx.Application.Id && e.IdempotencyKey == req.IdempotencyKey, ct);
            if (existing is not null) return new BusinessEventResponse { EventId = existing.Id, Duplicate = true };
        }
        var ev = new BusinessEvent
        {
            TenantId = ctx.Tenant.Id, CompanyId = ctx.Company.Id, ApplicationId = ctx.Application.Id, EventType = req.EventType.Trim(),
            PayloadJson = payload, IdempotencyKey = req.IdempotencyKey, CorrelationId = req.CorrelationId ?? caller.CorrelationId,
            ActorId = caller.ActorId, OccurredAt = req.OccurredAt?.UtcDateTime ?? DateTime.UtcNow,
        };
        db.BusinessEvents.Add(ev);
        Outbox.Add(db, MessageTypes.ProcessEvent, new ProcessEventMessage(ev.Id), ev.TenantId, correlationId: ev.CorrelationId);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (req.IdempotencyKey is not null)
        {
            db.ChangeTracker.Clear();
            var winner = await db.BusinessEvents.AsNoTracking().FirstAsync(e => e.ApplicationId == ctx.Application.Id && e.IdempotencyKey == req.IdempotencyKey, ct);
            return new BusinessEventResponse { EventId = winner.Id, Duplicate = true };
        }
        return new BusinessEventResponse { EventId = ev.Id };
    }

    public async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var ev = await db.BusinessEvents.FirstOrDefaultAsync(e => e.Id == message.Payload<ProcessEventMessage>().EventId, ct);
        if (ev is null || ev.ProcessedAt is not null) return;
        var result = await EvaluateAsync(ev, simulate: false, ct);
        ev.ProcessedAt = DateTime.UtcNow;
        ev.MatchedRules = result.Rules.Count(r => r.Matched);
        ev.NotificationsCreated = result.Notifications.Count;
        ev.ProcessingError = result.Errors.Count > 0 ? string.Join("; ", result.Errors)[..Math.Min(3900, string.Join("; ", result.Errors).Length)] : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Simulation: evaluates a sample event and renders previews without persisting anything.</summary>
    public async Task<RuleSimulationResult> SimulateAsync(CallerContext caller, BusinessEventRequest req, Guid? onlyRuleId, CancellationToken ct)
    {
        var ctx = await contexts.ResolveAsync(caller, req.ApplicationId, req.CompanyId, Permission.ViewRules, null, ct, allowSuspended: true);
        var ev = new BusinessEvent
        {
            TenantId = ctx.Tenant.Id, CompanyId = ctx.Company.Id, ApplicationId = ctx.Application.Id, EventType = req.EventType,
            PayloadJson = req.Payload.ToJsonString(), ActorId = caller.ActorId,
        };
        return await EvaluateAsync(ev, simulate: true, ct, onlyRuleId, caller);
    }

    private async Task<RuleSimulationResult> EvaluateAsync(BusinessEvent ev, bool simulate, CancellationToken ct, Guid? onlyRuleId = null, CallerContext? simulator = null)
    {
        var result = new RuleSimulationResult();
        var rules = await db.NotificationRules.AsNoTracking().Include(r => r.Conditions)
            .Where(r => r.TenantId == ev.TenantId && r.EventType == ev.EventType && (r.Enabled || r.Id == onlyRuleId)
                        && (r.CompanyId == null || r.CompanyId == ev.CompanyId) && (r.ApplicationId == null || r.ApplicationId == ev.ApplicationId))
            .OrderBy(r => r.Order).ToListAsync(ct);
        if (onlyRuleId is { } only) rules = rules.Where(r => r.Id == only).ToList();
        var payload = JsonNode.Parse(ev.PayloadJson) as JsonObject ?? new JsonObject();
        var doc = new JsonObject { ["eventType"] = ev.EventType, ["payload"] = payload.DeepClone() };

        // Rules run as the system on the event's tenant/application (the publisher or simulator was authorized already).
        var caller = new CallerContext
        {
            Kind = CallerKind.System, ActorId = $"rule-engine", ActorName = "Rule engine",
            Grants = [new ScopeGrant(Roles.PlatformAdmin, null, null, null, null)],
        };

        foreach (var rule in rules)
        {
            var tree = ConditionTree.Build(rule.Conditions);
            var matched = ConditionEvaluator.Evaluate(tree, doc);
            result.Rules.Add(new RuleMatchResult { RuleId = rule.Id, Name = rule.Name, Matched = matched, Reason = matched ? "conditions satisfied" : "conditions not satisfied" });
            if (!matched) continue;
            try
            {
                foreach (var req in BuildRequests(rule, ev, payload, simulate))
                {
                    var resp = await notifications.AcceptAsync(caller, req, RequestSource.Event, new AcceptOptions
                    {
                        BusinessEventId = simulate ? null : ev.Id, RuleId = rule.Id, DelaySeconds = rule.DelaySeconds,
                        QuietHours = Json.Deserialize<QuietHoursPolicy>(rule.QuietHoursJson),
                    }, ct);
                    result.Notifications.Add(resp);
                }
            }
            catch (HubException ex)
            {
                var detail = ex.Errors is null ? ex.Message : ex.Message + " " + string.Join("; ", ex.Errors.SelectMany(e => e.Value));
                result.Errors.Add($"Rule '{rule.Name}': {detail}");
                logger.LogWarning("Rule {Rule} for event {Event} failed: {Error}", rule.Id, ev.Id, detail);
            }
            if (rule.StopProcessing) break;
        }
        return result;
    }

    private static IEnumerable<SendNotificationRequest> BuildRequests(NotificationRule rule, BusinessEvent ev, JsonObject payload, bool simulate)
    {
        var actions = Json.Deserialize<List<RuleChannelAction>>(rule.ActionsJson) ?? [];
        var selector = Json.Deserialize<RecipientSelector>(rule.RecipientSelectorJson) ?? new RecipientSelector();
        var (recipients, audience) = Recipients(selector, payload);
        var variables = ConditionEvaluator.ToVariables(payload);
        // One request per template code so each channel renders its own template.
        foreach (var group in actions.GroupBy(a => a.TemplateCode))
        {
            yield return new SendNotificationRequest
            {
                ApplicationId = ev.ApplicationId, CompanyId = ev.CompanyId, NotificationType = rule.NotificationTypeCode, TemplateCode = group.Key,
                Recipients = recipients, Audience = audience, Channels = group.Select(a => a.Channel).ToList(), Variables = variables,
                Priority = rule.Priority, CorrelationId = ev.CorrelationId, DryRun = simulate,
                IdempotencyKey = simulate ? null : $"evt:{ev.Id:N}:rule:{rule.Id:N}:{group.Key}",
                Fallbacks = group.Where(a => a.FallbackChannel is not null)
                    .Select(a => new FallbackDto { From = a.Channel, To = a.FallbackChannel!.Value, TemplateCode = a.FallbackTemplateCode }).ToList(),
                Escalation = Json.Deserialize<EscalationDto>(rule.EscalationJson),
                RetryPolicy = Json.Deserialize<RetryPolicyDto>(rule.RetryPolicyJson),
                Metadata = new Dictionary<string, string> { ["eventType"] = ev.EventType, ["rule"] = rule.Name },
            };
        }
    }

    private static (List<RecipientDto>? Recipients, AudienceDto? Audience) Recipients(RecipientSelector s, JsonObject payload)
    {
        switch (s.Type.ToLowerInvariant())
        {
            case "users": return (null, new AudienceDto { UserIds = s.UserIds });
            case "roles": return (null, new AudienceDto { Roles = s.Roles });
            case "group": return (null, new AudienceDto { GroupIds = s.GroupId is { } g ? [g] : [] });
            case "company": return (null, new AudienceDto { AllCompanyUsers = true });
            default:
                var node = string.IsNullOrEmpty(s.Path) ? payload : ConditionEvaluator.Select(payload, s.Path);
                var list = new List<RecipientDto>();
                if (node is JsonArray arr) foreach (var n in arr) { if (n is JsonObject o) list.Add(FromJson(o)); }
                else if (node is JsonObject obj) list.Add(FromJson(obj));
                return (list, null);
        }

        static RecipientDto FromJson(JsonObject o)
        {
            string? S(params string[] keys) => keys.Select(k => o.FirstOrDefault(kv => string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)).Value)
                .Where(v => v is not null).Select(v => ConditionEvaluator.AsString(v)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            return new RecipientDto
            {
                UserId = S("userId"), CustomerId = S("customerId", "id"), Name = S("name", "customerName"), Email = S("email"),
                Mobile = S("mobile", "phone"), WhatsApp = S("whatsapp"), Locale = S("locale"), TimeZone = S("timeZone"),
            };
        }
    }
}

/// <summary>Converts between the API's condition tree and normalized <see cref="RuleCondition"/> rows.</summary>
public static class ConditionTree
{
    public static ConditionNode? Build(IReadOnlyList<RuleCondition> rows)
    {
        var root = rows.Where(r => r.ParentId is null).OrderBy(r => r.Order).ToList();
        if (root.Count == 0) return null;
        if (root.Count == 1) return Node(root[0], rows);
        return new ConditionNode { Op = "and", Children = root.Select(r => Node(r, rows)).ToList() };
    }

    private static ConditionNode Node(RuleCondition r, IReadOnlyList<RuleCondition> all)
        => r.GroupOperator is not null
            ? new ConditionNode { Op = r.GroupOperator, Children = all.Where(c => c.ParentId == r.Id).OrderBy(c => c.Order).Select(c => Node(c, all)).ToList() }
            : new ConditionNode { Field = r.Field, Comparison = r.Comparison, Value = r.ValueJson is null ? null : JsonNode.Parse(r.ValueJson) };

    public static List<RuleCondition> Flatten(Guid ruleId, ConditionNode? node)
    {
        var list = new List<RuleCondition>();
        if (node is null) return list;
        Add(node, null, 0);
        return list;

        void Add(ConditionNode n, Guid? parent, int order)
        {
            var row = new RuleCondition
            {
                RuleId = ruleId, ParentId = parent, Order = order, GroupOperator = n.IsGroup ? n.Op!.ToLowerInvariant() : null,
                Field = n.IsGroup ? null : n.Field, Comparison = n.IsGroup ? null : (n.Comparison ?? "eq").ToLowerInvariant(),
                ValueJson = n.IsGroup || n.Value is null ? null : n.Value.ToJsonString(),
            };
            list.Add(row);
            if (n.IsGroup) for (var i = 0; i < (n.Children?.Count ?? 0); i++) Add(n.Children![i], row.Id, i);
        }
    }
}
