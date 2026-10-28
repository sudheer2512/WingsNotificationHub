using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;

namespace Wings.NotificationHub.Infrastructure.Providers;

public sealed class MetaWhatsAppProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "meta-whatsapp";
    public IReadOnlyList<Channel> Channels => [Channel.WhatsApp];
    public string DisplayName => "WhatsApp Business Platform — Meta Cloud API";
    public IReadOnlyList<string> SecretKeys => ["accessToken", "appSecret"];
    public IReadOnlyList<string> SettingKeys => ["phoneNumberId", "businessAccountId", "apiVersion", "verifyToken"];
    public INotificationProvider Create(ProviderAccountConfig config) => new WhatsAppCloudProvider(config, http, WhatsAppApiFlavor.Meta);
}

public sealed class Dialog360WhatsAppProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "360dialog";
    public IReadOnlyList<Channel> Channels => [Channel.WhatsApp];
    public string DisplayName => "WhatsApp via 360dialog (Cloud API compatible BSP)";
    public IReadOnlyList<string> SecretKeys => ["apiKey"];
    public IReadOnlyList<string> SettingKeys => ["baseUrl"];
    public INotificationProvider Create(ProviderAccountConfig config) => new WhatsAppCloudProvider(config, http, WhatsAppApiFlavor.Dialog360);
}

public enum WhatsAppApiFlavor { Meta, Dialog360 }

