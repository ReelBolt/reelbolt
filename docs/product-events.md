# Product events: the funnel table

`product_events` is the table the signup → activation → first-value funnel is read from. It is
append-only, holds no PII, and carries a `source` column that separates a **deliberate absence**
from a **real drop-off**. Read this page before you build a funnel query or add an event: the BI
team reads the table through `powerbi_ro`, and this is the reference for what its rows mean.

This page is internal background, not published on the docs site.

To post a tracked link, turn the funnel on or read it per link and per campaign, start from
the operator runbook. This page is the reference for the rows.

## The table

The Inference API owns the schema (`InferenceApiDbContext`); the Go API maps the table with GORM and
never migrates it. A migration creates it and applies on container start.

| Column | Type | Nullable | Meaning |
|---|---|---|---|
| `id` | `bigint` identity | no | Primary key. |
| `user_id` | `uuid` | yes | The person, when one is known. |
| `organization_id` | `uuid` | yes | The organization, when the event is an organizational fact. Also set on `alpha.signup`: the person's Personal workspace (see [the vocabulary](#the-event-vocabulary)). |
| `event` | `varchar(64)` | no | The event name, from the closed vocabulary below. |
| `occurred_at` | `timestamptz` | no | When the event happened. |
| `source` | `varchar(16)` | no | `server`, `client` or `consent`. |
| `detail` | `varchar(64)` | yes | A closed token, never free text. What it holds depends on the event: the source label (`site.visit`, `signup.started`), the page key (`site.page_view`), the CTA location (`site.cta_click`), the template key (`product.template_chosen`), the checklist item key (`onboarding.checklist_item_done`), the persona (`onboarding.profile_saved`). Null on every other row. |
| `visitor_id` | `uuid` | yes | The random anonymous visitor id, on the five visitor events only (`site.*`, `signup.started`, `signup.submitted`). |

Two `CHECK` constraints hold the shape:

- `ck_product_events_subject` — `user_id IS NOT NULL OR organization_id IS NOT NULL OR visitor_id IS
  NOT NULL`. A row needs a subject; before the launch analytics work the check had no `visitor_id` arm. A payment belongs to an organization and may have no person; a client event belongs to a
  person and may have no organization. Neither id is forced non-null, because inventing one would
  force a lie.
- `ck_product_events_source` — `source IN ('server', 'client', 'consent')`. A fourth value needs a
  migration, on purpose.

Three indexes serve the reader: `(user_id, occurred_at)`, `(event, occurred_at)` and
`(organization_id)`. A partial unique index, `(organization_id, event) WHERE event =
'alpha.first_payment'`, makes the activation step at most one row per organization.

**Append-only.** Nothing updates or deletes a row, and there is no state machine. A later decision
is a later row, and the reader takes the latest. **No PII.** A row is ids, a closed event name, a
timestamp and a source — never an email, display name, IP, user agent, free text or request payload.

## The `source` vocabulary

`source` is the column the funnel's honesty rests on. It says who observed the event, and that is
what lets a reader tell three different silences apart.

| `source` | Who observed it | When a row exists |
|---|---|---|
| `server` | a backend service | Always, for every user the transition happened to. Consent cannot erase it. |
| `client` | the browser | Only when the person has granted telemetry consent. |
| `consent` | the browser, on the person's instruction | Whenever the person has made a telemetry choice — granted or declined — whether or not consent was given. |

The `consent` rows are why the column exists. The browser's telemetry consent is otherwise
`localStorage`-only, so the server never learns it and a person who declined is indistinguishable
from one who dropped off. This work delivers the decision to the server as one row, and that row is
the one transmission a declining browser still makes — recording the choice is how the choice is
honoured. The house precedent is the append-only log in
[`marketing-consent.md`](marketing-consent.md).

### The three reader outcomes

Read a client step with `source` in hand, and you get one of three outcomes, not one:

| The person's latest `consent` row | A missing `client` row means |
|---|---|
| `telemetry.consent.declined` | **deliberate absence** — a stated fact, not a silent zero. Exclude them from the client-step denominator. |
| `telemetry.consent.granted` | **a real drop-off** — their client events would have arrived, so their silence is a drop-off. |
| none (never asked) | **not measurable** — neither granted nor declined, so exclude them from the client-step denominator rather than counting them as a drop-off. |

Take the **latest** `consent` row per person: a person who declined and later granted reads
`measurable`, so a stale decline cannot mark a now-consented person unmeasurable forever.

## The event vocabulary

`varchar(64)` means a new event **name** costs no migration; the set below is the vocabulary the
emitters write.

