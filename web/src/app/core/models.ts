// Wire shapes of the Wings Notification Hub API (camelCase JSON, string enums, lower-case channels).
// The endpoint files under src/Wings.NotificationHub.Api/Endpoints are the source of truth.

export type Channel = 'email' | 'sms' | 'whatsapp' | 'inapp' | 'push' | 'webpush' | 'webhook';
export const CHANNELS: Channel[] = ['email', 'sms', 'whatsapp', 'inapp', 'push', 'webpush', 'webhook'];

export type DeliveryStatus = 'Draft' | 'Scheduled' | 'Queued' | 'Processing' | 'Submitted' | 'ProviderAccepted' | 'Sent' | 'Delivered' | 'Read'
  | 'Failed' | 'RetryPending' | 'Cancelled' | 'Expired' | 'Suppressed' | 'Unknown';
export const DELIVERY_STATUSES: DeliveryStatus[] = ['Draft', 'Scheduled', 'Queued', 'Processing', 'Submitted', 'ProviderAccepted', 'Sent', 'Delivered', 'Read',
  'Failed', 'RetryPending', 'Cancelled', 'Expired', 'Suppressed', 'Unknown'];

export type Priority = 'Low' | 'Normal' | 'High' | 'Critical';
export const PRIORITIES: Priority[] = ['Low', 'Normal', 'High', 'Critical'];
export type MessageClass = 'Transactional' | 'Promotional' | 'Critical';
export const MESSAGE_CLASSES: MessageClass[] = ['Transactional', 'Promotional', 'Critical'];
export type TemplateStatus = 'Draft' | 'InReview' | 'Approved' | 'Active' | 'Retired' | 'Rejected';
export type BodyFormat = 'Text' | 'Html';
export type CampaignStatus = 'Draft' | 'PendingApproval' | 'Approved' | 'Scheduled' | 'Running' | 'Paused' | 'Completed' | 'Cancelled';
export type ApprovalKind = 'TemplateVersion' | 'Campaign' | 'ProviderCredentials' | 'AiNotification';
export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled';
export type ProviderScope = 'Shared' | 'Tenant' | 'Company';
export type DuplicateRiskPolicy = 'Never' | 'SameProviderOnly' | 'AllowDuplicates';
export type SuppressionReason = 'HardBounce' | 'Complaint' | 'Unsubscribe' | 'InvalidAddress' | 'OptOut' | 'Manual';
export type ContactKind = 'User' | 'Customer' | 'Other';
export type Uuid = string;

export interface ApiError { code: string; message: string; errors?: Record<string, string[]> | null; correlationId?: string | null; }
export interface Paged<T> { items: T[]; page?: number; pageSize?: number; total: number; }

// ---- auth ----
export interface Grant { role: string; tenantId?: Uuid | null; companyId?: Uuid | null; applicationId?: Uuid | null; channel?: Channel | null; }
export interface Me {
  kind: string; actorId: string; actorName?: string | null; tenantId?: Uuid | null; applicationId?: Uuid | null; isAiAgent: boolean;
  scopes: string[]; grants: Grant[]; permissions: string[]; tenants: { id: Uuid; name: string; code: string }[];
}
export interface LoginResponse { accessToken: string; expiresAt: string; user: { id: Uuid; email: string; displayName: string }; }

