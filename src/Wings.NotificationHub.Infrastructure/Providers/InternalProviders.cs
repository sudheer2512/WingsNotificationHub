using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Providers;

/// <summary>Built-in in-app provider: persists the message in the user's inbox; real-time push happens through SignalR.</summary>
public sealed class InAppProviderFactory(IServiceScopeFactory scopes) : IProviderFactory
{
    public string ProviderType => "inapp";
    public IReadOnlyList<Channel> Channels => [Channel.InApp];
    public string DisplayName => "Wings in-app inbox (built-in)";
    public IReadOnlyList<string> SecretKeys => [];
    public IReadOnlyList<string> SettingKeys => [];
    public INotificationProvider Create(ProviderAccountConfig config) => new InAppProvider(scopes);
}

public sealed class InAppProvider(IServiceScopeFactory scopes) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsIdempotency = true, SupportsReadReceipts = true, SupportsDeliveryReceipts = true };

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        // Idempotent by delivery id (unique index): a resend after an ambiguous outcome never duplicates the inbox item.
        var existing = await db.InAppMessages.AsNoTracking().Where(x => x.DeliveryId == m.DeliveryId).Select(x => x.Id).FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty) return SendResult.Accepted(existing.ToString(), "already in inbox", DeliveryStatus.Delivered);
        var d = await db.NotificationDeliveries.AsNoTracking().FirstAsync(x => x.Id == m.DeliveryId, ct);
        var msg = new InAppMessage
        {
            TenantId = d.TenantId, CompanyId = d.CompanyId, ApplicationId = d.ApplicationId, DeliveryId = d.Id, UserId = m.To,
            Title = m.Content.Title ?? m.Content.Subject ?? "", Body = m.Content.Body, Category = m.Content.App?.Category ?? d.Category,
            Priority = m.Priority, DeepLink = m.Content.App?.DeepLink, ImageUrl = m.Content.App?.ImageUrl,
            ActionsJson = m.Content.App?.Actions is { Count: > 0 } a ? Json.Serialize(a) : null, ExpiresAt = m.ExpiresAt,
        };
        db.InAppMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        return SendResult.Accepted(msg.Id.ToString(), "stored in inbox", DeliveryStatus.Delivered);
    }

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct) => Task.FromResult(ProviderHealth.Healthy("Built-in."));
}

/// <summary>Built-in webhook channel: POSTs the rendered notification to the application's registered endpoint, signed.</summary>
public sealed class WebhookChannelProviderFactory(IServiceScopeFactory scopes, IHttpClientFactory http, ISecretProtector protector, UrlGuard guard) : IProviderFactory
{
    public string ProviderType => "webhook";
    public IReadOnlyList<Channel> Channels => [Channel.Webhook];
    public string DisplayName => "Outbound webhook (built-in)";
    public IReadOnlyList<string> SecretKeys => [];
    public IReadOnlyList<string> SettingKeys => [];
    public INotificationProvider Create(ProviderAccountConfig config) => new WebhookChannelProvider(scopes, http, protector, guard);
}

public sealed class WebhookChannelProvider(IServiceScopeFactory scopes, IHttpClientFactory http, ISecretProtector protector, UrlGuard guard) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsIdempotency = true };

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        WebhookSubscription? sub;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
            sub = Guid.TryParse(m.To, out var id) ? await db.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.TenantId == m.TenantId, ct) : null;
        }
        if (sub is null || !sub.Enabled) return SendResult.Permanent("webhook_missing", "Webhook subscription is missing or disabled.", invalidAddress: true);
        var body = new WebhookEventEnvelope
        {
            Id = m.DeliveryId, Type = WebhookEventTypes.NotificationWebhook, OccurredAt = DateTimeOffset.UtcNow, TenantId = sub.TenantId,
            CompanyId = sub.CompanyId, ApplicationId = sub.ApplicationId, Data = m.Content.WebhookPayload ?? new(),
        };
        var json = Json.Serialize(body);
        return await WebhookPoster.PostAsync(http, guard, sub.Url, protector.Unprotect(sub.SecretCipher, Purposes.WebhookSecret), json, m.IdempotencyKey, ct) switch
        {
            { Success: true } r => SendResult.Accepted(m.DeliveryId.ToString(), $"HTTP {r.Status}", DeliveryStatus.Delivered),
            { Status: 429 } r => SendResult.Throttled(r.Error ?? "429"),
            { Status: >= 400 and < 500 and not 408 } r => SendResult.Permanent($"webhook_{r.Status}", r.Error ?? "", responseCode: r.Status.ToString()),
            { Status: null, Error: { } e } when e.StartsWith("blocked") => SendResult.Permanent("webhook_blocked", e),
            { Status: null } r => SendResult.Transient("webhook_unreachable", r.Error ?? "unreachable"),
            var r => SendResult.Transient($"webhook_{r.Status}", r.Error ?? ""),
        };
    }

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct) => Task.FromResult(ProviderHealth.Healthy("Built-in."));
}

public sealed record WebhookPostResult(bool Success, int? Status, string? Error, int DurationMs);

public static class WebhookPoster
{
    /// <summary>Signed POST with replay-protected signature, idempotency header and SSRF guard.</summary>
    public static async Task<WebhookPostResult> PostAsync(IHttpClientFactory http, UrlGuard guard, string url, string secret, string json, string idempotencyKey, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (await guard.CheckAsync(url, ct) is { } blocked) return new(false, null, "blocked: " + blocked, 0);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(secret, json, DateTimeOffset.UtcNow));
            req.Headers.Add("Idempotency-Key", idempotencyKey);
            req.Headers.UserAgent.ParseAdd("WingsNotificationHub/1.0");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var resp = await http.CreateClient("webhooks").SendAsync(req, cts.Token);
            var code = (int)resp.StatusCode;
            var error = resp.IsSuccessStatusCode ? null : Redact.Sanitize(await resp.Content.ReadAsStringAsync(ct), 300);
            return new(resp.IsSuccessStatusCode, code, error, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, null, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }
}

/// <summary>
/// SSRF protection for outbound webhooks: https only (http allowed when configured for development) and no
/// loopback/private/link-local destinations unless <c>Webhooks:AllowPrivateNetworks</c> is set.
/// </summary>
public sealed class UrlGuard(IConfiguration config)
{
    private bool AllowHttp => config.GetValue("Webhooks:AllowHttp", false);
    private bool AllowPrivate => config.GetValue("Webhooks:AllowPrivateNetworks", false);

    public string? Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "invalid URL";
        if (u.Scheme != Uri.UriSchemeHttps && !(AllowHttp && u.Scheme == Uri.UriSchemeHttp)) return "only https URLs are permitted";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "credentials in URLs are not permitted";
        return null;
    }

    public async Task<string?> CheckAsync(string url, CancellationToken ct)
    {
        if (Validate(url) is { } err) return err;
        if (AllowPrivate) return null;
        var host = new Uri(url).Host;
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct); }
        catch (SocketException) { return null; } // unresolvable: the request fails normally
        return addresses.Any(IsPrivate) ? "destination resolves to a private or loopback address" : null;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0;
    }
}
