#!/usr/bin/env node
// Registers a Wings Code application's notification manifest with Wings Notification Hub.
// Zero dependencies (Node.js 20+). Intended for deployment pipelines.
//
//   WNH_URL=https://notifications.wings.example \
//   WINGS_CODE_PROVISIONING_CLIENT_ID=… WINGS_CODE_PROVISIONING_CLIENT_SECRET=… \
//   node register-manifest.mjs notification-manifest.json --secrets-out .wnh-secrets.json
//
// The provisioning credential needs the applications.register scope. Secrets returned by the hub (a new application
// client secret, new webhook signing secrets) are written ONLY to --secrets-out (mode 0600) for your secret store;
// they are never printed. Exit code: 0 success, 1 failure, 2 success with warnings when --strict is given.
import { readFile, writeFile } from 'node:fs/promises';

const args = process.argv.slice(2);
const manifestPath = args.find((a) => !a.startsWith('--')) ?? 'notification-manifest.json';
const secretsOut = valueOf('--secrets-out');
const strict = args.includes('--strict');
const hubUrl = (process.env.WNH_URL ?? 'http://localhost:5480').replace(/\/+$/, '');
const clientId = process.env.WINGS_CODE_PROVISIONING_CLIENT_ID;
const clientSecret = process.env.WINGS_CODE_PROVISIONING_CLIENT_SECRET;

function valueOf(flag) {
  const i = args.indexOf(flag);
  return i >= 0 ? args[i + 1] : undefined;
}

function fail(message) {
  console.error(`✖ ${message}`);
  process.exit(1);
}

if (!clientId || !clientSecret) fail('Set WINGS_CODE_PROVISIONING_CLIENT_ID and WINGS_CODE_PROVISIONING_CLIENT_SECRET.');

const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
delete manifest.$schema;

// 1. Client-credentials token restricted to the provisioning scope.
const tokenRes = await fetch(`${hubUrl}/connect/token`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({ grant_type: 'client_credentials', client_id: clientId, client_secret: clientSecret, scope: 'applications.register' }),
});
const token = await tokenRes.json().catch(() => ({}));
if (!tokenRes.ok) fail(`Token request failed (${tokenRes.status}): ${token.error ?? ''} ${token.error_description ?? ''}`.trim());

// 2. Idempotent upsert keyed by application.code. Retried on 429/502/503/504.
let res;
for (let attempt = 0; ; attempt++) {
  res = await fetch(`${hubUrl}/api/v1/registrations/manifest`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token.access_token}` },
    body: JSON.stringify(manifest),
  });
  if (![429, 502, 503, 504].includes(res.status) || attempt >= 3) break;
  const wait = Number(res.headers.get('retry-after')) * 1000 || 500 * 2 ** attempt;
  await new Promise((r) => setTimeout(r, wait));
}
const body = await res.json().catch(() => ({}));
if (!res.ok) {
  const details = body.errors ? Object.values(body.errors).flat().map((e) => `\n    - ${e}`).join('') : '';
  fail(`Registration failed (${res.status} ${body.code ?? ''}): ${body.message ?? ''}${details}${body.correlationId ? `\n  correlation id: ${body.correlationId}` : ''}`);
}

console.log(`✔ ${manifest.application.code}: application ${body.applicationId} ${body.created ? 'created' : 'updated'}`);
console.log(`  notification types upserted: ${body.notificationTypesUpserted}`);
console.log(`  templates registered:        ${body.templatesRegistered}${body.templatesRegistered ? ' (drafts unless sandbox)' : ' (unchanged)'}`);
console.log(`  webhooks registered:         ${body.webhooksRegistered}`);
if (body.clientId) console.log(`  new credential client id:    ${body.clientId}`);
for (const w of body.warnings ?? []) console.log(`  ⚠ ${w}`);

const secrets = { applicationId: body.applicationId, clientId: body.clientId ?? undefined, clientSecret: body.clientSecret ?? undefined, webhookSecrets: body.webhookSecrets ?? {} };
const hasSecrets = !!secrets.clientSecret || Object.keys(secrets.webhookSecrets).length > 0;
if (hasSecrets && secretsOut) {
  await writeFile(secretsOut, JSON.stringify(secrets, null, 2), { mode: 0o600 });
  console.log(`  secrets written to ${secretsOut} (store them in your secret store; the hub shows them only once)`);
} else if (hasSecrets) {
  console.log('  ⚠ the hub returned one-time secrets but --secrets-out was not given; they were discarded');
}
if (strict && (body.warnings ?? []).length > 0) process.exit(2);
