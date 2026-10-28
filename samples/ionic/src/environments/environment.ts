export const environment = {
  /** Wings Notification Hub (inbox REST + SignalR). Its Cors:Origins must include capacitor://localhost and http://localhost. */
  notificationHubUrl: 'https://notifications.wings.example',
  /** YOUR application backend (e.g. samples/AspNetCore.OrderService). It holds the hub client secret, not the app. */
  backendUrl: 'https://erp.wings.example',
  /** Android notification channel; must match the hub FCM provider's androidChannelId setting. */
  androidChannelId: 'wings-default',
};
