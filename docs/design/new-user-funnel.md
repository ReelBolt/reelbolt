# New-user funnel: design and cross-service contract

Status: design of record for the funnel re-architecture (2026-11-11). Internal background, not
published. The operator and BI reference is the operator runbook;
this page says what was decided and why, and fixes the names every service shares.

## 1. What was wrong

Measured from the code and the first soft-open users (three of three stalled before a first video):

| Goal | State before |
|---|---|
| Personalise on the user | Nothing asked, nothing stored. Every project got "Promo video from your code". |
| Social proof | None in the app; one developer-story film on the site. No real proof asset registry. |
| Fast first win | 8 surfaces, 3 overlays, 12 to 14 clicks, an 8-step tour and a 30-recipe gallery before a first run. |
| Emotional triggers | Procedural copy ("Start with a free workspace"). |
| Before/after | Absent on the site and in the app. |
| Friction | Confirm-password field, lost `?plan=`/`?next=`, dead "not in your plan" text, provider wording shown to trial users. |
| Per-link tracking | No link identity. Visit rows kept only the source label. Everything sat behind the cookie banner. The first-touch cookie was wiped on every full page load. `signup.started` was almost always `direct`. BI extracted none of it. |

The message was also too narrow: every audience named on the site was a software company.

## 2. Positioning

ReelBolt is for anyone who has a story to tell and no time to tell it or design it. Software teams
stay one audience among six. Personas are a closed set shared by the site, the app, the API and BI:

| Persona token | Landing page | Who |
|---|---|---|
| `creator` | `/for/creators` | People who film and post: vloggers, streamers, podcasters with video |
| `business` | `/for/small-business` | Shops, studios, restaurants, local services, solo marketers |
| `educator` | `/for/educators` | Coaches, teachers, course makers |
| `nonprofit` | `/for/nonprofits` | Charities, community groups, campaigns |
| `software` | `/for/software-teams` | Founders and product teams (the previous message, kept) |
| `agency` | `/for/agencies` | Studios and freelancers who edit for clients |
| `other` | none | Chosen in the app only. A signup link never carries it, and the admin form does not offer it as a link audience (the API still accepts it on a link, so older links keep it) |

Honesty rules are unchanged and binding: no invented customers, quotes, logos, counts, ratings or
time savings; no competitor prices; every number measured and labelled; the alpha label stays.
"Social proof" is therefore built as a **registry of real, signed-off assets** that renders nothing
while it is empty, plus trust facts that are true today (refund on our failure, every edited cut
scored before you see it, no card, first video on us, public alpha). The review fact is scoped to
the edited cut because that is what the recipes do: the footage starter `video-derush-edit` ends in
a review loop that scores the cut and sends a low score back for another pass.

## 3. The journey after the change

1. **Tracked link** `https://<site>/l/<code>` counts the click (no cookie, nothing stored about the
   person) and redirects to a landing page with UTM parameters; `utm_content` is the link code.
2. **Landing page** (`/` or `/for/<audience>`): outcome headline, before/after, what you can make
   today, proof slots, one CTA. The CTA carries the persona and the campaign parameters in its URL
   and, next to a campaign parameter, the path of the page the visit began on (`lp`).
3. **Signup** (`/app/signup?for=<persona>&utm_…&lp=<path>`): value panel beside the form, OAuth
   first, no confirm-password field. The persona, the campaign parameters and the landing path
   travel in the signup request (or in the OAuth `state`), so they survive the email round trip and
   need no cookie.
4. **Welcome** (`/app/welcome`): two one-click questions (who are you, what do you have). The answer
   creates the first project with the right recipe and lands on its drop zone. No tour first.
5. **First video**: a three-step rail (add clips, say what it is for, run) with persona examples.
6. **First result**: a "your first video" moment with real before/after facts of that run.

## 4. Shared vocabulary (closed sets)

- **Persona**: `creator`, `business`, `educator`, `nonprofit`, `software`, `agency`, `other`.
- **Material** (what the person starts from): `footage`, `code`, `idea`.
- **Starter recipe**: `footage` -> `edit-my-video`; `idea` -> `animation-from-idea` (an animation of text, shapes and
  motion made from words alone, no paid video generation, so every plan can run it); `code` -> `quick-win-promo`.
  `idea` also shows a persona shot list ("film these five clips on your phone") with a "Describe a video instead" link to the app.
