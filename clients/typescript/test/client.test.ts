import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  AuthenticationError, ConflictError, NetworkError, NotFoundError, NotificationHubClient, PermissionDeniedError, RateLimitError, ServerError,
  ValidationError, type SendNotificationRequest,
} from '../src/index.js';
import { hub, json, recordingSleep, tokenResponse } from './helpers.js';

const COMPANY = '6f1c2a7e-0000-4000-8000-000000000001';
const accepted = (id = 'n-1') => json(202, { notificationId: id, status: 'accepted', trackingUrl: `/api/v1/notifications/${id}/status`, duplicate: false, deliveries: [], warnings: [] });

function client(fetch: (input: string | URL, init?: RequestInit) => Promise<Response>, extra: Partial<ConstructorParameters<typeof NotificationHubClient>[0]> = {}) {
  const { sleep, sleeps } = recordingSleep();
  const c = new NotificationHubClient({ baseUrl: 'https://hub.example', clientId: 'erp', clientSecret: 's3cret', fetch, sleep, ...extra });
  return { c, sleeps };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('token handling', () => {
  it('requests a client-credentials token once and reuses it', async () => {
    const m = hub(() => accepted());
    const { c } = client(m.fetch, { scope: ['notifications.send', 'notifications.read'] });
    await c.send({ companyId: COMPANY, notificationType: 'ORDER_APPROVED', recipient: { userId: 'u-1' } });
    await c.send({ companyId: COMPANY, notificationType: 'ORDER_APPROVED', recipient: { userId: 'u-1' } });

    const tokenCalls = m.calls.filter((x) => x.url.pathname === '/connect/token');
    expect(tokenCalls).toHaveLength(1);
    const form = new URLSearchParams(tokenCalls[0]!.body);
    expect(form.get('grant_type')).toBe('client_credentials');
    expect(form.get('client_id')).toBe('erp');
    expect(form.get('client_secret')).toBe('s3cret');
    expect(form.get('scope')).toBe('notifications.send notifications.read');
    expect(tokenCalls[0]!.headers.get('content-type')).toBe('application/x-www-form-urlencoded');
    const apiCalls = m.calls.filter((x) => x.url.pathname !== '/connect/token');
    expect(apiCalls.every((x) => x.headers.get('authorization') === 'Bearer tok-1')).toBe(true);
  });

  it('refreshes the token before it expires', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-28T10:00:00Z'));
    const m = hub(() => json(200, []), (_c, i) => tokenResponse(`tok-${i + 1}`, 3600));
    const { c } = client(m.fetch);

    await c.getProviderHealth();
    vi.setSystemTime(new Date('2026-10-28T10:58:00Z')); // 58 min: still inside the 60 s refresh skew
    await c.getProviderHealth();
    vi.setSystemTime(new Date('2026-10-28T10:59:30Z')); // within 60 s of expiry: refresh
    await c.getProviderHealth();

    const auth = m.calls.filter((x) => x.url.pathname === '/api/v1/providers/health').map((x) => x.headers.get('authorization'));
    expect(auth).toEqual(['Bearer tok-1', 'Bearer tok-1', 'Bearer tok-2']);
  });

  it('drops the cached token and retries once after a 401', async () => {
    const m = hub((_c, i) => (i === 0 ? json(401, { code: 'unauthorized', message: 'expired' }) : json(200, [])), (_c, i) => tokenResponse(`tok-${i + 1}`));
    const { c } = client(m.fetch);
    await expect(c.getProviderHealth()).resolves.toEqual([]);
    expect(m.calls.map((x) => `${x.url.pathname} ${x.headers.get('authorization') ?? ''}`.trim())).toEqual([
      '/connect/token', '/api/v1/providers/health Bearer tok-1', '/connect/token', '/api/v1/providers/health Bearer tok-2',
    ]);
  });

  it('throws AuthenticationError when the refreshed token is also rejected', async () => {
    const m = hub(() => json(401, { code: 'unauthorized', message: 'nope' }));
    const { c } = client(m.fetch);
    await expect(c.getProviderHealth()).rejects.toBeInstanceOf(AuthenticationError);
    expect(m.calls.filter((x) => x.url.pathname === '/connect/token')).toHaveLength(2);
  });

  it('shares one in-flight token request between concurrent calls', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const m = hub(() => json(200, []), async () => { await gate; return tokenResponse(); });
    const { c } = client(m.fetch);
    const all = Promise.all([c.getProviderHealth(), c.getProviderHealth(), c.getProviderHealth()]);
    release();
    await all;
    expect(m.calls.filter((x) => x.url.pathname === '/connect/token')).toHaveLength(1);
  });

  it('maps token endpoint failures to AuthenticationError with the OAuth error code', async () => {
    const m = hub(() => json(200, []), () => json(401, { error: 'invalid_client', error_description: 'Client authentication failed.' }));
    const { c } = client(m.fetch);
    const err = await c.getProviderHealth().catch((e) => e);
    expect(err).toBeInstanceOf(AuthenticationError);
    expect(err.code).toBe('invalid_client');
    expect(err.message).toBe('Client authentication failed.');

    const m2 = hub(() => json(200, []), () => json(400, { error: 'invalid_scope', error_description: 'One or more requested scopes are not granted.' }));
    const err2 = await client(m2.fetch).c.getProviderHealth().catch((e) => e);
    expect(err2).toBeInstanceOf(AuthenticationError);
    expect(err2.code).toBe('invalid_scope');
  });
});

