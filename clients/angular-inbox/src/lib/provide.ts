import { type EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import { WINGS_INBOX_CONFIG, type WingsInboxConfig } from './inbox.config';
import { WingsInboxStore } from './inbox.store';

/**
 * Registers the inbox for the application. The factory form runs in an injection context, so it can use inject():
 *
 * ```ts
 * provideWingsInbox(() => {
 *   const http = inject(HttpClient);
 *   return {
 *     baseUrl: environment.notificationHubUrl,
 *     getToken: () => firstValueFrom(http.get<InboxTokenResponse>('/api/notifications/inbox-token')),
 *   };
 * })
 * ```
 */
export function provideWingsInbox(config: WingsInboxConfig | (() => WingsInboxConfig)): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: WINGS_INBOX_CONFIG, useFactory: typeof config === 'function' ? config : () => config },
    WingsInboxStore,
  ]);
}