// ---- tenancy ----
export interface QuietHours { start: string; end: string; appliesTo: MessageClass[]; channels: Channel[]; enabled: boolean; }
export interface Tenant {
  id: Uuid; code: string; name: string; status: string; suspendedReason?: string | null; allowSharedProviders: boolean; requireMarketingConsent: boolean;
  defaultTimeZone: string; branding?: Record<string, unknown> | null; bodyRetentionDays: number; dataRetentionDays: number; createdAt: string;
}
export interface Company {
  id: Uuid; tenantId: Uuid; code: string; name: string; country?: string | null; timeZone?: string | null; status: string; suspendedReason?: string | null;
  branding?: Record<string, unknown> | null; quietHours?: QuietHours | null;
}
export interface Application {
  id: Uuid; tenantId: Uuid; code: string; name: string; description?: string | null; status: string; suspendedReason?: string | null; sandbox: boolean;
  allowedChannels: Channel[]; defaultLocale: string; rateLimitPerMinute: number;
  companies: { companyId: Uuid; enabled: boolean; defaultSenders?: Record<string, Uuid> | null }[];
}
export interface Credential {
  id: Uuid; name: string; clientId: string; scopes: string[]; allowedCompanyIds: Uuid[]; isAiAgent: boolean; expiresAt?: string | null;
  revokedAt?: string | null; lastUsedAt?: string | null; createdBy?: string | null; createdAt: string;
}
export interface RoleAssignment { id: Uuid; userId: Uuid; role: string; tenantId?: Uuid | null; companyId?: Uuid | null; applicationId?: Uuid | null; channel?: Channel | null; }
export interface AdminUser { id: Uuid; email: string; displayName: string; enabled: boolean; lastLoginAt?: string | null; external: boolean; roles: RoleAssignment[]; }

// ---- dashboard / monitoring ----
export interface QueueStats { ready: number; inFlight: number; deadLettered: number; }
export interface Dashboard {
  totalNotifications: number; totalDeliveries: number; sentToday: number; delivered: number; accepted: number; failed: number; pending: number;
  suppressed: number; unknown: number; successRate?: number | null; byStatus: Record<string, number>;
  byChannel: { channel: Channel; total: number; delivered: number; failed: number }[];
  providerHealth: { id: Uuid; name: string; channel: Channel; healthStatus: string; suspended: boolean; avgResponseMs?: number | null; attempts24h: number }[];
  avgProviderResponseMs?: number | null; queues?: Record<string, QueueStats> | null; retryBacklog: number; awaitingOperator: number; estimatedCost: number;
  hourly: { hour: string; total: number; failed: number }[];
}
export interface SystemAlert { id: Uuid; tenantId?: Uuid | null; severity: string; source: string; message: string; createdAt: string; acknowledgedAt?: string | null; }
export interface Monitoring {
  transport: string; queues: Record<string, QueueStats>; outboxBacklog: number;
  workers: { worker: string; instance: string; status: string; processed: number; failed: number; lastError?: string | null; startedAt: string; lastHeartbeatAt: string }[];
  retries: number; awaitingOperator: number;
  deadLetters: { id: Uuid; queue: string; type: string; dequeueCount: number; lastError?: string | null; deadLetteredAt: string; enqueuedAt: string }[];
  providerErrors24h: { providerAccountId?: Uuid | null; errorCode?: string | null; n: number }[];
  circuits: { providerAccountId: Uuid; failures: number; openUntil?: string | null }[];
  alerts: SystemAlert[];
}

