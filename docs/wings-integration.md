# Wings ecosystem integration

## Wings MCP Server / AI agents

`POST /mcp` implements the Model Context Protocol (JSON-RPC 2.0, protocol `2025-06-18`, single-response HTTP). Wings MCP
Server (or any MCP client) authenticates with an application client credential that has `IsAiAgent = true` and the `mcp`
scope. Agents never see provider credentials.

| Tool | Scope | Purpose |
|---|---|---|
| `notification.send` | notifications.send | request a registered notification type for one known contact |
| `notification.schedule` | notifications.send | same, at `scheduleAt` |
| `notification.get_status` / `notification.get_history` | notifications.read | status / timeline |
| `notification.list_templates` / `notification.preview_template` | templates.read | discover and render templates |
| `notification.get_preferences` | preferences.read | preferences and consent state |
| `notification.register_device` | devices.write | push device registration |
| `notification.get_usage` | usage.read | usage and estimated cost |
| `notification.get_provider_health` | providers.health | provider health (names/status only) |

Each tool publishes a JSON schema (`tools/list`); arguments are validated (required fields, unknown fields, enums) before
execution. AI-specific restrictions are enforced by the notification service:

* only registered notification types with `aiAllowed = true`; `isSensitive` types (e.g. OTP, payroll) are refused;
* exactly one recipient, which must be a **registered contact** (`contactId`, `userId` or `customerId`) — raw e-mail
  addresses/phone numbers, cc/bcc and audiences are refused;
* no promotional messages, no batch/campaign endpoints;
* types with `requiresHumanApprovalForAi` return `pending_approval`; an administrator approves in *Approvals*, after which
  the message is rendered and delivered under the original request id (the decision is audited);
* consent, preferences, quotas and template validation apply exactly as for any other sender.

## Wings Code generated applications

A generated application declares its notifications in a manifest (`NotificationManifest`, example and JSON schema in
`samples/wings-code`) and registers it during deployment with a tenant **provisioning credential**
(scope `applications.register`):

```csharp
var hub = new NotificationHubClient(http, new() { BaseAddress = new(hubUrl), ClientId = provisioningId, ClientSecret = provisioningSecret });
var result = await hub.RegisterManifestAsync(manifest);   // idempotent upsert keyed by application code
// result.ClientId / result.ClientSecret: the app's own credential (returned once) → store in the deployment secret store
```

Registration (1) creates/updates the application and links it to the listed companies, (2) upserts notification types and
the events they correspond to, (3) registers templates as drafts (auto-approved for `sandbox` applications so tests can run
against mock providers before deployment), (4) restricts the permitted channels, (5) provisions a scoped credential,
(6) registers delivery/reply webhooks (signing secrets returned once). Re-running with an unchanged manifest changes nothing.

At runtime the generated code calls:

```csharp
await notificationHub.SendAsync(new
{
    NotificationType = "ORDER_APPROVED",
    RecipientUserId = userId,
    Variables = new { OrderNumber = orderNumber, OrderAmount = amount },
});
```

Other SDK operations: `PublishEventAsync`, `GetStatusAsync`, `GetHistoryAsync`, `CreateInboxTokenAsync` (for the in-app
inbox component), `RegisterDeviceAsync`, `RegisterWebhookAsync`, `VerifyWebhook`, `PreviewSendAsync` (dry run — test
notification workflows before deployment). TypeScript equivalents are in `clients/typescript`; UI components for the
inbox in `clients/angular-inbox` and the WinForms library in `samples/WinForms`.

## Standalone SaaS use

Nothing in the hub depends on Wings ERP: any organisation can be a tenant, use its own (customer-owned) provider accounts
or the shared ones, and integrate through the REST API, SDKs, webhooks and MCP.
