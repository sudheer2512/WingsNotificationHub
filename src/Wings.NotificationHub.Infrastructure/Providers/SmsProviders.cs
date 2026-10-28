using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Messaging;

namespace Wings.NotificationHub.Infrastructure.Providers;

internal static class HttpOutcome
{
    /// <summary>Common HTTP status → outcome mapping. 5xx after the request reached the provider is ambiguous.</summary>
    public static SendResult FromStatus(HttpResponseMessage resp, string body, string prefix)
    {
        var code = (int)resp.StatusCode;
        return code switch
        {
            429 => SendResult.Throttled(Trim(body), resp.Headers.RetryAfter?.Delta),
            408 => SendResult.Ambiguous($"{prefix}_408", Trim(body)),
            503 => SendResult.Transient($"{prefix}_503", Trim(body), "503", resp.Headers.RetryAfter?.Delta),
            >= 500 => SendResult.Ambiguous($"{prefix}_{code}", Trim(body)),
            401 or 403 => SendResult.Transient($"{prefix}_{code}", "Provider rejected our credentials: " + Trim(body), code.ToString()),
            _ => SendResult.Permanent($"{prefix}_{code}", Trim(body), responseCode: code.ToString()),
        };
    }

    public static string Trim(string s) => s.Length > 800 ? s[..800] : s;

    public static async Task<SendResult?> Guard(Func<Task<SendResult>> send)
    {
        try { return await send(); }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        {
            return SendResult.Transient("connection_failed", ex.Message);
        }
    }
}

// ───────────────────────────── Twilio ─────────────────────────────

public sealed class TwilioSmsProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "twilio";
    public IReadOnlyList<Channel> Channels => [Channel.Sms];
    public string DisplayName => "Twilio Programmable Messaging";
    public IReadOnlyList<string> SecretKeys => ["authToken"];
    public IReadOnlyList<string> SettingKeys => ["accountSid", "messagingServiceSid", "fromNumber", "validityPeriodSeconds"];
    public INotificationProvider Create(ProviderAccountConfig config) => new TwilioSmsProvider(config, http);
}

public sealed class TwilioSmsProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider, IStatusQueryProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsDeliveryReceipts = true, SupportsStatusQuery = true, SupportsInbound = true };

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var sid = config.RequireSetting("accountSid");
        var req = new HttpRequestMessage(method, $"https://api.twilio.com/2010-04-01/Accounts/{sid}/{path}") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{config.RequireSecret("authToken")}")));
        return req;
    }

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            var form = new Dictionary<string, string> { ["To"] = m.To, ["Body"] = m.Content.Body };
            if (config.Setting("messagingServiceSid") is { Length: > 0 } mss) form["MessagingServiceSid"] = mss;
            else form["From"] = m.From ?? config.RequireSetting("fromNumber");
            if (m.CallbackUrl is not null) form["StatusCallback"] = m.CallbackUrl;
            var validity = m.ExpiresAt is { } exp ? (int)Math.Clamp((exp - DateTime.UtcNow).TotalSeconds, 1, 36000)
                : int.TryParse(config.Setting("validityPeriodSeconds"), out var v) ? v : 0;
            if (validity > 0) form["ValidityPeriod"] = validity.ToString();
            using var req = Request(HttpMethod.Post, "Messages.json", new FormUrlEncodedContent(form));
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            if (resp.IsSuccessStatusCode)
                return SendResult.Accepted(json?["sid"]?.GetValue<string>(), $"twilio status={json?["status"]}", units: int.TryParse(json?["num_segments"]?.ToString(), out var seg) ? seg : 1);
            var errorCode = json?["code"]?.ToString();
            // 21211 invalid 'To', 21610 recipient replied STOP, 21614 not a mobile number.
            if (errorCode is "21211" or "21614" or "21610")
                return SendResult.Permanent($"twilio_{errorCode}", json?["message"]?.ToString() ?? body, invalidAddress: errorCode != "21610", responseCode: ((int)resp.StatusCode).ToString());
            return HttpOutcome.FromStatus(resp, body, "twilio");
        }))!;

    public async Task<ProviderStatusReport?> QueryStatusAsync(string? providerMessageId, string idempotencyKey, CancellationToken ct)
    {
        if (providerMessageId is null) return null; // Twilio cannot look messages up by our key
        using var req = Request(HttpMethod.Get, $"Messages/{providerMessageId}.json");
        var resp = await http.CreateClient("providers").SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new ProviderStatusReport { Found = false };
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return new ProviderStatusReport { Found = true, ProviderMessageId = providerMessageId, Status = TwilioCallbackHandler.Map(json?["status"]?.GetValue<string>()) ?? DeliveryStatus.ProviderAccepted };
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var req = Request(HttpMethod.Get, ".json");
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? ProviderHealth.Healthy("Twilio account reachable.") : ProviderHealth.Unhealthy($"Twilio returned {(int)resp.StatusCode}.");
        }
        catch (Exception ex) { return ProviderHealth.Unhealthy(ex.Message); }
    }
}

