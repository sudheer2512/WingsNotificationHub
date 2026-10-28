/**
 * Browser/desktop in-app inbox client. Uses a short-lived inbox token that YOUR backend mints with
 * `NotificationHubClient.createInboxToken` — never a client secret. Safe to bundle for browsers (no Node APIs).
 */
import { AuthenticationError } from './errors.js';
import { HttpTransport, type CallOptions, type TransportOptions } from './transport.js';
import type { Guid, InAppMessage, InboxBranding, InboxMessageAction, InboxPage, InboxQuery, InboxTokenResponse, IsoDateTime, PushInteraction } from './types.js';

export * from './types.js';
export * from './errors.js';
export type { CallOptions, FetchLike } from './transport.js';

/** What `getToken` may return: the raw token, or the hub's InboxTokenResponse passed through by your backend. */
export type InboxTokenResult = string | InboxTokenResponse | { accessToken: string; expiresAt?: IsoDateTime | number | Date; hubUrl?: string };

export type InboxConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** Minimal shape of the `@microsoft/signalr` module used by the inbox (structural, so any 8.x+ version fits). */
export interface SignalRModule {
  HubConnectionBuilder: new () => SignalRHubConnectionBuilder;
}
export interface SignalRHubConnectionBuilder {
  withUrl(url: string, options: { accessTokenFactory: () => string | Promise<string> }): SignalRHubConnectionBuilder;
  withAutomaticReconnect(retryDelays?: number[]): SignalRHubConnectionBuilder;
  configureLogging(logging: unknown): SignalRHubConnectionBuilder;
  build(): SignalRHubConnection;
}
export interface SignalRHubConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  on(methodName: string, handler: (...args: any[]) => void): void;
  off(methodName: string): void;
  onreconnecting(callback: (error?: Error) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
  onclose(callback: (error?: Error) => void): void;
}

export interface InboxClientOptions extends Omit<TransportOptions, 'baseUrl'> {
  /** Base URL of the hub (the hub must list your app's origin in Cors:Origins). */
  baseUrl: string | URL;
  /** Fetches a fresh inbox token from your backend. Called on first use, before expiry and after a 401. */
  getToken: () => Promise<InboxTokenResult>;
  /** The SignalR module (`import * as signalR from '@microsoft/signalr'`). Imported dynamically when omitted. */
  signalR?: SignalRModule;
  /** Hub path; defaults to the token response's hubUrl or "/hubs/inapp". */
  hubPath?: string;
  /** Refresh the token this many seconds before it expires (default 30). */
  tokenRefreshSkewSeconds?: number;
  /** Passed to SignalR's configureLogging (e.g. signalR.LogLevel.Warning). */
  signalRLogLevel?: unknown;
}

type Listener<T> = (value: T) => void;

const MAX_CACHE = 500;

export class InboxClient {
  private readonly http: HttpTransport;
  private readonly options: InboxClientOptions;
  private token?: string;
  private tokenExpiresAt = 0;
  private tokenRequest?: Promise<string>;
  private hubPath: string;
  private unread = 0;
  private unreadKnown = false;
  private readonly cache = new Map<Guid, InAppMessage>();
  private connection?: SignalRHubConnection;
  private state: InboxConnectionState = 'disconnected';
  private branding?: InboxBranding;
  private readonly notificationListeners = new Set<Listener<InAppMessage>>();
  private readonly unreadListeners = new Set<Listener<number>>();
  private readonly stateListeners = new Set<Listener<InboxConnectionState>>();
  private readonly errorListeners = new Set<Listener<unknown>>();

  constructor(options: InboxClientOptions) {
    if (typeof options.getToken !== 'function') throw new Error('getToken is required.');
    this.options = options;
    this.http = new HttpTransport(options);
    this.hubPath = options.hubPath ?? '/hubs/inapp';
  }

  /** Current unread count (kept up to date by list(), mutations and realtime notifications). */
  get unreadCount(): number {
    return this.unread;
  }

  get connectionState(): InboxConnectionState {
    return this.state;
  }

  // ───────── REST ─────────

  /** Lists messages (critical first, newest first). Also refreshes the unread count. */
  async list(query: InboxQuery = {}, opts?: CallOptions): Promise<InboxPage> {
    const page = await this.call<InboxPage>('GET', 'api/v1/inapp/messages', { query: { ...query }, ...opts });
    for (const m of page.items) this.remember(m);
    this.setUnread(page.unread);
    return page;
  }

  /** Re-reads the unread count from the hub. */
  async refreshUnreadCount(opts?: CallOptions): Promise<number> {
    const page = await this.call<InboxPage>('GET', 'api/v1/inapp/messages', { query: { unreadOnly: true, pageSize: 1 }, ...opts });
    this.setUnread(page.unread);
    return page.unread;
  }

  markRead(id: Guid, opts?: CallOptions): Promise<InAppMessage> {
    return this.mutate(id, 'read', undefined, opts);
  }