| `event` | `source` | Emitter |
|---|---|---|
| `alpha.signup` | `server` | Go API — `handleSignup`, invite signup and the first OAuth sign-in. Since 2026-11 the row also carries `organization_id`: the Personal workspace created with the account (null when it is not known, and on older rows) |
| `alpha.email_verified` | `server` | Go API — the email-verification consume |
| `alpha.workspace_created` | `server` | Go API — Personal-workspace provisioning and `POST /api/v1/orgs` |
| `alpha.invite_accepted` | `server` | Go API — both accept routes |
| `alpha.first_payment` | `server` | Inference API — `BillingEventProcessor`, when a paid transaction is committed |
| `site.visit` | `client` | Marketing site beacon → `POST /api/v1/telemetry/visit`, once per visitor per tab session. Visitor-only row; `detail` = source label |
| `site.page_view` | `client` | Marketing site beacon → the same endpoint, once per page key per page life. Visitor-only row; `detail` = page key (`home`, `features`, `pricing`, `about`, `contact`, `recipes`, `runner`, `for-<slug>`, `blog`, `blog:<slug>`) |
| `site.cta_click` | `client` | Marketing site beacon → the same endpoint. Visitor-only row; `detail` = CTA location (`hero`, `header`, `pricing-creator`, ...) |
| `signup.started` | `client` | Web signup page beacon → the same endpoint. Visitor-only row; `detail` = source label |
| `signup.submitted` | `client` | Web signup page beacon → the same endpoint, when the person asks for an account: the form is sent, or a Google or GitHub button on the signup page is pressed. Visitor-only row; no `detail`. It says the person asked, not that an account was created. The sign-in page sends neither signup event |
| `activation.project_created` | `server` | Inference API — a user's first project, once per user |
| `activation.file_uploaded` | `server` | Inference API — a user's first file added by their own upload (form upload, direct upload completion, import from a link, the assistant's upload and import tools), once per user |
| `activation.source_connected` | `server` | Inference API — a user's first connected source: a GitHub repository, a cloud drive folder or a computer folder linked to a project by the person's own action, once per user. Written when the link is created, never by a sync pass, a re-sync, a change of the selection or an unlink |
| `activation.output_downloaded` | `server` | Inference API — a user's first output download, once per user |
| `activation.output_shared` | `server` | Inference API — a user's first output share link, once per user |
| `billing.checkout_started` | `server` | Inference API — a user's first checkout, once per user |
| `billing.credits_exhausted` | `server` | Inference API — a user's first HTTP 402 `INSUFFICIENT_CREDITS`, once per user |
| `billing.plan_gate_hit` | `server` | Inference API — the shared MVC result filter, on every response carrying `code: PLAN_NOT_ENTITLED`; plain append; `detail` = the feature token (`resolution`, `audio_generation`, ...), `other` if not a plain token |
| `activation.run_failed` | `server` | Inference API — `RunFailedProductEventConsumer` on `WorkflowExecutionFailed`; once per execution; `detail` = the failing step's `FailureCode`, `other` if absent or not a plain token |
| `auth.login` | `server` | Go API — a successful login (initiative first-film-funnel-fixes T-002); `detail` = `password`, `google` or `github` |
| `billing.upgrade_prompt_shown` | `client` | `web` — an upgrade prompt rendered; `detail` = surface token |
| `billing.upgrade_clicked` | `client` | `web` — an upgrade prompt clicked; `detail` = surface token |
| `product.template_chosen` | `server` | Inference API — a recipe chosen: the template apply route, the public API's apply route, and a project created with a `starterTemplateKey`; plain append; `detail` = template key |
| `product.marketplace_copied` | `server` | Inference API — a community recipe copied into a project; plain append; `detail` = the copy's template key (`market:<recipe id>`) |
| `product.assistant_used` | `server` | Inference API — a user's first assistant chat turn, once per user |
| `product.mcp_connected` | `server` | Inference API — a user's first MCP tool call, once per user |
| `product.change_requested` | `server` | Inference API — a change requested on a composition; plain append |
| `onboarding.tour_started` | `server` | Inference API — `POST /api/v1/onboarding/events`; once per user |
| `onboarding.tour_completed` | `server` | Inference API — same endpoint; once per user |
| `onboarding.tour_skipped` | `server` | Inference API — same endpoint; once per user |
| `onboarding.checklist_dismissed` | `server` | Inference API — same endpoint; once per user |
| `onboarding.checklist_item_done` | `server` | Inference API — same endpoint; once per user per item; `detail` = the closed item key (`create_project`, `upload_file`, `run_video`, `open_result`, `try_assistant`, `open_editor`) |
| `onboarding.profile_saved` | `server` | Inference API — `PUT /api/v1/onboarding/profile` with the two welcome answers; once per user; `detail` = the persona of the first answer |
| `onboarding.profile_skipped` | `server` | Inference API — the same route with `{"skipped": true}`; once per user |
| `alpha.first_project` | `client` | `web` |
| `alpha.first_run` | `client` | `web` |
| `alpha.first_export_initiated` | `client` | `web` — the Download control on a render. Three places fire it, once per user between them: the output player, the result on an app's run page, and the "your first video" moment |
| `alpha.run_page_opened` | `client` | `web` — the run page was opened; no `detail` |
| `alpha.run_clicked` | `client` | `web` — the Run control was clicked; no `detail` |
| `alpha.run_blocked` | `client` | `web` — a run was blocked; `detail` (required) = one of `readiness`, `notes_required`, `plan`, `credits`, `estimate_gate`, `rate_limited`, `source`, `other`; once per user per detail |
| `telemetry.consent.granted` | `consent` | `web` consent surface → ingest |
| `telemetry.consent.declined` | `consent` | `web` consent surface → ingest |