/// <summary>Twilio status callbacks and inbound SMS. Validates X-Twilio-Signature (HMAC-SHA1 over URL + sorted form fields).</summary>
public sealed class TwilioCallbackHandler : ICallbackHandler
{
    public string ProviderType => "twilio";

    public static DeliveryStatus? Map(string? s) => s switch
    {
        "accepted" or "queued" or "sending" => DeliveryStatus.ProviderAccepted,
        "sent" => DeliveryStatus.Sent,
        "delivered" => DeliveryStatus.Delivered,
        "read" => DeliveryStatus.Read,
        "undelivered" or "failed" => DeliveryStatus.Failed,
        _ => null,
    };

    public Task<CallbackParseResult> ParseAsync(CallbackRequest request, ProviderAccountConfig config, CancellationToken ct)
    {
        var token = config.Secret("authToken");
        if (string.IsNullOrEmpty(token)) return Task.FromResult(CallbackParseResult.Reject("Twilio auth token not configured."));
        request.Headers.TryGetValue("X-Twilio-Signature", out var sig);
        var data = new StringBuilder(request.Url);
        foreach (var kv in request.Form.OrderBy(k => k.Key, StringComparer.Ordinal)) data.Append(kv.Key).Append(kv.Value);
        var expected = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(data.ToString())));
        if (sig is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(expected)))
            return Task.FromResult(CallbackParseResult.Reject("Invalid X-Twilio-Signature."));
        var f = request.Form;
        var sid = f.GetValueOrDefault("MessageSid") ?? f.GetValueOrDefault("SmsSid");
        var status = f.GetValueOrDefault("MessageStatus") ?? f.GetValueOrDefault("SmsStatus");
        if (status == "received")
        {
            var body = f.GetValueOrDefault("Body");
            return Task.FromResult(new CallbackParseResult
            {
                Authentic = true,
                Events = [new ProviderCallbackEvent
                {
                    EventId = sid,
                    Inbound = new InboundMessageData { From = f.GetValueOrDefault("From") ?? "", To = f.GetValueOrDefault("To"), Body = body, ProviderMessageId = sid },
                    // Carrier-level STOP: Twilio blocks further sends; mirror it as an opt-out suppression.
                    Suppress = body?.Trim().ToUpperInvariant() is "STOP" or "STOPALL" or "UNSUBSCRIBE" or "CANCEL" ? SuppressionReason.OptOut : null,
                    SuppressAddress = f.GetValueOrDefault("From"),
                }],
            });
        }
        var errorCode = f.GetValueOrDefault("ErrorCode");
        return Task.FromResult(new CallbackParseResult
        {
            Authentic = true,
            Events = [new ProviderCallbackEvent
            {
                EventId = $"{sid}:{status}", ProviderMessageId = sid, Status = Map(status), ErrorCode = errorCode is null ? null : "twilio_" + errorCode,
                // 30003 unreachable, 30005 unknown destination, 30006 landline: number is invalid.
                Suppress = errorCode is "30005" or "30006" ? SuppressionReason.InvalidAddress : null, SuppressAddress = f.GetValueOrDefault("To"),
            }],
        });
    }
}

// ───────────────────────────── MSG91 (India, DLT) ─────────────────────────────

public sealed class Msg91SmsProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "msg91";
    public IReadOnlyList<Channel> Channels => [Channel.Sms];
    public string DisplayName => "MSG91 (India, TRAI DLT)";
    public IReadOnlyList<string> SecretKeys => ["authKey"];
    public IReadOnlyList<string> SettingKeys => ["senderId", "route", "dltEntityId", "baseUrl"];
    public INotificationProvider Create(ProviderAccountConfig config) => new Msg91SmsProvider(config, http);
}

/// <summary>
/// MSG91 v2 sendsms with DLT fields. Indian commercial SMS requires a DLT-registered header (sender id), principal
/// entity id and content template id; messages without them are rejected here rather than by the operator.
/// </summary>
public sealed class Msg91SmsProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsDeliveryReceipts = true };

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            var entity = m.DltEntityId ?? config.Setting("dltEntityId");
            if (m.To.StartsWith("+91") && (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(m.DltTemplateId)))
                return SendResult.Permanent("dlt_missing", "Indian SMS requires a DLT entity id and a DLT content template id on the template version.");
            var payload = new JsonObject
            {
                ["sender"] = m.From ?? config.RequireSetting("senderId"),
                ["route"] = config.Setting("route") ?? "4",
                ["country"] = "0",
                ["unicode"] = SmsSegmenter.Analyze(m.Content.Body).Encoding == "UCS-2" ? 1 : 0,
                ["DLT_TE_ID"] = m.DltTemplateId,
                ["PE_ID"] = entity,
                ["sms"] = new JsonArray(new JsonObject { ["message"] = m.Content.Body, ["to"] = new JsonArray(m.To.TrimStart('+')) }),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, (config.Setting("baseUrl") ?? "https://api.msg91.com") + "/api/v2/sendsms")
            { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("authkey", config.RequireSecret("authKey"));
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            if (resp.IsSuccessStatusCode && json?["type"]?.GetValue<string>() == "success")
                return SendResult.Accepted(json["message"]?.GetValue<string>(), "msg91 success", units: SmsSegmenter.Analyze(m.Content.Body).Segments);
            if (resp.IsSuccessStatusCode) return SendResult.Permanent("msg91_error", json?["message"]?.ToString() ?? body);
            return HttpOutcome.FromStatus(resp, body, "msg91");
        }))!;

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(config.Secret("authKey") is { Length: > 0 } ? ProviderHealth.Unknown("Credentials configured; MSG91 has no side-effect-free health endpoint.") : ProviderHealth.Unhealthy("authKey missing."));
}

