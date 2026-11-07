# Testing

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet test tests/Wings.NotificationHub.UnitTests
dotnet test tests/Wings.NotificationHub.IntegrationTests
```

* **Unit tests** (111) — pure logic: template engine (escaping, filters, blocks, syntax errors, unsafe HTML, variable
  usage), WhatsApp positional parameters, consent/preference/suppression matrix, quiet hours across midnight, working
  days and holidays, retry backoff, forward-only status rules and request aggregation, E.164 normalization and masking,
  SMS GSM-7/UCS-2 segmentation, condition trees, AES-GCM key rotation and purpose binding, password/secret hashing,
  webhook signatures and replay window, caller/role/scope access rules, WhatsApp/Twilio webhook parsing and signature
  checks, Web Push aes128gcm encryption (round-tripped through a receiver-side decryptor) and VAPID JWTs, FCM payloads,
  SSRF guard, CSV parsing, channel wire names; and provider adapters against HTTP doubles (Meta WhatsApp, Twilio,
  MSG91/DLT, generic HTTP SMS, FCM with service-account OAuth, Web Push with VAPID, Microsoft Graph sendMail): request
  URL/auth/payload shape and error-code → outcome mapping.
* **Integration tests** (65) — `WebApplicationFactory` runs the real API **with all workers** (outbox relay, queue
  consumers, scheduler, reconciliation, mock receipts) on a private SQLite database seeded with the demo data. Tests use
  the C# SDK, raw HTTP, SignalR and the database. Outbound webhooks go to an in-memory receiver that can be told to fail.
* **Load test** — `tests/Wings.NotificationHub.LoadTests` (console; see below).
* **Client SDK tests** — `clients/typescript` (`npm test`, 50 vitest tests: tokens, idempotency, retries, errors, webhook
  verification, inbox) and `clients/angular-inbox` (`npm test`, helper tests).
* **Admin console tests** — `web/` (`npx ng test --watch=false`, 28 vitest tests: condition-builder model, error
  interceptor, query mapping, variable extraction, SMS counter, holiday parsing). Every screen was also exercised manually
  in a browser against a seeded instance; not-yet-submitted forms are listed in the UI section of the final report.

## Specification §21 acceptance scenarios → tests

| Scenario | Test(s) |
|---|---|
| Tenant isolation | `AccessAndChannelTests.Tenants_cannot_see_or_act_on_each_others_data` (+ `SecurityTests.Caller_scopes_…`) |
| Application registration | `Wings_code_registers_a_sandbox_application_from_a_manifest`, `Emergency_suspension…` (admin app creation) |
| Sending single notifications | `Single_notification_is_accepted_durably_then_delivered`, `Sdk_anonymous_overload_used_by_generated_code_works` |
| Sending on multiple channels | `Multi_channel_request_tracks_each_channel_independently` |
| Template rendering and versioning | `Template_versioning_keeps_history_and_requires_maker_checker`, `TemplateEngineTests.*` |
| Invalid template variables | `Invalid_template_variables_are_rejected_before_acceptance` |
| Scheduled delivery | `Scheduled_delivery_waits_until_due_and_can_be_cancelled`, `Recurring_schedule_runs_on_cron` |
| Bulk message dispatch | `Batch_dispatch_accepts_items_independently`, `Campaign_import_approval_and_throttled_dispatch` |
| Duplicate API requests | `Duplicate_requests_with_one_idempotency_key_create_one_notification` (8 concurrent + key-reuse conflict) |
| Queue failures and worker restart | `Worker_crash_recovery_resets_abandoned_leases`, `Queue_message_failures_are_retried_and_dead_lettered_with_alert` |
| Provider timeout ambiguity | `Ambiguous_timeout_with_idempotent_provider_retries_without_duplicate`, `…without_idempotency_is_…held_for_an_operator`, `…with_status_query_is_reconciled_without_resend` |
| Provider failover | `Provider_failover_moves_to_secondary_when_primary_is_down` |
| Delivery callbacks | `Provider_callbacks_are_authenticated_idempotent_and_never_regress`, `Meta_whatsapp_webhook_requires_valid_signature` |
| Out-of-order callbacks | `Provider_callbacks_…never_regress` (late "sent" after "delivered"), `Callback_arriving_before_acceptance_is_applied_afterwards`, `StatusRuleTests` |
| Consent validation | `Consent_is_enforced_per_channel_and_class` |
| Opt-out enforcement | `Whatsapp_stop_reply_and_unsubscribe_link_opt_the_recipient_out`, `Opt_out_after_acceptance_is_honoured_at_send_time` |
| Notification preferences | `Preferences_apply_to_transactional_but_not_critical_messages` |
| Unauthorized access | `Unauthorized_access_is_refused`, `Revoked_credentials_stop_working_immediately` |
| Rate limiting | `Per_application_rate_limit_returns_429`, `Monthly_quota_blocks_sends_beyond_the_limit` |
| (query filters) | `Enum_query_filters_accept_wire_casing` |
| In-app delivery | `In_app_notifications_arrive_in_real_time_and_track_reads` (SignalR + inbox API + action webhook) |
| Device registration and invalidation | `Device_registration_and_invalid_token_cleanup` |
| Inbound WhatsApp replies | `Inbound_whatsapp_reply_is_correlated_and_routed_to_the_application` |
| Webhook retries | `Webhook_subscribers_receive_signed_events_with_retries` |
| Sensitive-data redaction | `Sensitive_content_is_redacted_for_roles_and_logs`, `Meta_whatsapp_webhook…` (secrets never echoed), `SecurityTests.Redaction_…` |
| Billing and usage reconciliation | `Usage_and_billing_reconcile_with_accepted_deliveries`, `Prepaid_credits_are_reserved_consumed_and_enforced` |

Also covered: permanent failure → suppression + channel fallback, escalation when not read in time, business-event rules
with nested conditions and event idempotency, role-targeted rule simulation, MCP tools with AI restrictions and human
approval, emergency suspension holding messages, OpenAPI/health endpoints.

## Load testing

```bash
dotnet run --project tests/Wings.NotificationHub.LoadTests -- --count 2000 --concurrency 32            # single sends
dotnet run --project tests/Wings.NotificationHub.LoadTests -- --count 5000 --concurrency 8 --batch 100 --no-wait
```

Credentials default to `App_Data/dev-credentials.env` (ERP demo app); override with `WNH_CLIENT_ID/SECRET`, `--url`,
`--company`, `--channel`. The ERP app's default API limit (600 requests/min) applies, so raise `rateLimitPerMinute`
for the application before a load run (`PUT /api/v1/applications/{id}`).

Measured on this Mac (Apple Silicon, SQLite in WAL mode, API + workers in one process, mock providers), 2026-10-28:

| Run | Result |
|---|---|
| 2,000 single sends, 32 concurrent | 415 req/s accepted; per-call p50 1.4 ms, p95 153 ms, p99 462 ms |
| 5,000 notifications in batches of 100, 8 concurrent | 828 notifications/s accepted |
| End-to-end processing of the 2,000 (queue → provider acceptance → receipts) | ≈ 20 deliveries/s; all sampled notifications final after ~105 s |

Acceptance (the internal SLO) scales well; end-to-end throughput on SQLite is bounded by its single writer and by the demo
mock email provider's 50 messages/s rate limit. SQL Server with separate worker instances is the production topology and
has **not** been load-tested here.

## Not verified on this machine

* Real providers (SMTP, Amazon SES + SNS, Microsoft Graph, Twilio, MSG91, Meta/360dialog WhatsApp, FCM, APNs, Web Push
  push services): adapters are implemented against the providers' documented APIs and unit-tested for payloads,
  signatures and parsing, but no live credentials were available.
* SQL Server: migrations and scripts are generated from the model (`database/sqlserver`), but not executed (no SQL Server here).
* RabbitMQ and Redis code paths (transport, SignalR backplane, shared rate limiting) — no broker/cache installed here; the
  database transport and in-memory implementations are what the tests exercise.
* ClickHouse analytics sink, OIDC with a real identity provider, Docker images/compose, IIS, Windows service, systemd.
* WinForms and Ionic samples (need Windows / a mobile toolchain).
