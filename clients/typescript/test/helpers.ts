export interface RecordedCall {
  url: URL;
  method: string;
  headers: Headers;
  body?: string;
}

export type Handler = (call: RecordedCall, index: number) => Response | Promise<Response>;

/** A fetch stand-in that records every request and answers with the handler's response. */
export function mockFetch(handler: Handler) {
  const calls: RecordedCall[] = [];
  const fetch = async (input: string | URL, init?: RequestInit): Promise<Response> => {
    const call: RecordedCall = {
      url: new URL(String(input)),
      method: init?.method ?? 'GET',
      headers: new Headers(init?.headers),
      ...(typeof init?.body === 'string' ? { body: init.body } : {}),
    };
    calls.push(call);
    return handler(call, calls.length - 1);
  };
  return { fetch, calls };
}

export function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
}

export function tokenResponse(token = 'tok-1', expiresIn = 3600): Response {
  return json(200, { access_token: token, token_type: 'Bearer', expires_in: expiresIn, scope: 'notifications.send' });
}

/** Routes token requests to `token` and everything else to `api`. */
export function hub(api: Handler, token: Handler = () => tokenResponse()) {
  let apiIndex = 0;
  let tokenIndex = 0;
  return mockFetch((call) => (call.url.pathname.endsWith('/connect/token') ? token(call, tokenIndex++) : api(call, apiIndex++)));
}

export const noSleep = { sleeps: [] as number[] };
export function recordingSleep() {
  const sleeps: number[] = [];
  return { sleeps, sleep: async (ms: number) => { sleeps.push(ms); } };
}

/** Builds an unsigned JWT with the given exp (seconds since epoch). */
export function fakeJwt(exp: number, sub = 'u-1001'): string {
  const enc = (o: object) => Buffer.from(JSON.stringify(o)).toString('base64url');
  return `${enc({ alg: 'HS256', typ: 'JWT' })}.${enc({ sub, exp })}.sig`;
}
