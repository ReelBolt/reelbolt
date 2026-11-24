# SaaS accounts: lifecycle, abuse model and GDPR data map

Work package C0 of the SaaS launch plan. It fixes the decisions that C1 to C9 build on. Where this file and an area plan disagree, this file wins for account matters.

Scope: self-serve signup (D12, behind `SIGNUP_ENABLED`, default `false`), email verification, password reset, abuse controls, trial grant, invite acceptance, GDPR export and deletion. Self-hosted installs keep admin-created users and are unchanged.

## Account state machine

```
                 signup                verify link
  (none) ───────────────▶ Registered ─────────────▶ Verified
                          (unverified)                │  ▲
                              │                       │  │ login within grace
              unverified 7d   │         delete request│  │ cancels deletion
              (cloud only)    ▼                       ▼  │
                           Purged             DeletionRequested
                                              (grace 30 days)
                                                      │ grace elapsed
                                                      ▼
                                                   Purged
```

| State | How it is stored (C1 columns) | What the user can do |
|---|---|---|
| Registered (unverified) | `EmailVerifiedAt IS NULL` | Log in and see a "confirm your email" screen. Cannot run workflows, create providers or start a trial grant (C6 grants credits on verification, not on signup). |
| Verified | `EmailVerifiedAt` set | Everything the plan allows. |
| DeletionRequested | `DeletionRequestedAt` set, `TokensInvalidatedAt = now` | Existing sessions die. Logging in again within 30 days cancels the deletion (clears `DeletionRequestedAt`) and shows a banner. No workflow starts while the flag is set. |
| Purged | Row deleted by `AccountPurgeService` (C9b) | Nothing. The email can sign up again. |

Rules:

- Existing rows (self-host admins and invited users) are migrated with `EmailVerifiedAt = CreatedAt`, so nothing changes for them.
- Admin-created users (`POST /api/v1/admin/users`) are treated as verified on creation, because the admin vouches for the address. They still get a temporary password and `MustChangePassword`.
- Invited users (C8) who sign up from an invite link count as verified, but only when the signup email equals the invite email.
- `TokensInvalidatedAt` is the session kill switch: every JWT carries `iat`, and the Go auth middleware (C4) rejects a token issued before `TokensInvalidatedAt`. Password reset, password change and deletion request all set it. The .NET services need no change, because tokens last 24 hours and the engine's on-behalf-of tokens last 5 minutes. A cloud-only follow-up may add the check to the Inference API if the 24h window proves too long.
- An unverified account older than 7 days is purged by the same background service (cloud only, `Tenancy:Mode=Cloud`). It owns nothing worth keeping, and this limits the cost of signup spam.

## Token rules

One table, `user_tokens` (C1), serves verify-email, reset-password and the later email-change flow.

| Rule | Value |
|---|---|
| Generation | 32 bytes from `crypto/rand`, URL-safe base64 without padding (43 characters). |
| Storage | SHA-256 of the raw token, hex, unique column `token_hash`. The raw value exists only in the email and in the URL. A database leak yields nothing usable. |
| Lookup | Hash the presented token, then look up by `token_hash` (an indexed equality match, so no timing oracle on the secret). |
| TTL | `VerifyEmail` 24 hours. `ResetPassword` 1 hour. `EmailChange` 24 hours. |
| Single use | `used_at` is set in the same transaction that applies the effect. A used or expired token returns the same generic error as an unknown token. |
| Supersession | Issuing a new token for the same user and purpose sets `used_at` on all earlier unused ones. Only the newest link works. |
| Reset side effects | Set the new password hash, clear `MustChangePassword`, set `TokensInvalidatedAt`, invalidate other reset tokens. Do not auto-login. |
| Links | `PUBLIC_BASE_URL` + `/app/verify-email?token=…` and `/app/reset-password?token=…`. The token sits in the query string only, so it never reaches an `Authorization` header or an access log on a path. Pages must send `Referrer-Policy: no-referrer` (C7-UI). |
| Responses | Signup, resend-verification and forgot-password always answer 202 with the same body, whether or not the address exists. Existing addresses get a mail saying so (and a reset link for forgot-password). |

## Abuse model

Three threats matter at launch. C5 owns the implementation.

| Threat | What the attacker wants | Controls (C5 implements) |
|---|---|---|
| **Trial farming** | Many free 100-credit trials. | One trial grant per normalised email: lowercase, strip `+tag`, and strip dots for gmail.com and googlemail.com. Stored as `trial_claims` (hash only, survives account deletion, so deleting and re-signing up does not re-grant). Grant happens on email verification, never on signup. Reject a configurable list of disposable-email domains (`ABUSE_DISPOSABLE_DOMAINS_FILE`, empty list by default). Per-IP cap on signups (3 per hour, 10 per day) and a global signup-per-hour circuit breaker that flips `SIGNUP_ENABLED` semantics to "waitlist" when exceeded. Trial runs are watermarked and capped at 720p (B8b), which lowers the value of a stolen trial. A proof-of-work or CAPTCHA hook is left as an interface (`SignupChallenge`), off by default. |
| **Email bombing** | Use us to flood a victim's mailbox with verify or reset mails. | Per-address cooldown: at most 1 verification mail per 60 seconds and 5 per 24 hours; at most 3 reset mails per hour. Per-IP limit on `/auth/forgot-password`, `/auth/resend-verification` and `/auth/signup`. Mail sends go through the mailer's header and recipient validation (C2). No account-existence signal in responses. Invites (A4) are limited per org per day. |
| **Credential stuffing** | Try leaked passwords against `/api/v1/auth/token`. | Per-IP and per-account (email hash) login rate limits with exponential backoff, in nginx `limit_req_zone` for the coarse layer and an in-process limiter in Go for the per-account layer. Constant-time response: `handleToken` already compares against `dummyPasswordHash` when the user does not exist, and C5 keeps that parity (same status, same body, similar latency). Minimum password length 10 on every set path, and a bundled top-passwords denylist check. Failed-login counts are logged per account for the C11 review. Locked accounts get a generic message and a reset-password hint, never a "locked" status. |