describe('idempotency keys', () => {
  it('generates a key, writes it back to the request and sends it in body and header', async () => {
    const m = hub(() => accepted());
    const { c } = client(m.fetch);
    const req: SendNotificationRequest = { companyId: COMPANY, notificationType: 'INVOICE_CREATED', recipient: { email: 'a@b.example' } };
    await c.send(req);
    expect(req.idempotencyKey).toMatch(/^[0-9a-f]{32}$/);
    const call = m.calls.find((x) => x.url.pathname === '/api/v1/notifications/send')!;
    expect(JSON.parse(call.body!).idempotencyKey).toBe(req.idempotencyKey);
    expect(call.headers.get('idempotency-key')).toBe(req.idempotencyKey);
  });

  it('keeps a caller-supplied key and reuses the same key across retries', async () => {
    const m = hub((_c, i) => (i < 2 ? json(503, { code: 'unavailable', message: 'busy' }) : accepted()));
    const { c } = client(m.fetch);
    await c.send({ companyId: COMPANY, templateCode: 'X', recipient: { userId: 'u' }, idempotencyKey: 'order-42-approved' });
    const keys = m.calls.filter((x) => x.url.pathname === '/api/v1/notifications/send').map((x) => JSON.parse(x.body!).idempotencyKey);
    expect(keys).toEqual(['order-42-approved', 'order-42-approved', 'order-42-approved']);
  });

  it('re-sending the same request object reuses its generated key', async () => {
    const m = hub(() => accepted());
    const { c } = client(m.fetch);
    const req: SendNotificationRequest = { companyId: COMPANY, templateCode: 'X', recipient: { userId: 'u' } };
    await c.send(req);
    await c.send(req);
    const keys = m.calls.filter((x) => x.url.pathname === '/api/v1/notifications/send').map((x) => JSON.parse(x.body!).idempotencyKey);
    expect(new Set(keys).size).toBe(1);
  });

  it('assigns distinct keys to batch items and events, and applies the default company', async () => {
    const m = hub((call) => call.url.pathname.endsWith('/batch')
      ? json(200, { results: [], accepted: 2, rejected: 0 })
      : json(202, { eventId: 'e-1', status: 'accepted', duplicate: false }));
    const { c } = client(m.fetch, { defaultCompanyId: COMPANY });
    await c.sendBatch([{ templateCode: 'A', recipient: { userId: '1' } }, { templateCode: 'A', recipient: { userId: '2' } }]);
    const ev = { eventType: 'salesorder.approved', payload: { OrderNumber: 'SO-1' } };
    await c.publishEvent(ev);
    const batch = JSON.parse(m.calls.find((x) => x.url.pathname.endsWith('/batch'))!.body!);
    expect(batch.notifications[0].idempotencyKey).not.toBe(batch.notifications[1].idempotencyKey);
    expect(batch.notifications.every((n: { companyId: string }) => n.companyId === COMPANY)).toBe(true);
    const evBody = JSON.parse(m.calls.find((x) => x.url.pathname === '/api/v1/events')!.body!);
    expect(evBody.companyId).toBe(COMPANY);
    expect(evBody.idempotencyKey).toMatch(/^[0-9a-f]{32}$/);
  });

  it('marks previews as dry runs without changing the caller object', async () => {
    const m = hub(() => json(200, { notificationId: '00000000-0000-0000-0000-000000000000', status: 'preview', trackingUrl: '', duplicate: false, deliveries: [], warnings: [], preview: [] }));
    const { c } = client(m.fetch);
    const req: SendNotificationRequest = { companyId: COMPANY, templateCode: 'X', recipient: { userId: 'u' } };
    await c.previewSend(req);
    expect(JSON.parse(m.calls.at(-1)!.body!).dryRun).toBe(true);
    expect(req.dryRun).toBeUndefined();
  });
});

