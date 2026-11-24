# ReelBolt documentation

This folder is the single source of truth for ReelBolt's documentation. It is used three ways:

1. **Read on GitHub** — every file is plain Markdown with relative links.
2. **Published as a website — the user guide only.** [`/docs-site`](../docs-site) (Docusaurus) renders
   `user-guide/` and serves it at `/docs/` behind the root nginx. The developer docs are **not** published
   anywhere. See [`docs-site.md`](docs-site.md).
3. **Searched by the platform assistant** — the whole tree (user guide and developer docs) is shipped inside
   the Inference API image, chunked by heading and embedded into Qdrant. See
   [`platform-docs-search.md`](platform-docs-search.md).

## Layout

| Path | Audience | Published on the site | Searched by the assistant |
|------|----------|-----------------------|---------------------------|
| `user-guide/**` | People using ReelBolt — plain language, no internals | `/docs/` | Yes (preferred) |
| `*.md` (this folder) | Developers and operators — architecture, design decisions, configuration | No — repository only | Yes, as internal background (no public link) |
| `archive/` | Historical implementation notes kept for context; may be out of date | No | No |
| `design/`, `marketing/`, `screenshots/`, `research/` | Internal working material (reviews, research, QA notes) | No | No |
| `legal/drafts/` | Proposed legal text and DPIA drafts, not in force, each marked NEEDS COUNSEL (today: avatars) | No | No |