// ---- deliveries ----
export interface ExplorerRow {
  createdAt: string; notificationId: Uuid; deliveryId: Uuid; application: string; company: string; recipient: string; notificationType?: string | null;
  channel: Channel; provider?: string | null; status: DeliveryStatus; attempts: number; cost?: number | null; currency?: string | null;
  failureCode?: string | null; statusReason?: string | null; awaitingOperator: boolean; campaignId?: Uuid | null;
}
export interface DeliveryDto {
  deliveryId: Uuid; channel: Channel; recipient: string; status: DeliveryStatus; provider?: string | null; providerAccountId?: Uuid | null;
  providerMessageId?: string | null; attempts: number; failureCode?: string | null; failureReason?: string | null; cost?: number | null; currency?: string | null;
  templateVersionId?: Uuid | null; templateVersion?: number | null; fallbackOfDeliveryId?: Uuid | null; escalationOfDeliveryId?: Uuid | null;
  createdAt: string; nextAttemptAt?: string | null; submittedAt?: string | null; acceptedAt?: string | null; sentAt?: string | null;
  deliveredAt?: string | null; readAt?: string | null; failedAt?: string | null;
}
export interface DeliveryAttempt {
  id: Uuid; deliveryId: Uuid; attemptNumber: number; provider?: string | null; providerAccountId?: Uuid | null; outcome?: string | null;
  providerMessageId?: string | null; responseCode?: string | null; responseSummary?: string | null; errorCode?: string | null; errorMessage?: string | null;
  durationMs?: number | null; cost?: number | null; startedAt: string; completedAt?: string | null;
}
export interface DeliveryEvent { id: Uuid; deliveryId?: Uuid | null; type: string; status?: DeliveryStatus | null; source: string; detail?: string | null; occurredAt: string; recordedAt: string; }
export interface DeliveryDetail {
  delivery: DeliveryDto;
  request: { id: Uuid; notificationTypeCode?: string | null; templateCode?: string | null; source: string; actorId?: string | null; actorName?: string | null;
    businessEventId?: Uuid | null; ruleId?: Uuid | null; campaignId?: Uuid | null; correlationId?: string | null; traceId?: string | null; createdAt: string };
  content?: { subject?: string | null; title?: string | null; body?: string | null; textBody?: string | null; format?: BodyFormat | null; whatsAppTemplate?: string | null } | null;
  contentAvailable: boolean; contentPurgedAt?: string | null; awaitingOperator: boolean; deadLettered: boolean;
  attempts: DeliveryAttempt[]; events: DeliveryEvent[];
}
export interface Timeline { notificationId: Uuid; deliveries: DeliveryDto[]; attempts: DeliveryAttempt[]; events: DeliveryEvent[]; }

// ---- templates ----
export interface TemplateVariable { name: string; required: boolean; sampleValue?: string | null; description?: string | null; }
export interface TemplateSummary {
  id: Uuid; code: string; name: string; description?: string | null; category?: string | null; channel: Channel; locale: string; status: TemplateStatus;
  activeVersion?: number | null; latestVersion: number; tenantId: Uuid; companyId?: Uuid | null; applicationId?: Uuid | null;
  variables: TemplateVariable[]; updatedAt: string;
}
export interface WhatsAppButton { type: string; text: string; url?: string | null; variable?: string | null; phoneNumber?: string | null; }
export interface TemplateComponents {
  title?: string | null;
  whatsApp?: { headerFormat?: string | null; headerText?: string | null; headerMediaVariable?: string | null; footerText?: string | null; buttons: WhatsAppButton[] } | null;
  push?: { icon?: string | null; imageUrl?: string | null; sound?: string | null; deepLink?: string | null } | null;
}
export interface TemplateVersion {
  id: Uuid; versionNumber: number; status: TemplateStatus; subject?: string | null; body: string; bodyFormat: BodyFormat; textBody?: string | null;
  components?: TemplateComponents | null; providerTemplateName?: string | null; providerTemplateLanguage?: string | null; providerCategory?: string | null;
  providerApprovalStatus?: string | null; dltTemplateId?: string | null; changeNote?: string | null; createdBy?: string | null; createdAt: string;
  submittedBy?: string | null; submittedAt?: string | null; approvedBy?: string | null; approvedAt?: string | null; rejectionReason?: string | null;
  variables: TemplateVariable[];
}
export interface TemplateDetail { template: TemplateSummary; versions: TemplateVersion[]; }
export interface TemplateVersionRequest {
  subject?: string | null; body: string; bodyFormat: BodyFormat; textBody?: string | null; components?: TemplateComponents | null;
  providerTemplateName?: string | null; providerTemplateLanguage?: string | null; providerCategory?: string | null; dltTemplateId?: string | null;
  variables?: TemplateVariable[] | null; changeNote?: string | null;
}
export interface TemplateUpsertRequest {
  tenantId: Uuid; companyId?: Uuid | null; applicationId?: Uuid | null; code: string; name: string; description?: string | null; category?: string | null;
  channel: Channel; locale: string;
}
export interface TreeNode { type: 'tenant' | 'company' | 'application' | 'category' | 'template'; id?: Uuid; name: string; code?: string; channel?: Channel;
  locale?: string; status?: TemplateStatus; activeVersionNumber?: number | null; latestVersionNumber?: number; children?: TreeNode[]; }
