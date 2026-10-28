import { InjectionToken } from '@angular/core';
import type { InAppMessage, InboxTokenResult, SignalRModule } from '@wings/notification-hub-client/inbox';

export interface WingsInboxConfig {
  /** Base URL of Wings Notification Hub. The hub must allow your app's origin in Cors:Origins. */
  baseUrl: string;
  /**
   * Returns an inbox token for the signed-in user from YOUR backend (which calls
   * POST /api/v1/inapp/tokens with its client credential). Never put a client secret in the browser.
   */
  getToken: () => Promise<InboxTokenResult>;
  /** Live updates over SignalR (default true). */
  realtime?: boolean;
  /** SignalR module override (defaults to the @microsoft/signalr peer dependency). */
  signalR?: SignalRModule;
  /** Passed to SignalR configureLogging, e.g. signalR.LogLevel.Warning. */
  signalRLogLevel?: unknown;
  /** Messages per page (default 20, max 100). */
  pageSize?: number;
  /** Show a toast when a new message arrives (default true). */
  toasts?: boolean;
  /** Toast lifetime in milliseconds (default 6000; critical messages stay until closed). */
  toastDurationMs?: number;
  /** Panel title; defaults to branding.inboxTitle, then "Notifications". */
  title?: string;
  /** Locale for relative times (default: the browser's). */
  locale?: string;
  /**
   * Custom deep-link handling. Return `false` to fall back to the default behaviour
   * (router navigation for relative/same-origin links, new tab for other http(s) links).
   */
  openDeepLink?: (url: string, message: InAppMessage) => boolean | void | Promise<boolean | void>;
}

export const WINGS_INBOX_CONFIG = new InjectionToken<WingsInboxConfig>('WINGS_INBOX_CONFIG');