**Two kinds of `onboarding.*` row.** They share a prefix and a `source`, and they are not equally
complete:

- `onboarding.tour_*` and `onboarding.checklist_*` are **browser reports**. The web app posts them to
  `POST /api/v1/onboarding/events` only after the person granted telemetry consent, and the route answers
  404 unless `Telemetry:ClientIngestEnabled` is on. The row says `server` because the API validated and
  wrote it, but it exists only for consenting people: read these rows with the `measurability` column of
  the funnel query, like a `client` step. The endpoint validates the whole body against a closed allowlist
  first (unknown event or item -> 400, nothing written); the item key is the only `detail` value.
- `onboarding.profile_saved` and `onboarding.profile_skipped` are **true server rows**. Nothing in the
  browser reports them: the API writes the row itself when `PUT /api/v1/onboarding/profile` saves the
  answer (or the skip) on the account. No consent and no telemetry switch is involved, so the row exists
  for every person the transition happened to. The answers themselves are account data in
  `user_onboarding_profiles` (persona, material, the landing page's suggested persona, `answered_at`,
  `skipped_at`); the event is the funnel's record that it happened.

Both kinds are claimed per user (and per checklist item) with
`IProductEventLog.ClaimAndAppendWithDetailAsync`: the claim in `alpha_funnel_claims` and the row are one
statement, so a repeat writes nothing. A person who answers the welcome questions again changes their
profile row; the event keeps the persona of the first answer.

### The first-film funnel events

Five names were added to read the first-film drop-off. Payload rules: the `detail` is a closed token of at
most 64 characters, never free text, never an id, never a message; rows are append-only and carry no
personal data (user id and organization id only, like every other row).

- `billing.plan_gate_hit`: written for every plan refusal rendered as an MVC result (run start, composition
  revisions, step change requests, edit timelines, the workflow assistant route, and so on). A refusal the
  assistant or an MCP tool returns as tool output, not as an HTTP result, is not recorded.
- `activation.run_failed`: written when the engine publishes `WorkflowExecutionFailed`; the claim key
  carries the execution id, so a redelivery writes nothing.
- `auth.login`: written by the Go API (not by this service). It is a server row.
- `billing.upgrade_prompt_shown` / `billing.upgrade_clicked`: sent by the browser through
  `POST /api/v1/telemetry/events` with a `detail` that must be one of `insufficient_credits_alert`, `insufficient_credits_toast`,
  `plan_gate` or `pricing`. Any other surface, a missing surface, or a `detail` on any other event refuses
  the whole batch with 400 `invalid_detail`. One row per user per surface per event (the claim key carries
  the surface).

**Consent behaviour.** Server rows (`billing.plan_gate_hit`, `activation.run_failed`, `auth.login`) are
written for the signed-in user's own action and are **not** consent-gated, like every `server` row. Client
rows (`billing.upgrade_*`) are sent only when the person has granted telemetry consent and
`Telemetry:ClientIngestEnabled` is on, so a missing client row follows the reader outcomes above.

The three `alpha.run_*` events extend the client allowlist: `detail` is accepted only on `alpha.run_blocked`
(required there, from the closed set above); any other combination -> 400 `invalid_detail`, nothing
written. Each is claimed once per user per (event, detail), keyed `event:detail` in `alpha_funnel_claims`
(no migration).

The names live in one class, `ProductEventNames`
(`inference/src/ReelBolt.Inference.Api/Services/Telemetry/ProductEventNames.cs`), except
`product.marketplace_copied`, which is a constant of `MarketplaceCopyService`. "Once per user" rows are
claimed with `IProductEventLog.ClaimAndAppendAsync`; the "plain append" names can repeat.

**Not emitted, by design.** There are no rows for "first run started", "first run passed", "first
finished video" or "second run within seven days". Runs always reach `workflow_executions` and
`workflow_step_results`, so the SQL files under an operator file derive those steps
from them.

**Why the first upload is an event and not derived.** `project_files` cannot answer "when did this person
first upload": a row names no uploader (only its project), a move rewrites its `uploaded_at`, and deleting
the file deletes the row. Granting the table would also put file names and storage keys one column list
away from BI. So the upload routes write `activation.file_uploaded` once per user, after the file is
usable (a quarantined file writes nothing). A background sync (GitHub, a cloud drive, a computer folder),
an unpacked archive entry, a restored file and an agent's own file are not a person's upload and write
nothing. The public API's upload route does not write it either: its callers are API-key service
accounts, which no funnel counts.

**A connected source is the other way to add material.** The welcome flow sends a person who starts from
code to the repository tab, not to the drop zone, so an upload alone would read as "added nothing" for
them. `activation.source_connected` is written once per user when the person links a GitHub repository, a
cloud drive folder or a computer folder to a project. The acquisition SQL reads the EARLIER of the two
events as the "added material" step (`first_upload_at`, `first_upload`).

**Known gaps.**

