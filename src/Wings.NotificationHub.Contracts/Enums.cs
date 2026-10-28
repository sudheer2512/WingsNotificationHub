using System.Text.Json.Serialization;

namespace Wings.NotificationHub.Contracts;

/// <summary>Communication channel. Serialized in lower case on the wire ("email", "whatsapp", "inapp" …).</summary>
[JsonConverter(typeof(ChannelJsonConverter))]
public enum Channel
{
    Email = 1,
    Sms = 2,
    WhatsApp = 3,
    InApp = 4,
    Push = 5,
    WebPush = 6,
    Webhook = 7,
}

/// <summary>Per-delivery lifecycle status. Not every channel reaches every status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeliveryStatus>))]
public enum DeliveryStatus
{
    Draft = 0,
    Scheduled = 1,
    Queued = 2,
    Processing = 3,
    Submitted = 4,
    ProviderAccepted = 5,
    Sent = 6,
    Delivered = 7,
    Read = 8,
    Failed = 20,
    RetryPending = 21,
    Cancelled = 22,
    Expired = 23,
    Suppressed = 24,
    /// <summary>Provider acceptance could not be determined (e.g. timeout after submit). Needs reconciliation or an operator decision.</summary>
    Unknown = 25,
}

/// <summary>Aggregate status of a notification request across all of its deliveries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RequestStatus>))]
public enum RequestStatus
{
    Accepted = 0,
    Scheduled = 1,
    Processing = 2,
    Completed = 3,
    PartiallyFailed = 4,
    Failed = 5,
    Cancelled = 6,
    Suppressed = 7,
    PendingApproval = 8,
    Rejected = 9,
}

[JsonConverter(typeof(JsonStringEnumConverter<NotificationPriority>))]
public enum NotificationPriority { Low = 0, Normal = 1, High = 2, Critical = 3 }

/// <summary>Regulatory class of a message. Drives consent, preference and quiet-hour policy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageClass>))]
public enum MessageClass
{
    /// <summary>Essential messages about a transaction the recipient is party to (invoices, approvals).</summary>
    Transactional = 0,
    /// <summary>Marketing/promotional content. Requires marketing consent; honours unsubscribe.</summary>
    Promotional = 1,
    /// <summary>Security and regulatory notices. Ignores opt-out preferences (never hard suppressions).</summary>
    Critical = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<TemplateStatus>))]
public enum TemplateStatus { Draft = 0, InReview = 1, Approved = 2, Active = 3, Retired = 4, Rejected = 5 }

[JsonConverter(typeof(JsonStringEnumConverter<BodyFormat>))]
public enum BodyFormat { Text = 0, Html = 1 }

[JsonConverter(typeof(JsonStringEnumConverter<AttemptOutcome>))]
public enum AttemptOutcome
{
    /// <summary>Provider definitely accepted the message.</summary>
    Accepted = 0,
    /// <summary>Provider definitely rejected it; retrying will not help (bad address, policy).</summary>
    PermanentFailure = 1,
    /// <summary>Provider definitely did not accept it; safe to retry or fail over.</summary>
    TransientFailure = 2,
    /// <summary>Unknown whether the provider accepted it (timeout / connection reset after send).</summary>
    Ambiguous = 3,
    /// <summary>Provider asked us to slow down; not counted as a failed attempt.</summary>
    Throttled = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderScope>))]
public enum ProviderScope { Shared = 0, Tenant = 1, Company = 2 }

[JsonConverter(typeof(JsonStringEnumConverter<DuplicateRiskPolicy>))]
public enum DuplicateRiskPolicy
{
    /// <summary>Never resend after an ambiguous outcome unless the provider deduplicates; hold for reconciliation/operator.</summary>
    Never = 0,
    /// <summary>Resend through the same provider only (provider-side idempotency key).</summary>
    SameProviderOnly = 1,
    /// <summary>Duplicates are acceptable (e.g. in-app/OTP); retry and fail over freely.</summary>
    AllowDuplicates = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderHealthStatus>))]
