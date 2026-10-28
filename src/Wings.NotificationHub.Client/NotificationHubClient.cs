using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Client;

public sealed class NotificationHubClientOptions
{
    /// <summary>Base URL of the hub, e.g. https://notifications.wings.example.</summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5480");
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Optional subset of the credential's scopes.</summary>
    public string? Scope { get; set; }
    /// <summary>Default company for calls that do not specify one.</summary>
    public Guid? DefaultCompanyId { get; set; }
    /// <summary>Retries for 429/502/503/504 and network errors. Sends are safe to retry because every send carries an idempotency key.</summary>
    public int MaxRetries { get; set; } = 3;
}

public sealed class NotificationHubException(HttpStatusCode status, ApiError? error, string body)
    : Exception(error is null ? $"Notification Hub returned {(int)status}: {body}" : $"{error.Code}: {error.Message}")
{
    public HttpStatusCode StatusCode { get; } = status;
    public ApiError? Error { get; } = error;
}

/// <summary>
/// Typed client for Wings Notification Hub. Handles client-credential tokens (cached, refreshed before expiry),
/// idempotency keys, retries with backoff and error mapping. Thread-safe; register as a singleton or typed HttpClient.
/// </summary>
public sealed class NotificationHubClient
{
    private readonly HttpClient _http;
    private readonly NotificationHubClientOptions _options;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTime _tokenExpires;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ChannelJsonConverter(), new JsonStringEnumConverter() },
    };

    public NotificationHubClient(HttpClient http, NotificationHubClientOptions options)
    {
        _http = http;
        _options = options;
        _http.BaseAddress ??= options.BaseAddress;
    }

    // ───────── notifications ─────────

    /// <summary>Sends a notification. An idempotency key is generated when none is given, so retries never duplicate.</summary>
    public Task<SendNotificationResponse> SendAsync(SendNotificationRequest request, CancellationToken ct = default)
    {
        request.IdempotencyKey ??= Guid.NewGuid().ToString("N");
        if (request.CompanyId == Guid.Empty && _options.DefaultCompanyId is { } c) request.CompanyId = c;
        return PostAsync<SendNotificationResponse>("api/v1/notifications/send", request, ct);
    }

    /// <summary>
    /// Convenience overload for generated code: <c>SendAsync(new { NotificationType = "ORDER_APPROVED", RecipientUserId = id, Variables = new { … } })</c>.
    /// Recognised properties: NotificationType, TemplateCode, CompanyId, RecipientUserId, RecipientCustomerId, RecipientEmail, RecipientMobile,
    /// Channels, Variables, Priority, ScheduleAt, IdempotencyKey, CorrelationId.
    /// </summary>
    public Task<SendNotificationResponse> SendAsync(object anonymous, CancellationToken ct = default)
    {
        if (anonymous is SendNotificationRequest typed) return SendAsync(typed, ct);
        var doc = JsonSerializer.SerializeToElement(anonymous, JsonOptions);
        string? S(string name) => doc.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var req = new SendNotificationRequest
        {
            NotificationType = S("notificationType"), TemplateCode = S("templateCode"),
            CompanyId = Guid.TryParse(S("companyId"), out var cid) ? cid : _options.DefaultCompanyId ?? Guid.Empty,
            Recipient = new RecipientDto
            {
                UserId = S("recipientUserId"), CustomerId = S("recipientCustomerId"), Email = S("recipientEmail"), Mobile = S("recipientMobile"), Name = S("recipientName"),
            },
            Variables = doc.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Object
                ? vars.EnumerateObject().ToDictionary(p => char.ToUpperInvariant(p.Name[0]) + p.Name[1..], p => p.Value.Clone()) : null,
            Channels = doc.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray().Select(x => ChannelNames.TryParse(x.GetString(), out var c) ? c : throw new ArgumentException($"Unknown channel {x}")).ToList() : null,
            Priority = Enum.TryParse<NotificationPriority>(S("priority"), true, out var p) ? p : null,
            ScheduleAt = DateTimeOffset.TryParse(S("scheduleAt"), out var at) ? at : null,
            IdempotencyKey = S("idempotencyKey"), CorrelationId = S("correlationId"),
        };
        return SendAsync(req, ct);
    }

    public Task<BatchSendResponse> SendBatchAsync(IEnumerable<SendNotificationRequest> requests, CancellationToken ct = default)
    {
        var list = requests.ToList();
        foreach (var r in list) r.IdempotencyKey ??= Guid.NewGuid().ToString("N");
        return PostAsync<BatchSendResponse>("api/v1/notifications/batch", new BatchSendRequest { Notifications = list }, ct);
    }

    public Task<SendNotificationResponse> PreviewSendAsync(SendNotificationRequest request, CancellationToken ct = default)
    {
        request.DryRun = true;
        return PostAsync<SendNotificationResponse>("api/v1/notifications/send", request, ct);
    }

    public Task<NotificationStatusDto> GetStatusAsync(Guid notificationId, CancellationToken ct = default)
        => GetAsync<NotificationStatusDto>($"api/v1/notifications/{notificationId}/status", ct);

    public Task<DeliveryTimelineDto> GetHistoryAsync(Guid notificationId, CancellationToken ct = default)
        => GetAsync<DeliveryTimelineDto>($"api/v1/notifications/{notificationId}/history", ct);

    public Task<JsonElement> CancelAsync(Guid notificationId, CancellationToken ct = default)
        => PostAsync<JsonElement>($"api/v1/notifications/{notificationId}/cancel", new { }, ct);

    /// <summary>Polls until every delivery reaches a final state or the timeout elapses (tests/tools; production code should use webhooks).</summary>
    public async Task<NotificationStatusDto> WaitForCompletionAsync(Guid notificationId, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var s = await GetStatusAsync(notificationId, ct);
            if (s.Status is RequestStatus.Completed or RequestStatus.Failed or RequestStatus.PartiallyFailed or RequestStatus.Suppressed or RequestStatus.Cancelled or RequestStatus.Rejected
                || DateTime.UtcNow > until) return s;
            await Task.Delay(250, ct);
        }
    }

    // ───────── events, templates, recipients ─────────

    public Task<BusinessEventResponse> PublishEventAsync(BusinessEventRequest request, CancellationToken ct = default)
    {
        request.IdempotencyKey ??= Guid.NewGuid().ToString("N");
        if (request.CompanyId == Guid.Empty && _options.DefaultCompanyId is { } c) request.CompanyId = c;
        return PostAsync<BusinessEventResponse>("api/v1/events", request, ct);
    }

    public Task<List<TemplateSummaryDto>> ListTemplatesAsync(Channel? channel = null, CancellationToken ct = default)
        => GetAsync<List<TemplateSummaryDto>>("api/v1/templates" + (channel is { } c ? $"?channel={ChannelNames.ToWire(c)}" : ""), ct);

    public Task<TemplatePreviewResponse> PreviewTemplateAsync(TemplatePreviewRequest request, CancellationToken ct = default)
        => PostAsync<TemplatePreviewResponse>("api/v1/notifications/preview", request, ct);

    public Task<JsonElement> UpsertContactAsync(ContactUpsertRequest request, CancellationToken ct = default)
        => SendJsonAsync<JsonElement>(HttpMethod.Put, "api/v1/contacts", request, ct);

    public Task<DeviceDto> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken ct = default)
        => PostAsync<DeviceDto>("api/v1/devices", request, ct);

    /// <summary>Unregisters a device (logout/uninstall); it is no longer targeted.</summary>
    public Task<JsonElement> UnregisterDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => SendJsonAsync<JsonElement>(HttpMethod.Delete, $"api/v1/devices/{deviceId}", null, ct);

    public Task<JsonElement> DeleteWebhookAsync(Guid subscriptionId, CancellationToken ct = default)
        => SendJsonAsync<JsonElement>(HttpMethod.Delete, $"api/v1/webhooks/{subscriptionId}", null, ct);

    public Task<PreferencesDto> GetPreferencesAsync(Guid companyId, string externalId, CancellationToken ct = default)
        => GetAsync<PreferencesDto>($"api/v1/preferences?companyId={companyId}&externalId={Uri.EscapeDataString(externalId)}", ct);

    public Task<JsonElement> RecordConsentAsync(ConsentRequest request, CancellationToken ct = default)
        => PostAsync<JsonElement>("api/v1/consents", request, ct);

    /// <summary>Mints a short-lived inbox token for an end user (used by Angular/WinForms inbox clients).</summary>
    public Task<InboxTokenResponse> CreateInboxTokenAsync(InboxTokenRequest request, CancellationToken ct = default)
        => PostAsync<InboxTokenResponse>("api/v1/inapp/tokens", request, ct);

    public Task<JsonElement> RegisterWebhookAsync(string name, string url, IEnumerable<string> eventTypes, Guid? companyId = null, CancellationToken ct = default)
        => PostAsync<JsonElement>("api/v1/webhooks", new { name, url, eventTypes, companyId }, ct);

    public Task<ManifestRegistrationResult> RegisterManifestAsync(NotificationManifest manifest, CancellationToken ct = default)
        => PostAsync<ManifestRegistrationResult>("api/v1/registrations/manifest", manifest, ct);

    public Task<JsonElement> GetUsageAsync(DateTime? from = null, DateTime? to = null, string groupBy = "channel", CancellationToken ct = default)
        => GetAsync<JsonElement>($"api/v1/usage?groupBy={groupBy}" + (from is { } f ? $"&from={f:O}" : "") + (to is { } t ? $"&to={t:O}" : ""), ct);

    public Task<JsonElement> GetProviderHealthAsync(CancellationToken ct = default) => GetAsync<JsonElement>("api/v1/providers/health", ct);

    // ───────── plumbing ─────────

    public Task<T> GetAsync<T>(string path, CancellationToken ct) => SendJsonAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostAsync<T>(string path, object body, CancellationToken ct) => SendJsonAsync<T>(HttpMethod.Post, path, body, ct);

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _token = null; continue; }
                if ((int)resp.StatusCode is 429 or 502 or 503 or 504 && attempt < _options.MaxRetries)
                {
                    await Task.Delay(resp.Headers.RetryAfter?.Delta ?? Backoff(attempt), ct);
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    ApiError? err = null;
                    try { err = JsonSerializer.Deserialize<ApiError>(text, JsonOptions); } catch (JsonException) { }
                    throw new NotificationHubException(resp.StatusCode, err?.Code is { Length: > 0 } ? err : null, text);
                }
                if (typeof(T) == typeof(JsonElement) && string.IsNullOrEmpty(text)) return (T)(object)default(JsonElement);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt) + Random.Shared.Next(0, 200));

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = _options.ClientId, ["client_secret"] = _options.ClientSecret };
            if (_options.Scope is { } s) form["scope"] = s;
            using var resp = await _http.PostAsync("connect/token", new FormUrlEncodedContent(form), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new NotificationHubException(resp.StatusCode, null, text);
            using var doc = JsonDocument.Parse(text);
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _tokenExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
            return _token!;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>Verifies an incoming webhook from the hub (HMAC-SHA256, 5-minute replay window).</summary>
    public static bool VerifyWebhook(string signingSecret, string body, string? signatureHeader, TimeSpan? tolerance = null)
        => WebhookVerifier.Verify(signingSecret, body, signatureHeader, DateTimeOffset.UtcNow, tolerance ?? TimeSpan.FromMinutes(5));
}

/// <summary>Standalone webhook verification (no dependency on server assemblies).</summary>
public static class WebhookVerifier
{
    public const string Header = "X-Wings-Signature";

    public static bool Verify(string secret, string body, string? header, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header)) return false;
        long? t = null;
        var sigs = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], out var ts)) t = ts;
            else if (kv[0].Trim() == "v1") sigs.Add(kv[1].Trim());
        }
        if (t is null || Math.Abs(now.ToUnixTimeSeconds() - t.Value) > tolerance.TotalSeconds) return false;
        var expected = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant());
        return sigs.Any(s => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(s), expected));
    }
}
