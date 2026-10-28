# Architecture

## Shape: a modular monolith that can split

```
             ┌─────────────── Wings.NotificationHub.Api (ASP.NET Core) ───────────────┐
 apps ──────►│ /connect/token  /api/v1/*  /mcp  /hubs/inapp  /hubs/admin  /u/{token}   │◄──── admin console (Angular, wwwroot)
 providers ─►│ /api/v1/callbacks/{providerAccountId}   (signature-verified, idempotent) │
             │  NotificationService · RuleEngine · TemplateService · CampaignService …  │
             │  (optional) in-process workers when Workers:RunInApi = true               │
             └───────────────┬──────────────────────────────────────┬───────────────────┘
                             │ EF Core (SQLite dev / SQL Server)     │ IMessageBus
                             ▼                                       ▼
                   ┌────────────────────┐      outbox relay   ┌──────────────────────────────┐
                   │ application tables │ ──────────────────► │ database queue (default) or  │
                   │ + OutboxMessages   │                     │ RabbitMQ (quorum queues+DLX) │
                   └────────────────────┘                     └──────────────┬───────────────┘
                                                                              ▼
             ┌──────────── Wings.NotificationHub.Worker (0..n instances) ───────────────┐
             │ queue consumers → DeliveryDispatcher → provider adapters (INotificationProvider) │
             │ SchedulerWorker · ReconciliationWorker · ProviderHealthWorker · RetentionWorker  │
             └──────────────────────────────────────────────────────────────────────────────┘
```

* **Contracts** (wire DTOs) → **Core** (pure domain: entities, template engine, consent/preference policy, quiet hours,
  retry/backoff, status rules, condition evaluator, caller/permission model) → **Infrastructure** (EF Core, services,
  messaging, workers, provider adapters) → **Api** / **Worker** hosts.
* The API and the worker share the same infrastructure registration. Run everything in one process for small installs,
  or set `Workers:RunInApi=false` on the API and run N worker instances. Instances compete for work through row leases
  (database transport) or broker deliveries (RabbitMQ); no job is ever owned twice.
* Provider adapters are selected per **provider account** by `ProviderType`. Adding a provider means implementing
  `IProviderFactory`/`INotificationProvider` (+ optional `ICallbackHandler`, `IStatusQueryProvider`) and registering it;
  the orchestration model is unchanged. Adding a channel means adding a `Channel` value, a renderer branch and adapters.

## Tenancy and authorization

Hierarchy: **Hub → Tenant → Company → Application → configuration**. Applications belong to a tenant and are enabled per
company (`CompanyApplications`). Templates, rules and notification types may be tenant-wide, company-, application- or
company+application-specific; resolution is most-specific-wins.

Every request gets a `CallerContext` built from validated claims only:

| Caller | Token | Scope of access |
|---|---|---|
| Application | `/connect/token` client credentials (JWT, 60 min) | its tenant + application, optional company allow-list, OAuth scopes |
| Administrator | `/api/v1/auth/login` (8 h JWT) or an external OIDC token (`Authentication:Oidc:*`) | role assignments narrowed by tenant/company/application/channel |
| End user (inbox) | `/api/v1/inapp/tokens` minted by the app backend | that user's inbox in one company/application |
| AI agent | client credential flagged `IsAiAgent` with the `mcp` scope | as application, plus AI restrictions |

Payload `applicationId`/`companyId` values are checked against the caller; mismatches are `403`, and resources of
another tenant answer `404` (no existence probing). List queries use `AccessFilter` clauses translated to SQL.
Roles: PlatformAdministrator, TenantAdministrator, CompanyAdministrator, ApplicationAdministrator, TemplateDesigner,
TemplateApprover, CampaignManager, NotificationOperator, Auditor (read-only, no message bodies, masked addresses) and the
synthetic ApiClient role whose permissions come from scopes. Maker-checker applies to template versions, campaigns,
provider credential changes and high-risk AI notifications (`ApprovalRequests`; the requester can never approve).

## Processing pipeline (spec §8)

