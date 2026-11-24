# Recipe marketplace: design and contract

Status: decided 2026-11-02. Everything ships behind
`Marketplace:Enabled` (default `false`; `Marketplace__Enabled`). This file is the contract every layer builds to; change
it in the same commit as the code that departs from it.

## Decisions in one place

| Decision | Rule |
|---|---|
| Who checks | Automatic check on every publish and every new version. A recipe is listed at once only if all checks pass AND the author has a recipe already `listed` (ever approved) or the organization is on a paid plan (`creator`, `pro`, `team`, `enterprise`). Otherwise, and for any content check in the review band, status `pending` waits in the platform-admin queue. A block verdict refuses the publish. |
| What it may contain | Built-in agents and plain step settings only. Custom agents, custom skill lists, run-bound sources, timeline renders, `Revise` and hidden kinds refuse to publish, with the reason in words. Every author-owned id is removed or becomes an open slot. |
| Where it lives | Community gallery in the app (`/app/community`) AND on the marketing site (`/recipes`, `/it/recipes`), both browsable without an account. Copy and publish need a signed-in verified account. |
| Legal | Required consent box at publish; the consent text version and time are stored on the version. No Terms re-acceptance. Author label defaults to the account display name; the author may change it per recipe in the dialog. |
| Who may publish and rate | Every verified-email account. Caps: 5 publishes per user per UTC day, 50 live recipes per organization (counted from rows). Ratings: 1-5 stars, one per account, only from an account whose organization copied the recipe and has at least one `Passed` run of that copy. Average shown only from 5 ratings. Usage = distinct organizations that copied. No written reviews. |

## Ids, tenancy and what never leaves

- Recipe ids are random Guids. No route, body, log line or response ever carries `author_user_id`, `author_org_id`, a project id,
  workflow id, provider id, project file id, voice id, step result id or storage key of the author. Public DTOs have an
  `authorLabel` string only.
- Marketplace tables are public by design: no `Tenant` query filter; they sit in the unfiltered list of
  `TenantBoundaryInventoryTests` with the reason. Every owner action compares `author_org_id` to the caller's active organization
  and answers `404` otherwise. Admin routes check `IsPlatformAdmin` first.
- Every anonymous failure is the same bare `404` (no body).
- Free text (name, description, labels, prompts) is plain text: control characters and markup are stripped on the way in, clients
  render it as text. It is never written into `docs/assistant-kb/**` or the docs index, and every assistant tool result that
  carries it is fenced and labelled untrusted.

## Data (Inference API owns; one EF migration, generated last)

`marketplace_recipes`, `marketplace_recipe_versions`, `marketplace_copies`, `marketplace_ratings`, `marketplace_reports`,
`marketplace_audit` (append-only). Columns are listed with the migration.
Status of a recipe: `draft` (version being prepared), `pending` (waiting for a human), `listed`, `rejected`, `removed`,
`unpublished`, plus a `hidden_by_reports` flag and `human_rejected_at` (set when an admin rejects or removes; while set no version auto-lists, every version ends `pending`). A new version supersedes the pending one; `current_version_id` changes only on approval. The live cap counts listed, pending and preparing drafts, and is re-checked at finalize (`live_limit`). Version `preview_status`: `preparing | ready | failed`.
A copy is an ordinary workflow: `created_via = 'marketplace'`, `template_key = 'market:<recipeId:N>'`. No engine-table migration.

## Routes

`{id}` is the recipe Guid.

### Anonymous (prefix `/api/v1/public/marketplace`, own nginx zones; GET and HEAD only)

| Method | Path | Answer |
|---|---|---|
| GET | `/recipes?q=&category=&cost=&stepType=&language=&sort=new\|copied\|rated&cursor=&limit=` | `{items: RecipeCard[], nextCursor?}`; default 24, max 50; keyset |
| GET | `/recipes/{id}` | `RecipeDetail` |
| GET | `/recipes/{id}/preview` | mp4, Range, `Cache-Control: public, max-age=120`, strong `ETag` = stored sha256, `If-None-Match` answers 304; served only if the object matches the stored hash |
| GET | `/recipes/{id}/poster` | jpeg; same cache, ETag and hash rule as the preview (`PosterSha256`) |
| GET | `/facets` | `{categories: [{key,count}], stepTypes: [{key,count}], languages: [{key,count}], costClasses: [{key,count}]}`; counts of listed, visible recipes |

The `cost` filter and the `costClasses` keys are the `AppCostClass` words, sent as-is: `No credits`, `Uses credits`,
`Uses credits and a voice`, `Uses credits and AI shots`. `stepType` filter and `stepTypes` keys are the step type names
(for example `Agent`, `Voiceover`). A recipe is visible only when it is `listed`, not hidden by reports, and its current version has
a ready preview; the preview bytes are served only if they match the stored `PreviewSha256`. Any other case, and a flag-off
answer, is the same bare 404. An invalid cursor, sort or a query over 100 characters on the list is a bare 400.

