import { AuthenticationError, errorFromResponse, NotificationHubError } from './errors.js';
import { HttpTransport, newIdempotencyKey, type CallOptions, type QueryValue, type TransportOptions } from './transport.js';
import {
  FINAL_REQUEST_STATUSES,
  type BatchSendResponse, type BusinessEventRequest, type BusinessEventResponse, type CancelResult, type Channel, type Contact,
  type ConsentRequest, type ContactUpsertRequest, type ContactKind, type DeliveryTimeline, type Device, type Guid, type InboxTokenRequest,
  type InboxTokenResponse, type IsoDateTime, type ManifestRegistrationResult, type NotificationManifest, type NotificationStatus,
  type Preferences, type PreferencesUpdate, type ProviderHealth, type PushInteraction, type RegisterDeviceRequest, type RequestStatus,
  type SendNotificationRequest, type SendNotificationResponse, type TemplatePreviewRequest, type TemplatePreviewResponse,
  type TemplateSummary, type TokenResponse, type UsageGroupBy, type UsageRow, type WebhookEventType, type WebhookRegistration,
  type WebhookSubscription,
} from './types.js';
import { verifyWebhook, type VerifyWebhookOptions } from './webhooks.js';

export interface NotificationHubClientOptions extends TransportOptions {
  clientId: string;
  /** Server-side secret. Never ship it to browsers or mobile apps; mint inbox tokens instead. */
  clientSecret: string;
  /** Optional subset of the credential's scopes (space-separated or an array). */
  scope?: string | string[];
  /** Company used when a request does not specify one. */
  defaultCompanyId?: Guid;
  /** Refresh the access token this many seconds before it expires (default 60). */
  tokenRefreshSkewSeconds?: number;
  /** Generates idempotency keys for sends/events that do not carry one. */
  idempotencyKeyFactory?: () => string;
}

export interface WaitOptions extends CallOptions {
  /** Give up after this many milliseconds and return the last status (default 30 000). */
  timeoutMs?: number;
  /** Poll interval in milliseconds (default 250). */
  intervalMs?: number;
}

export interface ListNotificationsQuery {
  companyId?: Guid;
  applicationId?: Guid;
  status?: RequestStatus;
  notificationType?: string;
  correlationId?: string;
  from?: IsoDateTime | Date;
  to?: IsoDateTime | Date;
  page?: number;
  pageSize?: number;
}

export interface NotificationListItem {
  notificationId: Guid;
  companyId: Guid;
  applicationId: Guid;
  notificationType?: string | null;
  templateCode?: string | null;
  status: RequestStatus;
  source: string;
  priority: string;
  correlationId?: string | null;
  idempotencyKey?: string | null;
  createdAt: IsoDateTime;
  completedAt?: IsoDateTime | null;
  deliveries: number;
}

export interface UsageQuery {
  from?: IsoDateTime | Date;
  to?: IsoDateTime | Date;
  groupBy?: UsageGroupBy;
  companyId?: Guid;
  applicationId?: Guid;
  channel?: Channel;
}

export interface RegisterWebhookRequest {
  name: string;
  url: string;
  eventTypes: (WebhookEventType | '*')[];
  companyId?: Guid;
  /** Only for administrator tokens; application credentials use their own application. */
  applicationId?: Guid;
}

/**
 * Server-side client for Wings Notification Hub (Node.js 20+, Deno, Bun, edge runtimes with fetch).
 * Handles client-credential tokens (cached, refreshed before expiry and after a 401), idempotency keys,
 * retries with Retry-After/backoff and typed errors. One instance per credential; safe to share.
 */
export class NotificationHubClient {
  private readonly http: HttpTransport;
  private readonly options: NotificationHubClientOptions;
  private token?: string;
  private tokenExpiresAt = 0;
  private tokenRequest?: Promise<string>;

  constructor(options: NotificationHubClientOptions) {
    if (!options.clientId || !options.clientSecret) throw new Error('clientId and clientSecret are required.');
    this.options = options;
    this.http = new HttpTransport(options);
  }