/// <summary>
/// WhatsApp Cloud API message sending. Business-initiated messages use pre-approved templates (header/body/button
/// parameters); free-form text is only sent inside the 24-hour customer-service window (enforced by the dispatcher).
/// </summary>
public sealed class WhatsAppCloudProvider(ProviderAccountConfig config, IHttpClientFactory http, WhatsAppApiFlavor flavor) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsDeliveryReceipts = true, SupportsReadReceipts = true, SupportsInbound = true };

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        HttpRequestMessage req;
        if (flavor == WhatsAppApiFlavor.Meta)
        {
            var version = config.Setting("apiVersion") ?? "v21.0";
            req = new HttpRequestMessage(method, $"https://graph.facebook.com/{version}/{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.RequireSecret("accessToken"));
        }
        else
        {
            req = new HttpRequestMessage(method, (config.Setting("baseUrl") ?? "https://waba-v2.360dialog.io") + "/" + path);
            req.Headers.Add("D360-API-KEY", config.RequireSecret("apiKey"));
        }
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    public static JsonObject BuildPayload(NotificationMessage m)
    {
        var to = m.To.TrimStart('+');
        var payload = new JsonObject { ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = to };
        if (m.Content.WhatsAppTemplate is { } t)
        {
            var components = new JsonArray();
            if (t.HeaderMediaUrl is { Length: > 1 } media && t.HeaderMediaType is { } mt)
                components.Add(new JsonObject
                {
                    ["type"] = "header",
                    ["parameters"] = new JsonArray(new JsonObject { ["type"] = mt, [mt] = new JsonObject { ["link"] = media } }),
                });
            else if (t.HeaderParameters.Count > 0)
                components.Add(new JsonObject { ["type"] = "header", ["parameters"] = TextParams(t.HeaderParameters) });
            if (t.BodyParameters.Count > 0) components.Add(new JsonObject { ["type"] = "body", ["parameters"] = TextParams(t.BodyParameters) });
            foreach (var b in t.Buttons)
                components.Add(new JsonObject
                {
                    ["type"] = "button", ["sub_type"] = b.SubType, ["index"] = b.Index.ToString(),
                    ["parameters"] = new JsonArray(b.SubType == "url"
                        ? new JsonObject { ["type"] = "text", ["text"] = b.Value }
                        : new JsonObject { ["type"] = "payload", ["payload"] = b.Value }),
                });
            payload["type"] = "template";
            payload["template"] = new JsonObject
            {
                ["name"] = t.Name, ["language"] = new JsonObject { ["code"] = t.Language }, ["components"] = components,
            };
        }
        else if (m.Content.App?.Actions is { Count: > 0 } actions)
        {
            // Interactive quick-reply buttons (max 3) inside the customer-service window.
            payload["type"] = "interactive";
            payload["interactive"] = new JsonObject
            {
                ["type"] = "button",
                ["body"] = new JsonObject { ["text"] = m.Content.Body },
                ["action"] = new JsonObject
                {
                    ["buttons"] = new JsonArray(actions.Take(3).Select(a => (JsonNode)new JsonObject
                    {
                        ["type"] = "reply", ["reply"] = new JsonObject { ["id"] = a.Id, ["title"] = a.Label.Length > 20 ? a.Label[..20] : a.Label },
                    }).ToArray()),
                },
            };
        }
        else
        {
            payload["type"] = "text";
            payload["text"] = new JsonObject { ["body"] = m.Content.Body, ["preview_url"] = true };
        }
        return payload;

        static JsonArray TextParams(IEnumerable<string> values) => new(values.Select(v => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = v }).ToArray());
    }

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            var path = flavor == WhatsAppApiFlavor.Meta ? $"{m.WhatsAppPhoneNumberId ?? config.RequireSetting("phoneNumberId")}/messages" : "messages";
            using var req = Request(HttpMethod.Post, path, BuildPayload(m));
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            if (resp.IsSuccessStatusCode)
                return SendResult.Accepted(json?["messages"]?[0]?["id"]?.GetValue<string>(), $"whatsapp accepted ({json?["messages"]?[0]?["message_status"] ?? "accepted"})");
            var code = json?["error"]?["code"]?.ToString();
            var msg = json?["error"]?["message"]?.ToString() ?? body;
            return code switch
            {
                // Rate limits: business/pair/spam.
                "4" or "80007" or "130429" or "131056" or "131048" => SendResult.Throttled(msg, TimeSpan.FromSeconds(30)),
                // Not a WhatsApp user / invalid recipient.
                "131026" or "131030" => SendResult.Permanent($"wa_{code}", msg, invalidAddress: code == "131026"),
                // Re-engagement required (outside 24h window), template/parameter problems, policy.
                "131047" or "132000" or "132001" or "132005" or "132007" or "132012" or "132015" or "132016" or "131051" or "100" or "131008" or "131009"
                    => SendResult.Permanent($"wa_{code}", msg),
                "131000" or "131016" or "2" => SendResult.Transient($"wa_{code}", msg),
                "190" or "10" or "200" => SendResult.Transient($"wa_{code}", "Access token or permission problem: " + msg),
                _ => HttpOutcome.FromStatus(resp, body, "whatsapp"),
            };
        }))!;

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var req = flavor == WhatsAppApiFlavor.Meta
                ? Request(HttpMethod.Get, $"{config.RequireSetting("phoneNumberId")}?fields=display_phone_number,quality_rating,verified_name")
                : Request(HttpMethod.Get, "v1/configs/webhook");
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode ? ProviderHealth.Healthy(HttpOutcome.Trim(body)) : ProviderHealth.Unhealthy($"{(int)resp.StatusCode}: {HttpOutcome.Trim(body)}");
        }
        catch (Exception ex) { return ProviderHealth.Unhealthy(ex.Message); }
    }

    /// <summary>Submits a template to Meta for approval (message_templates on the WhatsApp Business Account).</summary>
    public async Task<(bool Ok, string? Id, string? Status, string Detail)> SubmitTemplateAsync(JsonObject definition, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, $"{config.RequireSetting("businessAccountId")}/message_templates", definition);
        var resp = await http.CreateClient("providers").SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
        return (resp.IsSuccessStatusCode, json?["id"]?.ToString(), json?["status"]?.ToString(), HttpOutcome.Trim(body));
    }

    /// <summary>Reads template approval statuses (name, language, status, category) from Meta.</summary>
    public async Task<List<(string Name, string Language, string Status, string? Category)>> ListTemplatesAsync(CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, $"{config.RequireSetting("businessAccountId")}/message_templates?fields=name,language,status,category&limit=200");
        var resp = await http.CreateClient("providers").SendAsync(req, ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (json?["data"]?.AsArray() ?? []).Where(n => n is not null)
            .Select(n => (n!["name"]!.ToString(), n["language"]?.ToString() ?? "en", n["status"]?.ToString() ?? "UNKNOWN", n["category"]?.ToString())).ToList();
    }
}

/// <summary>
/// WhatsApp webhooks (Meta and 360dialog share the format). GET verification handshake with the configured verify token;
/// POST bodies are authenticated with X-Hub-Signature-256 (HMAC-SHA256 with the app secret) when one is configured.
/// </summary>
public sealed class WhatsAppCallbackHandler(string providerType) : ICallbackHandler
{
    public string ProviderType => providerType;

