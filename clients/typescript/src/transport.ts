import { errorFromResponse, NetworkError, parseRetryAfter } from './errors.js';

export type FetchLike = (input: string | URL, init?: RequestInit) => Promise<Response>;

export interface TransportOptions {
  /** Base URL of the hub, e.g. "https://notifications.wings.example". A path prefix is kept. */
  baseUrl: string | URL;
  /** fetch implementation (defaults to globalThis.fetch). */
  fetch?: FetchLike;
  /** Retries for 429/502/503/504 and network errors (default 3). Sends are safe to retry: they carry idempotency keys. */
  maxRetries?: number;
  /** Per-attempt timeout in milliseconds (default 30 000; 0 disables). */
  timeoutMs?: number;
  /** Upper bound for a single Retry-After/backoff wait in milliseconds (default 30 000). */
  maxRetryDelayMs?: number;
  /** Extra headers sent with every request. */
  headers?: Record<string, string>;
  /** Replaces the delay between retries (tests, custom schedulers). */
  sleep?: (ms: number) => Promise<void>;
}

/** Per-call options accepted by every SDK method. */
export interface CallOptions {
  signal?: AbortSignal;
  headers?: Record<string, string>;
}

export type QueryValue = string | number | boolean | null | undefined;

export interface RequestSpec {
  method: 'GET' | 'POST' | 'PUT' | 'DELETE';
  path: string;
  query?: Record<string, QueryValue>;
  json?: unknown;
  form?: Record<string, string>;
  headers?: Record<string, string>;
  /** Supplies a bearer token; forceRefresh is true once after a 401. */
  auth?: (forceRefresh: boolean) => Promise<string | undefined>;
  /** "all" (default) retries 429/502/503/504 and network errors; "rate-limit" only 429 (non-idempotent calls). */
  retry?: 'all' | 'rate-limit' | 'none';
  signal?: AbortSignal;
}

const RETRYABLE_STATUS = new Set([429, 502, 503, 504]);

export class HttpTransport {
  readonly baseUrl: URL;
  private readonly fetchImpl: FetchLike;
  private readonly maxRetries: number;
  private readonly timeoutMs: number;
  private readonly maxRetryDelayMs: number;
  private readonly defaultHeaders: Record<string, string>;
  private readonly sleep: (ms: number) => Promise<void>;

  constructor(options: TransportOptions) {
    this.baseUrl = normalizeBaseUrl(options.baseUrl);
    if (!options.fetch && typeof globalThis.fetch !== 'function') throw new Error('No fetch implementation available; pass options.fetch.');
    // Unbound window.fetch throws "Illegal invocation" in browsers, so wrap the global.
    this.fetchImpl = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
    this.maxRetries = Math.max(0, options.maxRetries ?? 3);
    this.timeoutMs = options.timeoutMs ?? 30_000;
    this.maxRetryDelayMs = options.maxRetryDelayMs ?? 30_000;
    this.defaultHeaders = { ...(options.headers ?? {}) };
    this.sleep = options.sleep ?? ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
  }

  url(path: string, query?: Record<string, QueryValue>): URL {
    const url = new URL(path.replace(/^\/+/, ''), this.baseUrl);
    for (const [k, v] of Object.entries(query ?? {})) {
      if (v !== undefined && v !== null && v !== '') url.searchParams.set(k, String(v));
    }
    return url;
  }

