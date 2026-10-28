import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthenticationError, InboxClient, jwtExpiry, type InAppMessage, type SignalRModule } from '../src/inbox.js';
import { fakeJwt, json, mockFetch, type Handler } from './helpers.js';

const msg = (id: string, extra: Partial<InAppMessage> = {}): InAppMessage => ({
  id, title: `Title ${id}`, body: 'Body', category: 'Approval', priority: 'Normal', actions: [], createdAt: '2026-10-28T10:00:00Z', readAt: null, archivedAt: null, ...extra,
});

function inbox(handler: Handler, extra: Partial<ConstructorParameters<typeof InboxClient>[0]> = {}) {
  const m = mockFetch(handler);
  let n = 0;
  const getToken = vi.fn(async () => ({ accessToken: `inbox-${++n}`, expiresAt: new Date(Date.now() + 3_600_000).toISOString(), hubUrl: '/hubs/inapp' }));
  const client = new InboxClient({ baseUrl: 'https://hub.example', getToken, fetch: m.fetch, sleep: async () => {}, ...extra });
  return { client, calls: m.calls, getToken };
}

/** Echoes the action back as the updated message, like the hub does. */
const actionHandler = (unreadTotal = 2): Handler => (call) => {
  const path = call.url.pathname;
  if (call.method === 'GET' && path === '/api/v1/inapp/messages') {
    return call.url.searchParams.get('unreadOnly') === 'true'
      ? json(200, { unread: 7, items: [] })
      : json(200, { unread: unreadTotal, items: [msg('a'), msg('b'), msg('c', { readAt: '2026-10-28T10:01:00Z' })] });
  }
  const action = path.split('/').at(-1);
  const id = path.split('/').at(-2)!;
  const now = '2026-10-28T10:05:00Z';
  if (action === 'read') return json(200, msg(id, { readAt: now }));
  if (action === 'unread') return json(200, msg(id, { readAt: null }));
  if (action === 'archive') return json(200, msg(id, { readAt: now, archivedAt: now }));
  if (action === 'dismiss') return json(200, msg(id));
  if (action === 'action') return json(200, msg(id, { readAt: now, actionTaken: JSON.parse(call.body!).actionId }));
  if (path.endsWith('/read-all')) return json(200, { marked: 2 });
  return json(404, { code: 'not_found', message: 'nope' });
};

afterEach(() => vi.useRealTimers());

describe('inbox token', () => {
  it('fetches the token once and sends it as a bearer token', async () => {
    const { client, calls, getToken } = inbox(actionHandler());
    await client.list();
    await client.list({ unreadOnly: true });
    expect(getToken).toHaveBeenCalledTimes(1);
    expect(calls.every((c) => c.headers.get('authorization') === 'Bearer inbox-1')).toBe(true);
  });

  it('refreshes the token shortly before expiry', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-28T10:00:00Z'));
    const { client, calls, getToken } = inbox(actionHandler(), {
      getToken: vi.fn(async () => ({ accessToken: `t-${Date.now()}`, expiresAt: '2026-10-28T10:10:00Z' })),
    });
    void getToken;
    await client.list();
    vi.setSystemTime(new Date('2026-10-28T10:09:00Z'));
    await client.list();
    vi.setSystemTime(new Date('2026-10-28T10:09:45Z')); // within the 30 s skew
    await client.list();
    const tokens = calls.map((c) => c.headers.get('authorization'));
    expect(tokens[0]).toBe(tokens[1]);
    expect(tokens[2]).not.toBe(tokens[1]);
  });

  it('asks for a new token after a 401 and retries the request', async () => {
    const { client, calls, getToken } = inbox((call, i) => (i === 0 ? json(401, { code: 'unauthorized', message: 'expired' }) : actionHandler()(call, i)));
    await expect(client.list()).resolves.toMatchObject({ unread: 2 });
    expect(getToken).toHaveBeenCalledTimes(2);
    expect(calls.map((c) => c.headers.get('authorization'))).toEqual(['Bearer inbox-1', 'Bearer inbox-2']);
  });

  it('accepts a plain JWT string and schedules refresh from its exp claim', async () => {
    const exp = Math.floor(Date.now() / 1000) + 3600;
    expect(jwtExpiry(fakeJwt(exp))).toBe(exp * 1000);
    const getToken = vi.fn(async () => fakeJwt(exp));
    const { client } = inbox(actionHandler(), { getToken });
    await client.list();
    await client.list();
    expect(getToken).toHaveBeenCalledTimes(1);
  });

  it('fails with AuthenticationError when the token source returns nothing', async () => {
    const { client } = inbox(actionHandler(), { getToken: async () => '' });
    await expect(client.list()).rejects.toBeInstanceOf(AuthenticationError);
  });
});

