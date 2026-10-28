import type { ApiErrorBody } from './types.js';

export interface HubErrorInit {
  status: number;
  code: string;
  message: string;
  errors?: Record<string, string[]> | null;
  correlationId?: string | null;
  body?: string;
  retryAfterMs?: number;
  cause?: unknown;
}

/**
 * Base class of every error thrown by the SDK. `status` is the HTTP status (0 for network failures),
 * `code` the hub's machine-readable error code (e.g. "validation_failed", "rate_limited", "invalid_client").
 */
export class NotificationHubError extends Error {
  readonly status: number;
  readonly code: string;
  /** Field-level validation messages, when the hub returned them. */
  readonly errors?: Record<string, string[]>;
  /** X-Correlation-Id of the failed request; quote it when reporting problems. */
  readonly correlationId?: string;
  /** Raw response body (useful when the body was not an ApiError). */
  readonly body?: string;

  constructor(init: HubErrorInit) {
    super(init.message, init.cause === undefined ? undefined : { cause: init.cause });
    this.name = new.target.name;
    this.status = init.status;
    this.code = init.code;
    if (init.errors) this.errors = init.errors;
    if (init.correlationId) this.correlationId = init.correlationId;
    if (init.body !== undefined) this.body = init.body;
  }
}

/** 400 / 422: the request was rejected by validation. Do not retry without changing it. */
export class ValidationError extends NotificationHubError {}
/** 401 or a token-endpoint failure: bad client credentials, expired/invalid token. */
export class AuthenticationError extends NotificationHubError {}
/** 403: authenticated but not allowed (missing scope, wrong tenant/company, AI restrictions). */
export class PermissionDeniedError extends NotificationHubError {}
/** 404: not found, or not visible to this credential (the hub does not distinguish). */
export class NotFoundError extends NotificationHubError {}
/** 409: state conflict (e.g. not retryable, concurrency conflict). */
export class ConflictError extends NotificationHubError {}
/** 429: rate limited. The SDK already retried with Retry-After before throwing this. */
export class RateLimitError extends NotificationHubError {
  /** Server-suggested delay before retrying, when it sent Retry-After. */
  readonly retryAfterMs?: number;
  constructor(init: HubErrorInit) {
    super(init);
    if (init.retryAfterMs !== undefined) this.retryAfterMs = init.retryAfterMs;
  }
}
/** 5xx: hub-side failure. 502/503/504 were already retried before throwing. */
export class ServerError extends NotificationHubError {}
/** The request never produced an HTTP response (DNS, connection reset, timeout, CORS). */
export class NetworkError extends NotificationHubError {}

/** Maps an HTTP error response to the matching typed error. Understands ApiError and OAuth error bodies. */
export function errorFromResponse(status: number, bodyText: string, headers?: { get(name: string): string | null }): NotificationHubError {
  let code = `http_${status}`;
  let message = `Notification Hub returned HTTP ${status}`;
  let errors: Record<string, string[]> | null | undefined;
  let correlationId: string | null | undefined = headers?.get('x-correlation-id');
  const parsed = tryParse(bodyText);
  if (parsed && typeof parsed === 'object') {
    const api = parsed as Partial<ApiErrorBody> & { error?: string; error_description?: string; title?: string; detail?: string };
    if (typeof api.code === 'string' && api.code) {
      code = api.code;
      message = typeof api.message === 'string' && api.message ? api.message : message;
      errors = api.errors;
      correlationId = api.correlationId ?? correlationId;
    } else if (typeof api.error === 'string') {
      // OAuth 2.0 token endpoint: {"error":"invalid_client","error_description":"…"}
      code = api.error;
      message = api.error_description ?? api.error;
    } else if (typeof api.title === 'string') {
      message = api.detail ?? api.title; // RFC 7807 problem details
    }
  } else if (bodyText) {
    message = `${message}: ${bodyText.slice(0, 500)}`;
  }
  const retryAfterMs = parseRetryAfter(headers?.get('retry-after') ?? null);
  const init: HubErrorInit = { status, code, message, errors, correlationId, body: bodyText };
  if (retryAfterMs !== undefined) init.retryAfterMs = retryAfterMs;
  if (status === 400 || status === 422) return new ValidationError(init);
  if (status === 401) return new AuthenticationError(init);
  if (status === 403) return new PermissionDeniedError(init);
  if (status === 404) return new NotFoundError(init);
  if (status === 409) return new ConflictError(init);
  if (status === 429) return new RateLimitError(init);
  if (status >= 500) return new ServerError(init);
  return new NotificationHubError(init);
}

/** Retry-After as milliseconds: delta-seconds or an HTTP date. Undefined when absent/unparseable. */
export function parseRetryAfter(value: string | null, now: number = Date.now()): number | undefined {
  if (!value) return undefined;
  const trimmed = value.trim();
  if (/^\d+(\.\d+)?$/.test(trimmed)) return Math.round(Number(trimmed) * 1000);
  const at = Date.parse(trimmed);
  return Number.isNaN(at) ? undefined : Math.max(0, at - now);
}

function tryParse(text: string): unknown {
  if (!text) return undefined;
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}