export interface PreviewResponse { subject?: string | null; body?: string | null; textBody?: string | null; missingVariables: string[]; unknownVariables: string[];
  errors: string[]; sms?: { encoding: string; characters: number; segments: number } | null; }
export interface NotificationType {
  id: Uuid; tenantId: Uuid; applicationId?: Uuid | null; code: string; name: string; description?: string | null; category: string; messageClass: MessageClass;
  defaultChannels: Channel[]; defaultTemplateCode?: string | null; defaultPriority: Priority; mandatory: boolean; aiAllowed: boolean;
  requiresHumanApprovalForAi: boolean; isSensitive: boolean; createdAt?: string;
}

// ---- providers ----
export interface ProviderType { providerType: string; displayName: string; channels: Channel[]; secretKeys: string[]; settingKeys: string[]; }
export interface ProviderAccount {
  id: Uuid; name: string; channel: Channel; providerType: string; scope: ProviderScope; tenantId?: Uuid | null; companyId?: Uuid | null;
  settings: Record<string, unknown> | null; secrets: Record<string, string>; priority: number; rateLimitPerSecond?: number | null; monthlyCapacity?: number | null;
  allowedCountryPrefixes: string[]; unitCost?: number | null; currency: string; failoverProviderAccountId?: Uuid | null; duplicateRiskPolicy: DuplicateRiskPolicy;
  timeoutSeconds: number; enabled: boolean; suspended: boolean; suspendedReason?: string | null; healthStatus: string; lastHealthCheckAt?: string | null;
  lastHealthDetail?: string | null; pendingApprovalId?: Uuid | null; createdAt: string; updatedAt: string;
}
export interface SenderIdentity {
  id: Uuid; providerAccountId: Uuid; tenantId?: Uuid | null; companyId?: Uuid | null; channel: Channel; address: string; displayName?: string | null;
  replyTo?: string | null; signature?: string | null; dltEntityId?: string | null; whatsAppPhoneNumberId?: string | null; whatsAppBusinessAccountId?: string | null;
  isDefault: boolean; suspended: boolean;
}
export interface ProviderHealthEvent { id: Uuid; providerAccountId: Uuid; status: string; detail?: string | null; latencyMs?: number | null; occurredAt: string; }
export interface ProviderDetail {
  account: ProviderAccount; senders: SenderIdentity[]; health: ProviderHealthEvent[];
  last24h: { outcome?: string | null; count: number; avgMs?: number | null }[]; callbackUrl: string;
}
export interface RoutingRule {
  id: Uuid; tenantId?: Uuid | null; companyId?: Uuid | null; applicationId?: Uuid | null; channel: Channel; countryPrefix?: string | null;
  messageClass?: MessageClass | null; providerAccountId: Uuid; order: number; enabled: boolean;
}