  /** Verifies an incoming hub webhook (HMAC-SHA256 over "t.body", 5-minute replay window). */
  static verifyWebhook(secret: string, rawBody: string | Uint8Array, signatureHeader: string | null | undefined, options?: VerifyWebhookOptions): boolean {
    return verifyWebhook(secret, rawBody, signatureHeader, options);
  }

  // ───────── notifications ─────────

  /**
   * Sends a notification (202 = durably accepted, not delivered). An idempotency key is generated and written
   * back to `request.idempotencyKey` when missing, so re-sending the same object can never duplicate.
   */
  send(request: SendNotificationRequest, opts?: CallOptions): Promise<SendNotificationResponse> {
    this.prepare(request);
    return this.call('POST', 'api/v1/notifications/send', { json: request, ...opts, headers: { 'Idempotency-Key': request.idempotencyKey!, ...opts?.headers } });
  }

  /** Up to 1000 notifications; each item succeeds or fails independently (see `results[i].error`). */
  sendBatch(requests: SendNotificationRequest[], opts?: CallOptions): Promise<BatchSendResponse> {
    for (const r of requests) this.prepare(r);
    return this.call('POST', 'api/v1/notifications/batch', { json: { notifications: requests }, ...opts });
  }

  /** Validates, resolves recipients and renders without persisting or delivering anything. */
  previewSend(request: SendNotificationRequest, opts?: CallOptions): Promise<SendNotificationResponse> {
    const r: SendNotificationRequest = { ...request, dryRun: true };
    if (!r.companyId && this.options.defaultCompanyId) r.companyId = this.options.defaultCompanyId;
    return this.call('POST', 'api/v1/notifications/send', { json: r, ...opts });
  }

  getStatus(notificationId: Guid, opts?: CallOptions): Promise<NotificationStatus> {
    return this.call('GET', `api/v1/notifications/${enc(notificationId)}/status`, opts);
  }

  /** Immutable delivery timeline: deliveries, provider attempts and events. */
  getHistory(notificationId: Guid, opts?: CallOptions): Promise<DeliveryTimeline> {
    return this.call('GET', `api/v1/notifications/${enc(notificationId)}/history`, opts);
  }

  /** Cancels scheduled/queued/retrying deliveries. Deliveries already being submitted cannot be recalled. */
  cancel(notificationId: Guid, opts?: CallOptions): Promise<CancelResult> {
    return this.call('POST', `api/v1/notifications/${enc(notificationId)}/cancel`, { json: {}, ...opts });
  }

  listNotifications(query: ListNotificationsQuery = {}, opts?: CallOptions): Promise<{ items: NotificationListItem[]; page: number; pageSize: number; total: number }> {
    return this.call('GET', 'api/v1/notifications', { query: { ...query, from: iso(query.from), to: iso(query.to) }, ...opts });
  }

  /** Polls until the request reaches a final status or the timeout elapses (tests/tools; production code should use webhooks). */
  async waitForCompletion(notificationId: Guid, opts: WaitOptions = {}): Promise<NotificationStatus> {
    const until = Date.now() + (opts.timeoutMs ?? 30_000);
    for (;;) {
      const s = await this.getStatus(notificationId, opts);
      if (FINAL_REQUEST_STATUSES.includes(s.status) || Date.now() > until) return s;
      await new Promise((resolve) => setTimeout(resolve, opts.intervalMs ?? 250));
    }
  }

  // ───────── events & templates ─────────

  /** Raises a business event (e.g. "salesorder.approved") evaluated by the hub's notification rules. */
  publishEvent(request: BusinessEventRequest, opts?: CallOptions): Promise<BusinessEventResponse> {
    request.idempotencyKey ??= this.newKey();
    if (!request.companyId && this.options.defaultCompanyId) request.companyId = this.options.defaultCompanyId;
    return this.call('POST', 'api/v1/events', { json: request, ...opts, headers: { 'Idempotency-Key': request.idempotencyKey, ...opts?.headers } });
  }

  listTemplates(filter: { channel?: Channel; code?: string; companyId?: Guid } = {}, opts?: CallOptions): Promise<TemplateSummary[]> {
    return this.call('GET', 'api/v1/templates', { query: { ...filter, channel: channelQuery(filter.channel) }, ...opts });
  }