  async request<T>(spec: RequestSpec): Promise<T> {
    const url = this.url(spec.path, spec.query);
    const retryMode = spec.retry ?? 'all';
    let retries = 0;
    let forceRefresh = false;
    let authRetried = false;

    for (;;) {
      const headers = new Headers({ Accept: 'application/json', ...this.defaultHeaders, ...(spec.headers ?? {}) });
      if (spec.auth) {
        const token = await spec.auth(forceRefresh);
        if (token) headers.set('Authorization', `Bearer ${token}`);
      }
      let body: string | undefined;
      if (spec.json !== undefined) {
        headers.set('Content-Type', 'application/json');
        body = JSON.stringify(spec.json);
      } else if (spec.form) {
        headers.set('Content-Type', 'application/x-www-form-urlencoded');
        body = new URLSearchParams(spec.form).toString();
      }

      const attempt = attemptSignal(spec.signal, this.timeoutMs);
      let res: Response;
      try {
        const init: RequestInit = { method: spec.method, headers };
        if (body !== undefined) init.body = body;
        if (attempt.signal) init.signal = attempt.signal;
        res = await this.fetchImpl(url, init);
      } catch (err) {
        attempt.dispose();
        if (spec.signal?.aborted) throw spec.signal.reason ?? err;
        if (retryMode === 'all' && retries < this.maxRetries) {
          await this.sleep(Math.min(backoff(retries++), this.maxRetryDelayMs));
          continue;
        }
        const timedOut = attempt.timedOut();
        throw new NetworkError({
          status: 0,
          code: timedOut ? 'timeout' : 'network_error',
          message: timedOut ? `Request to ${url.pathname} timed out after ${this.timeoutMs} ms` : `Request to ${url.pathname} failed: ${describe(err)}`,
          cause: err,
        });
      }

      try {
        // A 401 on a cached token usually means it expired or was rotated: refresh once and retry.
        if (res.status === 401 && spec.auth && !authRetried) {
          authRetried = true;
          forceRefresh = true;
          await drain(res);
          continue;
        }
        forceRefresh = false;
        if (RETRYABLE_STATUS.has(res.status) && retries < this.maxRetries
            && (retryMode === 'all' || (retryMode === 'rate-limit' && res.status === 429))) {
          const wait = parseRetryAfter(res.headers.get('retry-after')) ?? backoff(retries);
          retries++;
          await drain(res);
          await this.sleep(Math.min(wait, this.maxRetryDelayMs));
          continue;
        }
        const text = await res.text();
        if (!res.ok) throw errorFromResponse(res.status, text, res.headers);
        if (!text) return undefined as T;
        const contentType = res.headers.get('content-type') ?? '';
        if (contentType.includes('json') || /^\s*[[{"]/.test(text)) return JSON.parse(text) as T;
        return text as T;
      } finally {
        attempt.dispose();
      }
    }
  }
}

/** Exponential backoff with jitter: 200 ms · 2^n + 0–200 ms (same schedule as the C# SDK). */
export function backoff(attempt: number): number {
  return 200 * 2 ** attempt + Math.floor(Math.random() * 200);
}

export function normalizeBaseUrl(base: string | URL): URL {
  const raw = typeof base === 'string' ? base : base.toString();
  const withSlash = raw.endsWith('/') ? raw : `${raw}/`;
  const origin = (globalThis as { location?: { href?: string } }).location?.href;
  return origin ? new URL(withSlash, origin) : new URL(withSlash);
}

/** Random idempotency key (32 hex characters, like Guid.ToString("N")). */
export function newIdempotencyKey(): string {
  const c = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (c?.randomUUID) return c.randomUUID().replace(/-/g, '');
  let s = '';
  for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
  return s;
}

function attemptSignal(user: AbortSignal | undefined, timeoutMs: number): { signal?: AbortSignal; dispose(): void; timedOut(): boolean } {
  if (!timeoutMs || timeoutMs <= 0) return { ...(user ? { signal: user } : {}), dispose() {}, timedOut: () => false };
  const ctrl = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    ctrl.abort(new DOMException('The request timed out.', 'TimeoutError'));
  }, timeoutMs);
  const onAbort = () => ctrl.abort(user?.reason);
  if (user) {
    if (user.aborted) ctrl.abort(user.reason);
    else user.addEventListener('abort', onAbort, { once: true });
  }
  return {
    signal: ctrl.signal,
    dispose() {
      clearTimeout(timer);
      user?.removeEventListener('abort', onAbort);
    },
    timedOut: () => timedOut,
  };
}

async function drain(res: Response): Promise<void> {
  try {
    await res.body?.cancel();
  } catch {
    /* ignore */
  }
}

function describe(err: unknown): string {
  if (err instanceof Error) return err.cause instanceof Error ? `${err.message} (${err.cause.message})` : err.message;
  return String(err);
}