public enum ProviderHealthStatus { Unknown = 0, Healthy = 1, Degraded = 2, Unhealthy = 3, CircuitOpen = 4 }

[JsonConverter(typeof(JsonStringEnumConverter<CampaignStatus>))]
public enum CampaignStatus { Draft = 0, PendingApproval = 1, Approved = 2, Scheduled = 3, Running = 4, Paused = 5, Completed = 6, Cancelled = 7 }

[JsonConverter(typeof(JsonStringEnumConverter<ConsentStatus>))]
public enum ConsentStatus { Granted = 0, Withdrawn = 1 }

[JsonConverter(typeof(JsonStringEnumConverter<ConsentPurpose>))]
public enum ConsentPurpose
{
    /// <summary>Opt-in to receive business-initiated messages on a channel (required for WhatsApp).</summary>
    ChannelOptIn = 0,
    /// <summary>Consent to marketing/promotional communication.</summary>
    Marketing = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<SuppressionReason>))]
public enum SuppressionReason { HardBounce = 0, Complaint = 1, Unsubscribe = 2, InvalidAddress = 3, OptOut = 4, Manual = 5 }

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalStatus>))]
public enum ApprovalStatus { Pending = 0, Approved = 1, Rejected = 2, Cancelled = 3 }

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalKind>))]
public enum ApprovalKind { TemplateVersion = 0, Campaign = 1, ProviderCredentials = 2, AiNotification = 3 }

[JsonConverter(typeof(JsonStringEnumConverter<RequestSource>))]
public enum RequestSource { Api = 0, Batch = 1, Event = 2, Schedule = 3, Campaign = 4, Mcp = 5, Admin = 6, Escalation = 7, TestSend = 8 }

[JsonConverter(typeof(JsonStringEnumConverter<DevicePlatform>))]
public enum DevicePlatform { Android = 0, Ios = 1, Web = 2 }

[JsonConverter(typeof(JsonStringEnumConverter<ContactKind>))]
public enum ContactKind { User = 0, Customer = 1, Other = 2 }

[JsonConverter(typeof(JsonStringEnumConverter<InboundRoutingStatus>))]
public enum InboundRoutingStatus { Pending = 0, Routed = 1, NoSubscriber = 2, Failed = 3 }

/// <summary>Lower-case channel names on the wire, case-insensitive on input. Accepts "in_app", "in-app", "web_push".</summary>
public sealed class ChannelJsonConverter : JsonConverter<Channel>
{
    public override Channel Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(Channel), n))
            return (Channel)n;
        var s = reader.GetString();
        return ChannelNames.TryParse(s, out var c) ? c : throw new System.Text.Json.JsonException($"Unknown channel '{s}'.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Channel value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(ChannelNames.ToWire(value));
}

public static class ChannelNames
{
    public static string ToWire(Channel c) => c switch
    {
        Channel.Email => "email",
        Channel.Sms => "sms",
        Channel.WhatsApp => "whatsapp",
        Channel.InApp => "inapp",
        Channel.Push => "push",
        Channel.WebPush => "webpush",
        Channel.Webhook => "webhook",
        _ => c.ToString().ToLowerInvariant(),
    };

    public static bool TryParse(string? s, out Channel channel)
    {
        channel = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var norm = s.Replace("_", "").Replace("-", "").Trim().ToLowerInvariant();
        switch (norm)
        {
            case "email": channel = Channel.Email; return true;
            case "sms": channel = Channel.Sms; return true;
            case "whatsapp": channel = Channel.WhatsApp; return true;
            case "inapp": channel = Channel.InApp; return true;
            case "push": case "mobilepush": channel = Channel.Push; return true;
            case "webpush": case "browserpush": channel = Channel.WebPush; return true;
            case "webhook": channel = Channel.Webhook; return true;
            default: return false;
        }
    }
}
