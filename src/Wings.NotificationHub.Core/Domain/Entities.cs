using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Core.Domain;

/// <summary>Entities carrying a tenant boundary. Every query on these must be filtered by the caller's access scope.</summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}

/// <summary>Entities with an optimistic-concurrency stamp refreshed on every update.</summary>
public interface IVersioned
{
    Guid ConcurrencyStamp { get; set; }
}

public enum EntityStatus { Active = 0, Suspended = 1, Disabled = 2 }

// ───────────────────────────── Tenancy ─────────────────────────────

public sealed class Tenant : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public EntityStatus Status { get; set; }
    public string? SuspendedReason { get; set; }
    /// <summary>May this tenant's companies route through Wings-managed shared providers.</summary>
    public bool AllowSharedProviders { get; set; } = true;
    /// <summary>Promotional messages need an explicit Marketing consent record.</summary>
    public bool RequireMarketingConsent { get; set; } = true;
    public string DefaultTimeZone { get; set; } = "Asia/Kolkata";
    /// <summary>White-label branding (logo, colours, inbox title, email footer) as JSON.</summary>
    public string? BrandingJson { get; set; }
    /// <summary>Rendered message bodies are purged after this many days (metadata retained).</summary>
    public int BodyRetentionDays { get; set; } = 90;
    /// <summary>Delivery records are deleted after this many days.</summary>
    public int DataRetentionDays { get; set; } = 730;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public sealed class Company : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Country { get; set; } = "IN";
    public string? TimeZone { get; set; }
    public EntityStatus Status { get; set; }
    public string? SuspendedReason { get; set; }
    public string? BrandingJson { get; set; }
    /// <summary>Quiet hours as JSON <see cref="Policies.QuietHoursPolicy"/>.</summary>
    public string? QuietHoursJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public sealed class Application : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public EntityStatus Status { get; set; }
    public string? SuspendedReason { get; set; }
    /// <summary>Sandbox applications only ever reach mock providers.</summary>
    public bool Sandbox { get; set; }
    public List<Channel> AllowedChannels { get; set; } = [];
    public string DefaultLocale { get; set; } = "en";
    /// <summary>API calls per minute per credential.</summary>
    public int RateLimitPerMinute { get; set; } = 600;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>Which companies may use which applications.</summary>
public sealed class CompanyApplication : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Per-company default sender for each channel, JSON {"email":"guid",…}.</summary>
    public string? DefaultSendersJson { get; set; }
}

