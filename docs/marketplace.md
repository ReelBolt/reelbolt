# Recipe marketplace (as built)

Internal background. The contract it was built to is [design/recipe-marketplace.md](design/recipe-marketplace.md); this page
records what the code on the `recipe-marketplace` stack does, and what an operator must switch on. The product rules it follows: who checks a
recipe, what a recipe may contain, where it lives, the legal consent, and who may publish and rate.


## Switch and options

Everything is behind `Marketplace:Enabled` (default `false`; env `Marketplace__Enabled`). While it is off every marketplace route
answers a bare `404` and no worker does anything. Options (`MarketplaceOptions`, section `Marketplace`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch |
| `MaxPublishesPerUserPerDay` | 5 | Publishes per submitting user per UTC day |
| `MaxLivePerOrganization` | 50 | Live recipes per organization (listed, pending and preparing drafts), counted from rows, re-checked at finalize |
| `MaxBlockedPublishAttemptsPerUserPerDay` | 20 | Text-refused attempts per user per UTC day, then `PUBLISH_ATTEMPTS_LIMITED` (429) |
| `MinRatingsForAverage` | 5 | Ratings needed before an average shows |
| `ReportsAutoHideThreshold` | 3 | Open reports from distinct counting users that hide a recipe |
| `MaxReportsPerUserPerDay` | 10 | Reports per user per day |
| `MinReporterAccountAgeDays` | 7 | Only reports from a verified account at least this old count toward the auto-hide |
| `PreviewSweepIntervalMinutes` | 5 | Preview sweeper period; `0` turns it off |
| `PreviewStuckMinutes` | 30 | A version still `preparing` after this long is re-dispatched once, then marked failed |
| `RetentionMonths` | 12 | The retention sweep deletes routine moderation log rows and actioned reports 12 months after they were written or handled. Rows tied to a safety, illegal-content, copyright or rejected-by-moderation finding (classified by the exact `reason` in the row, plus every row of the same recipe or version) are kept. A dismissed report is cut to a minimal record (recipe, reporter, state, dates; reason neutralised, note and handler cleared) because it still stops that reporter counting toward an auto-hide. Open reports are never touched |
| `RetentionSweepIntervalMinutes` | 1440 | Retention sweep period; `0` turns it off |
| `ConsentVersion` | `MarketplaceConsentText.CurrentVersion` | Consent text version a publisher accepts |
| `RunCount:IntervalMinutes` | 60 | Run-count sweeper period (`Marketplace:RunCount`, also `StartupDelaySeconds`, default 60) |

The safety delisting sweeper runs every 2 minutes after a 90 second start-up delay (not configurable).

## Data

Tables `marketplace_recipes`, `marketplace_recipe_versions`, `marketplace_copies`, `marketplace_ratings`, `marketplace_reports`,
`marketplace_audit` (append-only). They are public by design: no tenant query filter, listed in `TenantBoundaryInventoryTests`.
Recipe ids are random Guids. No route, body or log line carries the author's user, organization, project, workflow, provider,
file, voice, step-result id or storage key; public DTOs carry an `authorLabel` string only. Free text is plain text (control
characters and markup stripped on the way in), is never written to `docs/assistant-kb/**`, and the assistant tool results that
carry it are fenced and labelled untrusted: the four marketplace tools, and for a copied workflow `ListWorkflows`, `GetWorkflow`,
`ListWorkflowExecutions` and `GetWorkflowExecution` (name, steps, labels; `communityOrigin: true`). Fence markers are `[BEGIN COMMUNITY RECIPE TEXT <nonce>]` and `[END COMMUNITY RECIPE TEXT <nonce>]` with a fresh nonce per result; the step list of a workflow comes back as `stepsFenced`. Community origin survives patch and assistant clone (`CreatedVia` is inherited by the clone; a `market:` template key also counts). Not fenced: a copied
workflow's step outputs and run errors, which are produced from the author's prompts (see `platform-assistant-mcp.md`).

Statuses: `draft` (a version is being prepared), `pending` (waits for a human), `listed`, `rejected`, `removed`, `unpublished`.
Extra state: `hidden_by_reports` (flag) and `human_rejected_at` (set when an admin rejects or removes). A new version supersedes the
pending one; `current_version_id` changes only on approval. `marketplace_recipes.revision` is bumped by
`InferenceApiDbContext.SaveChanges`; a set-based update on recipes must bump it itself.

## Listing decision

Every publish and every new version gets an automatic check (`ListingDecision.Decide`). It lists at once only when the text
check and the preview check both allow, no human rejected the recipe, the content does not match rejected content, AND the
submitter has had a recipe listed or the organization is on a paid plan (`creator`, `pro`, `team`, `enterprise`). Reasons:

- Refused: `text_blocked`, `preview_blocked`, `preview_failed`.
- Pending: `moderation_unavailable` (moderation off, no provider, or no answer), `text_review`, `preview_not_checked`,
  `preview_review`, `digest_match`, `human_review_required`, `not_trusted`.
- Listed: `auto_listed`.

Trust and the daily cap belong to the submitter (`submitted_by_user_id`). A rejection or removal sets `human_rejected_at` and revokes
trust: an author or organization with any human-rejected, hidden or removed recipe has every new recipe and version sent to `pending`.
A submitter with a held or quarantined project in any organization they belong to publishes nothing (no hint why, and the submitter
check is the user's, not only the author organization's).

`digest_match` compares the version's `snapshot_digest` (a hash of the snapshot and the manifest) with every other version of every
recipe, from any author. It matches when that other version is still being processed, waits for or was given `pending` or `rejected`,
was superseded, or belongs to a recipe that is removed, hidden or human-rejected. The matched recipe and version ids are recorded for
the admin queue only; no owner or public DTO carries them. A second identical submission by the same submitter while one is
`preparing` answers `409 DUPLICATE_SUBMISSION`.

## What is stripped and refused

Only built-in agents and plain step settings publish. The verdict per step type is `MarketplaceStepPublishability.For`, an explicit
arm per `StepType` with no default; `MarketplaceCatalogDriftTests` fails when a step type or config property has no decision.

- Refused with a reason: `CUSTOM_AGENT`, `RUN_BOUND_SOURCE`, `STEP_TYPE_NOT_SHAREABLE` (`Revise`, `DesignGraphic`, `TimelineRender`,
  hidden workflow kinds as `HIDDEN_WORKFLOW_KIND`), `TOO_LARGE`, `TEXT_BLOCKED`, `PREVIEW_REQUIRED`, `EXPRESSION_INVALID`,
  `LOOP_BOUNDS`.
- Open slots (the copier fills them): provider ids, voice ids (`ProjectVoiceId`, `DefaultVoiceId`, `VoiceoverLine.VoiceId`), project file
  ids. Field classes in `MarketplaceFieldCatalog`: `Keep`, `Text` (sanitised, listed in the detail, content-checked), `Slot`, `Strip`.
- Run-bound sources (`StepResultId` fields, analysis results) are stripped and refuse the recipe.
- `SourceInputProfiles` (camera log profile per source index, on `VideoCompile` and `VideoAnalyze`) is kept as a closed vocabulary (`FieldRule.Keep`, filtered by `ConfigWalker.KeepSourceInputProfiles`).
- Built-in director and seat agent ids are dropped to the default. `allowSourceMediaEgress` is forced `false` on copy.
- `ConfigWalker` drops any property the catalog does not list at run time.
- Conditions and mappings are validated as expressions. Loops: at most 10 rounds (`MaxIterations`), pass mark 1 to 10 (`MinScore`).
- `SnapshotLeakGuard` checks the finished snapshot for author-owned ids before it is stored. `MarketplaceText.Clean()` is one linear pass: control, bidi and zero-width characters are removed, the text is NFKC-normalised, every bracket is replaced with a space, whitespace runs collapse, the cut is by code point (never mid-pair), and it never throws.

## Preview pipeline

The engine trims a preview and a poster over the video lane. Every dispatch carries a fresh `RunNonce` (16 bytes, hex) stored as
`preview_run_nonce`. The engine writes only to `marketplace/{recipeId}/{versionId}/staging/{nonce}/`. The API ignores a result with a
different nonce or for a version not `preparing` and deletes its staging objects. It scans the staged bytes (CSAM scan, content check),
verifies the SHA-256, copies to the final immutable key, re-hashes the copy, then records `preview_key`, `poster_key`,
`preview_sha256` and finalizes. The public preview route serves bytes only when they match the stored `preview_sha256`. A preview
source must come from a project with no content quarantine or CSAM output hold, and a file that is clean or never scanned (a file whose CSAM status is quarantined, pending or scanning, or whose moderation status is quarantined or scanning, is refused) or a video step result.
The sweeper re-dispatches a stuck version once (`PreviewStuckMinutes`), then fails it and removes its staging objects.
Version `preview_status`: `preparing`, `ready`, `failed`.

## Routes

Anonymous, `/api/v1/public/marketplace`, GET and HEAD only: `recipes`, `recipes/{id}`, `recipes/{id}/preview`, `recipes/{id}/poster`,
`facets`. A recipe is visible only when listed, not hidden by reports, and its current version has a ready preview. Every other case
is the same bare 404; an invalid cursor, sort or a query over 100 characters is a bare 400. The public preview and poster answer with `Cache-Control: public, max-age=120` and an `ETag`; the preview bytes are still checked against `preview_sha256` before a body is served.

Signed in, `/api/v1/marketplace`: `consent`, `publish-preview` (writes nothing), `recipes` (POST publish, PUT new version),
`recipes/{id}/unpublish`, `mine` (each row carries a `reasonCategory`, one of `content`, `unsafe`, `copyright`, `low_quality`, `other`, `preview`, `safety`, never free text), `recipes/{id}/me`, `recipes/{id}/rating` (PUT), `recipes/{id}/report` (POST), and the copy route
`POST /api/v1/projects/{projectId}/marketplace/{id}/copy`. Publish, copy and report need a verified email (`EMAIL_NOT_VERIFIED`, 403).
Errors are `{code, message}`; `CONSENT_VERSION_STALE` and `CONCURRENT_UPDATE` are 409.

Admin, `/api/v1/marketplace/admin` (platform admin only; not under `/api/v1/admin/*`, which nginx sends to the Go API):
`queue`, `reports`, `recipes`, `recipes/{id}`, `recipes/{id}/copies`, `recipes/{id}/{approve|reject|remove|restore}`,
`reports/{id}/dismiss`, `audit`, and `versions/{versionId}/preview` and `.../poster` (the reviewer's view of the exact version under review: that version's own final object, hash-checked on every serve and never cached; any failure is a bare 404). The queue is paged in SQL. Approve and reject take `{versionId, digest}`: a missing `versionId` is `400 VERSION_REQUIRED` (only an explicit `ClearHidden` approve on a live, hidden recipe works without one), a missing digest is `400 DIGEST_REQUIRED`, and `409 STALE_VERSION` answers when the version is not the latest pending one or its digest changed. Reject and remove take a reason from `content`, `unsafe`, `copyright`, `low_quality`,
`other` and a note up to 500 characters.

## Copy

A copy is an ordinary workflow in the caller's project: `created_via = 'marketplace'`, `template_key = 'market:<recipeId:N>'`. There
is no live link: later versions, unpublishing or removal never change a copy. Nothing runs and nothing is charged. The answer lists
what the copy still needs (open slots and the usual readiness items). Idempotent per (recipe, project, name): a repeat answers `200`
with the existing copy. A recipe that is not copyable at that moment answers `409 RECIPE_UNAVAILABLE` and adds nothing to the project. A community origin survives an assistant clone or patch of the copy (`CreatedVia` is inherited). One row per (recipe, organization) in `marketplace_copies` drives the `copy_orgs` counter ("usage"). The
event `product.marketplace_copied` carries `market:<recipeId>` as detail.

## Ratings

1 to 5 stars, one per account, from an account whose organization copied the recipe and has at least one `Passed` run of that copy
(`403 RATING_NOT_ELIGIBLE` otherwise). The average shows from `MinRatingsForAverage` ratings. No written reviews. Run counts come
from the run-count sweeper.

## Reports, hiding, removal, restore, audit

A report is `{reason, note?}` with reason `Illegal`, `Sexual`, `Copyright`, `Spam`, `Misleading` or `Other` (note up to 500
characters). Open reports from `ReportsAutoHideThreshold` distinct users set `hidden_by_reports` (audited as `auto_hidden`). Only reports from a verified account at least `MinReporterAccountAgeDays` old count, and a reporter whose earlier report on that recipe an admin dismissed no longer counts; the
recipe disappears from the public API and cannot be copied until an admin decides. An admin may dismiss reports, reject, remove or
restore. Remove sets `human_rejected_at`; restore is the only way out of `removed`, and it never lists at once: the recipe goes back to review. A restore is refused (`409 RESTORE_BLOCKED_SAFETY`) for a safety removal and while the author, a submitter or the organization is under an active hold. Every transition writes an audit row. The author
gets one mail (`marketplace_notice`) carrying a reason category only (`content`, `unsafe`, `copyright`, `low_quality`, `other`,
`preview`, `safety`), never an admin note, a reporter or free text. Sending is best effort.

## CSAM preview match

A preview that matches the CSAM control is attributed to the SUBMITTER of the version, not to the recipe's author or organization.
The submitter gets a HARD hold: every recipe they submitted comes down at once in every workspace (the response runs the safety
takedown immediately rather than waiting for the 2-minute sweep), and leaving an organization does not escape it.

The response saves first and copies second. The first save records the evidence marker (`evidence: staged`, naming both staged
keys), the audit row, the version as failed, the recipe as removed or rejected, and the CSAM and content holds on the source
project; it retries up to 5 times on a concurrency conflict, re-reading the conflicting rows and re-inserting anything the failed
save had not stored. Only after that save succeeds are the staged objects copied to `quarantine/`, and a staged object is deleted
only once its quarantine copy is verified by hash. If every attempt of the first save fails, nothing is deleted and the version
stays `preparing`. Quarantined keys are never deleted by any path.

## Delisting hooks

`MarketplaceSafetyService` (every 2 minutes): when any project of an author's organization holds a CSAM output hold, or the preview
source project of one of its recipes is under a content quarantine, every recipe of that organization goes `removed` with reason
`safety` through the admin transition (so `human_rejected_at` is set) and its objects are deleted. No notice is sent.

Account and organization purge (`AccountPurgeService`) deletes the user's ratings and reports (a rating also leaves the counters),
then every recipe authored by the user or a purged workspace with its versions, copies, ratings, reports and stored objects. Audit
rows stay without the actor.

## Failure modes

- Moderation off or no provider: text verdict `Unavailable`, every version waits for a human.
- Preview fails or its source is blocked: the version is `failed`/refused and the author gets a `preview` notice.
- Concurrent update on a recipe row: finalize retries once, then ignores the version (`version_ignored`); owner actions answer `409 CONCURRENT_UPDATE`. The CSAM response is the exception: it retries up to 5 times, re-reading the conflicting rows each time, and a save that still fails is raised rather than ignored (see "CSAM preview match").
- Object deletion after a removal that fails is retried on each safety pass.

## Before switching it on

Do all of this before setting `Marketplace__Enabled=true`. Nothing is public until then, so each step can be done in advance.

1. **Legal gate.** The legal text is published in the Terms (section 10), the Acceptable Use Policy (section 5.1) and the Privacy Policy at
   version `2026-11-10`; the source is an operator document. The flag may be enabled
   once the release that carries that text is deployed.
2. **Migrations.** Apply `AddRecipeMarketplace` and `AddMarketplacePreviewRunNonce` (the Inference API applies its migrations at start-up).
3. **Auto-listing prerequisites.** Set `ContentModeration:Enabled=true` and configure a moderation provider. Without them the text verdict
   is `Unavailable` and every version waits in the admin queue (safe, just slow). Configure the CSAM control (see
   [csam.md](csam.md)): the preview gate and the safety delisting sweeper depend on it.
4. **nginx.** `locations.conf` carries the anonymous prefix `/api/v1/public/marketplace/` (GET and HEAD only) with its own zones in
   `nginx.conf`: `marketplace_list` (60 requests a minute per address, burst 30) for list, detail and facets, and `marketplace_media`
   (120 a minute, burst 60) for preview and poster.
5. **Marketing site.** The gallery (`/recipes`, `/it/recipes`) reads the API through same-origin routes under `/api/recipes*` that forward to
   `${REELBOLT_API_URL}/api/v1/public/marketplace/...` (see [marketing-site.md](marketing-site.md)). Set `REELBOLT_API_URL` at
   build time so the build pre-renders the listed recipes, and add a scheduled (daily) deploy of the Pages project so new recipes get
   static pages and sitemap entries. Add a Cloudflare rate-limiting rule for `/api/recipes*`; the site proxy has no limit of its own.
6. **Admin.** A platform admin must watch the queue under `/api/v1/marketplace/admin` (the web admin page).
7. **Switch on.** Set `MARKETPLACE_ENABLED=true` on both hosts: compose passes it as `Marketplace__Enabled` to the Inference API and to
   the engine. A deployment that writes the host `.env` from stored secrets (`CONTROL_ENV_FILE`, `ENGINE_ENV_FILE`) must change them
   there, or the next deploy puts the old value back. Environment is read when a container is created, so recreate the services through the
   normal release path (a new slot deploy, or the operator host script on each host); do not name `workflow-engine` on a host where it is
   profile-disabled, since that starts it. Do the engine host first, then the API host, so no preview is refused while the API is on.
   Then publish one test recipe end to end. The default is `false`, so every existing deployment is unchanged until the value is set.

To switch it off again, set `MARKETPLACE_ENABLED=false` the same way: every route answers a bare 404 again and no
worker acts. Recipes, ratings and stored previews are kept, so switching back on resumes where it stopped.