  markUnread(id: Guid, opts?: CallOptions): Promise<InAppMessage> {
    return this.mutate(id, 'unread', undefined, opts);
  }

  /** Archives (and marks read). */
  archive(id: Guid, opts?: CallOptions): Promise<InAppMessage> {
    return this.mutate(id, 'archive', undefined, opts);
  }

  /** Removes the message from the user's inbox. */
  dismiss(id: Guid, opts?: CallOptions): Promise<InAppMessage> {
    return this.mutate(id, 'dismiss', undefined, opts);
  }

  /** Records that the user pressed one of the message's action buttons (raises an inapp.action webhook to your app). */
  performAction(id: Guid, actionId: string, opts?: CallOptions): Promise<InAppMessage> {
    return this.mutate(id, 'action', { actionId }, opts);
  }

  async markAllRead(opts?: CallOptions): Promise<{ marked: number }> {
    const result = await this.call<{ marked: number }>('POST', 'api/v1/inapp/messages/read-all', opts);
    const now = new Date().toISOString();
    for (const [id, m] of this.cache) if (!m.readAt) this.cache.set(id, { ...m, readAt: now });
    this.setUnread(0);
    return result;
  }

  /** White-label settings (title, colours, logo) of the user's company; cached after the first call. */
  async getBranding(options: { refresh?: boolean } & CallOptions = {}): Promise<InboxBranding> {
    if (!this.branding || options.refresh) this.branding = (await this.call<InboxBranding>('GET', 'api/v1/inapp/branding', options)) ?? {};
    return this.branding;
  }

  /** Reports a push receipt/open/click for a delivery (deliveryId comes from the push payload's data). */
  reportPushInteraction(deliveryId: Guid, interaction: PushInteraction, opts?: CallOptions): Promise<void> {
    return this.call('POST', 'api/v1/push/events', { json: { deliveryId, interaction }, ...opts });
  }

  // ───────── realtime ─────────

  /** Subscribes to new messages pushed over SignalR. Returns an unsubscribe function. */
  onNotification(listener: Listener<InAppMessage>): () => void {
    return subscribe(this.notificationListeners, listener);
  }

  onUnreadCountChange(listener: Listener<number>): () => void {
    return subscribe(this.unreadListeners, listener);
  }

  onConnectionStateChange(listener: Listener<InboxConnectionState>): () => void {
    return subscribe(this.stateListeners, listener);
  }

  /** Background errors (realtime connection failures, unread-count refresh failures). */
  onError(listener: Listener<unknown>): () => void {
    return subscribe(this.errorListeners, listener);
  }

  /** Opens the SignalR connection to /hubs/inapp and starts delivering `notification` events. Idempotent. */
  async connect(): Promise<void> {
    if (this.connection) return;
    const signalR = this.options.signalR ?? (await loadSignalR());
    await this.accessToken(); // validates the token source and learns hubUrl before connecting
    let builder = new signalR.HubConnectionBuilder()
      .withUrl(this.http.url(this.hubPath).toString(), { accessTokenFactory: () => this.accessToken() })
      .withAutomaticReconnect();
    if (this.options.signalRLogLevel !== undefined) builder = builder.configureLogging(this.options.signalRLogLevel);
    const connection = builder.build();
    connection.on('notification', (m: InAppMessage) => this.receive(m));
    connection.onreconnecting(() => this.setState('reconnecting'));
    connection.onreconnected(() => {
      this.setState('connected');
      // Messages may have arrived while offline: resync the badge.
      this.refreshUnreadCount().catch((e) => this.emitError(e));
    });
    connection.onclose((e) => {
      if (this.connection === connection) this.connection = undefined;
      this.setState('disconnected');
      if (e) this.emitError(e);
    });
    this.connection = connection;
    this.setState('connecting');
    try {
      await connection.start();
      this.setState('connected');
    } catch (e) {
      this.connection = undefined;
      this.setState('disconnected');
      throw e;
    }
  }

  async disconnect(): Promise<void> {
    const c = this.connection;
    this.connection = undefined;
    if (c) await c.stop();
    this.setState('disconnected');
  }

  /** Disconnects and removes every listener. */
  async dispose(): Promise<void> {
    await this.disconnect();
    this.notificationListeners.clear();
    this.unreadListeners.clear();
    this.stateListeners.clear();
    this.errorListeners.clear();
  }

  /** Returns a valid inbox token, calling getToken when none is cached or it is about to expire. */
  async accessToken(forceRefresh = false): Promise<string> {
    if (!forceRefresh && this.token && Date.now() < this.tokenExpiresAt) return this.token;
    this.tokenRequest ??= this.loadToken().finally(() => {
      this.tokenRequest = undefined;
    });
    return this.tokenRequest;
  }

  // ───────── internals ─────────

