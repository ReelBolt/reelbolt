# Avatars

INTERNAL BACKGROUND. An avatar is a consented likeness of one real person, kept in one project: a named set of
reference pictures, short videos and speech recordings plus a recorded consent, retention, revoke and delete,
modelled on a cloned voice. The approved plan, with every decision (`AV-1` .. `AV-13`), the vendor facts and the
later phases, is [`design/avatars.md`](design/avatars.md). The customer page is
[`user-guide/projects/avatars.md`](user-guide/projects/avatars.md). Phase 1 built the foundation; phase 2 lets a
`VideoGenerate` step show an avatar through MiniMax's reference mode (below, from "Generating a clip with an
avatar"); phase 3 adds the cheaper still-then-animate path on fal.ai ("The still-then-animate path (phase 3)"). All
stay inert until the switches are on.

The avatar objects initiative ([`design/avatar-objects.md`](design/avatar-objects.md), decisions `AO-1` .. `AO-8`)
widens the word: an avatar is now a **person** or an **object** (a product, a vehicle, a building, anything that is
not a person). Every lane of that initiative is built; each part has its own section below, from "Object avatars" on:
objects, the picture consent version, the project media consent and the one media rule, the workspace avatar library,
variants, pulling an object out of a video, avatars in the picture editor, several subjects in one clip on every model
path, planned shots and the dailies room, clips from the editor, and the assistant and MCP tools. "Avatar objects: not
built yet" lists what is still missing.

## Avatar tables and who owns them

The Inference API owns four tables, created by the API migration `AddProjectAvatars` (history
`__EFMigrationsHistory_Api`): `project_avatars` (the avatar and its consent columns), `project_avatar_references`
(one row per reference file, linked to `project_files` with `ON DELETE NO ACTION`, with the attested
`content_sha256`), `project_avatar_vendor_assets` (vendor-side uploads, empty until phase 2) and
`project_avatar_events` (append-only audit, no foreign key to the avatar, never media or the subject's name). None
carries the `Tenant` query filter: every route is under `/projects/{projectId}/avatars` and loads the project
through `IProjectAccessService` first, so the tables are guarded at the parent (`TenantBoundaryInventoryTests`).

Three later API migrations (all expand-only) add to them. `AddAvatarObjects`: `project_avatars.subject_kind` (text,
default `'Person'`, CHECK `Person`/`Object`), the reference roles `Front`, `Angle`, `Detail` (CHECK widened) and
`projects.subject_media_consent_at` / `subject_media_consent_by_user_id` (the project media consent).
`AddAvatarLibrary`: `avatar_library_items` (the workspace library; it carries the named `Tenant` query filter,
because it is addressed by a caller-chosen id and owned by one organization), `avatar_library_references` (its
media rows, reached only through the item, no filter) and `project_avatars.library_item_id` (unique per project).
`AddAvatarVariants`: `project_avatars.variant_of_avatar_id` (self reference, `ON DELETE SET NULL`),
`variant_label` (unique per original among live rows), `variant_description`, and the table `avatar_build_jobs`
(variants made by the picture model and objects pulled out of a video; a project child, cascade-deleted with it,
no filter, guarded at the project). `TenantBoundaryInventoryTests` lists `avatar_library_items` among the filtered
tables and the other new tables among the unfiltered ones.

The WorkflowEngine maps `project_avatars` and `project_avatar_references` read-only (`ExcludeFromMigrations()`),
through the shared column configuration `ProjectAvatarModel`, exactly like `project_voices`. The engine's avatar
columns are its own, on `external_generation_jobs`: `avatar_id` (engine migration `AddExternalGenerationJobAvatar`,
nullable, no foreign key, partial index), written on each take that used an avatar, and `subject_ids_json` (engine
migration `AddExternalGenerationJobSubjects`, jsonb null), every avatar a take showed, people and objects; `avatar_id`
then holds the person when there is one, else the first object. The usage endpoint reads both.

## The avatar selectability rule

`ProjectAvatar.UnselectableReason(utcNow, references)` is the one rule; `IsSelectable` is "no reason". It takes one
`AvatarReferenceState` (kind, present, held) per reference row and answers the first broken condition, in this
order: `consent_missing` (no attestation time or attesting user, empty statement, or the adult box not ticked),
`consent_revoked`, `retention_expired` (`retention_until <= now`), `reference_missing`, `reference_held`,
`no_image_reference`. For an object it checks `consent_missing` (no attestation time or user, or an empty
statement; the adult flag is not read), then `reference_missing`, `reference_held` and `no_image_reference`; it never
answers `consent_revoked` or `retention_expired` for an object. The API's `selectable` flag, the web picker, the
assistant tool and the engine resolver all
call it, so they cannot disagree; `ProjectAvatarSelectabilityTests` is its truth table. A linked voice that has
become unselectable does not make the avatar unselectable: phase 2 fails a step that needs that voice with the
voice's own code.

## How an avatar reference is observed

`AvatarReferenceObserver.ObserveAsync` (`ReelBolt.Shared/Avatars`) turns reference rows into states, on any
context that maps projects, project files, step results and executions. Both the Inference API and the engine
call it, so a reference is judged by the same signals on both sides.

- **Present** means the file row exists in the avatar's project (query filters bypassed) and its bytes now hash to
  the attested `content_sha256`. Nothing stores a digest on `project_files`, so the bytes are read and hashed:
  the engine through `IAvatarReferenceDigestReader` (`S3AvatarReferenceDigestReader`, streamed, never buffered),
  the API through `IFileStorageService.DownloadAsync`. An object that is gone or cannot be read is missing (fail
  closed). A key outside `projects/{projectId}/` is missing and is not read.
- **Held** means: quarantined by CSAM matching or by ingest moderation; or a platform-made output at or before the
  project's `content_quarantined_at` plus the clock margin (the same predicate as the engine's `ProjectFile`
  query filter); or a platform-made output covered by the CSAM output hold (`ProjectOutputHold`: by its own
  stamp, by a held producing step result, and everything platform-made when the hold state cannot be read). A
  user's own upload is never held by the output hold. The picture-export chain that the API's `OutputGate`
  follows is not followed here (the engine does not map picture documents); a reference is a plain upload in
  practice, and attach-time checks (lane A2) refuse held files outright.
- A held file's bytes are never read: it is observed present and held, so the rule answers `reference_held`.
- An avatar that fails consent, revoke or retention has no reference read at all, because the rule checks those
  first.

## The engine avatar resolver

`IAvatarResolver` (`WorkflowEngine/Services/Avatars/IAvatarResolver.cs`), a singleton registered beside
`IProjectVoiceResolver`, copies that resolver's contract:

- `ResolveAsync(projectId, avatarId)` reads `WHERE id = @a AND project_id = @p` in a fresh scope on every call
  (never memoized). Another project's id is `avatar_not_found`, with an empty name and nothing read.
- `ResolveRequiredAsync(projectId, avatarId)` fails closed: it returns only a usable avatar, otherwise it throws
  `AvatarUnusableException` with `Code`, `Reason` and `FailureClass.UserInput`. It never turns an unusable avatar
  into "no avatar": a step asked to show a person must not quietly show nobody or someone else.
- `ListSelectableAsync(projectId)` lists the usable avatars of the project, oldest first.
- `ResolvedAvatar.References` (file id, kind, role, ordinal, digest, storage key, MIME type) is empty unless the
  avatar is usable, so a caller cannot send the media of an unusable avatar by reading the wrong field.
- Failure codes (`AvatarFailureCodes.For`): `AVATAR_NOT_FOUND` (`avatar_not_found`), `AVATAR_CONSENT_REVOKED`
  (`consent_revoked`), `AVATAR_CONSENT_MISSING` (every other reason, which travels in the failure details).

Phase 1 wires the resolver into nothing that spends money. Phase 2's `VideoGenerate` resolves the avatar before the
budget gate reserves anything and again before each take's submit; a cache hit, resume, retry or replay copy of a
result that used an avatar re-resolves it first. `AvatarResolverTests` replays every row of the rule's truth table
through real rows and pins the cross-project, fail-closed and never-read behaviours.

## The avatar assistant and MCP tools