Common to all three: the real client IP must reach Go. nginx uses `real_ip_header X-Forwarded-For` with `set_real_ip_from` set to the load balancer range (Area F, tracked in C11). Rate-limit state is in process for v1 (single Go replica). Moving to Redis or Postgres is needed before a second replica (tracked as a C5 follow-up).

Out of scope for v1: device fingerprinting, phone verification, MFA. MFA is a post-launch candidate.

## Decision: how the Inference API sends mail

This closes the open point left in C9a.

**Decision: the Go API owns all outbound mail. The Inference API (and later the engine) call a Go internal mail endpoint. The Inference API gets no SMTP settings.**

Shape:

- `POST /internal/mail/send` on the Go API, body `{ "to", "template", "data": {…} }`, where `template` is one of the C2 `TemplateName` values and `data` maps to `MailData`.
- It is **not routed by nginx** (nginx only forwards `/api/v1/*`, `/health` and the site paths, so `/internal/*` is unreachable from outside the compose network, the same way the sandbox executor is kept private).
- Authenticated with a bearer token: a service JWT minted from the shared `JWT_SIGNING_KEY` with `act=inference-api` and a 5-minute lifetime (reusing the on-behalf-of shape from `OnBehalfOfTokenIssuer`), or a dedicated shared secret `INTERNAL_API_TOKEN` if a simpler model is preferred when C9a lands. Decide there, either is compatible with this design. The endpoint only accepts the closed template list, so a caller can never send arbitrary content.
- Recipient is validated by the mailer. Additionally, the endpoint only mails an address that belongs to an existing user, or an invite address recorded by A4, which stops a compromised caller from using it as an open relay.
- The Go API sends asynchronously and returns 202. Delivery failures are logged and never block the caller (export links are also shown in the UI).

Why not give the Inference API its own SMTP client:

1. **One place for SMTP credentials and one `From` identity.** SPF, DKIM and DMARC are set up once (Area F DNS checklist). A second sender would need the same credentials in a second service's configuration.
2. **One template set.** Templates and the `html/template` escaping live in Go (C2). A C# mailer would duplicate seven templates and drift.
3. **Self-host parity.** `SMTP_*` is already configured on `go-api` only. Self-hosters keep configuring mail in exactly one place. If no SMTP is configured, the Go fallback logs and succeeds, which is today's behaviour.
4. **The mail triggers that originate in .NET are few:** `export_ready` (C9a), `low_balance` (B10c) and the purge confirmation. A single internal call covers them.

Cost of this choice: a runtime dependency from Inference API to Go API for mail. The Inference API already depends on Go for identity (compose `depends_on`), and every call is best-effort. The endpoint itself ships with the first .NET consumer (C9a, or B10c if earlier), not in C2.

## GDPR data map

Personal data is anything tied to a natural person: the account email and name, and the content they produced (prompts, uploaded footage, voices). The map was built by reading `inference/src/ReelBolt.Shared/Data/Models` and `api/models`, and the object-store key layout in `S3FileStorageService`.

Legend for *Erasure*: **Delete** removes the rows. **Anonymise** keeps the row but drops the person link. **Retain** keeps the row for a legal reason.

### Tables holding personal data

