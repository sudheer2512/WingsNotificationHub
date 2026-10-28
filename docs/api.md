# API guide

Base path `/api/v1`. JSON is camelCase; enums are strings; channels are lower-case (`email`, `sms`, `whatsapp`, `inapp`,
`push`, `webpush`, `webhook`). The full, generated reference is at `/api-docs` (OpenAPI document `/openapi/v1.json`).

## Authentication

**Applications** use OAuth 2.0 client credentials:

```bash
curl -X POST https://hub/connect/token -d grant_type=client_credentials -d client_id=$ID -d client_secret=$SECRET [-d scope="notifications.send notifications.read"]
# → {"access_token":"…","token_type":"Bearer","expires_in":3600,"scope":"…"}
```

Credentials are created per application by administrators (`POST /applications/{id}/credentials`, secret shown once),
may be restricted to companies, expire, and can be revoked (effective within 30 s even for issued tokens).

| Scope | Allows |
|---|---|
| `notifications.send` / `notifications.batch` | send, schedule, cancel, batch |
| `notifications.read` | status, history, list, delivery explorer for own application |
| `events.publish` | `POST /events` |
| `templates.read` / `templates.write` | list/preview templates / manage own application's templates |
| `contacts.write`, `devices.write`, `preferences.read`, `preferences.write` | recipients, devices, preferences, consent |
| `inapp.token` | mint end-user inbox tokens |
| `usage.read`, `providers.health` | usage/reports, provider health |
| `webhooks.manage` | delivery/reply callback subscriptions |
| `mcp` | the `/mcp` endpoint (AI agents) |
| `applications.register` | Wings Code manifest registration (tenant provisioning credentials only) |

**Administrators** sign in with `POST /api/v1/auth/login` (local accounts) or present a token from the configured OIDC
authority (`Authentication:Oidc:Authority/Audience`; users must be pre-provisioned by e-mail). **End users** use inbox
tokens minted by their application's backend (`POST /inapp/tokens`).

## Idempotency, correlation, errors

* Send `Idempotency-Key` (header) or `idempotencyKey` (body) on sends, schedules and events. The same key with the same
  body returns the original notification (`duplicate: true`); with a different body → `409 idempotency_key_reused`.
  The SDKs generate keys automatically, so client retries never duplicate.
* `X-Correlation-Id` is accepted and echoed; it is stored on the notification and included in webhooks and logs.
* Errors: `{ "code": "validation_failed", "message": "…", "errors": { "field": ["…"] }, "correlationId": "…" }`.
  Codes include `validation_failed` (422), `no_deliverable_channels` (422), `forbidden` (403), `not_found` (404),
  `idempotency_key_reused` (409), `quota_exceeded` (429), `rate_limited` (429 + `Retry-After`), `insufficient_credits` (402),
  `tenant_suspended`/`application_suspended` (403), `ai_*` (403).

## Notifications

| Method | Path | Notes |
|---|---|---|
| POST | `/notifications/send` | 202 Accepted (durably recorded). `dryRun: true` validates, resolves and renders without persisting. |
| POST | `/notifications/schedule` | same body, `scheduleAt` required |
| POST | `/notifications/batch` | `{ notifications: [...] }` up to 1000; each item independent |
| POST | `/notifications/{id}/cancel` | cancels scheduled/queued/retrying deliveries |
| GET | `/notifications/{id}/status` | aggregate + per-channel status |
| GET | `/notifications/{id}/history` | deliveries, immutable attempts, events |
| GET | `/notifications` | paged list (companyId, applicationId, status, notificationType, correlationId, from, to) |
| POST | `/notifications/preview` | render a template with variables/sample data |
| GET | `/deliveries`, `/deliveries/{id}`, `/deliveries/export?format=csv|xlsx` | explorer (admins and apps) |
| POST | `/deliveries/{id}/retry`, `/deliveries/{id}/resolve` | operator actions (`resend` / `mark_sent` / `mark_failed`) |