- **Link channel**: `hackernews`, `reddit`, `linkedin`, `x`, `producthunt`, `youtube`, `tiktok`,
  `instagram`, `facebook`, `github`, `discord`, `newsletter`, `email`, `blog`, `directory`, `press`,
  `partner`, `ads`, `other`.
- **Link medium**: `social`, `community`, `video`, `email`, `referral`, `paid`, `owned`, `press`.
- **Link code**: `^[a-z0-9][a-z0-9-]{1,39}$` (always a valid UTM value).
- **UTM value** (unchanged): `^[a-z0-9][a-z0-9._~+-]{0,63}$`, lowercased and trimmed, an invalid
  value is dropped, never rejected.
- **Tracked-link convention**: `utm_source` = channel, `utm_medium` = medium, `utm_campaign` =
  campaign token, `utm_content` = link code. A hand-written URL with the same `utm_content` joins the
  same link.
- **Landing-path parameter**: `lp`, the path of the page of ours the visit began on. A path and
  nothing else (`^/[a-z0-9/_~.-]{0,119}$` after lowercasing; a query or fragment is cut off; no host).
  Our own links add it only next to a campaign parameter: on its own it would describe a touch with
  no source behind it. Same name and rule in `site/lib/attribution-signup-link.ts`,
  `web/lib/auth/signup-carry.ts` and the Go API (`oauthLandingParam`); the signup body sends the same
  value as `attribution.landingPath`.
- **Visitor events** (browser, cookie consent required, anonymous `visitor_id`):
  `site.visit`, `site.page_view`, `site.cta_click`, `signup.started`, `signup.submitted`.
  `detail` is a closed token `^[a-z0-9][a-z0-9:._-]{0,63}$`: the source label on `site.visit` and
  `signup.started`, the page key on `site.page_view` (`home`, `features`, `pricing`, `about`,
  `contact`, `recipes`, `runner`, `for-<slug>`, `blog`, `blog:<slug>`), the CTA location on
  `site.cta_click`, null on `signup.submitted`.
  `signup.started` is sent when the signup page opens. `signup.submitted` means "asked for an
  account": the signup form passed its own checks and is being sent, or a Google or GitHub button on
  the signup page was pressed (a provider sign-up has no form, so the press is the submission). It
  says nothing about the outcome. The sign-in page sends neither event.
- **Visitor identity** (one rule, the same on the site and on the signup page): the first-touch
  cookie `rb_ft` and the visitor id are one fact.
  - A readable `rb_ft` (it decodes and names a visitor id) names the visitor. It is never rewritten
    (first touch wins) and the id kept in `localStorage` follows the cookie, never the other way round.
    The signup page and the Go API accept only a version-4 id; the site's check accepts any UUID shape
    but only ever mints version 4.
  - No readable `rb_ft` (never set, ended after its 30 days, cleared by the API at signup, unreadable):
    a fresh `rb_ft` is written and with it a **new** visitor id. An id left in `localStorage` by an
    earlier visit is not continued under a different first touch. So a person who returns after the
    cookie ended, or after they signed up, counts as a new consented visitor.
  - The fresh cookie could not be written (cookies blocked, value too long): the stored id is kept.
  - The site starts a first touch on any landing, a direct one included. The signup page starts one
    only when its own address carries a campaign parameter, as a session cookie, and only when the
    page opens (never on submit). With no `rb_ft` and no campaign parameter it writes nothing and
    keeps the stored id.
  - The landing page of the document gets its `site.page_view` even when Accept comes pages later.
- **Server events** (new, each once per user): `onboarding.profile_saved` (`detail` = persona),
  `onboarding.profile_skipped`, `activation.file_uploaded` (the person's own first upload or import
  from a link) and `activation.source_connected` (the person's first connected repository, cloud
  drive folder or computer folder). "Added material" is the earlier of the last two: the welcome
  flow sends a person who starts from code to the repository, so an upload alone would read as a
  stop for them.

## 5. Schema (Inference API owns; one expand-only migration `AddAcquisitionFunnel`)

All new tables are platform-wide or keyed by user or visitor: no `Tenant` filter, registered in the
unfiltered list of `TenantBoundaryInventoryTests`. All four are granted table-wide `SELECT` to
`powerbi_ro` (migration, the role script, guard tests).

