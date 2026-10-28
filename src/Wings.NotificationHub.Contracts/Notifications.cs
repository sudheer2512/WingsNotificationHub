using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wings.NotificationHub.Contracts;

/// <summary>POST /api/v1/notifications/send. Channel-independent notification request.</summary>
public sealed class SendNotificationRequest
{
    /// <summary>Calling application. Optional for application credentials (taken from the token); validated against it when supplied.</summary>
    public Guid? ApplicationId { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>Registered notification type code, e.g. INVOICE_CREATED. Supplies category, message class and default template.</summary>
    public string? NotificationType { get; set; }
    /// <summary>Template code. When omitted the notification type's default template code is used.</summary>
    public string? TemplateCode { get; set; }
    /// <summary>Single recipient (shorthand for Recipients = [Recipient]).</summary>
    public RecipientDto? Recipient { get; set; }
    public List<RecipientDto>? Recipients { get; set; }
    /// <summary>Role/group/company-wide targeting; expanded against registered contacts.</summary>
    public AudienceDto? Audience { get; set; }
    /// <summary>Channels to deliver on. When omitted the notification type's default channels are used.</summary>
    public List<Channel>? Channels { get; set; }
    public Dictionary<string, JsonElement>? Variables { get; set; }
    public NotificationPriority? Priority { get; set; }
    public DateTimeOffset? ScheduleAt { get; set; }
    /// <summary>Do not deliver after this instant; pending deliveries become Expired.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CorrelationId { get; set; }
    /// <summary>Optional per-channel fallback, e.g. whatsapp → sms when WhatsApp fails or is suppressed.</summary>
    public List<FallbackDto>? Fallbacks { get; set; }
    public EscalationDto? Escalation { get; set; }
    public RetryPolicyDto? RetryPolicy { get; set; }
    /// <summary>In-app / push specific options (deep link, actions, image).</summary>
    public AppNotificationOptionsDto? App { get; set; }
    public List<AttachmentRefDto>? Attachments { get; set; }
    /// <summary>Free-form metadata echoed back in status and webhook events.</summary>
    public Dictionary<string, string>? Metadata { get; set; }
    /// <summary>Validate, resolve and render without persisting or delivering anything.</summary>
    public bool DryRun { get; set; }
}

public sealed class RecipientDto
{
    public Guid? ContactId { get; set; }
    /// <summary>Application user id (in-app/push target and preference owner).</summary>
    public string? UserId { get; set; }
    /// <summary>Customer id in the calling application.</summary>
    public string? CustomerId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    /// <summary>WhatsApp number when different from Mobile.</summary>
    public string? WhatsApp { get; set; }
    public string? TimeZone { get; set; }
    public string? Locale { get; set; }
    /// <summary>Restrict push to these device registrations.</summary>
    public List<Guid>? DeviceIds { get; set; }
    public List<string>? Cc { get; set; }
    public List<string>? Bcc { get; set; }
}

public sealed class AudienceDto
{
    public List<string>? Roles { get; set; }
    public List<Guid>? GroupIds { get; set; }
    public List<string>? UserIds { get; set; }
    /// <summary>Every registered user contact in the company (announcements).</summary>
    public bool AllCompanyUsers { get; set; }
}

public sealed class FallbackDto
{
    public Channel From { get; set; }
    public Channel To { get; set; }
    public string? TemplateCode { get; set; }
    /// <summary>Statuses that trigger the fallback. Default: Failed, Suppressed, Expired.</summary>
    public List<DeliveryStatus>? OnStatuses { get; set; }
}

public sealed class EscalationDto
{
    /// <summary>Escalate when the original delivery has not reached <see cref="UnlessStatus"/> within this many minutes.</summary>
    public int AfterMinutes { get; set; } = 30;
    public DeliveryStatus UnlessStatus { get; set; } = DeliveryStatus.Read;
    public Channel Channel { get; set; }
    public string? TemplateCode { get; set; }
    /// <summary>Escalate to a different recipient (e.g. a manager). Defaults to the original recipient.</summary>
    public RecipientDto? Recipient { get; set; }
}

public sealed class RetryPolicyDto
{
    public int? MaxAttempts { get; set; }
    public int? InitialDelaySeconds { get; set; }
    public int? MaxDelaySeconds { get; set; }
}

public sealed class AppNotificationOptionsDto
{
    public string? DeepLink { get; set; }
    public string? ImageUrl { get; set; }
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public List<AppActionDto>? Actions { get; set; }
    /// <summary>Push time-to-live in seconds.</summary>
    public int? TimeToLiveSeconds { get; set; }
    public JsonObject? Data { get; set; }
}

public sealed class AppActionDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? DeepLink { get; set; }
}

