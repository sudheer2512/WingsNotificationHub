using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

/// <summary>
/// Model Context Protocol endpoint (JSON-RPC 2.0 over streamable HTTP, single-response mode) for Wings MCP Server and
/// Wings AI agents. Agents authenticate with a client credential holding the <c>mcp</c> scope; they never see provider
/// credentials. AI restrictions (registered types only, known recipients only, no bulk, human approval for configured
/// high-risk types) are enforced by the notification service, not by the agent.
/// </summary>
public static class McpEndpoints
{
    private const string ProtocolVersion = "2025-06-18";

    private sealed record Tool(string Name, string Description, string RequiredScope, JsonObject InputSchema, Func<McpCall, Task<object>> Handler);
    private sealed record McpCall(CallerContext Caller, JsonObject Args, IServiceProvider Services, CancellationToken Ct);

    private static JsonObject Schema(JsonObject properties, params string[] required)
        => new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()), ["additionalProperties"] = false };

    private static JsonObject Str(string description, params string[] enumValues)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (enumValues.Length > 0) o["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)e).ToArray());
        return o;
    }

    private static readonly JsonObject RecipientSchema = new()
    {
        ["type"] = "object", ["description"] = "A registered contact. AI agents may not supply raw email addresses or phone numbers.",
        ["properties"] = new JsonObject { ["contactId"] = Str("Contact GUID"), ["userId"] = Str("Application user id"), ["customerId"] = Str("Customer id") },
        ["additionalProperties"] = false,
    };

    private static JsonObject SendSchema(bool requireSchedule) => Schema(new JsonObject
    {
        ["companyId"] = Str("Company GUID"),
        ["notificationType"] = Str("Registered notification type code, e.g. INVOICE_CREATED"),
        ["templateCode"] = Str("Optional template code (defaults to the type's template)"),
        ["recipient"] = RecipientSchema.DeepClone(),
        ["channels"] = new JsonObject { ["type"] = "array", ["items"] = Str("Channel", "email", "sms", "whatsapp", "inapp", "push", "webpush") },
        ["variables"] = new JsonObject { ["type"] = "object", ["description"] = "Template variables", ["additionalProperties"] = true },
        ["priority"] = Str("Priority", "Low", "Normal", "High"),
        ["scheduleAt"] = Str("ISO-8601 time to deliver"),
        ["idempotencyKey"] = Str("Retry-safe key; reuse it when retrying the same request"),
    }, requireSchedule ? ["companyId", "notificationType", "recipient", "scheduleAt"] : ["companyId", "notificationType", "recipient"]);

    private static readonly Tool[] Tools =
    [
        new("notification.send", "Request delivery of a registered notification type to one known recipient. Returns a tracking id; acceptance is not delivery. High-risk types may return status pending_approval until a human approves.",
            Scopes.NotificationsSend, SendSchema(false), c => SendAsync(c, schedule: false)),
        new("notification.schedule", "Schedule a registered notification for a future time.", Scopes.NotificationsSend, SendSchema(true), c => SendAsync(c, schedule: true)),
        new("notification.get_status", "Get aggregate and per-channel status of a notification.", Scopes.NotificationsRead,
            Schema(new JsonObject { ["notificationId"] = Str("Notification GUID") }, "notificationId"), GetStatusAsync),
        new("notification.get_history", "Get the delivery timeline (attempts and provider events) of a notification.", Scopes.NotificationsRead,
            Schema(new JsonObject { ["notificationId"] = Str("Notification GUID") }, "notificationId"), GetHistoryAsync),
        new("notification.list_templates", "List active templates available to this application.", Scopes.TemplatesRead,
            Schema(new JsonObject { ["channel"] = Str("Filter by channel", "email", "sms", "whatsapp", "inapp", "push", "webpush", "webhook"), ["code"] = Str("Filter by template code") }), ListTemplatesAsync),
        new("notification.preview_template", "Render a template with variables (no message is sent).", Scopes.TemplatesRead,
            Schema(new JsonObject { ["templateCode"] = Str("Template code"), ["channel"] = Str("Channel", "email", "sms", "whatsapp", "inapp", "push", "webpush"), ["companyId"] = Str("Company GUID"),
                ["variables"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true } }, "templateCode", "channel"), PreviewAsync),
        new("notification.get_preferences", "Get a recipient's communication preferences and consent state.", Scopes.PreferencesRead,
            Schema(new JsonObject { ["companyId"] = Str("Company GUID"), ["externalId"] = Str("User or customer id") }, "companyId", "externalId"), PreferencesAsync),
        new("notification.register_device", "Register a push device token for a user.", Scopes.DevicesWrite,
            Schema(new JsonObject { ["companyId"] = Str("Company GUID"), ["userId"] = Str("User id"), ["platform"] = Str("Platform", "Android", "Ios", "Web"), ["token"] = Str("FCM/APNs token or Web Push endpoint"),
                ["p256dh"] = Str("Web Push key"), ["auth"] = Str("Web Push auth secret"), ["deviceName"] = Str("Friendly name") }, "companyId", "userId", "platform", "token"), RegisterDeviceAsync),
        new("notification.get_usage", "Communication usage and estimated cost for this application.", Scopes.UsageRead,
            Schema(new JsonObject { ["from"] = Str("ISO date"), ["to"] = Str("ISO date") }), UsageAsync),
        new("notification.get_provider_health", "Health of the communication providers available to this application (no credentials are exposed).", Scopes.ProvidersHealth,
            Schema(new JsonObject()), ProviderHealthAsync),
    ];

    public static void Map(WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Results.Json(Error(null, -32700, "Parse error")); }
            var caller = await ctx.CallerAsync();
            if (caller.Kind == CallerKind.Application && !caller.HasScope(Scopes.Mcp))
                return Results.Json(Error(body?["id"], -32001, "The credential lacks the 'mcp' scope."), statusCode: 403);
            if (body is JsonArray batch)
            {
                var responses = new JsonArray();
                foreach (var item in batch) if (await HandleAsync(item as JsonObject, caller, ctx) is { } r) responses.Add(r);
                return responses.Count == 0 ? Results.Accepted() : Results.Json(responses);
            }
            var single = await HandleAsync(body as JsonObject, caller, ctx);
            return single is null ? Results.Accepted() : Results.Json(single);
        }).RequireAuthorization("app-or-admin").RequireRateLimiting(RateLimiting.Api).WithTags("MCP").WithSummary("Model Context Protocol (JSON-RPC 2.0) for Wings AI agents");

        app.MapGet("/mcp", () => Results.StatusCode(405)).AllowAnonymous().ExcludeFromDescription();
    }

    private static async Task<JsonObject?> HandleAsync(JsonObject? req, CallerContext caller, HttpContext ctx)
    {
        if (req is null) return Error(null, -32600, "Invalid request");
        var id = req["id"]?.DeepClone();
        var method = req["method"]?.GetValue<string>();
        if (id is null) return null; // notifications (e.g. notifications/initialized) need no response
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "wings-notification-hub", ["title"] = "Wings Notification Hub", ["version"] = "1.0.0" },
                    ["instructions"] = "Use notification.send to request notifications for registered contacts. Never ask users for provider credentials. Acceptance is not delivery; check notification.get_status.",
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                var visible = Tools.Where(t => caller.HasScope(t.RequiredScope));
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(visible.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description + $" Requires scope '{t.RequiredScope}'.", ["inputSchema"] = t.InputSchema.DeepClone(),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = !t.Name.EndsWith("send") && !t.Name.EndsWith("schedule") && !t.Name.EndsWith("register_device"), ["openWorldHint"] = t.Name is "notification.send" or "notification.schedule" },
                    }).ToArray()),
                });
            case "tools/call":
                var name = req["params"]?["name"]?.GetValue<string>();
                var tool = Tools.FirstOrDefault(t => t.Name == name);
                if (tool is null) return Error(id, -32602, $"Unknown tool '{name}'.");
                if (!caller.HasScope(tool.RequiredScope)) return ToolError(id, $"Permission denied: scope '{tool.RequiredScope}' is required.");
                var args = req["params"]?["arguments"] as JsonObject ?? new JsonObject();
                if (ValidateArgs(tool.InputSchema, args) is { } invalid) return ToolError(id, invalid);
                try
                {
                    var output = await tool.Handler(new McpCall(caller, args, ctx.RequestServices, ctx.RequestAborted));
                    var json = JsonSerializer.SerializeToNode(output, Json.Options)!;
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = json.ToJsonString() }),
                        ["structuredContent"] = json is JsonObject ? json : new JsonObject { ["result"] = json },
                        ["isError"] = false,
                    });
                }
                catch (HubException ex) { return ToolError(id, $"{ex.Code}: {ex.Message}" + (ex.Errors is null ? "" : " " + string.Join("; ", ex.Errors.SelectMany(e => e.Value)))); }
                catch (AccessDeniedException ex) { return ToolError(id, "Permission denied: " + ex.Message); }
            default:
                return Error(id, -32601, $"Method '{method}' not found.");
        }
    }

    /// <summary>Minimal JSON-schema validation: required fields, unknown fields, string enums.</summary>
    private static string? ValidateArgs(JsonObject schema, JsonObject args)
    {
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        foreach (var r in schema["required"]?.AsArray() ?? [])
            if (args[r!.GetValue<string>()] is null) return $"Missing required argument '{r}'.";
        foreach (var (k, v) in args)
        {
            if (!props.ContainsKey(k)) return $"Unknown argument '{k}'.";
            if (props[k]?["enum"] is JsonArray e && v is JsonValue jv && jv.TryGetValue<string>(out var s) && !e.Any(x => x!.GetValue<string>().Equals(s, StringComparison.OrdinalIgnoreCase)))
                return $"Argument '{k}' must be one of: {string.Join(", ", e)}.";
            if (props[k]?["additionalProperties"] is JsonValue ap && ap.GetValue<bool>() == false && v is JsonObject nested && props[k]?["properties"] is JsonObject np)
                foreach (var (nk, _) in nested) if (!np.ContainsKey(nk)) return $"Unknown field '{k}.{nk}'.";
        }
        return null;
    }

    private static Guid G(JsonObject a, string k) => Guid.TryParse(a[k]?.GetValue<string>(), out var g) ? g : throw HubException.Validation($"'{k}' must be a GUID.");

    private static async Task<object> SendAsync(McpCall c, bool schedule)
    {
        var a = c.Args;
        var req = new SendNotificationRequest
        {
            CompanyId = G(a, "companyId"), NotificationType = a["notificationType"]!.GetValue<string>(), TemplateCode = a["templateCode"]?.GetValue<string>(),
            Recipient = a["recipient"]!.Deserialize<RecipientDto>(Json.Options),
            Channels = a["channels"]?.AsArray().Select(x => ChannelNames.TryParse(x!.GetValue<string>(), out var ch) ? ch : throw HubException.Validation($"Unknown channel {x}")).ToList(),
            Variables = a["variables"]?.Deserialize<Dictionary<string, JsonElement>>(),
            Priority = a["priority"] is { } p ? Enum.Parse<NotificationPriority>(p.GetValue<string>(), true) : null,
            ScheduleAt = a["scheduleAt"] is { } s ? DateTimeOffset.Parse(s.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null,
            IdempotencyKey = a["idempotencyKey"]?.GetValue<string>(), CorrelationId = c.Caller.CorrelationId,
        };
        if (schedule && req.ScheduleAt is null) throw HubException.Validation("scheduleAt is required.");
        var svc = c.Services.GetRequiredService<NotificationService>();
        var r = await svc.AcceptAsync(c.Caller, req, RequestSource.Mcp, null, c.Ct);
        return new { notificationId = r.NotificationId, status = r.Status, duplicate = r.Duplicate, deliveries = r.Deliveries.Select(d => new { channel = ChannelNames.ToWire(d.Channel), d.Recipient, status = d.Status.ToString(), d.Reason }), r.Warnings };
    }

    private static async Task<NotificationRequest> LoadRequest(McpCall c)
    {
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var id = G(c.Args, "notificationId");
        var r = await db.NotificationRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, c.Ct);
        if (r is null || !c.Caller.Can(Permission.ViewNotifications, r.TenantId, r.CompanyId, r.ApplicationId) || (c.Caller.Kind == CallerKind.Application && c.Caller.ApplicationId != r.ApplicationId))
            throw HubException.NotFound("Notification");
        return r;
    }

    private static async Task<object> GetStatusAsync(McpCall c)
    {
        var r = await LoadRequest(c);
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var ds = await db.NotificationDeliveries.AsNoTracking().Where(d => d.RequestId == r.Id).ToListAsync(c.Ct);
        return new
        {
            notificationId = r.Id, status = r.Status.ToString(), r.NotificationTypeCode, r.CreatedAt, r.CompletedAt,
            deliveries = ds.Select(d => new { channel = ChannelNames.ToWire(d.Channel), recipient = Addressing.Mask(d.Channel, d.Address), status = d.Status.ToString(), d.AttemptCount, d.FailureCode, d.DeliveredAt, d.ReadAt }),
        };
    }

    private static async Task<object> GetHistoryAsync(McpCall c)
    {
        var r = await LoadRequest(c);
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var ids = await db.NotificationDeliveries.AsNoTracking().Where(d => d.RequestId == r.Id).Select(d => d.Id).ToListAsync(c.Ct);
        var events = await db.DeliveryEvents.AsNoTracking().Where(e => e.DeliveryId != null && ids.Contains(e.DeliveryId.Value)).OrderBy(e => e.RecordedAt).ToListAsync(c.Ct);
        var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => ids.Contains(a.DeliveryId)).OrderBy(a => a.StartedAt).ToListAsync(c.Ct);
        return new
        {
            notificationId = r.Id,
            attempts = attempts.Select(a => new { a.DeliveryId, a.AttemptNumber, outcome = a.Outcome?.ToString(), a.ErrorCode, a.StartedAt, a.DurationMs }),
            events = events.Select(e => new { e.DeliveryId, e.Type, status = e.Status?.ToString(), e.Source, e.Detail, e.OccurredAt }),
        };
    }

    private static async Task<object> ListTemplatesAsync(McpCall c)
    {
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var q = db.NotificationTemplates.AsNoTracking().Where(t => t.ActiveVersionId != null).Visible(c.Caller.FilterFor(Permission.ViewTemplates), includeBroader: true);
        if (c.Args["channel"]?.GetValue<string>() is { } ch && ChannelNames.TryParse(ch, out var channel)) q = q.Where(t => t.Channel == channel);
        if (c.Args["code"]?.GetValue<string>() is { } code) q = q.Where(t => t.Code == code.ToUpper());
        var rows = await q.OrderBy(t => t.Code).Take(200).ToListAsync(c.Ct);
        var versionIds = rows.Select(r => r.ActiveVersionId!.Value).ToList();
        var vars = await db.TemplateVariables.AsNoTracking().Where(v => versionIds.Contains(v.TemplateVersionId)).ToListAsync(c.Ct);
        return new { templates = rows.Select(t => new { t.Code, t.Name, channel = ChannelNames.ToWire(t.Channel), t.Locale, t.Category, variables = vars.Where(v => v.TemplateVersionId == t.ActiveVersionId).Select(v => new { v.Name, v.Required }) }) };
    }

    private static async Task<object> PreviewAsync(McpCall c)
    {
        ChannelNames.TryParse(c.Args["channel"]!.GetValue<string>(), out var ch);
        var svc = c.Services.GetRequiredService<TemplateService>();
        var r = await svc.PreviewAsync(c.Caller, new TemplatePreviewRequest
        {
            TemplateCode = c.Args["templateCode"]!.GetValue<string>().ToUpperInvariant(), Channel = ch, CompanyId = c.Args["companyId"] is null ? null : G(c.Args, "companyId"),
            Variables = c.Args["variables"]?.Deserialize<Dictionary<string, JsonElement>>(),
        }, c.Ct);
        return r;
    }

    private static async Task<object> PreferencesAsync(McpCall c)
    {
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var companyId = G(c.Args, "companyId");
        var ext = c.Args["externalId"]!.GetValue<string>();
        await c.Services.GetRequiredService<TenantContextResolver>().ResolveAsync(c.Caller, null, companyId, Permission.ViewContacts, Scopes.PreferencesRead, c.Ct, allowSuspended: true);
        var contact = await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.ExternalId == ext && x.ErasedAt == null, c.Ct) ?? throw HubException.NotFound("Contact");
        var prefs = await db.RecipientPreferences.AsNoTracking().Where(p => p.ContactId == contact.Id).ToListAsync(c.Ct);
        var consents = (await db.RecipientConsents.AsNoTracking().Where(x => x.ContactId == contact.Id).ToListAsync(c.Ct)).GroupBy(x => (x.Channel, x.Purpose)).Select(g => g.OrderByDescending(x => x.CapturedAt).First());
        return new
        {
            contactId = contact.Id, preferences = prefs.Select(p => new { p.Category, channel = ChannelNames.ToWire(p.Channel), p.Enabled }),
            consents = consents.Select(x => new { channel = ChannelNames.ToWire(x.Channel), purpose = x.Purpose.ToString(), status = x.Status.ToString(), x.CapturedAt }),
        };
    }

    private static async Task<object> RegisterDeviceAsync(McpCall c)
    {
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var tc = await c.Services.GetRequiredService<TenantContextResolver>().ResolveAsync(c.Caller, null, G(c.Args, "companyId"), Permission.ManageContacts, Scopes.DevicesWrite, c.Ct);
        var token = c.Args["token"]!.GetValue<string>();
        var platform = Enum.Parse<DevicePlatform>(c.Args["platform"]!.GetValue<string>(), true);
        var hash = Addressing.Sha256Hex(token);
        var d = await db.DeviceRegistrations.FirstOrDefaultAsync(x => x.ApplicationId == tc.Application.Id && x.TokenHash == hash, c.Ct);
        if (d is null) db.DeviceRegistrations.Add(d = new DeviceRegistration { TenantId = tc.Tenant.Id, ApplicationId = tc.Application.Id, Token = token, TokenHash = hash });
        d.CompanyId = tc.Company.Id; d.UserId = c.Args["userId"]!.GetValue<string>(); d.Platform = platform;
        d.Provider = platform switch { DevicePlatform.Ios => "apns", DevicePlatform.Web => "webpush", _ => "fcm" };
        d.P256dh = c.Args["p256dh"]?.GetValue<string>(); d.Auth = c.Args["auth"]?.GetValue<string>(); d.DeviceName = c.Args["deviceName"]?.GetValue<string>();
        d.LastSeenAt = DateTime.UtcNow; d.InvalidatedAt = null;
        await db.SaveChangesAsync(c.Ct);
        return new { deviceId = d.Id, provider = d.Provider };
    }

    private static async Task<object> UsageAsync(McpCall c)
    {
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var from = c.Args["from"] is { } f ? DateTime.Parse(f.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime() : Periods.StartOf(DateTime.UtcNow);
        var to = c.Args["to"] is { } t ? DateTime.Parse(t.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime() : DateTime.UtcNow.AddDays(1);
        var rows = await db.UsageRecords.AsNoTracking().Visible(c.Caller.FilterFor(Permission.ViewUsage)).Where(u => u.OccurredAt >= from && u.OccurredAt < to).ToListAsync(c.Ct);
        return new
        {
            from, to,
            byChannel = rows.GroupBy(u => u.Channel).Select(g => new { channel = ChannelNames.ToWire(g.Key), messages = g.Count(), units = g.Sum(u => u.Units), estimatedCost = Math.Round(g.Sum(u => u.EstimatedCost), 2), billed = Math.Round(g.Sum(u => u.BilledAmount), 2) }),
        };
    }

    private static async Task<object> ProviderHealthAsync(McpCall c)
    {
        var db = c.Services.GetRequiredService<NotificationHubDbContext>();
        var rows = await ProviderEndpoints.VisibleAccounts(db, c.Caller, Permission.ViewProviders).Where(a => a.Enabled).ToListAsync(c.Ct);
        return new { providers = rows.Select(a => new { a.Name, channel = ChannelNames.ToWire(a.Channel), health = a.HealthStatus.ToString(), a.Suspended, a.LastHealthCheckAt }) };
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    private static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static JsonObject ToolError(JsonNode? id, string message) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }), ["isError"] = true,
    });
}