**`marketing_links`**: `id uuid pk`; `code varchar(40)` unique; `channel varchar(32)`;
`medium varchar(32)`; `campaign varchar(64)`; `placement varchar(160)` (operator's note: where the
link is posted); `destination_path varchar(200)` (own-origin path starting `/`, never `//`, no
scheme, no query); `persona varchar(32)` null; `cost_minor bigint` null; `cost_currency varchar(3)`
null; `created_at`; `created_by_user_id uuid` null; `archived_at timestamptz` null.

**`marketing_link_daily_stats`**: `link_id uuid` fk cascade; `day date`; `clicks int`; `bot_hits int`;
pk `(link_id, day)`. Upsert adds one. It holds no visitor, IP or user agent: it is a counter.

**`visitor_attribution`**: `visitor_id uuid pk`; `source_label varchar(64)`; `utm_source`,
`utm_medium`, `utm_campaign`, `utm_content`, `utm_term varchar(64)` null; `referrer_host varchar(100)`
null; `landing_path varchar(120)` null; `first_touch_at timestamptz`; `created_at`. First touch wins
(`ON CONFLICT DO NOTHING`). Deleted by the visitor retention sweep (13 months) with the visitor rows.

**`user_onboarding_profiles`**: `user_id uuid pk` fk `application_users` cascade; `persona varchar(32)`
null; `material varchar(32)` null; `suggested_persona varchar(32)` null (from the landing page);
`answered_at`, `skipped_at timestamptz` null; `created_at`; `updated_at`.

**`user_attribution`** gains `capture varchar(16)` null: `cookie` (consented first-touch cookie) or
`url` (parameters carried in the signup request, no cookie). Null on rows older than this change.

## 6. HTTP contract

### Inference API

| Method | Path | Auth | Behaviour |
|---|---|---|---|
| `GET` `HEAD` | `/api/v1/l/{code}` | anonymous | Active link: add one to today's `clicks` or `bot_hits` (see "What a click is" below), then `302` to `{destination_path}?utm_source={channel}&utm_medium={medium}&utm_campaign={campaign}&utm_content={code}` plus `&for={persona}` when set. Relative `Location`, `Cache-Control: no-store`. The code is matched without regard to letter case. Unknown, malformed or archived code: `302` to `/`, nothing counted. Never 404 (a posted link must keep working). `404` only while `Marketing:Links:Enabled` is false (default true). A failed count never touches the redirect. |
| `GET` `POST` | `/api/v1/marketing/admin/links` | platform admin | List (with total clicks) / create. Create validates every closed set and the code pattern; duplicate code `409 link_code_taken`. |
| `PATCH` | `/api/v1/marketing/admin/links/{id}` | platform admin | `placement`, `destinationPath`, `persona`, `costMinor`, `costCurrency`, `archived`. Code, channel, medium and campaign are immutable. |
| `GET` | `/api/v1/marketing/admin/links/funnel?days=N` | platform admin | The last N UTC calendar days, today included (default 30, clamped to 1..365). One row per link: clicks, bot hits, consented visitors, signups (attributed), verified, onboarded, first project, first run, first finished video, paid, plus cost; one `unattributed` object; two switches. Same definitions as an operator file. See "The funnel answer" below. |
| `POST` | `/api/v1/telemetry/visit` | anonymous | Existing endpoint. Accepts the five visitor events, adds `content`, `term`, `detail` to the body, stores first touch in `visitor_attribution`, dedupes on `(visitor, event, detail)` for 30 minutes. Still `404` unless `Telemetry:VisitIngestEnabled`. |
| `GET` | `/api/v1/onboarding` | auth | Existing. Adds `profile: {persona, material, suggestedPersona, answeredAt, skippedAt}` (all nullable). |
| `PUT` | `/api/v1/onboarding/profile` | auth | `{persona, material}` or `{skipped: true}`. Closed sets, else `400`. Writes the row and the server event. Returns the profile and `starterTemplateKey`. |
| `POST` | `/api/v1/projects` | auth | Existing. Optional `starterTemplateKey`: a listed, creatable template key. When present the project gets that app and the automatic code-promo starter is not created. Unknown key `400`. |

JSON shapes (camelCase, shared by the API, the admin page and the tests):

