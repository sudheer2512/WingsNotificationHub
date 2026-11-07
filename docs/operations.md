# Operations

## Topologies

| Size | Layout |
|---|---|
| Small / single server | API with in-process workers (`Workers:RunInApi=true`), SQL Server, database queue transport. IIS or Windows service. |
| Production | 2+ API instances (`Workers:RunInApi=false`) behind a load balancer, 2+ worker instances, SQL Server (Always On), RabbitMQ (`Hub:MessageTransport=rabbitmq`), Redis (`Redis:Connection`: shared provider rate limits, distributed cache, SignalR backplane). |
| Containers | `deploy/docker` (API + worker images, compose with SQL Server, RabbitMQ, Redis). |

Every instance is stateless; work ownership uses leases (database rows) or broker deliveries, so instances can be added,
removed or restarted at any time. SignalR clients are served by the API instance they connect to (the relay pushes
committed rows); with several API instances behind a load balancer use sticky sessions or the Redis backplane.

## Configuration (environment variables use `__` for `:`)

| Key | Purpose | Default |
|---|---|---|
| `ConnectionStrings__NotificationHub` | database | SQLite `App_Data/notification-hub.db` |
| `Database__Provider` | `sqlite` / `sqlserver` | sqlite |
| `Database__AutoCreate` | SQL Server: apply EF migrations at start; SQLite: create schema | true |
| `Hub__PublicBaseUrl` | external https URL (callbacks, unsubscribe links, signatures) | — (required) |
| `Hub__MessageTransport` | `database` / `rabbitmq` | database |
| `Messaging__RabbitMq__Uri` | `amqps://user:pass@host/vhost` | — |
| `Redis__Connection` | StackExchange.Redis configuration string | — |
| `Security__EncryptionKeys__<keyId>` + `Security__ActiveKeyId` | AES-256 keys (base64, 32 bytes) for secrets and message bodies; keep old keys for decryption when rotating | **required outside Development** |
| `Auth__SigningKey` | base64 ≥ 256-bit key for hub-issued JWTs | **required outside Development** |
| `Authentication__Oidc__Authority` / `__Audience` | external admin identity provider (Entra ID, Keycloak, Wings Authentication) | — |
| `Workers__RunInApi`, `Workers__DispatchConcurrency`, `Workers__PollIntervalMs` | worker placement and throughput | true, 8, 500 |
| `Hub__LeaseSeconds`, `Hub__ReconcileAfterMinutes` | crash-recovery lease, receipt reconciliation delay | 120, 15 |
| `RateLimiting__*` | per-user/anonymous/auth limits (per-application limits are on the application) | — |
| `Webhooks__AllowHttp`, `Webhooks__AllowPrivateNetworks` | SSRF guard relaxations (development only) | false |
| `Security__RequireApprovalForCredentialChanges` | maker-checker for provider secrets | true |
| `OpenTelemetry__OtlpEndpoint` | OTLP collector for traces + metrics | — |
| `Analytics__ClickHouse__Url/Table/User/Password` | optional analytics sink (never on the send path) | — |
| `Hub__SeedDemoData`, `Hub__EnableDevEndpoints` | demo data and `/api/v1/dev/*` | Development only |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` | first administrator (otherwise generated into `App_Data/dev-credentials.env`) | — |

Secrets belong in a secret store (Azure Key Vault, AWS Secrets Manager, Kubernetes secrets, Windows DPAPI-protected
environment) — never in `appsettings.json`. Provider credentials are entered through the API/UI and stored encrypted.
Generate keys with `openssl rand -base64 32`.

## Database

* SQL Server: `src/Wings.NotificationHub.Infrastructure/Data/Migrations` (applied automatically when `AutoCreate=true`) or
  `database/sqlserver/migrate.sql` (idempotent script for DBAs; set `AutoCreate=false`). `create.sql` is the full schema.
  Optional monthly partitioning for history tables: `database/sqlserver/partitioning.sql` (review before use).
* Enable TDE (or Always Encrypted for contact columns) for at-rest protection; message bodies and secrets are already
  application-encrypted.
* **Backup/restore**: full + differential + log backups per your RPO; the encryption keys must be backed up separately and
  restored together with the database (without them encrypted columns cannot be read). After a restore, run the API once:
  migrations apply, reconciliation recovers in-flight deliveries (expired leases → retry or ambiguous handling), and the
  outbox relay republishes unpublished messages. Restoring an *older* backup can resend messages that were sent after the
  backup point; deliveries in `Unknown` need operator review.

## Retention and privacy

Per tenant: `BodyRetentionDays` (default 90 — rendered bodies, variables and inbound bodies are purged, metadata kept)
and `DataRetentionDays` (default 730 — requests, deliveries, attempts, events, inbox items deleted). The retention worker
runs hourly; it also trims deduplication records (14 days), published outbox rows (7 days), dead letters (30 days).
`DELETE /api/v1/contacts/{id}` erases a person: contact fields, recipient snapshots and delivery addresses are scrubbed
(addresses replaced by a hash), bodies purged, devices removed; consent evidence is kept. Logs never contain message
bodies, secrets or tokens; provider responses are sanitized and truncated; addresses are masked in API responses for
roles without `ViewMessageContent`.

## Service-level objectives (proposed)

| SLO | Target | Measured by |
|---|---|---|
| API availability (`/health/ready`, send endpoint) | 99.9 % monthly | load balancer / synthetic checks |
| Acceptance latency (`POST /notifications/send`, single recipient) | p95 < 300 ms, p99 < 1 s | `http.server.request.duration` |
| Queue pickup (accepted → submitted to provider), normal priority | p95 < 10 s; critical p95 < 3 s | `SubmittedAt − CreatedAt` (report *Queue backlog trends*) |
| Callback processing | p95 < 2 s | `wnh.callbacks` + logs |
| Provider delivery | **not an SLO of the hub** — reported per provider (*Provider performance*, *Average delivery latency*) | provider receipts |

## Monitoring

* Admin console → System Monitoring (`GET /api/v1/monitoring`): queue depths (ready/in-flight/dead-lettered), outbox
  backlog, workers per instance (heartbeat every 15 s; *unresponsive* after 2 min), retries, deliveries awaiting an
  operator, dead letters (requeue), provider errors (24 h), circuit breakers, alerts.
* That view follows the caller's grants. A platform-wide grant sees everything. Other callers do not get queues, the
  outbox backlog or workers, because those serve every tenant. Retries, deliveries awaiting an operator and provider
  errors cover the deliveries they can see, and circuit breakers the providers they can see. Dead letters and requeue
  need a grant on a whole tenant and cover that tenant only. `QueueMessages.TenantId` is copied from the outbox, so a
  message queued before the `AddQueueMessageTenant` migration has none and stays with platform-wide grants. On
  RabbitMQ, dead letters stay in the broker's DLQs, so the list is empty and only the queue counts report them.
* Metrics (OpenTelemetry meter `Wings.NotificationHub`): `wnh.notifications.accepted`, `wnh.deliveries.outcomes`
  (provider/channel/outcome), `wnh.provider.latency`, `wnh.callbacks`, plus ASP.NET Core and HttpClient metrics.
* Traces: `notification.accept`, `delivery.dispatch`, `consume <type>` spans + incoming/outgoing HTTP.
* Logs: Serilog JSON/console with `CorrelationId`; route to your log platform via Serilog configuration.
* Alerts are raised for: dead-lettered messages, circuit opened, provider unhealthy, ambiguous deliveries needing an
  operator, critical notifications failing after all retries, quota thresholds, low prepaid credit, disabled webhooks,
  emergency suspensions.

## Runbooks

* **Provider outage**: circuit opens after 5 consecutive failures (60 s cool-down, half-open probe) and traffic fails
  over to the next eligible account. To force it: Providers → Suspend. Messages already accepted by the failed provider
  are not resent elsewhere unless the account's duplicate-risk policy allows it.
* **Deliveries in `Unknown` / awaiting operator**: Explorer → filter *awaiting operator* → check the provider portal →
  *Resolve*: `mark_sent`, `mark_failed`, or `resend` (accepts duplicate risk). Each decision is audited.
* **Dead letters**: Monitoring → inspect the error → fix the cause → *Requeue*.
* **Abusive or compromised sender/application**: `POST /api/v1/suspend/application/{id}` (or tenant/company/campaign,
  provider, sender). New requests are refused with 403; queued messages are held (no attempts consumed) and continue
  after `suspended=false`. Revoke credentials in Applications → Credentials.
* **Key rotation**: add a new key id, set `Security__ActiveKeyId` to it, restart; new data uses the new key, old data
  remains readable. Re-encrypt by re-saving provider secrets; retire the old key after body retention has elapsed.
* **Scaling**: add worker instances or raise `Workers__DispatchConcurrency`; respect provider rate limits
  (`rateLimitPerSecond`, shared through Redis when configured).

## Deployment assets

See `deploy/` (Dockerfiles, docker-compose, IIS `web.config` and guide, Windows service installer, systemd unit).
The Angular console is built by `npm run build` in `web/` into the API's `wwwroot` and served from `/`.