- `activation.file_uploaded` and `activation.source_connected` exist since 2026-11. An account that added
  material before that has no row, so `first_upload_at` is empty for it until it uploads or connects
  again, and is then the time of that later action. Read the step for accounts created since the release.
- `product.template_chosen` is not written when the assistant creates a workflow from a recipe
  (`ApplyCreateFromTemplate`), and the automatic starter of a project created without a
  `starterTemplateKey` is not a choice and writes nothing.
- Output sharing through the REST API is not wired to `activation.output_shared` (the only share path is the
  assistant/MCP `GetOutput` tool).
- `billing.credits_exhausted` is written for every HTTP 402 `INSUFFICIENT_CREDITS` and for the in-chat
  `ExecuteWorkflow` refusal; an MCP tool call refused for credits answers 200 with an error result and a chat
  turn that ends out of credits are not recorded.
- "First run" read from `workflow_executions` runs slightly high: hidden editor and graphics workflows also
  leave run rows, and `workflow_definitions` is not granted to the BI role, so they cannot be filtered out.
- The server emitters do not skip API-key service accounts (the Go API's do) and do not skip platform
  admins. The acquisition SQL files and the admin funnel leave both out; a query written directly on
  `product_events` should join `application_users` and filter `is_service_account` and `is_admin`.
- `alpha.signup` carries `organization_id` only since 2026-11. Older rows have none. No shipped query
  depends on it being null or set.
- Every visitor row is a floor: it needs the cookie banner's Accept and
  `TELEMETRY_VISIT_INGEST_ENABLED=true`. Link clicks (`marketing_link_daily_stats`) need neither.
- An account carries a link code only when the first-touch cookie was consented, or when URL capture
  (`ATTRIBUTION_URL_CAPTURE_ENABLED`, default off) was on at signup. Until it is on, per-link account counts
  are a floor and the rest of the accounts read `capture = 'none'`.
- The visit beacon's 30-minute de-duplication is one statement but not a lock: two identical beacons that
  arrive at the same instant can both be recorded. Count distinct visitors, not rows.
- Event rows are deleted after 12 months (13 for visitor rows), see [Retention](#retention). A step
  timestamp read from an event is gone after that; the steps read from `application_users`, `projects`,
  `user_onboarding_profiles` and the run tables are not.

`product.change_requested` is written by both the REST revision route and the assistant revision tool.

The browser emits the three `client` steps. The ingest accepts only those three names as `client`
rows, plus the two consent names, and validates the whole batch first — a batch carrying an unknown
name writes nothing at all, so one bad entry cannot half-write a batch.

### `alpha.first_payment` means activation

`alpha.first_payment` means the **organization activated**. It is exactly one row per organization,
not one per transaction: a partial unique index on `(organization_id, event) WHERE event =
'alpha.first_payment'` makes the write at most once, so a second paid transaction — or a concurrent
writer — appends nothing. The person on the row is the organization's Owner (the membership with
the lowest `created_at`).

**Revenue per payment is not here.** `billing_payments` holds every payment and stays authoritative
for money; `alpha.first_payment` is the derived activation marker. Read the funnel from
`product_events` and the money from [`billing.md`](billing.md).

## Visits, signup started and acquisition source

Five events exist before an account does, so they carry a `visitor_id` and no user or organization
(design and the closed sets: [`design/new-user-funnel.md`](design/new-user-funnel.md) section 4).

| Event | Sent by | When | `detail` |
|---|---|---|---|
| `site.visit` | marketing site | once per visitor per tab session | the source label (computed by the API) |
| `site.page_view` | marketing site | once per page key per page life; the page the document opened on gets one too, even when Accept came pages later | the page key |
| `site.cta_click` | marketing site | a click on a call to action | the CTA location |
| `signup.started` | web signup page | the signup page opens | the source label (computed by the API) |
| `signup.submitted` | web signup page | the signup form is sent, or a Google or GitHub button on the signup page is pressed | none |

All five need the cookie banner's Accept (`rf-cookie-consent` = `granted`): nothing is read, written or
sent before it. They go to the anonymous endpoint `POST /api/v1/telemetry/visit`: body at most 2 KB,
answer `204`. The row is `source = 'client'`, `visitor_id` set, and no IP, user agent, user id or
organization id. A visit with no consent writes no row, so visitor rows are a floor, not a total. The
endpoint answers `404` and records nothing unless `TELEMETRY_VISIT_INGEST_ENABLED` is `true` (config key
`Telemetry:VisitIngestEnabled`, default `false`).

**The body.** `event` and `visitorId` (a version-4 UUID) are validated: an unknown event is
`400 unknown_event`, a bad id `400 invalid_visitor_id`. The attribution fields `source`, `medium`,
`campaign`, `content`, `term`, `landingPath` and `referrerHost` are each lowercased and trimmed, then
checked against a strict pattern; an invalid field is dropped, never rejected, so a bad `utm_*` does not
lose the event. `detail` is different. On `site.page_view` and `site.cta_click` it IS the row's meaning,
so it must be one closed token (`^[a-z0-9][a-z0-9:._-]{0,63}$`, compared as sent): missing or malformed
is `400 invalid_detail` and nothing is written. On the other three events a `detail` in the body is
ignored: the API decides it.

**Source label.** `utm_source` if valid, else the referrer host, else `direct`. A source or a referrer
that is one of the site's own hosts (`Telemetry:OwnHosts`, plus the host the request arrived on) is
dropped: a self-referral is not an acquisition source.

**De-duplication.** A repeat of the same (visitor, event, detail) within 30 minutes is answered `204`
and writes no event row. Two different pages, or the same page for two visitors, are two rows.

### Which visitor id a browser sends

One rule, the same in `site/lib/attribution-capture.ts` (`planIdentity`) and
`web/lib/attribution/visitor-events.ts` (`planSignupIdentity`): the first-touch cookie `rb_ft` and the
visitor id are one fact.

- **A readable `rb_ft` names the visitor.** "Readable" means it decodes and holds a visitor id (the
  signup page and the Go API require a version-4 id; the site's check accepts any UUID shape and only
  ever mints version 4). Its first touch is never rewritten, and the id in
  `localStorage['rf-visitor-id']` follows the cookie, never the other way round.
- **A fresh `rb_ft` is a fresh visitor id.** With no readable cookie (never set, ended after its 30
  days, cleared by the API at signup, unreadable) a new first touch is written together with a new id.
  The id an earlier visit left in `localStorage` is not continued under another first touch. So a
  person who returns after the cookie ended, or after they signed up, is a new `visitor_attribution`
  row and a new consented visitor. When the browser still held the earlier id, the new visitor's
  `site.visit` is sent even inside a tab session that already sent one.
- **No cookie could be written** (cookies blocked, value over the size limit): the stored id is kept;
  a new one is minted only when nothing is stored.
- **The site** starts a first touch on any landing, a direct one included. **The signup page** starts
  one only when its own address carries a campaign parameter (a session cookie, written when the page
  opens, never on submit). Opened with no `rb_ft` and no campaign parameter, it writes nothing and
  keeps the stored id, so that earlier visitor row gets `signup.started` while the account has no
  cookie to be attributed by.
- Reject or withdraw removes `rb_ft`, `rf-visitor-id` and `rf-visit-sent`.

Why one rule: the server keeps one first touch per visitor id, first write wins, and an account's
attribution is whatever its `rb_ft` says. A cookie rewritten under an old id would file the same person
under one link as a visitor and under another as an account.

### The visitor's first touch: `visitor_attribution`

The first event the API sees for a visitor id also stores that visitor's first touch, once. It is the
same statement that writes the event row (`ProductEventLog.RecordVisitSql`): `INSERT ... ON CONFLICT
(visitor_id) DO NOTHING`, so first touch wins and a later visit through another campaign never rewrites
it. Every beacon carries the visitor's stored first touch (the readable `rb_ft`) when there is one, and the
landing the browser holds in memory only when no first touch is stored (`beaconTouch` on the site,
`beaconFirstTouch` in the app), so whichever of the five events arrives first records the same thing.

| Column | Type | Nullable |
|---|---|---|
| `visitor_id` | `uuid`, primary key | no |
| `source_label` | `varchar(64)` | no |
| `utm_source`, `utm_medium`, `utm_campaign`, `utm_content`, `utm_term` | `varchar(64)` | yes |
| `referrer_host` | `varchar(100)` | yes |
| `landing_path` | `varchar(120)` | yes |
| `first_touch_at` | `timestamptz` | no |
| `created_at` | `timestamptz` | no |

The fields are sanitized exactly like `user_attribution`'s (the same patterns, the own-host rule on the
source and the referrer; a landing path loses its query and fragment). On a tracked link `utm_content`
is the link code, which is how a visitor joins `marketing_links`. The table holds no account: a visitor
becomes joinable to a person only through `user_attribution.visitor_id`, when the consented cookie
carried the visitor id to the signup. `powerbi_ro` has table-wide `SELECT`.

