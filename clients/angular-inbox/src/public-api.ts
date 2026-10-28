/*
 * Public API of @wings/notification-hub-angular.
 */
export { provideWingsInbox } from './lib/provide';
export { WINGS_INBOX_CONFIG, type WingsInboxConfig } from './lib/inbox.config';
export { WingsInboxStore, type InboxView } from './lib/inbox.store';
export { WingsInboxBellComponent, type InboxActionEvent } from './lib/inbox-bell.component';
export { WingsInboxPanelComponent } from './lib/inbox-panel.component';
export { WingsInboxToastsComponent } from './lib/inbox-toasts.component';
export { WingsRelativeTimePipe } from './lib/relative-time.pipe';
export { resolveDeepLink, inboxTheme, safeColor, contrastText, relativeTime, badgeText, type DeepLinkTarget, type InboxTheme } from './lib/utils';
export type { InAppMessage, AppAction, InboxBranding, InboxTokenResponse, InboxTokenResult, InboxConnectionState } from '@wings/notification-hub-client/inbox';