  previewTemplate(request: TemplatePreviewRequest, opts?: CallOptions): Promise<TemplatePreviewResponse> {
    return this.call('POST', 'api/v1/notifications/preview', { json: request, ...opts });
  }

  // ───────── recipients ─────────

  upsertContact(request: ContactUpsertRequest, opts?: CallOptions): Promise<Contact> {
    return this.call('PUT', 'api/v1/contacts', { json: this.withCompany(request), ...opts });
  }

  /** Registers or refreshes a push token for one of your users (call from your backend after the app reports it). */
  registerDevice(request: RegisterDeviceRequest, opts?: CallOptions): Promise<Device> {
    return this.call('POST', 'api/v1/devices', { json: this.withCompany(request), ...opts });
  }

  listDevices(userId: string, companyId?: Guid, opts?: CallOptions): Promise<Device[]> {
    return this.call('GET', 'api/v1/devices', { query: { userId, companyId: companyId ?? this.options.defaultCompanyId }, ...opts });
  }

  /** Invalidates a device registration (logout/uninstall). */
  unregisterDevice(deviceId: Guid, opts?: CallOptions): Promise<void> {
    return this.call('DELETE', `api/v1/devices/${enc(deviceId)}`, opts);
  }

  getPreferences(query: { externalId: string; companyId?: Guid; kind?: ContactKind }, opts?: CallOptions): Promise<Preferences> {
    return this.call('GET', 'api/v1/preferences', { query: { ...query, companyId: query.companyId ?? this.options.defaultCompanyId }, ...opts });
  }

  updatePreferences(update: PreferencesUpdate, opts?: CallOptions): Promise<Preferences> {
    return this.call('PUT', 'api/v1/preferences', { json: this.withCompany(update), ...opts });
  }

  /** Records consent or its withdrawal (append-only evidence). */
  recordConsent(request: ConsentRequest, opts?: CallOptions): Promise<{ id: Guid }> {
    return this.call('POST', 'api/v1/consents', { json: this.withCompany(request), retry: 'rate-limit', ...opts });
  }

  // ───────── in-app & push ─────────

  /** Mints a short-lived inbox token for one of your end users; hand it to the browser/desktop inbox client. */
  createInboxToken(request: InboxTokenRequest, opts?: CallOptions): Promise<InboxTokenResponse> {
    return this.call('POST', 'api/v1/inapp/tokens', { json: this.withCompany(request), ...opts });
  }

  /** Reports a push receipt/open/click on behalf of a device (the device can also report it with an inbox token). */
  reportPushInteraction(deliveryId: Guid, interaction: PushInteraction, opts?: CallOptions): Promise<void> {
    return this.call('POST', 'api/v1/push/events', { json: { deliveryId, interaction }, ...opts });
  }

  // ───────── webhooks & registration ─────────

  /** Registers a webhook. The signing secret is returned once: store it and pass it to verifyWebhook. */
  registerWebhook(request: RegisterWebhookRequest, opts?: CallOptions): Promise<WebhookRegistration> {
    return this.call('POST', 'api/v1/webhooks', { json: request, retry: 'rate-limit', ...opts });
  }

  listWebhooks(opts?: CallOptions): Promise<WebhookSubscription[]> {
    return this.call('GET', 'api/v1/webhooks', opts);
  }

  deleteWebhook(id: Guid, opts?: CallOptions): Promise<void> {
    return this.call('DELETE', `api/v1/webhooks/${enc(id)}`, opts);
  }

  /** Queues a signed test event to the subscription. */
  testWebhook(id: Guid, opts?: CallOptions): Promise<{ eventId: Guid }> {
    return this.call('POST', `api/v1/webhooks/${enc(id)}/test`, { json: {}, retry: 'rate-limit', ...opts });
  }

  /** Wings Code: registers/updates an application from its manifest (needs the applications.register scope). */
  registerManifest(manifest: NotificationManifest, opts?: CallOptions): Promise<ManifestRegistrationResult> {
    return this.call('POST', 'api/v1/registrations/manifest', { json: manifest, ...opts });
  }

  // ───────── usage & health ─────────