describe('retries', () => {
  it('honours Retry-After on 429 and then succeeds', async () => {
    const m = hub((_c, i) => (i === 0 ? json(429, { code: 'rate_limited', message: 'slow down' }, { 'retry-after': '2' }) : accepted()));
    const { c, sleeps } = client(m.fetch);
    await expect(c.send({ companyId: COMPANY, templateCode: 'X', recipient: { userId: 'u' } })).resolves.toMatchObject({ notificationId: 'n-1' });
    expect(sleeps).toEqual([2000]);
  });

  it('caps Retry-After at maxRetryDelayMs', async () => {
    const m = hub((_c, i) => (i === 0 ? json(503, {}, { 'retry-after': '3600' }) : json(200, [])));
    const { c, sleeps } = client(m.fetch, { maxRetryDelayMs: 5000 });
    await c.getProviderHealth();
    expect(sleeps).toEqual([5000]);
  });

  it('backs off exponentially on 502/503/504 and gives up after maxRetries with a ServerError', async () => {
    const m = hub(() => json(503, { code: 'unavailable', message: 'down' }));
    const { c, sleeps } = client(m.fetch, { maxRetries: 3 });
    const err = await c.getStatus('n-1').catch((e) => e);
    expect(err).toBeInstanceOf(ServerError);
    expect(err.status).toBe(503);
    expect(m.calls.filter((x) => x.url.pathname.endsWith('/status'))).toHaveLength(4);
    expect(sleeps).toHaveLength(3);
    expect(sleeps[0]).toBeGreaterThanOrEqual(200);
    expect(sleeps[1]).toBeGreaterThanOrEqual(400);
    expect(sleeps[2]).toBeGreaterThanOrEqual(800);
  });

  it('throws RateLimitError (with retryAfterMs) when 429 persists', async () => {
    const m = hub(() => json(429, { code: 'rate_limited', message: 'slow down' }, { 'retry-after': '1' }));
    const { c } = client(m.fetch, { maxRetries: 1 });
    const err = await c.getProviderHealth().catch((e) => e);
    expect(err).toBeInstanceOf(RateLimitError);
    expect(err.retryAfterMs).toBe(1000);
  });

  it('retries network failures and then throws NetworkError', async () => {
    let n = 0;
    const m = hub(() => { n++; throw new TypeError('fetch failed'); });
    const { c } = client(m.fetch, { maxRetries: 2 });
    const err = await c.getProviderHealth().catch((e) => e);
    expect(err).toBeInstanceOf(NetworkError);
    expect(err.status).toBe(0);
    expect(n).toBe(3);
  });

  it('does not retry 500 or validation errors', async () => {
    const m = hub(() => json(500, { code: 'internal_error', message: 'boom' }));
    const { c } = client(m.fetch);
    await expect(c.getProviderHealth()).rejects.toBeInstanceOf(ServerError);
    expect(m.calls.filter((x) => x.url.pathname !== '/connect/token')).toHaveLength(1);
  });

  it('does not retry non-idempotent registrations on 503 (only on 429)', async () => {
    const m = hub(() => json(503, { code: 'unavailable', message: 'down' }));
    const { c } = client(m.fetch);
    await expect(c.registerWebhook({ name: 'erp', url: 'https://erp.example/hook', eventTypes: ['delivery.status_changed'] })).rejects.toBeInstanceOf(ServerError);
    expect(m.calls.filter((x) => x.url.pathname === '/api/v1/webhooks')).toHaveLength(1);
  });
});

