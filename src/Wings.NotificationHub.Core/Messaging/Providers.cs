using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Core.Messaging;

/// <summary>Rendered, channel-ready content stored (encrypted) on each delivery.</summary>
public sealed class RenderedContent
{
    public string? Subject { get; set; }
    /// <summary>HTML for email; text for every other channel.</summary>
    public string Body { get; set; } = "";
    public BodyFormat Format { get; set; }
    public string? TextBody { get; set; }
    /// <summary>In-app / push title.</summary>
    public string? Title { get; set; }
    /// <summary>WhatsApp template invocation (name, language, ordered parameters) when sending a pre-approved template.</summary>
    public WhatsAppTemplateCall? WhatsAppTemplate { get; set; }
    public AppNotificationOptionsDto? App { get; set; }
    public List<AttachmentRefDto>? Attachments { get; set; }
    public List<string>? Cc { get; set; }
    public List<string>? Bcc { get; set; }
    public string? UnsubscribeUrl { get; set; }
    /// <summary>Webhook channel payload.</summary>
    public JsonObject? WebhookPayload { get; set; }
}

public sealed class WhatsAppTemplateCall
{
    public string Name { get; set; } = "";
    public string Language { get; set; } = "en";
    public List<string> HeaderParameters { get; set; } = [];
    public List<string> BodyParameters { get; set; } = [];
    /// <summary>Header media (image/document/video URL) for media templates.</summary>
    public string? HeaderMediaUrl { get; set; }
    public string? HeaderMediaType { get; set; }
    /// <summary>Dynamic URL-button suffixes / quick-reply payloads, by button index.</summary>
    public List<WhatsAppButtonParameter> Buttons { get; set; } = [];
}

public sealed class WhatsAppButtonParameter
{
    public int Index { get; set; }
    /// <summary>"url" or "quick_reply".</summary>
    public string SubType { get; set; } = "url";
    public string Value { get; set; } = "";
}

public sealed class OutboundAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = [];
    public bool Inline { get; set; }
    public string? ContentId { get; set; }
}