    public Task<CallbackParseResult> ParseAsync(CallbackRequest request, ProviderAccountConfig config, CancellationToken ct)
    {
        if (request.Method == "GET")
        {
            var ok = request.Query.GetValueOrDefault("hub.mode") == "subscribe" && config.Setting("verifyToken") is { Length: > 0 } vt
                     && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(vt), Encoding.UTF8.GetBytes(request.Query.GetValueOrDefault("hub.verify_token") ?? ""));
            return Task.FromResult(ok
                ? new CallbackParseResult { Authentic = true, HandshakeResponse = request.Query.GetValueOrDefault("hub.challenge") }
                : CallbackParseResult.Reject("Webhook verification token mismatch."));
        }
        var secret = config.Secret("appSecret") ?? config.Secret("__callbackSecret");
        if (string.IsNullOrEmpty(secret)) return Task.FromResult(CallbackParseResult.Reject("No app secret configured for signature verification."));
        request.Headers.TryGetValue("X-Hub-Signature-256", out var sig);
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Body)));
        if (sig is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(expected)))
            return Task.FromResult(CallbackParseResult.Reject("Invalid X-Hub-Signature-256."));
        return Task.FromResult(new CallbackParseResult { Authentic = true, Events = Parse(request.Body) });
    }

    public static List<ProviderCallbackEvent> Parse(string body)
    {
        var events = new List<ProviderCallbackEvent>();
        var root = JsonNode.Parse(body);
        foreach (var entry in root?["entry"]?.AsArray() ?? [])
        foreach (var change in entry?["changes"]?.AsArray() ?? [])
        {
            var value = change?["value"];
            if (value is null) continue;
            var ourNumber = value["metadata"]?["display_phone_number"]?.ToString();
            var contacts = value["contacts"]?.AsArray();
            foreach (var s in value["statuses"]?.AsArray() ?? [])
            {
                if (s is null) continue;
                var status = s["status"]?.ToString() switch
                {
                    "sent" => DeliveryStatus.Sent, "delivered" => DeliveryStatus.Delivered, "read" => DeliveryStatus.Read, "failed" => DeliveryStatus.Failed,
                    _ => (DeliveryStatus?)null,
                };
                var err = s["errors"]?[0];
                events.Add(new ProviderCallbackEvent
                {
                    EventId = $"{s["id"]}:{s["status"]}", ProviderMessageId = s["id"]?.ToString(), Status = status,
                    OccurredAt = long.TryParse(s["timestamp"]?.ToString(), out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.UtcNow,
                    ErrorCode = err?["code"] is { } c ? "wa_" + c : null, ErrorMessage = err?["title"]?.ToString() ?? err?["message"]?.ToString(),
                    Suppress = err?["code"]?.ToString() == "131026" ? SuppressionReason.InvalidAddress : null,
                    SuppressAddress = s["recipient_id"] is { } rid ? "+" + rid : null,
                });
            }
            foreach (var msg in value["messages"]?.AsArray() ?? [])
            {
                if (msg is null) continue;
                var type = msg["type"]?.ToString() ?? "text";
                var text = type switch
                {
                    "text" => msg["text"]?["body"]?.ToString(),
                    "button" => msg["button"]?["text"]?.ToString(),
                    "interactive" => msg["interactive"]?["button_reply"]?["title"]?.ToString() ?? msg["interactive"]?["list_reply"]?["title"]?.ToString(),
                    _ => msg[type]?["caption"]?.ToString(),
                };
                var media = new List<InboundMediaRef>();
                if (type is "image" or "document" or "audio" or "video" or "sticker" && msg[type] is { } mnode)
                    media.Add(new InboundMediaRef { ProviderMediaId = mnode["id"]?.ToString(), ContentType = mnode["mime_type"]?.ToString() ?? "application/octet-stream", FileName = mnode["filename"]?.ToString() });
                var from = "+" + msg["from"];
                var stop = text?.Trim().ToUpperInvariant() is "STOP" or "UNSUBSCRIBE";
                events.Add(new ProviderCallbackEvent
                {
                    EventId = msg["id"]?.ToString(),
                    OccurredAt = long.TryParse(msg["timestamp"]?.ToString(), out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.UtcNow,
                    Inbound = new InboundMessageData
                    {
                        From = from, To = ourNumber is null ? null : "+" + ourNumber.TrimStart('+'), Body = text, MessageType = type,
                        FromName = contacts?.FirstOrDefault(c => c?["wa_id"]?.ToString() == msg["from"]?.ToString())?["profile"]?["name"]?.ToString(),
                        ProviderMessageId = msg["id"]?.ToString(), InReplyToProviderMessageId = msg["context"]?["id"]?.ToString(),
                        ButtonPayload = msg["button"]?["payload"]?.ToString() ?? msg["interactive"]?["button_reply"]?["id"]?.ToString(), Media = media,
                    },
                    Suppress = stop ? SuppressionReason.OptOut : null, SuppressAddress = stop ? from : null,
                });
            }
        }
        return events;
    }
}