```jsonc
// Link (list item, create and patch response)
{ "id": "uuid", "code": "hn-launch", "channel": "hackernews", "medium": "community",
  "campaign": "launch-2026-11", "placement": "Show HN, first comment",
  "destinationPath": "/for/software-teams", "persona": "software",
  "costMinor": null, "costCurrency": null, "createdAt": "…", "archivedAt": null,
  "shortPath": "/l/hn-launch",
  "longPath": "/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software",
  "totalClicks": 0 }

// POST body: code, channel, medium, campaign, placement, destinationPath, persona?, costMinor?, costCurrency?
// PATCH body (all optional): placement, destinationPath, persona, costMinor, costCurrency, archived

// GET …/links/funnel?days=30
{ "days": 30, "generatedAt": "…", "urlCaptureEnabled": false, "visitIngestEnabled": false,
  "items": [ { "linkId": "uuid", "code": "hn-launch", "channel": "hackernews", "medium": "community",
      "campaign": "launch-2026-11", "placement": "…", "destinationPath": "…", "archived": false,
      "costMinor": null, "costCurrency": null,
      "clicks": 0, "botHits": 0, "visitors": 0, "signups": 0, "verified": 0, "onboarded": 0,
      "firstProject": 0, "firstRun": 0, "firstVideo": 0, "paid": 0 } ],
  "unattributed": { "signups": 0, "verified": 0, "onboarded": 0, "firstProject": 0,
      "firstRun": 0, "firstVideo": 0, "paid": 0 } }

// GET /api/v1/onboarding adds, and PUT /api/v1/onboarding/profile returns:
{ "profile": { "persona": "creator", "material": "footage", "suggestedPersona": "creator",
               "answeredAt": "…", "skippedAt": null },
  "starterTemplateKey": "video-derush-edit" }
```

**The funnel answer.**

- `urlCaptureEnabled` mirrors `Attribution:UrlCaptureEnabled` on the Inference API (env
  `ATTRIBUTION_URL_CAPTURE_ENABLED`, the same variable the Go API reads), so the admin page can say
  whether account counts per link are complete.
- `visitIngestEnabled` mirrors `Telemetry:VisitIngestEnabled` (env `TELEMETRY_VISIT_INGEST_ENABLED`).
  While it is false no visitor row is ever written, so every `visitors` is 0 whatever people accepted.
  The two switches exist so that a zero a switch caused is never read as "nobody came".
- **Who is counted.** The cohort is every account created in the window. Platform admins
  (`application_users.is_admin`, the seeded first admin included) and API-key service accounts are
  in no count: they were not acquired. An account an admin created for somebody else is a person and
  counts. The three SQL files of section 8 leave the same two kinds out.
- **`unattributed`** holds the accounts of the cohort that belong to no link: no `user_attribution`
  row, a null `utm_content`, or a `utm_content` that is no link's code (a hand-written URL, an ad
  platform's own value). The link rows and `unattributed` together are the whole cohort, each account
  counted once.
- Each step counts the accounts of the cohort that **ever** reached it. Steps are independent facts,
  not a strict sequence.

**What a click is** (`LinkPreviewAgents`). A hit is a click only when nothing says otherwise. It is a
bot hit when: the request is a `HEAD`; there is no user agent; the user agent names a link-preview
fetcher, a crawler, a scripted or headless browser, or an HTTP library; the browser says the load is
speculative (`Sec-Purpose`, `Purpose`, `X-Purpose`, `X-Moz`: prefetch, prerender, preview); or the
browser sends fetch metadata that is not a page being opened (`Sec-Fetch-Mode` other than `navigate`,
`Sec-Fetch-Dest` other than `document`). These headers are read once to choose the column and
dropped. Software that presents itself as an ordinary browser opening a page is a click, so `clicks`
is "opened like a person would": openings, an upper bound on people, never a count of people. An edge
in front of the API has to forward those headers for the last two rules to apply; nginx and the Pages
Function both do.

### Go API

- `POST /api/v1/auth/signup` body gains optional `persona` and optional
  `attribution: {source, medium, campaign, content, term, landingPath}`.