public sealed class ApplicationCredential : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Null for tenant-level provisioning credentials (Wings Code registration).</summary>
    public Guid? ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    /// <summary>Companies this credential may act for. Empty = every company linked to the application.</summary>
    public List<Guid> AllowedCompanyIds { get; set; } = [];
    /// <summary>Credential used by a Wings AI agent (MCP). Subject to AI restrictions.</summary>
    public bool IsAiAgent { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Identity / RBAC ─────────────────────────────

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? PasswordHash { get; set; }
    /// <summary>"iss|sub" of an external OpenID Connect identity.</summary>
    public string? ExternalSubject { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// <summary>A role granted to a user, optionally narrowed to a tenant/company/application/channel.</summary>
public sealed class RoleAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Role { get; set; } = "";
    public Guid? TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Channel? Channel { get; set; }
}

public sealed class ApprovalRequest : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public ApprovalKind Kind { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Change payload (encrypted when it carries secrets).</summary>
    public string? PayloadJson { get; set; }
    public bool PayloadEncrypted { get; set; }
    public ApprovalStatus Status { get; set; }
    public string RequestedBy { get; set; } = "";
    public string? RequestedByName { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string? ActorName { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Summary { get; set; }
    /// <summary>Redacted detail (never secrets or message bodies).</summary>
    public string? DetailJson { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Notification types & templates ─────────────────────────────

public sealed class NotificationType : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Preference category, e.g. "Invoice", "Payment", "Security".</summary>
    public string Category { get; set; } = "General";
    public MessageClass MessageClass { get; set; }
    public List<Channel> DefaultChannels { get; set; } = [];
    public string? DefaultTemplateCode { get; set; }
    public NotificationPriority DefaultPriority { get; set; } = NotificationPriority.Normal;
    /// <summary>Transactional types that bypass user channel preferences (still honour hard suppressions).</summary>
    public bool Mandatory { get; set; }
    public bool AiAllowed { get; set; }
    public bool RequiresHumanApprovalForAi { get; set; } = true;
    /// <summary>Sensitive types (OTPs, payroll) are blocked for AI agents and have bodies redacted in the UI.</summary>
    public bool IsSensitive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public sealed class NotificationTemplate : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public Channel Channel { get; set; }
    public string Locale { get; set; } = "en";
    public TemplateStatus Status { get; set; }
    public Guid? ActiveVersionId { get; set; }
    public int? ActiveVersionNumber { get; set; }
    public int LatestVersionNumber { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<TemplateVersion> Versions { get; set; } = [];
}

/// <summary>Immutable once submitted for review. Deliveries reference the exact version they were rendered from.</summary>
public sealed class TemplateVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public int VersionNumber { get; set; }
    public TemplateStatus Status { get; set; }
    public string? Subject { get; set; }
    public string Body { get; set; } = "";
    public BodyFormat BodyFormat { get; set; }
    /// <summary>Optional plain-text alternative for HTML email.</summary>
    public string? TextBody { get; set; }
    /// <summary>Channel-specific structure: WhatsApp components, push title/data, in-app title — JSON.</summary>
    public string? ComponentsJson { get; set; }
    /// <summary>Approved WhatsApp template name/language at Meta, when channel = WhatsApp.</summary>
    public string? ProviderTemplateName { get; set; }
    public string? ProviderTemplateLanguage { get; set; }
    public string? ProviderCategory { get; set; }
    public string? ProviderApprovalStatus { get; set; }
    /// <summary>India DLT registered content template id (SMS).</summary>
    public string? DltTemplateId { get; set; }
    public string Checksum { get; set; } = "";
    public string? ChangeNote { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionReason { get; set; }
    public List<TemplateVariable> Variables { get; set; } = [];
}

public sealed class TemplateVariable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateVersionId { get; set; }
    public string Name { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? SampleValue { get; set; }
    public string? Description { get; set; }
    public int? MaxLength { get; set; }
}

// ───────────────────────────── Rules ─────────────────────────────

public sealed class NotificationRule : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string EventType { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    /// <summary>Stop evaluating lower-order rules once this one matches.</summary>
    public bool StopProcessing { get; set; }
    public string? NotificationTypeCode { get; set; }
    /// <summary>JSON <see cref="Rules.RecipientSelector"/>.</summary>
    public string RecipientSelectorJson { get; set; } = "{}";
    /// <summary>JSON list of <see cref="Rules.RuleChannelAction"/>.</summary>
    public string ActionsJson { get; set; } = "[]";
    public NotificationPriority? Priority { get; set; }
    public int DelaySeconds { get; set; }
    public string? EscalationJson { get; set; }
    public string? RetryPolicyJson { get; set; }
    public string? QuietHoursJson { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<RuleCondition> Conditions { get; set; } = [];
}

/// <summary>A node in a rule's condition tree. Group nodes carry Operator And/Or; leaf nodes carry Field/Comparison/Value.</summary>
public sealed class RuleCondition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public Guid? ParentId { get; set; }
    public int Order { get; set; }
    /// <summary>"and" / "or" for groups; null for leaves.</summary>
    public string? GroupOperator { get; set; }
    public string? Field { get; set; }
    public string? Comparison { get; set; }
    public string? ValueJson { get; set; }
}

public sealed class BusinessEvent : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? IdempotencyKey { get; set; }
    public string? CorrelationId { get; set; }
    public string ActorId { get; set; } = "";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public int MatchedRules { get; set; }
    public int NotificationsCreated { get; set; }
    public string? ProcessingError { get; set; }
}

// ───────────────────────────── Requests & deliveries ─────────────────────────────

public sealed class NotificationRequest : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public RequestSource Source { get; set; }
    /// <summary>Authenticated identity: credential id or admin user id.</summary>
    public string ActorId { get; set; } = "";
    public string? ActorName { get; set; }
    public Guid? BusinessEventId { get; set; }
    public Guid? RuleId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? ScheduleId { get; set; }
    public string? NotificationTypeCode { get; set; }
    public string? TemplateCode { get; set; }
    public string? Category { get; set; }
    public MessageClass MessageClass { get; set; }
    public NotificationPriority Priority { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime? ScheduleAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? IdempotencyKey { get; set; }
    /// <summary>SHA-256 of the canonical request, to detect idempotency-key reuse with a different payload.</summary>
    public string? RequestHash { get; set; }
    public string? CorrelationId { get; set; }
    /// <summary>W3C trace id of the API request that created it.</summary>
    public string? TraceId { get; set; }
    public string? MetadataJson { get; set; }
    /// <summary>Encrypted variables (needed for fallback/escalation rendering).</summary>
    public string? VariablesCipher { get; set; }
    public string? RetryPolicyJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<NotificationRecipient> Recipients { get; set; } = [];
    public List<NotificationDelivery> Deliveries { get; set; } = [];
}

public sealed class NotificationRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid? ContactId { get; set; }
    public string? UserId { get; set; }
    public string? CustomerId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string? WhatsApp { get; set; }
    public string? TimeZone { get; set; }
    public string? Locale { get; set; }
}

public sealed class NotificationDelivery : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid RecipientId { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? CampaignId { get; set; }
    public Channel Channel { get; set; }
    /// <summary>Destination: email, E.164 number, user id, device registration id or webhook subscription id.</summary>
    public string Address { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public int? TemplateVersionNumber { get; set; }
    public MessageClass MessageClass { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? Category { get; set; }
    /// <summary>Encrypted rendered content (<see cref="Messaging.RenderedContent"/>). Purged after the body retention period.</summary>
    public string? ContentCipher { get; set; }
    public DateTime? ContentPurgedAt { get; set; }
    public DeliveryStatus Status { get; set; }
    public string? StatusReason { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderType { get; set; }
    public Guid? SenderIdentityId { get; set; }
    public string? ProviderMessageId { get; set; }
    /// <summary>After an ambiguous submission, further attempts may only use this account (no cross-provider duplicates).</summary>
    public Guid? PinnedProviderAccountId { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Last attempt outcome was ambiguous and no safe automatic resend exists.</summary>
    public bool AwaitingOperator { get; set; }
    public bool DeadLettered { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public Guid? FallbackOfDeliveryId { get; set; }
    public Guid? EscalationOfDeliveryId { get; set; }
    public string? FallbackJson { get; set; }
    public string? EscalationJson { get; set; }
    public DateTime? EscalateAt { get; set; }
    public bool FollowUpProcessed { get; set; }
    public Guid? ThreadId { get; set; }
    public int Units { get; set; } = 1;
    public decimal? Cost { get; set; }
    public decimal? EstimatedCost { get; set; }
    public string? Currency { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>Immutable record of one submission to a provider. Retries add rows; nothing is overwritten.</summary>
public sealed class DeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliveryId { get; set; }
    public Guid TenantId { get; set; }
    public int AttemptNumber { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderType { get; set; }
    public string? WorkerId { get; set; }
    /// <summary>Idempotency token sent to providers that support it (stable across retries of one delivery).</summary>
    public string? IdempotencyToken { get; set; }
    public AttemptOutcome? Outcome { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ResponseCode { get; set; }
    public string? ResponseSummary { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? DurationMs { get; set; }
    public decimal? Cost { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class DeliveryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null while a provider callback is waiting for its delivery to be recorded (out-of-order arrival).</summary>
    public Guid? DeliveryId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderEventId { get; set; }
    public string Type { get; set; } = "";
    public DeliveryStatus? Status { get; set; }
    public string Source { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>Whether the event changed the delivery's status (false for regressions/duplicates).</summary>
    public bool Applied { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Providers ─────────────────────────────

public sealed class ProviderAccount : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Channel Channel { get; set; }
    /// <summary>Adapter key: mock, smtp, ses, m365, twilio, msg91, http-sms, meta-whatsapp, 360dialog, fcm, apns, webpush, inapp, webhook.</summary>
    public string ProviderType { get; set; } = "";
    public ProviderScope Scope { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>Non-secret settings (host, region, base URL, sender defaults).</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>AES-GCM encrypted JSON of secret settings. Never returned by the API.</summary>
    public string? SecretsCipher { get; set; }
    /// <summary>Names of configured secret keys (for masked display).</summary>
    public List<string> SecretKeys { get; set; } = [];
    /// <summary>Encrypted secret used to verify provider callbacks.</summary>
    public string? CallbackSecretCipher { get; set; }
    public int Priority { get; set; } = 100;
    public double? RateLimitPerSecond { get; set; }
    public int? MonthlyCapacity { get; set; }
    /// <summary>E.164 prefixes this provider may deliver to (empty = any).</summary>
    public List<string> AllowedCountryPrefixes { get; set; } = [];
    public decimal? UnitCost { get; set; }
    public string Currency { get; set; } = "INR";
    public Guid? FailoverProviderAccountId { get; set; }
    public DuplicateRiskPolicy DuplicateRiskPolicy { get; set; } = DuplicateRiskPolicy.Never;
    public int TimeoutSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public bool Suspended { get; set; }
    public string? SuspendedReason { get; set; }
    public ProviderHealthStatus HealthStatus { get; set; }
    public DateTime? LastHealthCheckAt { get; set; }
    public string? LastHealthDetail { get; set; }
    public Guid? PendingApprovalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public sealed class SenderIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderAccountId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Channel Channel { get; set; }
    /// <summary>From address, SMS sender id/header, WhatsApp display number.</summary>
    public string Address { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? ReplyTo { get; set; }
    /// <summary>HTML signature appended to email bodies.</summary>
    public string? Signature { get; set; }
    public string? DltEntityId { get; set; }
    public string? WhatsAppPhoneNumberId { get; set; }
    public string? WhatsAppBusinessAccountId { get; set; }
    public bool IsDefault { get; set; }
    public bool Suspended { get; set; }
    public string VerificationStatus { get; set; } = "unverified";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ProviderRoutingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Channel Channel { get; set; }
    public string? CountryPrefix { get; set; }
    public MessageClass? MessageClass { get; set; }
    public Guid ProviderAccountId { get; set; }
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ProviderHealthEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderAccountId { get; set; }
    public ProviderHealthStatus Status { get; set; }
    public int? LatencyMs { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Recipients ─────────────────────────────

public sealed class RecipientContact : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public ContactKind Kind { get; set; }
    /// <summary>User id or customer id in the source application.</summary>
    public string ExternalId { get; set; } = "";
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string? WhatsApp { get; set; }
    public string? TimeZone { get; set; }
    public string? Locale { get; set; }
    public string? Country { get; set; }
    public List<string> Roles { get; set; } = [];
    public string? AttributesJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ErasedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public sealed class RecipientPreference : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ContactId { get; set; }
    public string Category { get; set; } = "*";
    public Channel Channel { get; set; }
    public bool Enabled { get; set; }
    public string? Source { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Append-only consent evidence. The latest row per (contact, channel, purpose) is the current state.</summary>
public sealed class RecipientConsent : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ContactId { get; set; }
    public Channel Channel { get; set; }
    public ConsentPurpose Purpose { get; set; }
    public ConsentStatus Status { get; set; }
    public string Source { get; set; } = "";
    public string? Evidence { get; set; }
    public string? Scope { get; set; }
    public string? IpAddress { get; set; }
    public string RecordedBy { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Suppression : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Null = tenant-wide.</summary>
    public Guid? CompanyId { get; set; }
    public Channel Channel { get; set; }
    /// <summary>Normalized address (lower-case email or E.164).</summary>
    public string Address { get; set; } = "";
    public SuppressionReason Reason { get; set; }
    /// <summary>Only blocks Promotional messages (unsubscribe), otherwise all non-critical / all.</summary>
    public bool PromotionalOnly { get; set; }
    public string? Source { get; set; }
    /// <summary>Why an operator added it.</summary>
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}

public sealed class RecipientGroup : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Dynamic groups evaluate this filter (JSON condition tree over contact fields) instead of static members.</summary>
    public bool Dynamic { get; set; }
    public string? FilterJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RecipientGroupMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Guid ContactId { get; set; }
}

public sealed class DeviceRegistration : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? ContactId { get; set; }
    public string UserId { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    /// <summary>fcm, apns or webpush.</summary>
    public string Provider { get; set; } = "";
    public string Token { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? P256dh { get; set; }
    public string? Auth { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public string? Locale { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? InvalidatedAt { get; set; }
    public string? InvalidReason { get; set; }
}

public sealed class InAppMessage : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid DeliveryId { get; set; }
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Category { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? DeepLink { get; set; }
    public string? ImageUrl { get; set; }
    public string? ActionsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime? DismissedAt { get; set; }
    public string? ActionTaken { get; set; }
    public DateTime? ActedAt { get; set; }
}

// ───────────────────────────── Scheduling & campaigns ─────────────────────────────

public sealed class NotificationSchedule : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Cron expression (5 fields) evaluated in <see cref="TimeZone"/>. Null for reminder sequences.</summary>
    public string? CronExpression { get; set; }
    /// <summary>Reminder sequence offsets in minutes from <see cref="StartAt"/>, JSON int list.</summary>
    public string? SequenceOffsetsJson { get; set; }
    public int SequenceIndex { get; set; }
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public DateTime StartAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public int RunCount { get; set; }
    public int? MaxRuns { get; set; }
    public bool WorkingDaysOnly { get; set; }
    public Guid? HolidayCalendarId { get; set; }
    /// <summary>The <c>SendNotificationRequest</c> to issue on each run (JSON).</summary>
    public string RequestJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public string CreatedBy { get; set; } = "";
    public string? CreatedByCredential { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public sealed class HolidayCalendar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public List<HolidayDate> Dates { get; set; } = [];
}

public sealed class HolidayDate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CalendarId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Campaign : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<Channel> Channels { get; set; } = [];
    public string TemplateCode { get; set; } = "";
    public string? NotificationTypeCode { get; set; }
    public MessageClass MessageClass { get; set; } = MessageClass.Promotional;
    public CampaignStatus Status { get; set; }
    /// <summary>Recipient groups whose members are added at launch (in addition to imported recipients).</summary>
    public List<Guid> GroupIds { get; set; } = [];
    /// <summary>Segment filter (condition tree over contact fields) applied at launch.</summary>
    public string? SegmentJson { get; set; }
    public string? VariablesJson { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public int ThrottlePerMinute { get; set; } = 600;
    public bool Suspended { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? CreatedByName { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int TotalRecipients { get; set; }
    public decimal? EstimatedCost { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public enum CampaignRecipientStatus { Pending = 0, Queued = 1, Skipped = 2, Failed = 3 }

public sealed class CampaignRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid? ContactId { get; set; }
    public string? ExternalId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string? VariablesJson { get; set; }
    public CampaignRecipientStatus Status { get; set; }
    public Guid? NotificationRequestId { get; set; }
    public string? Error { get; set; }
    public int RowNumber { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

// ───────────────────────────── Inbound ─────────────────────────────

public sealed class ConversationThread : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Channel Channel { get; set; }
    public Guid? ProviderAccountId { get; set; }
    /// <summary>The customer's address (E.164 / email).</summary>
    public string ExternalAddress { get; set; } = "";
    /// <summary>Our sender address (WhatsApp number / SMS id / mailbox).</summary>
    public string? SenderAddress { get; set; }
    public Guid? ContactId { get; set; }
    public string? ContactName { get; set; }
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    /// <summary>Last customer-initiated message: opens WhatsApp's 24-hour customer-service window.</summary>
    public DateTime? LastInboundAt { get; set; }
    public int UnreadCount { get; set; }
    public bool Closed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InboundMessage : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Channel Channel { get; set; }
    public Guid ProviderAccountId { get; set; }
    public Guid ThreadId { get; set; }
    public string From { get; set; } = "";
    public string? To { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public string MessageType { get; set; } = "text";
    public string? ProviderMessageId { get; set; }
    public string? InReplyToProviderMessageId { get; set; }
    public Guid? CorrelatedDeliveryId { get; set; }
    public Guid? CorrelatedRequestId { get; set; }
    public Guid? ContactId { get; set; }
    public InboundRoutingStatus RoutingStatus { get; set; }
    public string? RoutingDetail { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public sealed class MessageAttachment : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>"inbound" or "outbound".</summary>
    public string Direction { get; set; } = "outbound";
    public Guid? InboundMessageId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Opaque storage key inside the attachment store (never a user-controlled path).</summary>
    public string StorageKey { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Webhooks ─────────────────────────────

public sealed class WebhookSubscription : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> EventTypes { get; set; } = [];
    public string SecretCipher { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int ConsecutiveFailures { get; set; }
    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class WebhookDeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public int AttemptNumber { get; set; }
    public bool Success { get; set; }
    public int? ResponseCode { get; set; }
    public string? Error { get; set; }
    public int? DurationMs { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Usage, billing, quotas ─────────────────────────────

public sealed class UsageRecord : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Channel Channel { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid? CampaignId { get; set; }
    public MessageClass MessageClass { get; set; }
    /// <summary>yyyyMM.</summary>
    public int Period { get; set; }
    public int Units { get; set; } = 1;
    public decimal EstimatedCost { get; set; }
    public decimal? ProviderReportedCost { get; set; }
    public decimal BilledAmount { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public sealed class CostRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProviderAccountId { get; set; }
    public Channel Channel { get; set; }
    public string? CountryPrefix { get; set; }
    public MessageClass? MessageClass { get; set; }
    public decimal UnitCost { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
}

/// <summary>Customer billing rate: fixed unit price, or provider cost plus margin.</summary>
public sealed class BillingRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Channel Channel { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal MarginPercent { get; set; }
    public string Currency { get; set; } = "INR";
}

public sealed class TenantQuota : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Channel? Channel { get; set; }
    public int MonthlyLimit { get; set; }
    public int AlertThresholdPercent { get; set; } = 80;
}

public sealed class CreditAccount : ITenantScoped, IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public decimal Balance { get; set; }
    public decimal Reserved { get; set; }
    public decimal LowBalanceThreshold { get; set; }
    public string Currency { get; set; } = "INR";
    public bool Enabled { get; set; } = true;
    public DateTime? LowBalanceAlertedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public enum CreditTransactionKind { TopUp = 0, Reserve = 1, Consume = 2, Release = 3, Adjust = 4 }

public sealed class CreditTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public CreditTransactionKind Kind { get; set; }
    public decimal Amount { get; set; }
    public Guid? DeliveryId { get; set; }
    public string? Note { get; set; }
    public string? Actor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────────── Messaging infrastructure ─────────────────────────────

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public Guid? TenantId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Durable queue table used by the database message transport (competing consumers via leases).</summary>
public sealed class QueueMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Queue { get; set; } = "";
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public Guid MessageId { get; set; }
    /// <summary>Copied from the outbox row. Null means no tenant is known, so only platform-wide grants reach the message.</summary>
    public Guid? TenantId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime VisibleAt { get; set; } = DateTime.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public int DequeueCount { get; set; }
    public DateTime? DeadLetteredAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Records processed message keys per consumer, making at-least-once delivery effectively-once.</summary>
public sealed class InboxDeduplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Consumer { get; set; } = "";
    public string MessageKey { get; set; } = "";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SystemAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string Severity { get; set; } = "warning";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Key { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
}

public sealed class WorkerHeartbeat
{
    public string Id { get; set; } = "";
    public string Worker { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Status { get; set; } = "running";
    public long Processed { get; set; }
    public long Failed { get; set; }
    public string? LastError { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
}
