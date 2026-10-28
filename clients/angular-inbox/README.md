# @wings/notification-hub-angular

An in-app inbox for Angular 22 apps, built on the `@wings/notification-hub-client/inbox` TypeScript client.
The package is a buildable library (ng-packagr, partial compilation, FESM2022 output).

- `<wings-inbox-bell>` is a bell button with an unread badge. It opens a dropdown panel in a CDK overlay and shows
  toasts for messages that arrive in realtime.
- `<wings-inbox-panel>` is the list on its own, for example on a full-page `/notifications` route. It has
  All / Unread / Archived tabs and category chips. Messages are styled by priority: critical messages get a red
  bar and are listed first, high-priority ones get an amber bar. The panel handles deep links, action buttons,
  read/unread, archive, dismiss and "Mark all as read".
- `<wings-inbox-toasts>` is the toast stack. Critical toasts stay until the user closes them.
- The colours follow the hub's white-label branding (`GET /api/v1/inapp/branding`: `primaryColor`, `accentColor`,
  `inboxTitle`, `logoUrl`). You can override them with CSS custom properties.
- State is held in signals (`WingsInboxStore`), so the components work with both zone.js and zoneless apps. All
  network work starts in `afterNextRender`, which makes them safe for SSR.

Peer dependencies are `@angular/core`, `@angular/common`, `@angular/router` and `@angular/cdk` (^22.2),
`@microsoft/signalr` (^10) and `@wings/notification-hub-client` (^1). The library has no Angular Material dependency.

## Build

```bash
cd clients/typescript && npm install && npm run build      # the TS client (dependency of this library)
cd ../angular-inbox && npm install && npm run build         # → dist/ (ng-packagr)
npm test                                                     # unit tests for deep-link/theme/time helpers (node --test)
```

Use it from an app by installing the two packages, either from your registry or as local tarballs:

```bash
(cd clients/typescript && npm pack) && (cd clients/angular-inbox/dist && npm pack)
npm install ./wings-notification-hub-client-1.0.0.tgz ./wings-notification-hub-angular-1.0.0.tgz @microsoft/signalr @angular/cdk
```

## Use

1. **Backend: mint inbox tokens.** Your server holds the client credential and returns an inbox token for the
   signed-in user. The browser never sees a client secret.

   ```ts
   // Node example (C#: NotificationHubClient.CreateInboxTokenAsync; see samples/AspNetCore.OrderService)
   app.get('/api/notifications/inbox-token', requireLogin, async (req, res) =>
     res.json(await hub.createInboxToken({ userId: req.user.id, companyId: req.user.companyId })));
   ```

2. **Provide the inbox** in `app.config.ts`:

   ```ts
   import { provideHttpClient } from '@angular/common/http';
   import { inject } from '@angular/core';
   import { HttpClient } from '@angular/common/http';
   import { firstValueFrom } from 'rxjs';
   import { provideWingsInbox, type InboxTokenResponse } from '@wings/notification-hub-angular';

   export const appConfig: ApplicationConfig = {
     providers: [
       provideRouter(routes),
       provideHttpClient(),
       provideWingsInbox(() => {
         const http = inject(HttpClient);
         return {
           baseUrl: environment.notificationHubUrl,                    // e.g. https://notifications.wings.example
           getToken: () => firstValueFrom(http.get<InboxTokenResponse>('/api/notifications/inbox-token')),
           // optional: pageSize, toasts, toastDurationMs, title, realtime, locale, openDeepLink
         };
       }),
     ],
   };
   ```

3. **Place the bell** in your toolbar:

   ```ts
   import { WingsInboxBellComponent } from '@wings/notification-hub-angular';

   @Component({
     selector: 'app-toolbar',
     imports: [WingsInboxBellComponent],
     template: `<header>… <wings-inbox-bell (messageOpened)="onOpened($event)" (actionInvoked)="onAction($event)" /></header>`,
   })
   ```

   Inputs are `ariaLabel` (default "Notifications"), `panelWidth` (default 380), `showToasts` and `closeOnOpen`.
   Outputs are `messageOpened` (an `InAppMessage`) and `actionInvoked` (`{ message, action }`). The hub also sends
   an `inapp.action` webhook to your backend when an action is pressed.

4. **Allow your origin on the hub.** Set `Cors__Origins__0=https://erp.example.com` so the browser can call the
   REST API and open the SignalR connection (`/hubs/inapp`, event `notification`).

### Deep links

When a message is clicked, it is marked read and its `deepLink` is followed. An action button follows the
action's own `deepLink`, falling back to the message's link.

| Link | Behaviour |
|---|---|
| `/sales/orders/SO-1` or a same-origin `https://…` URL | `Router.navigateByUrl` (no page reload) |
| Other `http(s)://` URL | Opens in a new tab with `noopener,noreferrer` |
| `javascript:`, `data:`, `//host`, custom schemes, `#` | Ignored (a console warning is logged) |

Pass `openDeepLink(url, message)` to handle links yourself, for example custom schemes in Capacitor/Electron.
Return `false` from it to fall back to the default behaviour.

### Theming

Branding from the hub is applied automatically. Override it, or style the parts branding does not cover, with
these custom properties:

```css
wings-inbox-bell, wings-inbox-panel, wings-inbox-toasts {
  --wings-inbox-primary: #00695c;     /* header, active tab, unread tint, action buttons */
  --wings-inbox-on-primary: #fff;
  --wings-inbox-surface: #fff;  --wings-inbox-text: #1f1f1f;  --wings-inbox-muted: #5f6368;  --wings-inbox-border: #e3e3e3;
  --wings-inbox-critical: #c62828;  --wings-inbox-high: #ef6c00;  --wings-inbox-badge: #d32f2f;
  --wings-inbox-bell-color: currentColor;  --wings-inbox-font: Roboto, sans-serif;
}
```

The dropdown is a CDK overlay attached to `<body>`. Set the overrides globally (`:root`, or on `.cdk-overlay-pane
wings-inbox-panel`) if the panel needs them as well.

### Building your own UI

Inject `WingsInboxStore` to get the state without the bundled components. It exposes these signals:
`messages`, `unreadCount`, `branding`, `theme`, `view`, `category`, `categories`, `loading`, `error`,
`connection` and `toasts`. Its methods are `start()`, `reload()`, `loadMore()`, `setView()`, `setCategory()`,
`markRead()`, `markUnread()`, `archive()`, `dismiss()`, `markAllRead()`, `open()` and `act()`. The underlying
`InboxClient` is available as `store.client`.
