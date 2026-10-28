import { createHmac, timingSafeEqual } from 'node:crypto';
import type { WebhookEventEnvelope } from './types.js';

/** Header carrying the signature: `t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>`. */
export const WEBHOOK_SIGNATURE_HEADER = 'X-Wings-Signature';
/** Default replay window: signatures older (or newer) than 5 minutes are rejected. */
export const DEFAULT_WEBHOOK_TOLERANCE_SECONDS = 300;

export interface VerifyWebhookOptions {
  /** Replay window in seconds (default 300). */
  toleranceSeconds?: number;
  /** Clock override (Date or epoch milliseconds), for tests. */
  now?: Date | number;
}

export class WebhookVerificationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'WebhookVerificationError';
  }
}

/**
 * Verifies a webhook POSTed by the hub. Pass the raw request body exactly as received (do not re-serialize parsed JSON),
 * the shared signing secret and the X-Wings-Signature header. Uses a constant-time comparison and rejects
 * timestamps outside the replay window. Multiple v1 entries (secret rotation) are accepted.
 */
export function verifyWebhook(secret: string, rawBody: string | Uint8Array, signatureHeader: string | null | undefined, options: VerifyWebhookOptions = {}): boolean {
  if (!secret || !signatureHeader) return false;
  let t: number | undefined;
  const signatures: string[] = [];
  for (const part of signatureHeader.split(',')) {
    const i = part.indexOf('=');
    if (i <= 0) continue;
    const key = part.slice(0, i).trim();
    const value = part.slice(i + 1).trim();
    if (key === 't' && /^\d+$/.test(value)) t = Number(value);
    else if (key === 'v1' && value) signatures.push(value);
  }
  if (t === undefined || signatures.length === 0) return false;
  const nowMs = options.now === undefined ? Date.now() : typeof options.now === 'number' ? options.now : options.now.getTime();
  const tolerance = options.toleranceSeconds ?? DEFAULT_WEBHOOK_TOLERANCE_SECONDS;
  if (Math.abs(Math.floor(nowMs / 1000) - t) > tolerance) return false;
  const expected = Buffer.from(computeSignature(secret, t, rawBody), 'ascii');
  return signatures.some((s) => {
    const candidate = Buffer.from(s.toLowerCase(), 'ascii');
    return candidate.length === expected.length && timingSafeEqual(candidate, expected);
  });
}

/** Verifies and parses a webhook body; throws WebhookVerificationError when the signature is invalid or stale. */
export function parseWebhookEvent<TData = Record<string, unknown>>(secret: string, rawBody: string | Uint8Array, signatureHeader: string | null | undefined,
                                                                   options?: VerifyWebhookOptions): WebhookEventEnvelope<TData> {
  if (!verifyWebhook(secret, rawBody, signatureHeader, options)) throw new WebhookVerificationError('Invalid or expired webhook signature.');
  const text = typeof rawBody === 'string' ? rawBody : Buffer.from(rawBody).toString('utf8');
  return JSON.parse(text) as WebhookEventEnvelope<TData>;
}

/** Produces a signature header the way the hub does (local testing of your webhook receiver). */
export function signWebhook(secret: string, rawBody: string | Uint8Array, timestamp: Date | number = Date.now()): string {
  const t = Math.floor((typeof timestamp === 'number' ? timestamp : timestamp.getTime()) / 1000);
  return `t=${t},v1=${computeSignature(secret, t, rawBody)}`;
}

function computeSignature(secret: string, t: number, rawBody: string | Uint8Array): string {
  const hmac = createHmac('sha256', Buffer.from(secret, 'utf8'));
  hmac.update(`${t}.`, 'utf8');
  hmac.update(typeof rawBody === 'string' ? Buffer.from(rawBody, 'utf8') : rawBody);
  return hmac.digest('hex');
}
