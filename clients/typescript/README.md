# @wings/notification-hub-client

TypeScript SDK for Wings Notification Hub. ESM, ships its own types, uses `fetch`, no runtime dependencies
(`@microsoft/signalr` is an optional peer dependency for the realtime inbox).

| Entry point | Runs in | Purpose |
|---|---|---|
| `@wings/notification-hub-client` | Node.js 20+ (server) | `NotificationHubClient`: the TypeScript version of the C# SDK, plus `verifyWebhook` |
| `@wings/notification-hub-client/inbox` | Browser, Electron, Capacitor | `InboxClient`: an end user's in-app inbox, authenticated with an inbox token |
| `@wings/notification-hub-client/webhooks` | Node.js | `verifyWebhook`, `parseWebhookEvent`, `signWebhook` only |

> **Never put a client secret in browser or mobile code.** Your backend holds the client credential and mints
> short-lived inbox tokens (`createInboxToken`) for its signed-in users. Browsers only ever see inbox tokens.

## Install

```bash
npm install @wings/notification-hub-client            # from a registry, or:
npm install ../path/to/clients/typescript             # local checkout (run `npm run build` there first)
npm install @microsoft/signalr                         # only if you use InboxClient.connect()
```

## Server client

```ts
import { NotificationHubClient, ValidationError, RateLimitError } from '@wings/notification-hub-client';

const hub = new NotificationHubClient({
  baseUrl: process.env.WNH_URL!,                 // e.g. https://notifications.wings.example
  clientId: process.env.WNH_CLIENT_ID!,
  clientSecret: process.env.WNH_CLIENT_SECRET!,
  defaultCompanyId: process.env.WNH_COMPANY_ID,  // used when a request has no companyId
});

// 202 = durably accepted (not yet delivered). An idempotency key is generated and written back to the request.
const sent = await hub.send({
  notificationType: 'ORDER_APPROVED',
  recipient: { userId: 'u-1001' },
  channels: ['inapp', 'push'],
  variables: { OrderNumber: 'SO-2231', OrderAmount: 184500, ApprovedBy: 'Rahul Verma' },
  app: { deepLink: '/sales/orders/SO-2231', actions: [{ id: 'view', label: 'View order' }] },
});

const status = await hub.getStatus(sent.notificationId);
await hub.publishEvent({ eventType: 'salesorder.approved', payload: { OrderNumber: 'SO-2231', requester: { userId: 'u-1001' } } });
const { accessToken, expiresAt, hubUrl } = await hub.createInboxToken({ userId: 'u-1001', lifetimeMinutes: 60 });
```