describe('error mapping', () => {
  it('maps ApiError bodies to typed errors with code, field errors and correlation id', async () => {
    const m = hub(() => json(422, { code: 'validation_failed', message: 'Invalid request.', errors: { recipient: ['A recipient is required.'] }, correlationId: 'corr-1' }));
    const { c } = client(m.fetch);
    const err = await c.send({ companyId: COMPANY, templateCode: 'X' }).catch((e) => e);
    expect(err).toBeInstanceOf(ValidationError);
    expect(err).toMatchObject({ status: 422, code: 'validation_failed', message: 'Invalid request.', correlationId: 'corr-1' });
    expect(err.errors).toEqual({ recipient: ['A recipient is required.'] });
    expect(m.calls.filter((x) => x.url.pathname.endsWith('/send'))).toHaveLength(1);
  });

  it.each([
    [400, ValidationError], [403, PermissionDeniedError], [404, NotFoundError], [409, ConflictError], [500, ServerError],
  ])('maps HTTP %i', async (status, type) => {
    const m = hub(() => json(status, { code: `c${status}`, message: `m${status}` }));
    const err = await client(m.fetch).c.getStatus('n-1').catch((e) => e);
    expect(err).toBeInstanceOf(type);
    expect(err.code).toBe(`c${status}`);
  });

  it('keeps non-JSON error bodies', async () => {
    const m = hub(() => new Response('<html>Bad gateway</html>', { status: 418 }));
    const err = await client(m.fetch).c.getStatus('n-1').catch((e) => e);
    expect(err.code).toBe('http_418');
    expect(err.body).toContain('Bad gateway');
  });
});

describe('routing', () => {
  it('keeps a base path, encodes ids and serializes query parameters', async () => {
    const m = hub(() => json(200, []));
    const c = new NotificationHubClient({ baseUrl: 'https://gw.example/notifications', clientId: 'a', clientSecret: 'b', fetch: m.fetch });
    await c.listTemplates({ channel: 'inapp' });
    await c.getUsage({ from: new Date('2026-10-01T00:00:00Z'), groupBy: 'company' });
    await c.getPreferences({ companyId: COMPANY, externalId: 'u 1' });
    const urls = m.calls.map((x) => x.url.toString());
    expect(urls[0]).toBe('https://gw.example/notifications/connect/token');
    expect(urls[1]).toBe('https://gw.example/notifications/api/v1/templates?channel=InApp');
    expect(urls[2]).toBe('https://gw.example/notifications/api/v1/usage?groupBy=company&from=2026-10-01T00%3A00%3A00.000Z');
    expect(urls[3]).toBe(`https://gw.example/notifications/api/v1/preferences?companyId=${COMPANY}&externalId=u+1`);
  });

  it('mints inbox tokens and cancels notifications on the documented routes', async () => {
    const m = hub((call) => call.url.pathname.endsWith('/tokens')
      ? json(200, { accessToken: 'inbox', expiresAt: '2026-10-28T11:00:00Z', hubUrl: '/hubs/inapp' })
      : json(200, { cancelled: 1, inFlight: 0 }));
    const { c } = client(m.fetch, { defaultCompanyId: COMPANY });
    await expect(c.createInboxToken({ userId: 'u-1001' })).resolves.toMatchObject({ accessToken: 'inbox', hubUrl: '/hubs/inapp' });
    await expect(c.cancel('n-9')).resolves.toEqual({ cancelled: 1, inFlight: 0 });
    expect(JSON.parse(m.calls.find((x) => x.url.pathname === '/api/v1/inapp/tokens')!.body!)).toEqual({ userId: 'u-1001', companyId: COMPANY });
    expect(m.calls.find((x) => x.url.pathname === '/api/v1/notifications/n-9/cancel')!.method).toBe('POST');
  });
});
