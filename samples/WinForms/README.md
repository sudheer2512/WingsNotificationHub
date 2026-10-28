# Windows Forms: in-app inbox library and sample

| Project | Type | Contents |
|---|---|---|
| `Wings.NotificationHub.WinForms` | Class library, `net10.0-windows`, `UseWindowsForms` | `InboxClient` (REST + SignalR via `Microsoft.AspNetCore.SignalR.Client`), `NotificationBellControl`, `InboxPanel`, `InboxForm`, `ToastForm` |
| `Wings.NotificationHub.WinForms.Sample` | WinExe | A "Wings ERP" window with the bell in its title bar, an event log, and buttons that make the sample backend send notifications |

Both projects set `<EnableWindowsTargeting>true</EnableWindowsTargeting>`, so they **build on macOS/Linux**
(`dotnet build Wings.NotificationHub.WinForms.slnx`). They only **run on Windows**: Windows Forms needs the Windows
Desktop runtime.

## What the library provides

- **`InboxClient`** is the desktop equivalent of the TypeScript `InboxClient`. It has no UI dependency, so it can
  also be used from WPF or a console app.
  - `ListAsync`, `MarkReadAsync`, `MarkUnreadAsync`, `ArchiveAsync`, `DismissAsync`, `PerformActionAsync`,
    `MarkAllReadAsync`, `GetBrandingAsync`, `RefreshUnreadCountAsync`, `ConnectAsync`/`DisconnectAsync`
    (SignalR `/hubs/inapp`, event `notification`, automatic reconnect).
  - It tracks the unread count locally and resyncs it after a reconnect. The events are `NotificationReceived`,
    `UnreadCountChanged`, `ConnectionStateChanged` and `BackgroundError`. **They are raised on the UI thread**:
    the client captures the `SynchronizationContext` it was created on.
  - Tokens come from `InboxClientOptions.TokenProvider`, which should be **your backend's** inbox-token endpoint.
    `InboxClient.FromBackend(http, "me/inbox-token")` is a ready-made provider. Tokens are cached, refreshed
    30 s before they expire and refreshed again after a 401. The desktop app never holds a client secret.
  - It retries 429/5xx and network errors and raises `InboxClientException` with `StatusCode`, `Code` and `CorrelationId`.
- **`NotificationBellControl`** is a custom-painted bell with an unread badge (99+) and an amber "reconnecting" dot.
  Click it, or press Enter/Space, to open the inbox in a `ToolStripDropDown`. New messages pop up as toasts.
  Properties: `Client`, `WebBaseAddress`, `PanelSize`, `ShowToasts`, `ToastDuration`, `CloseOnOpen`, `BadgeColor`.
  Events: `MessageOpened`, `ActionInvoked`, `DeepLinkRequested`.
- **`InboxPanel`** shows a branded header (title and colour come from `/api/v1/inapp/branding`) with
  "Mark all as read", and All / Unread (n) / Archived tabs. Its message cards carry priority strips (critical red,
  high amber), category and relative time, action buttons, and a ⋯ / right-click menu (read/unread, archive,
  dismiss). New messages are inserted live and "Load more" pages through older ones. Keyboard: Enter opens a
  message, Delete dismisses it.
- **Deep links.** `DeepLinkRequested` lets the host app navigate internally (set `e.Handled = true`). Otherwise
  http(s) links open in the default browser, and relative links (`/sales/orders/42`) are opened against
  `WebBaseAddress` when one is set. Other schemes are ignored.
- **`ToastForm`** is a bottom-right, non-activating toast. At most 3 are stacked. Critical toasts stay until closed.
- **`InboxForm`** is a resizable window that hosts an `InboxPanel`, for a "Notifications" menu item.

```csharp
var backend = new HttpClient { BaseAddress = new Uri("https://erp.example.com/") };   // your API, with the user's auth
var inbox = new InboxClient(new InboxClientOptions
{
    BaseAddress = new Uri("https://notifications.example.com/"),
    TokenProvider = InboxClient.FromBackend(backend, "api/notifications/inbox-token"),
});
bell.Client = inbox;                         // NotificationBellControl dropped on a form/toolbar
bell.DeepLinkRequested += (_, e) => { if (Navigate(e.Url)) e.Handled = true; };
await bell.StartAsync();                     // branding + unread count + realtime connection
```

## Run the sample on Windows

Prerequisites: Windows 10/11, the .NET 10 SDK (or the .NET 10 Desktop Runtime to run a published build).

1. Start the hub (Development seeds demo data and writes `App_Data\dev-credentials.env`):
   ```powershell
   dotnet run --project src\Wings.NotificationHub.Api          # http://localhost:5480
   ```
2. Start the sample backend, which mints inbox tokens and sends notifications. Copy the values from `dev-credentials.env`:
   ```powershell
   $env:NotificationHub__ClientId     = '<ERP_AUTO_DEMO_CLIENT_ID>'
   $env:NotificationHub__ClientSecret = '<ERP_AUTO_DEMO_CLIENT_SECRET>'
   $env:NotificationHub__CompanyId    = '<DEMO_COMPANY_ABC_ID>'
   dotnet run --project samples\AspNetCore.OrderService           # http://localhost:5492
   ```
3. Run the desktop sample:
   ```powershell
   dotnet run --project samples\WinForms\Wings.NotificationHub.WinForms.Sample
   ```
   Settings are read from `appsettings.json` next to the exe (`HubBaseUrl`, `BackendBaseUrl`, `UserId`,
   `WebBaseUrl`). You can override them with `WINGS_SAMPLE_HUB_URL`, `WINGS_SAMPLE_BACKEND_URL`,
   `WINGS_SAMPLE_USER_ID` and `WINGS_SAMPLE_WEB_URL`.
4. Click **Send test notification**: a toast appears, the badge increments, and the dropdown shows the message
   with *View order* / *Mark shipped* buttons. **Approve order (business event)** goes through the hub's
   `salesorder.approved` rule. Pressing an action button raises an `inapp.action` webhook, which the order service
   logs (see `GET /webhooks/events`).

To publish a single folder: `dotnet publish samples\WinForms\Wings.NotificationHub.WinForms.Sample -c Release -r win-x64 --self-contained false -o out`.

## What was verified

- Both projects build on macOS with no warnings (`dotnet build Wings.NotificationHub.WinForms.slnx`).
- `InboxClient.cs` was compiled into a net10.0 console harness and run against a live hub. It listed messages,
  loaded branding, connected over SignalR, received a critical message with actions, and ran
  read/unread/action/archive/dismiss/read-all. The unread count went 0→1→0→1→0, and the token provider was called once.
- **Not verified:** the Windows Forms UI (bell painting, dropdown, cards, toasts) was never run. That needs Windows.