/// <summary>What a provider adapter receives. Everything is resolved; adapters never look up tenant data.</summary>
public sealed class NotificationMessage
{
    public Guid DeliveryId { get; init; }
    public Guid TenantId { get; init; }
    public Channel Channel { get; init; }
    public string To { get; init; } = "";
    public string? From { get; init; }
    public string? FromName { get; init; }
    public string? ReplyTo { get; init; }
    public string? DltEntityId { get; init; }
    public string? DltTemplateId { get; init; }
    public string? WhatsAppPhoneNumberId { get; init; }
    public MessageClass MessageClass { get; init; }
    public NotificationPriority Priority { get; init; }
    public RenderedContent Content { get; init; } = new();
    public IReadOnlyList<OutboundAttachment> Attachments { get; init; } = [];
    /// <summary>Stable per delivery: lets providers that support idempotency drop duplicates across retries.</summary>
    public string IdempotencyKey { get; init; } = "";
    public DateTime? ExpiresAt { get; init; }
    /// <summary>Push device data when Channel is Push/WebPush.</summary>
    public PushTarget? Push { get; init; }
    public string? CallbackUrl { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

public sealed class PushTarget
{
    public Guid DeviceId { get; init; }
    public DevicePlatform Platform { get; init; }
    public string Token { get; init; } = "";
    public string? P256dh { get; init; }
    public string? Auth { get; init; }
}

public sealed class SendResult
{
    public AttemptOutcome Outcome { get; init; }
    /// <summary>Status reached on acceptance. Most providers: ProviderAccepted; in-app/webhook: Delivered.</summary>
    public DeliveryStatus AcceptedStatus { get; init; } = DeliveryStatus.ProviderAccepted;
    public string? ProviderMessageId { get; init; }
    public string? ResponseCode { get; init; }
    /// <summary>Sanitized response summary; adapters must not include secrets or the full message body.</summary>
    public string? ResponseSummary { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public decimal? ReportedCost { get; init; }
    public int Units { get; init; } = 1;
    /// <summary>Destination is permanently invalid (bounce, unregistered device token): suppress / invalidate.</summary>
    public bool InvalidAddress { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    public static SendResult Accepted(string? messageId, string? summary = null, DeliveryStatus status = DeliveryStatus.ProviderAccepted, int units = 1, decimal? cost = null)
        => new() { Outcome = AttemptOutcome.Accepted, ProviderMessageId = messageId, ResponseSummary = summary, AcceptedStatus = status, Units = units, ReportedCost = cost, ResponseCode = "accepted" };

    public static SendResult Permanent(string code, string message, bool invalidAddress = false, string? responseCode = null)
        => new() { Outcome = AttemptOutcome.PermanentFailure, ErrorCode = code, ErrorMessage = message, InvalidAddress = invalidAddress, ResponseCode = responseCode };

    public static SendResult Transient(string code, string message, string? responseCode = null, TimeSpan? retryAfter = null)
        => new() { Outcome = AttemptOutcome.TransientFailure, ErrorCode = code, ErrorMessage = message, ResponseCode = responseCode, RetryAfter = retryAfter };

    public static SendResult Ambiguous(string code, string message)
        => new() { Outcome = AttemptOutcome.Ambiguous, ErrorCode = code, ErrorMessage = message };

    public static SendResult Throttled(string message, TimeSpan? retryAfter = null)
        => new() { Outcome = AttemptOutcome.Throttled, ErrorCode = "throttled", ErrorMessage = message, RetryAfter = retryAfter, ResponseCode = "429" };
}

public sealed class ProviderHealth
{
    public ProviderHealthStatus Status { get; init; }
    public string? Detail { get; init; }
    public int? LatencyMs { get; init; }
    public static ProviderHealth Healthy(string? detail = null, int? latency = null) => new() { Status = ProviderHealthStatus.Healthy, Detail = detail, LatencyMs = latency };
    public static ProviderHealth Unhealthy(string detail) => new() { Status = ProviderHealthStatus.Unhealthy, Detail = detail };
    public static ProviderHealth Unknown(string detail) => new() { Status = ProviderHealthStatus.Unknown, Detail = detail };
}

/// <summary>Capabilities that change how the orchestrator treats a provider.</summary>
public sealed class ProviderCapabilities
{
    /// <summary>Provider drops duplicate submissions carrying the same idempotency key: safe to resend after an ambiguous outcome.</summary>
    public bool SupportsIdempotency { get; init; }
    /// <summary>Provider can be asked for a message's status (reconciliation of missing callbacks / ambiguous submissions).</summary>
    public bool SupportsStatusQuery { get; init; }
    public bool SupportsDeliveryReceipts { get; init; }
    public bool SupportsReadReceipts { get; init; }
    public bool SupportsInbound { get; init; }
}

/// <summary>A provider adapter bound to one provider account's configuration.</summary>
public interface INotificationProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<SendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
    Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Optional: query a message status from the provider (reconciliation).</summary>
public interface IStatusQueryProvider
{
    /// <summary>Looks a message up by provider message id, or by our idempotency key when the id is unknown (ambiguous submit).</summary>
    Task<ProviderStatusReport?> QueryStatusAsync(string? providerMessageId, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class ProviderStatusReport
{
    public bool Found { get; init; }
    public string? ProviderMessageId { get; init; }
    public DeliveryStatus Status { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Provider-account configuration handed to adapter factories (secrets already decrypted, in memory only).</summary>
public sealed class ProviderAccountConfig
{
    public Guid AccountId { get; init; }
    public string Name { get; init; } = "";
    public string ProviderType { get; init; } = "";
    public Channel Channel { get; init; }
    public JsonObject Settings { get; init; } = new();
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public string? Setting(string key) => Settings.TryGetPropertyValue(key, out var v) && v is not null ? Rules.ConditionEvaluator.AsString(v) : null;
    public string? Secret(string key) => Secrets.TryGetValue(key, out var v) ? v : null;
    public string RequireSecret(string key) => Secret(key) is { Length: > 0 } s ? s : throw new ProviderConfigurationException($"Provider '{Name}' is missing secret '{key}'.");
    public string RequireSetting(string key) => Setting(key) is { Length: > 0 } s ? s : throw new ProviderConfigurationException($"Provider '{Name}' is missing setting '{key}'.");
}

public sealed class ProviderConfigurationException(string message) : Exception(message);

/// <summary>Creates adapters for one provider type. Registered in DI; selected by <see cref="ProviderType"/>.</summary>
public interface IProviderFactory
{
    string ProviderType { get; }
    IReadOnlyList<Channel> Channels { get; }
    string DisplayName { get; }
    /// <summary>Secret keys the provider needs (shown in the admin UI).</summary>
    IReadOnlyList<string> SecretKeys { get; }
    IReadOnlyList<string> SettingKeys { get; }
    INotificationProvider Create(ProviderAccountConfig config);
}

/// <summary>A normalized provider callback (delivery receipt, read receipt, bounce, inbound message).</summary>
public sealed class ProviderCallbackEvent
{
    public string? EventId { get; init; }
    public string? ProviderMessageId { get; init; }
    public DeliveryStatus? Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public decimal? Cost { get; init; }
    /// <summary>Bounce/complaint/invalid number: add the address to the suppression list.</summary>
    public SuppressionReason? Suppress { get; init; }
    public string? SuppressAddress { get; init; }
    /// <summary>"open", "click", "unsubscribe" interaction events.</summary>
    public string? Interaction { get; init; }
    public InboundMessageData? Inbound { get; init; }
}

public sealed class InboundMessageData
{
    public string From { get; init; } = "";
    public string? To { get; init; }
    public string? FromName { get; init; }
    public string? Subject { get; init; }
    public string? Body { get; init; }
    public string MessageType { get; init; } = "text";
    public string? ProviderMessageId { get; init; }
    public string? InReplyToProviderMessageId { get; init; }
    /// <summary>Quick-reply button payload, when the customer tapped a button.</summary>
    public string? ButtonPayload { get; init; }
    public List<InboundMediaRef> Media { get; init; } = [];
}

public sealed class InboundMediaRef
{
    public string? ProviderMediaId { get; init; }
    public string? Url { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string? FileName { get; init; }
    /// <summary>Inline content when the provider posts it directly (email inbound).</summary>
    public byte[]? Content { get; init; }
}

public sealed class CallbackRequest
{
    public string Method { get; init; } = "POST";
    public string Url { get; init; } = "";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Body { get; init; } = "";
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class CallbackParseResult
{
    public bool Authentic { get; init; }
    public string? RejectReason { get; init; }
    public IReadOnlyList<ProviderCallbackEvent> Events { get; init; } = [];
    /// <summary>Response body for verification handshakes (Meta hub.challenge, SNS subscription confirmation).</summary>
    public string? HandshakeResponse { get; init; }
    public static CallbackParseResult Reject(string reason) => new() { Authentic = false, RejectReason = reason };
}

/// <summary>Verifies and parses a provider's webhook callbacks. Implemented by adapters that support callbacks.</summary>
public interface ICallbackHandler
{
    string ProviderType { get; }
    Task<CallbackParseResult> ParseAsync(CallbackRequest request, ProviderAccountConfig config, CancellationToken cancellationToken);
}