`RecipeCard` = `{id, name, description (first 200 chars), category, language, costClass, stepCount, stepTypes[], authorLabel,
copyCount, ratingAverage|null, ratingCount, previewSeconds, listedAt, version}` (poster and preview URLs are derived by clients
from `id`).
`RecipeDetail` = card plus `{description (full), steps: [{order, type, label}], prompts: [{stepOrder, field, text}],
slots: [{kind: provider|file|voice, stepOrder, label}], inputs: [{key,label,kind}], costNote}`.

**nginx.** `nginx/locations.conf` has two case-insensitive regex locations for `/api/v1/public/marketplace/` (the media one for `recipes/{id}/preview|poster` first). Both are anonymous (`auth_basic off`, `Authorization` and `Cookie` blanked toward the upstream), GET/HEAD only (`limit_except`), and pass the upstream `Cache-Control: public` through. Zones in `nginx/nginx.conf`, keyed on `$client_ip_key` (IPv6 /64): `marketplace_list` 60r/m (burst 30) and `marketplace_media` 120r/m (burst 60, unbuffered, Range passthrough, 5m read timeout). Recipe ids are public, so the normal access log is kept. The signed-in `/api/v1/marketplace/` routes (no `/public/`) stay on the authenticated `/api/v1/` location. Pinned by `nginx/tests/forwarded-headers.sh`.