describe('inbox REST and unread tracking', () => {
  it('tracks the unread count across list, read, unread, archive and dismiss without extra requests', async () => {
    const { client, calls } = inbox(actionHandler(2));
    const counts: number[] = [];
    client.onUnreadCountChange((n) => counts.push(n));

    await client.list();
    expect(client.unreadCount).toBe(2);
    await client.markRead('a');
    expect(client.unreadCount).toBe(1);
    await client.markRead('a'); // already read: no change
    await client.markUnread('c');
    expect(client.unreadCount).toBe(2);
    await client.archive('b');
    expect(client.unreadCount).toBe(1);
    await client.dismiss('c');
    expect(client.unreadCount).toBe(0);
    expect(counts).toEqual([2, 1, 2, 1, 0]);
    expect(calls.filter((c) => c.url.searchParams.get('unreadOnly') === 'true')).toHaveLength(0);
  });

  it('posts actions to the documented routes', async () => {
    const { client, calls } = inbox(actionHandler());
    await client.list();
    const updated = await client.performAction('a', 'approve');
    expect(updated.actionTaken).toBe('approve');
    await client.markAllRead();
    expect(client.unreadCount).toBe(0);
    const posts = calls.filter((c) => c.method === 'POST');
    expect(posts.map((c) => c.url.pathname)).toEqual(['/api/v1/inapp/messages/a/action', '/api/v1/inapp/messages/read-all']);
    expect(JSON.parse(posts[0]!.body!)).toEqual({ actionId: 'approve' });
    expect(posts[1]!.body).toBeUndefined();
  });

  it('sends a JSON body with read/unread/archive/dismiss (the hub route requires application/json)', async () => {
    const { client, calls } = inbox(actionHandler());
    await client.markRead('a');
    await client.dismiss('b');
    const posts = calls.filter((c) => c.method === 'POST');
    expect(posts.map((c) => [c.body, c.headers.get('content-type')])).toEqual([['{}', 'application/json'], ['{}', 'application/json']]);
  });

  it('refreshes the count from the hub when the message state was unknown', async () => {
    const { client, calls } = inbox(actionHandler());
    await client.markRead('zzz');
    await vi.waitFor(() => expect(client.unreadCount).toBe(7));
    expect(calls.some((c) => c.url.searchParams.get('unreadOnly') === 'true' && c.url.searchParams.get('pageSize') === '1')).toBe(true);
  });

  it('caches branding and passes list filters', async () => {
    const { client, calls } = inbox((call) => (call.url.pathname.endsWith('/branding')
      ? json(200, { inboxTitle: 'Wings Notifications', primaryColor: '#1565c0' })
      : json(200, { unread: 0, items: [] })));
    expect(await client.getBranding()).toEqual({ inboxTitle: 'Wings Notifications', primaryColor: '#1565c0' });
    await client.getBranding();
    await client.list({ archived: true, category: 'Approval', page: 2, pageSize: 10 });
    expect(calls.filter((c) => c.url.pathname.endsWith('/branding'))).toHaveLength(1);
    expect(calls.at(-1)!.url.search).toBe('?archived=true&category=Approval&page=2&pageSize=10');
  });

  it('reports push interactions with the inbox token', async () => {
    const { client, calls } = inbox(() => new Response(null, { status: 204 }));
    await client.reportPushInteraction('d-1', 'opened');
    expect(calls[0]!.url.pathname).toBe('/api/v1/push/events');
    expect(JSON.parse(calls[0]!.body!)).toEqual({ deliveryId: 'd-1', interaction: 'opened' });
  });
});