| Table (owner) | Personal data it holds | Keyed by | Export | Erasure |
|---|---|---|---|---|
| `application_users` (API; Go reads and writes) | `email`, `display_name`, `password_hash`, `created_at`, `is_admin`. C1 adds `signup_ip`, `auth_provider`, `email_verified_at`, `deletion_requested_at`. | `id` | Yes: profile JSON without `password_hash`. | Delete (the last step of purge). |
| `user_tokens` (C1) | Token hashes, purposes, timestamps. No raw token. | `user_id` | No (security artefacts, no user content). | Delete. |
| `legal_acceptances` (C1) | Document, version, accepted_at, `ip`. | `user_id` | Yes (proof of consent is the user's data). | Delete. |
| `organization_members` and `organization_invites` (A1) | Membership role; invite `email`, inviter. | `user_id`, `email` | Yes (memberships). | Delete the user's memberships. Invites sent to the user's email are deleted. Invites the user sent are kept but `invited_by` is set to null. |
| `organizations` (Personal kind, A1) | Name derived from the user's name. | `id` (one owner) | Yes. | Delete with the user. A Team org the user solely owns with other members blocks deletion (C9b). A Team org where the user is the only member is deleted. |
| `projects` | `name`, `description`. | `owner_id` (to become the org id after A1/D4) | Yes. | Delete (cascade through the rows below). |
| `project_files` and the objects they point to | `original_file_name`, `agent_summary` (LLM text about the file), `storage_metadata_json`; the **objects**: uploaded footage, images, documents. | `project_id` | Yes: metadata JSON plus original files up to a size cap, otherwise a manifest with signed links (C9a). | Delete rows and the objects under `projects/{projectId}/` (prefixes `userFiles`, `agentFiles`, `outputFiles`). |
| `media_derivatives` | Storage keys of proxies, waveforms, thumbnails derived from the user's media (no writer yet). | `project_id`, `source_project_file_id` | Manifest only (derivatives are regenerable). | Delete rows and objects. |
| `workflow_definitions`, `workflow_steps` | Workflow name and step configuration (may contain prompts and brand text). | `project_id` | Yes. | Delete. |
| `workflow_executions` | `user_request` (free-text prompt), `style_config_json`, `result_json`, `error_message`, `initiated_by_user_id`. | `project_id`, `initiated_by_user_id` | Yes. | Delete. |
| `workflow_step_results` | `input_json` and `output_json` (prompts, scripts, transcripts of the user's speech, captions read from their video); storage keys for rendered video, artifacts and timelines. | `workflow_execution_id` | Yes (JSON); rendered outputs as files or manifest. | Delete rows and the objects under the project prefix. |
| `review_scores` | Reviewer feedback text about the user's output. | `workflow_execution_id` | Yes. | Delete. |
| `workflow_step_cache_entries` | Cached step outputs (contain transcripts, scripts) and storage keys. No user column. | `project_id` | No (derived cache). | Delete by project. |
| `decision_observations` | Question, options, answers and probabilities of decision models. The `state` sent is not stored, but question text can echo content. **No user column and no foreign keys.** | `execution_id` (denormalised) | No (telemetry). | Delete by `execution_id` for every execution of the user's projects. |
| `external_generation_jobs` | `prompt` sent to the video generation vendor, `remote_job_id`, cost, storage key of the clip. No user column. | `project_id`, `execution_id` | Prompt and manifest yes. | Delete rows and clips. The cost totals needed for bookkeeping are held in `usage_events` (below), which survives. |
| `assistant_threads` and `assistant_messages` | The full chat with the platform assistant: `title`, message `content`. | `user_id` | Yes (JSON). | Delete. |
| `agent_definitions` (custom, `owner_id` not null) | The user's system prompts and tool configuration. Built-in rows are not personal. | `owner_id` | Yes. | Delete the user's custom agents. |
| `editing_styles` | Saved style name, description, config. | `owner_id` | Yes. | Delete. |
| `project_voices` | Name, vendor voice id, **cloned-voice consent record** (`consent_subject_name`, `consent_statement`, attesting user ids) and references to the **reference recordings** (biometric data of the subject). | `project_id`, `created_by_user_id` | Yes: record and consent facts; the reference recordings are included as files, not as links. | Delete the vendor model first (`IVoiceCloningClient`, best effort and logged), then the recordings (objects and `project_files` rows), then the row. The consent attestation is deleted with it. Vendor deletion failures are listed in the purge log for manual follow-up. |
| `project_avatars`, `project_avatar_references`, `project_avatar_vendor_assets` (avatars) | An avatar's name and description, the **consent record** (`consent_subject_name`, the verbatim `consent_statement`, version, locale, basis, attesting and revoking user ids, retention date), links to the **reference pictures, videos and recordings** (likeness and voice of the subject; treated as special-category data, docs/design/avatars.md) and, from phase 2, vendor-side copy ids. | `project_id`, `consent_attested_by_user_id`, `created_by_user_id` | Yes: the avatar rows with their references and vendor-copy outcomes in one document; the reference files are in the files/ folder. | Delete vendor copies first (best effort, logged), then the avatar rows, references and vendor-asset rows, then the files, in the purge's one transaction (a reference holds its file with `NO ACTION`). |
| `avatar_library_items`, `avatar_library_references` (workspace avatar library) | A library item's name and description, the **workspace consent record** (`consent_subject_name`, the verbatim workspace `consent_statement`, version, locale, basis, attesting and revoking user ids, retention date) and, per file, its kind, role, digest, size and name. The media are the library's own copies in organization storage (`organizations/{organizationId}/avatar-library/`), treated as special-category data like an avatar's reference files (docs/design/avatar-objects.md). | `organization_id`, `consent_attested_by_user_id`, `created_by_user_id` | Yes: the items of the personal workspace with the facts of their files in one document; the library's own copies are not packed (an import's files are project files and are in the files/ folder). | Delete the organization's library prefix from the object store, then the imports (they are project avatars and go with their projects), then the item and reference rows, in the purge's one transaction (an import holds its item with `NO ACTION`). |
| `project_avatar_events` (avatars) | Append-only audit of avatars: kind, time, actor user id, avatar/project/organization ids, statement version, counts and outcome words. **Never** media, file names, storage keys or the subject's name. No foreign keys. | `project_id`, `actor_user_id` | Yes: the events of the user's projects and every event they acted in. | **Retain** for 3 years (AV-7, accountability: proof of consent, revoke and deletion), pseudonymised: the purge sets `actor_user_id` to null and keeps the row, as `usage_events` keeps its row without the person. Purged by `ProjectAvatarRetentionService` after 3 years. |
| `edit_timelines`, `edit_timeline_ops`, `edit_timeline_snapshots` | The editor document (clip names and text), the full op history and snapshots. | `project_id`, `timeline_id` | Yes (documents; ops optional, included). | Delete. |
| `inference_providers` (org rows, A1) | Provider name, endpoint, **encrypted API key** that the user pasted (a credential belonging to the user). | `organization_id` | Metadata only, never the key. | Delete the org's provider rows with the org. Platform-managed rows (`organization_id` null) are not personal data. |
| `usage_events`, `credit_ledger`, `credit_accounts` (B2/B5) | Per-run usage with `user_id` and `organization_id`, our cost, credits. | `organization_id`, `user_id` | Yes: the user's usage rows as JSON. | **Anonymise**: set `user_id` to null. The rows are retained for bookkeeping, keyed by org, with no email. |
| `subscriptions`, `billing_customers`, `webhook_events` (B5/B9) | Provider customer id, billing email copy (if stored), plan, invoices references. | `organization_id` | Yes: plan and invoice list. | **Retain** the financial records required by tax law (typically 7 to 10 years, confirm with the lawyer review in H7), keyed by organisation, with the billing email removed on purge. Cancel the live subscription first (C9b via B9a). |
| `trial_claims` (B5) | **Hash only** of the normalised email. | `email_hash` | No (not reversible). | **Retain** (legitimate interest: fraud prevention, stated in the privacy policy). |
| `marketing_consents` and `marketing_consent_events` (saas-promotions) | Normalised `email`, optional `first_name` and `last_name` on `marketing_consents` only (never in events), consent status, wording version, source, timestamps, request `ip` and `user_agent`; the events repeat email, IP and user agent per change. | `email`, `user_id` | Yes: `marketing-consent.json`, the row and every event (GDPR 15/20). Matched by user id, or by email only once the account has verified it. | Delete the rows (with the name) and events, matched by user id, or by email only when the account verified it (an unverified account never reaches the consent of whoever subscribed with the address it typed). A row that was `OptedIn` or `Withdrawn` leaves a `marketing_suppressions` entry; `Pending` rows go outright. Brevo holds the email and name of confirmed opted-in contacts between sends (see [contact sync](../marketing-consent.md#contact-sync-and-names)); the contact is deleted there on withdrawal or erasure. |
| `run_rate_events` (launch-film-platform-gaps) | The hourly run limiter's counters: `user_id`, `project_id`, which bucket (`paid` or `free`), `created_at`. Bookkeeping only; no content. | `user_id` | No: limiter bookkeeping, pruned after a day; the runs it counts are exported as `workflow_executions`. The reason is recorded in `AccountExportSections`. | Delete the user's rows (`AccountPurgeService`). |
| `marketing_suppressions` (saas-promotions) | **Keyed HMAC-SHA256 only** of a normalised email (pepper `marketing-suppression-pepper`), plus `withdrawn_at`. | `email_hmac` | No (not linkable without the secret). | **Retain** (legitimate interest: never mail an erased address again). Removed by a later confirmed opt-in through the Inference API. |
| `feedback_reports` (T-019) | The message a user typed into "Flag a bug / Send feedback" and the context they chose to attach (page, project id, execution id, last assistant turns). **The only place user prose is stored** (product decision, 2026-10-18). | `user_id` | Yes: the user's reports as JSON, with the attached context. | Delete (`AccountPurgeService`). |
| `user_onboarding_profiles` (new-user funnel; API-owned, the Go API writes `suggested_persona` when the account is created) | What the person said about themselves on the welcome page: `persona` (who they are) and `material` (what they start from), plus `suggested_persona` (the audience page they signed up from) and `answered_at` / `skipped_at`. Closed tokens only, no free text. | `user_id` | Yes: `onboarding-profile.json`. | Delete: the row goes with the user (foreign key, `ON DELETE CASCADE`). |
| `user_attribution` (new-user funnel; API-owned, the Go API writes it when the account is created) | How the account first reached us: `source_label`, the campaign values of the link (`utm_source`, `utm_medium`, `utm_campaign`, `utm_content`, `utm_term`), `referrer_host`, `landing_path`, `first_touch_at`, how it was learned (`capture`: the consented cookie or the signup link) and `visitor_id`, the random id of the browser when the first-touch cookie was accepted. Closed-shape tokens only: no IP address, no user agent, no free text. | `user_id` | Yes: `signup-attribution.json`. | Delete: the row goes with the user (foreign key, `ON DELETE CASCADE`). After that nothing links the anonymous visitor rows of that browser to a person; they hold no user id and expire with the 13-month visitor sweep. |

### Data outside the database

| Location | Contents | Export | Erasure |
|---|---|---|---|
| Object store `projects/{projectId}/userFiles`, `agentFiles` (incl. `video-analysis`, `generated`, keyframes), `outputFiles` | Footage, renders, analysis artifacts, generated clips, rendered overlay assets. | Originals up to a cap, plus manifest. Derived artifacts are not exported. | Delete every key under `projects/{projectId}/` for each of the user's projects (list and delete by prefix, not by DB row, so orphans go too). |
| Qdrant collections `{projectId:N}_{model}_{hash8}_{dim}` | Embedded chunks of the user's file text. | No (derived). | Delete every collection that starts with the project id (same sweep the file-delete path already does). |
| Sandbox workspaces (`sandbox-executor`) | Remotion project files authored for a run; ephemeral per sandbox. | No. | Removed when the sandbox is released. Purge also asks the executor to delete any workspace labelled with the project id (best effort). |
| RabbitMQ queues and dead letters | Message bodies for in-flight executions. | No. | Expire by TTL. A purge does not touch them. Documented as accepted residue (hours to days). |
| Application and proxy logs | Email at signup and login, IP addresses, workflow ids. | No. | Retention-limited, not individually erased: 30 days for application and nginx logs (Area F sets this), stated in the privacy policy. No prompt or file content is logged. The mail senders (the Go mailer and internal relay, the Inference API's mail clients) name a recipient by a keyed `rcpt=` reference (`MAIL_LOG_REF_KEY`), not the address; see [`marketing-consent.md`](../marketing-consent.md#log-references). |
| Paddle (or Stripe, D11) | Customer name, email, address, tax id, payment method. | Paddle holds it as the merchant of record and is a separate controller for it. We link to their portal. | On account deletion we call the provider's customer-delete or anonymise endpoint where it allows it. Records they must keep stay with them. |
| SMTP provider | Delivery logs with the recipient address. | No. | Provider retention (typically 30 to 90 days), listed as a sub-processor. |
| LLM and generation vendors (Azure OpenAI, Anthropic, MiniMax, Fish Audio, ASR) | Prompts and media sent for inference. | No. | Governed by each vendor's retention terms. The privacy policy lists them as sub-processors, and the platform-managed provider contracts must state zero-retention or 30-day retention where available (F0, H7). |

### Notes the implementing packages must honour

- **Export (C9a):** exactly one export per 24 hours, built in the background, stored under a dedicated export prefix outside any project prefix, link valid 7 days, deleted by a sweep after 7 days. The export includes every row marked "Yes" above. A test asserts the exported table list equals this table, so a new personal-data table cannot be added without a decision here.
- **Purge (C9b):** order is cancel subscription, delete vendor-held voice models, delete objects, delete Qdrant collections, delete DB rows (children before parents, `decision_observations`/`workflow_step_cache_entries` by project and execution ids because they have no foreign keys), anonymise `usage_events`, delete memberships and the user. The job is idempotent and resumable: it records progress on the user row and a failed step is retried on the next sweep, not skipped.
- **Soft-deleted rows** (`edit_timelines.deleted_at`, `media_derivatives.deleted_at`) are deleted for real on purge, not left as tombstones.
- **A new table or column holding personal data** must update this map in the same PR. The C11 review checks for drift.
- **Retention outside the account lifecycle:** voice references follow `RetentionUntil` and the existing `ProjectVoiceRetentionService`. Executions and step results have no automatic expiry in v1, and the privacy policy must say so (H7).
- **Marketing consent** (saas-promotions): unconfirmed `Pending` consent rows are deleted after 30 days by `PendingConsentSweeper`. The retention period for consent proof after withdrawal is an open counsel decision; see [`marketing-consent.md`](../marketing-consent.md#before-the-first-marketing-send).
- **Records that name a user id but are neither exported nor erased** (saas-promotions): `promotions.created_by_user_id`, `promotion_redemptions.user_id`, `promotion_checkouts.user_id` (a checkout's promotion reservation), `admin_credit_grants.granted_by_user_id` and `marketing_consent_exports.exported_by_user_id`. They hold a bare user id with no foreign key, no email and no content, and the purge leaves them. They are not in `AccountExportSections`.

## What C1 to C9 need from this document

| WP | Takes from here |
|---|---|
| C1 | The state columns, token and acceptance tables (invitations come from A1, per R2). |
| C2 | Template names and the `MailData` fields (implemented in `api/services/smtp_service.go`). |
| C3 | State machine, verification TTL, uniform 202 responses, `PUBLIC_BASE_URL` links. |
| C4 | Reset TTL and side effects, `TokensInvalidatedAt` check in `middleware.Auth`. |
| C5 | The three-threat control list and the shared-IP requirement. |
| C6 | Grant on verification, one per normalised email, `trial_claims` hash. |
| C8 | Invite token equals verification, only when emails match. |
| C9a | The internal mail endpoint decision, and the export table list. |
| C9b | Purge order, retention exceptions, sole-owner rule. |

## Founder decisions recorded here

No decision in this document needs the founder before work continues. These are the defaults taken, and each can be changed here first:

1. Mail goes through the Go API, with an internal endpoint, not a second SMTP client in .NET.
2. Unverified cloud accounts are purged after 7 days.
3. Deletion grace is 30 days, cancelled by logging in.
4. Per-IP signup limit is 3 per hour and 10 per day. Minimum password length is 10.
5. Application and proxy logs are kept 30 days.
6. Billing records are retained for the statutory period. The exact number of years is confirmed in the lawyer review (H7).

## Implemented abuse controls (C5)

| Control | Where | Behaviour |
|---|---|---|
| Coarse per-IP rate limit | `nginx/nginx.conf` zone `auth_public` (5 req/min, burst 5, 429), applied in `nginx/locations.conf` by exact-match locations for `/api/v1/auth/{token,signup,forgot-password,resend-verification}` and by case-insensitive regex locations with an optional trailing slash for `/api/v1/marketing/{subscribe,confirm,unsubscribe}` | Keyed on `$client_ip_key`, a map on `$binary_remote_addr` after `realip.conf`: an IPv4 client per address, an IPv6 client per /64. With `REAL_IP_FROM_CLOUDFLARE=true` it is the visitor's address. The Go auth routes keep exact matches because gorilla/mux is case-sensitive and does not strip a trailing slash, so a variant 404s at the API. The CI job `nginx-live` runs `nginx/tests/forwarded-headers.sh` against a live container, including the path variants and the /64 key. |
| Per-IP signup cap | `middleware.SignupIPLimiter` | 3 per hour and 10 per day, counted from `application_users.signup_ip` (any replica). Answers 429 with `Retry-After`. |
| Per-email mail cap | `middleware.EmailTokenLimiter` | At most 3 emailed tokens per account per rolling hour (verification and reset together), counted from `user_tokens.created_at`. A suppressed mail is silent: the endpoint still answers its uniform 202. This replaces the 60-second/5-per-day cooldown sketched above. |
| Disposable domains | `services/disposable_domains.txt` (embedded, CC0 snapshot, licence beside it) | Signup returns 400 for a listed domain or any subdomain. `ABUSE_BLOCK_DISPOSABLE=false` turns it off. `ABUSE_DISPOSABLE_DOMAINS_FILE` was dropped: the list ships in the binary. |
| MX check | `services.CheckMX`, `ABUSE_MX_CHECK` | Off by default. Only a definite "no MX and no address record" rejects; a resolver outage allows. |
| Turnstile | `services.VerifyTurnstile`, `TURNSTILE_SECRET` | Skipped when unset. When set, signup, forgot-password and resend-verification need `turnstileToken`; a missing, wrong or unverifiable token is 400 (fail closed). |
| Verified email before running | Inference API `EmailVerificationGate` | `POST .../workflows/{id}/execute`, `.../executions/{id}/retry`, `.../assistant/execute` and the assistant `ExecuteWorkflow` tool refuse with 403 `EMAIL_NOT_VERIFIED` when the JWT carries `emailVerified: false`. A token without the claim (issued before C3, or an engine on-behalf-of token) counts as verified. |
| `TRIAL_REQUIRES_CARD` | `config.TrialRequiresCard` | Read and logged only. It needs the Paddle $0 checkout (B9b); until then it has no effect. |

## Implemented invite acceptance (C8)

Invite creation, listing and revocation are A4's (`/api/v1/orgs/{id}/invites`). C8 adds the acceptance side under `/api/v1/auth/invitations`:

| Endpoint | Auth | Behaviour |
|---|---|---|
| `GET /api/v1/auth/invitations/{token}` | none (the token is the credential) | Preview: `organizationName`, `inviterName`, `role`, `email` (to prefill signup), `expiresAt`, `accountExists`. Unknown, revoked, used or deleted-org is 404; expired is 410. |
| `POST /api/v1/auth/invitations/{token}/accept` | signed in | Delegates to `orgs.AcceptInvite` (the A4 logic): email must match (403), expired 410, then joins and re-issues the session (token and cookies) for that org. Idempotent: accepting again as an existing member is 200 with no change. |
| `POST /api/v1/auth/signup` with `inviteToken` | none | New-user path. The token proves the address, so the user is created already verified, the invite is accepted in the same transaction, no verification mail is sent, and the session is issued immediately. The disposable-domain and MX checks are skipped (a team admin chose the address); the per-IP cap and Turnstile still apply. An address that already has an account gets 409 and must sign in and accept. The user still gets a Personal workspace; the trial grant follows C6. |

Seat hook: when a membership is really created (either path), `services.NotifySeatsChanged` calls `services.SeatChanges`. The default is a no-op; B9a/B9b assign an implementation that calls the billing provider's `ChangePlanAsync` with the new seat count for seat-priced Team plans.

## Implemented OAuth sign-in (C10)

`GET /api/v1/auth/oauth/{provider}/start` and `.../callback` (provider `google` or `github`). A
provider is offered only when **both** its client id and secret are configured
(`OAUTH_GOOGLE_CLIENT_ID`/`_SECRET`, `OAUTH_GITHUB_CLIENT_ID`/`_SECRET`); otherwise both endpoints
answer **404**, which is what keeps a self-hosted install invite-only by default (D12). The protocol
endpoints, scopes and decoders are fixed in `api/handlers/auth/oauth.go` — no environment variable
can point a client secret at another host.

| Step | Behaviour |
|---|---|
| `start` | 32 random bytes of state plus a PKCE verifier, both in an httpOnly `reelbolt_oauth_state` cookie (10 minutes, `SameSite=Lax`) whose value also names the provider, then a 302 to the provider with `code_challenge` (S256). |
| `callback` | The state is compared to the cookie in constant time and the provider tag must match. The code is exchanged (with `code_verifier`) and the identity read: Google's `/userinfo`, GitHub's `/user` plus `/user/emails`. |
| Address rule | Only an address the provider reports as **verified** can sign in. An unverified one is refused and creates nothing. |
| Linking | An account that already exists for that address is linked **only when that account has verified itself** (C11 F-1, a ruling). A verified account is used as it is and nothing is rewritten: `auth_provider` keeps its value, so a linked password account stays a password account. An account that has **never** confirmed its address is refused with `oauth_account_unverified` — the address may have been signed up by somebody else first (the pre-hijack: an unverified account with a password of their choosing, which the link would keep working), so the visitor is not signed into it. No second account is created either: `application_users.email` is unique. The visitor verifies the address (or resets the password, which proves the same mailbox) and comes back. |
| New account | Created already verified, with `auth_provider = Google`/`GitHub`, no password hash, and its Personal workspace in the same transaction. The per-IP signup cap (`SignupIPLimiter`) applies to new accounts only, so an existing user is never locked out of signing in. |
| Outcome | The session cookies are set (never a JSON body — the callback must be able to redirect), then the browser goes to `/app/accept-terms` when the account has **no** recorded Terms acceptance (`legal_acceptances`), and to `/app/dashboard` otherwise. |
| Failure | Any failure redirects to `/app/login?error=<code>` with a fixed code (`oauth_failed`, `oauth_email_unverified`, `oauth_rate_limited`, `oauth_no_workspace`, `oauth_account_unverified`, `oauth_delete_blocked`); the detail is logged server-side. |

Because an OAuth first login has no signup form to accept on, consent is captured on
`/app/accept-terms`, which posts to:

| Endpoint | Auth | Behaviour |
|---|---|---|
| `POST /api/v1/auth/accept-terms` | signed in | Records the `Terms` and `Privacy` acceptances for the caller at **the versions this deployment serves**, read from `legal_documents`, and re-issues the session, so the JWT `termsVersion` claim and the readable `reelbolt_user` cookie carry what was just recorded. The body has to name those same versions; anything else is refused with **409** and writes nothing, and a deployment that cannot state a version is **503** rather than a guess (F-7). Re-posting the same version writes no second row; a newer version is a new row, and the older one stays as part of the record. |

That endpoint closes the gap C7-UI recorded: before it, `legal_acceptances` was written only by
signup and invite signup, so an already-signed-in user could record consent in the readable cookie
alone. The cookie now carries `termsVersion` because the API set it, and `web/proxy.ts`'s
re-acceptance redirect is live for any session that carries a version — an absent field still means
"this session predates the gate" and is left alone, while a first login with nothing accepted is
routed to `/accept-terms` by the callback itself.

### The recorded version is the server's, not the caller's (F-7)

Signup and `/auth/accept-terms` used to take the accepted version **from the request body**, and
validate only that it was non-empty and at most 32 characters. A caller could therefore record
acceptance of any string at all — a version that was never served, or a future one — and
`web/proxy.ts` compared that same self-asserted value against a `NEXT_PUBLIC_TERMS_VERSION` build
constant, so both sides of the comparison were values the browser could produce. `legal_acceptances`
is the evidence artefact for "this user agreed to this text", so it was evidence of nothing.

The versions now live in `legal_documents`, which the **Inference API owns and migrates** (migration
`AddLegalDocuments`; the Go API reads it by SQL and never migrates it, like `organizations` and
`platform_settings`):

| Column | Meaning |
|---|---|
| `document` | `Terms` or `Privacy` — the primary key, stored by name. |
| `version` | The version this deployment currently serves. |
| `effective_at` | When it became current. This is the instant acceptances of the **previous** version stopped being current, which is what makes the audit question answerable. |
| `updated_at` / `updated_by` | When it was published and by whom (`config` for the first-boot seed, otherwise the platform admin's email). |

The row is seeded on first boot from `Legal:TermsVersion`/`Legal:PrivacyVersion` (default
`2026-10`, the version `web` shipped as its build-time default, so an upgrade re-prompts nobody) and
**never overwritten by a restart**. A new version is published with
`PUT /api/v1/legal/documents/{document}` (platform admin only — it changes what every tenant is
asked to accept), and `GET /api/v1/legal/documents` answers anonymously, because the proxy gate runs
before anything is authenticated.

| Caller | Answer |
|---|---|
| Names the served versions | The acceptance is written **at those versions**, read from the database, and the session is re-issued. |
| Names anything else | **409** — well-formed, but the documents moved underneath it. The body carries `currentTermsVersion`/`currentPrivacyVersion`, and signup, `/accept-terms` and both forms adopt them, clear the consent checkbox and ask again. Nothing is written. |
| Named nothing (empty) | The same 409. "Empty" is not a version the deployment serves. |
| The deployment cannot state a version | **503**, and nothing is written. A missing row is "the server cannot say what it serves", never `""` — an empty string would be a version the API could compare against. |

`legal_acceptances.version` can now only ever name a version the deployment actually served, because
the Go handler does not use the submitted string for anything except equality. The JWT claim and the
readable cookie still carry `termsVersion` — **kept deliberately**: the proxy gate needs to know what
*this session* accepted without a database read per navigation, and the value is written by the Go
API from the database, so it is server-established rather than client-asserted. The record of truth
remains the row.

`web/proxy.ts` compares the cookie against what `GET /api/v1/legal/documents` reports **now**
(`web/lib/auth/legal-server.ts`, 30-second answer reuse), so a publication takes effect for
already-signed-in sessions without a re-login and without a rebuild. A read that does not answer
leaves the session alone: the gate moves people it *knows* are behind, and an API outage must not
lock every session out of the dashboard.

**Answering "which version did this user accept, and when" is a query**, not a support ticket:

```sql
select u.email, a.document, a.version, a.accepted_at,
       d.effective_at, d.updated_by
  from legal_acceptances a
  join application_users u on u.id = a.user_id
  left join legal_documents d on d.document = a.document
 where u.email = 'someone@example.com'
 order by a.document, a.accepted_at;
```

Web side: `web/components/auth/OAuthButtons.tsx` renders one plain anchor per configured provider on
`/login` and `/signup`. Which providers are configured is asked of the API itself
(`GET /api/v1/auth/oauth/providers`, read by `web/lib/auth/oauth-server.ts` with a 30-second cache),
so a button exists exactly when its endpoints answer; the `OAUTH_GOOGLE_ENABLED`/`OAUTH_GITHUB_ENABLED`
flags are only the fallback for when the API cannot be asked. The signup page shows the buttons only
when the API reports `createsAccount` (signup is open). The login page renders the `?error=` code the
callback sends back.

**Provider sign-in respects invite-only.** With `SIGNUP_ENABLED` off, the callback still links a
verified existing account, but it never creates one: an address with no account is sent back with
`?error=oauth_no_account` ("ask your workspace admin to invite you"), exactly as
`POST /api/v1/auth/signup` answers 404 on such a deployment. Before this rule a Google or GitHub
sign-in was a way around an invite-only server.

## Privacy acknowledgement and privacyVersion

Added by saas-promotions, when the Privacy Policy changed to cover marketing consent. Terms and
Privacy can now move independently, and they are treated differently: accepting Terms is
agreement, while a new privacy notice is information that the user acknowledges.

**The session carries both versions.** Go writes `privacyVersion` next to `termsVersion` in the JWT
claim, the readable `reelbolt_user` cookie and `TokenResponse` (and in the MCP OAuth access token).
The value is `services.LatestPrivacyVersion`: the newest Privacy version the user has **accepted**,
never the one currently served. It is omitted when the user has accepted none.

**The gate.** `web/proxy.ts` asks `legalChangeFor(cookie, served)`:

| Session | Answer |
|---|---|
| No `termsVersion` | Left alone, as before. |
| `termsVersion` behind the served Terms | `terms`: redirect to `/accept-terms`, which asks for explicit acceptance (a required checkbox, "Accept and continue"). |
| Terms current, `privacyVersion` behind the served Privacy | `privacy`: redirect to `/accept-terms`, which shows the acknowledgement variant. |
| No `privacyVersion` | Never gated for Privacy. A session issued before the deploy is re-checked at its next sign-in, when Go issues the claim. |

**The acknowledgement variant** has the heading "We've updated our Privacy Policy", a one-line
summary, a link to the policy and a **Continue** button. There is no required checkbox and no "I
agree". It posts the same `POST /api/v1/auth/accept-terms`, so the record is a `legal_acceptances`
row at the served Privacy version, which is what the F-7 audit query above returns. If the 409
refusal shows that Terms moved too, the page switches to the acceptance variant.

**Both variants** carry the optional, unticked marketing checkbox. Its `PUT
/api/v1/marketing/consent { source: "acceptTerms" }` runs only after the acceptance succeeded,
and its failure never keeps the user on the page.

**Never blocked by the gate:** `/accept-terms` itself and `/settings/data` (where a person downloads
their data or deletes their account), the only two entries in `legalGateExempt` (`web/proxy.ts`).
The rest of `/settings` stays gated. Sign-out (`/api/auth/logout`), `/unsubscribe` and
`/marketing/confirm` are public paths, so the gate never reaches them. The page links to sign-out
and to `/settings/data` (`LegalGateExits`).

**The version bump.** Migration `AddPromotionsAndMarketingConsent` sets the `legal_documents`
Privacy row to `2026-10-12` only where it still holds the shipped default `2026-10`
(`updated_by = 'migration:AddPromotionsAndMarketingConsent'`); an operator-published version is
never touched. `LegalDocumentSeeder.DefaultPrivacyVersion` is `2026-10-12` for fresh installs, and

On 2026-10-26 (launch alpha) migration `LaunchAlphaLegalVersions` moved Terms to `2026-10-26` = a
re-acceptance (contract party is now a named person, new 14-day refund promise, forum clause) and
Privacy to `2026-10-26` with it, shown together on `/accept-terms`; the Terms change drives the
re-acceptance. A future Privacy-only bump stays an acknowledgement.
Terms stays `2026-10`. The site's `LAST_UPDATED` moved with it.

The consent store itself is described in [`marketing-consent.md`](../marketing-consent.md).

## Implemented export and deletion (C9)

Two halves, split the way the plan splits them.

**Export (C9a)** is the Inference API's. `POST /api/v1/account/export` queues a build;
`AccountExportService` writes a zip under `exports/{userId}/{exportId}.zip`, mints a seven-day
presigned link against the public object-store endpoint and mails it through the Go API's internal
endpoint (the decision recorded above). `GET /api/v1/account/export` returns a fresh fifteen-minute
link while the archive is still inside its seven days, which is what makes the feature usable on a
self-host with no SMTP. `GET /api/v1/account/export/status` is what the dashboard's **Your data** card
renders: `preparing` (a request newer than the newest archive, bounded to two hours so a failed build
does not read as "preparing" forever), the newest archive's `createdAt`/`sizeBytes`/`expiresAt`, and
`nextRequestAt` while the once-a-day throttle holds. It never carries a link. The throttled clock is the `exports/{userId}/request.json` marker object's
`LastModified`: no table, no migration, and it survives a restart or several API replicas. A sweeper
deletes archives older than the link lifetime.

**Deletion (C9b)** is split across the two services because of what each one can reach. Go owns the
request — `POST /api/v1/auth/account/delete` (`api/handlers/auth/account.go`) confirms that the
caller controls the account, refuses a sole Owner of a Team that still has members (`409 sole_owner`
with the workspace's name), stamps `deletion_requested_at` and `tokens_invalidated_at` together,
drops both session cookies and sends `deletion_scheduled`. Signing in during the grace period clears
the flag (`cancelPendingDeletion`), on the password login **and** on the Google/GitHub callback — an
account created by a provider has no other way to sign in, so before the callback called it such an
account's deletion could not be cancelled at all. The client is told: the token response carries
`deletionCancelled: true`, and the callback appends `?deletion=cancelled` to the page it lands on;
the dashboard shows a notice for either (`web/components/auth/DeletionCancelledNotice.tsx`). The
dashboard's own UI for the export and the deletion is the **Your data** section of Account settings
(`web/components/settings/DataExportCard.tsx`, `DeleteAccountCard.tsx`). The Inference API owns the purge
(`AccountPurgeService`), because the rows and the blobs are its to remove.

**Two ways to confirm, and neither is a session alone** (C11 F-8). A password in the body is checked
against the account exactly as before — a wrong one is still `401`. An account that signs in with a
provider has no password to re-enter, so a request with **no** password is answered `409` with
`provider_confirmation_required`, the provider to name and the authorization URL of a fresh round
trip `GET /api/v1/auth/oauth/{provider}/start` would not carry; nothing is scheduled and no session
is touched. Completing that round trip at the provider — which authenticates **the very identity the
account was created from**, the equivalent of re-entering a password — is what schedules the deletion,
and the callback then redirects to `/app/login?deletion=scheduled&date=<RFC 3339 purge date>`, the
same landing the dashboard uses after a password confirmation. The intent lives in the
`reelbolt_oauth_state` cookie, which only that authenticated request sets, so a cross-site
navigation (or a stolen session on its own) can neither arm nor complete it; the identity's verified
address must equal the account's, so a round trip that authenticates somebody else is refused with
`oauth_failed` and deletes nothing. An account whose provider is not configured on this deployment
is answered `409 confirmation_unavailable` with the route that does work — set a password through
the reset link, then confirm with it.

Two things the plan left open, decided here:

- **When the subscription is cancelled.** The plan puts it on the request; the billing tables are only
  reachable from .NET, so the sweep does it on its **first pass over a deletion-requested account**
  (within one `Account:Purge:IntervalMinutes`, an hour by default), not thirty days later. The
  content half still waits for the grace period.
- **What the purge can cancel.** `IBillingProvider` (B9a) exposes no cancellation operation — the
  provider's portal and the webhook are the cancellation surfaces (see `docs/billing.md`) — so
  `AccountBillingCanceller` cancels the internal Trial subscription, which has no provider behind it,
  and **reports** a live provider subscription instead of pretending it is gone. It appears in the
  purge log and in the run result as `SubscriptionsRetainedForVendor`, and the user guide tells the
  reader to cancel a paid plan from the billing portal. Wiring that call is B9b's (or B12's), not a
  second Paddle client here.

**Order and resumability.** Cancel the subscription, ask the vendors to delete cloned voice models,
delete the objects under each project prefix, delete the Qdrant collections, delete the rows
children-first (`workflow_step_cache_entries`, `decision_observations` and
`external_generation_jobs` by project and execution id, because they carry no foreign key), then the
memberships, the workspace and the user. Every step is idempotent and the user row is the worklist:
**if anything fails, nothing in the database is touched and the same account is retried on the next
sweep.** One consequence is deliberate and worth stating: the purge runs once per sweep, so a failure
of a *later* sweep re-deletes objects that are already gone, which the object store answers
idempotently. The object store is not part of the database transaction, so a failed sweep may already
have deleted the blobs; that is safe because the *rows* survive, and the prefix is re-listed — an
already-empty prefix is simply nothing to delete.

**All of the row deletion is one transaction, and that is load-bearing rather than tidy.** Children,
memberships, workspace and user go in a single `SaveChangesAsync`. The memberships are also the only
handle the sweep has on the workspace (they resolve the organizations, and through them the projects,
the object prefixes and the vector collections), so committing them *ahead* of the rest — which is
what the two-commit version did — leaves a retry that resolves no workspace, no project and no
object, deletes the user row anyway and returns `Purged = true` with the content still there and
nothing left to retry it. That was C11 F-2, and `AccountPurgeTransactionTests` pins it. A batch the
object store answers with per-key `Delete` errors defers for the same reason: the rows are the only
map back to a surviving object.

**Corrections to the data map above**, found while implementing it:

- `external_generation_jobs` stores **no prompt** — the model has `ClipId`, `RequestHash`,
  `RemoteJobId`, costs and the clip's storage key. The prompt lives in the step result, which the
  export already carries; the section exists so the job record itself is not lost.
- `billing_webhook_events` is exported **without `PayloadJson`**: the raw payload repeats the
  provider's own record and can carry payment-method detail that is theirs to hand over. What we did
  with each event — id, type, timestamps, attempts, outcome — is ours, and that is the section.
- **Sandbox workspaces** are the one item on the map the purge does **not** reach: the Inference API
  has no client for the sandbox executor (the WorkflowEngine owns it), so a workspace left behind by a
  crashed run expires with its container instead. Recorded here as accepted, not forgotten.

**Tests.** `AccountExportSectionsTests` pins the exported table list against this document's map in
both directions (a table in the map must be exported or explicitly excluded with a reason), so adding
personal data without a decision here fails the build. `AccountExportServiceTests` (mocked
`IAmazonS3`) assert one document per section, the packing cap, the manifest and the throttle;
`AccountPurgeServiceTests` assert the rows and blob keys go, usage is anonymised rather than deleted,
a shared team workspace survives, the grace period is honoured and a failed step defers the whole
account instead of half-deleting it. `AccountPurgeTransactionTests` drives the real service against
a provider that honours transactions (Sqlite always; a real PostgreSQL on a throwaway database when
`REELBOLT_TEST_PG` is set) with the failure injected *inside the database* — a one-row probe table
whose foreign key refuses the delete of the account row — so the rollback, and not merely the
ordering, is what the test observes.
