// Run with `npm test` (node --test; Node 22.18+/24 strips the TypeScript types natively).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { badgeText, contrastText, inboxTheme, relativeTime, resolveDeepLink, safeColor, safeImageUrl } from './utils.ts';

test('resolveDeepLink: relative and same-origin links go through the router', () => {
  assert.deepEqual(resolveDeepLink('/sales/orders/SO-1?tab=2'), { kind: 'internal', path: '/sales/orders/SO-1?tab=2' });
  assert.deepEqual(resolveDeepLink('https://erp.example/inv/1#x', 'https://erp.example'), { kind: 'internal', path: '/inv/1#x' });
});

test('resolveDeepLink: other http(s) links open externally', () => {
  assert.deepEqual(resolveDeepLink('https://docs.example/a', 'https://erp.example'), { kind: 'external', href: 'https://docs.example/a' });
});

test('resolveDeepLink: dangerous or unusable links are blocked', () => {
  for (const url of ['javascript:alert(1)', 'JAVASCRIPT:alert(1)', 'data:text/html,<b>x</b>', '//evil.example/x', '/\\evil', 'wingserp://orders/1', '#', '', null, 'not a url']) {
    assert.equal(resolveDeepLink(url as string, 'https://erp.example').kind, 'blocked', String(url));
  }
});

test('safeImageUrl only allows http(s)', () => {
  assert.equal(safeImageUrl('https://cdn.example/a.png'), 'https://cdn.example/a.png');
  assert.equal(safeImageUrl('javascript:alert(1)'), null);
  assert.equal(safeImageUrl('data:image/png;base64,AAAA'), null);
  assert.equal(safeImageUrl(undefined), null);
});

test('branding colours are validated and get a readable text colour', () => {
  assert.equal(safeColor('#1565c0'), '#1565c0');
  assert.equal(safeColor('rgb(10, 20, 30)'), 'rgb(10, 20, 30)');
  assert.equal(safeColor('red; background:url(https://x)'), null);
  assert.equal(safeColor('expression(alert(1))'), null);
  assert.equal(safeColor(42), null);
  assert.equal(contrastText('#1565c0'), '#ffffff');
  assert.equal(contrastText('#ffeb3b'), '#1f1f1f');
  assert.equal(contrastText('#fff'), '#1f1f1f');
  assert.deepEqual(inboxTheme({ primaryColor: '#ffeb3b' }), { primary: '#ffeb3b', onPrimary: '#1f1f1f', accent: '#ffeb3b' });
  assert.deepEqual(inboxTheme({ primaryColor: 'bogus' }), { primary: '#1565c0', onPrimary: '#ffffff', accent: '#1565c0' });
  assert.deepEqual(inboxTheme(null), { primary: '#1565c0', onPrimary: '#ffffff', accent: '#1565c0' });
});

test('relativeTime and badgeText', () => {
  const now = Date.parse('2026-10-28T10:00:00Z');
  assert.equal(relativeTime('2026-10-28T09:59:50Z', now, 'en'), 'now');
  assert.equal(relativeTime('2026-10-28T09:55:00Z', now, 'en'), '5 minutes ago');
  assert.equal(relativeTime('2026-10-27T10:00:00Z', now, 'en'), 'yesterday');
  assert.equal(relativeTime('garbage', now, 'en'), '');
  assert.equal(badgeText(0), '');
  assert.equal(badgeText(7), '7');
  assert.equal(badgeText(120), '99+');
});
