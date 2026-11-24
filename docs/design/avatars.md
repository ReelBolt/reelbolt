# Avatars: a consented likeness a video model can use

Status: approved 2026-11-05 by the product owner as written (every `AV-*` decision at its recommended option,
see "Decisions for the owner"); written 2026-11-05 before any build. Phase 1 is built; phase 2 is built behind
`Avatars:Generation` (default `Off`) with the gaps listed in [`../avatars.md`](../avatars.md) "Generating a clip
with an avatar" (C2PA not signed, must-measure items unverified); phase 3 is built behind the same switches on
fal.ai (default talking head `minimax/h3-max/lip-sync/image-to-video`, registered under the `LipSync` capability, still model `google/nano-banana-2.1/edit`,
the still made inside the `VideoGenerate` executor; see [`../avatars.md`](../avatars.md) "The still-then-animate path
(phase 3)"), never called live. Phase 1 (the foundation) is specified in
full; phases 2 (MiniMax generation) and 3 (cheap still-then-animate) are specified only as far as they
constrain phase 1. Internal document (not published). Legal statements here are research notes for a lawyer,
not conclusions. Decisions are lettered `AV-1` ... `AV-13`; all were taken on 2026-11-05 (see the table).

Builds on: the voice library and its consent model ([`../voiceover.md`](../voiceover.md) "Voices and
consent"), the `VideoGenerate` step and its ledger ([`../video-generation.md`](../video-generation.md)),
egress consent, content moderation ([`../content-moderation.md`](../content-moderation.md)), the CSAM
control ([`../csam.md`](../csam.md)) and tenancy ([`../multi-tenancy.md`](../multi-tenancy.md)).

## Goal, scope and non-goals

**Goal.** A customer can turn a real person (themselves, a founder, a presenter who agreed) into a reusable
**avatar**: a named set of reference pictures, reference videos and a voice, held under the same kind of
recorded consent, retention, revoke and delete rules a cloned voice has today. A video model that accepts a
person as input can then put that person in a generated clip, and a later, cheaper path (make a still of the
person, then animate it from the narration) uses the same avatar without a redesign.

The product owner's words: "like voice cloning, but for video models". The analogy is exact for consent and
lifecycle, and loose for the vendor side: most video vendors keep **no reusable avatar id**; the avatar is
our record plus our copies of the media, and each generation request re-sends the media it needs (see
"Vendor facts").

**Phase 1 (this design, buildable now):**

- The avatar entity: tables, storage, the one `IsSelectable` rule, consent capture, revoke, delete with a
  per-outcome report, the retention sweep, an audit trail.
- Reference media checks at attach time (moderation, CSAM, held outputs, limits).
- An **Avatars** tab next to **Voices**, in English and Italian.
- The engine-side resolver, wired into nothing that spends money yet, plus a usage column on the ledger.
- One read-only assistant tool.

**Non-goals for phase 1:** generating any clip with an avatar; a preview render; a vendor-side "avatar" or
"character" registration; face detection, face matching or age estimation; avatars shared across projects
or organisations; minors; public-figure lookalikes; lip-syncing existing footage; the assistant creating,
revoking or deleting an avatar.

## What already exists and is reused

| Existing piece | Where | How avatars use it |
|---|---|---|
| Voice consent model | `ProjectVoice` (`ReelBolt.Shared/Data/Models/ProjectVoice.cs`), `ProjectVoicesController` | Same columns (attested by/at, verbatim statement, subject name, revoked by/at, retention), same one read-path rule, same `400` that returns the required statement |
| "Unselectable fails, never falls back" | `IProjectVoiceResolver`, `VOICE_CONSENT_MISSING` / `VOICE_NOT_FOUND` | `IAvatarResolver` copies it: an unusable avatar fails the step, it never becomes "no avatar" |
| Retention sweep | `ProjectVoiceRetentionService` (6 h, revoked grace 30 days, 200 per sweep, `ProjectVoices:RetentionSweepEnabled`) | `ProjectAvatarRetentionService`, same constants and the same staging kill switch |
| Linked voice | `project_voices` | An avatar may point at one `ProjectVoice` of the same project; the voice keeps its own consent and lifecycle |
| Upload path | direct upload, import from link, cloud and folder sync | Reference media are ordinary project files first, so they get the CSAM scan, ingest moderation, probes and derivatives for free |
| Ingest moderation and CSAM | `ContentModerationWorker`, `FileModerationStatus`, `CsamScanStatus`, `OutputGate.HeldFileIdsAsync`, `PublishModeration.GateStoredCopyAsync` | Attach-time checks, stricter than voices (see "Consent and safety") |
| Generation ledger and budget gate | `external_generation_jobs`, `IVideoGenerationBudgetGate` | Phase 2 uses it unchanged; phase 1 adds one nullable `avatar_id` column (engine migration) so usage can be reported |
| Egress consent | `VideoGenerateStepConfig.AllowSourceMediaEgress`, `EGRESS_REFUSED`, `AssistantEgressConsentGuard` | Sending a person's media to a vendor still needs the step's egress consent, which the app UI turns on, or the assistant on the user's request (approval card in chat; never over MCP) |
| Image input declaration | `IVideoImageInputSupport` (pure, per model) | Model for a sibling declaration, `IVideoReferenceInputSupport` (phase 2) |
| Plan gates | `PlanGate` features (`video_generation`, `audio_cleanup`, ...), `plans.catalog.json` | One new feature, `avatars` |
| Project access | `IProjectAccessService` (404 across orgs, 403 low role) | Every avatar route; no new tenant filter needed (see "Data model") |

## Vendor facts (checked 2026-11-05)

Checked on 2026-11-05 against the vendors' own pages (MiniMax pages read in full that day; fal model pages
read that day). Earlier repository research: an operator document
(2026-10-12) and [`../video-generation.md`](../video-generation.md) "MiniMax facts". Where a row says "not
found", nothing official was found; it is not a statement that the feature does not exist.

**The product owner's belief holds, with two precisions.** MiniMax's v2 endpoint does take pictures, video
and audio of a person together in **one** request, but only in its *reference* mode, which cannot be combined
with a first or last frame; and nothing on the vendor side is a reusable "avatar": an uploaded file id
expires after 7 days.

| # | Fact | Source |
|---|---|---|
| M1 | `POST /v2/video_generation` (models `MiniMax-H3`, `MiniMax-H3-Highspeed`, `MiniMax-H3-Lite`, `MiniMax-H3-Max`) takes a `content[]` array of `text`, `image_url`, `video_url` and `audio_url` items, each with a `role` | [v2 create][mm-create], [guide][mm-guide] |
| M2 | Reference roles and counts per request: `reference_image` ≤ 9, `reference_video` ≤ 3, `reference_audio` ≤ 3; at most 12 files and a 64 MB body | [v2 create][mm-create] |
| M3 | **Frame mode and reference mode are mutually exclusive**: `first_frame`/`last_frame` cannot be sent with any `reference_*` item; inside reference mode any mix of images, videos and audio is allowed | [v2 create][mm-create] |
| M4 | Reference image: JPG, PNG, WEBP, HEIC, HEIF; ≤ 30 MB; 256..5760 px a side; aspect 0.4..2.5 | [v2 create][mm-create] |
| M5 | Reference video: MP4 or MOV, H.264/H.265, ≤ 50 MB, 2..15 s each and ≤ 15 s in total, 23.976..60 fps | [v2 create][mm-create] |
| M6 | Reference audio: WAV or MP3, ≤ 15 MB, 2..15 s each and ≤ 15 s in total | [v2 create][mm-create] |
| M7 | There is no "character" or "avatar" field: `reference_image` is the subject reference, which the guide says keeps "character, motion, camera, style, or voice" consistent. No avatar, digital-human or talking-head API is in the official docs index; the guide does not mention lip sync | [guide][mm-guide], [docs index][mm-index] |
| M8 | Upload File with purpose `video_generation_input` returns a `file_id`, used as `mm_file://{file_id}`; the file is **valid for 7 days**, then generation fails with "file expired" | [file upload][mm-file] |
| M9 | Prices per output second: H3 768P $0.08, 2K $0.13; H3-Highspeed 768P $0.06 / 480P $0.04; H3-Lite 768P $0.04 / 480P $0.025; H3-Max 768P $0.08 / 480P $0.05. **Input video is billed per second** (at the output rate; H3-Max $0.143 / $0.0553); reference images beyond the first 5 cost $0.04 (Highspeed $0.03, Lite $0.02), on H3-Max beyond the first 2 cost $0.074; input audio is free | [pricing][mm-pricing] |
| M10 | The legacy v1 `S2V-01` "subject reference" takes **one** image of **one** character, no video, no audio; not on the pricing page | [S2V][mm-s2v] |
| M11 | MiniMax voice cloning exists (`/v1/voice_clone`, 10 s..5 min audio); "if a cloned voice is not used within 7 days, the system will delete it"; no speaker-consent step is documented | [voice clone][mm-vc] |
| M12 | The open H3 weights' licence **excludes the EU**, UK, US and South Korea (no right to use the weights there, outputs barred there), so a self-hosted H3 on our ComfyUI arm is not an option from Italy. Its AUP bans impersonation without consent and requires disclosure of public machine-generated output | [H3 licence][mm-lic], [local deploy][mm-local] |
| M13 | No likeness or deepfake clause was found in MiniMax's general terms; the platform-specific terms were not read (to do before launch) | [ToS][mm-tos] |
| M14 | The repository today offers only `MiniMax-H3` and `MiniMax-H3-Max` (`Describe`) and sends only the frame mode (`IVideoImageInputSupport` = `FirstAndLastFrame`) | `MiniMaxVideoGenerationClient.cs` |

**Cheap still-then-animate candidates** (fal model pages, 2026-11-05; "commercial" is fal's tag for a partner
model, the vendor's own licence text was not shown):

| Endpoint | Inputs | Price | Notes |
|---|---|---|---|
| `minimax/h3-max/lip-sync/image-to-video` ([page][fal-h3ls]) | image + audio (≥ 5 s, cut at 15 s) | $0.05/s 480p, $0.08 768p, $0.16 1080p, $0.255 2K | commercial; page says the uploader must hold rights to face and voice |
| `fal-ai/kling-video/ai-avatar/v2/standard` ([page][fal-kling]) | image + audio (+ prompt) | $0.0562/s | commercial (partner) |
| `veed/fabric-1.0` ([page][fal-fabric]) | image + audio | $0.08/s 480p, $0.15/s 720p | commercial (partner) |
| `fal-ai/creatify/aurora` ([page][fal-aurora]) | image + audio | $0.07/s 480p, $0.14/s 720p, whole seconds | commercial (partner) |
| `fal-ai/bytedance/omnihuman/v1.5` ([page][fal-omni]) | image + audio | $0.16/s | commercial (partner), most expensive |

Not shortlisted: `fal-ai/infinitalk` (deprecated), `fal-ai/wan/v2.2-14b/speech-to-video` (deprecation
2027-01-04), `fal-ai/sync-lipsync/v2` (re-lips existing *video*, a different consent, see "Risks").
Higgsfield: no speak or avatar endpoint found in its official docs (models are listed only in its console).
Self-hosted on ComfyUI: Wan2.2-S2V-14B (Apache-2.0, image + audio), InfiniteTalk (Apache-2.0 README, image +
audio) and MuseTalk (MIT code, weights "for any purpose") are the open options; each would be a new vetted
template under the "only vetted graphs" rule.

**What this means for the design.**

- Our storage is the master copy of every reference; MiniMax file ids (7 days) are a per-run optimisation at
  most, recorded in `project_avatar_vendor_assets` so delete can remove them. Phase 2 may simply send data
  URIs per request (images) and upload videos per run.
- Our reference limits are set inside MiniMax's envelope (M2, M4..M6) so a ready avatar is never refused by
  the vendor for a reason we could check first.
- An avatar step cannot also take a first frame on MiniMax (M3): the reference declaration carries
  `ExclusiveWithFrames`, and the save validator refuses `AvatarId` together with `FirstFrame`/`LastFrame`.
- Reference video is the expensive input (M9): the estimate must price it and the UI must say so.
- A linked voice cloned with Fish is not a MiniMax voice; reference **audio** of the person (M6) is what
  carries the voice into a MiniMax clip, so an avatar keeps its own short speech samples besides the link.
- Nobody upstream verifies consent (M11, M13, fal's page): it is entirely ours, which is why it fails closed.

[mm-create]: https://platform.minimax.io/docs/api-reference/video-generation-v2-create.md
[mm-guide]: https://platform.minimax.io/docs/guides/video-generation.md
[mm-index]: https://platform.minimax.io/docs/llms.txt
[mm-file]: https://platform.minimax.io/docs/api-reference/file-management-upload.md
[mm-pricing]: https://platform.minimax.io/docs/pricing/overview.md
[mm-s2v]: https://platform.minimax.io/docs/api-reference/video-generation-s2v.md
[mm-vc]: https://platform.minimax.io/docs/api-reference/voice-cloning-clone.md
[mm-lic]: https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/LICENSE
[mm-local]: https://platform.minimax.io/docs/guides/local-deploy-h3.md
[mm-tos]: https://www.minimax.io/terms-of-service-v2.html
[fal-h3ls]: https://fal.ai/h3-max-lip-sync
[fal-kling]: https://fal.ai/models/fal-ai/kling-video/ai-avatar/v2/standard
[fal-fabric]: https://fal.ai/models/veed/fabric-1.0
[fal-aurora]: https://fal.ai/models/fal-ai/creatify/aurora
[fal-omni]: https://fal.ai/models/fal-ai/bytedance/omnihuman/v1.5

## Data model

**Scope: project, not organisation (recommended, `AV-1`).** An avatar belongs to exactly one project, like a
voice. Reasons: the consent statement names a purpose ("videos in this project") that a person can
understand and withdraw; deletion cascades with the project; access is the project's access, so no new
tenancy rule is needed; and the voice it links to is project-scoped too. The cost is that a company with a
spokesperson in ten projects attests ten times. An org-level library (like the brand library,
`design_system_libraries`) is the later option: it would need the named `Tenant` query filter, because the
library id is caller-chosen, and an org-wide consent wording. Phase 1 keeps the door open by keeping the
consent and media rows free of anything project-specific except the foreign key.

All tables are **owned by the Inference API** (one migration, `AddProjectAvatars`, history
`__EFMigrationsHistory_Api`). The WorkflowEngine maps `project_avatars` and `project_avatar_references`
read-only (`ExcludeFromMigrations()`), exactly like `project_voices`. The engine's one write in phase 1 is a
column on its own table (below). `ApiMigrationOwnershipGuardTests` must stay green: the API migration
touches no engine-mapped table.

**Tenancy.** None of the three tables is reached by an id alone: every route is
`/projects/{projectId}/avatars/...` and loads the project through `IProjectAccessService` first, then
queries `WHERE project_id = @p`. By the documented rule (a table addressable by a caller-chosen id carries
the filter; one reached only via an authorized parent is guarded at the parent) they carry **no** `Tenant`
filter and are added to the unfiltered list in `TenantBoundaryInventoryTests` with that reason. If `AV-1`
picks org scope, `project_avatars` becomes an org table and must carry the filter.

### `project_avatars`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `project_id` | uuid FK `projects` `ON DELETE CASCADE` | indexed |
| `name` | text, 1..200 | display name, unique per project among live rows (partial unique index `WHERE consent_revoked_at IS NULL`, so a revoked avatar awaiting the sweep does not block its name) |
| `description` | text ≤ 2000, null | shown to the user and, in phase 2, to a planner as plain text (sanitized, never an instruction) |
| `project_voice_id` | uuid FK `project_voices` `ON DELETE SET NULL`, null | must be a voice of the same project (checked in code; a CHECK cannot see the other row) |
| `consent_attested_by_user_id`, `consent_attested_at` | uuid, timestamptz | set together, never null on a live row |
| `consent_statement` | text | verbatim, as accepted |
| `consent_statement_version` | text | e.g. `avatar-consent-2026-11` (see `AV-6`) |
| `consent_locale` | text | `en` or `it`, the language the statement was shown in |
| `consent_subject_name` | text 1..200 | the person, as the user typed it |
| `consent_basis` | text enum `Self` / `WrittenPermission` | append-only, stored by name |
| `consent_subject_adult` | bool | must be `true` to create (minors refused) |
| `consent_document_file_id` | uuid FK `project_files` `ON DELETE SET NULL`, null | optional signed release (PDF or image); **never sent to any vendor**, excluded from every egress path (`AV-4`) |
| `consent_revoked_at`, `consent_revoked_by_user_id` | null | revoke stamps both, once |
| `retention_until` | timestamptz | 1..730 days from attestation, default 365 |
| `disclosure_label` | text enum `Visible` / `MetadataOnly` | default `Visible`; see "AI Act disclosure" |
| `created_at`, `created_by_user_id`, `updated_at` | | |
| `xmin` (`RowVersion`) | xmin concurrency token, a system column (no real column is created) | revoke versus re-attest races |

**The one read-path rule**, `ProjectAvatar.IsSelectable(now, references)`: consent attested, not revoked,
`retention_until > now`, at least one usable **image** reference, and every reference the avatar lists is
still present and not held. `UnselectableReason` returns, in this order: `consent_missing`,
`consent_revoked`, `retention_expired`, `reference_missing`, `reference_held`, `no_image_reference`. The API
`selectable` flag, the web picker and the engine resolver all call it, so they cannot disagree. A linked
voice that has become unselectable does **not** make the avatar unselectable; it makes the avatar's voice
unusable, and phase 2 fails a step that needs that voice with the voice's own code.

### `project_avatar_references`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `avatar_id` | uuid FK `project_avatars` `ON DELETE CASCADE` | |
| `project_file_id` | uuid FK `project_files` `ON DELETE NO ACTION` | the file is the media; unique per avatar; see below |
| `kind` | text enum `Image` / `Video` / `Audio` | from the file's MIME, checked |
| `role` | text enum `Face` / `HalfBody` / `FullBody` / `Motion` / `Speech` | append-only; a hint for phase 2 and 3 mapping, never sent as free text |
| `ordinal` | int | user order; the first `Face` image is the "primary" picture |
| `content_sha256` | text | digest at attach time; a changed object is `reference_missing` until re-attested |
| `width`, `height`, `duration_ms` | int, null | from the ingest probe (`source_probe_json`) |
| `attached_at` | timestamptz | |

**References are project files, linked, not copied (`AV-3`).** Uploading through the normal paths gives each
picture or video the CSAM scan, moderation, the probe and derivatives with no new code. The link is
`ON DELETE NO ACTION` (built in A1; not `RESTRICT`, because `RESTRICT` is checked immediately and could fail a
project delete that cascades to both the file and the avatar in either order, while `NO ACTION` is checked at
the end of the statement), so a reference cannot disappear from under a live avatar: deleting the file is refused
with `409 FILE_IN_USE_BY_AVATAR` naming the avatar, the same shape as `PICTURE_COPIES_OVER_QUOTA`. Deleting
the avatar deletes its reference files (as deleting a cloned voice deletes its recordings), after listing any
other use of them (timelines, pictures) in the confirmation dialog. Files used as references are marked
`project_files.source_kind = 'avatar_reference'` (existing column, new value) so the media pool can badge
them "Avatar: <name>", and so `ListProjectFiles` can tell the assistant not to use them as ordinary footage.

**Limits (ours, inside MiniMax's envelope, "Vendor facts" M2 and M4..M6):** at most 8 images, 3 videos and
3 audio files per avatar; images JPEG/PNG/WebP (HEIC/HEIF refused, as the MiniMax client already does,
because no header reader checks their size), ≤ 30 MB, 256..5760 px a side, 0.4..2.5 aspect; videos MP4 or MOV,
H.264/H.265, 2..15 s and ≤ 50 MB each, 23.976..60 fps; audio WAV or MP3, 2..15 s and ≤ 15 MB each;
20 avatars per project. A phase 2 request may still have to pick a subset (≤ 15 s of video and of audio in
total, ≤ 12 files); the per-model declaration picks it in ordinal order and says so in the manifest. Every number is a named constant in
`ProjectAvatarLimits` and is returned by `GET .../avatars/requirements`.

### `project_avatar_vendor_assets` (shape fixed in phase 1, written from phase 2)

Some vendors let a file be uploaded once and referenced by id (MiniMax `mm_file://{file_id}`, see "Vendor
facts"). When phase 2 uses that, the id must be on record so that delete and retention can remove it.

| Column | Notes |
|---|---|
| `id`, `avatar_id` (FK cascade), `reference_id` (FK cascade, null) | |
| `model_capability_id` | the `VideoGeneration` capability the upload belongs to (the provider account) |
| `remote_asset_id` | vendor's id; never shown to the assistant (`AssistantReplySanitizer` already strips ids) |
| `uploaded_at`, `vendor_expires_at` | the vendor's own retention, when documented |
| `deleted_at`, `delete_outcome` | `deleted`, `already_gone`, `vendor_refused`, `vendor_error`, `not_supported` |

Phase 1 creates the table so delete and retention code is written once against its final shape; it stays
empty until phase 2. A vendor asset row is never reused after its avatar's consent is revoked.

### `project_avatar_events` (append-only audit)

One row per `created`, `references_changed` (re-attested), `consent_revoked`, `deleted`,
`retention_deleted`, `vendor_asset_deleted`, `used_in_run` (phase 2): actor user id (null for the sweeper),
time, avatar id (no FK, so rows survive the avatar), project id, organisation id, statement version, and a
small JSON of outcomes (counts and outcome words only; never media, URLs, storage keys or the subject's
name). It is the accountability record the voice table keeps only in its own columns, and it survives the
delete so "we deleted it on this date" can be proven. Retained 3 years, then purged by the same sweeper
(`AV-7`: confirm the period with counsel).

### Engine column

Engine migration `AddExternalGenerationJobAvatar`: `external_generation_jobs.avatar_id uuid null` (no FK,
the engine does not own avatars). Written in phase 2 on each take that used an avatar; read by
`GET .../avatars/{id}/usage`. Added in phase 1 so the usage endpoint and the delete dialog have a stable
source.

### Storage

No new storage prefix for media: references stay where their project files are. The optional consent
document is a project file too. Vendor uploads (phase 2) leave our storage only as request bodies or vendor
uploads. Phase 3 stills land under `projects/{p}/agentFiles/avatars/{avatarId}/stills/` and are deleted with
the avatar.

## Consent and safety

**What the user attests, verbatim (`AV-6` for the wording; a lawyer must approve it):**

> I confirm that I am the person shown and heard in these files, or that I have that person's written
> permission; that the person is 18 or older; that they have given informed consent to their face, body and
> voice being used to generate AI videos in this project, and to these files being sent for that purpose to
> the AI providers this project uses; that I will keep a record of that consent; and that I will revoke or
> delete this avatar if they withdraw it.

Like `RequiredConsentStatement`, the server holds the text as a constant and compares it ordinally; any other
text is `400 consent_statement_required` with the required statement in the body. Unlike voices, the
statement has an Italian twin (`consent_locale`), because an Italian user must not be held to a sentence in a
language the form did not show. Both versions are registered under one `consent_statement_version`; the
validator accepts either, and stores the one that was shown.

**Required with it:** the subject's name; the basis (`Self` or `WrittenPermission`); the adult checkbox;
a retention period; verified email (`403 EMAIL_NOT_VERIFIED`, the same check uploads from links use); project
**Edit** access; the plan feature `avatars` (`402 PLAN_NOT_ENTITLED`, feature `avatars`).

**Fails closed (refused, nothing written):**

- Wrong or missing statement, missing subject name, adult checkbox not ticked, retention out of range.
- A reference that is not a file of this project, is `Quarantined` by CSAM or moderation, is held by the
  output hold (`OutputGate.HeldFileIdsAsync`), or is a generated output whose publish verdict is not
  allowed (`PublishModeration.GateStoredCopyAsync`). Avatars of synthetic people made by our own image tools
  are allowed only through that verdict.
- **Moderation pending.** When `ContentModeration:Enabled` is true, a reference whose `ModerationStatus` is
  `Unmoderated` or `Scanning` is refused with `409 AVATAR_REFERENCE_PENDING` (retry later). This is stricter
  than voices on purpose: a face is the most abusable input we take. `Error` (moderation could not run) is
  also refused for avatars (`AV-5`), where ingest otherwise fails open.
- When CSAM matching is enabled and a reference is `NotScanned`, the attach is refused with
  `409 AVATAR_REFERENCE_UNSCANNED` and a re-scan is queued. With the control disabled (self-host default)
  the rule cannot apply and the deployment's own operator is responsible; the docs say so.
- The project's `content_quarantined_at` marker is set: no new avatar in that project until it is cleared
  by the existing release process (avatars are a high-risk surface; one blocked file is reason enough).

**What we cannot check, stated, not hidden.** We do not estimate age, detect faces, match the face to the
account holder, or recognise public figures. Phase 1 relies on the attestation, the moderation categories
(`sexual/minors` always blocks), and the audit trail. The terms must say that an avatar of someone who did
not agree, of a minor, or impersonating a public figure breaks them (`AV-8` for the public-figure rule; the
recommendation is a flat ban unless the basis is `Self`, i.e. the public figure is the account holder).

**Revoke.** `POST .../revoke-consent` stamps `consent_revoked_at` once. From that instant the avatar is
unselectable everywhere: pickers hide it, step saves naming it are refused, and (phase 2) a running step
re-resolves the avatar immediately before **each take's submit**, as it re-checks the lease, so a revoke
during a run stops the next take (`AVATAR_CONSENT_REVOKED`, `UserInput`, the take is not charged because it
was never submitted). Outputs already made are the customer's files and are not deleted automatically; the
usage endpoint lists them so the customer can delete them, and replay or reuse of them is refused (see
"Engine"). A revoked avatar is deleted by the sweep after 30 days.

**Delete.** `DELETE .../avatars/{id}` (project **Delete** permission, as voices) removes, in order: every vendor
asset (best effort, outcome per asset), the reference files (storage object and row, outcome per file), the
consent document file, the phase 3 stills, then the row, and writes one `deleted` event. The response reports
what was actually removed:

```
{ id, rowDeleted: true,
  references: { deleted: 5, failed: 0 },
  vendorAssets: [ { provider: "MiniMax", outcome: "deleted" } ],
  consentDocumentDeleted: true }
```

The row is removed even when a vendor refuses, so a withdrawn consent is never kept alive here; the vendor
failure is logged with the asset id for the operator, as voices do.

**Retention sweep.** `ProjectAvatarRetentionService` (every 6 h, 2 min start delay, at most 200 avatars per
sweep, `ProjectAvatars:RetentionSweepEnabled`, default true and false on local staging) deletes avatars past
`retention_until` or revoked more than 30 days ago, through the same delete routine, and writes
`retention_deleted`. A 7-day warning notification (category `avatar_retention`, per-user preference) lets the
owner re-attest to extend; re-attestation is a new full statement, never a silent extension.

**Changing references** is re-attestation: `PUT .../avatars/{id}/references` takes the full new list plus the
statement again, re-runs every attach check, stamps a new `consent_attested_at`, and writes
`references_changed`. Name, description, voice link and disclosure label are a plain `PATCH`.

**Egress (sending a person's media to a vendor).** Two consents must both be true in phase 2, and neither
implies the other:

1. The avatar's consent (the subject agreed to AI providers receiving their media).
2. The step's `AllowSourceMediaEgress` (the customer agreed that this step sends their media out). The assistant
   turns it on only on the user's request (card in chat only; never over MCP); `AssistantEgressConsentGuard` resets or refuses it otherwise, and
   phase 2 adds the avatar fields to the "context changed" comparison, so pointing an already-consented step
   at a different avatar resets egress to false with a `needsBeforeRunning` notice.

Missing either is `EGRESS_REFUSED` before any reservation, frame cut or upload.

**AI Act disclosure.** Phase 1 records the label choice and the documentation; phase 2 enforces it on
output. Every clip made with an avatar is **marked machine-readably** (container metadata and, if `AV-9`
picks it, a C2PA manifest), and carries `aiGenerated: true, avatarId` in the step manifest and the output's
DB row. A **visible** "AI-generated" label is burned in by default (`disclosure_label = Visible`); a customer
can switch to `MetadataOnly` only by ticking that they disclose the content another way. Trial output is
already watermarked. The design follows Art. 50 as researched below; the exact obligations, and whether
ReelBolt is a "provider" or only the customer is a "deployer", need legal review.

**Data protection notes for counsel (not conclusions).** Photos and videos of an identifiable person and
recordings of their voice are personal data. Whether conditioning a generative model on them is processing
of **biometric** data under GDPR Art. 9 turns on Art. 4(14) and Recital 51 ("specific technical
processing ... allowing or confirming unique identification"); our platform does not identify anyone, but
the vendor's model may build a face representation. Treat as special-category for safety: explicit consent
(Art. 9(2)(a)) collected by the customer, a DPIA before launch, vendor DPAs and transfer mechanisms checked,
retention bounded. Italian image rights (art. 10 c.c., art. 96-97 L. 633/1941) require the person's consent
to show their portrait; Italy's 2025 AI law added a criminal offence for harmful unlawful deepfakes. See
"Legal anchors".

## Engine and generation

**Resolver.** `IAvatarResolver` in `WorkflowEngine/Services/Avatars/`, a copy of `IProjectVoiceResolver`:

```
Task<ResolvedAvatar> ResolveAsync(Guid projectId, Guid avatarId, CancellationToken ct);
Task<IReadOnlyList<ResolvedAvatar>> ListSelectableAsync(Guid projectId, CancellationToken ct);

record ResolvedAvatar(Guid Id, string Name, bool Usable, string? Reason,
    IReadOnlyList<ResolvedAvatarReference> References,   // empty unless Usable
    Guid? ProjectVoiceId, AvatarDisclosure Disclosure);
```

Only `WHERE id = @a AND project_id = @p`; another project's id is `avatar_not_found`. The rule is
`ProjectAvatar.IsSelectable`, shared code, so API and engine cannot disagree. **An unusable avatar fails the
step** with `AVATAR_NOT_FOUND` or `AVATAR_CONSENT_MISSING` (reason in `ErrorDetails`), `FailureClass.UserInput`.
It never falls back to "no avatar": a step asked to show a person must not quietly show nobody or someone
else. `References` is empty unless usable, so a caller cannot send the media of an unconsented avatar by
reading the wrong field (the voice resolver's `RemoteVoiceId` trick).

**Which step? `VideoGenerate`, not a new step type (recommended, `AV-10`).** An avatar is one more input to
a generated clip, like `FirstFrame`. `VideoGenerate` already has takes, the budget gate, the ledger,
crash-resume, change requests (re-take one shot), dailies, compile placement, egress and per-model input
declarations; a new `AvatarVideo` step type would copy all of it and need its own arm in
`StepChangeCapabilities`, `StepCatalog`, the linter, the cost estimator and the step cache. Append-only
config members (phase 2):

| Member | Meaning |
|---|---|
| `AvatarId` (Guid?) | the avatar to show; null keeps every existing path byte-identical |
| `AvatarReferenceRoles` (list?) | optional subset of roles to send (default: what the model accepts, in ordinal order) |
| `AvatarAudio` (`ExtractInputRef?`) | phase 3 and audio-driven models: the speech that drives the mouth (a `Voiceover` step output or a project audio file) |

A `ShotDirector` plan can name the avatar per shot later (an offered id `a{n}`, never a GUID in model text);
phase 2 starts with one avatar per step.

**Model declaration, not a new capability (recommended, `AV-11`).** Whether a model takes reference images,
reference video or driving audio is a fact **per model**, so it follows the `IVideoImageInputSupport`
pattern: a pure, offline `IVideoReferenceInputSupport.ReferenceInputSupport(model)` returning
`{ MaxImages, MaxVideos, MaxAudio, AudioDrivesSpeech, ExclusiveWithFrames }`. `IVideoGenerationClient` stays
frozen; `VideoGenerationRequest` gains one optional trailing member `References`
(`VideoGenerationReference(Kind, Role, ContentType, Bytes)`), so every existing caller and fake still
compiles. A model that cannot carry what the step asks for is refused before the budget gate reserves
anything (`AVATAR_NOT_SUPPORTED_BY_MODEL`), exactly as `KEYFRAME_NOT_SUPPORTED` is. Phase 2 needs no new
`InferenceProviderCapability`. Phase 3 first filed talking-head models under `VideoGeneration`; that was wrong, because
the default video model rejects a talking-head model id, so one default row could not serve both jobs. They now have
their own capability, `LipSync` (appended; unit unchanged, `VideoGenSeconds`, same per-resolution price rows as
`VideoGeneration`). A step with avatar audio resolves `LipSync` (step row, else default row, else none) and never falls
back to `VideoGeneration`; with none set up the run is refused at start with `LIP_SYNC_MODEL_NOT_SET`. Operator step:
add the fal.ai lip-sync model under Lip-sync and mark it default.

**Ledger and budget.** Unchanged gate; each take row gets `avatar_id`. Reference media add vendor
**input-material** cost on some models (MiniMax bills extra images and input video, see "Vendor facts"):
the client's estimate must include it, so `Describe` alone is not enough. Phase 2 adds an
`InputMaterialCost(model, references)` to the reference declaration and the estimator adds it to the hold.

**Step cache and reuse.** `VideoGenerate` is cacheable. An avatar step's key gains the avatar id, the
reference digests in order and `consent_attested_at` (folded into the config hash, so steps without an avatar
keep their keys; no `SchemaVersion` bump is required because no hashed field changes meaning, only a new
field appears when set). A cache **hit** re-resolves the avatar first and is refused if it is unusable, like
the quarantine rule. Ledger-level clip reuse, `Resume`, `Retry` and change-request `Reuse` copies of a step
whose result used an avatar re-check the avatar too, and refuse with `AVATAR_CONSENT_MISSING` when consent
is gone: reusing an output is new use. `DeterministicStepRevisions` needs no bump (no output changes for an
input that existed before).

**Change requests.** `VideoGenerate` re-take keeps working; the override catalogue later gains
`avatarId` (swap the person in one shot) only after the owner decides it (it is a new subject, so the card must
name both people). Not in phase 2's first cut.

**Linter and step catalog.** A new rule (next free `WL0xx`): "This step shows <avatar name> but egress is
off" and "this avatar is not usable (reason)". The `StepCard` for `VideoGenerate` lists the avatar input;
`docs/assistant-kb/step-cards.md` is regenerated.

## API, assistant and web

### REST (Inference API, under `/api/v1/projects/{projectId}`)

| Method | Path | Access | Description |
|---|---|---|---|
| `GET` | `/avatars/requirements` | View | Required statement (en, it) and version, limits, accepted MIME types, plan entitlement, whether moderation is on |
| `GET` | `/avatars` | View | List with `selectable`, `unselectableReason`, reference summaries (kind, role, thumbnail via existing derivative route), voice name, retention date |
| `GET` | `/avatars/{id}` | View | One |
| `POST` | `/avatars` | Edit, verified email, plan `avatars` | `{name, description?, consentStatement, consentLocale, consentSubjectName, consentBasis, subjectIsAdult, consentDocumentFileId?, retentionDays, projectVoiceId?, references:[{projectFileId, role}]}` → `201` |
| `PATCH` | `/avatars/{id}` | Edit | name, description, voice link, disclosure label |
| `PUT` | `/avatars/{id}/references` | Edit | New list + statement again (re-attestation) |
| `POST` | `/avatars/{id}/revoke-consent` | Edit | Idempotent |
| `DELETE` | `/avatars/{id}` | Delete | Per-outcome report |
| `GET` | `/avatars/{id}/usage` | View | Runs, steps and outputs that used it (from the ledger `avatar_id`) |

Errors: `400 consent_statement_required` (body has the statement), `consent_subject_required`,
`subject_must_be_adult`, `reference_file_not_found`, `reference_kind_not_allowed`, `too_many_references`,
`references_too_large`, `no_image_reference`; `402 PLAN_NOT_ENTITLED`; `403 EMAIL_NOT_VERIFIED`;
`404` across organisations and for another project's avatar; `409 AVATAR_REFERENCE_PENDING`,
`AVATAR_REFERENCE_UNSCANNED`, `PROJECT_CONTENT_QUARANTINED`, `AVATAR_LIMIT_REACHED`, `FILE_IN_USE_BY_AVATAR`
(on the file-delete route). The controller joins `ReviewedProjectRouteControllers`.

### Assistant and MCP

Phase 1 adds **one** read-only tool, `ListProjectAvatars` (name, description, selectable and the reason in
plain words, roles present, linked voice name; never ids in replies, never the subject's name unless it is
the avatar's own display name). Counts move from **111 to 112** chat tools and **74 to 75** MCP tools; the
`AssistantToolProvider` group count, `AssistantPromptConsistencyTests`, the MCP catalog test (title +
`readOnlyHint: true`) and the counts in `CLAUDE.md` and `docs/platform-assistant-mcp.md` change in the same
PR. It is free and not gated.

**No create, revoke, delete or re-attest tool, ever.** Consent is given only in the app UI by a person
ticking the statement, exactly as voice cloning; the assistant's answer to "make an avatar of me" points to
the Avatars tab. The system prompt gains one line saying so, pinned by the consistency test.

Phase 2: the run brief gains `avatar: { avatarId }` (validator, `[RUN BRIEF]` sentence, MCP typed brief
object), and the workflow tools may set `VideoGenerate.avatarId`. The approval card for `ExecuteWorkflow`,
`ApplySaveWorkflow` and `ApplyPatchWorkflow` then names the avatar ("Shows: Anna (avatar)") and that it sends
her pictures to the provider; egress stays UI-only as today.

### Web

- **Avatars tab** next to Voices on the project page (`ProjectAvatarsTab`), cards with the primary picture,
  status badge (Ready, Checking pictures, Consent withdrawn, Expires on ..., Reference missing), voice, usage
  count, and Revoke / Delete actions.
- **Create flow**, three steps: (1) choose or upload files, with guidance (clear front-facing face, good light,
  one person, no sunglasses; a short video speaking to camera; the files that are still being checked show a
  spinner, and the Create button waits); (2) the person: name, "this is me" or "I have their written
  permission" (optional upload of the signed release), the adult checkbox, and the statement shown in full
  with one checkbox; (3) keep for (days, default 365), link a voice (only selectable voices of the project,
  or "Clone a voice first" link), disclosure label. The statement is fetched from `/avatars/requirements`,
  never typed in the client.
- **Delete dialog** lists what will be removed, including other uses of the reference files, then shows the
  report.
- **Media pool**: reference files badged "Avatar: <name>".
- **i18n**: new namespace `web/messages/{en,it}/avatars.json`, area added to `web/i18n/migrated.json`;
  glossary entry "avatar" → "avatar" and "consent" → "consenso" in `docs/i18n-glossary.md`. User guide
  page `docs/user-guide/projects/avatars.md` plus the Italian page under `docs/user-guide-i18n/it/` stamped
  with `i18n-stamp.mjs`.

## Billing and plan gating

- **New plan feature `avatars`** (`PlanGate.AvatarsFeature`, entitlement `avatarsAllowed` in both catalog
  copies, which CI keeps byte-identical): Trial `false`, Creator / Pro / Team / Enterprise `true`
  (recommended, `AV-2`). Creating an avatar on Creator is useful before phase 3 only for the cheap path, so
  the alternative is Pro and up until phase 3 ships.
- **Free:** creating, listing, revoking, deleting an avatar; the attach checks; moderation of the references
  (already free at ingest); the sweep. Reference files count toward storage quota like any project file.
- **Metered (phase 2 and 3, on the ordinary path):** the `VideoGenerate` take, plus the vendor's
  input-material cost for references, in the same hold and settle; using an avatar does not need its own
  hold. Generating with an avatar also needs `video_generation` (Pro and up today). Phase 3's still is an
  `ImageEdit` or `ImageGeneration` call priced like the picture tools.
- **Run rate:** a step with an avatar is `Paid`, as every `VideoGenerate` step.

## Phased delivery

Each lane is one reviewable PR with its tests; the whole `ReelBolt.WorkflowEngine.Tests` suite runs on the
final head of the phase, per the whole-suite rule.

**Phase 1, the foundation (about 9 to 12 developer-days):**

| Lane | Content | Tests | Size |
|---|---|---|---|
| A1 | Shared model (`ProjectAvatar`, references, vendor assets, events), `IsSelectable` / `UnselectableReason`, API migration `AddProjectAvatars`, engine read-only mapping, engine migration `AddExternalGenerationJobAvatar`, `project_files.source_kind` value | rule truth table; ownership guard; tenant inventory; migration round trip | S |
| A2 | `ProjectAvatarsController` (requirements, list, get, create, patch, references, revoke, delete), attach checks, plan feature in both catalogs, file-delete `409`, `ReviewedProjectRouteControllers` | 404 across orgs, 403 low role, each fail-closed check, verbatim statement (en and it), delete report with fake vendor outcomes, races on revoke | M |
| A3 | `ProjectAvatarRetentionService`, events table writer, usage endpoint, 7-day warning notification | sweep bounds, staging switch, revoked grace, event never holds media | S |
| A4 | Web tab, create flow, dialogs, media badge, `avatars.json` en + it | component tests, `no-small-fonts`, catalog and glossary tests, one Playwright happy path | M |
| A5 | `IAvatarResolver` (engine), `ListProjectAvatars` tool and every count, prompt line, docs (`docs/avatars.md` internal, user guide en + it, `CLAUDE.md` table rows, `docs/README.md`) | resolver parity with the API rule; prompt consistency; MCP catalog; docs-site build | S |
| A6 | Legal and disclosure text: privacy policy row and purpose, terms clause, DPIA draft, `provider-data-handling.md` row; **needs counsel** | site build; legal version bump decision (Terms bump = re-acceptance) | S |

**Phase 2, MiniMax-backed generation (about 8 to 10 days, after the must-measure items):**
`IVideoReferenceInputSupport` and `VideoGenerationRequest.References`; the MiniMax client's reference mode
(and, as a separate pricing change, the cheaper `MiniMax-H3-Highspeed` and `MiniMax-H3-Lite` in `Describe`)
(and, if measured useful, `mm_file` uploads recorded in `project_avatar_vendor_assets`); `VideoGenerate`
config members, save validation, egress and linter rule; input-material pricing in the estimate and hold;
ledger `avatar_id`; per-take re-resolve; cache, resume and reuse checks; disclosure marking and visible label
in the normalizer; run brief field and approval-card text; one template "Spokesperson clip" (`AutoCreate`
false); a live smoke run behind a platform-admin flag before the plan feature is shown.

**Phase 3, the cheap still-then-animate path (about 6 to 8 days):** a still of the avatar from its reference
pictures with a reference-capable image model (an `ImageEdit` call in the picture editor or a workflow step),
saved under the avatar's stills prefix; then a talking-head or image-to-video model on fal (or a vetted
ComfyUI template) driven by `FirstFrame` = the still and `AvatarAudio` = the narration from a `Voiceover`
step with the avatar's linked voice. Same consent, same egress, same ledger. Candidate models are listed in
"Vendor facts"; one is picked after measurement.

## Decisions for the owner

All thirteen were **decided on 2026-11-05 by the product owner**, each at its recommended option (the plan was
approved as written). Items that say "confirm with counsel" or "approved by counsel" are decided for the build;
the legal review in lane A6 can still reopen them.

| Id | Question | Options | Recommendation | Decision |
|---|---|---|---|---|
| AV-1 | Scope of an avatar | **A** project (like voices); B organisation library with tenant filter | **A** now; B later with an org-wide consent wording | **A**, project scope (2026-11-05, product owner) |
| AV-2 | Which plans may create avatars | **A** Creator and up; B Pro and up until phase 3; C every plan incl. Trial | **A** (generation stays Pro+ through `video_generation`) | **A**, Creator and up (2026-11-05, product owner); **changed 2026-11-06 to Pro and up** (Creator has no video generation, so it could make an avatar but no clip; the Avatars tab stays visible on Trial and Creator with a "Pro" badge) |
| AV-3 | Reference media storage | **A** link existing project files, delete with the avatar; B copy into an avatar-only prefix | **A** (reuses scans and probes; `RESTRICT` keeps them) | **A**, link existing project files (2026-11-05, product owner) |
| AV-4 | Signed release document | A required for `WrittenPermission`; **B** optional upload, never sent anywhere; C not collected | **B**, revisit after counsel | **B**, optional signed release, never sent anywhere (2026-11-05, product owner) |
| AV-5 | Moderation stance for references | **A** fail closed while pending or on `Error`; B fail open as ingest does | **A** | **A**, fail closed (2026-11-05, product owner) |
| AV-6 | Consent wording and language | **A** the statement above, en + it versions under one version id; B English only, like voices | **A**, wording approved by counsel | **A**, en + it under one version id (2026-11-05, product owner; wording still for counsel) |
| AV-7 | Audit event retention | A 1 year; **B** 3 years; C forever | **B**, confirm with counsel | **B**, 3 years (2026-11-05, product owner; period still for counsel) |
| AV-8 | Public figures | **A** banned unless basis is `Self`; B allowed with written permission | **A** | **A**, banned unless basis is `Self` (2026-11-05, product owner) |
| AV-9 | Machine-readable marking | A container metadata only; **B** metadata now, C2PA manifest in phase 2; C nothing until the law is final | **B** | **B**, metadata now, C2PA in phase 2 (2026-11-05, product owner) |
| AV-10 | Step shape | **A** extend `VideoGenerate`; B new `AvatarVideo` step type | **A** | **A**, extend `VideoGenerate` (2026-11-05, product owner) |
| AV-11 | Capability modelling | **A** per-model reference declaration under `VideoGeneration`; B new `AvatarVideo` capability | **A** | **A**, per-model reference declaration (2026-11-05, product owner) |
| AV-12 | Minors | **A** refused outright; B allowed with parental consent | **A** | **A**, minors refused (2026-11-05, product owner) |
| AV-13 | Vendor for phase 2 | **A** MiniMax direct (H3 reference mode); B fal-hosted equivalent first | **A**, per the product owner, subject to the must-measure list | **A**, MiniMax direct for phase 2 (2026-11-05, product owner; subject to the must-measure list) |

### Must measure with a live vendor call

Only a paid call can settle these; each blocks phase 2 or 3, not phase 1.

1. **Identity hold on H3 reference mode.** Does a clip from 3..9 reference pictures (plus a short video)
   keep the person recognisable across takes and angles? Ten prompts, two subjects who consented, scored by
   people. Decides whether phase 2 ships on H3 or only on a fal talking-head model.
2. **Does `reference_audio` make the person say those words, or only set a voice and rhythm?** The guide
   does not say (M7). Decides whether `AvatarAudio` maps to MiniMax at all or only to the phase 3 models.
3. **Input-material billing:** do `reference_image` items count against the free 5 (2 on H3-Max), and is
   reference video billed for its full length? Check `usage.total_seconds` and the invoice.
4. **Data URI versus `mm_file`** for reference video and audio: accepted inline, or upload required?
5. **Docs inconsistency:** is `last_frame` alone allowed? (Not an avatar question, but the client's
   validation depends on it.)
6. **Failed and moderated tasks:** charged on pay-as-you-go or not?
7. **fal talking-head models:** real latency, real price per second at 720p, data-URI acceptance, whether
   `X-Fal-Store-IO: 0` is honoured on these endpoints.
8. **Vendor-side refusals of faces:** which reference pictures MiniMax refuses (for example well-known
   faces) and with what error, so the step can name it.

## Risks

- **Non-consensual likeness.** The attestation is a sentence; a bad actor can tick it. Mitigations are the
  audit trail, the verified email, revocation, the terms, moderation, and the visible label; none detects
  abuse. A face-match between the uploader and the subject is out of scope and would itself be biometric
  processing.
- **Minors.** No age check exists. A wrongly attested minor's likeness is the worst outcome; the
  `sexual/minors` category only catches the worst content, not the person's age.
- **Vendor retention outlives our delete.** If a vendor keeps request inputs (fal keeps 30 days unless told
  not to; MiniMax's terms need reading), deleting the avatar here does not delete their copy. The delete
  report and the privacy page must say so; for fal, phase 3 should send `X-Fal-Store-IO: 0` on avatar
  requests (`AV` follow-up).
- **Legal moving target.** The AI Act transparency duties and the Code of Practice on marking were still
  settling in late 2026; the design keeps marking and labelling switchable per output.
- **Revoke during a run.** Covered per take, but a take already submitted finishes and is charged; the
  output is then refused for reuse. Stated in the UI.
- **Reference file deletion friction.** The `NO ACTION` link on project files will surprise users cleaning up media;
  the `409` names the avatar and links to it.
- **Cost surprise.** Reference video is billed by MiniMax per second (at the output rate; more on H3-Max);
  a 10 s reference video nearly doubles a 6 s take's price. The estimate shows the input-material part separately.
- **Scope creep into an editor feature.** Lip-syncing existing footage (dubs, pickups) is a different
  product with different consent (the person never agreed to new words); it is explicitly not covered by
  this consent.

## Legal anchors

Research notes for counsel, checked 2026-11-05. None of this is a legal conclusion; every row needs legal
review before the consent text, terms or privacy policy change.

| Topic | What the source says | Design consequence | Source |
|---|---|---|---|
| Biometric data | GDPR Art. 4(14): data from specific technical processing that allows or confirms unique identification. Recital 51: photographs are biometric only when processed through specific technical means allowing unique identification. Art. 9(1) bans processing biometric data *for the purpose of uniquely identifying* a person unless Art. 9(2) applies (for example explicit consent) | No EDPB or Garante position was found on conditioning a generative model on face pictures. We treat avatar media as special-category to be safe: explicit consent, DPIA, bounded retention | [Art. 4][gdpr4], [Art. 9][gdpr9], [Recital 51][rec51] |
| AI Act Art. 50 | 50(2): providers of generative AI systems mark output machine-readably and detectably. 50(4): deployers disclose deepfakes. Applies from 2 August 2026 | Machine-readable marking always; visible label by default (`AV-9`). Whether ReelBolt is a "provider" or only the customer a "deployer" is for counsel | [Art. 50][ai50] |
| Digital Omnibus on AI | Provisional agreement in May 2026 (reported adopted as Regulation (EU) 2026/1744; not verified here): systems on the market before 2 August 2026 have until **2 December 2026** for 50(2) marking; the rest of Art. 50 unchanged; a new Art. 5 ban on non-consensual intimate imagery generators | If ReelBolt counts as already on the market, marking is due by 2 December 2026 whatever happens to avatars; avatars must not launch without it | [Gibson Dunn][omni] (secondary source) |
| Code of Practice on marking and labelling | Final text published 10 June 2026; voluntary, open for signature | Use it to choose the marking format in phase 2 | [Commission][cop] |
| Italian image rights | Art. 10 c.c. (abuse of a person's image); L. 633/1941 art. 96 (no exhibition or reproduction of a portrait without consent) and art. 97 (exceptions such as notoriety, never where honour, reputation or decorum are harmed) | The person shown must consent; the notoriety exception does not cover putting words in a public figure's mouth (`AV-8`) | [art. 10][cc10], [art. 97][lda97] |
| Italian AI law | Legge 132/2025 (in force 10 October 2025) adds art. 612-quater c.p.: unlawful dissemination without consent of AI-generated or altered images, video or voice able to mislead, causing unjust harm; 1 to 5 years | A customer's misuse is a crime in Italy; terms, revoke and the audit trail matter, and the audit trail is evidence | [612-quater][cp612] |
| Minors | Codice privacy art. 2-quinquies: from 14 a minor consents to information-society services, under 14 the parent. L. 132/2025 art. 4(4): under-14s need parental consent to use AI systems | We refuse minors outright (`AV-12`), stricter than both, with no age-dependent consent flow | [2-quinquies][cp2q] |
| Vendor terms | MiniMax general terms: no likeness clause found; the H3 open-weights AUP bans impersonation without consent. fal's H3 lip-sync page puts the rights burden on the uploader | Our consent is the only consent; the privacy policy's provider table gains a row for each vendor that receives avatar media | M12, M13 in "Vendor facts" |

[gdpr4]: https://gdpr-info.eu/art-4-gdpr/
[gdpr9]: https://gdpr-info.eu/art-9-gdpr/
[rec51]: https://gdpr-info.eu/recitals/no-51/
[ai50]: https://artificialintelligenceact.eu/article/50/
[omni]: https://www.gibsondunn.com/eu-ai-act-omnibus-agreement-postponed-high-risk-deadlines-and-other-key-changes/
[cop]: https://digital-strategy.ec.europa.eu/en/news/commission-publishes-code-practice-marking-and-labelling-ai-generated-content
[cc10]: https://www.brocardi.it/codice-civile/libro-primo/titolo-i/art10.html
[lda97]: https://www.brocardi.it/legge-diritto-autore/titolo-ii/capo-vi/sezione-ii/art97.html
[cp612]: https://miolegale.it/codici/codice-penale/art-612-quater/
[cp2q]: https://www.brocardi.it/codice-della-privacy/parte-i/titolo-i/capo-ii/art2quinquies.html
