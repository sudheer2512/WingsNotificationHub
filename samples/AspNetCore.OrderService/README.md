# Sample: ASP.NET Core order service

This is a minimal API that plays the role of an *application backend*, such as an ERP order module. It calls
Wings Notification Hub through the C# SDK (`src/Wings.NotificationHub.Client`, referenced as a project).

| Endpoint | What it shows |
|---|---|
| `POST /orders/{orderNumber}/notify` `{ userId, amount?, approvedBy?, channels?, idempotencyKey? }` | Sends an `ORDER_APPROVED` notification (in-app by default, push optional) directly, with a deep link, action buttons, metadata and a **stable idempotency key**. Repeating the call returns the same notification with `duplicate: true`. |
| `POST /orders/{orderNumber}/approve` `{ requesterUserId, amount, approvedBy }` | Raises the `salesorder.approved` **business event**. The hub's rule ("Sales order approved → in-app + push") picks the channels and recipients. The idempotency key is `salesorder.approved:{orderNumber}`. |
| `GET /notifications/{id}` | Notification status, passed through from the hub. |
| `GET /me/inbox-token` | Mints a short-lived **inbox token** for the signed-in user. The Angular, WinForms and Ionic inbox clients call this endpoint, so they never hold a client secret. |
| `POST /me/devices` `{ platform, token, deviceName?, appVersion?, locale?, p256dh?, auth? }` | Registers the user's push device with the hub for the mobile app (see `samples/ionic`). |
| `POST /webhooks/notification-hub` | Receives hub webhooks. It verifies `X-Wings-Signature` (`WebhookVerifier.Verify`, HMAC-SHA256 over `"{t}.{rawBody}"`, 5-minute window) against the **raw body**, de-duplicates on the event id or `Idempotency-Key`, and handles `delivery.status_changed`, `notification.completed`, `inapp.action` and `push.interaction`. |
| `GET /webhooks/events[?type=]` | The last verified webhook events (demo only). |

> **Authentication is a demo stub.** The current user comes from an `X-User-Id` header (`DemoAuth`). Replace it
> with your real cookie/JWT authentication. The inbox-token and device endpoints must only ever act for the
> authenticated user.

## Configure

| Setting | Meaning |
|---|---|
| `NotificationHub:BaseUrl` | Hub URL, e.g. `http://localhost:5480` |
| `NotificationHub:ClientId` / `ClientSecret` | This application's client credential. Use user-secrets or environment variables, never `appsettings.json`. |
| `NotificationHub:CompanyId` | The company this service sends for |
| `NotificationHub:WebhookSigningSecret` | Secret of this service's webhook subscription (it is returned once, when the webhook is registered) |
| `NotificationHub:RegisterWebhookOnStartup` + `PublicBaseUrl` | Development convenience: registers `{PublicBaseUrl}/webhooks/notification-hub` at startup and keeps the secret in memory. Each start creates a new subscription. |
| `Cors:Origins` | Origins of browser/mobile apps that call `/me/*` |

The hub's `NotificationHubClient` is registered as a **singleton** because it caches the OAuth token. Do not
create one per request, and do not register it as a transient typed `HttpClient`.

## Run against a local hub

```bash
# 1. Start the hub (Development seeds demo data and writes App_Data/dev-credentials.env).
dotnet run --project src/Wings.NotificationHub.Api          # http://localhost:5480

# 2. Start the sample with the seeded ERP credential.
set -a; . App_Data/dev-credentials.env; set +a
cd samples/AspNetCore.OrderService
dotnet user-secrets set NotificationHub:ClientId     "$ERP_AUTO_DEMO_CLIENT_ID"
dotnet user-secrets set NotificationHub:ClientSecret "$ERP_AUTO_DEMO_CLIENT_SECRET"
dotnet user-secrets set NotificationHub:CompanyId    "$DEMO_COMPANY_ABC_ID"
NotificationHub__RegisterWebhookOnStartup=true dotnet run   # http://localhost:5492
```

On Windows PowerShell, set `$env:NotificationHub__ClientId = "…"` and so on instead.

Then try the requests in `OrderService.http`, or:

```bash
curl -X POST localhost:5492/orders/SO-1001/notify  -H 'Content-Type: application/json' -d '{"userId":"u-1001","amount":18450}'
curl -X POST localhost:5492/orders/SO-1002/approve -H 'Content-Type: application/json' -d '{"requesterUserId":"u-1001","amount":184500,"approvedBy":"Rahul Verma"}'
curl localhost:5492/me/inbox-token -H 'X-User-Id: u-1001'
curl localhost:5492/webhooks/events        # delivery.status_changed / notification.completed arrive within a few seconds
```

Webhook URLs must be `https` in production. The hub only accepts `http`/loopback targets when
`Webhooks:AllowHttp` and `Webhooks:AllowPrivateNetworks` are true, which is the default in Development.

## Error handling

`HubErrorMiddleware` turns `NotificationHubException` into a JSON error. Caller mistakes (400/404/409/422) keep the
hub's status. Credential problems and hub outages (401/403/5xx) become `502`, because they are the service's
problem, not the caller's. Every error includes the hub's `correlationId`.

## Not covered by the C# SDK

The SDK has no methods for `DELETE /api/v1/devices/{id}` or `DELETE /api/v1/webhooks/{id}`, so this sample does not
unregister devices or clean up auto-registered webhooks. The TypeScript SDK has both
(`unregisterDevice`, `deleteWebhook`).