### Signed-in (normal `/api/v1/` location; verified email where stated)

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/marketplace/consent` | `{version, text, licenceSummary}` for the dialog |
| POST | `/api/v1/marketplace/publish-preview` | `{projectId, workflowId}` -> `{publishable, refusals[{code,stepOrder?,message}], removed[{stepOrder,field,what}], slots[], steps[], prompts[], costClass}`; writes nothing; Edit on the project |
| POST | `/api/v1/marketplace/recipes` | `{projectId, workflowId, outputRef, name, description, category, language, authorLabel, consentAccepted, consentVersion}` -> `201 {id, status, version, previewStatus}`; verified email; durable caps; `409 CONSENT_VERSION_STALE` |
| PUT | `/api/v1/marketplace/recipes/{id}` | same body (workflow may differ); new version, re-checked; owner org only |
| POST | `/api/v1/marketplace/recipes/{id}/unpublish` | owner org; copies stay |
| GET | `/api/v1/marketplace/mine` | the caller organization's recipes with status, reason category, version, preview status, stats (copies, runs, rating) |
| GET | `/api/v1/marketplace/recipes/{id}/me` | `{copied, canRate, myStars|null, reported}` |
| POST | `/api/v1/projects/{projectId}/marketplace/{id}/copy` | Edit; verified email; `{name?}` -> `201 {workflowId, name, needs[]}`; idempotent per (recipe, project, name); egress consent off |
| PUT | `/api/v1/marketplace/recipes/{id}/rating` | `{stars}`; `403 RATING_NOT_ELIGIBLE` |
| POST | `/api/v1/marketplace/recipes/{id}/report` | `{reason, note?}`; 10 per day per user; verified email |
| GET/POST | `/api/v1/marketplace/admin/...` | platform admin only: `queue` (rows carry `versionId` and `digest`), `reports`, `recipes`, `recipes/{id}/{approve\|reject}` (body `{versionId, digest}`, both required: `VERSION_REQUIRED`, `DIGEST_REQUIRED`; `409 STALE_VERSION` when the version is not the latest pending one or its digest changed), `recipes/{id}/{remove\|restore}`, `audit`. NOT under `/api/v1/admin/*` (nginx sends that to the Go API) |

Admin review rules (security verdict L5): `GET /api/v1/marketplace/admin/versions/{versionId}/preview` and `/poster` serve THAT version's own final object to a platform admin only (flag filter, `IsPlatformAdmin` re-read, Range, `Cache-Control: private, no-store`, preview sha verified, never a quarantine key; any failure a bare 404). Approve and reject need `versionId` and `digest`. A dismiss closes every open report of the recipe and clears `hidden_by_reports`; a dismissed (recipe, reporter) pair never counts again. Only reports from verified accounts at least `Marketplace:MinReporterAccountAgeDays` (7) old count toward `ReportsAutoHideThreshold`. `restore` answers 409 `RESTORE_BLOCKED_SAFETY` for a safety removal or when the author, a submitter or the organization has a project under a CSAM hold or content quarantine, and otherwise always returns the recipe to `pending` (an admin approves it afterwards). A CSAM hold removes, with reason `safety`, every recipe of the held organization and every recipe the project owner authored or submitted anywhere. Object deletes never touch keys containing `/quarantine/`.

Errors: `{code, message}`; codes in `SCREAMING_SNAKE`. Refusal codes for publish: `CUSTOM_AGENT`, `RUN_BOUND_SOURCE`,
`STEP_TYPE_NOT_SHAREABLE`, `HIDDEN_WORKFLOW_KIND`, `TOO_LARGE`, `TEXT_BLOCKED`, `PREVIEW_REQUIRED`, `DAILY_PUBLISH_LIMIT`,
`LIVE_RECIPE_LIMIT`, `MARKETPLACE_DISABLED` (404, bare when disabled), `EMAIL_NOT_VERIFIED` (403).

## Layers (stack, each behind the flag)

1. `data-and-publish`: tables, options, snapshot stripper, publishability catalog and drift test, publish gate, owner API.
2. `preview`: engine trim/poster job over the video lane, platform keys, scan and content gate, finalize.
3. `public-read`: anonymous read API, search, nginx zones and tests.
4. `copy-and-ratings`: copy, counters, run-count sweeper, ratings.
5. `moderation`: reports, admin API, audit, delist hooks, author notice.
6. `web`: dashboard community gallery, publish dialog, my recipes, admin page. Separate: `site` (marketing gallery).
7. `agents-and-docs`: assistant and MCP tools, docs, Italian, legal text draft.

### Preview objects: run nonce, staging, final key

Every preview dispatch carries a fresh random `RunNonce` (16 bytes, hex), stored on the version (`preview_run_nonce`) and echoed in the request and result events. The engine writes only to `marketplace/{recipeId}/{versionId}/staging/{nonce}/preview.mp4` and `poster.jpg`, never to a final key. The API ignores (and deletes the staging objects of) a result whose nonce is not the version's current one or whose version is not `preparing`. It runs the CSAM scan and the content check on the staged bytes, verifies the SHA-256, copies them to the final immutable key (`.../preview.mp4`, `poster.jpg`), re-hashes the copy, and only then records `preview_key`, `poster_key`, `preview_sha256` and finalizes. Nothing but the API writes a final key. The sweeper removes the staging objects of a version it fails. Layer 3 serving must verify the stored `preview_sha256` against the bytes it serves.

Each part is described in the matching section of [marketplace.md](../marketplace.md).

## Publish hardening

- Trust and the daily cap are the SUBMITTER's (`version.submitted_by_user_id`). A takedown, rejection or `human_rejected_at` revokes trust; an author (or author organization) with any human-rejected or removed recipe has every new recipe and version sent to `pending`, and a version whose `snapshot_digest` equals that of any other version that is still processing, pending, rejected or superseded, or that sits on a removed, hidden or human-rejected recipe, from any author, goes to `pending` (`digest_match`). A submitter with a held or quarantined project in any of their organizations publishes nothing. `409 DUPLICATE_SUBMISSION` answers an identical submission while one is preparing.
- Caps are counted and inserted under a per-organization lock; refused publishes are limited (`PUBLISH_ATTEMPTS_LIMITED`, 429, 20 per user per UTC day).
- A preview source must come from a project with no content quarantine or CSAM output hold, a clean (or never scanned) file or a video step result.
- Conditions and mappings are validated as expressions (`EXPRESSION_INVALID`); loops are bounded (`LOOP_BOUNDS`: at most 10 rounds, pass mark 1 to 10). Detail `steps[].limits` lists loop rounds and credit caps.
- Concurrency: `marketplace_recipes.revision` is bumped centrally by `InferenceApiDbContext.SaveChanges` for every modified recipe row (one mechanism). A set-based `ExecuteUpdate`/`ExecuteDelete` on recipes must bump `revision` itself or run under the same lock/transaction. Finalize retries once on a concurrent update, then ignores the version (`version_ignored`); owner actions answer `409 CONCURRENT_UPDATE`.

## As built (T-D1 check)

Departures and additions found when the docs were written; the as-built description is [../marketplace.md](../marketplace.md).

- The admin API also has `GET recipes/{id}`, `GET recipes/{id}/copies` and `POST reports/{id}/dismiss`.
- A report reason is one of `Illegal`, `Sexual`, `Copyright`, `Spam`, `Misleading`, `Other`; the note is at most 500 characters.
- A reject or remove reason is one of `content`, `unsafe`, `copyright`, `low_quality`, `other`; the author mail adds `preview` and `safety` categories (a safety removal sends no mail).
- Options added beyond the contract: `MaxBlockedPublishAttemptsPerUserPerDay`, `MinRatingsForAverage`, `ReportsAutoHideThreshold`, `MaxReportsPerUserPerDay`, `MinReporterAccountAgeDays`, `PreviewSweepIntervalMinutes`, `PreviewStuckMinutes`, `ConsentVersion`, `RunCount:IntervalMinutes`.
- Also built: admin `versions/{versionId}/preview` (reviewer's hash-checked, uncached view); restore never lists at once and is refused with `RESTORE_BLOCKED_SAFETY` after a safety removal or under an active hold; a report counts only from a verified account older than `MinReporterAccountAgeDays` (7) and not from a reporter an admin dismissed; copy answers `409 RECIPE_UNAVAILABLE`; `mine` carries a `reasonCategory` from a closed set; `SourceInputProfiles` is kept as a closed vocabulary; `Clean()` is a single linear pass that replaces every bracket with a space.
