using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;

namespace Wings.Samples.OrderService;

public sealed class HubSettings
{
    public string BaseUrl { get; set; } = "http://localhost:5480";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Company this service sends for (multi-company apps pass it per request instead).</summary>
    public Guid CompanyId { get; set; }
    /// <summary>Signing secret of this service's webhook subscription (shown once when the webhook was registered).</summary>
    public string? WebhookSigningSecret { get; set; }
    /// <summary>Development convenience: register a webhook subscription at startup and keep its secret in memory.</summary>
    public bool RegisterWebhookOnStartup { get; set; }
    /// <summary>How the hub reaches this service (used for webhook registration).</summary>
    public string? PublicBaseUrl { get; set; }
}

public sealed record NotifyRequest(string UserId, decimal? Amount, string? ApprovedBy, List<string>? Channels, string? IdempotencyKey);
public sealed record ApproveRequest(string RequesterUserId, decimal Amount, string ApprovedBy);
public sealed record DeviceRequest(string Platform, string Token, string? P256dh, string? Auth, string? DeviceName, string? AppVersion, string? Locale);
public sealed record ReceivedWebhook(Guid Id, string Type, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, JsonElement Data);

/// <summary>
/// DEMO ONLY: the signed-in user comes from an X-User-Id header. Replace with your real authentication
/// (cookie/JWT) — the inbox-token endpoint must only ever mint tokens for the authenticated user.
/// </summary>
public static class DemoAuth
{
    public static string? UserId(HttpContext ctx)
    {
        var id = ctx.Request.Headers["X-User-Id"].ToString().Trim();
        return id.Length is > 0 and <= 100 ? id : null;
    }
}

/// <summary>Holds the webhook signing secret and the last verified events (de-duplicated by event id / Idempotency-Key).</summary>
public sealed class WebhookInbox(IOptions<HubSettings> options)
{
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private readonly ConcurrentQueue<ReceivedWebhook> _recent = new();
    private string? _secret = string.IsNullOrWhiteSpace(options.Value.WebhookSigningSecret) ? null : options.Value.WebhookSigningSecret;

    public string? SigningSecret => _secret;

    public void UseSecret(string secret) => _secret = secret;

    public bool TryAdd(WebhookEventEnvelope evt, string? idempotencyKey)
    {
        var key = string.IsNullOrEmpty(idempotencyKey) ? evt.Id.ToString() : idempotencyKey;
        if (!_seen.TryAdd(key, 0)) return false;
        _recent.Enqueue(new ReceivedWebhook(evt.Id, evt.Type, evt.OccurredAt, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(evt.Data)));
        while (_recent.Count > 500 && _recent.TryDequeue(out var old)) _seen.TryRemove(old.Id.ToString(), out _);
        return true;
    }

    public IEnumerable<ReceivedWebhook> Recent(string? type) => _recent.Reverse().Where(e => type is null || e.Type == type).Take(200);
}

/// <summary>
/// When NotificationHub:RegisterWebhookOnStartup is true, registers {PublicBaseUrl}/webhooks/notification-hub with the hub
/// and keeps the returned signing secret in memory. Development convenience only: each start creates a new subscription.
/// In production, register once (admin console, manifest or SDK) and store the secret in your secret store.
/// </summary>
public sealed class WebhookRegistrar(NotificationHubClient hub, WebhookInbox inbox, IOptions<HubSettings> options, ILogger<WebhookRegistrar> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.RegisterWebhookOnStartup) return;
        if (string.IsNullOrWhiteSpace(o.PublicBaseUrl)) { log.LogWarning("RegisterWebhookOnStartup is set but PublicBaseUrl is empty."); return; }
        var url = $"{o.PublicBaseUrl.TrimEnd('/')}/webhooks/notification-hub";
        try
        {
            var result = await hub.RegisterWebhookAsync("order-service (sample)", url,
                [WebhookEventTypes.DeliveryStatusChanged, WebhookEventTypes.NotificationCompleted, WebhookEventTypes.InAppAction, WebhookEventTypes.PushInteraction], o.CompanyId, ct);
            inbox.UseSecret(result.GetProperty("signingSecret").GetString()!);
            log.LogInformation("Registered webhook {Url} (subscription {Id}); signing secret kept in memory.", url, result.GetProperty("id").GetGuid());
        }
        catch (NotificationHubException ex)
        {
            log.LogError("Webhook registration failed: {Code} {Message}", ex.Error?.Code, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Maps hub SDK errors to responses: caller mistakes keep their status, credential/hub problems become 502.</summary>
public sealed class HubErrorMiddleware(RequestDelegate next, ILogger<HubErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (NotificationHubException ex)
        {
            var hubStatus = (int)ex.StatusCode;
            var status = hubStatus is 400 or 404 or 409 or 422 ? hubStatus : StatusCodes.Status502BadGateway;
            log.LogWarning("Notification Hub call failed: {Status} {Code} {Message} (correlation {Correlation})", hubStatus, ex.Error?.Code, ex.Message, ex.Error?.CorrelationId);
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Error?.Code ?? "notification_hub_error", message = ex.Error?.Message ?? "Notification Hub request failed.", hubStatus, correlationId = ex.Error?.CorrelationId, errors = ex.Error?.Errors });
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Notification Hub unreachable");
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            await ctx.Response.WriteAsJsonAsync(new { error = "notification_hub_unreachable", message = ex.Message });
        }
    }
}