1. **Receive** `POST /notifications/send`, `/batch`, `/events`, MCP `notification.send`, schedules, campaigns.
2. **Authenticate/authorize**: `TenantContextResolver` — credential ↔ application ↔ company link, suspension checks.
3. **Quotas & credits**: monthly quotas per tenant/company/application/channel; prepaid credit reservation (`402`).
4. **Recipients**: contact resolution (contactId → customerId → userId), roles/groups/company-wide audiences, devices,
   webhook subscriptions. Explicit addresses override stored ones.
5. **Consent & preferences**: `CommunicationPolicy` (pure, unit-tested) — suppressions, WhatsApp opt-in, marketing
   consent, category/channel preferences, mandatory and critical classes. Denied deliveries are recorded as `Suppressed`.
6. **Templates & channels**: active version by code/channel/locale chain; record the exact `TemplateVersionId`.
7. **Render** with the restricted engine (no code, per-format escaping, required-variable checks; errors → `422`).
8. **Persist** request, recipients, deliveries (content encrypted), events and outbox rows in **one transaction**;
   idempotency via a unique `(ApplicationId, IdempotencyKey)` index plus request hash (`409` on key reuse with a different body).
9. **Outbox relay** publishes to the bus. 10. **Queue consumers** hand `delivery.dispatch` to `DeliveryDispatcher`.
11. **Claim** with a conditional update (`Queued|RetryPending → Processing` + lease) — duplicates are no-ops (idempotent consumer).
    Re-check suspension (hold, no attempt consumed), expiry, consent (opt-out after acceptance is honoured), WhatsApp 24 h window.
12. **Select provider** (`ProviderRouter`): visibility (company → tenant → shared if allowed; sandbox apps → mock only),
    routing rules, circuit state, health, geography, monthly capacity, priority, cost; rate-limit tokens (in-memory or Redis).
13. **Submit**: an immutable `DeliveryAttempt` and the `Submitted` state are saved *before* the call; the call runs with
    the account timeout.
14. **Outcome**: Accepted / PermanentFailure / TransientFailure / Throttled / **Ambiguous** (see below).
15. **Callbacks** update status forward-only; **retry/fallback/escalation** as configured; usage and cost recorded.
16. **Emit** `delivery.status_changed` → webhooks, analytics sink (optional ClickHouse), SignalR relay.

### Ambiguous outcomes and duplicate risk

A timeout or connection reset after the request left us means the provider *may* have accepted the message.

| Situation | Behaviour |
|---|---|
| Provider deduplicates by our idempotency key (`wnh-{deliveryId}`) | retry **pinned to the same provider** — no duplicate |
| Account `DuplicateRiskPolicy = SameProviderOnly` | retry pinned to the same provider (duplicate accepted, never cross-provider) |
| `AllowDuplicates` (e.g. push, in-app, webhooks) | normal retry and failover |
| Provider supports status queries | `Unknown`; reconciliation asks the provider: found → accepted (no resend); not found → safe resend |
| none of the above | `Unknown` + `AwaitingOperator`; alert raised; operator chooses resend / mark sent / mark failed |

Failover to another provider only happens after a **definite** non-acceptance (transient failure, throttling, open circuit).
A worker crash after `Submitted` is detected by lease expiry and treated as ambiguous; a crash before submission is retried.

### Status model

`Draft, Scheduled, Queued, Processing, Submitted, ProviderAccepted, Sent, Delivered, Read, Failed, RetryPending,
Cancelled, Expired, Suppressed, Unknown`. Provider acceptance, provider-reported delivery and user interaction are distinct:
push is `ProviderAccepted` until a device reports receipt/open (`POST /push/events`); in-app is `Delivered` when stored in
the inbox and `Read` when the user reads it. Callbacks never move a delivery backwards; late events are recorded with
`Applied=false`. Callbacks that arrive before the provider message id is saved are parked and applied afterwards.
`DeliveryAttempts` and `DeliveryEvents` are insert-only: retries never overwrite earlier evidence.

## Data model

