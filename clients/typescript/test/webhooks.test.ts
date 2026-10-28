import { createHmac } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { NotificationHubClient, parseWebhookEvent, signWebhook, verifyWebhook, WebhookVerificationError } from '../src/index.js';

const secret = 'whsec_test_secret';
const body = '{"id":"5d0c","type":"delivery.status_changed","occurredAt":"2026-10-28T10:00:00Z","tenantId":"t","data":{"status":"Delivered"}}';
const t = 1_793_181_600; // 2026-10-28T10:00:00Z
const now = t * 1000;

/** Independent implementation of the hub's scheme (WebhookSignature.Sign in Wings.NotificationHub.Core). */
function hubSign(s: string, b: string, ts: number) {
  return `t=${ts},v1=${createHmac('sha256', s).update(`${ts}.${b}`).digest('hex')}`;
}

describe('verifyWebhook', () => {
  it('accepts a signature produced by the hub scheme', () => {
    expect(verifyWebhook(secret, body, hubSign(secret, body, t), { now })).toBe(true);
    expect(NotificationHubClient.verifyWebhook(secret, body, hubSign(secret, body, t), { now })).toBe(true);
  });

  it('accepts the raw body as bytes and handles non-ASCII content', () => {
    const unicode = '{"body":"₹25,000 — invoice ✓"}';
    const header = hubSign(secret, unicode, t);
    expect(verifyWebhook(secret, Buffer.from(unicode, 'utf8'), header, { now })).toBe(true);
    expect(verifyWebhook(secret, new TextEncoder().encode(unicode), header, { now })).toBe(true);
  });

  it('signWebhook matches the hub format', () => {
    expect(signWebhook(secret, body, now)).toBe(hubSign(secret, body, t));
  });

  it('rejects a tampered body, a wrong secret and a missing/malformed header', () => {
    const header = hubSign(secret, body, t);
    expect(verifyWebhook(secret, body.replace('Delivered', 'Read'), header, { now })).toBe(false);
    expect(verifyWebhook('whsec_other', body, header, { now })).toBe(false);
    expect(verifyWebhook(secret, body, undefined, { now })).toBe(false);
    expect(verifyWebhook(secret, body, '', { now })).toBe(false);
    expect(verifyWebhook(secret, body, 'garbage', { now })).toBe(false);
    expect(verifyWebhook(secret, body, `t=${t}`, { now })).toBe(false);
    expect(verifyWebhook(secret, body, header.replace(/v1=[0-9a-f]+/, 'v1=abc'), { now })).toBe(false);
    expect(verifyWebhook('', body, header, { now })).toBe(false);
  });

  it('enforces the 5-minute replay window in both directions', () => {
    const header = hubSign(secret, body, t);
    expect(verifyWebhook(secret, body, header, { now: now + 300_000 })).toBe(true);
    expect(verifyWebhook(secret, body, header, { now: now + 301_000 })).toBe(false);
    expect(verifyWebhook(secret, body, header, { now: now - 301_000 })).toBe(false);
    expect(verifyWebhook(secret, body, header, { now: now + 3_600_000, toleranceSeconds: 3600 })).toBe(true);
  });

  it('accepts any matching v1 entry (secret rotation)', () => {
    const good = hubSign(secret, body, t).split(',')[1];
    expect(verifyWebhook(secret, body, `t=${t},v1=${'0'.repeat(64)},${good}`, { now })).toBe(true);
  });

  it('uses the current clock by default', () => {
    expect(verifyWebhook(secret, body, signWebhook(secret, body))).toBe(true);
    expect(verifyWebhook(secret, body, signWebhook(secret, body, Date.now() - 600_000))).toBe(false);
  });
});

describe('parseWebhookEvent', () => {
  it('returns the envelope for a valid signature', () => {
    const event = parseWebhookEvent<{ status: string }>(secret, body, hubSign(secret, body, t), { now });
    expect(event.type).toBe('delivery.status_changed');
    expect(event.data.status).toBe('Delivered');
  });

  it('throws WebhookVerificationError for an invalid signature', () => {
    expect(() => parseWebhookEvent(secret, body, hubSign('wrong', body, t), { now })).toThrow(WebhookVerificationError);
  });
});