Available methods (they correspond to the C# `NotificationHubClient`):

| Area | Methods |
|---|---|
| Notifications | `send`, `sendBatch`, `previewSend` (dry run), `getStatus`, `getHistory`, `cancel`, `listNotifications`, `waitForCompletion` |
| Events and templates | `publishEvent`, `listTemplates`, `previewTemplate` |
| Recipients | `upsertContact`, `registerDevice`, `listDevices`, `unregisterDevice`, `getPreferences`, `updatePreferences`, `recordConsent` |
| In-app and push | `createInboxToken`, `reportPushInteraction` |
| Webhooks and registration | `registerWebhook`, `listWebhooks`, `deleteWebhook`, `testWebhook`, `registerManifest` |
| Operations | `getUsage`, `getProviderHealth`, `getAccessToken`, `request` (any other endpoint) |

Every method takes an optional last argument `{ signal, headers }`.

### What the client handles for you

- **Tokens.** Gets a `client_credentials` token from `POST /connect/token` and caches it. It refreshes the token
  60 s before expiry, and once more after a `401`. Concurrent calls share a single token request.
- **Idempotency.** `send`, `sendBatch` items and `publishEvent` get a random key when they have none. The key is
  sent in the body and in the `Idempotency-Key` header and written back to your request object, so a retry
  (by the SDK or by you with the same object) never sends a duplicate.
- **Retries.** `429`, `502`, `503`, `504` and network errors are retried up to `maxRetries` (default 3).
  The client honours `Retry-After` (seconds or an HTTP date, capped by `maxRetryDelayMs`). Otherwise it backs off
  exponentially with jitter (200 ms·2ⁿ + 0–200 ms). Non-idempotent registrations (`registerWebhook`,
  `recordConsent`, `testWebhook`) are only retried on `429`.
- **Typed errors.** Every error is a `NotificationHubError` carrying `status`, `code`, `message`, `errors`
  (field messages), `correlationId` and `body`. The subclasses are:
  `ValidationError` (400/422), `AuthenticationError` (401 and token-endpoint failures such as `invalid_client`),
  `PermissionDeniedError` (403), `NotFoundError` (404), `ConflictError` (409), `RateLimitError` (429, `retryAfterMs`),
  `ServerError` (5xx) and `NetworkError` (no response; `code` is `timeout` or `network_error`).
- **Timeouts.** Each attempt is aborted after `timeoutMs` (default 30 s). Pass `signal` to cancel a call.

### Verifying webhooks

The hub signs every webhook with `X-Wings-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>`.
Verify it against the **raw** body, before you parse the JSON. Signatures more than 5 minutes old (or ahead) are rejected.

```ts
import express from 'express';
import { verifyWebhook, type WebhookEventEnvelope } from '@wings/notification-hub-client';

app.post('/webhooks/notification-hub', express.raw({ type: 'application/json' }), (req, res) => {
  if (!verifyWebhook(process.env.WNH_WEBHOOK_SECRET!, req.body, req.header('X-Wings-Signature'))) return res.sendStatus(401);
  const event = JSON.parse(req.body.toString('utf8')) as WebhookEventEnvelope;
  // De-duplicate on event.id or the Idempotency-Key header: deliveries are at-least-once.
  res.sendStatus(200);
});
```

`parseWebhookEvent(secret, rawBody, header)` verifies and parses in one step; it throws `WebhookVerificationError`.
`signWebhook(secret, body)` creates valid headers for testing your receiver.

## Browser inbox client

```ts
import * as signalR from '@microsoft/signalr';
import { InboxClient } from '@wings/notification-hub-client/inbox';

const inbox = new InboxClient({
  baseUrl: 'https://notifications.wings.example',
  signalR,                                         // optional; imported dynamically when omitted
  // Your backend calls hub.createInboxToken({ userId }) for the signed-in user and returns the result.
  getToken: () => fetch('/api/notifications/inbox-token', { credentials: 'include' }).then((r) => r.json()),
});

const page = await inbox.list({ pageSize: 20 });        // { unread, items } (critical first, newest first)
inbox.onUnreadCountChange((n) => setBadge(n));
inbox.onNotification((m) => showToast(m.title));
await inbox.connect();                                  // SignalR /hubs/inapp, event "notification"

await inbox.markRead(id);  await inbox.markUnread(id);  await inbox.archive(id);  await inbox.dismiss(id);
await inbox.performAction(id, 'approve');               // raises an inapp.action webhook to your backend
await inbox.markAllRead();
const branding = await inbox.getBranding();             // { inboxTitle, primaryColor, … } white-label settings
await inbox.reportPushInteraction(deliveryId, 'opened'); // push open tracking from the device
```

- `getToken` may return a raw JWT string or `{ accessToken, expiresAt, hubUrl }`, which is the hub's
  `InboxTokenResponse` passed through unchanged. The client refreshes it 30 s before expiry (it reads `exp` from the
  JWT when no `expiresAt` is given) and again after a `401`. SignalR reconnects use the same token source.
- The unread count is kept current from `list()`, from your own mutations (with no extra request when the
  message's previous state is known) and from realtime notifications. It is re-read from the hub after a reconnect.
- The hub must allow your app's origin in `Cors:Origins` for both REST calls and SignalR.

## Development

```bash
npm install
npm run build      # tsc → dist/ (ESM + .d.ts)
npm test           # vitest: token refresh, idempotency keys, retries, error mapping, webhook verification, inbox
npm run typecheck  # also type-checks the tests
```

### Hub behaviours this SDK works around

- Query-string enum binding on the hub is case-sensitive: `?channel=inapp` returns 400, `?channel=InApp` works.
  `listTemplates`/`getUsage` therefore send the enum member names. JSON bodies keep the lower-case wire names.
- `POST /api/v1/inapp/messages/{id}/{read|unread|archive|dismiss}` only matches when the request has a JSON body.
  Without one, the request falls through to the `/api/{**path}` fallback and returns a 404. The inbox client always
  sends `{}`.
