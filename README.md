# Wings Notification Hub

Centralized, multi-tenant notification and communication platform for Wings products (and any SaaS customer):
**email, SMS, WhatsApp Business, in-app, mobile push, browser push and webhooks** behind one channel-independent API.
Applications *request* a notification; the hub decides templates, providers, consent, retries and tracking.

| Piece | Where |
|---|---|
| REST API + SignalR + MCP endpoint (ASP.NET Core, .NET 10) | `src/Wings.NotificationHub.Api` |
| Domain, template engine, policies, rule evaluation | `src/Wings.NotificationHub.Core` |
| EF Core (SQLite dev / SQL Server prod), workers, provider adapters | `src/Wings.NotificationHub.Infrastructure` |
| Standalone worker host (scale-out processing) | `src/Wings.NotificationHub.Worker` |
| Wire contracts / C# SDK | `src/Wings.NotificationHub.Contracts`, `src/Wings.NotificationHub.Client` |
| Angular admin console | `web/` (builds into the API's `wwwroot`) |
| TypeScript SDK, Angular in-app inbox | `clients/` |
| Sample integrations (ASP.NET Core, WinForms, Ionic, Wings Code manifest) | `samples/` |
| SQL Server migrations & scripts | `src/Wings.NotificationHub.Infrastructure/Data/Migrations`, `database/sqlserver` |
| Docker / IIS / Windows service / systemd | `deploy/` |
| Tests: unit, integration (end-to-end acceptance), load | `tests/` |
| Documentation | `docs/` |

## Quick start (development)

Prerequisites: .NET 10 SDK (here at `~/.dotnet`), Node 22+ for the UI.

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet run --project src/Wings.NotificationHub.Api --launch-profile http     # http://localhost:5480
```

On first start in Development the hub creates `App_Data/notification-hub.db`, a development encryption key and signing key,
and seeds demo data: tenants *Wings Demo Tenant* (companies ABC Traders, Sunrise Retail) and *Acme Holdings*, Wings applications,
mock providers for every channel, templates for every event in the specification, rules, contacts with consent, devices,
quotas, cost/billing rates and credentials. Generated logins and client secrets are written to
`App_Data/dev-credentials.env` (never logged).

```bash
scripts/demo.sh            # end-to-end walkthrough: send, idempotency, timeline, rules, failover, consent, inbox, inbound reply, MCP, dashboard
```

- Admin console: http://localhost:5480 (after `cd web && npm install && npm run build`), or `npm start` in `web/` for http://localhost:4480 with live reload.
- API reference: http://localhost:5480/api-docs (OpenAPI at `/openapi/v1.json`).
- Mock providers simulate every outcome by destination: `…0000`/`fail-permanent` (rejected), `…1111`/`fail-transient` (temporary failure → failover),
  `…2222`/`timeout` (accepted but response lost → ambiguous), `…3333`/`undeliverable` (accepted, then failed receipt).

## Sending a notification

```http
POST /connect/token            grant_type=client_credentials&client_id=…&client_secret=…
POST /api/v1/notifications/send
Authorization: Bearer …
Idempotency-Key: invoice-guid-created-v1

{ "companyId": "…", "notificationType": "INVOICE_CREATED", "templateCode": "INVOICE_CREATED_V1",
  "recipient": { "customerId": "CUST-1001", "email": "customer@example.com", "mobile": "+919876543210" },
  "channels": ["email", "whatsapp", "inapp"],
  "variables": { "CustomerName": "ABC Traders", "InvoiceNumber": "INV-10045", "InvoiceAmount": "25000", "InvoiceDate": "2026-10-28" } }

202 Accepted
{ "notificationId": "…", "status": "accepted", "trackingUrl": "/api/v1/notifications/…/status", "deliveries": [ … ] }
```

`202` means the request is durably recorded (request, deliveries and outbox message in one transaction), **not** delivered.
Track it with `GET /api/v1/notifications/{id}/status` / `/history`, or subscribe to `delivery.status_changed` webhooks.

## Tests

```bash
dotnet test tests/Wings.NotificationHub.UnitTests          # 111 tests: template engine, policies, crypto, provider payloads and HTTP contracts…
dotnet test tests/Wings.NotificationHub.IntegrationTests   # 44 end-to-end acceptance tests against an in-process hub with real workers
dotnet run --project tests/Wings.NotificationHub.LoadTests -- --count 2000 --concurrency 32
```

See [docs/testing.md](docs/testing.md) for how the specification's acceptance scenarios map to tests, and what is not verified.

## Documentation

- [Architecture](docs/architecture.md) — modules, data model, processing pipeline, reliability design
- [API guide](docs/api.md) — authentication, scopes, idempotency, endpoints, webhooks, errors
- [Administrator guide](docs/admin-guide.md) — tenants, templates, rules, providers, campaigns, approvals, emergency controls
- [Providers](docs/providers.md) — configuring SMTP, SES, Microsoft 365, Twilio, MSG91/DLT, WhatsApp, FCM, APNs, Web Push
- [Operations](docs/operations.md) — deployment, configuration, secrets, SLOs, monitoring, backup/restore, retention, runbooks
- [Wings ecosystem](docs/wings-integration.md) — MCP tools for AI agents, Wings Code manifest, SDKs
- [Testing](docs/testing.md)