`AssistantAvatarTools` holds twelve tools (chat 153 in all, 152 registered without a thread, MCP 101; the full table
is in [`design/avatar-objects.md`](design/avatar-objects.md) "Avatar tools for the assistant and MCP" and the detail
in [`platform-assistant-mcp.md`](platform-assistant-mcp.md#avatars-and-objects-list-make-objects-variants-the-library-clips)):

- `ListProjectAvatars` (free, read-only, same name over MCP): per avatar the display name, `kind` (`person` or
  `object`), the description, `selectable` and the reason in plain words, reference counts by kind, the roles, the
  linked voice's name, `keepUntil` (null for an object), `coversPictures`, `fromLibrary`, and for a variant
  `variantOf`, `variantLabel` and `variantDescription` (its original lists `variants`). The answer adds
  `mediaConsent` (read only), `builds` (variants and extractions being made, and failed ones of the last day) and
  `clips` (the newest clips with avatars). A kind whose switch is off is not listed.
- `ListAvatarLibrary` (free, read-only): the workspace library by name, a variant item under its original's name.
- Pairs, collapsed over MCP: `CreateObjectAvatar` (an object from project files by path), `ExtractObjectFromVideo`
  (an object pulled out of a video, below), `CreateAvatarVariant` (a variant of an OBJECT, from files or by the
  picture model), `ImportLibraryAvatar` (a library item by name) and `GenerateSubjectClip` (a clip with avatars and
  objects by name; it answers the run's `executionId`). Each calls the service its REST route calls.

Names, never ids, in every answer and argument; never a storage key, a URL, a digest, media or the consent subject's
name. There is no tool that creates, re-attests, revokes or deletes a person's avatar (consent is given only in the
app's Avatars tab), deletes an object or a library item, or saves to the library; the prompt section "People and
objects" (both copies, pinned by `AssistantPromptConsistencyTests`) says so. Making an object is the member's own
attestation: in chat the card shows the object statement and approving it is the tick; over MCP the caller passes
`statement` verbatim and `locale`. The four object and import applies always ask (auto mode too). The picture tools
(`ProposeRunPictureTool` / `ApplyRunPictureTool`) take `subjects` (avatar names) for `generate` and `instruct`; the
card adds one line per subject from the database and the token binds the resolved ids. Without the recorded project
media consent the clip, variant, extraction and picture tools take `allowMediaEgress: true` for one call, under the
one rule below.

## The avatars kill switch

`Avatars:Enabled` (env `AVATARS_ENABLED`, bool, default `false`) is the operator kill switch for the whole
feature; lane A2 adds it to compose, `.env.example` and the API routes. The assistant tool reads the key directly
from configuration on every call and, when it is off, answers
`{ "available": false, "message": "Avatars are not available in this workspace." }`: a plain result, not an
error, with nothing read. The tool stays registered either way, so the tool counts never depend on configuration.
The engine resolver does not read the switch: with the feature off no avatar can be created, so there is nothing
to resolve.

Phase 2 adds a second switch: `Avatars:Generation` (env `AVATARS_GENERATION`, `Off` | `PlatformAdminOnly` | `On`,
default `Off`), about PEOPLE only. A `VideoGenerate` step that shows a person runs only when `Avatars:Enabled` is true
AND the mode is not `Off`; `PlatformAdminOnly` lets only runs started by a platform admin (`application_users.is_admin`
of the initiator) generate: that is the live smoke run the design asks for before the plan feature is shown. Every
other case fails `AVATAR_GENERATION_DISABLED` (`EntitlementDenied`) before anything is read or reserved. The workflow
engine is the authority. The Inference API reads the same key (`AvatarOptions.Generation`, enum
`AvatarPersonGeneration` with the engine's names) for one purpose: a clip from the editor or the assistant
(`ISubjectClipService`) that shows a person is refused up front with `409 AVATAR_GENERATION_DISABLED` (the estimate
answers it as `unavailableCode`), by the same rule and with the platform-admin read on every call, so it is never
queued only to fail. Compose passes both keys to `workflow-engine`, and `AVATARS_GENERATION` to `inference` too.

Objects have their own switch (decision AO-4), so they can go live independently of the people switch (the Terms and Privacy versions that cover people, objects, pictures and the workspace library are live, see the runbook below):
`Avatars:Objects:Enabled` (env `AVATAR_OBJECTS_ENABLED`, bool, default `false`), passed by compose to `inference` and
`workflow-engine`. Every `/api/v1/projects/{projectId}/avatars/**` route (and `/api/v1/avatar-library`) answers `404`
while neither switch is on (`AvatarOptions.AnyEnabled`). With one on, a row of the other kind is treated as absent: not
listed, `404` by id; only delete and the file-in-use guard still work on it. The variant, build and extraction routes
need the objects switch. In the engine, `AvatarGenerationOptions.AllowsGeneration(kind)`: a person needs `Enabled` and a
`Generation` mode other than `Off`, an object needs only `ObjectsEnabled`; otherwise `AVATAR_GENERATION_DISABLED`.

A switch that holds a value it cannot read is OFF, in the Inference API and in the engine alike, and never makes a
request fail. Both services read the three keys through one helper (`AvatarSwitchConfiguration` in the shared project),
so they cannot disagree. The two bools accept `true` or `false` only (`yes`, `1`, `on` are read as `false`).
`Avatars:Generation` accepts the names `Off`, `PlatformAdminOnly` and `On` only (any case); a number is not a member of
the list, so `2` is read as `Off`, not `On`, and `true` is read as `Off`: clips of people stay refused. Each host logs
the problem once at startup at Error, naming the key and the accepted values (the value is not a secret and is
printed).

## Generating a clip with an avatar

Phase 2 (decisions AV-9, AV-10, AV-11, AV-13). A `VideoGenerate` step (inline only) names `AvatarId`; the avatar's
pictures, short videos and speech samples go to the model as reference material on every take. A step config:

```json
{
  "planSource": "Inline",
  "inlinePrompt": "The person looks into the camera and greets the viewer.",
  "model": "MiniMax-H3", "resolution": "768P", "durationSeconds": 6, "ratio": "9:16",
  "takes": 1, "maxClips": 1, "maxSpendCredits": 400,
  "avatarId": "<a project avatar>",
  "avatarReferenceRoles": ["Face", "HalfBody", "Speech"],
  "allowSourceMediaEgress": true
}
```

- **Config** (`VideoGenerateStepConfig`, appended, null keeps every existing path byte-identical): `AvatarId`,
  `AvatarReferenceRoles` (optional subset; must keep a picture role), `AvatarAudio` / `AvatarAudioProjectFileId`
  (driving speech, for a model that declares audio drives speech: the phase 3 talking-head models). Shape rules: inline only, never
  with a first/last frame; phase 3 lets an inline step name `AvatarAudio` and leave the avatar to the run brief (the
  engine fails `CONFIG_INVALID` when none arrives, so the speech is never ignored). Save time: `VideoGenerateAvatarSaveValidator` refuses another project's avatar and one
  whose consent is missing, withdrawn or expired (consent half of the one rule; the engine hashes the files).
- **Model declaration** (`IVideoReferenceInputSupport`, beside `IVideoImageInputSupport`): per model, the most
  pictures, videos and audio files, the file count, the total video and audio length, whether audio drives speech
  and whether reference mode excludes frames, plus `InputMaterialCostUsd`. MiniMax (all four H3 models): 9 / 3 / 3,
  12 files, 15 s per kind, frames exclusive, `AudioDrivesSpeech = false` (unmeasured). Higgsfield and ComfyUI declare nothing and refuse a
  request that carries references; fal declares only its talking-head routes (phase 3). `VideoGenerationRequest.References` is the trailing
  optional member; a request without it is byte-identical on the wire.
- **Order in the executor, every refusal before anything is reserved:** the switches; the step's
  `AllowSourceMediaEgress` (`EGRESS_REFUSED`; set in the app, or by the assistant only on the user's request through an always-asked card, never over MCP); the model's
  declaration (`AVATAR_NOT_SUPPORTED_BY_MODEL`: no reference mode, a frame alongside, a driving audio track);
  `IAvatarResolver.ResolveRequiredAsync` (`AVATAR_NOT_FOUND` / `AVATAR_CONSENT_REVOKED` / `AVATAR_CONSENT_MISSING`,
  `UserInput`, never "no avatar"); the per-model selection from the STORED sizes (pictures, then audio, then video,
  in the avatar's order, inside the counts, the totals and MiniMax's 64 MB body; what is left out is listed in
  `meta.avatar.referencesSkipped` with a reason word); then only the chosen bytes are read, each re-hashed against
  the attested `content_sha256` (a changed file is `AVATAR_CONSENT_MISSING`; a storage failure
  `AVATAR_REFERENCE_UNAVAILABLE`, refunded).
- **Transport:** inline base64 data URIs for every kind (the design's default). The vendor documents data URIs for
  pictures; whether `video_url` / `audio_url` accept them or need an `mm_file` upload is must-measure #4. Nothing is
  written to `project_avatar_vendor_assets` in phase 2: no upload path exists until a live call shows one is needed.
- **Money:** every take's reservation, and so the step cap and the daily caps, includes the vendor's input-material
  charge for the references actually sent (`InputMaterialCostUsd`: pictures beyond the free 5, 2 on H3-Max, at
  $0.04 / $0.03 / $0.02 / $0.074; input video per second at the output rate, H3-Max at its own rate; audio free).
  The credit hold (`VideoGenerateCreditPricing`) adds the most our avatar limits allow on the model (eight pictures,
  the model's whole video allowance) because it is sized before the references are read; a hold only grows and
  settlement charges what was sent: a second usage row `videogen-input:{job}` (`VideoGenSeconds`, variant
  `input-material`, priced from the declared USD at the platform markup). The UI does not yet show the
  input-material part separately (the step output has `meta.avatar.inputMaterialCostPerTake`).
- **Ledger:** every take row of an avatar step has `external_generation_jobs.avatar_id`.
- **Per-take consent re-check:** right before each take's submit (beside the lease check) the avatar is resolved
  again and its fingerprint compared. A revoke, an expiry or a re-attestation stops that take (`Cancelled`, not
  charged, nothing was sent) and fails the step with the avatar's code; clips already delivered stay in the
  manifest. A take already submitted finishes and is charged.
- **Reuse:** `AvatarFingerprint` (avatar id, consent attestation time, disclosure, each reference's kind, role, order
  and digest) joins the ledger request hash (with the ids of the references sent), the step-cache key
  (`StepCacheKeyInputs.AvatarConsent`, appended only when set: no `SchemaVersion` bump, no
  `DeterministicStepRevisions` bump) and `IAvatarReuseGuard`, which `WorkflowExecutorService` asks on a cache hit,
  a resume reuse and a composition-replay copy (beside the voice guard): a revoked, expired or changed avatar is a
  miss, so the step runs live and fails with its code. A change request keeps a clip only if its
  `avatarFingerprint` equals the avatar's now; otherwise nothing is made (`AVATAR_CONSENT_MISSING`).
- **Marking (AV-9):** `GeneratedClipNormalizer` writes container metadata on every avatar clip (`comment`,
  `ai_generated=true`, `ai_generator=ReelBolt`, `+use_metadata_tags`) and, when the avatar's `DisclosureLabel` is
  `Visible`, burns a small "AI-generated" label (first-party text, the configured font). Clips without an avatar
  normalize byte-identically. The output and manifest carry `meta.avatar` (`avatarId`, display name, fingerprint,
  `aiGenerated`, `disclosure`, `marking`, `c2pa`, counts, skipped) and per clip `aiGenerated` and
  `avatarFingerprint`; never the subject's name, a storage key or a URL. No project-file column marks the clip yet.
- **C2PA (AV-9 B) is a documented gap, not faked.** `IGeneratedClipProvenanceSigner` is the seam; the only
  registration is `NoProvenanceSigner`, so `meta.avatar.c2pa` is `not_available`. The candidate is the C2PA
  reference implementation (`c2pa-rs` / `c2patool`, MIT/Apache-2.0, runs offline); it is not vetted here, is not in
  the engine image, and signing needs an X.509 certificate from a CA on the C2PA trust list whose custody is an open
  decision.
- **Run brief:** `avatar: { avatarId }` (validator, splitter: the avatar must be of this project with consent on
  record; stored on `run_brief_json`). The engine applies it only to an inline step that names no avatar and no
  frame (`RunBriefOverrides.ApplyToVideoGenerate`), never to the egress flag; it joins the brief's cache component.
  No `[RUN BRIEF]` agent sentence is added in this phase (no agent writes for the avatar yet). WL011 lists an
  avatar that no `VideoGenerate` step reads.
- **Assistant and MCP:** no new tool (counts unchanged). The typed brief schema gains `avatar`. The approval cards of
  `ExecuteWorkflow`, `ApplySaveWorkflow` / `ApplyWorkflow` and `ApplyPatchWorkflow` add one line per avatar shown:
  `Shows: "Anna" (avatar). Sends that person's pictures, videos and voice samples to the video provider.` (the
  avatar's display name from the database, never an id or the subject's name). The egress guard already fingerprints
  every config field, so pointing a consented step at another avatar resets the permission with a notice.
- **Linter:** WL015 gains the `showsAnAvatar` variant (an avatar step without the permission); WL027
  `avatar-unusable` (error) reads the consent facts through `IWorkflowLintFacts.ProjectAvatarAsync`.
- **Template:** `spokesperson-clip` ("Spokesperson clip", `AutoCreateOnProject` false): one inline `VideoGenerate` on
  MiniMax-H3, 768P, 6 s, 9:16, cap 400 credits, permission off, avatar from the brief.

### Still to measure with a live call (docs/design/avatars.md "Must measure")

All of these are unverified in code and default to the conservative reading: identity hold across takes (#1);
whether `reference_audio` makes the person say the words (#2; `AudioDrivesSpeech = false`, so `AvatarAudio` is
refused on MiniMax); whether reference pictures count against the free five and reference video is billed for its
whole length (#3; priced that way); data URI versus `mm_file` for video and audio (#4; data URI); whether the ratio
is read in reference mode (sent); failed and moderated tasks billing (#6); vendor refusals of faces (#8; surfaces as
`PROVIDER_REJECTED`); and the H3-Highspeed / H3-Lite duration range and ratios (not on the pricing page; set to
5..15 s and the H3 list).

## The still-then-animate path (phase 3)

The cheaper path of `docs/design/avatars.md` "Phased delivery". A talking-head model on fal.ai animates ONE still of
the avatar so the person says the words of a speech track. Same switches, consent, egress, ledger, reuse checks,
marking and brief field as phase 2; what differs is below.

- **Models** (`FalVideoCatalog`, job `TalkingHead`, schemas and prices in
  [`fal-ai-models.md`](fal-ai-models.md) section 14): default `minimax/h3-max/lip-sync/image-to-video` (image +
  audio, `resolution` 480P/768P/1080P/2K at $0.05/$0.08/$0.16/$0.32 per second; the only shortlisted route whose
  schema states its input bounds, audio 5 s..15 min, and the cheapest published tier); alternative
  `fal-ai/kling-video/ai-avatar/v2/standard` ($0.0562/s flat, a motion prompt, no stated audio bounds). The platform
  sends 5..15 s of audio to both (above 15 s H3 bills x1.2, which a per-second card cannot express). No duration
  field (the clip follows the audio) and no ratio field (the canvas follows the still, made at the step's ratio;
  `RatioFromFirstFrame`). Veed Fabric was not wired: its URLs are capped at 2083 characters, which rules out data URIs.
- **Capability** (`InferenceProviderCapability.LipSync`): a step with avatar audio resolves its provider through
  `LipSyncStepResolution`: the step's own LipSync row, else the default LipSync row, else none. It never falls back to
  `VideoGeneration`, and a step with no row fails at run start with `LIP_SYNC_MODEL_NOT_SET` (see
  [`workflow-linter.md`](workflow-linter.md) WL006). The admin accepts a talking-head model only under LipSync, and a
  LipSync row only for a fal.ai talking-head model of the catalog. Operator step: add the fal.ai lip-sync model under
  Lip-sync and mark it default. A talking-head row left under Video generation is not used any more: the Inference API logs one
  warning at boot with its row ids.
  A step not bound to a LipSync row runs the default LipSync row's model, and where that model does not offer the step's
  resolution, ratio or duration it uses the model's own, so the template's model and resolution are only hints. A step bound
  to a LipSync row keeps its own settings and is refused at run start (`VIDEO_MODEL_UNSUPPORTED`) when its model does not offer its resolution or ratio (duration is not checked).
  A talking-head step's provider must be a LipSync row when the step is saved; an older saved step naming a Video
  generation row runs on the default LipSync row. Project readiness asks for a lip-sync model ("Add a lip-sync model") for
  talking-avatar recipes.
- **Declaration**: `VideoReferenceInputSupport.AnimatesStill` (with `TakesPrompt`, `MinAudioMs`; appended, default
  off). Such a model takes no reference pictures itself; `AudioDrivesSpeech` is true and `MaxAudio` 1, so
  `AvatarAudio` is unlocked. `VideoReferenceInput.Refusal` refuses a step frame and a step with no speech
  (`AVATAR_NOT_SUPPORTED_BY_MODEL`). The fal client maps exactly one audio reference to `audio_url` and refuses
  anything else; every other fal endpoint still refuses reference material.
- **Why inside the executor, not a step.** The still is made by `VideoGenerateStepExecutor` (an `ImageEdit` call
  before the takes), not by a workflow step: a step's output is an ordinary output (registered, offered, reusable,
  revisable), and the still is derived personal data that must live and die with the avatar; inside the executor
  the switches, the egress check, the one avatar rule, the digest checks and the per-take re-check cover it with no
  new step type, linter arm, cache arm or change-request arm (AV-10's reasoning).
- **Order, nothing costs before the still:** switches, egress, declaration, the avatar (one rule), the pictures
  (picture roles only, the avatar's order, at most `AvatarStillStorage.MaxPictures` = 3, re-hashed), the speech
  (`IAvatarSpeechTrackSource`: a `Voiceover` step's `ok` lines joined in order with a 0.25 s pause, or a project
  audio file; first-party ffmpeg argv, `-format_whitelist wav,mp3`, mono 44.1 kHz WAV), its length against the
  model and the step's `DurationSeconds` (`AVATAR_AUDIO_UNAVAILABLE`, `AVATAR_AUDIO_LENGTH`, UserInput;
  `AVATAR_AUDIO_UNREADABLE` refunded), then prompt moderation.
- **The still** (`IAvatarStillMaker`): the default `ImageEdit` capability, fal.ai only (data URIs and the retention
  header are fal's), a catalogued instruction-edit model that takes an image array (default
  `google/nano-banana-2.1/edit`); otherwise `AVATAR_STILL_PROVIDER_MISSING` / `AVATAR_STILL_MODEL_UNSUPPORTED`
  (UserInput). The pictures are downscaled to 1536 px (JPEG, first-party argv) and sent as data URIs with
  `X-Fal-Store-IO: 0`; the step's prompt is wrapped in a fixed first-party instruction (`AvatarStillMaker.Instruction`:
  keep the face, one person, chest up, mouth closed) at the step's ratio. A failure is `AVATAR_STILL_FAILED`
  (refunded). After it is made the avatar is re-resolved: a revoke or change meanwhile deletes a still made now and
  fails the step with the avatar's code.
- **Storage**: `projects/{p}/agentFiles/avatars/{avatarId}/stills/{digest}.img` (`AvatarStillStorage`, shared by the
  engine, the delete routine and the sweep). A bare object: no `project_files` row, never an output, never in the
  media pool, never a step-output key (`meta.avatar.still` records `made`, `model`, `sha256` only). Content-addressed
  over the recipe version, the avatar fingerprint, the model, the ratio, the instruction and the pictures, so an
  unchanged avatar reuses it and buys nothing; a re-attested avatar gets a new one.
- **The take**: `Images = [still]`, `References = [speech]`, the prompt only where the model takes one, duration =
  the speech's whole seconds (what the take is reserved and billed at, never more than the step's
  `DurationSeconds`), `NoVendorRetention`. The ledger request key adds the speech and still digests (a new narration
  is a new clip). A change request's new prompt on a model with no prompt field is `CHANGE_NOT_APPLIED`.
- **Money**: the hold adds one still at `VideoGenerateCreditPricing.AvatarStillHoldUsd` ($0.063, the 2K card of the
  default still model; its 1K page figure is $0.044 for two inputs) to the takes at `DurationSeconds`; settlement
  charges the image client's metered edit (a reused still meters nothing) and the seconds delivered. No
  input-material charge.
- **Voice**: `VoiceoverStepConfig.VoiceFromAvatar` (appended, default false) speaks in the voice linked to the run
  brief's avatar (a brief that names a voice wins); no avatar or no linked voice is `AVATAR_VOICE_MISSING`
  (UserInput), and the voice then passes the voice consent guard. The step-cache avatar component keys such a
  Voiceover on the voice linked now.
- **Delete**: the one delete routine lists the stills folder (`IObjectListing`), marks every still in the delete's
  transaction, deletes the objects and reports `stills: {deleted, failed}` (`failed` -1 = not listed); the event
  records the count. A project delete marks every still of its avatars in the same save (marks outlive the
  project). The storage sweep's orphan pass scans `agentFiles/avatars/` and marks a still whose avatar is gone (a
  still uploaded during a racing delete, or one a delete could not list). Account purge removes the whole prefix.
- **Template** `talking-avatar-budget` ("Talking avatar (budget)", `AutoCreateOnProject` false): Scriptwriter, then
  Voiceover (`ScriptScenes`, `voiceFromAvatar`), then `VideoGenerate` on the H3 lip-sync route at 768P, 15 s, 9:16,
  `avatarAudio` = step 2, cap 250 credits, permission off, avatar from the brief. Needs a fal.ai `LipSync`
  model and a fal.ai `ImageEdit` model as the defaults.
- **Not built**: a vetted ComfyUI talking-head graph (Wan2.2-S2V, InfiniteTalk, MuseTalk are the open candidates;
  each needs its own vetted template) and a real C2PA signer.

## Choosing an avatar in the dashboard

The `VideoGenerate` step editor has an "Avatar" section (`VideoGenerateAvatarSection`, helpers `web/lib/avatars/step-picker.ts`) just above the egress switch, and an app whose manifest exposes the brief field `avatar` shows an "Avatar" select on the run page (`AvatarRunInput`); both render only while `GET .../avatars/requirements` answers (404 = switch off), list unusable avatars disabled with the Avatars tab's reason words, never touch `allowSourceMediaEgress`, and the step section greys out on a plan source or a frame. `avatar` is an `AppManifestVocabulary.BriefFields` member (no default, no choices); `AppManifestDeriver` adds the input only when a step has `AppStepFacts.TakesBriefAvatar` (inline, no avatar, no frame: the engine's `ApplyToVideoGenerate` condition), so the two avatar templates get it and a b-roll app does not; while `Avatars:Enabled` is false no step takes it (the API installs the live switch into `AppStepFacts` at startup), so no app gains an Avatar input, and the avatar recipes (`spokesperson-clip`, `talking-avatar-budget`, `avatar-clip`) are neither listed, suggested nor created. The run gate, the assistant's approval card and the engine's credit hold price a step with the brief's avatar filled in (`BriefAvatarPricing`, the executor's own rule), so the avatar input material and the still are held for a template that leaves the avatar to the run; WL015 and WL027 also judge the brief's avatar.

Both pickers list objects too, grouped by kind (people first), with variants shown as `Car · Red`, and hide the
speech controls for an object. A step or a run brief that names one object goes through the same single-avatar path
as a person (`AvatarId`, the reference mode of the model); the object then needs only the objects switch, its
fingerprint adds `kind=object` (a person's fingerprint never moves, `AvatarFingerprintStabilityTests`), and its clip
gets neutral machine-readable marking (`ai_generated=true`, no burned label). The step section picks one avatar or one
object; a step that shows up to three, how they reach each model and planned shots are described in "Several avatars
and objects in one clip" and "Avatars and objects in planned shots and the dailies room" below (those settings have no
control in the step editor yet).

### Still to measure for phase 3 (with a key)

Rows T1-T9 of [`fal-ai-models.md`](fal-ai-models.md) section 14: data URIs accepted for the still and the audio;
`X-Fal-Store-IO: 0` honoured on these routes; Kling's real audio bounds and resolution; H3 billing at the audio's
whole seconds; latency per take; identity hold of the still and the animation (two consenting subjects, ten prompts,
scored by people); the real token cost of a three-picture edit; faces the vendors refuse and their errors; the
vendors' own likeness terms.

## Object avatars

An object avatar (`subject_kind = 'Object'`, decision AO-1 of [`design/avatar-objects.md`](design/avatar-objects.md))
is a named set of pictures and short videos of a product, a vehicle, a building or anything that is not a person. It
needs no consent of a person, has no retention date and cannot be revoked. It reuses the consent columns for its own,
lighter attestation: `consent_statement` holds the object statement, `consent_subject_name` is empty,
`consent_basis` `Self` and `consent_subject_adult` false (both unused), `retention_until` is
`ProjectAvatar.NoRetention` (9999-12-31, never swept, never warned), `disclosure_label` is `MetadataOnly`, and the
consent revoke columns stay null.

- **Statement.** `AvatarObjectStatement`, version `avatar-object-2026-11`, en and it, compared ordinally like the
  person's (else `400 object_statement_required` with the texts). The member ticks it at create and again on a file
  change (`PUT …/references`).
- **Files.** 1 to 8 pictures and 0 to 3 videos, no audio (`reference_kind_not_allowed`); pictures take `Front`,
  `Angle` or `Detail`, videos `Motion` (`ProjectAvatarLimits.RoleFits(kind, fileKind, role)` is the one rule). The
  size, moderation, CSAM and held checks are the person's. An object may also reference a platform-made file (an
  extracted frame, a picture export, a render); a file synced from a connected source is still refused. Its files are
  not marked `source_kind = avatar_reference`: they stay ordinary media, and `409 FILE_IN_USE_BY_AVATAR` still
  protects them.
- **Person check** (`ObjectReferencePersonCheck`). Each picture, and one frame of each video, is shown to the default
  `Vision` provider with one fixed first-party question; nothing the member typed goes with it. The answer must be
  exactly `{"person":"yes"}` or `{"person":"no"}`. `yes` refuses the file (`400 object_reference_shows_person`). `yes` is for a person who is clearly a main subject (a face clearly visible and prominent, identifiable from the picture); an incidental person (background, far away, small, blurred, turned away or seen from behind, no face visible, in a crowd, not the main subject) and an anonymous body part such as a hand is `no`, and a doubt about whether a person is prominent enough to be a main subject is `no` (relaxed 2026-11-23; before, any recognisable body counted). A person who is large in the frame or prominent stays `yes` even when blurred, in shadow, a silhouette or turned away, and a doubt about whether a prominent face is identifiable is `yes`; prominence is judged by share of the frame, so the downscale of a big picture (long side 1568 px, pictures over 4 MB) cannot hide a prominent person. Each picture is judged alone: a person who only ever appears incidentally across several pictures is not refused (the confirmed statement still covers it). The statement the member confirms is unchanged; an
  error, a timeout or any other answer fails closed (`503 OBJECT_PERSON_CHECK_UNAVAILABLE`). **A Vision provider is
  mandatory for objects:** with no default `Vision` provider, every path that makes or verifies an object's pictures
  (create, replace the references, a variant from files, a variant made by the picture model, an extraction from a
  video) refuses with `409 VISION_PROVIDER_REQUIRED` ("Checking that a picture shows no person needs a vision model, and
  none is set up for this workspace. ..."; `reason: NO_PROVIDER`, `capability: Vision`, `canConfigure`) before anything is
  charged, queued or sent. A background build that finds no Vision provider when it runs fails with the same code and
  releases its credit hold. There is no skip: the Created event records `person_check: passed`; an older event that says
  `skipped` is history only (no avatar row carries the word, and every new picture made from it is checked again).
  `requirements.personCheck` says whether a provider is set up. Free to the customer, metered like indexing.
- **Limits and plan.** 20 objects per project, counted apart from the 20 people (`409 AVATAR_LIMIT_REACHED`). Plan
  feature `avatar_objects` (catalog key `avatarObjectsAllowed`: Trial off, Creator and up on, in both catalog copies;
  `402 PLAN_NOT_ENTITLED`). A clip that shows an object also needs `video_generation` through the step's own gate.
- **REST.** `POST /avatars` takes `subjectKind: "Object"`; a person-only field sent with an object is
  `400 field_not_for_objects`. `PATCH` takes name and description only. `POST …/revoke-consent` on an object is
  `409 AVATAR_NOT_A_PERSON`. `DELETE` removes the row, its reference rows and its stills and KEEPS the files
  (report `references { deleted: 0, failed: 0, kept: n }`). `GET /requirements` adds `kinds { person, object }` (each
  `{ enabled, entitled }`), `objectStatementVersion`, `objectStatements`, `objectRoles`, `objectLimits`,
  `personCheck`, `mediaConsent` and `library`. `ProjectAvatarResponse` adds `subjectKind` and `coversPictures`;
  `retentionUntil` is null for an object.
- **Web.** The Avatars tab has a People / Objects switch, a "New object" form in two steps (files, then name and the
  statement), kind-aware cards, badges and delete reports.

## The picture consent version

The first person statement, `avatar-consent-2026-11`, covers "AI videos" only. Decision AO-2 adds a second version,
`avatar-consent-2026-12` (`AvatarConsentStatement.Version`), whose text says "AI videos and pictures" ("video e
immagini" in Italian); the old strings stay under `Version2026_11` / `English2026_11` / `Italian2026_11` and their
SHA-256 pins. `For` and `Matches` answer the current version only, so every new attestation is made under it.

`ProjectAvatar.CoversPictures` is true for an object, and for a person whose statement version covers pictures
(`AvatarConsentStatement.CoversPictures`: `avatar-consent-2026-12` and the workspace text
`avatar-consent-workspace-2026-11`). A person attested under the old text keeps working for video and is refused in a
picture with `409 AVATAR_CONSENT_NO_PICTURES` until it is re-attested (`PUT …/references`, which moves it to the
current version). The web lists such a person disabled in the picture editor's picker, with that reason. The counsel
draft carries both texts: [`legal/drafts/avatars-privacy-terms.md`](legal/drafts/avatars-privacy-terms.md) section 3.

## The project media consent

One project-level agreement that the reference pictures and videos of the project's avatars and objects may be sent
to AI providers from the editors and the assistant: `projects.subject_media_consent_at` and
`subject_media_consent_by_user_id`. Routes `GET` / `PUT` / `DELETE /api/v1/projects/{projectId}/avatars/media-consent`
(View / Edit with a verified email / Edit), answering `{ consented, consentedAt }`; `PUT` keeps an existing record. It
is given only in the app (the row "Sending media to AI services" in the Avatars tab, and the one shared dialog the
editors and "Make with AI" open on `409 CONSENT_REQUIRED`); there is no assistant or MCP tool for it.

It does not touch workflow steps: a `VideoGenerate` step keeps its own stored `allowSourceMediaEgress`, which only the
app turns on. A run of an app may also allow sending media for that run through the app's own field; that rule is
[`design/app-inputs.md`](design/app-inputs.md) section 30 and the customer page
[`user-guide/workflows/app-inputs.md`](user-guide/workflows/app-inputs.md).

Who reads it: the picture editor with subjects, a clip with avatars, a variant made by the picture model, and the
segmentation half of pulling an object out of a video. Each of them also accepts a permission for one call when the
consent is not recorded (next section).

## The one media rule: allowing media for one call

Every surface that sends avatar or object media (and a workflow run, through the app field `allowMediaEgress`) asks ONE
rule when the project consent is not recorded and the call carries `allowMediaEgress: true`:
`IMediaEgressCallPermission` (`Services/Avatars/MediaEgressCallPermission.cs`, `GrantorAsync(MediaEgressCall)`, which
answers the grantor word of `MediaEgressPermission`). Surfaces (`MediaEgressSurface`): picture operations, subject clips,
variants, extraction, workflow runs. Ways in (`MediaEgressVia`):

- **A request:** the person's own app session on that surface's own route (a `POST`), or an API key holding
  `media.egress`. Never a scoped OAuth token, the engine acting for someone, a background worker or another route (the
  assistant runs a surface inside its own chat request, so it never counts as the app).
- **The assistant chat:** only inside a card that was shown and allowed (`AssistantMediaEgressApproval`), on a context
  the chat controller built (`ChatApproved`; the workflow-assistant route never). Such a card says so
  (`ApprovalCard.AllowsMediaEgress`) and always asks, in auto mode too.
- **MCP:** only while the user's "Let connected tools allow sending my media" switch
  (`application_users.mcp_media_egress_enabled`, `mcpMediaEgressEnabled` on `/api/v1/auth/settings`) is on, read on
  every call.

Anything else, or a missing database, is refused (fail closed). The grant counts for that one call: the project consent
stays as it is (`projects.subject_media_consent_at` is never written by it) and no stored step flag is switched on.

## The workspace avatar library

Decisions AO-5 .. AO-7: a workspace (organization) keeps people and objects in one library, never visible to another
organization; importing an item into a project makes a LINKED project avatar that follows the item. Service
`AvatarLibraryService`, controller `AvatarLibraryController` (`/api/v1/avatar-library`, `404` while neither avatar
switch is on and for another organization's item).

- **Storage.** The item holds its own copies of the media under
  `organizations/{organizationId}/avatar-library/{itemId}/{referenceId}.{ext}` (and the signed release), written
  only by the library service; the storage sweep's orphan pass removes objects nothing names any more.
- **Statements.** `AvatarWorkspaceConsentStatement` (`avatar-consent-workspace-2026-11`, a person, "AI videos and
  pictures in any project of this workspace") and `AvatarWorkspaceObjectStatement`
  (`avatar-object-workspace-2026-11`). A project consent never becomes workspace-wide by itself: publishing a person
  is a new attestation under the workspace text, by an Owner or Admin.
- **Who.** Publish, rename, confirm again, withdraw consent and delete: Owner or Admin of the active organization
  (`OrgAuthority.IsOrganizationAdmin`), in the app only. List and preview: any member. Import into a project and
  remove an import: Edit on that project, a verified email, the kind's switch, plan feature and project limit.
- **Routes.** `GET /?projectId=` (`{ items, library }`; each item says `usable`, `unusableReason`, counts,
  `importCount`, `imported` for that project, `canManage`), `GET /{id}`, `GET /{id}/references/{referenceId}/content`
  (pictures only), `POST /` (publish a usable project avatar that is not itself an import: the workspace statement
  verbatim, every reference re-hashed, bytes copied, then the source avatar becomes a linked import), `PATCH /{id}`
  (name, description; imports follow), `POST /{id}/reconfirm`, `POST /{id}/revoke-consent` (a person; stamps every
  import in the same transaction), `DELETE /{id}` (every import first, then the item; report
  `{ imports, objects }`). Project side: `POST /api/v1/projects/{projectId}/avatars/import` `{ libraryItemId }` →
  `201`, files copied inside the store to `userFiles/avatars/{name}/`, quota-checked, same digests, consent columns
  copied.
- **Limits and codes.** 100 items of each kind per organization (`409 LIBRARY_LIMIT_REACHED`), 50 imports per item
  (`409 LIBRARY_IMPORT_LIMIT`), `409 LIBRARY_NAME_TAKEN`, `AVATAR_ALREADY_IMPORTED`, `LIBRARY_ITEM_NOT_USABLE`,
  `AVATAR_NOT_USABLE`. On the project routes a linked import takes only its voice link on `PATCH`; `PUT references`
  and `revoke-consent` answer `409 AVATAR_FROM_LIBRARY`; `DELETE` removes the import and its copied files only.
  `ProjectAvatarResponse` adds `libraryItemId` and `fromLibrary`.
- **Transfer.** Moving a project to another organization answers `409 PROJECT_HAS_LIBRARY_AVATAR` while the workspace
  library holds a copy of one of its files or avatar references: remove the avatar from the workspace library first.
- **Quarantine and holds.** Publishing is refused like creating or re-attaching an avatar: a project with
  quarantined content or under the CSAM output hold answers `409 PROJECT_CONTENT_QUARANTINED` (a marker that cannot be
  read refuses too), and a source file that is quarantined, blocked by moderation or still waiting for its CSAM scan
  answers `409 AVATAR_NOT_USABLE` with `reason: reference_held`; nothing is copied. A hold that lands AFTER a
  publish reaches the copy: a library copy is held when any project file of the same organization with the same
  SHA-256 is quarantined. A held item is not served by the picture route (the same `404` as a missing picture), is
  listed with `usable: false` and `unusableReason: reference_held`, and an import is refused with
  `409 LIBRARY_ITEM_NOT_USABLE` (`reason: reference_held`). A failed read holds. Imports and builds never read library
  objects directly: an import makes ordinary project files that take the usual scan.
- **The hold survives deletion.** The live digest rule alone would lose a hold when its source row is removed, so a
  durable marker backs it: `avatar_library_references.held_at` and `held_reason` (`csam`, `moderation`,
  `content_marker`; migration `AddAvatarLibraryReferenceHold`, additive). The hold gate writes it whenever it sees a
  live hold, and `InferenceApiDbContext` writes it in the same save that removes a held `ProjectFile`,
  `ProjectAvatarReference` or `Project` (every API delete path goes through that save; set-based deletes of those
  tables are not allowed and a test checks it). The gate reads marker OR live rule. Nothing clears a marker: a file
  delete, a re-scan or a release of the source leaves the library copy held until an admin removes the item.
  The marker is stamped at verdict time (same save as the verdict), before a project transfer, and again on removal
  and on observation as backstops; see docs/csam.md.
- **A person's variants are the person.** Withdrawing a person item withdraws every library variant of it and every
  import of those variants in the same save; deleting a person item deletes its variants first (each with its
  imports); re-confirming the original gives its variants the same statement and keep-for date (a variant withdrawn
  on its own stays withdrawn). Whatever the stored state, a person variant whose original is revoked or expired is
  listed unusable, refused on import and not served. An object's variants keep the old behaviour: they stay as items
  of their own, ungrouped, when the original is deleted.
  The variants are read again inside the guarded section of a delete, so a variant published meanwhile is deleted too
  (the delete goes round again), never ungrouped; a variant linked after a withdrawal is unusable through the derived rule.
- **Retention.** `ProjectAvatarRetentionService` sweeps the library first: an item past its `retention_until`, or
  revoked for more than 30 days, is deleted with its imports; the 7-day warning goes to the organization's Owners and
  Admins (an import gets no warning of its own).
- **Web.** The Avatars tab: "From the workspace library" (picker and Import), a "Workspace library" badge on imports,
  "Save to the workspace library" for an Owner or Admin. Settings > Avatar library (`/app/settings/avatar-library`):
  rename, confirm again, withdraw consent, delete with its report; the nav link shows only while the library answers.
- **Variants** (migration `AddAvatarLibraryVariants`, expand-only): `avatar_library_items.variant_of_item_id` (self
  reference, `ON DELETE SET NULL`), `variant_label` (1..60) and `variant_description` (1..500), the library twin of
  the project columns, with the partial unique index `ux_avatar_library_items_variant_live_label` (a label once per
  original among live items). The rules are the project's, held at the item (`AvatarLibraryService.Variants.cs`): one
  level only, the kind of the original, at most 10 variants per item. Publishing a project variant carries its label
  and description to the item; when its original is linked to a library item (an import of it, or the avatar it was
  published from) the new item becomes a variant of that item (`409 LIBRARY_VARIANT_LIMIT_REACHED` /
  `LIBRARY_VARIANT_LABEL_TAKEN`, nothing written); publishing an original links, in the same save, the items its
  project variants were already published as. Importing a variant item into a project that holds an import of its
  original groups it under that import (the project's variant rules apply in place of the per-kind limit:
  `409 VARIANT_LIMIT_REACHED` / `VARIANT_LABEL_TAKEN`, nothing written); otherwise it is an ordinary avatar of the
  project that keeps its label, and importing the original later groups it (an import that cannot be grouped stays
  ungrouped, counted in the import event, never dropped). A linked import's label and variant description follow the
  item (`PATCH` of either answers `409 AVATAR_FROM_LIBRARY`). Deleting, withdrawing or the retention sweep of an
  original leave its variants as items: a delete ungroups them in its own save (`variants_ungrouped` in the event).
  List and get add `variantOfItemId`, `variantLabel`, `variantDescription`, `variantCount`; a grouped import counts
  only toward its original's 10, and the import's per-kind count leaves variants out, as every other way in does. The
  web groups variants under their original on Settings > Avatar library and in "From the workspace library"
  (`Car · Red`). A library item's label is set at publish and is not edited in the library yet.
- **Assistant and MCP:** `ListAvatarLibrary` and `ImportLibraryAvatar` (see "The avatar assistant and MCP tools").

## Avatar variants

Decision AO-8: the same car in another colour, the same bottle with another label. A variant is a complete avatar row
of its own (own files, attestation and selectability) grouped under its original by `variant_of_avatar_id`, with a
`variant_label` (1..60, unique per original among live rows) and a required `variant_description` (1..500, what sets
it apart). It shows as `Original · Label`. One level only: a variant of a variant attaches to the original. A variant
has the kind of its original, does not count toward the project's 20 per kind, and an original has at most 10
(`409 VARIANT_LIMIT_REACHED`, `VARIANT_LABEL_TAKEN`). Deleting an OBJECT original leaves its variants, ungrouped.

**A person's variants follow the person.** A variant of a person shows the same person under the same consent. Revoking
the original revokes every variant in the same save (same stamp, one `ConsentRevoked` event each); deleting it (by the
user, or by the retention sweep) deletes its variants first through the one delete routine, then itself; re-confirming
the original copies its new keep-until date to its variants; a revoked original cannot be re-confirmed, so no variant
comes back. The one selectability rule also reads the original (`UnselectableReason(..., originals)`): a person variant
whose original is revoked, deleted or missing is `consent_revoked`, one whose original expired is `retention_expired`,
in the API list and by id, the linter, the engine resolver and everything else that asks the rule. A person avatar
that is not a variant, and every object, answer exactly as before. Variants did not exist before this change, so there
is nothing to backfill.

- **From other files** (people and objects): `POST /avatars` with `variantOfAvatarId`, `variantLabel`,
  `variantDescription`, otherwise the normal create of the kind. A person's variant asks the full consent form again
  (these are new files of the person), with the subject's name and basis copied from the original.
- **Made by the picture model** (objects only; a person's pictures are never changed by a model here):
  `POST …/avatars/{id}/variants/estimate` (writes nothing) and `POST …/avatars/{id}/variants` `{ instruction,
  variantLabel, variantDescription?, name?, description?, referenceIds?, includeVideos?, consentStatement,
  consentLocale, allowMediaEgress? }` → `202 { jobId, state }`. Order: objects switch (404), Edit, verified email,
  plan `avatar_objects`, a usable original, the object statement, the media permission (else `409 CONSENT_REQUIRED`),
  a default `ImageEdit` model that takes an image array (`409 VARIANT_PROVIDER_MISSING` / `VARIANT_MODEL_UNSUPPORTED`;
  with `includeVideos` also a default image-capable `VideoGeneration` model, `VARIANT_VIDEO_MODEL_MISSING`, whose takes
  can be priced in credits, `VARIANT_VIDEO_MODEL_UNPRICED` (no price set up while billing is on, or a self-hosted
  ComfyUI model, which is budgeted in GPU minutes that a variant's take cannot carry), and the plan feature
  `video_generation`), the limits, credits (`402`), the paid run-rate bucket (`429`). The estimate answers these model
  codes as `unavailableReason` with no figure; a start answers them as `409`, before anything is held.
- **The job** (`IAvatarBuildService`, one background worker, row in `avatar_build_jobs`, kind `variant`): each chosen
  picture is re-hashed and changed by the instruction-edit model inside a fixed first-party frame, the first result
  going along as a reference for the next ones; metered as editor picture work, held up front, settled at what was
  metered. With `includeVideos` the first frame of each chosen video is grabbed, changed like a picture and animated
  again by the default image-capable video model, in the hidden workflow kind `AvatarBuild` (= 10; Retry and Resume
  refused, `AVATAR_BUILD_RETRY_NOT_SUPPORTED` / `AVATAR_BUILD_RESUME_NOT_SUPPORTED`; `TimelineRenderOrphanReaper`
  covers it). Results are project files under `userFiles/avatars/{original}/{label}/` with `source_kind =
  avatar_variant`, then the variant is created through the same create path as any object (person check included).
  No picture made = `VARIANT_FAILED`, hold released. States `queued`, `pictures`, `videos`, `creating`, then `ready`,
  `failed` or `cancelled`. A job is restart-safe (progress in `params_json`, `updated_at` as heartbeat).
- **Reading builds.** `GET …/avatars/builds`, `GET …/avatars/builds/{jobId}`
  (`{ jobId, kind, state, stage, progress { done, total }, name, avatarId?, failureCode?, message?, credits? }`),
  `POST …/avatars/builds/{jobId}/cancel` (idempotent; what was made is charged, the rest released).
- **Web.** Variants sit under their original's card as labelled chips; "Add a variant" offers "From other files" and,
  for an object, "Make with AI" (instruction, label, which pictures, videos or not, the estimate, the statement, the
  media permission dialog). A "Being made" list shows running builds with Cancel; the activity tray tracks them as
  job type `avatarBuild`.
- **Assistant and MCP:** `CreateAvatarVariant` (objects only; see "The avatar assistant and MCP tools").
- **In the engine:** the resolver gives a variant the description `<the avatar's own description> Variant: <what sets
  it apart>` (`ResolvedAvatar.DescribeVariant`; an original keeps exactly its own), so the shot planner's offer line and
  the scene-still instruction carry it as quoted, cleaned text. The offer line names a variant by its own name (by
  default `Car · Red`).

## Pulling an object out of a video

A second kind of avatar build (`kind = extraction`): `POST /api/v1/projects/{projectId}/avatars/extract-object`
`{ mediaRef, objectDescription, name, description?, atSeconds?, consentStatement, consentLocale, allowMediaEgress? }`
→ `202 { jobId, state }`, read through `GET …/avatars/builds/{jobId}`. Objects switch, Edit access, the object
statement.

1. Up to 6 moments of the video (`atSeconds`, else evenly spaced).
2. With a default `Segmentation` provider AND the media permission (recorded, or allowed for the call), each moment's
   still goes to the segmentation model with the description as its prompt; a miss drops that moment. Metered as
   `UsageKind.SegmentationImages`, held up front. Without either, whole frames are used. A video that is still an
   output's bytes (a render, `step_result:<id>`) goes only with a current publish verdict, the gate the picture editor
   applies to an output's bytes (`IStoredCopyGate.GateStoredCopyAsync` over the source video's key; an upload passes at
   once): the request answers `409 CONTENT_MODERATION_PENDING` (or the gate's other code, `422 CONTENT_BLOCKED`) and
   writes nothing, and the worker reads the verdict again before the first still is sent and ends the job with that
   code (hold released, or what a resumed job already metered charged).
3. One `AvatarBuild` hidden workflow of free `ExtractFrame` steps cuts each kept moment at full size: the box plus a
   12 % margin, at least 256 px a side (`ExtractionCrop`), PNG, registered under `userFiles/objects/{name}/`
   (`source_kind = avatar_extraction`).
4. The object avatar is created from the frames through the same create path (role `Front` for the first, `Angle`
   for the rest, every check). No usable frame = `failed` with `OBJECT_NOT_FOUND_IN_VIDEO`.

The order, for people in the video: with a segmentation model, each still is shown to the default `Vision` provider
BEFORE any still is sent out, and a still that shows a person refuses the whole extraction (`object_reference_shows_person`,
nothing was sent, the hold is released). The cut-out frames are checked again when the avatar is created. No default
`Vision` provider = `409 VISION_PROVIDER_REQUIRED` at the request (and the estimate), before anything is held; a running
job that finds none fails with the same code and releases its hold. Before a build job sends anything out, it reads
again what the request checked (Vision provider, file holds, the project's content-quarantine marker, the media
permission it was allowed under, and that the member who asked still has Edit access); a refusal ends the job with a
plain message.

States `queued`, `finding`, `cutting`, `creating`, then `ready`, `failed` or `cancelled`. There is no web entry point
yet (the build list shows such a job as "From a video"); the assistant and MCP start it with `ExtractObjectFromVideo`.

## Avatars and objects in the picture editor

`POST …/image-documents/{id}/operations` takes `subjectIds` (1 to 3, at most one person) for `generate` and
`instruct`; the contract, the order of the checks and the result layer's `content`
(`{ subjects, aiGenerated, disclosure }`) are in [`editor.md`](editor.md) ("Avatars and objects in a picture").
`generate` with subjects runs on the `ImageEdit` capability, so its price and availability are that route's:
`GET …/operations/generate/capabilities?subjects=true` describes and prices it. `GET …/avatars/{id}/usage` counts
such pictures as `picturesGenerated`.

Web: the AI tools "Create a picture" and "Edit with words" show a subject picker (`SubjectPicker`, people first, a
person without picture consent disabled with the reason). Once subjects are chosen for "Create a picture" the editor
asks the capabilities route with `subjects=true` and shows that route's price and availability, so a project with no
`ImageGeneration` service can still make a picture of its avatars when an `ImageEdit` model is set up. A
`409 CONSENT_REQUIRED` opens the shared media permission dialog, records the project consent and runs the same
request again. A layer made with subjects says "AI-generated" and who it shows on its row; export adds a small
"AI-generated" label in the bottom-right corner when a visible layer shows a person whose disclosure is `Visible`
(objects only: marked in the data, no label).

## Several avatars and objects in one clip

A `VideoGenerate` step may show up to three project avatars, people and objects, at most one of them a person (design
record "Subjects in a video generation step"; the full engine reference is
[`video-generation.md`](video-generation.md) "Several subjects per clip: people and objects, on every model").

- **Config** (`VideoGenerateStepConfig`, appended; a config that sets none of them is byte-identical): `Subjects`
  (`[{avatarId, roles?}]`, 1..3, distinct, one person at most, never with the legacy `AvatarId`, a step frame, or on a
  plan source), `SubjectMode` (`Auto` | `References` | `FirstFrame`), `[JsonIgnore] EffectiveSubjects` (the legacy
  `AvatarId` is "one subject"). A talking head (`AvatarAudio`) takes exactly one subject, a person. The save check
  (`VideoGenerateAvatarSaveValidator`) judges every subject: this project, the kind's switch, one person, consent, the
  talking head, and AO-3 on the step's own provider.
- **The path per model** (`VideoSubjectPaths.Decide`, from the model's own declarations, before anything is reserved;
  one table for the engine, the save check, the linter, the estimate and subject clips): `TalkingHead` (phase 3,
  unchanged), `References` (MiniMax H3 direct with pictures, videos and a person's speech samples; the three fal.ai
  reference-to-video routes with pictures only; slots shared in turns, every subject one picture before any a second,
  `VideoReferenceInput.SelectShared`), `FirstFrame` (every image-capable model: one composed scene still as the first
  frame), else `AVATAR_NOT_SUPPORTED_BY_MODEL` naming the image-capable sibling of the same family
  (`VideoSubjectSiblings.For`). Lint WL034 `subject-model-unsupported` (error, fix `chooseModel`, sentences
  `WL034.useSibling` / `WL034.noSibling`) says so before a run; WL015 `showsAnAvatar` and WL027 `avatar-unusable` judge
  every subject.
- **The scene still** (recipe `subject-scene-still-v1`, `ISubjectStillMaker`, the widened `IAvatarStillMaker`; the
  talking-head recipe `avatar-still-v1` is byte-identical): one fal.ai instruction edit over up to three pictures of each
  subject and six in all, 1536 px, data URIs, `X-Fal-Store-IO: 0`, a fixed first-party instruction naming each subject
  by kind and picture position with its description quoted. Stored under the person's stills folder when a person is
  shown, else the first object's; deleted with either avatar.
- **Checks and money:** every subject is resolved before anything is reserved and again before each take; the
  generation switch is asked per kind (a person: `Avatars:Enabled` + `Avatars:Generation`; an object: only
  `Avatars:Objects:Enabled`); the step's egress permission once. Holds: the reference material of every subject on
  `References`, one still per inline step on `FirstFrame`.
- **Records:** `meta.subjects[]`, `meta.subjectPath`, `meta.subjectFacts`, `subjectsFingerprint` per clip; the ledger
  row's `avatar_id` (the person, else the first object) and `subject_ids_json`. The legacy single-person `AvatarId` step
  keeps its exact request, request hash, cache component, `meta.avatar` and ledger row
  (`VideoGenerateLegacyAvatarPinTests`); on a model with no reference mode it now takes the `FirstFrame` path
  (`meta.avatar.path = "firstFrame"`). Objects only = the neutral marking; a person among the subjects = that avatar's
  disclosure.
- **Reuse:** the reuse guard, the step-cache component and a change request's carry follow the whole subject set, in
  order: a clip is reused only while every subject is usable and unchanged.

## Avatars and objects in planned shots and the dailies room

A plan-sourced `VideoGenerate` step (`PlanSource = StepRef`) lets the shot planner show the project's people and objects
(design record "Avatars and objects in planned shots"; detail in [`video-generation.md`](video-generation.md) "Avatars
and objects in planned shots" and "Dailies: choosing between takes").

- **When:** `PlannedSubjects = Auto` (the default; `Off` turns it off) and the step's own `allowSourceMediaEgress` on
  (the app field may switch it on for one run) and a model that can show subjects at all. `PlannedSubjectIds` (null =
  every usable subject) narrows the offer. Otherwise nothing is offered and every prompt, key and output is
  byte-identical.
- **The offer:** a `[PROJECT SUBJECTS]` block (`ProjectSubjectsBlock`, `Services/Avatars/PlannedSubjects.cs`), computed
  once at run start (`ProjectSubjectsRunState.LoadAsync`) and given to `ShotDirector` only: one line per usable subject a
  run may show, `a1b2c3d4 (person) "Anna": presenter`, names and descriptions cleaned and quoted as untrusted text,
  people first, at most 12; an offer id two subjects share is offered for neither (`SubjectOfferId`). It joins the
  planner step's cache key (`StepCacheKeyInputs.ProjectSubjects`, appended only when set, no schema bump).
- **The plan:** `ShotPlan.Subjects` (a string of offer ids, at most three, one person). The step resolves each shot's ids
  before anything is read or bought and reports drops in `meta.plannedSubjects.shots[].dropped` (`unknown_subject`,
  `subject_not_allowed`, `too_many_subjects`, `second_person`, `planned_subjects_off`, `egress_off`); a subject this run
  could show that is not usable now fails the step with the avatar's code. A shot with subjects takes no planned frame
  (`first_frame_dropped_for_subjects`). The kept subjects take the inline path table, prepared once per distinct set.
- **Money:** one still per planned shot is held while planned subjects can apply (`PlannedSubjects = Auto`, egress on,
  the project holds an avatar; the API and the engine read that fact the same way through `ProjectSubjectsPricing`).
- **The dailies room:** manifest clips that show subjects carry `subjects: [{name, kind}]`; `VideoAnalyze` over
  `GeneratedClips` copies them into `view.sources[].subjects`, and the room's judges, director and solo fallback get a
  names-and-kinds block and are asked to check that each subject looks like itself across takes (`TakeSelector` and
  `DailiesDirector`, both prompt copies). `TakeSelectionOutput` is unchanged.
- **The assistant:** the `ExecuteWorkflow` card of such a run says "May show the project's people and objects", naming
  none.

## Avatars in the timeline editor's media pool

The media pool has an "Avatars" tab (`SubjectsPanel`) beside Media and Sound effects while the project's avatars
answer, listing people and objects with their status and a "Generate clip" dialog (`SubjectClipDialog`: subjects,
prompt, model, length, shape, estimate, the media permission dialog). The server side,
`/api/v1/projects/{projectId}/subject-clips` and the hidden kind `SubjectClip`, is described in "Clips from the
editor" below.

## Clips from the editor

"Generate clip" in the editor's media pool (the "Avatars" tab, `SubjectsPanel` / `SubjectClipDialog`) makes a clip
that shows up to three of the project's avatars and objects, outside any workflow (design:
[`design/avatar-objects.md`](design/avatar-objects.md) "Generating a clip with avatars from the editor"). One service,
`ISubjectClipService` (`Inference.Api/Services/Avatars/SubjectClipService.cs`), sits behind the routes and the
assistant's tool, so they cannot disagree on a check, a price or a limit.

| Method | Path (under `/api/v1/projects/{projectId}`) | Access | What |
|---|---|---|---|
| `POST` | `/subject-clips/estimate` | Edit | `{ billingEnabled, credits, creditsHigh, balanceCredits, affordable, path, stillIncluded, unavailableReason, unavailableCode }`; writes nothing |
| `POST` | `/subject-clips` | Edit, verified email | `202 { executionId, state: "queued", credits }` |
| `GET` | `/subject-clips/{executionId}` | View | `{ executionId, state (queued\|running\|ready\|failed\|cancelled), outputFileIds[], message, failureCode, subjects[{ name, kind }] }` |
| `POST` | `/subject-clips/{executionId}/cancel` | Edit | `202 { executionId, state: "stopping" }`; `409 SUBJECT_CLIP_ALREADY_FINISHED` when it ended first |

Every route is a bare `404` while neither avatar switch is on, and another project's run, another kind's and a missing
one are the same `404`. The body is the web client's `SubjectClipRequest`
(`web/lib/editing/subjects/subject-clip-api.ts`): `subjects [{ avatarId }]` (1..3, one person at most), `prompt`
(1..2000), `capabilityId?` (a `VideoGeneration` model of the workspace or the platform, else `400 VIDEO_MODEL_NOT_FOUND`;
left out = the default one), `durationSeconds`, `ratio`, `resolution` (the model's own words), `takes` (1..2),
`speechFileId?` (a WAV or MP3 of the project: a talking clip, one person and nothing else), `subjectMode?`, and
`allowMediaEgress?` (below). The estimate and a start run the same checks, in this order, and a refusal writes nothing:

1. neither switch on: bare `404`; then project access (`IProjectAccessService`, once per action);
2. verified email (`403 EMAIL_NOT_VERIFIED`);
3. the subjects the ids name (`IProjectSubjectReader.FindAsync`): an unknown id, another project's or a kind switched
   off is `404 AVATAR_NOT_FOUND`; `400 too_many_subjects`, `duplicate_subject`, `too_many_people`; then, when a person
   is among them, who may generate with a person (`Avatars:Generation`, the engine's own rule): `Off`, or
   `PlatformAdminOnly` and a caller whose account row is not a platform admin (read on every call; a failed read
   refuses), is `409 AVATAR_GENERATION_DISABLED` at start and `unavailableCode` of the same name in the estimate (no
   figure); a clip of objects only is not affected;
4. the plan: `402 PLAN_NOT_ENTITLED` with `feature` `video_generation`, then `avatars` / `avatar_objects` per kind;
5. the media consent: the project's recorded `subject_media_consent_at`, or this call's `allowMediaEgress: true` from a
   caller the safe rule lets give it (`IMediaEgressCallPermission`, surface `MediaEgressSurface.SubjectClip`: a person
   signed in to the app, on `POST .../subject-clips` or its `/estimate`); else `409 CONSENT_REQUIRED` at start (the editor
   then asks once and records it through `PUT .../avatars/media-consent`). The estimate goes on and answers
   `unavailableCode: CONSENT_REQUIRED` beside the price;
6. the one rule over every subject (`IProjectSubjectReader.ReadAsync`, files re-hashed): `409 AVATAR_NOT_USABLE` with
   `reason`; then the speech of a talking clip (`400 speech_needs_one_person`, `400 speech_file_invalid`);
7. the model and the path (`VideoSubjectPaths.Decide`): `409 AVATAR_NOT_SUPPORTED_BY_MODEL` with `sibling` (the
   image-capable model of the same family, `VideoSubjectSiblings`). No video model, one that cannot be used or priced,
   and a `FirstFrame` / `TalkingHead` path with no fal.ai picture-edit model for the still answer
   `unavailableReason` + `unavailableCode` in the estimate (`VIDEO_MODEL_MISSING`, `VIDEO_MODEL_UNPRICED`,
   `AVATAR_STILL_PROVIDER_MISSING`, `AVATAR_STILL_MODEL_UNSUPPORTED`) and `409` with that code at start;
8. the settings against the model's own offer: `400 SUBJECT_CLIP_REQUEST_INVALID { message, errors[] }`. A talking clip
   is as long as its speech (the stored probe, rounded up, within the model's range);
9. credits: `402 INSUFFICIENT_CREDITS { message, missing }`; 10. the paid run-rate bucket (`IAssistantExecutionGuard`,
   source `subject_clip`): `429 RUN_RATE_LIMITED` with `Retry-After`; the plan's concurrency cap inside the queue is
   `429` too, and the workflow is deleted again.

A start then writes ONE hidden workflow, `WorkflowDefinitionKind.SubjectClip` (9), named
`Avatar clip: <names>`, with ONE inline `VideoGenerate` step: `Subjects` in the order chosen, `SubjectMode`,
`AvatarAudioProjectFileId` for a talking clip, `ProviderId` and `Model` pinned to the model it was priced on,
`Takes` = `MaxClips`, `AllowSourceMediaEgress = true` (set by the server only, because the media permission was
established; never recorded as the project consent), `MaxSpendCredits` = the estimate's high figure in credits rounded
up to the cent (never below the vendor's own price of the takes and the worst-case input material, so a
bring-your-own model held at nothing still has a positive cap; a ComfyUI model gets `MaxGpuMinutes` instead), the
step option `RegisterOutputAsProjectFile` (the takes become ordinary project video files) and `CacheMode = Never` (the
same request is never answered with an earlier run's takes). The estimate's `credits` is the engine's own hold for that
step (`VideoGenerateCreditPricing.TryHoldMillisAsync`, through the workflow cost estimator), so what is shown, what is
held and the step's cap agree. The kind is hidden like `VideoEnhance` (8) and `AvatarBuild` (10): every list, edit,
delete and execute path reads `Kind == Standard`, Retry answers `409 SUBJECT_CLIP_RETRY_NOT_SUPPORTED`, Resume
`409 SUBJECT_CLIP_RESUME_NOT_SUPPORTED`, and `TimelineRenderOrphanReaper` deletes a workflow of the kind that never got
an execution. `GET /api/v1/activity` lists the run with type `subjectClip` (the web's own job type) titled with the
subjects' names; the web stops a listed row through the cancel route.

Status: `queued` / `running` from the run; `ready` once it passed, with `outputFileIds` = the project files of the run's
delivered takes (keys under `agentFiles/generated/{executionId}/`, in take order; quarantined and held files left out);
a passed run whose result is held, or whose files cannot be found, is `failed` with `OUTPUT_UNAVAILABLE`; `failed`
carries the step's `failureCode` and sentence (`meta.failed` / `meta.error`). `subjects` names what the clip shows (an
avatar deleted since, or of a kind switched off, is left out). The engine still applies every check of its own at run
time (the one rule per take, the generation switch per kind, the egress permission, the budget gate), so a setting
changed between the start and the run fails the run with the engine's code, never shows a subject it should not.

The web words the refusals by their code (`web/lib/editing/subjects/subject-clip-api.ts` `describeSubjectClipRefusal`
and the dialog's `editor-media.subjects.dialog.refusal.*` keys, en and it; credits and settings refusals show the
server's sentence); `AVATAR_GENERATION_DISABLED` reads "Clips that show a
person are not available in this workspace yet. A clip with objects only can be made." in the estimate and on a refused
start.

## Object fidelity: facts and the take check

An object avatar carries its colour, make and model, distinguishing marks and size as short plain-word facts
(`identityFacts` in the avatar response). They are read once from the pictures by the Vision provider when the object is made
(one metered call, never a reason to refuse the create), and the owner can type or correct them (`PATCH` with
`identityFacts`) or have them read again (`POST /api/v1/projects/{projectId}/avatars/{avatarId}/identity-facts/derive`, which
keeps the owner's own words unless `overwrite` is true). An object made before this existed has no facts until one of those
is done; the shot planner and the video prompt are only as exact as the facts.

With facts, the shot planner is told them as the truth about the object, and each clip that shows the object gets one
sentence added to its prompt saying so. After each take is generated, one Vision call compares a frame of the take with the
object's pictures and facts; the verdict is `subjectFidelity.status` on the clip in the step output
(`match`, `mismatch`, `unsure`, `unavailable`). A mismatched take is not picked by the dailies room and is left out
of the cut when its shot has another take; a shot whose every take mismatches keeps its first take. With no Vision provider
the check is `unavailable` and nothing else changes. The app does not show the facts or the verdict yet (see
[`design/avatar-objects.md`](design/avatar-objects.md) "Object fidelity").

## Avatar objects: not built yet

Every lane of [`design/avatar-objects.md`](design/avatar-objects.md) is built. What is still missing, on purpose or for
want of a measurement:

- **No live vendor call was ever made.** The fal.ai reference-to-video routes, the scene still, the talking heads and
  MiniMax's reference mode were built from the vendors' documents and price pages only; every assumption is a
  must-measure row ([`fal-ai-models.md`](fal-ai-models.md) section 15 rows R1-R10, section 14 rows T1-T9, and "Still to
  measure with a live call" above). Subject hold with several subjects in one request (R7) is unmeasured.
- **Higgsfield `elements`** (Kling's own subject references) are not built: they take vendor-registered element ids,
  not pictures, so Higgsfield shows subjects through `FirstFrame` only.
- **Five fal.ai reference routes** were read and left out (`fal-ai/vidu/q4/reference-to-video`,
  `minimax/h3-max/reference-to-video`, `minimax/h3-max-turbo/reference-to-video` and the two
  `fal-ai/kling-video/o3/*/reference-to-video` tiers); the reasons, all about a price that cannot be stated per second,
  are in [`fal-ai-models.md`](fal-ai-models.md) section 15. Reference videos and reference audio are never sent on fal.
- **No control in the step editor** for several subjects: the `VideoGenerate` step's Avatar section picks one avatar or
  object; `Subjects`, `SubjectMode`, `PlannedSubjects` and `PlannedSubjectIds` are set in the step's config (a saved
  workflow, the assistant's workflow tools, the API). Several subjects in one clip from the app: the editor's
  **Generate clip**.
- **No web entry point for pulling an object out of a video:** the assistant and MCP start it
  (`ExtractObjectFromVideo`); the REST route exists and the Avatars tab lists the job.
- **A person's variant made by the picture model** is refused (AO-8's conservative choice, open to a later decision):
  people get variants from other files only.
- **A library item's variant label** is set at publish and cannot be edited in the library.
- **C2PA signing** is still the `NoProvenanceSigner` gap (phase 2), and no project-file column marks an avatar clip.

## Runbook: the person shown in an avatar asks privacy@ to revoke it

INTERNAL RUNBOOK. The Terms and Privacy Policy versions that cover avatars of people are live: `2026-11-05` (Terms 9.1, 11 and 18,
Acceptable Use, Privacy Part A; migration `AvatarsLegalVersions`), then `2026-11-10` (Terms) and `2026-11-11` (Privacy), then `2026-11-17` (both; migration `AvatarLibraryLegalVersions`;
published on 2026-11-17 by the owner's decision, with every user re-accepting the Terms). The `2026-11-17` texts
cover: sharing an avatar across the workspace through the avatar library (Terms 9.1, a person's consent has to cover the
whole workspace), pictures as well as videos, avatars of objects and the automatic check for people, variants, and in the
Privacy Policy the fal reference-to-video, picture-edit, lip-sync and segmentation routes with the makers behind them and
the Vision check. This page draws no legal conclusion and no lawyer has reviewed those texts.
The privacy policy tells a person who appears
in an avatar that they can write to privacy@ to have it withdrawn. The product owner chose a runbook, not a staff
endpoint: an operator uses what exists (the owner's own revoke in the app, then a documented database step). Every
deadline and every legal point below marked **needs counsel** must be confirmed by counsel before launch.

**1. Log the request.** Open a privacy ticket the day the mail arrives: date received, the requester's mail
address, what they name (account mail, workspace or project name, avatar name), and nothing else. The ticket is
the record for this request; keep it with the other privacy requests. **Needs counsel:** the response deadline
(GDPR Art. 12(3): one month, extendable) and whether an objection or consent withdrawal must stop processing
"without undue delay" before verification ends.

**2. Verify the requester is the person shown, collecting no more than needed.** Do not ask for an identity
document by default. Compare what the requester sends with what we already hold: the avatar's
`consent_subject_name` and, if needed, one of the avatar's reference pictures (look at it in the operator's own
session only; never send it to the requester or anyone else). Ask only for what closes the gap, for example a reply
from an address the account owner's records already associate with the person, or a short photo of the requester
holding a note with the ticket number, compared by eye and deleted after the decision. Record only "verified on
<date> by <operator>, method <words>" in the ticket, never the material itself. If the person cannot be verified,
say so and what would be enough. **Needs counsel:** the verification standard (Art. 12(6)) and whether an unverified
request should still trigger a precautionary revoke, given that a revoke only stops use and deletes nothing at once.

**3. Find the avatar.** Read-only queries, platform operator access to the database (never the assistant):

```sql
-- By the account the requester names (the owner's sign-in mail):
SELECT a.id AS avatar_id, a.name AS avatar_name, a.consent_subject_name, a.consent_revoked_at,
       a.retention_until, p.id AS project_id, p.name AS project_name, p.organization_id, u.email AS owner_email
FROM project_avatars a
JOIN projects p ON p.id = a.project_id
JOIN application_users u ON u.id = p.owner_id
WHERE lower(u.email) = lower(:'owner_email');

-- By the project or the avatar name the requester names:
SELECT a.id AS avatar_id, a.name, a.consent_subject_name, a.consent_revoked_at, p.id AS project_id, p.name AS project_name
FROM project_avatars a JOIN projects p ON p.id = a.project_id
WHERE p.name ILIKE :'project_name' OR a.name ILIKE :'avatar_name' OR a.consent_subject_name ILIKE :'subject_name';
```

Several matches: narrow by the subject name and the reference pictures; never revoke an avatar of someone else.
No match: tell the requester we found no avatar with what they gave and ask for the account or project name.

Also look in the workspace avatar library (`avatar_library_items`, same `consent_subject_name` column, scoped by
`organization_id`). A project avatar whose `library_item_id` is set is a linked import: its consent is the library
item's, and withdrawing it means withdrawing the library item, which stamps every import at once. Ask the
workspace's Owners and Admins to choose Withdraw consent on Settings > Avatar library; step 5 below covers a project
avatar only, and this runbook has no reviewed statement for a library item yet (**needs a reviewed SQL** before it is
used). An object avatar (`subject_kind = 'Object'`) has no consent to withdraw: if it shows a person after all, the
owner deletes it.

**4. Contact the account owner.** Mail the project's owner (and the workspace Owners) from support@: a person shown
in avatar "<avatar name>" of project "<project name>" asks that it be withdrawn; please open the project's Avatars
tab and choose Withdraw consent (or Delete) within **5 working days** (**needs counsel**: the period). Do not
forward the requester's mail or details unless they agreed to that. The owner's revoke in the app is the normal path:
it stamps the row and writes the `ConsentRevoked` event itself.

**5. If the owner does not act by the deadline, an operator revokes it in the database.** This does what the app's
revoke does: it stamps `consent_revoked_at` and `consent_revoked_by_user_id` once (never overwrites an earlier
revoke) and writes one `ConsentRevoked` row to `project_avatar_events`, in one transaction. `:operator_user_id` is
the operator's own platform-admin user id (the revoke must name a person). Writing the row moves `xmin`, so an owner
editing the avatar at the same moment gets a concurrency refusal rather than undoing the revoke.

```sql
\set avatar_id '00000000-0000-0000-0000-000000000000'
\set project_id '00000000-0000-0000-0000-000000000000'
\set operator_user_id '00000000-0000-0000-0000-000000000000'
BEGIN;
WITH revoked AS (
    UPDATE project_avatars
       SET consent_revoked_at = now(),
           consent_revoked_by_user_id = :'operator_user_id'::uuid,
           updated_at = now()
     WHERE id = :'avatar_id'::uuid
       AND project_id = :'project_id'::uuid
       AND consent_revoked_at IS NULL
    RETURNING id, project_id, consent_statement_version
)
INSERT INTO project_avatar_events
    (id, avatar_id, project_id, organization_id, kind, actor_user_id, occurred_at, consent_statement_version, outcomes_json)
SELECT gen_random_uuid(), r.id, r.project_id, p.organization_id, 'ConsentRevoked', :'operator_user_id'::uuid, now(),
       r.consent_statement_version, '{"via": "privacy_request"}'::jsonb
FROM revoked r
JOIN projects p ON p.id = r.project_id
RETURNING avatar_id, occurred_at;
-- Exactly one row returned: COMMIT. Zero rows: the avatar was already revoked (or the ids are wrong): ROLLBACK and re-check.
COMMIT;
```

`kind` is stored by name (`AvatarEventKind.ConsentRevoked`), `occurred_at` and `consent_revoked_at` share the
transaction's `now()`, and `outcomes_json` carries outcome words only, never the requester's name, mail or media
(the audit-event rule). If lane A2's in-app revoke writes extra outcome words, use the same words here. From the
commit on, the avatar is unusable everywhere (the API, the web picker, the assistant tool and the engine resolver all
read `consent_revoked_at` through the one rule); the retention sweep deletes it, with its reference files, 30 days
later. If the requester asks for deletion now, not only withdrawal, the owner's Delete in the app is the path; an
operator delete outside the app is out of scope of this runbook (**needs counsel** on whether erasure must not wait
for the 30-day sweep).

**6. Tell the account owner and confirm to the requester.** Mail the owner that the avatar was withdrawn at the
person's request and that videos already made with it are their files (the privacy request may extend to them:
**needs counsel**). Reply to the requester: the avatar was withdrawn on <date>, it can no longer be used, and it will
be deleted on <date + 30 days>. Do not name the account owner unless counsel says to.

**7. Close the record.** In the ticket: dates of receipt, verification, owner contact, revoke (and by whom: owner or
operator, with the event's `occurred_at`) and confirmation. The `project_avatar_events` row is the system record; it
survives the avatar's deletion for three years.