  getUsage(query: UsageQuery = {}, opts?: CallOptions): Promise<UsageRow[]> {
    return this.call('GET', 'api/v1/usage', { query: { groupBy: 'channel', ...query, channel: channelQuery(query.channel), from: iso(query.from), to: iso(query.to) }, ...opts });
  }

  getProviderHealth(opts?: CallOptions): Promise<ProviderHealth[]> {
    return this.call('GET', 'api/v1/providers/health', opts);
  }

  // ───────── plumbing ─────────

  /** Calls any hub endpoint with this client's authentication, retries and error mapping. */
  request<T>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, init: { json?: unknown; query?: Record<string, QueryValue> } & CallOptions = {}): Promise<T> {
    return this.call(method, path, init);
  }

  /** Returns a valid access token (cached; refreshed `tokenRefreshSkewSeconds` before expiry). */
  async getAccessToken(forceRefresh = false): Promise<string> {
    if (!forceRefresh && this.token && Date.now() < this.tokenExpiresAt) return this.token;
    // Single flight: concurrent callers share one token request.
    this.tokenRequest ??= this.fetchToken().finally(() => {
      this.tokenRequest = undefined;
    });
    return this.tokenRequest;
  }

  private async fetchToken(): Promise<string> {
    const form: Record<string, string> = { grant_type: 'client_credentials', client_id: this.options.clientId, client_secret: this.options.clientSecret };
    const scope = Array.isArray(this.options.scope) ? this.options.scope.join(' ') : this.options.scope;
    if (scope) form.scope = scope;
    let res: TokenResponse;
    try {
      res = await this.http.request<TokenResponse>({ method: 'POST', path: 'connect/token', form });
    } catch (err) {
      if (err instanceof NotificationHubError && err.status === 400) {
        // invalid_scope / unsupported_grant_type are credential configuration problems, not request validation.
        throw new AuthenticationError({ status: err.status, code: err.code, message: err.message, body: err.body, cause: err });
      }
      throw err;
    }
    if (!res?.access_token) throw errorFromResponse(500, JSON.stringify({ code: 'invalid_token_response', message: 'The token endpoint returned no access_token.' }));
    const skew = this.options.tokenRefreshSkewSeconds ?? 60;
    const lifetime = res.expires_in > skew * 2 ? res.expires_in - skew : res.expires_in / 2;
    this.token = res.access_token;
    this.tokenExpiresAt = Date.now() + lifetime * 1000;
    return res.access_token;
  }

  private call<T>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string,
                  init: { json?: unknown; query?: Record<string, QueryValue>; retry?: 'all' | 'rate-limit' | 'none' } & CallOptions = {}): Promise<T> {
    return this.http.request<T>({
      method, path, json: init.json, query: init.query, headers: init.headers, signal: init.signal, retry: init.retry,
      auth: (force) => {
        if (force) this.token = undefined;
        return this.getAccessToken(force);
      },
    });
  }

  private prepare(request: SendNotificationRequest): void {
    request.idempotencyKey ??= this.newKey();
    if (!request.companyId && this.options.defaultCompanyId) request.companyId = this.options.defaultCompanyId;
  }

  private withCompany<T extends { companyId?: Guid }>(request: T): T {
    return request.companyId || !this.options.defaultCompanyId ? request : { ...request, companyId: this.options.defaultCompanyId };
  }

  private newKey(): string {
    return (this.options.idempotencyKeyFactory ?? newIdempotencyKey)();
  }
}

/**
 * Query-string enum binding on the hub is case-sensitive and only accepts the enum member names ("InApp"),
 * while JSON bodies use the lower-case wire names ("inapp"). Translate for query parameters.
 */
const CHANNEL_QUERY_NAMES: Record<Channel, string> = {
  email: 'Email', sms: 'Sms', whatsapp: 'WhatsApp', inapp: 'InApp', push: 'Push', webpush: 'WebPush', webhook: 'Webhook',
};

function channelQuery(c: Channel | undefined): string | undefined {
  return c === undefined ? undefined : (CHANNEL_QUERY_NAMES[c] ?? c);
}

function enc(id: string): string {
  return encodeURIComponent(id);
}

function iso(v: IsoDateTime | Date | undefined): string | undefined {
  return v instanceof Date ? v.toISOString() : v;
}
