# Sample: Wings Code integration (notification manifest)

A Wings Code generated application describes its notifications in a manifest and registers it with the hub during
deployment. The manifest covers the application itself, the companies that use it, its channels, notification
types, templates, webhooks and, optionally, a client credential. Registration is an **idempotent upsert keyed by
`application.code`**, so a pipeline can run it on every deployment.

| File | Purpose |
|---|---|
| `notification-manifest.json` | Example manifest for a generated *Wings Field Service* app: 3 notification types, 5 templates (in-app, push, WhatsApp, SMS, HTML email), 1 webhook, 1 credential |
| `notification-manifest.schema.json` | JSON Schema (draft 2020-12) for `NotificationManifest` (`src/Wings.NotificationHub.Contracts/Manifest.cs`). Reference it with `"$schema"` to get editor completion and validation. |
| `register-manifest.mjs` | Zero-dependency Node.js 20+ registration script for CI/CD |
| `register-manifest.ps1` | The same script for Windows/PowerShell 7 pipelines |

## Register

The hub endpoint is `POST /api/v1/registrations/manifest`. It requires a **provisioning credential** whose token has
the `applications.register` scope. The tenant is taken from the credential. The development hub seeds one such
credential as `WINGS_CODE_PROVISIONING_CLIENT_ID` / `_SECRET` in `App_Data/dev-credentials.env`.

```bash
set -a; . App_Data/dev-credentials.env; set +a        # local demo credentials
WNH_URL=http://localhost:5480 node samples/wings-code/register-manifest.mjs samples/wings-code/notification-manifest.json \
  --secrets-out .wnh-secrets.json
```

```text
✔ WINGS-FIELD-SERVICE: application 6b96df4e-… created
  notification types upserted: 3
  templates registered:        5 (drafts unless sandbox)
  webhooks registered:         1
  new credential client id:    wings-field-service-ddxu4b9j
  ⚠ Templates were registered as drafts. Submit and approve them in the Template Manager before production use.
  secrets written to .wnh-secrets.json (…)
```

Running it again reports `updated`, `templates registered: 0 (unchanged)` and `webhooks registered: 0`.

**One-time secrets.** On the first registration the hub returns the new application's `clientSecret` and the signing
secret of each new webhook. It never shows them again. The script writes them only to `--secrets-out` (mode 0600) so
the pipeline can move them into its secret store (Key Vault, Vault, GitHub/Azure DevOps secrets). Without
`--secrets-out` they are discarded. Use `--strict` to fail the step (exit code 2) when the hub returns warnings.

You can also register from code with the SDKs:

```ts
await hub.registerManifest(manifest);                       // @wings/notification-hub-client
```
```csharp
await hub.RegisterManifestAsync(manifest);                  // Wings.NotificationHub.Client (NotificationManifest contract)
```

## What the hub does with it

- **Application** is created or updated by code, and the channel allow-list is set from `channels`.
  `sandbox: true` routes the application to mock providers only and auto-approves its templates, which is useful for
  pre-deployment test runs.
- **Companies** are linked by code. Unknown codes are skipped with a warning.
- **Notification types** are upserted (category, message class, default channels/template/priority, AI flags).
- **Templates** are created as **Draft** versions, or get a new draft when the subject or body changed, and go
  through the normal review/approval workflow. A template's channel must be listed in `channels`.
- **Webhooks** are created when no subscription with the same URL exists. URLs must be `https` in production.
- **Credential** is created only when the application has no active credential. `applications.register` is never
  granted to it.

Validation errors come back as `422 validation_failed`, with the details under `errors.manifest[]`. The script prints them.

## Validate locally

```bash
npx --yes -p ajv-cli@5 -p ajv-formats@3 ajv validate --spec=draft2020 -c ajv-formats -s notification-manifest.schema.json -d notification-manifest.json
```

The schema enforces the same rules as the hub: the code pattern `^[A-Za-z0-9_.-]{2,100}$`, at most 500 templates,
known channels, scopes and event types, and https webhooks. The one rule it cannot express is "a template channel
must be in `channels`"; the hub checks that.