- `GET /api/v1/auth/oauth/{provider}/start` accepts `for`, `utm_source`, `utm_medium`,
  `utm_campaign`, `utm_content`, `utm_term` and `lp` (the landing path). Sanitized values ride the
  OAuth `state` parameter (URL round trip, nothing stored on the device) and are read back in the
  callback: `state = <random>` when there is nothing to carry, else
  `<random>.<base64url(JSON)>`. The suffix is unsigned and at most 768 characters; one that is
  missing, oversized or garbage is ignored and can never fail a sign-in. The persona always rides.
  The campaign parameters and the landing path ride only while `ATTRIBUTION_URL_CAPTURE_ENABLED` is
  on: off, the callback would not record them, so they are not sent through the provider at all.
- A sign-in that does not complete (cancelled at the provider, no account can be created) is sent
  back to `/app/login?error=…` with the same carry in the address (`for`; `utm_*` and `lp` under the
  same switch), sanitized again, so a person who then signs up another way does not lose it. The
  login and signup pages pass `for`, `utm_*` and `lp` to each other and ignore `error`.
- On account creation: `persona` -> `user_onboarding_profiles.suggested_persona`
  (`ON CONFLICT DO NOTHING`). Attribution: the consented `rb_ft` cookie wins (`capture = 'cookie'`)
  and is cleared by the request that used it; otherwise, only when
  `ATTRIBUTION_URL_CAPTURE_ENABLED=true` (default false), the URL-carried parameters are written
  with `capture = 'url'` (first touch = the time of the request; no referrer and no visitor id). An
  unknown persona or invalid value is dropped, never a refusal: signup must not fail on marketing
  data.
- `landing_path` on a `capture = 'url'` row is the `lp` the link carried (signup form and provider
  sign-up alike). When a signup link with campaign parameters carried no `lp` and the signup page
  itself was the first page opened (no referrer of ours), the signup form sends that page's path.
  Our own pages send a landing path only next to a campaign parameter. The API does not require
  that: a request that carries a landing path alone still writes a row, labelled `direct`.

### Edge

`/l/{code}` on the public host maps to `/api/v1/l/{code}`: an nginx location for self-host and
staging, a Pages Function `site/functions/l/[code].ts` (plus `_routes.json`) for Cloudflare Pages.
The access log keeps the path (a link code is not a secret).

- **nginx.** One location serves both `/l/{code}` and `/api/v1/l/{code}`, so the API's own path
  cannot be used to walk around the limit. `GET` and `HEAD` only, `Authorization` and `Cookie`
  blanked, no basic auth, the click's query string not forwarded. Exactly one of two rate-limit zones
  applies per request: `link_redirect` (120 a minute per address, burst 40), or `link_relay` (1200 a
  minute per relaying address, burst 400) for a caller that proves it is the marketing site's relay
  by sending `X-ReelBolt-Site-Token` equal to nginx's own `SITE_FORWARD_TOKEN` (fixed-time compare in
  `nginx/auth.js`; the header stops at nginx). A wrong token, no token, or the variable unset on
  nginx means `link_redirect`. It decides the zone only and grants no access.
- **A click that cannot be counted still lands on the campaign.** Over the limit (429) or when the
  API cannot answer (5xx, or no answer within the short timeouts), nginx answers
  `302 /?utm_content=<code>`: the click is not counted, but a URL that carries a link's code as
  `utm_content` joins that link, so the visit and any signup still belong to it. Anything else under
  `/l/` or `/api/v1/l/` (a code that cannot exist, a deeper path) is `302 /`.
