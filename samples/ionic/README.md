# Sample: Ionic / Capacitor mobile app

This is a small Ionic 9 + Angular 22 + Capacitor 8 app. It shows how a mobile app connects to Wings Notification
Hub **without ever holding a client secret**:

| Concern | How | Files |
|---|---|---|
| Push registration | `@capacitor/push-notifications` asks for permission, creates the Android channel and registers with FCM/APNs. The token goes to **the app's own backend** (`POST /me/devices`), which calls the hub's `POST /api/v1/devices` with its client credential. | `src/app/push.service.ts`, `src/app/backend.service.ts` |
| Push open tracking | On `pushNotificationActionPerformed` the app reports `opened` with `POST /api/v1/push/events { deliveryId, interaction }`. When a push arrives in the foreground it reports `received`. The call is authenticated with the user's **inbox token**; the hub checks that the device belongs to that user. A tap marks the delivery `Read`. | `src/app/push.service.ts` |
| Deep links | Hub push payloads carry `deliveryId` and `deepLink` in `notification.data`. Only in-app routes (`/sales/orders/SO-1`) are followed. | `push.service.ts`, `order.page.ts` |
| In-app inbox | The `InboxClient` from `@wings/notification-hub-client/inbox` handles list, read/unread, archive, dismiss, action buttons, mark all read, the unread badge and live SignalR updates. Inbox tokens come from the backend (`GET /me/inbox-token`). | `src/app/inbox.service.ts`, `src/app/inbox.page.ts` |

The backend used here is `samples/AspNetCore.OrderService`, which provides `/me/inbox-token` and `/me/devices`.
Its `X-User-Id` header is a demo stand-in for your real session.

## Run

```bash
cd clients/typescript && npm install && npm run build      # the SDK is referenced as file:../../clients/typescript
cd ../../samples/ionic && npm install
# edit src/environments/environment.ts: notificationHubUrl, backendUrl, androidChannelId
npm start                       # browser preview (no native push; the inbox works)
npm run build && npx cap add android && npx cap add ios && npx cap sync
npx cap open android            # or: npx cap open ios
```

`npm run build` for the web target was checked on macOS. The native projects were **not** generated or run here,
because there is no Android SDK, Xcode or device in that environment.

## Native setup checklist

**Android (FCM)**
- Put `google-services.json` in `android/app/`. In the hub, configure an FCM provider account with the Firebase
  service account and set the provider setting `androidChannelId` to `wings-default`, the same value as
  `environment.androidChannelId`.
- The hub's FCM payload sets `click_action: "OPEN_DEEP_LINK"` when a notification has a deep link. Android then
  opens the activity that declares that intent action. **Add this intent filter to `MainActivity`** in
  `android/app/src/main/AndroidManifest.xml`. Without it, tapping such a notification does nothing:

  ```xml
  <intent-filter>
      <action android:name="OPEN_DEEP_LINK" />
      <category android:name="android.intent.category.DEFAULT" />
  </intent-filter>
  ```
- Android 13 and later need the `POST_NOTIFICATIONS` permission. Capacitor's plugin requests it through
  `requestPermissions()`.

**iOS (APNs)**
- Enable the *Push Notifications* capability and *Background Modes → Remote notifications* in Xcode.
- Add the AppDelegate forwarding methods described in the
  [@capacitor/push-notifications docs](https://capacitorjs.com/docs/apis/push-notifications) (`didRegisterForRemoteNotificationsWithDeviceToken` and the failure handler).
- On iOS the plugin returns the **APNs device token**, which is what the hub's `apns` provider expects (platform `ios`).
  Configure an APNs provider account on the hub (key, team id, bundle id `com.wings.erp.sample`, sandbox or production).

**Hub**
- Add `capacitor://localhost` (iOS) and `http://localhost` (Android) to `Cors:Origins` so the inbox REST calls and
  SignalR work from the WebView.
- Add the same origins to the backend's `Cors:Origins`.

## Notes

- Registration runs on every app start. The hub upserts by token hash, so a token that moves to another user
  (shared device, re-login) is re-bound rather than duplicated.
- On logout, unregister the device (`DELETE /api/v1/devices/{id}` through your backend) and stop the inbox
  (`inbox.client.dispose()`). The sample backend does not have an unregister endpoint, because the C# SDK has no
  delete-device method; the TypeScript SDK has `unregisterDevice`.
- Browser/PWA push would use Web Push: `platform: 'web'` with the subscription endpoint as `token` plus `p256dh`/`auth`.
  This sample does not cover it.