  /** Handles a message pushed by the hub (also usable to feed messages from another channel, e.g. a push payload). */
  receive(message: InAppMessage): void {
    const known = this.cache.has(message.id);
    this.remember(message);
    if (!known && !message.readAt && !message.archivedAt) this.setUnread(this.unread + 1);
    for (const l of this.notificationListeners) safe(() => l(message), (e) => this.emitError(e));
  }

  private async mutate(id: Guid, action: InboxMessageAction | 'action', body: unknown, opts?: CallOptions): Promise<InAppMessage> {
    const before = this.cache.get(id);
    // Always send a JSON body: the hub's action route binds an optional JSON body and does not match bodiless POSTs.
    const after = await this.call<InAppMessage>('POST', `api/v1/inapp/messages/${encodeURIComponent(id)}/${action}`, { json: body ?? {}, ...opts });
    const gone = action === 'dismiss';
    if (gone) this.cache.delete(id);
    else this.remember(after);
    if (before && this.unreadKnown) {
      const delta = (!gone && isUnread(after) ? 1 : 0) - (isUnread(before) ? 1 : 0);
      this.setUnread(this.unread + delta);
    } else {
      this.refreshUnreadCount().catch((e) => this.emitError(e));
    }
    return after;
  }

  private async loadToken(): Promise<string> {
    const result = await this.options.getToken();
    const t = typeof result === 'string' ? { accessToken: result } : result;
    if (!t?.accessToken) throw new AuthenticationError({ status: 401, code: 'no_inbox_token', message: 'getToken() returned no access token.' });
    if ('hubUrl' in t && t.hubUrl && !this.options.hubPath) this.hubPath = t.hubUrl;
    const expires = 'expiresAt' in t && t.expiresAt !== undefined ? new Date(t.expiresAt).getTime() : jwtExpiry(t.accessToken);
    const skew = (this.options.tokenRefreshSkewSeconds ?? 30) * 1000;
    const now = Date.now();
    if (expires && !Number.isNaN(expires)) {
      const lifetime = expires - now;
      this.tokenExpiresAt = lifetime > skew * 2 ? expires - skew : now + Math.max(0, lifetime / 2);
    } else {
      this.tokenExpiresAt = now + 5 * 60_000; // unknown expiry: re-ask every 5 minutes
    }
    this.token = t.accessToken;
    return t.accessToken;
  }

  private call<T>(method: 'GET' | 'POST', path: string, init: { json?: unknown; query?: Record<string, string | number | boolean | undefined> } & CallOptions = {}): Promise<T> {
    return this.http.request<T>({
      method, path, json: init.json, query: init.query, headers: init.headers, signal: init.signal,
      // Inbox mutations are naturally idempotent (read/archive/dismiss set timestamps once).
      auth: (force) => this.accessToken(force),
    });
  }

  private remember(m: InAppMessage): void {
    this.cache.delete(m.id);
    this.cache.set(m.id, m);
    if (this.cache.size > MAX_CACHE) {
      const oldest = this.cache.keys().next().value;
      if (oldest !== undefined) this.cache.delete(oldest);
    }
  }

  private setUnread(n: number): void {
    const value = Math.max(0, n);
    const changed = value !== this.unread || !this.unreadKnown;
    this.unread = value;
    this.unreadKnown = true;
    if (changed) for (const l of this.unreadListeners) safe(() => l(value), (e) => this.emitError(e));
  }

  private setState(s: InboxConnectionState): void {
    if (s === this.state) return;
    this.state = s;
    for (const l of this.stateListeners) safe(() => l(s), (e) => this.emitError(e));
  }

  private emitError(e: unknown): void {
    for (const l of this.errorListeners) safe(() => l(e), () => {});
  }
}

function isUnread(m: InAppMessage): boolean {
  return !m.readAt && !m.archivedAt;
}

function subscribe<T>(set: Set<Listener<T>>, l: Listener<T>): () => void {
  set.add(l);
  return () => set.delete(l);
}

function safe(fn: () => void, onError: (e: unknown) => void): void {
  try {
    fn();
  } catch (e) {
    onError(e);
  }
}

/** Reads `exp` from a JWT without validating it (expiry scheduling only). */
export function jwtExpiry(token: string): number | undefined {
  const part = token.split('.')[1];
  if (!part) return undefined;
  try {
    const b64 = part.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(part.length / 4) * 4, '=');
    const json = JSON.parse(atob(b64)) as { exp?: number };
    return typeof json.exp === 'number' ? json.exp * 1000 : undefined;
  } catch {
    return undefined;
  }
}

async function loadSignalR(): Promise<SignalRModule> {
  try {
    const mod = (await import('@microsoft/signalr')) as unknown as SignalRModule & { default?: SignalRModule };
    return mod.HubConnectionBuilder ? mod : mod.default!;
  } catch (e) {
    throw new Error('Realtime inbox needs @microsoft/signalr: install it or pass options.signalR.', { cause: e });
  }
}