describe('realtime (SignalR)', () => {
  function fakeSignalR() {
    const handlers: Record<string, (...a: any[]) => void> = {};
    const lifecycle: { reconnected?: () => void; reconnecting?: () => void; close?: (e?: Error) => void } = {};
    const state = { url: '', tokenFactory: undefined as undefined | (() => string | Promise<string>), started: 0, stopped: 0, autoReconnect: false };
    const connection = {
      start: vi.fn(async () => { state.started++; }),
      stop: vi.fn(async () => { state.stopped++; lifecycle.close?.(); }),
      on: (name: string, h: (...a: any[]) => void) => { handlers[name] = h; },
      off: (name: string) => { delete handlers[name]; },
      onreconnecting: (cb: () => void) => { lifecycle.reconnecting = cb; },
      onreconnected: (cb: () => void) => { lifecycle.reconnected = cb; },
      onclose: (cb: (e?: Error) => void) => { lifecycle.close = cb; },
    };
    class Builder {
      withUrl(url: string, o: { accessTokenFactory: () => string | Promise<string> }) { state.url = url; state.tokenFactory = o.accessTokenFactory; return this; }
      withAutomaticReconnect() { state.autoReconnect = true; return this; }
      configureLogging() { return this; }
      build() { return connection; }
    }
    return { module: { HubConnectionBuilder: Builder } as unknown as SignalRModule, handlers, lifecycle, state };
  }

  it('connects to /hubs/inapp with the inbox token and delivers notifications', async () => {
    const sr = fakeSignalR();
    const { client } = inbox(actionHandler(2), { signalR: sr.module });
    const states: string[] = [];
    client.onConnectionStateChange((s) => states.push(s));
    const received: InAppMessage[] = [];
    client.onNotification((m) => received.push(m));

    await client.list();
    await client.connect();
    await client.connect(); // idempotent
    expect(sr.state.url).toBe('https://hub.example/hubs/inapp');
    expect(sr.state.started).toBe(1);
    expect(sr.state.autoReconnect).toBe(true);
    expect(await sr.state.tokenFactory!()).toBe('inbox-1');
    expect(states).toEqual(['connecting', 'connected']);

    sr.handlers.notification!(msg('new-1', { priority: 'Critical' }));
    sr.handlers.notification!(msg('new-1', { priority: 'Critical' })); // duplicate delivery is not double counted
    expect(received.map((m) => m.id)).toEqual(['new-1', 'new-1']);
    expect(client.unreadCount).toBe(3);

    await client.markRead('new-1');
    expect(client.unreadCount).toBe(2);
  });

  it('resyncs the unread count after reconnecting and reports state changes', async () => {
    const sr = fakeSignalR();
    const { client } = inbox(actionHandler(), { signalR: sr.module });
    const states: string[] = [];
    client.onConnectionStateChange((s) => states.push(s));
    await client.connect();
    sr.lifecycle.reconnecting!();
    sr.lifecycle.reconnected!();
    await vi.waitFor(() => expect(client.unreadCount).toBe(7));
    await client.disconnect();
    expect(sr.state.stopped).toBe(1);
    expect(states).toEqual(['connecting', 'connected', 'reconnecting', 'connected', 'disconnected']);
  });

  it('uses the hubUrl returned with the token', async () => {
    const sr = fakeSignalR();
    const { client } = inbox(actionHandler(), {
      baseUrl: 'https://gw.example/notify/', signalR: sr.module,
      getToken: async () => ({ accessToken: 'x', expiresAt: new Date(Date.now() + 3_600_000).toISOString(), hubUrl: '/hubs/inapp-v2' }),
    });
    await client.connect();
    expect(sr.state.url).toBe('https://gw.example/notify/hubs/inapp-v2');
  });
});
