# Provider configuration

Providers are configured as **provider accounts** (Admin console → Providers, or `POST /api/v1/providers`):
`name`, `channel`, `providerType`, `scope` (`Shared` = Wings-managed, `Tenant`, `Company` = customer-owned), non-secret
`settings`, write-only `secrets` (AES-GCM encrypted; only key names are ever returned), `priority`, `rateLimitPerSecond`,
`monthlyCapacity`, `allowedCountryPrefixes`, `unitCost`, `failoverProviderAccountId`, `duplicateRiskPolicy`, `timeoutSeconds`.
Changing secrets of an existing account requires approval by a second administrator (`Security:RequireApprovalForCredentialChanges`).
Sender identities (from address/display name/reply-to/signature, SMS header + DLT entity id, WhatsApp phone number id) are
added per account; a company+application can pin default senders.

Each account has a callback URL: **`{PublicBaseUrl}/api/v1/callbacks/{providerAccountId}`** — configure it at the provider
for delivery receipts, bounces and inbound messages. `Hub:PublicBaseUrl` must be the externally visible https URL.

Selection order for a delivery: company-scoped → tenant-scoped → shared (if the tenant allows shared providers), filtered
by routing rules (tenant/company/application, country prefix, message class), suspension, circuit breaker, health,
destination country and monthly capacity, then `priority` and cost. Sandbox applications only use `mock` accounts.

| Type (`providerType`) | Channel | Settings | Secrets | Callbacks / notes |
|---|---|---|---|---|
| `mock` | all | `latencyMs`, `transientFailures`, `autoDeliver`, `autoRead`, `deliverAfterMs`, `supportsIdempotency`, `supportsStatusQuery`, `unitCost` | — | HMAC-signed JSON (`X-Wings-Signature`, account callback secret); simulated receipts |
| `smtp` | email | `host`, `port` (587), `security` (auto/starttls/ssl/none), `username`, `fromAddress`, `fromName`, `messageIdDomain` | `password` | no receipts (use bounce mailbox / provider events); 5xx = permanent, 4xx = transient; Microsoft 365 SMTP AUTH works here |
| `ses` | email | `region`, `configurationSet`, `fromAddress`, `fromName` | `accessKeyId`, `secretAccessKey` (or instance role) | Configuration-set event destination → SNS topic → HTTPS subscription to the callback URL. SNS signatures are verified against the AWS certificate; subscription confirmation is automatic. Delivery, Bounce (permanent → suppression), Complaint (→ suppression), Open (→ Read), Click |
| `m365` | email | `tenantId`, `clientId`, `senderMailbox`, `saveToSentItems` | `clientSecret` | Graph `sendMail` app-only; grant `Mail.Send` (application) and restrict with an Exchange application access policy |
| `twilio` | sms | `accountSid`, `messagingServiceSid` or `fromNumber`, `validityPeriodSeconds` | `authToken` | Status callback + inbound SMS URL = callback URL; `X-Twilio-Signature` verified (requires correct `PublicBaseUrl`); STOP replies → opt-out |
| `msg91` | sms | `senderId` (DLT header), `route`, `dltEntityId`, `baseUrl` | `authKey` | India: DLT entity id (sender identity or setting) and DLT content template id (template version) are mandatory for +91 |
| `http-sms` | sms | `url`, `method`, `contentType`, `bodyTemplate` with `{to} {toDigits} {from} {body} {dltEntityId} {dltTemplateId} {reference} {unicode}`, `authHeader`, `authScheme`, `messageIdPath`, `successPath`, `successValue` | `apiKey` | generic gateway adapter (many Indian aggregators) |
| `meta-whatsapp` | whatsapp | `phoneNumberId`, `businessAccountId`, `apiVersion` (v21.0), `verifyToken` | `accessToken` (system user), `appSecret` | Webhook URL = callback URL, verify token = `verifyToken`; `X-Hub-Signature-256` verified with `appSecret`; statuses (sent/delivered/read/failed + pricing), inbound messages, button replies, STOP |
| `360dialog` | whatsapp | `baseUrl` | `apiKey` | Cloud-API-compatible BSP; same webhook format (set `appSecret`/callback secret for signatures) |
| `fcm` | push, webpush | `projectId`, `androidChannelId` | `serviceAccountJson` | HTTP v1; UNREGISTERED/invalid tokens invalidate the device; acceptance ≠ device delivery |
| `apns` | push | `teamId`, `keyId`, `bundleId`, `environment` (production/sandbox) | `privateKeyP8` | token-based JWT over HTTP/2; BadDeviceToken/Unregistered invalidate the device |
| `webpush` | webpush | `vapidPublicKey`, `subject` (mailto:) | `vapidPrivateKey` | RFC 8291 aes128gcm + VAPID; 404/410 invalidate the subscription. Generate keys with `WebPushCrypto.GenerateVapidKeys()` |
| `inapp` | inapp | — | — | built-in (seeded); stores in the inbox, SignalR push |
| `webhook` | webhook | — | — | built-in (seeded); posts to the application's `notification.webhook` subscriptions |

## WhatsApp templates

Business-initiated WhatsApp messages must use templates approved by Meta. In the Template Manager set
`providerTemplateName` (lower-case, underscores), language and category (UTILITY / MARKETING / AUTHENTICATION) and the
header/footer/buttons; the body uses normal `{{Variables}}`, converted to Meta's `{{1}}, {{2}}…` automatically.
`POST /templates/{id}/whatsapp-submit` submits it to the WhatsApp Business Account; the status is recorded on the version.
Free-form text (and interactive quick-reply buttons) is only sent inside the 24-hour customer-service window after the
customer's last message — the dispatcher enforces this. Opt-in (`ChannelOptIn` consent on the WhatsApp channel) is required
for every business-initiated message.

## Email sender domains

`GET /api/v1/providers/sender-guidance?domain=example.com` returns SPF, DKIM and DMARC records to publish. Use a dedicated
subdomain for bulk/promotional mail, align the MAIL FROM/return-path domain for DMARC, and start DMARC at `p=none`.

## India (TRAI DLT)

Register the principal entity, headers (sender ids) and content templates on the DLT platform, then: set the entity id on
the SMS sender identity (or provider setting), put each template's DLT content template id on its SMS template version, and
make sure the rendered text matches the registered template exactly (variables only where `{#var#}` placeholders exist).
Promotional SMS must go through promotional headers and respects quiet hours (configured per company).