54 tables (see `database/sqlserver/create.sql`), GUID keys everywhere, UTC timestamps (recipient/schedule time zones are
stored separately), optimistic concurrency stamps on mutable aggregates, filtered unique indexes for idempotency keys.
Spec entity list → tables: Tenants, Companies, Applications (+CompanyApplications), ApplicationCredentials,
NotificationTypes, NotificationTemplates, TemplateVersions, TemplateVariables, NotificationRules, RuleConditions
(normalized condition tree), BusinessEvents, NotificationRequests, NotificationRecipients, NotificationDeliveries,
DeliveryAttempts, DeliveryEvents, ProviderAccounts (+SenderIdentities), ProviderRoutingRules, ProviderHealthEvents,
RecipientContacts, RecipientPreferences, RecipientConsents (append-only evidence), RecipientGroups (+Members),
Suppressions, DeviceRegistrations, InAppMessages, NotificationSchedules, HolidayCalendars (+Dates), Campaigns,
CampaignRecipients, InboundMessages, ConversationThreads, MessageAttachments, WebhookSubscriptions,
WebhookDeliveryAttempts, UsageRecords, CostRates, BillingRates, TenantQuotas, CreditAccounts (+Transactions),
OutboxMessages, QueueMessages (database transport), InboxDeduplication, AuditLogs, ApprovalRequests, AdminUsers,
RoleAssignments, SystemAlerts, WorkerHeartbeats.

Sensitive data: provider secrets, webhook secrets, callback secrets, rendered message bodies, request variables,
approval payloads and attachments are AES-256-GCM encrypted (`ISecretProtector`, versioned key ring, purpose-bound).
Contact addresses are stored in clear for querying and masked in every API response unless the caller holds
`ViewMessageContent`; use SQL Server TDE/Always Encrypted for at-rest protection of the database files.

## Template engine

Restricted Mustache-like language (`Core/Templates/TemplateEngine.cs`): `{{Var}}`, `{{a.b}}`, filters (`upper lower title
trim default truncate number currency date url`), `{{#if}}…{{else}}…{{/if}}`, `{{#unless}}`, `{{#each}}` with `this`/`@index`,
comments. No raw output, no expressions or method calls. HTML bodies encode every value; text channels strip control
characters; subjects strip line breaks (header injection); `| url` only allows http(s)/mailto/tel/relative links; template
HTML is rejected if it contains scripts, event handlers, `javascript:` URLs, iframes, forms etc. WhatsApp templates map
`{{Var}}` to Meta's positional `{{1}}…` parameters automatically.

## Scheduling, rules, campaigns, inbound

* **SchedulerWorker** (1 s): promotes due scheduled/retry deliveries (priority-ordered, conditional per-row claim), expires,
  creates fallbacks/escalations, runs cron/reminder schedules (Cronos, time zone, working days, holiday calendars) and
  advances campaigns within their per-minute throttle.
* **Rules**: business events are persisted then evaluated asynchronously; nested AND/OR condition trees over the event
  payload; recipients from payload paths, users, roles, groups or company; per-channel templates with fallback; delay,
  escalation, retry and quiet-hour overrides; simulation renders everything without persisting.
* **Campaigns**: CSV/XLSX import with row validation, groups (static/dynamic), segments, test send, maker-checker
  approval, scheduled start, throttled batches, per-recipient outcomes, pause/resume/cancel, cost tracking.
* **Inbound**: provider webhooks → tenant/thread/contact resolution, reply correlation (WhatsApp context id or last
  outbound), encrypted attachments, STOP handling (consent withdrawal + suppression), routing to the originating
  application via `inbound.message_received` webhooks and an `inbound.message_received` business event for rules.
  The hub never executes business operations itself.

## Reliability features (spec §18)

Durable request persistence, transactional outbox, competing consumers with leases, retry queues with exponential
backoff + jitter, dead-letter queue with alerts and requeue, provider rate limiting (Redis-shared optional), tenant
quotas, circuit breakers, backpressure (throttled deliveries reschedule without consuming attempts), webhook signature
verification and replay windows, idempotent callback processing (`InboxDeduplication`), OpenTelemetry traces/metrics,
Serilog structured logs with correlation ids, audit trail, health checks (`/health/live`, `/health/ready`), graceful
shutdown (lease expiry recovers in-flight work), retention worker, TLS/HSTS outside Development, emergency suspension of
tenant/company/application/campaign/provider/sender (messages are held, not lost).