// ---- rules ----
export interface ConditionNode { op?: 'and' | 'or' | string | null; children?: ConditionNode[] | null; field?: string | null; comparison?: string | null; value?: unknown; isGroup?: boolean; }
export interface RecipientSelector { type: 'payload' | 'users' | 'roles' | 'group' | 'company' | string; path?: string | null; userIds?: string[] | null; roles?: string[] | null; groupId?: Uuid | null; }
export interface RuleAction { channel: Channel; templateCode: string; fallbackChannel?: Channel | null; fallbackTemplateCode?: string | null; }
export interface Escalation { afterMinutes: number; unlessStatus: DeliveryStatus; channel: Channel; templateCode?: string | null; }
export interface RetryPolicy { maxAttempts?: number | null; initialDelaySeconds?: number | null; maxDelaySeconds?: number | null; }
export interface Rule {
  id: Uuid; tenantId: Uuid; companyId?: Uuid | null; applicationId?: Uuid | null; name: string; description?: string | null; eventType: string; enabled: boolean;
  order: number; stopProcessing: boolean; notificationTypeCode?: string | null; conditions?: ConditionNode | null; recipients: RecipientSelector;
  actions: RuleAction[]; priority?: Priority | null; delaySeconds: number; escalation?: Escalation | null; retryPolicy?: RetryPolicy | null;
  quietHours?: QuietHours | null; createdBy?: string | null; createdAt: string; updatedAt: string;
}
export type RuleRequest = Omit<Rule, 'id' | 'createdBy' | 'createdAt' | 'updatedAt'>;
export interface SendResponse {
  notificationId: Uuid; status: string; trackingUrl: string; duplicate: boolean; warnings: string[];
  deliveries: { deliveryId: Uuid; channel: Channel; recipient: string; status: DeliveryStatus; reason?: string | null }[];
  preview?: { channel: Channel; recipient: string; subject?: string | null; body?: string | null; textBody?: string | null; policyDecision?: string | null; sendAt?: string | null }[] | null;
}
export interface SimulationResult { rules: { ruleId: Uuid; name: string; matched: boolean; reason?: string | null }[]; notifications: SendResponse[]; errors: string[]; }

// ---- campaigns ----
export interface Campaign {
  id: Uuid; name: string; description?: string | null; status: CampaignStatus; suspended: boolean; companyId: Uuid; applicationId: Uuid; channels: Channel[];
  templateCode: string; messageClass: MessageClass; scheduledAt?: string | null; throttlePerMinute: number; totalRecipients: number;
  createdBy?: string | null; createdByName?: string | null; approvedBy?: string | null; approvedAt?: string | null; startedAt?: string | null;
  completedAt?: string | null; createdAt: string; recipients: Record<string, number>; deliveries: Record<string, number>; cost: number;
}
export interface CampaignRecipient {
  id: Uuid; rowNumber: number; externalId?: string | null; name?: string | null; email?: string | null; mobile?: string | null; status: string;
  error?: string | null; notificationRequestId?: Uuid | null; processedAt?: string | null;
}

// ---- inbox ----
export interface ConversationSummary {
  id: Uuid; companyId?: Uuid | null; applicationId?: Uuid | null; channel: Channel; externalAddress: string; contactId?: Uuid | null; contactName?: string | null;
  lastMessageAt: string; lastInboundAt?: string | null; unreadCount: number; closed: boolean; windowOpen: boolean; lastMessage?: string | null; routing?: string | null;
}
export interface ConversationMessage {
  direction: 'inbound' | 'outbound'; id: Uuid; at: string; body?: string | null; type: string; status: string; detail?: string | null; inReplyTo?: Uuid | null;
  attachments: { id: Uuid; fileName: string; contentType: string; size: number }[];
}

// ---- usage ----
export interface UsageRow { key: string; messages: number; units: number; estimatedCost: number; providerReportedCost?: number | null; billedAmount: number; margin: number; }
export interface Quota { id: Uuid; tenantId: Uuid; companyId?: Uuid | null; applicationId?: Uuid | null; channel?: Channel | null; monthlyLimit: number; alertThresholdPercent: number; used: number; }
export interface CreditAccount {
  id: Uuid; tenantId: Uuid; companyId?: Uuid | null; balance: number; reserved: number; available: number; lowBalanceThreshold?: number | null; currency: string;
  enabled: boolean; transactions: { id: Uuid; kind: string; amount: number; deliveryId?: Uuid | null; note?: string | null; createdAt: string }[];
}
export interface BillingRate { id: Uuid; tenantId?: Uuid | null; channel: Channel; unitPrice?: number | null; marginPercent: number; currency: string; }
export interface CostRate { id: Uuid; providerAccountId?: Uuid | null; channel: Channel; countryPrefix?: string | null; messageClass?: MessageClass | null; unitCost: number; currency: string; effectiveFrom?: string; }

