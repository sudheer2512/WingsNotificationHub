# Administrator guide

Sign in at the hub URL with your administrator account (local or your organisation's identity provider). Menus appear
according to your role and scope (tenant, company, application, channel).

## Onboarding a customer

1. **Tenant** (platform administrators): code, name, whether shared Wings providers may be used, whether promotional
   messages need recorded marketing consent, default time zone, white-label branding (logo, colours, inbox title, e-mail
   footer), body/data retention days.
2. **Companies**: country, time zone, branding override, quiet hours (e.g. 21:00–08:00 for promotional messages).
3. **Applications**: code, permitted channels, default locale, API rate limit, sandbox flag (sandbox apps only reach mock
   providers). Enable the application for each company and optionally pick default senders.
4. **Credentials**: create a client credential per deployment with only the scopes it needs; restrict to companies if
   required; copy the secret (shown once). Revoke immediately when compromised.
5. **Users & roles**: grant roles at the narrowest scope that works (e.g. Template Designer for one company).
6. **Providers**: use shared accounts or add customer-owned ones (see [providers.md](providers.md)); add sender identities,
   test connectivity, set fallbacks and routing rules; set quotas, cost and billing rates, prepaid credits.

## Templates (maker-checker)

Template Manager shows Tenant → Company → Application → Category → Templates. Create a template for a code, channel and
locale (translations are separate locales of the same code; clone to start one). Edit the draft with the rich-text or HTML
editor, insert variables, preview with sample data (SMS shows encoding and segments), send a test. **Submit** for review; a
different person with the Template Approver role **approves** (optionally activating it) or rejects with a comment.
Activating a new version never changes history: every delivery records the version it used. Roll back by activating an
older approved/retired version. Template HTML cannot contain scripts, event handlers, `javascript:` links or frames.

## Rules

Rule Designer: choose the event type, build conditions with nested AND/OR groups (fields are paths in the event payload,
e.g. `payload.DaysOverdue >= 7`), choose recipients (from a payload object such as `customer`, specific users, roles,
a group, or the whole company), add channel actions with templates and optional fallback channels, priority, delay,
escalation (e.g. SMS if not read within 30 minutes), retry policy and quiet hours. Use **Simulate** with a sample payload
to see matched rules, resolved recipients, consent decisions and rendered messages — nothing is sent.

## Campaigns

Create a draft (channels, template, message class, groups/segment, variables, schedule, throttle per minute), import
recipients from CSV/Excel (columns `email`, `mobile`, `name`, `externalId` + template variables; invalid rows are listed),
send a test, then **submit**; a different authorised person **approves**. Approved campaigns start at the scheduled time
and send in throttled batches. Promotional campaigns honour marketing consent, unsubscribes, suppressions and quiet hours;
skipped recipients show the reason. Pause, resume or cancel at any time (cancel also cancels queued messages).

## Daily operations

* **Dashboard**: volumes, success rate, channel usage, provider health and latency, queue depth, retry backlog, cost.
* **Notification Explorer**: search by recipient, notification id, application, company, channel, provider, status,
  date; export CSV/Excel; open a delivery to see its full timeline and sanitized provider responses. Retry failed
  deliveries; resolve *unknown-outcome* deliveries after checking with the provider.
* **Communication Inbox**: customer replies by thread; reply within WhatsApp's 24-hour window.
* **Approvals**: pending template versions, campaigns, provider credential changes and AI-agent notifications.
* **Usage & Billing**, **Reports** (15 reports with CSV/Excel export), **System Monitoring**, **Audit log**.

## Consent and preferences

Applications record consent evidence (`POST /consents`: channel, purpose, granted/withdrawn, source, evidence) and user
preferences (per category and channel). Rules applied to every message:

| Message class | Preferences | Marketing consent | WhatsApp opt-in | Unsubscribe | Bounce/invalid | Complaint/opt-out |
|---|---|---|---|---|---|---|
| Transactional | honoured (unless the type is *mandatory*) | – | required | ignored | blocks | blocks |
| Promotional | honoured | required (if tenant requires) | required | blocks | blocks | blocks |
| Critical (security/regulatory) | ignored | – | required | ignored | blocks | ignored |

Opt-outs are honoured even for messages accepted earlier: the hub re-checks at send time.

## Emergency controls

Suspend a tenant, company, application, campaign, provider account or sender identity from its page (or the API). New
sends are refused; queued messages are held without consuming retries and resume when the suspension is lifted. Every
suspension raises an alert and is audited.