Integrations that bring files into a project: [`github-app.md`](github-app.md) (a GitHub repository, pulled on every push), [`cloud-drive-sync.md`](cloud-drive-sync.md) (a folder on Google Drive, OneDrive/SharePoint or Dropbox, synced in the background) and [`local-folder-sync.md`](local-folder-sync.md) (a folder on a person's own computer, kept copied into a project by the desktop app — the iCloud Drive path); their user-guide pages are [`user-guide/github-repository.md`](user-guide/projects/github-repository.md), [`user-guide/cloud-drive-sync.md`](user-guide/projects/cloud-drive-sync.md) and [`user-guide/local-folder-sync.md`](user-guide/projects/local-folder-sync.md).

Zip uploads: [`zip-upload-unpack.md`](zip-upload-unpack.md) is how an uploaded zip becomes project files (pipeline, caps, exclusions, folder naming, quota, failure codes) and why sync paths never unpack.

The run brief: [`run-brief.md`](run-brief.md) is the typed set of choices a run is started with (language, length, format, resolution, style and tone, design system, voice, source files), its validation rules, where each field is stored, the request and response shapes, and the places a run's brief is copied.

Workflow apps: [`workflow-apps.md`](workflow-apps.md) explains how every workflow is also an app (title, inputs, cost badge, result), the optional app manifest and what it may not do, what is derived when there is none, the project gallery and the effective-manifest endpoints, and who created a workflow.

The pre-run linter: [`workflow-linter.md`](workflow-linter.md) lists the rules WL001 to WL022 (when each fires, why, how to fix it), the finding shape, the lint endpoints, the `Workflows:Lint:Enforce` rollout flag and the false-positive scan.

Project organization: [`project-organization.md`](project-organization.md) covers tags and folders (data model, caps, routes, tenancy, ETag), the activity-by-tag view and the assistant's `tags` and `folder` arguments.

Project versioning: [`project-versioning.md`](project-versioning.md) covers what a version holds, copy-on-change through the storage decorator, content identities and the hash sweeper, retention, quota counting, "Delete forever" and quarantine erasure, the restore rules for every kind with their permissions, the auto-capture triggers, the REST routes and the configuration keys; its user-guide page is [`user-guide/projects/history.md`](user-guide/projects/history.md).

Change requests: [`change-requests.md`](change-requests.md) is how one step of a finished run (the narration, line by line) is redone as a new linked run, which steps accept one, the API, and what is reused for free; the design is in [`design/change-requests.md`](design/change-requests.md).

Public API: [`public-api.md`](public-api.md) is the internal design of API keys, scopes, service accounts, per-key caps, queued run requests, idempotency, audit and webhooks; the customer pages are under [`user-guide/api/`](user-guide/api/index.md).

Billing and marketing: [`billing.md`](billing.md) (the Paddle and Stripe providers, the billing endpoints, and promotions and discounts), [`metering-and-billing.md`](metering-and-billing.md) (usage, the credit ledger and promo grants) and [`marketing-consent.md`](marketing-consent.md) (our marketing-email consent store, signed unsubscribe links, the admin export and the privacy acknowledgement); their user-guide pages are [`user-guide/account/plans-and-credits.md`](user-guide/account/plans-and-credits.md), [`user-guide/your-data.md`](user-guide/your-data.md) and [`user-guide/admin/promotions.md`](user-guide/admin/promotions.md).

New-user funnel: [`product-events.md`](product-events.md) is the reference for the `product_events` rows (the event vocabulary, the visitor events, first-touch attribution and retention); the decisions and the contract shared by the site, the app, the two APIs and BI are in [`design/new-user-funnel.md`](design/new-user-funnel.md). The operator runbooks for tracked links and the BI role are operator documents and are not in the public repository.

Desktop runner distribution: the customer pages are under `user-guide/runner/`. No runner container image is published to GHCR at launch.

On-demand cloud compute: [`pool-waker.md`](pool-waker.md) covers the pool waker that starts and deletes CloudPool servers when work is queued: the loop, the node and spend caps, orphan cleanup, the heartbeat, the usage record, failure modes, the security model and the operator runbook (stage 0, bake, flag). Off by default; internal and not published. Turning it on in production: the operator runbook (the `pool-waker` compose service, the pool-only engine switches, the public object-store prerequisite, the private pool subnet and host isolation with its apply-and-verify runbook, enable and rollback order).

Local models: [`local-llm-gateway.md`](local-llm-gateway.md) covers using a model on a member's own computer through the desktop runner: the architecture, the `llm.*` messages and internal routes with their error mapping, the `LocalRunner` provider kind, configuration, runner-side controls, the threat model and an operator checklist. Its user-guide page is [`user-guide/account/local-model.md`](user-guide/account/local-model.md). It is internal background and is not published.

Background activity (the header indicator and tray, `GET /api/v1/activity`, the shared event stream, how to add a job type, and the rules for tools that change the document while they run) is in [`background-activity.md`](background-activity.md).

Post text: [`post-text.md`](post-text.md) covers the `WritePostText` assistant and MCP tool (one billed call that spends credits, text only, nothing saved), the opt-in read-the-video and web-search hashtag options with what leaves the host, and why hashtags are never called trending; the customer page is [`user-guide/assistant/post-text.md`](user-guide/assistant/post-text.md).

Social publishing: [`social-publishing.md`](social-publishing.md) is the architecture (tables, state machine, lease, fire-time re-check, limits, assistant tools, `SocialPublishing__*` keys); [`social-publishing-credentials.md`](social-publishing-credentials.md) lists the developer-app credentials, scopes, reviews and redirect URIs per platform; [`social-publishing-legal-draft.md`](social-publishing-legal-draft.md) is draft Terms and Privacy copy awaiting sign-off; the customer page is [`user-guide/projects/publishing.md`](user-guide/projects/publishing.md).

Avatars: [`avatars.md`](avatars.md) covers a project's avatars (a consented likeness of a real person for video models): the tables and who owns them, the one selectability rule, how a reference is observed (digest, held signals), the engine's `IAvatarResolver` and its failure codes, the read-only `ListProjectAvatars` tool and the `Avatars:Enabled` kill switch; the plan is [`design/avatars.md`](design/avatars.md) and the customer page is [`user-guide/projects/avatars.md`](user-guide/projects/avatars.md).

Camera log profiles (the clip's `inputProfile`, the render and preview stages, detection, the viewing LUT and known gaps) are in [`camera-profiles.md`](camera-profiles.md).
Recipe marketplace: [`marketplace.md`](marketplace.md) is the community recipe gallery as built (routes, listing decision, what is stripped from a shared recipe, previews, copy, ratings, reports and removal, options and the operator checklist); the contract is [`design/recipe-marketplace.md`](design/recipe-marketplace.md); the user-guide pages are in [`user-guide/recipes/`](user-guide/recipes/share-a-recipe.md). Draft legal text for counsel is in `marketing/legal-drafts/recipe-marketplace.md` (not live).

Design records in `design/`: [`app-inputs.md`](design/app-inputs.md) is the plan for typed app inputs (prompts, numbers, switches, model choice) bound into step settings and Conditional branches, and the merged "Avatar clip" recipe (plan, 2026-11-06); [`tenancy.md`](design/tenancy.md) is the organizations, roles, JWT and entitlements ADR for the SaaS launch; [`runner-protocol-adr.md`](design/runner-protocol-adr.md) is the compute fabric's, [`desktop-runner-adr.md`](design/desktop-runner-adr.md) the desktop runner's, and [`local-folder-sync-and-storage.md`](design/local-folder-sync-and-storage.md) the computer folder sync, plan storage limits and uncapped uploads; [`guest-review-links.md`](design/guest-review-links.md) is the design and threat model for guest review links; [`avatars.md`](design/avatars.md) is the plan for avatars (a consented likeness of a person, with reference pictures, videos and voice, for video models; phase 1 foundation, approved 2026-11-05); [`editor-pro-ui-alignment.md`](design/editor-pro-ui-alignment.md) is the editor's page model, panel geometry, where masks, colour and deliver live, and the keymap proposal (plan for approval, 2026-10-30); [`editor-simple-mode.md`](design/editor-simple-mode.md) is the design record of the Simple shell beside the full editor (who starts where, what it shows, the ask line over the existing helpers, how it is built on the split workspace; built 2026-11-11, with what differs from the proposal); [`control-shapes.md`](design/control-shapes.md) is the three slider and switch shape proposals (rounded parallelogram), with measured contrast, cost, theme hooks and the `--rb-control-*` token set (proposal for approval, 2026-10-30).

Operating the stack: [`observability.md`](observability.md) (telemetry export, dashboards and alerts), [`database-connections.md`](database-connections.md) (each service's Postgres pool cap, the connection budget against `max_connections`, the pool metrics and alerts, and what must change before PgBouncer), [`postgres-backups.md`](postgres-backups.md) (the nightly encrypted `pg_dump` to R2: key custody, retention, and the restore), the operator runbook (the one-time cut-over that leaves the render host with least-privilege database logins, the engine token key instead of the session key, no cloud provider token and its own object-store token; each step with verify and rollback, and the residual risks), the operator runbook (zero-downtime deploys: slots, drain and retire, rollback, the expand and contract migration rule, capacity during overlap, enabling the Actions workflow, and 3am troubleshooting) and [`csam.md`](csam.md) (the CSAM hash-matching control: what it does and does not guarantee, what leaves the host, and the manual match-response and reporting runbook).

Web search for agents: [`web-search.md`](web-search.md) covers the `WebSearch` tool group and capability, how DeepSeek, OpenAI and Gemini are called, prices, the caps and error codes, the Gemini grounding restriction, billing and the known gaps.

Content safety: [`content-moderation.md`](content-moderation.md) covers the `ContentModeration` inference capability, its four phase-1 surfaces (generation prompts, ingest, generated text, publish/egress), the block/review thresholds, the fail-open and fail-closed stances, video/audio sampling and hash-only audit retention, the platform-admin review queue, operator settings, and the CSAM exclusion/runbook. It is internal background and is not published.

## Writing guidelines

- **User guide**: plain language for non-engineers. Explain what a feature does and when to use it; put exact
  step names, agent names and JSON keys in backticks or code blocks, with a plain explanation next to them.
  Every page needs front matter (`title`, `sidebar_position`, `description`).
- **Every `##` section should stand on its own.** The assistant retrieves individual sections, not whole
  pages, so restate the subject instead of writing "as above".
- **Markdown, not MDX.** `.md` files are parsed as CommonMark, so literal `{` and `<` are fine.
- **The user guide links only to other user-guide pages.** It is published and read by customers, so it never
  points at a developer doc, a source file or a screenshot outside `user-guide/`; the site build fails on any
  such link. Developer docs may link anywhere in the repository (GitHub resolves it).
- **The user guide describes what ReelBolt does today.** It never says something is planned, coming or "not
  yet" available. Where an operation is not supported, say plainly that it is not supported, and name the
  closest alternative if there is one.
- A developer doc needs no registration: it is picked up by the assistant's documentation search as soon as it
  is in the tree, and it is not published. It cannot be reached from the site.
- `npm run build` in `/docs-site` fails on any broken link and on anything that would publish more than the
  user guide — run it before pushing user-guide changes (CI does).
- **Publishing, versioning and release notes:** [`docs-process.md`](docs-process.md) is the procedure — how
  to edit the current guide, how to cut a version, how to write release notes, and what is never published.
- **Enhancement backlog:** [`enhancement-backlog.md`](enhancement-backlog.md) is internal and unpublished — candidate enhancements derived from the limits once shown on audience pages (removed 2026-11-13); the owner picks what to build.