### First-touch attribution: `user_attribution`

The first-touch cookie `rb_ft` carries the landing attribution and the visitor id from the browser
to the Go API. The site sets it (30 days) after consent; the web signup page sets a session cookie
when the signup URL itself carries UTM parameters and no readable `rb_ft` exists, **only if consent is
granted** (nothing is read, written or sent otherwise). The Go API reads it
at password signup, invite signup and the first OAuth sign-in, writes one `user_attribution` row
(first touch wins, `ON CONFLICT DO NOTHING`) and clears the cookie.

A second way in needs no cookie. The signup request (and the OAuth `state`) can carry the campaign
parameters of the signup URL itself and the landing path (`lp` on the link, `attribution.landingPath`
in the signup body); when no consented cookie gave a row and
`ATTRIBUTION_URL_CAPTURE_ENABLED=true` (default `false`), the Go API writes the row from those. The
`capture` column says which of the two happened. While the switch is off the campaign parameters and
the landing path are not put into the OAuth `state` at all; the persona always is.

| Column | Type | Nullable |
|---|---|---|
| `user_id` | `uuid`, primary key, FK `application_users` `ON DELETE CASCADE` | no |
| `source_label` | `varchar(64)` | no |
| `utm_source`, `utm_medium`, `utm_campaign`, `utm_content`, `utm_term` | `varchar(64)` | yes |
| `referrer_host` | `varchar(100)` | yes |
| `landing_path` | `varchar(120)` | yes |
| `visitor_id` | `uuid` | yes |
| `first_touch_at` | `timestamptz` | yes |
| `created_at` | `timestamptz` | no |
| `capture` | `varchar(16)` | yes |