- **Pages Function.** `followTrackedLink` (`site/lib/tracked-link.ts`) lowercases and checks the
  code, calls `{REELBOLT_API_URL}/api/v1/l/{code}` with a 3-second timeout, and relays a `302` whose
  `Location` is a path on this host. Upstream it sends the caller's `User-Agent` and the six
  request-kind headers (`Sec-Fetch-Mode`, `Sec-Fetch-Dest`, `Sec-Purpose`, `Purpose`, `X-Purpose`,
  `X-Moz`), and `SITE_FORWARD_TOKEN` as `X-ReelBolt-Site-Token` when the Pages project has it; never
  a cookie, `Authorization` or an address header. `HEAD` stays `HEAD`. A well-formed code whose click
  could not be counted (`REELBOLT_API_URL` unset, timeout, any answer that is not a `302`, a
  `Location` that is not an own-origin path) goes to `/?utm_content=<code>`, as nginx does for its own 429 and an API 5xx (unlike nginx, the Function also turns the API's 404 for switched-off links into that redirect); a
  malformed code goes to `/`. The API's own answers are relayed as they are, including its `302 /`
  for an unknown or archived code.
- **Per-visitor limit on Pages** is a Cloudflare rate-limit rule on `/l/*`, shipped as
  the operator `links` rule file (each file is a complete rule list for that
  plan and replaces the plain `rate-limit.<plan>.rules.json`). The Function keeps no limit of its
  own; behind it the origin limits the relay as a whole (`link_relay`).

## 7. Privacy position

| Data | Needs cookie consent | Why |
|---|---|---|
| Click count per link per day | No | A counter. No identifier, IP or user agent is stored; nothing is written to or read from the device. |
| `site.*` and `signup.*` visitor events, `visitor_attribution`, `rb_ft` | Yes | Uses a stored random visitor id. Unchanged rule: nothing before Accept. |
| Request-kind headers and user agent of a click | No | Read once to tell a preview, crawler or prefetch from a person opening the link; never stored with the count. |
| Persona in the signup URL and request | No | A product setting the person carries in their own link; stored on their account. |
| Campaign parameters and landing path in the signup request (`capture = 'url'`) | No device storage; off by default | Recorded on the account on a legitimate-interest basis. The landing path is a path of ours only, never a query and never the referring site. The controller turns it on (`ATTRIBUTION_URL_CAPTURE_ENABLED`) after reading the Privacy text that describes it. While it is off, neither travels through Google or GitHub. |
| Onboarding answers | n/a | Account data the person gives to shape their workspace. |

The Privacy Policy text and the `legal_documents` version move with this change (Privacy-only bump:
an acknowledgement, not a re-acceptance).

## 8. Reading the funnel

Three grains, each one statement runnable by `powerbi_ro`, owned and tested in this repo and copied
verbatim into the BI repo:

- an operator file: one row per account. Attribution (source, medium,
  campaign, link code, landing path, capture), persona, and one timestamp per step: signed up,
  email verified, onboarding answered, first project, first upload (added material), first run
  started, first finished video, first download or share, checkout started, first payment. Two more
  columns explain a stop: `welcome_skipped_at` (the person pressed Skip on the welcome questions; a
  skip is not an answer) and `first_run_outcome` (how the first run stands now: `Queued`, `Running`,
  `Passed`, `Failed` or `Cancelled`; it tells a first run that failed from one never started).
  `landing_path` is filled from the consented cookie, or with `capture = 'url'` from the `lp` the
  signup link carried.
- an operator file: one row per consented visitor. First-touch fields and
  the furthest pre-signup step (visited, viewed a second page, clicked a CTA, reached signup,
  submitted signup, became an account).
- an operator file: one row per tracked link with clicks and every count above.

**The visitor ladder from the signup form on.** The two signup events are sent by the browser and
can be missing for somebody who did sign up (a provider account created from the sign-in page sends
neither; an event sent while the page is being left can be lost). The account is a server fact, so:
`submitted_signup` = a `signup.submitted` event **or** an account carries this visitor id;
`reached_signup` = a `signup.started` event **or** `submitted_signup`. Became an account implies
submitted, and submitted implies reached, so the three counts never grow from one step to the next.
`viewed_second_page` and `clicked_cta` are not filled in this way: they are facts on their own, and a
visitor can open the signup form from a direct link without either. The links file counts
`visitors_signup_started` and `visitors_signup_submitted` by the same rule. `signups` per link can
still be higher than `visitors_signup_submitted`: accounts attributed by URL capture have no visitor
id.

**Who is in no count.** Platform admins and API-key service accounts, in all three files and in the
admin endpoint. Accounts with no link code, or with a `utm_content` that is no link's code, are not
in the links file; read them from the person file. The admin endpoint counts both kinds in
`unattributed`.

Clicks are a consent-free total of openings, not people. Visitor steps are a consented floor, and a
person who returns after the cookie ended or after signup is a new visitor. Account steps are server
facts and complete; their link attribution is complete only when URL capture is on.

**Retention and whole-life cost.** Visitor rows and visitor first touches are deleted after 13 months and account-linked event rows (first export, checkout, payment, first upload) after 12 months; clicks, links and the account tables are kept. A link's visitor figures are therefore a floor over the last 13 months, and cost per paying account or per first video compares a whole-life cost with counts that lose rows after 12 months: read them inside a link's first year.