// ---- approvals ----
export interface Approval {
  id: Uuid; tenantId: Uuid; kind: ApprovalKind; entityType: string; entityId: Uuid; title: string; status: ApprovalStatus; requestedBy: string;
  requestedByName?: string | null; requestedAt: string; decidedBy?: string | null; decidedAt?: string | null; comment?: string | null; canDecide: boolean;
}

// ---- recipients ----
export interface Contact {
  id: Uuid; tenantId: Uuid; companyId: Uuid; kind: ContactKind; externalId: string; name?: string | null; email?: string | null; mobile?: string | null;
  whatsapp?: string | null; timeZone?: string | null; locale?: string | null; country?: string | null; roles: string[]; attributes?: Record<string, string> | null; updatedAt: string;
}
export interface Preferences {
  contactId: Uuid; externalId: string; preferences: { category: string; channel: Channel; enabled: boolean }[];
  consents: { id: Uuid; channel: Channel; purpose: string; status: string; source: string; evidence?: string | null; scope?: string | null; capturedAt: string }[];
}
export interface Suppression { id: Uuid; tenantId: Uuid; companyId?: Uuid | null; channel: Channel; address: string; reason: SuppressionReason; promotionalOnly: boolean; source?: string | null; createdAt: string; }
export interface Group { id: Uuid; companyId: Uuid; name: string; description?: string | null; dynamic: boolean; filter?: ConditionNode | null; members: number; }

// ---- admin misc ----
export interface Webhook { id: Uuid; applicationId: Uuid; companyId?: Uuid | null; name: string; url: string; eventTypes: string[]; enabled: boolean; consecutiveFailures: number; disabledAt?: string | null; createdAt: string; }
export interface WebhookAttempt { id: Uuid; subscriptionId: Uuid; eventId?: Uuid; eventType?: string; attemptNumber?: number; statusCode?: number | null; error?: string | null;
  durationMs?: number | null; attemptedAt: string; succeeded?: boolean; [k: string]: unknown; }
export interface Schedule {
  id: Uuid; name: string; companyId: Uuid; applicationId: Uuid; cronExpression?: string | null; sequence?: number[] | null; sequenceIndex: number; timeZone: string;
  startAt?: string | null; endAt?: string | null; nextRunAt?: string | null; lastRunAt?: string | null; runCount: number; maxRuns?: number | null;
  workingDaysOnly: boolean; holidayCalendarId?: Uuid | null; enabled: boolean; request?: Record<string, unknown> | null;
}
export interface HolidayCalendar { id: Uuid; tenantId?: Uuid | null; name: string; country?: string | null; dates: { date: string; name: string }[]; }
export interface AuditEntry {
  id: Uuid; tenantId?: Uuid | null; companyId?: Uuid | null; applicationId?: Uuid | null; actorId?: string | null; actorName?: string | null; actorType?: string | null;
  action: string; entityType?: string | null; entityId?: string | null; detail?: string | null; ipAddress?: string | null; correlationId?: string | null;
  occurredAt: string; [k: string]: unknown;
}
export interface ReportInfo { code: string; name: string; }
export interface ReportResult { code: string; name: string; rows: Record<string, unknown>[]; }

/** Realtime payloads from /hubs/admin. */
export interface DeliveryPush { deliveryId: Uuid; notificationId: Uuid; channel: Channel; status?: DeliveryStatus | null; source: string; at: string; companyId: Uuid; applicationId: Uuid; }
export interface InboundPush { id: Uuid; threadId: Uuid; channel: Channel; from: string; body?: string | null; receivedAt: string; }