Send request fields: `applicationId?` (taken from the token for apps), `companyId`, `notificationType?`, `templateCode?`,
`recipient` / `recipients[]` (`contactId`, `userId`, `customerId`, `name`, `email`, `mobile`, `whatsApp`, `timeZone`,
`locale`, `deviceIds`, `cc`, `bcc`), `audience` (`roles`, `groupIds`, `userIds`, `allCompanyUsers`), `channels`,
`variables`, `priority`, `scheduleAt`, `expiresAt`, `idempotencyKey`, `correlationId`, `fallbacks[]` (`from`, `to`,
`templateCode`, `onStatuses`), `escalation` (`afterMinutes`, `unlessStatus`, `channel`, `templateCode`, `recipient`),
`retryPolicy`, `app` (in-app/push: `deepLink`, `imageUrl`, `icon`, `category`, `actions[]`, `timeToLiveSeconds`, `data`),
`attachments[]` (from `POST /attachments`), `metadata`.

## Other resources

* Events & rules: `POST /events`, `GET /events/{id}`, `/rules` CRUD, `POST /rules/simulate`.
* Templates: `/templates` (list, `tree`, get, create, `PUT {id}/draft`, `versions/{n}/submit|review|activate`, `retire`,
  `clone`, `test-send`, `whatsapp-submit`), `/notification-types`.
* Recipients: `PUT /contacts`, `GET /contacts`, `DELETE /contacts/{id}` (erasure), `/groups`, `/preferences`, `/consents`,
  `/suppressions`, `/devices`, public `GET|POST /u/{token}` (unsubscribe).
* Providers: `/providers` (types, CRUD with write-only secrets, `test`, `suspend`, `senders`, `sender-guidance`), `/providers/health`, `/routing-rules`.
* Tenancy/admin: `/tenants`, `/companies`, `/applications` (+`/companies`, `/credentials`), `/credentials/{id}/revoke`, `/users`,
  `/quotas`, `/cost-rates`, `/billing-rates`, `/credits`, `/suspend/{tenant|company|application|campaign}/{id}`, `/approvals`, `/audit`.
* Campaigns & schedules: `/campaigns` (+`import`, `test-send`, `submit`, `approve`, `pause`, `resume`, `cancel`, `recipients`), `/schedules`, `/holiday-calendars`.
* Communications: `/conversations` (+`/{id}`, `read`, `reply`), `/attachments`, `/webhooks` (+`attempts`, `test`, `enable`),
  `/inapp/tokens`, `/inapp/messages` (+`/{id}/read|unread|archive|dismiss|action`, `read-all`), `/inapp/branding`, `/push/events`.
* Reporting: `/dashboard`, `/reports`, `/reports/{code}?format=csv|xlsx`, `/usage?groupBy=…`, `/monitoring`.
* Provider callbacks: `GET|POST /api/v1/callbacks/{providerAccountId}` (configure this URL at the provider).
* Real time: SignalR `/hubs/inapp` (event `notification`) and `/hubs/admin` (events `delivery`, `inbound`); pass the token as `access_token`.
* MCP: `POST /mcp` — see [wings-integration.md](wings-integration.md).

## Webhooks to your application

Register with `POST /webhooks { name, url, eventTypes }` (https only outside development; private networks blocked).
Event types: `delivery.status_changed`, `notification.completed`, `inbound.message_received`, `push.interaction`,
`inapp.action`, `notification.webhook` (the Webhook *channel*). Each POST carries:

```
X-Wings-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>
Idempotency-Key: <event id>
{ "id": "<event id>", "type": "delivery.status_changed", "occurredAt": "…", "tenantId": "…", "companyId": "…", "applicationId": "…", "data": { … } }
```

Verify the signature and reject timestamps older than 5 minutes (`NotificationHubClient.VerifyWebhook`, TS `verifyWebhook`).
Retries use the same event id (deduplicate on it): up to 8 attempts with exponential backoff and jitter; endpoints failing
100 times in a row are disabled and an alert is raised.