**`capture`** is how the row was learned: `cookie` (the consented first-touch cookie) or `url` (campaign
parameters carried in the signup request, nothing read from or stored on the device). A `CHECK` holds it
to those two. It is null on rows written before the column existed (2026-11); the cookie was the only
writer then, so the acquisition SQL reads such a row as `cookie`. A `url` row has no `visitor_id` and no
referrer: there was no visitor. Its `landing_path` is the `lp` the signup link carried (the site's
signup, sign-in and language links add it next to a campaign parameter, and it is kept through a Google
or GitHub sign-up); or, when a campaign link opened the signup page itself, that page's path. Its
`first_touch_at` is the time of the request that created the account.

The Inference API owns the table; the Go API maps it and never migrates it. `powerbi_ro` has
table-wide `SELECT` (the operator runbook). A user who
neither consented nor signed up while URL capture was on has no row. Join it to the funnel on `user_id`,
to visitor rows on `visitor_id`, and to `marketing_links` on `utm_content` = `code`.

### Retention

`ProductEventsRetentionService` runs daily (first sweep two minutes after start) and deletes
**visitor-only** rows (no user, no organization) older than `Telemetry:VisitRetentionMonths`
(default 13), and with them, in the same transaction and at the same cutoff, the `visitor_attribution`
rows whose `first_touch_at` is older. A first touch is judged by its own age, not by whether the visitor
still has recent events. One browser can read as several first touches well before that: the `rb_ft`
cookie lasts 30 days and is cleared at signup, and a fresh cookie is a fresh visitor id
([Which visitor id a browser sends](#which-visitor-id-a-browser-sends)). The same service also runs a second daily sweep (`SweepAccountDataAsync`) that deletes
**account-linked** rows (user or organization set) older than `Telemetry:RetentionMonths` (default 12,
minimum 1) and `feedback_reports` rows older than the same cutoff. This backs the Privacy Policy's
12-month promise. The same sweep deletes `alpha_funnel_claims` rows older than the cutoff. Consequences:
a first-time event (a `(user_id, event)` claim) can be recorded again after 12 months, and the
per-organization `alpha.first_payment` unique index reopens once that row is deleted. Both deletes run in one
transaction with the same cutoff, and a claim and its event row share one timestamp, so within the window
neither can be missing and first-payment counting is unaffected. Rows with `source = 'consent'` are **kept
while the person's account exists**: they are the evidence of the person's telemetry consent choice, so we
can show it. Once the user no longer exists in `application_users` (or the row names no user) they are
deleted after the cutoff like the rest. Each of the two daily sweeps has its own try/catch (logged at Error,
retried tomorrow), so one failing cannot skip the other. `user_attribution` and `user_onboarding_profiles`
rows live as long as the user. `marketing_links` and `marketing_link_daily_stats` hold no person and are
kept. Health
signals are OTLP spans exported to Grafana Cloud, not tables of ours, so no sweep applies.

### Why production had about six rows

Three reasons stacked. `TELEMETRY_CLIENT_INGEST_ENABLED` and `NEXT_PUBLIC_TELEMETRY_ENABLED` default
to `false`, so no `client` row existed. Signup was closed, so the `server` signup steps had almost
no subjects. And visits had no event at all. Turn the visit path on with
`TELEMETRY_VISIT_INGEST_ENABLED=true` in the same `.env` as the two flags below, then recreate
`inference`. Server-side events need no flag. The full ordered list of switches for the new-user funnel is
in the operator runbook.

The launch funnel, per signup cohort week and source label, is in
an operator file.

## Reading the acquisition funnel: three files

The new-user funnel is read at three grains. Each file under an operator file is ONE
statement that `powerbi_ro` can run on the tables and columns it is granted, written so the BI extractor
can run it as a single `psql \copy` line (full-line comments only; no double quote, dollar sign, backtick
or backslash). The files are owned and tested here and copied verbatim into the BI repository;
`AcquisitionQueryFilesTests` reads the files, flattens them the way the extractor does, runs them under
`SET ROLE powerbi_ro` and checks every number against seeded people, visitors and links.

| File | One row per | What it carries |
|---|---|---|
| an operator file | account (API-key service accounts and platform admins left out) | `user_id`, `signup_at`, `auth_provider`; first-touch attribution (`source_label`, the five UTM fields with `utm_content` named `link_code`, `referrer_host`, `landing_path`, `capture`); `persona`, `suggested_persona`, `material`; one nullable timestamp per step, plus `welcome_skipped_at` and `first_run_outcome`; `measurability`. No email, no name |
| an operator file | consented visitor (`visitor_attribution`) | the first touch; `pages_viewed`, `viewed_second_page`, `clicked_cta`, `first_cta_location`, `reached_signup`, `submitted_signup`, `became_account`, `user_id`, `furthest_step` |
| an operator file | tracked link (`marketing_links`) | clicks and bot hits; consented visitors and how far they got; accounts and how many reached each step, all time |

**The steps of `acquisition-person.sql`.** Each is the first time the person reached it; they are
independent facts, not a strict sequence.

| Column | Read from |
|---|---|
| `signup_at` | `application_users.created_at` |
| `verified_at` | `application_users.email_verified_at` |
| `onboarded_at` | `user_onboarding_profiles.answered_at` (a skip is not an answer) |
| `first_project_at` | the earliest `projects` row the person owns |
| `welcome_skipped_at` | `user_onboarding_profiles.skipped_at`: the person pressed Skip. Empty together with `onboarded_at` means the welcome questions were never answered either way |
| `first_upload_at` | the earlier of `activation.file_uploaded` and `activation.source_connected` (the person added material) |
| `first_run_at` | the earliest `workflow_executions` row the person initiated (`queued_at`, else `started_at`) |
| `first_run_outcome` | how that first run stands now: `Queued`, `Running`, `Passed`, `Failed` or `Cancelled` (`workflow_executions.status`). It tells a first run that failed from one that was never started |
| `first_video_at` | the earliest `Completed` `workflow_step_results` row of such a run whose output is a video file under the project's `outputFiles/` |
| `first_download_or_share_at` | the earlier of `activation.output_downloaded` and `activation.output_shared` |
| `checkout_started_at` | `billing.checkout_started` |
| `first_payment_at` | `alpha.first_payment` (once per organization, on its earliest Owner) |

`capture` is `cookie`, `url` or `none` (no `user_attribution` row; `source_label` is then
`unattributed`). `landing_path` is filled from the consented cookie, or on a `url` row from the landing
path the signup link carried; it is empty otherwise.

**The visitor steps of `acquisition-visitor.sql`.** From the signup form on, a step also counts every
visitor known to have gone further, because the two signup events come from the browser and can be
missing (a provider account created from the sign-in page sends neither; an event sent while the page
is being left can be lost):

| Column | True when |
|---|---|
| `viewed_second_page` | two or more distinct `site.page_view` page keys. A fact on its own |
| `clicked_cta` | at least one `site.cta_click`. A fact on its own |
| `reached_signup` | a `signup.started` event, **or** `submitted_signup` |
| `submitted_signup` | a `signup.submitted` event, **or** `became_account` |
| `became_account` | an account's `user_attribution` row carries this visitor id (an account attributed by URL capture has none and is not matched) |

So `became_account` implies `submitted_signup`, which implies `reached_signup`, and those three counts
never grow from one step to the next. The first two are not filled in: a visitor can open the signup
form from a direct link, so either can be lower than `reached_signup`. The links file counts
`visitors_signup_started` and `visitors_signup_submitted` by the same rule; its `signups` can still be
higher than `visitors_signup_submitted`, because accounts attributed by URL capture have no visitor id. `measurability` is computed exactly as [the funnel query](#the-funnel-query) computes it,
from the person's latest consent row. It qualifies browser-reported data only: every column of the three
files is a server fact, or a visitor row that already required consent.

**How complete each count is.**

| Count | Completeness |
|---|---|
| Clicks and bot hits per link | A total. A counter per link per day; no consent, nothing about the person |
| Visitor rows and visitor steps | A floor: browsers that accepted the cookie banner, while visit ingest is on |
| Account steps | Complete: server facts for every account the files count (API-key service accounts and platform admins are in none of them) |
| Which link an account belongs to | Complete only while URL capture is on; otherwise the consenting accounts, and the rest read `capture = 'none'` |

`acquisition-links.sql` uses the definitions of the admin funnel endpoint
(`GET /api/v1/marketing/admin/links/funnel`, `TrackedLinkFunnelReader.FunnelSql`) with no time window; a
test runs both over the same rows and compares them. The file adds columns the endpoint does not have:
the four visitor steps, `first_upload`, `first_download_or_share` and `checkout_started`. Accounts with no
link code, or with a `utm_content` that is no link's code, are not in it: read them from
`acquisition-person.sql` (`link_code` is empty, or matches no code). The admin endpoint counts both kinds
in its one `unattributed` row.

Every visitor column of the links file is 0 while `TELEMETRY_VISIT_INGEST_ENABLED` is `false` (the
shipped default). That zero says the switch is off, not that nobody accepted cookies. The admin endpoint
reports the switch as `visitIngestEnabled`; the SQL file cannot see it.

## What is authoritative

**`product_events` is authoritative for the funnel.** Build the signup → activation → first-value
model on it.

`alpha_funnel_claims` keeps its original job: the durable at-most-once claim behind the
`reelbolt.alpha.funnel` OTLP counter (see
[`observability.md`](observability.md#the-alpha-funnel)). A client funnel event claims and appends
in one statement, so the claim and its `product_events` row can never diverge, and the counter moves
only when the claim is new. `alpha_funnel_claims` is **not** granted to `powerbi_ro`: it cannot
express the consent honesty above, so granting it would hand the BI team a second, worse funnel. The
grant regime is in the operator runbook, where
`product_events` is granted table-wide `SELECT`.

## The funnel query

This is the funnel query: one statement, runnable by `powerbi_ro` (it reads `product_events` and
nothing else). It returns one row per person with the furthest `server` step reached, the furthest
`client` step reached, and a `measurability` column computed from the latest `consent` row.

```sql
WITH step_rank (event, source, step) AS (
    VALUES
        ('alpha.signup',                 'server', 1),
        ('alpha.email_verified',         'server', 2),
        ('alpha.workspace_created',      'server', 3),
        ('alpha.invite_accepted',        'server', 4),
        ('alpha.first_payment',          'server', 5),
        ('alpha.first_project',          'client', 1),
        ('alpha.first_run',              'client', 2),
        ('alpha.first_export_initiated', 'client', 3)
),
person AS (
    SELECT DISTINCT user_id
    FROM product_events
    WHERE user_id IS NOT NULL
),
latest_consent AS (
    SELECT DISTINCT ON (user_id) user_id, event
    FROM product_events
    WHERE source = 'consent'
    ORDER BY user_id, occurred_at DESC, id DESC
),
reached AS (
    SELECT e.user_id, r.source, max(r.step) AS step
    FROM product_events e
    JOIN step_rank r ON r.event = e.event AND r.source = e.source
    WHERE e.user_id IS NOT NULL
    GROUP BY e.user_id, r.source
)
SELECT
    p.user_id,
    coalesce(max(CASE r.source WHEN 'server' THEN r.step END), 0) AS furthest_server_step,
    coalesce(max(CASE r.source WHEN 'client' THEN r.step END), 0) AS furthest_client_step,
    CASE
        WHEN lc.event = 'telemetry.consent.declined' THEN 'deliberate_absence'
        WHEN lc.event = 'telemetry.consent.granted'  THEN 'measurable'
        ELSE 'not_measurable'
    END AS measurability
FROM person p
LEFT JOIN reached r ON r.user_id = p.user_id
LEFT JOIN latest_consent lc ON lc.user_id = p.user_id
GROUP BY p.user_id, lc.event
ORDER BY p.user_id;
```

This is the exact statement `ProductEventsFunnelQueryTests` runs: the doc quotes it verbatim from
that test's `FunnelQuery` constant, and the test reads the three outcomes off the seeded data, so the
query and the table cannot drift.

## A limitation to know: a payment with no resolvable person

`alpha.first_payment` carries `organization_id`, and the organization's Owner as `user_id` when one
resolves. A payment whose organization resolves but whose Owner does not is recorded with a null
`user_id`: it is visible in the table, keyed by organization, but it does not appear in a
user-keyed funnel. Do not read its absence from a user-keyed step as a drop-off; read the
organization-keyed step for that organization instead.

## Turning telemetry on (operator step)

Telemetry is off by default and stays off until an operator turns it on. Two switches are needed:
the Inference API's ingest, and the browser's build flag. The anonymous visit path has its own third flag, described under
[Visits](#visits-signup-started-and-acquisition-source).

These two switches feed the three `client` steps, the tour and checklist rows and the `measurability`
column. The acquisition funnel does not need them: its account steps are server facts and its visitor
steps use the visit flag. The runbook for the whole funnel is
the operator runbook.

1. Set both variables in `/opt/reelbolt/.env`:

   ```
   TELEMETRY_CLIENT_INGEST_ENABLED=true
   NEXT_PUBLIC_TELEMETRY_ENABLED=true
   ```

2. Rebuild `inference` and `web`:

   ```bash
   cd /opt/reelbolt
   docker compose -f docker-compose.yml -f docker-compose.control.yml up -d --build inference web
   ```

   Rebuild, not restart: `NEXT_PUBLIC_*` is inlined into the browser bundle at build time, so a
   restart leaves the browser flag as it was compiled.

   **Cloud deploys pull the image CI built, so the host `.env` cannot turn the browser flag on there.** Set
   the repository variable `NEXT_PUBLIC_TELEMETRY_ENABLED=true` (the CI `web` build passes it as a build
   argument; an operator file pins that) and ship a new `web` image. Until
   then the shipped `web` image has the app's funnel events and consent popup compiled out. The ingest flags
   stay in the host `.env`. The soft-open read of the funnel is
   an operator document.

With `TELEMETRY_CLIENT_INGEST_ENABLED` false the ingest answers `404` and records nothing; with
`NEXT_PUBLIC_TELEMETRY_ENABLED` false the browser sends nothing and shows no consent surface. Both
must be true for the funnel to be measurable.

### What it means for a user who has not consented

The consent bar appears in `/app`. Nothing leaves the browser until the person chooses. Declining
changes nothing about what they can do — the bar gates no product action. The only thing recorded
about a decline is the fact of the decline: one `consent` row that says this person said no, so the
funnel can tell their silence apart from a drop-off. That row carries no organization.