public sealed class AttachmentRefDto
{
    /// <summary>Id returned by POST /api/v1/attachments.</summary>
    public Guid AttachmentId { get; set; }
    /// <summary>Embed as an inline image referenced by cid:{ContentId}.</summary>
    public bool Inline { get; set; }
    public string? ContentId { get; set; }
}

public sealed class SendNotificationResponse
{
    public Guid NotificationId { get; set; }
    public string Status { get; set; } = "accepted";
    public string TrackingUrl { get; set; } = "";
    /// <summary>True when an earlier request with the same idempotency key was returned.</summary>
    public bool Duplicate { get; set; }
    public List<DeliverySummaryDto> Deliveries { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    /// <summary>Only for dry runs: rendered content per delivery.</summary>
    public List<RenderedPreviewDto>? Preview { get; set; }
}

public sealed class DeliverySummaryDto
{
    public Guid DeliveryId { get; set; }
    public Channel Channel { get; set; }
    public string Recipient { get; set; } = "";
    public DeliveryStatus Status { get; set; }
    public string? Reason { get; set; }
}

public sealed class RenderedPreviewDto
{
    public Channel Channel { get; set; }
    public string Recipient { get; set; } = "";
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public string? TextBody { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public string? PolicyDecision { get; set; }
    public DateTimeOffset? SendAt { get; set; }
}

public sealed class BatchSendRequest
{
    public List<SendNotificationRequest> Notifications { get; set; } = [];
}

public sealed class BatchSendResponse
{
    public List<BatchItemResult> Results { get; set; } = [];
    public int Accepted { get; set; }
    public int Rejected { get; set; }
}

public sealed class BatchItemResult
{
    public int Index { get; set; }
    public Guid? NotificationId { get; set; }
    public string Status { get; set; } = "";
    public bool Duplicate { get; set; }
    public ApiError? Error { get; set; }
}

public sealed class NotificationStatusDto
{
    public Guid NotificationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public string? NotificationType { get; set; }
    public string? TemplateCode { get; set; }
    public RequestStatus Status { get; set; }
    public RequestSource Source { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ScheduleAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public List<DeliveryDto> Deliveries { get; set; } = [];
}

public sealed class DeliveryDto
{
    public Guid DeliveryId { get; set; }
    public Channel Channel { get; set; }
    /// <summary>Masked destination address.</summary>
    public string Recipient { get; set; } = "";
    public DeliveryStatus Status { get; set; }
    public string? Provider { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderMessageId { get; set; }
    public int Attempts { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public decimal? Cost { get; set; }
    public string? Currency { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public int? TemplateVersion { get; set; }
    public Guid? FallbackOfDeliveryId { get; set; }
    public Guid? EscalationOfDeliveryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
}

public sealed class DeliveryTimelineDto
{
    public Guid NotificationId { get; set; }
    public List<DeliveryDto> Deliveries { get; set; } = [];
    public List<DeliveryAttemptDto> Attempts { get; set; } = [];
    public List<DeliveryEventDto> Events { get; set; } = [];
}

public sealed class DeliveryAttemptDto
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public int AttemptNumber { get; set; }
    public string? Provider { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public AttemptOutcome? Outcome { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ResponseCode { get; set; }
    /// <summary>Sanitized provider response (secrets and full message bodies removed).</summary>
    public string? ResponseSummary { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? DurationMs { get; set; }
    public decimal? Cost { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class DeliveryEventDto
{
    public Guid Id { get; set; }
    public Guid? DeliveryId { get; set; }
    public string Type { get; set; } = "";
    public DeliveryStatus? Status { get; set; }
    public string Source { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

public sealed class ApiError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, string[]>? Errors { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>POST /api/v1/events — a business event evaluated by the notification rule engine.</summary>
public sealed class BusinessEventRequest
{
    public Guid? ApplicationId { get; set; }
    public Guid CompanyId { get; set; }
    public string EventType { get; set; } = "";
    public JsonObject Payload { get; set; } = new();
    public string? IdempotencyKey { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
}

public sealed class BusinessEventResponse
{
    public Guid EventId { get; set; }
    public string Status { get; set; } = "accepted";
    public bool Duplicate { get; set; }
}

public sealed class TemplatePreviewRequest
{
    public Guid? TemplateId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public string? TemplateCode { get; set; }
    public Channel? Channel { get; set; }
    public string? Locale { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    /// <summary>Ad-hoc content to render (editor preview before saving).</summary>
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public BodyFormat? BodyFormat { get; set; }
    public Dictionary<string, JsonElement>? Variables { get; set; }
    /// <summary>Use the template's sample values for variables that are not supplied.</summary>
    public bool UseSampleData { get; set; } = true;
}

public sealed class TemplatePreviewResponse
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public string? TextBody { get; set; }
    public List<string> MissingVariables { get; set; } = [];
    public List<string> UnknownVariables { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public SmsInfoDto? Sms { get; set; }
}

public sealed class SmsInfoDto
{
    public string Encoding { get; set; } = "GSM-7";
    public int Characters { get; set; }
    public int Segments { get; set; }
}

public sealed class TemplateSummaryDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public Channel Channel { get; set; }
    public string Locale { get; set; } = "en";
    public TemplateStatus Status { get; set; }
    public int? ActiveVersion { get; set; }
    public int LatestVersion { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public List<TemplateVariableDto> Variables { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TemplateVariableDto
{
    public string Name { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? SampleValue { get; set; }
    public string? Description { get; set; }
}

public sealed class RegisterDeviceRequest
{
    public Guid? ApplicationId { get; set; }
    public Guid CompanyId { get; set; }
    public string UserId { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    /// <summary>FCM/APNs registration token, or the Web Push endpoint URL.</summary>
    public string Token { get; set; } = "";
    /// <summary>Web Push subscription keys.</summary>
    public string? P256dh { get; set; }
    public string? Auth { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public string? Locale { get; set; }
    /// <summary>Force a specific provider ("fcm", "apns", "webpush"). Defaults by platform.</summary>
    public string? Provider { get; set; }
}

public sealed class DeviceDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    public string Provider { get; set; } = "";
    public string TokenPreview { get; set; } = "";
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
    public string? InvalidReason { get; set; }
}

public sealed class ContactUpsertRequest
{
    public Guid CompanyId { get; set; }
    public ContactKind Kind { get; set; } = ContactKind.User;
    public string ExternalId { get; set; } = "";
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string? WhatsApp { get; set; }
    public string? TimeZone { get; set; }
    public string? Locale { get; set; }
    public string? Country { get; set; }
    public List<string>? Roles { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }
}

public sealed class PreferenceItemDto
{
    /// <summary>Notification category (e.g. "Invoice") or "*" for all categories.</summary>
    public string Category { get; set; } = "*";
    public Channel Channel { get; set; }
    public bool Enabled { get; set; }
}

public sealed class PreferencesDto
{
    public Guid ContactId { get; set; }
    public string ExternalId { get; set; } = "";
    public List<PreferenceItemDto> Preferences { get; set; } = [];
    public List<ConsentDto> Consents { get; set; } = [];
}

public sealed class ConsentRequest
{
    public Guid CompanyId { get; set; }
    public string ExternalId { get; set; } = "";
    public Channel Channel { get; set; }
    public ConsentPurpose Purpose { get; set; }
    public ConsentStatus Status { get; set; }
    /// <summary>Where consent was captured: web-form, whatsapp-reply, import, crm …</summary>
    public string Source { get; set; } = "";
    /// <summary>Evidence reference: form submission id, message id, document link.</summary>
    public string? Evidence { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
}

public sealed class ConsentDto
{
    public Guid Id { get; set; }
    public Channel Channel { get; set; }
    public ConsentPurpose Purpose { get; set; }
    public ConsentStatus Status { get; set; }
    public string Source { get; set; } = "";
    public string? Evidence { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed class InAppMessageDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Category { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? DeepLink { get; set; }
    public string? ImageUrl { get; set; }
    public List<AppActionDto> Actions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? ActionTaken { get; set; }
}

public sealed class InboxTokenRequest
{
    public Guid? ApplicationId { get; set; }
    public Guid CompanyId { get; set; }
    public string UserId { get; set; } = "";
    public int? LifetimeMinutes { get; set; }
}

public sealed class InboxTokenResponse
{
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string HubUrl { get; set; } = "/hubs/inapp";
}

public sealed class TokenResponse
{
    public string Access_token { get; set; } = "";
    public string Token_type { get; set; } = "Bearer";
    public int Expires_in { get; set; }
    public string? Scope { get; set; }
}

/// <summary>Payload of outbound webhook events (delivery.status_changed, inbound.message_received, …).</summary>
public sealed class WebhookEventEnvelope
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public JsonObject Data { get; set; } = new();
}

public static class WebhookEventTypes
{
    public const string DeliveryStatusChanged = "delivery.status_changed";
    public const string NotificationCompleted = "notification.completed";
    public const string InboundMessageReceived = "inbound.message_received";
    public const string PushInteraction = "push.interaction";
    public const string InAppAction = "inapp.action";
    /// <summary>Delivery of a notification on the Webhook channel.</summary>
    public const string NotificationWebhook = "notification.webhook";
    public static readonly string[] All = [DeliveryStatusChanged, NotificationCompleted, InboundMessageReceived, PushInteraction, InAppAction, NotificationWebhook];
}

public static class Scopes
{
    public const string NotificationsSend = "notifications.send";
    public const string NotificationsRead = "notifications.read";
    public const string NotificationsBatch = "notifications.batch";
    public const string EventsPublish = "events.publish";
    public const string TemplatesRead = "templates.read";
    public const string TemplatesWrite = "templates.write";
    public const string ContactsWrite = "contacts.write";
    public const string DevicesWrite = "devices.write";
    public const string PreferencesRead = "preferences.read";
    public const string PreferencesWrite = "preferences.write";
    public const string InAppToken = "inapp.token";
    public const string UsageRead = "usage.read";
    public const string ProvidersHealth = "providers.health";
    public const string WebhooksManage = "webhooks.manage";
    public const string Mcp = "mcp";
    public const string ApplicationsRegister = "applications.register";
    /// <summary>Scope carried by end-user inbox tokens.</summary>
    public const string InAppUser = "inapp.user";

    public static readonly string[] ApplicationDefaults =
    [
        NotificationsSend, NotificationsRead, NotificationsBatch, EventsPublish, TemplatesRead, ContactsWrite, DevicesWrite,
        PreferencesRead, PreferencesWrite, InAppToken, UsageRead, ProvidersHealth, WebhooksManage,
    ];

    public static readonly string[] All =
    [
        NotificationsSend, NotificationsRead, NotificationsBatch, EventsPublish, TemplatesRead, TemplatesWrite, ContactsWrite, DevicesWrite,
        PreferencesRead, PreferencesWrite, InAppToken, UsageRead, ProvidersHealth, WebhooksManage, Mcp, ApplicationsRegister,
    ];
}