// ───────────────────────────── Generic HTTP SMS gateway ─────────────────────────────

public sealed class HttpSmsProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "http-sms";
    public IReadOnlyList<Channel> Channels => [Channel.Sms];
    public string DisplayName => "Generic HTTP SMS gateway (configurable request)";
    public IReadOnlyList<string> SecretKeys => ["apiKey"];
    public IReadOnlyList<string> SettingKeys => ["url", "method", "contentType", "bodyTemplate", "authHeader", "authScheme", "messageIdPath", "successPath", "successValue"];
    public INotificationProvider Create(ProviderAccountConfig config) => new HttpSmsProvider(config, http);
}

/// <summary>
/// Adapter for gateways with simple HTTP APIs. <c>bodyTemplate</c> placeholders: {to}, {toDigits}, {from}, {body},
/// {dltEntityId}, {dltTemplateId}, {reference}, {unicode} — values are JSON- or URL-escaped to match <c>contentType</c>.
/// </summary>
public sealed class HttpSmsProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new();

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            var contentType = config.Setting("contentType") ?? "application/json";
            var json = contentType.Contains("json");
            string Esc(string? v) => json ? JsonEncodedText.Encode(v ?? "").ToString() : Uri.EscapeDataString(v ?? "");
            var values = new Dictionary<string, string?>
            {
                ["to"] = m.To, ["toDigits"] = m.To.TrimStart('+'), ["from"] = m.From, ["body"] = m.Content.Body,
                ["dltEntityId"] = m.DltEntityId, ["dltTemplateId"] = m.DltTemplateId, ["reference"] = m.IdempotencyKey,
                ["unicode"] = SmsSegmenter.Analyze(m.Content.Body).Encoding == "UCS-2" ? "1" : "0",
            };
            var template = config.Setting("bodyTemplate") ?? "{\"to\":\"{to}\",\"from\":\"{from}\",\"text\":\"{body}\",\"reference\":\"{reference}\"}";
            var bodyText = values.Aggregate(template, (acc, kv) => acc.Replace("{" + kv.Key + "}", Esc(kv.Value)));
            var url = values.Aggregate(config.RequireSetting("url"), (acc, kv) => acc.Replace("{" + kv.Key + "}", Uri.EscapeDataString(kv.Value ?? "")));
            using var req = new HttpRequestMessage(new HttpMethod(config.Setting("method") ?? "POST"), url);
            if (req.Method != HttpMethod.Get) req.Content = new StringContent(bodyText, Encoding.UTF8, contentType);
            if (config.Secret("apiKey") is { Length: > 0 } key)
            {
                var header = config.Setting("authHeader") ?? "Authorization";
                req.Headers.TryAddWithoutValidation(header, config.Setting("authScheme") is { Length: > 0 } scheme ? $"{scheme} {key}" : key);
            }
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return HttpOutcome.FromStatus(resp, text, "http_sms");
            JsonNode? doc = null;
            try { doc = JsonNode.Parse(text); } catch (JsonException) { }
            if (config.Setting("successPath") is { Length: > 0 } sp && Core.Rules.ConditionEvaluator.AsString(Core.Rules.ConditionEvaluator.Select(doc, sp)) != (config.Setting("successValue") ?? "true"))
                return SendResult.Permanent("http_sms_rejected", HttpOutcome.Trim(text));
            var id = config.Setting("messageIdPath") is { Length: > 0 } mp ? Core.Rules.ConditionEvaluator.AsString(Core.Rules.ConditionEvaluator.Select(doc, mp)) : m.IdempotencyKey;
            return SendResult.Accepted(string.IsNullOrEmpty(id) ? m.IdempotencyKey : id, "gateway accepted", units: SmsSegmenter.Analyze(m.Content.Body).Segments);
        }))!;

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct) => Task.FromResult(ProviderHealth.Unknown("Generic gateway: no health endpoint configured."));
}
