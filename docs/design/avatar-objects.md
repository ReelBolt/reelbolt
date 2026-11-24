# Avatars: objects, every model, every surface

INTERNAL BACKGROUND. Status: approved by the product owner on 2026-11-16 (decisions `AO-1` .. `AO-4` below were
asked and answered that day; everything else follows the existing avatar design,
[`avatars.md`](avatars.md), and the patterns it names). Operator reference: [`../avatars.md`](../avatars.md). Built:
every lane (see "Build lanes for avatar objects"); what the build did differently is in "Deviations recorded at build
time", and what is still missing in the operator reference's "Avatar objects: not built yet".

This record is the contract every lane of the build works from. Names in `code font` are binding: two lanes that
never read each other's code meet on them.

## Goal of the avatar objects initiative

An avatar is no longer only a person. A customer can also keep an **object** (a product, a vehicle, a building,
anything that is not a person) as a named set of reference pictures and short videos, and show it in generated
videos and pictures. Objects need no consent of a person, no retention date and no revoke.

People and objects are usable from: workflow steps (inline and planned shots), the run brief, the timeline
editor (generate a clip into the media pool), the picture editor (generate and edit pictures; people too), the
assistant and MCP. The assistant can make an object avatar from project files or by pulling an object out of a
video. The shot planner and the dailies room know the project's avatars and objects and may use them.

## Decisions taken on 2026-11-16 (AO-1 .. AO-4)

| Id | Question | Answer |
|---|---|---|
| AO-1 | A person's photo uploaded as an "object" would skip consent. Guard? | One tick at creation ("these files show no identifiable person, and I may use them"), stored as a versioned statement, plus an automatic Vision check that refuses a picture showing a person. The check is mandatory: with no Vision provider an object is refused (`409 VISION_PROVIDER_REQUIRED`), and it fails closed when the configured one errors. |
| AO-2 | The person consent text `avatar-consent-2026-11` covers "AI videos" only. Pictures? | A new statement version `avatar-consent-2026-12` covers videos and pictures. An avatar attested under the old text keeps working for video and must be re-confirmed before it is used in a picture. Wording still for counsel. |
| AO-3 | A video model with no image input at all | Refused before anything is charged, at save, lint and run, with a message that names the image-capable model of the same family. No silent model change. |
| AO-4 | Switch and plan for objects | Own switch `Avatars:Objects:Enabled` (env `AVATAR_OBJECTS_ENABLED`, default false), so objects can go live independently of the people switch. (The Terms and Privacy versions are live: `2026-11-05`, `2026-11-10`, `2026-11-11` and now `2026-11-17`, published on 2026-11-17 by the owner's decision with re-acceptance of the Terms by every user; they cover workspace-wide sharing through the avatar library, pictures, avatars of objects and the providers added. No legal conclusion is drawn here and no lawyer reviewed them.) Own plan feature `avatar_objects`: Creator and up (Trial off). Video generation with any avatar still needs `video_generation`. |

Defaults taken without a question (same reasoning as the existing design; change by a new decision):

- At most **3 subjects** in one clip or picture, and at most **one of them a person**. One person keeps the
  consent re-check, the still storage (a still of a person lives and dies with that person's avatar) and the
  talking-head path unambiguous.
- A clip that shows only objects gets neutral machine-readable marking (`ai_generated=true`) and no burned label.
  A clip that shows a person keeps that avatar's disclosure label, as today.
- A picture made with a person avatar is flagged on its layer; the editor adds an "AI-generated" label at export
  when that avatar's disclosure is `Visible`.
- Sending reference media out of the host from the editors and the assistant needs one project-level consent,
  given once in the app (the video-enhance pattern). A workflow step keeps its own stored
  `allowSourceMediaEgress` switch, which the app UI turns on, or the assistant on the user's request through an always-asked card.
- Automated callers may still allow sending media **for one call**, safely (product owner, 2026-11-16; the rule
  is the one of the app field `allowMediaEgress`, see `app-inputs.md` "Permission to send media as an app field"):
  the call says so explicitly (`allowMediaEgress: true`), and a person granted the ability beforehand: in the
  assistant chat the approval card always asks (also in auto mode) and approving it is the consent; over MCP the
  user must have switched on "Let connected tools allow sending my media"; an API key needs the `media.egress`
  scope. The avatar tools that send media (`GenerateSubjectClip`, `ExtractObjectFromVideo` when it cuts with the
  segmentation model, the picture tools with subjects) take that argument under the same rule when the project
  consent is not recorded. None of them records the project consent or switches a stored step flag on.
- Person avatars are still created, re-attested, revoked and deleted only in the app. Object avatars can also be
  created by the assistant.

## Avatar subject kind (data model)

API migration `AddAvatarObjects` (expand-only), history `__EFMigrationsHistory_Api`:

- `project_avatars.subject_kind` text NOT NULL default `'Person'`, CHECK `IN ('Person','Object')`. Enum
  `AvatarSubjectKind { Person, Object }` (append-only, by name), property `ProjectAvatar.SubjectKind`.
- `project_avatar_references.role` CHECK widened with `Front`, `Angle`, `Detail` (enum `AvatarReferenceRole`,
  appended). Object pictures take `Front` / `Angle` / `Detail`; object videos take `Motion`; an object takes no
  audio. Person roles are unchanged. `ProjectAvatarLimits.RoleFits(subjectKind, kind, role)` is the one rule.
- `projects.subject_media_consent_at` timestamptz null, `projects.subject_media_consent_by_user_id` uuid null
  (the project-level consent to send avatar and object reference media to AI providers from the editors and the
  assistant).
- New table `avatar_build_jobs` (API-owned, no `Tenant` filter, guarded at the project; listed as unfiltered in
  `TenantBoundaryInventoryTests`), shared by variants and object extraction; see "Avatar variants".

Engine migration `AddExternalGenerationJobSubjects` (history `__EFMigrationsHistory_Workflow`):
`external_generation_jobs.subject_ids_json` jsonb null (every avatar id the take showed, people and objects;
`avatar_id` stays and holds the person when there is one, else the first object).

An object row reuses the consent columns for its own, lighter attestation (no schema churn, no nullable flips):

| Column | Person | Object |
|---|---|---|
| `consent_statement`, `_version`, `_locale` | the consent statement | the object statement (`avatar-object-2026-11`) |
| `consent_attested_by_user_id`, `consent_attested_at` | who attested the person's consent | who ticked the object statement |
| `consent_subject_name` | the person's name | empty string |
| `consent_basis` | `Self` / `WrittenPermission` | `Self` (unused) |
| `consent_subject_adult` | true | false (unused) |
| `consent_revoked_*` | revoke | always null (revoke is refused: `409 AVATAR_NOT_A_PERSON`) |
| `retention_until` | 1..730 days | `ProjectAvatar.NoRetention` (9999-12-31T00:00:00Z); never swept, never warned |
| `disclosure_label` | `Visible` / `MetadataOnly` | `MetadataOnly` |
| `project_voice_id`, `consent_document_file_id` | optional | null |

## The object statement and the picture consent version

`AvatarObjectStatement` (`ReelBolt.Shared/Avatars`, same shape as `AvatarConsentStatement`: `Version`, `English`,
`Italian`, `Locales`, `For`, `Matches`; never edited in place):

- Version `avatar-object-2026-11`.
- en: "I confirm that these files show an object, a product or a place and no identifiable person, and that I
  have the right to use them to generate AI videos and pictures in this project and to send them for that purpose
  to the AI providers this project uses."
- it: "Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e
  che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in questo progetto e di
  inviarli, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto."

`AvatarConsentStatement` gains a second constant pair, never touching the first:

- `Version2026_11 = "avatar-consent-2026-11"` with the existing `English` / `Italian` strings unchanged (their
  SHA-256 pins stay).
- `Version = "avatar-consent-2026-12"` (the current one) with `English` / `Italian` equal to the old strings
  except "to generate AI videos in this project" becomes "to generate AI videos and pictures in this project" and
  "per generare video con l'intelligenza artificiale in questo progetto" becomes "per generare video e immagini
  con l'intelligenza artificiale in questo progetto". The old strings move to `English2026_11` / `Italian2026_11`.
- `For` / `Matches` answer the CURRENT version only (a new attestation is always under the current text).
- `CoversPictures(version)` is true for `avatar-consent-2026-12` and for the workspace text
  `avatar-consent-workspace-2026-11` (both cover videos and pictures); false for `avatar-consent-2026-11`.

`ProjectAvatar.CoversPictures`: an object, or a person whose `ConsentStatementVersion` covers pictures. A person
avatar that does not is refused in pictures with `409 AVATAR_CONSENT_NO_PICTURES` ("Confirm this person's consent
again in the Avatars tab before using them in a picture."); re-attesting (PUT references) moves it to the current
version. The counsel draft (`docs/legal/drafts/avatars-privacy-terms.md`) gains the new version as a draft.

## The avatar selectability rule with objects

`ProjectAvatar.UnselectableReason(utcNow, references)` stays the one rule. For `SubjectKind = Object` it checks, in
order: `consent_missing` (no attestation time, no attesting user or an empty statement; the adult flag is not
read), then `reference_missing`, `reference_held`, `no_image_reference`. It never answers `consent_revoked` or
`retention_expired` for an object. For a person nothing changes (`ProjectAvatarSelectabilityTests` keeps its truth
table and gains the object rows).

## Avatar switches and plan features

- API `AvatarOptions`: `Enabled` (people, as today) and `Objects.Enabled` (`Avatars:Objects:Enabled`, env
  `AVATAR_OBJECTS_ENABLED`, default false). `AnyEnabled` = either. Compose and `.env.example` pass both to
  `inference` and `workflow-engine`.
- Every `/api/v1/projects/{projectId}/avatars/**` route is `404` while neither is on. A row of a kind whose switch
  is off is treated as absent (not listed, `404` by id), except delete and the file-in-use guard, which always work.
- Engine `AvatarGenerationOptions.ObjectsEnabled` (`Avatars:Objects:Enabled`). A person subject needs `Enabled` and
  `Generation` as today; an object subject needs only `ObjectsEnabled`. Otherwise `AVATAR_GENERATION_DISABLED`.
- Plan catalog key `avatarObjectsAllowed` (trial false; creator, pro, team, enterprise true) in BOTH catalog copies
  (`inference/.../Billing/plans.catalog.json` and the byte-identical `site/lib/pricing/plans.catalog.json`).
  `OrganizationEntitlementSet.AvatarObjectsAllowed` (omitted = allowed), `PlanGate.AvatarObjectsFeature =
  "avatar_objects"`, `PlanGate.CheckAvatarObjects`. Creating or re-attaching an object needs it; using an object in a
  picture needs it; using one in a video also needs `video_generation` through the step's own gate.

## Avatar REST contract with objects

All under `/api/v1/projects/{projectId}/avatars` (`ProjectAvatarsController`), camelCase JSON.

- `GET /requirements` adds: `kinds: { person: { enabled, entitled }, object: { enabled, entitled } }`,
  `objectStatementVersion`, `objectStatements { en, it }`, `objectRoles` (`["Front","Angle","Detail","Motion"]`),
  `objectLimits { maxImages: 8, maxVideos: 3 }`, `personCheck` (bool: a Vision provider is configured, so object
  pictures are checked), `mediaConsent { consented, consentedAt }`. `statementVersion` / `statements` are the
  CURRENT person text. `entitled` stays (= `kinds.person.entitled`).
- `POST /` body adds `subjectKind` (`"Person"` default, `"Object"`). For an object: `name`, `description?`,
  `consentStatement` + `consentLocale` carrying the OBJECT statement verbatim (else `400 object_statement_required`
  with `requiredStatement`, `statementVersion`, `statements`), `references [{projectFileId, role}]`. Person-only
  fields sent with an object are `400 field_not_for_objects`. Plan: `402 PLAN_NOT_ENTITLED` feature `avatar_objects`.
  Limit: 20 objects per project, counted apart from the 20 people (`409 AVATAR_LIMIT_REACHED`).
- Object reference checks (`AvatarReferenceChecks`): images 1..8 and videos 0..3, no audio
  (`reference_kind_not_allowed`), roles by `RoleFits`, the same size, moderation, CSAM and held checks as a person.
  An object may also reference a platform-made file (an extracted frame, a picture export, a render); a file synced
  from a connected source is still refused (`reference_file_source_not_allowed`). An object's files are NOT marked
  `source_kind = avatar_reference` (they stay ordinary media); the delete/replace guard `409 FILE_IN_USE_BY_AVATAR`
  still protects them.
- The person check (AO-1), objects only, after the other checks: each picture (and one frame of each video, from its
  existing poster or filmstrip derivative; none yet = `409 AVATAR_REFERENCE_PENDING`) is shown to the default Vision
  provider with a fixed first-party question; answer `yes` = `400 object_reference_shows_person` naming the file;
  provider error or timeout = `503 OBJECT_PERSON_CHECK_UNAVAILABLE`; no Vision provider = `409 VISION_PROVIDER_REQUIRED` (the
  check is mandatory, never skipped). The Created event records `person_check: "passed"`. Free to the customer (metered
  like indexing).
- Responses: `ProjectAvatarResponse` adds `subjectKind`, `coversPictures`; `retentionUntil` is null for an object.
- `PATCH /{id}`: an object takes `name` and `description` only. `PUT /{id}/references`: an object sends the object
  statement again, no retention. `POST /{id}/revoke-consent` on an object: `409 AVATAR_NOT_A_PERSON`.
- `DELETE /{id}` of an object: deletes the row, its references rows and its stills; the reference FILES are kept
  (ordinary project files). Report `references: { deleted: 0, failed: 0, kept: n }`.
- `GET /{id}/usage` also counts takes whose `subject_ids_json` holds the id, and pictures generated with it.
- Media consent: `GET` / `PUT` / `DELETE /api/v1/projects/{projectId}/avatars/media-consent` (View / Edit / Edit)
  → `{ consented, consentedAt }`. UI only; no assistant tool, no MCP tool, never a request-body field elsewhere.

## Subjects in a video generation step

`VideoGenerateStepConfig` (appended; all null/false = byte-identical to today):

- `Subjects : List<VideoGenerateSubject>?`, `VideoGenerateSubject(Guid AvatarId, List<AvatarReferenceRole>? Roles =
  null)`. 1..3 entries, distinct ids, at most one person (the engine checks kinds at run time; the save validator
  when it can read them). Mutually exclusive with the legacy `AvatarId` (which stays and means "one subject").
  `[JsonIgnore] EffectiveSubjects` unifies the two; `HasAvatar` is true for either.
- `SubjectMode : VideoSubjectMode` = `Auto` (default) | `References` | `FirstFrame`.
- `PlannedSubjects : VideoPlannedSubjects` = `Auto` (default) | `Off` (plan sources only), `PlannedSubjectIds :
  List<Guid>?` (null = every usable subject of the project; else the allowlist). `Auto` lets the shot planner use the
  project's avatars and objects as it sees fit (product owner, 2026-11-16) whenever the step's own
  `AllowSourceMediaEgress` is on; with egress off, or with no usable subject in the project, nothing is offered and the
  run is byte-identical to today.
- Shape rules: subjects never with a step `FirstFrame` / `LastFrame`; `AvatarAudio` (a talking head) only with exactly
  one subject, a person; `Subjects` on an inline step, `PlannedSubjects` on a plan step.

How a subject reaches a model (`VideoSubjectPath`, decided per model from its own declarations, before anything is
reserved):

| Path | When | What is sent |
|---|---|---|
| `TalkingHead` | the model declares `AnimatesStill` and the step has `AvatarAudio` | today's still-then-animate path, unchanged; person only |
| `References` | the model declares reference pictures (`ReferenceInputSupport.MaxImages > 0`) and `SubjectMode` is not `FirstFrame` | each subject's pictures (and videos, a person's speech samples) as reference items, shared out across subjects inside the model's counts |
| `FirstFrame` | the model declares an image input (`ImageInputSupport != None`) | ONE composed still showing every subject in the scene of the prompt, as the first frame |
| refused | none of the above (a text-only route) | `AVATAR_NOT_SUPPORTED_BY_MODEL`, message names the image-capable sibling (`VideoSubjectSiblings.For(kind, model)`) |

- `References` today: MiniMax direct (`MiniMax-H3*`). A fal reference-to-video route or Higgsfield's `elements` is
  added only when its request schema was read from the vendor's own documentation during the build; what could not
  be verified is listed in `docs/avatars.md` "Not built" and is served by `FirstFrame`.
- `FirstFrame` covers every image-capable model: fal image-to-video routes, Higgsfield Kling image-to-video, the
  ComfyUI `wan2.2-ti2v-5b-i2v` graph and MiniMax when `SubjectMode = FirstFrame`.
- The composed still is made by the generalised still maker (`ISubjectStillMaker`, the existing `IAvatarStillMaker`
  widened; the talking-head recipe `avatar-still-v1` and its instruction stay byte-identical). Scene recipe
  `subject-scene-still-v1`: default `ImageEdit` capability, fal only, an instruction-edit model that takes an image
  array; up to 3 pictures per subject and 6 in all, downscaled to 1536 px, data URIs, `X-Fal-Store-IO: 0`; a fixed
  first-party instruction that names each subject by kind and picture position, then the step prompt, at the step's
  ratio. Failure codes as today (`AVATAR_STILL_PROVIDER_MISSING`, `AVATAR_STILL_MODEL_UNSUPPORTED`, `AVATAR_STILL_FAILED`).
- Still storage: with a person in it, under that person's `agentFiles/avatars/{avatarId}/stills/`; objects only,
  under the first object's folder. Deleting an avatar of either kind deletes its stills folder.
- Money: a `FirstFrame` clip holds one still (`VideoGenerateCreditPricing.AvatarStillHoldUsd`) per inline step and
  one per planned shot (`MaxClips`) when planned subjects can apply (`PlannedSubjects = Auto`, egress on, and the
  project has a usable subject when the hold is planned); settlement charges the metered edit (a reused still
  meters nothing). `References` input material is priced as today over all subjects' material.
- One rule per subject, as today: resolved before anything is reserved, re-resolved before each take's submit, the
  fingerprint of every subject in the ledger request hash, the step-cache component and the reuse guard. A
  single-person legacy `AvatarId` step keeps its exact hashes and `meta.avatar`; a step with `Subjects` writes
  `meta.subjects[]` (`avatarId`, `name`, `kind`, `fingerprint`, counts, skipped) and `meta.subjectPath`.
- Marking: a person among the subjects = that avatar's disclosure, as today. Objects only =
  `GeneratedClipDisclosure.Neutral` (metadata `ai_generated=true`, `ai_generator=ReelBolt`, comment "AI-generated
  video (ReelBolt).", no label).
- Run brief: `avatar: { avatarId }` may name an object. No new brief field.
- Linter: WL015 `showsAnAvatar` and WL027 `avatar-unusable` judge every subject; new WL034
  `subject-model-unsupported` (error: the step's model takes no pictures; fix names the sibling).

## Avatars and objects in planned shots

- Offer id of a subject: `SubjectOfferId.For(avatarId)` = `"a"` + the first 8 hex digits of the id (`N` format).
  Stable, never reused, needs no stored map. `SubjectOfferId.Resolve(offerId, usableSubjects)` answers the one
  subject it names, or null (unknown or ambiguous).
- Ambient block `[PROJECT SUBJECTS]` (first-party frame, names and descriptions sanitized and quoted as untrusted
  text): one line per usable subject, `a1b2c3d4 (person) "Anna": presenter, dark hair` / `(object)`, at most 12,
  people first. Given to `ShotDirector` only when a `VideoGenerate` step of the run reads its plan with
  `PlannedSubjects = Auto` and `AllowSourceMediaEgress` on, limited to that step's `PlannedSubjectIds`; and to
  `TakeSelector` / `DailiesDirector` (names and kinds only) when the takes they judge showed subjects. Absent
  otherwise, so every existing run is byte-identical.
- `ShotPlan.Subjects : string?` (the invariant stays "all strings"): offer ids separated by spaces or commas, at most
  3, at most one person. The planner decides per shot, as it sees fit, and describes in `prompt` what the subject does.
- Plan-mode executor: per shot, resolve the named subjects (dropped with a reported reason: `unknown_subject`,
  `subject_not_allowed`, `too_many_subjects`, `second_person`, `planned_subjects_off`, `egress_off`), then the
  same path table as an inline step. A shot with subjects never also takes a planned `firstFrame` (the subjects win,
  reported `first_frame_dropped_for_subjects`). An unusable subject fails the step with the avatar's code, as inline.
- Manifest clips carry `subjects: [{ name, kind }]` and `subjectsFingerprint`. `VideoAnalyze` over `GeneratedClips`
  copies `subjects` names and kinds into `view.sources[]`, which is what the dailies room reads. `TakeSelectionOutput`
  is unchanged.
- Prompts change in both copies (class `DefaultPrompt` and `DatabaseSeeder`): ShotDirector, TakeSelector,
  DailiesDirector.

## Generating a clip with avatars from the editor (subject clips)

Shared service `ISubjectClipService` (`Inference.Api/Services/Avatars/SubjectClipService.cs`), used by the REST
routes and the assistant tool. Routes under `/api/v1/projects/{projectId}/subject-clips`:

- `POST /estimate` → `{ billingEnabled, credits?, creditsHigh?, balanceCredits?, affordable, path, stillIncluded,
  unavailableReason? }` (writes nothing).
- `POST /` → `202 { executionId, state, credits? }`; `GET /{executionId}` → `{ executionId, state
  (queued|running|ready|failed|cancelled), outputFileIds[], message?, failureCode?, subjects[{name, kind}] }`;
  `POST /{executionId}/cancel`.
- Body: `{ subjects: [{ avatarId }] (1..3, one person at most), prompt (1..2000), capabilityId? (a VideoGeneration
  capability; default = the default one), durationSeconds, ratio, resolution, takes (1..2, default 1),
  speechFileId? (a project audio file: a talking clip, one person only), subjectMode? }`.
- Order: switch per kind (404), Edit access, verified email, plan (`video_generation`, plus `avatars` /
  `avatar_objects` per kind), the media consent (`409 CONSENT_REQUIRED`), subject checks (the one rule), model path
  (`409 AVATAR_NOT_SUPPORTED_BY_MODEL` with `sibling`), config validation, credits preflight (`402`), paid run rate
  (`429`), then one hidden workflow (`WorkflowDefinitionKind.SubjectClip` = 9, name "Avatar clip: …") with one inline
  `VideoGenerate` step: `Subjects`, `AllowSourceMediaEgress = true` (set by the server only because the consent is
  recorded), `RegisterOutputAsProjectFile = true`, `MaxSpendCredits` = the estimate's high figure. Hidden from every
  list, edit, execute, retry and resume path like the other hidden kinds; `TimelineRenderOrphanReaper` covers it.
- The result is an ordinary project video file; the editor's media pool shows it. No new op (34 stays).

Web: the media pool gains a third tab "Avatars" (`components/editor/media/SubjectsPanel.tsx`): the project's people
and objects with status, "Generate clip" (`SubjectClipDialog`: subjects, prompt, model, length, shape from the
timeline, estimate, the one-time consent dialog), running jobs, and a link to the project's Avatars tab. It works in
the Full and Simple shells and on the phone surface through the same pool.

## Avatars and objects in the picture editor

`POST …/image-documents/{id}/operations`, operations `generate` and `instruct`: new field `subjectIds: [guid]` (1..3,
one person at most).

- Order after the existing checks: switch per kind (`404` as an unknown subject), plan feature per kind (`402`), media
  consent (`409 CONSENT_REQUIRED`), the one rule (`409 AVATAR_NOT_USABLE` with `reason`), `CoversPictures`
  (`409 AVATAR_CONSENT_NO_PICTURES`), then up to 3 pictures per subject (6 in all), each re-hashed against its
  attested digest.
- Model: an instruction-edit model that takes an image array (else `409 OPERATION_UNSUPPORTED` naming the ones that
  do). `generate` with subjects runs on the `ImageEdit` capability: the subjects' pictures are the image array and
  the prompt is wrapped in a fixed first-party instruction (`SubjectPictureInstruction`). `instruct` with subjects
  keeps the layer as the source and adds the subjects' pictures as references. With a person among the subjects the
  pictures go as data URIs with `NoVendorRetention`.
- The result layer's content records `subjects: [{ avatarId, name, kind }]`, `aiGenerated: true` and `disclosure`
  (the person's label, else `MetadataOnly`). `GET …/operations/{op}/capabilities` reports `subjects: { supported,
  maxSubjects, models[] }`.
- Web: `AiPanel` shows a subject picker (`SubjectPicker`, shared with the timeline dialog) for both operations; a
  person avatar without picture consent is listed disabled with the reason; export adds the "AI-generated" label
  when a visible layer's disclosure is `Visible`.
- Assistant: `ProposeRunPictureTool` / `ApplyRunPictureTool` take `subjects` (avatar names) for those two operations.

## Avatar tools for the assistant and MCP

Final set (built in L7, with the library and the variants): chat tools 142 → 153, registered without a thread
141 → 152, MCP 95 → 101 (pairs 45 → 50, collapsed 42 → 47). The tools live in `AssistantAvatarTools` (partials
`.Objects`, `.Variants`, `.Library`, `.Clips`) and call the same services as the REST routes (`ObjectAvatarCreator`,
`IAvatarBuildService`, `AvatarLibraryService`, `ISubjectClipService`).

| In-app | MCP | Risk | What |
|---|---|---|---|
| `ListProjectAvatars` (existing) | same | free, read-only | people and objects (`kind`), whether each covers pictures, its variants under it (`variantOf`, `variantLabel`, `variantDescription`, `variants`), `builds` (variants and extractions being made, and failed ones of the last day), `clips` (the newest clips with avatars: state, the avatars shown, the new files' names), whether the media consent is recorded |
| `ListAvatarLibrary` | same | free, read-only | the workspace library: names, kinds, status, counts, keep-until, how many projects use each, `inThisProject`; a variant item carries `variantOf` (its original's name) and its label and description |
| `ProposeCreateObjectAvatar` / `ApplyCreateObjectAvatar` | `CreateObjectAvatar` | free; always asks (never auto) | an object avatar from project files named by path through `ObjectAvatarCreator` (the person check included); the card shows the object statement and approving it is the tick; over MCP the caller passes `statement` verbatim and `locale` |
| `ProposeExtractObjectFromVideo` / `ApplyExtractObjectFromVideo` | `ExtractObjectFromVideo` | billed when frames are cut by the segmentation model; always asks | pulls an object out of a video (by path, up to 6 moments) into a new object avatar in the background (below); the statement as above |
| `ProposeCreateAvatarVariant` / `ApplyCreateAvatarVariant` | `CreateAvatarVariant` | free from files, paid by the picture model; always asks | a variant of an OBJECT by its original's name and a label, from other files or an `instruction` (+ `includeVideos`); a person's variant is refused with where to do it (the Avatars tab); the statement as above |
| `ProposeImportLibraryAvatar` / `ApplyImportLibraryAvatar` | `ImportLibraryAvatar` | free; always asks | a library item by name into the project (a linked import); a project that holds it already answers `alreadyImported` |
| `ProposeGenerateSubjectClip` / `ApplyGenerateSubjectClip` | `GenerateSubjectClip` | paid (the clip run's credit gate) | a clip with one to three avatars or objects by name (model by name, speech file by path); answers the run's `executionId` without waiting (for `GetExecutionStatus` / `WatchExecution`) |

The picture tools that take `subjects` (`ProposeRunPictureTool` / `ApplyRunPictureTool`) take an optional
`allowMediaEgress` too.

**One media rule for every caller.** `IMediaEgressCallPermission` (`Services/Avatars/MediaEgressCallPermission.cs`)
answers who gives the per-call permission to send media (`GrantorAsync(MediaEgressCall)`, the grantor words of
`MediaEgressPermission`), for every surface (`MediaEgressSurface`: picture operations, subject clips, variants,
extraction, workflow runs) and every way in (`MediaEgressVia`: a request, the assistant chat, MCP). A request: the
person's own app session on the surface's own route (POST), or an API key holding `media.egress`; never a scoped
OAuth token, the engine acting for someone, a background worker or another route (the assistant runs a surface inside
its own chat request, so it never counts as the app). The chat: only inside a card that was shown and allowed
(`AssistantMediaEgressApproval`), and only on a context the chat controller built (`ChatApproved`; the
workflow-assistant route never). MCP: only while `application_users.mcp_media_egress_enabled` is on, read on every
call. Anything else, or a missing database, is refused (fail closed). The grant counts for that one call: no stored
step flag is switched on and `projects.subject_media_consent_at` is never written. A card whose call allows it says
so (`ApprovalCard.AllowsMediaEgress`) and always asks, in auto mode too.

**The object statement in chat.** The card of an object-making call (create, extract, variant) carries the statement
(`ApprovalCard.Statement`); the gate sets `AssistantStatementApproval` for the call only when that card was shown and
allowed, and the tool accepts that as the tick. Over MCP the caller passes `statement` verbatim and `locale`; a call
without it is answered with the text to show.

Names, never ids, in every answer and argument (the clip's `executionId` is the one run handle, as on every
run-starting tool). No tool creates, re-attests, revokes or deletes a person avatar, and none deletes an object or a
library item, or saves to the library (the app does). `AssistantApprovalCatalog`, the card builder
(`AssistantApprovalCardBuilder.Avatars.cs`), `AssistantMcpToolSurface`, `AssistantToolAnnotations`,
`AssistantPlatformTools`, `ProjectHistoryAutoCapture`, both prompt copies ("People and objects"), the count pins and
the MCP fixture move together.

## Pulling an object out of a video

A second kind of build job of `IAvatarBuildService` (`kind = extraction` in `avatar_build_jobs`, see "Avatar
variants"; the moments and boxes live in `params_json`). REST twin
`POST /api/v1/projects/{projectId}/avatars/extract-object` → `202 { jobId, state }`, read through `GET …/builds/{jobId}`.

1. **Frames.** Up to 6 moments of the video (the caller's `atSeconds`, else evenly spaced). The `Stills` derivative
   is requested for them.
2. **Find the object** (optional): with a default `Segmentation` provider AND the recorded media consent, each still
   goes to the segmentation model with the description as its text prompt; a hit gives a box, a miss drops that
   moment. Metered `UsageKind.SegmentationImages` per still, held up front. Without either, whole frames are used and
   the answer says so. Before any still is sent, each is shown to the default Vision provider; a still that shows a
   person refuses the whole extraction (see "Deviations recorded at build time").
3. **Cut at full size.** One hidden workflow (`WorkflowDefinitionKind.AvatarBuild` = 10) of free `ExtractFrame`
   steps on the source video, one per kept moment: `At = Time`, `Crop` = the box plus a 12 % margin (grown to at
   least 256 px a side), PNG, `RegisterProjectFile = true` under `userFiles/objects/{name}/`.
4. **Make the object.** When the run finishes, a sweeper creates the object avatar from the registered frames through
   the same create path as the REST route (object statement of the request, role `Front` for the first frame, `Angle`
   for the rest, all checks, the person check included). No frame usable = `failed` with `OBJECT_NOT_FOUND_IN_VIDEO`
   (the segmentation hold is settled at what was metered).

States `queued` → `finding` → `cutting` → `creating` → `ready` | `failed`. `ListProjectAvatars` reports pending and
failed extractions by name.

## The workspace avatar library (global avatars)

Decisions AO-5 .. AO-7, taken on 2026-11-16: a global avatar belongs to ONE organization (a workspace library,
never visible to another organization; no platform stock library); it may be a person or an object; importing
it into a project makes a **linked** project avatar that follows the library item.

**Shape.** The library item is the master record and holds its own copies of the media in organization storage.
An import is an ordinary `project_avatars` row plus ordinary project files copied from the library, so the
Avatars tab, the pickers, the assistant, MCP and the engine resolver see a normal project avatar and need no
special case. The link is one column.

API migration `AddAvatarLibrary` (expand-only):

- `avatar_library_items`: `id`, `organization_id`, `subject_kind`, `name` (1..200, unique per organization among
  live rows), `description`, the same consent columns as `project_avatars` (`consent_statement`, `_version`,
  `_locale`, `consent_subject_name`, `consent_basis`, `consent_subject_adult`, `consent_attested_by_user_id`,
  `consent_attested_at`, `consent_revoked_at`, `consent_revoked_by_user_id`), `consent_document_storage_key` (null;
  the signed release, copied, never sent anywhere), `retention_until`, `disclosure_label`, `created_by_user_id`,
  `created_at`, `updated_at`, `xmin`. Addressable by a caller-chosen id, so it carries the named `Tenant` query
  filter (`TenantBoundaryInventoryTests`: one more filtered table).
- `avatar_library_references`: `id`, `library_item_id` (cascade), `kind`, `role`, `ordinal`, `content_sha256`,
  `storage_key`, `mime_type`, `file_name`, `size_bytes`, `width`, `height`, `duration_ms`, `attached_at`. Reached
  only through its item (no filter).
- `project_avatars.library_item_id` uuid null (FK `avatar_library_items`, `ON DELETE NO ACTION`: the library delete
  routine removes the imports first), unique `(project_id, library_item_id)`.

Storage: `organizations/{organizationId:D}/avatar-library/{itemId:D}/{referenceId:D}.{ext}` (and `…/consent.{ext}`),
written only by `AvatarLibraryService`; registered wherever an organization's storage is purged or swept.

**Statements** (`ReelBolt.Shared/Avatars`, same shape and rules as the others; both cover videos and pictures):

- `AvatarWorkspaceConsentStatement`, version `avatar-consent-workspace-2026-11`. en: "I confirm that I am the person
  shown and heard in these files, or that I have that person's written permission; that the person is 18 or older;
  that they have given informed consent to their face, body and voice being used to generate AI videos and pictures
  in any project of this workspace, and to these files being sent for that purpose to the AI providers those
  projects use; that I will keep a record of that consent; and that I will revoke or delete this avatar if they
  withdraw it." it: "Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso
  scritto di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del
  suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in qualsiasi
  progetto di questo spazio di lavoro, e all'invio di questi file, a tale scopo, ai provider di intelligenza
  artificiale usati da quei progetti; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo
  avatar se la persona ritira il consenso."
- `AvatarWorkspaceObjectStatement`, version `avatar-object-workspace-2026-11`. en: "I confirm that these files show an
  object, a product or a place and no identifiable person, and that I have the right to use them to generate AI
  videos and pictures in any project of this workspace and to send them for that purpose to the AI providers those
  projects use." it: "Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona
  identificabile, e che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in
  qualsiasi progetto di questo spazio di lavoro e di inviarli, a tale scopo, ai provider di intelligenza artificiale
  usati da quei progetti."

Wording for counsel, like every other statement. A project-scoped consent never becomes workspace-wide by itself:
publishing a person to the library is a new attestation under the workspace text.

**Who may do what.** Publish, rename, re-confirm, withdraw consent and delete: Owner or Admin of the active
organization (`OrgAuthority.IsOrganizationAdmin`), in the app only. List and preview: any member. Import into a
project and remove an import: Edit on that project (verified email, the kind's switch and plan feature, the
per-kind limit of the project).

**Routes** `/api/v1/avatar-library` (`AvatarLibraryController`; `404` while neither switch is on, and for another
organization's item):

- `GET /?projectId=` → `{ items, library }` (`library` = the same object as `requirements.library`, so the workspace
  page needs no project to read the statements); each item (`id`, `subjectKind`, `name`, `description`, `usable`, `unusableReason`
  (`consent_revoked` | `retention_expired`), `references { images, videos, audio }`, `primaryReferenceId`,
  `retentionUntil`, `importCount`, `imported` (for that project), `canManage`); `GET /{id}`;
  `GET /{id}/references/{referenceId}/content` (pictures only, `SafeContentPolicy`).
- `POST /` `{ projectId, avatarId, consentStatement, consentLocale, subjectIsAdult?, retentionDays? }`: publishes a
  usable project avatar that is not itself an import. Checks the workspace statement of the kind verbatim
  (`400 consent_statement_required` / `object_statement_required` with the texts), re-hashes every reference, copies
  the bytes to organization storage, creates the item, then turns the source avatar into a linked import of it. At
  most 100 items of each kind per organization (`409 LIBRARY_LIMIT_REACHED`). A name already used by a live item:
  `409 LIBRARY_NAME_TAKEN`. A source avatar that is not usable or is itself an import: `409 AVATAR_NOT_USABLE`.
  Refusals carry `{ code, message }`; a statement refusal also carries `statements { en, it }`.
- `PATCH /{id}` (name, description; imports follow), `POST /{id}/reconfirm` (the statement again, retention for a
  person; no file change: new files are a new item), `POST /{id}/revoke-consent` (a person; stamps every import in
  the same transaction), `DELETE /{id}` (removes every import through the project delete routine, then the item and
  its objects; report `{ imports: { deleted, failed }, objects: { deleted, failed } }`).
- `POST /api/v1/projects/{projectId}/avatars/import` `{ libraryItemId }` → `201` a project avatar: the library
  objects are copied inside the store to project files (`userFiles/avatars/{name}/…`, quota-checked), reference rows
  carry the same digests, the consent columns are copied, `library_item_id` is set. Already imported:
  `409 AVATAR_ALREADY_IMPORTED`. An unusable item: `409 LIBRARY_ITEM_NOT_USABLE`. At most 50 imports per item
  (`409 LIBRARY_IMPORT_LIMIT`), which bounds the propagation loops.
- A linked import in the project routes: `ProjectAvatarResponse` adds `libraryItemId`, `fromLibrary`; `PATCH` takes
  only the voice link; `PUT references` and `revoke-consent` answer `409 AVATAR_FROM_LIBRARY`; `DELETE` removes the
  import and its copied files and leaves the library item alone.
- `GET …/avatars/requirements` adds `library { enabled, canManage, consentStatementVersion, consentStatements,
  objectStatementVersion, objectStatements, maxItemsPerKind }`.

The retention sweep also deletes a library item past its `retention_until` or revoked for more than 30 days (with
its imports). The 7-day warning goes to the organization's Owners and Admins.

**Assistant and MCP:** `ListAvatarLibrary` (free, read-only: names, kinds, status, whether already in the project)
and `ProposeImportLibraryAvatar` / `ApplyImportLibraryAvatar` (MCP `ImportLibraryAvatar`; free; by name). Publishing,
withdrawing and deleting stay in the app. `ListProjectAvatars` marks an import (`fromLibrary: true`).

**Web:** the Avatars tab gains "From the workspace library" (a picker with pictures and Import), a "Workspace
library" badge on imports, and for an Owner or Admin "Save to the workspace library" on a project avatar. Workspace
settings gain an "Avatar library" page (list, rename, confirm again, withdraw consent, delete with its report, where
each item is imported).

With the library the assistant tool counts were planned at chat 151, registered without a thread 150, MCP 100
(pairs 49, collapsed 46); the final figures with the variants are in "Avatar tools for the assistant and MCP".

## Avatar variants

Decision AO-8 (product owner, 2026-11-16): an avatar can have **variants**: the same car in another colour, the
same bottle with another label. A variant is made either from other files, or by asking the picture model to
change the avatar's own pictures (and, when asked, its videos). The assistant and MCP can do both for objects.

**Shape.** A variant is a complete avatar row of its own (own reference files, own attestation, own
selectability), so every picker, the planner, the engine and the library use it with no special case. Two
columns group it: `project_avatars.variant_of_avatar_id` uuid null (self reference, `ON DELETE SET NULL`: when the
original is deleted its variants stay, ungrouped), `project_avatars.variant_label` text null (1..60, for example
"Red"; unique per original among live rows) and `project_avatars.variant_description` text null (1..500, required on
a variant: what sets it apart from the original, for example "matte red paint, black rims"). A variant holds the
classic avatar files (pictures, short videos, and for a person speech samples) plus that description. The
description is shown under the chip, given to the shot planner in the offer line, returned by the assistant's list
tool, and added as quoted, sanitized text to the scene-still and picture instructions so the model shows that
variant. For a variant made by the picture model it defaults to the instruction. One level only: a variant of a variant attaches to the
original. A variant has the kind of its original. It shows as `Car · Red` wherever a name is shown. Variants do
not count toward the 20-per-kind project limit; an avatar has at most 10 variants (`409 VARIANT_LIMIT_REACHED`).
API migration `AddAvatarVariants` (expand-only), which also creates `avatar_build_jobs` (below).

**From other files.** `POST /avatars` takes `variantOfAvatarId`, `variantLabel` and `variantDescription`; everything else is the normal
create of that kind (an object: the object statement; a person: the full consent form again, because these are new
files of the person, with the subject's name and basis copied from the original and not editable).

**Made by the picture model (objects only in this version).** A person's pictures are not changed by a model here:
a generated variant of a person would be new synthetic pictures of them used as references, which the consent
texts do not clearly cover. People get variants from other files only. This is the one conservative choice of
AO-8 and is open to a later decision.

Routes under `/api/v1/projects/{projectId}/avatars`:

- `POST /{id}/variants/estimate` → `{ billingEnabled, credits?, creditsHigh?, balanceCredits?, affordable, pictures,
  videos, unavailableReason? }` (writes nothing).
- `POST /{id}/variants` `{ instruction (1..1000), variantLabel, variantDescription? (default: the instruction),
  name? (default "<original> · <label>"), description?, referenceIds? (which of the original's pictures and videos to change; default all pictures),
  includeVideos? (default false), consentStatement, consentLocale (the object statement), allowMediaEgress? }` →
  `202 { jobId, state }`.
- `GET /builds` and `GET /builds/{jobId}` → `{ jobId, kind (variant | extraction), state, stage, progress { done,
  total }, name, avatarId?, failureCode?, message?, credits? }`; `POST /builds/{jobId}/cancel`.

Order of checks: the objects switch (404), Edit access, verified email, plan `avatar_objects`, the original is a
usable object (the one rule), the object statement, the media permission (the recorded project consent, or an
explicit per-call `allowMediaEgress` under the safe rule; else `409 CONSENT_REQUIRED`), a default `ImageEdit`
model that takes an image array (`409 VARIANT_PROVIDER_MISSING` / `VARIANT_MODEL_UNSUPPORTED`), `includeVideos`
also needs a default `VideoGeneration` model with an image input and the `video_generation` plan feature, the
limits, credits (`402 INSUFFICIENT_CREDITS`), the paid run-rate bucket (`429`).

The job (`IAvatarBuildService`, one background worker, table `avatar_build_jobs`: `id`, `project_id`,
`organization_id`, `requested_by_user_id`, `kind`, `source_avatar_id`, `source_media_ref`, `instruction`,
`object_description`, `name`, `variant_label`, `params_json`, `state`, `stage`, `done`, `total`, `execution_id`,
`avatar_id`, `statement_locale`, `allowed_by`, `failure_code`, `message`, `hold_id`, `created_at`, `updated_at`;
API-owned, no `Tenant` filter, guarded at the project; it replaces the `avatar_object_extractions` table named
earlier in this record, which was never built):

1. **Pictures.** Each chosen picture of the original is re-hashed against its attested digest, then changed by the
   instruction-edit model: the picture is the source, the instruction is wrapped in a fixed first-party frame
   ("Change only what is asked. Keep the object's shape, proportions, details, the camera angle and the background
   exactly as they are."), and from the second picture on the first finished result goes along as a reference so
   every view gets the same change. Metered as editor picture work (`UsageSource.Editor`), held up front for all
   pictures, settled at what was metered. A picture that fails is skipped; no picture made = the job fails
   (`VARIANT_FAILED`, hold released).
2. **Videos** (only with `includeVideos`): the first frame of each chosen video is grabbed (free `ExtractFrame`),
   changed like a picture, and animated again from that frame by the default image-capable video model for the
   original's length (clamped to the model). One hidden workflow (`WorkflowDefinitionKind.AvatarBuild` = 10, shared
   with object extraction) runs the grabs and the takes, through the usual budget gate and ledger. The result is a
   video made again from the changed picture: the movement can differ from the original, and the UI says so.
   Changing a video frame by frame is not built.
3. **Files.** Results are ordinary project files under `userFiles/avatars/{original}/{label}/` with
   `source_kind = avatar_variant` (accepted as object references), quota-checked.
4. **The variant.** Created through the same create path as any object (all checks, the person check included),
   `variant_of_avatar_id` and `variant_label` set, roles copied from the pictures they came from.

States `queued` → `pictures` → `videos` → `creating` → `ready` | `failed` | `cancelled`.

**Assistant and MCP:** `ProposeCreateAvatarVariant` / `ApplyCreateAvatarVariant` (MCP `CreateAvatarVariant`): the
original by name, a label, then either `files` (other project files) or `instruction` (made by the picture model,
with `includeVideos`); objects only; always asks; paid when the model makes it. `ListProjectAvatars` shows each
variant under its original and lists running builds.

**Web:** variants sit under their original's card as labelled chips; "Add a variant" offers "From other files" and
"Make with AI" (instruction, label, which pictures, videos or not, the estimate, the statement, progress). Pickers
show `Car · Red`. The planner's offer line reads `(object) "Car", variant "Red": matte red paint, black rims`.

**Library:** `avatar_library_items` carries `variant_of_item_id` and `variant_label` too; publishing a variant whose
original is linked to a library item links the two, and importing a variant re-groups it under the original's import
when that is in the project.

With variants the assistant tool counts are: chat 153, registered without a thread 152, MCP 101 (pairs 50,
collapsed 47), as built (see "Avatar tools for the assistant and MCP").

## Build lanes for avatar objects

| Lane | Scope | Status |
|---|---|---|
| L1 | data model, statements, rule, switches, plan feature, REST for objects, person check, media consent, engine mapping and resolver, `ListProjectAvatars` | Built |
| L2 | web: Avatars tab with objects (en + it) | Built |
| L3 | engine: `Subjects`, the path table, the scene still, siblings, pricing and holds, marking, linter, native reference routes that verify | Built (three fal.ai reference routes; Higgsfield `elements` not built) |
| L4 | planned shots and the dailies room (offers, `ShotPlan.Subjects`, prompts in both copies) | Built |
| L5 | subject clips: service, hidden kind, REST, the editor's pool tab | Built (web tab first, then the API) |
| L6 | picture editor: operations, web picker, export label | Built |
| L7 | assistant and MCP tools, object extraction, counts, fixtures | Built (the extraction job itself came with L12) |
| L8 | operator and customer docs, `CLAUDE.md`, the whole engine test suite | Built (with three small fixes, below) |
| L9 | workspace avatar library: tables, storage, statements, service, routes, import, propagation, sweep | Built |
| L10 | web: library picker, "Save to the workspace library", the workspace "Avatar library" page | Built |
| L11 | the media permission as an app field (see `app-inputs.md`) | Built |
| L12 | avatar variants: columns, `avatar_build_jobs`, the build service and worker, routes, web | Built (with object extraction) |
| L13 | variants in the workspace library (`AddAvatarLibraryVariants`) | Built |

## Deviations recorded at build time

What the build did differently from the text above, as the lanes reported it. Everything else follows the record.

- **Statements.** `CoversPictures` is also true for the workspace person text `avatar-consent-workspace-2026-11` (it
  says "videos and pictures"), not only for `avatar-consent-2026-12`.
- **Reference routes (L3).** Besides MiniMax direct, three fal.ai reference-to-video routes serve the `References` path
  (`alibaba/wan-3.0/reference-to-video` by default, `fal-ai/veo3.1/reference-to-video`,
  `fal-ai/veo3.1/fast/reference-to-video`), pictures only; five more were read and left out because no per-second price
  can be stated for them (`docs/fal-ai-models.md` section 15). Higgsfield `elements` take vendor-registered ids, so
  Higgsfield stays on `FirstFrame`. A ratio word that is not an aspect ratio (`native`, `adaptive`) is not sent to the
  picture model of the scene still.
- **The legacy `AvatarId` (L3).** On a model with no reference mode, the one-avatar step now takes the `FirstFrame` path
  (`meta.avatar.path = "firstFrame"`) instead of being refused; on a reference model its request, hash, cache component
  and ledger row are unchanged.
- **Planned shots (L4).** The `[PROJECT SUBJECTS]` block also needs a model that can show subjects at all, and lists only
  the subjects this run may show (the per-kind switch; a person in the `PlatformAdminOnly` smoke mode only in a platform
  admin's run). A variant appears in the offer line under its own name, with `Variant: <what sets it apart>` added to
  its description (`ResolvedAvatar.DescribeVariant`), not in the form `"Car", variant "Red"`; the scene still gets the
  same description.
- **Subject clips (L5).** The body also takes `allowMediaEgress` (the one media rule); the estimate adds
  `unavailableCode`; the step is never served from the step cache (`CacheMode = Never`), its spend cap is never below the
  vendor's own price of its takes, and a ComfyUI model gets `MaxGpuMinutes` instead; the activity feed lists the run
  as `subjectClip`; a stop after the end is `409 SUBJECT_CLIP_ALREADY_FINISHED`.
- **Picture editor (L6).** Every other operation answers `400 subjects_not_supported` to `subjectIds`; the capabilities'
  `subjects` object adds `maxPeople` and `provider`, and `?subjects=true` prices `generate` on the `ImageEdit` route;
  the answer adds `subjectPicturesSkipped`.
- **Assistant (L7).** One rule, `IMediaEgressCallPermission`, replaced the per-surface seams for every caller, REST
  included. A person's variant asked of a tool is refused with `VARIANT_OF_PERSON_IN_APP`.
- **Object extraction (L12).** The frames are registered as project files by the API worker
  (`source_kind = avatar_extraction`, `userFiles/objects/{name}/`), not by the `ExtractFrame` steps
  (`RegisterProjectFile = false` there). There is no web entry point; the assistant and MCP start it.
- **Library variants (L13).** The library item carries `variant_description` too, with a partial unique index on the
  live labels of one original (`ux_avatar_library_items_variant_live_label`), and an import's per-kind count leaves
  variants out.
- **Final pass (L8).** The Inference API reads `Avatars:Generation` (env `AVATARS_GENERATION`, now passed to
  `inference`), so a subject clip that shows a person is refused up front with `409 AVATAR_GENERATION_DISABLED` instead
  of failing in the engine; a variant's videos on a default model that cannot be priced in credits (no price, or a
  ComfyUI model budgeted in GPU minutes) are refused up front with `409 VARIANT_VIDEO_MODEL_UNPRICED` instead of being
  silently left out; and an extraction from a render sends its stills to the segmentation model only with a current
  publish verdict (`IStoredCopyGate`).
- **Vision is mandatory (review of the objects PR).** The person check is no longer skipped when no default Vision
  provider is configured. Create, replace the references, a variant from files, a variant by the picture model (its
  outputs), an extraction (its stills and its outputs) refuse `409 VISION_PROVIDER_REQUIRED` (`reason: NO_PROVIDER`,
  `capability: Vision`, `canConfigure`) before anything is charged, queued or sent; an estimate of a build refuses the
  same way (for a variant it answers `unavailableReason: VISION_PROVIDER_REQUIRED`). A running job that finds no provider
  fails with the same code and its hold is released. The event word `skipped` is never written now; an old one is
  history, and the avatar row carries no such flag, so nothing needs a migration or a re-check pass.
- **Extraction checks before it sends (review).** Every still that has not been looked at is shown to the Vision provider
  before ANY still is sent to the segmentation model. A still that shows a person refuses the whole extraction
  (`object_reference_shows_person`), the same as a person in an output frame; nothing was sent, so the hold is released.
  A check that cannot answer waits like the create stage and times out as `AVATAR_CHECKS_TIMED_OUT`. The output frames
  are checked too, as before. Without a segmentation model nothing leaves the host, so the frames are checked as outputs.
- **Worker re-checks (review).** Before a build job sends anything out (source pictures to the picture model, first frames
  to the video model, stills to the segmentation model) it re-reads from the database what the request checked: the
  Vision provider, file holds (quarantine, CSAM and moderation state, the output hold) and the digest of each source
  file, the project's content-quarantine marker, the media permission the job was allowed under (a withdrawn project
  media consent stops it), and that the member who asked is still a member with Edit on the project. The pictures stage
  and the videos stage share one helper (`RecheckBeforeSendingAsync`). A refusal ends the job with a plain message; what
  was already metered is settled, with nothing metered the hold is released.
- **A person's variants follow the person (review).** A variant of a person shows the same person under the same
  consent, so: revoking the original revokes every variant in the same save (same stamp, one `ConsentRevoked` event
  each, `original_revoked: true`); deleting the original deletes its variants first, each through the one delete routine
  (files, copies, vendor-side cleanup), then the original, so none is left as an ungrouped usable avatar (the retention
  sweep does the same, as it calls that routine); the keep-until date of the original, when it is re-confirmed, is copied
  to its variants. Revoked consent is final, so no variant comes back. Defence in depth in the one rule
  (`ProjectAvatar.UnselectableReason` with the originals, read by `AvatarVariantOriginals`, used by the API list and by
  id, the linter fact, the brief splitter, the step save check, the assistant, the subject reader and the engine
  resolver): a person variant whose original is revoked, deleted or missing is `consent_revoked`, one whose original
  expired is `retention_expired`. An OBJECT's variants are unchanged: deleting the original ungroups them. No backfill:
  the variant columns arrive with this change (migration `AddAvatarVariants` is not on the main branch), so no person
  variant exists in production.
- **Switch parsing (review).** `Avatars:Enabled`, `Avatars:Objects:Enabled` and `Avatars:Generation` are read tolerantly
  by one helper (`AvatarSwitchConfiguration`) in both the Inference API and the engine, so they cannot disagree. A value
  that cannot be read is Off (`Avatars:Generation`, accepted: `Off`, `PlatformAdminOnly`, `On`; a number is not
  accepted) or false (a bool, accepted: `true`, `false`). It is logged once at startup at Error naming the key and the
  accepted values; no request throws because of it.
- **Publish respects quarantine (library).** `PublishAsync` refuses a project with `projects.content_quarantined_at`
  or an active CSAM output hold with `409 PROJECT_CONTENT_QUARANTINED` (the code create, re-attach and import use for
  that condition; an unreadable marker refuses), and refuses per file: a source file that is CSAM-quarantined,
  moderation-quarantined or (while the CSAM control is on) still pending or scanning is `409 AVATAR_NOT_USABLE`,
  `reason: reference_held`. The checks run before any byte is copied.
- **Holds reach library copies (library).** Library copies live outside `project_files`, so no verdict path names
  them. Instead of a stored flag, `AvatarLibraryHoldGate` judges a copy at read time by its content: held when any
  project file of the SAME organization with the same SHA-256 (read from the file row and from the avatar reference
  rows that attested it) is quarantined by CSAM or moderation, or is a platform-made output at or before its
  project's content-quarantine marker. A future verdict path needs no change. A reference does not record its source
  project, so the project marker applies through the files, not through the item. The picture route answers the same
  `404` as a missing picture, the list and item read show `usable: false` with `unusableReason: reference_held` (no
  new field), import answers `409 LIBRARY_ITEM_NOT_USABLE`, and a read that fails holds everything. No migration.
  `AvatarLibraryHoldTests.Every_library_read_path_goes_through_the_hold_gate` fails if a library read path stops using
  the gate.
- **The hold survives deletion (library).** `AvatarLibraryHoldGate` holds a copy on `avatar_library_references.held_at`
  (a durable marker, migration `AddAvatarLibraryReferenceHold`) OR on the live digest rule. A security review found
  that deleting the source file or avatar-reference row after a verdict made the live rule forget it (shown by
  `AvatarLibraryHoldSurvivesDeletionTests`: the hold vanished for a file, a reference row and a project, for CSAM,
  moderation and the content marker). Fix: the gate stamps the marker when it sees a hold, and `InferenceApiDbContext`
  stamps it inside every save that removes a held `ProjectFile`, `ProjectAvatarReference` or `Project`. The engine's own
  deletes and cascades are covered too (0f6eb3470, 588c19b3f, 84a6d5db4). A transfer of a project that has a copy in
  its workspace library is refused (`409 PROJECT_HAS_LIBRARY_AVATAR`), so a later verdict can still reach that copy.
- **A person's variants follow the original (library).** Revoke stamps the variants and their imports in the same
  save, delete removes a person's variants first (an object's variants are still ungrouped), reconfirm carries the
  statement and keep-for date to the variants and their imports, and the list, import and picture treat a person
  variant of a revoked or expired original as unusable with the original's reason. No migration for this part.


## Object fidelity: identity facts and the take check (broll-film-quality-and-avatar-fidelity T-003)

Status: built in the same change as this note, except the parts marked "not built".

### What went wrong (run 21bb6f46, read-only on the production database)

Workflow "Reel C3 con b-roll AI", model `MiniMax-H3`, object avatar `47b53817` (6 pictures of a silver Citroen C3, one
`Front` and five `Angle`). The VideoGenerate step output records `subjectPath: "references"` and
`referencesSent: { images: 6 }` for both planned shots, and `external_generation_jobs.subject_ids_json` holds the
avatar. So the photos WERE sent as `reference_image` items; "the photos are never sent" is ruled out.

What reached the model besides the pictures was the planner's own `prompt`: "... a compact **dark** hatchback parked on
a wet city street **at night**, ... warm cinematic grade ...". The `[PROJECT SUBJECTS]` line the planner had was
`a47b53817 (object) "C3": La C3 estratta dal video C3.mp4`: a description with no colour (the avatar was extracted from a
video; the description is only the filename). The planner never knew the car is silver, and invented "dark" from the
brief's night mood. A text prompt that states a colour and a scene beats reference pictures in a reference-to-video
model, and nothing in the pipeline compared the result with the pictures afterwards.

Root causes (proven): (1) the avatar carries no colour or identity data (only a free description), so the planner and the
video prompt cannot be told the facts; (2) nothing after generation checks that a take still shows the object.
Hypothesis, not proven here: how strongly MiniMax-H3 weighs the pictures against the prompt (the fix does not depend on it:
it removes the contradicting words and checks the result).

### Identity facts (data)

- `project_avatars.identity_facts` text null (API migration `AddAvatarIdentityFacts`, expand-only; the engine maps the
  column read-only through `ProjectAvatarModel`). JSON of `AvatarIdentityFacts { colour, makeModel, marks, scale }`,
  each at most 120 characters of plain words (control characters, quotes, brackets and angle brackets stripped on every
  write), all absent = null. Objects only (a person gets none: `400 field_not_for_people`). Not copied to the
  workspace library in this change (not built: a library import starts without facts and can derive or type them).
- Derived once at creation by the default Vision provider (`IAvatarIdentityDescriber`, one call over up to 3 of the
  object's pictures, fixed first-party question, strict JSON answer, no customer text sent, metered like the person check:
  the indexing usage source, attributed to the organization and project). Fail open: no provider, an error, a timeout or an
  unreadable answer leaves the facts empty and never refuses a create. A later `POST
  /api/v1/projects/{projectId}/avatars/{avatarId}/identity-facts/derive` (Edit) derives them for an existing object (this is
  how the C3 gets its colour); `PATCH` with `identityFacts` lets the owner type or correct them. The facts always belong
  to the owner: derivation never overwrites a field the owner typed unless `overwrite: true` is sent.
- The stored JSON keeps non-ASCII text as written (relaxed encoder, never rendered raw into HTML) and `ToJson` cuts the
  longest field until the serialised value fits `MaxStoredChars` (1200, the column), so no input can overflow the save.
- Accepted gap: the derive route has no per-user rate limit and no credit preflight (no existing helper for metered vision
  routes); it needs Edit permission, reads at most 3 pictures, and is attributed to the organization and project.
- The facts join `AvatarFingerprint` for objects only when present, so a clip made before the facts existed or under other
  facts is not reused for the changed avatar; an object without facts keeps its exact fingerprint.

### Facts as hard constraints (engine)

- `ResolvedAvatar.IdentityFacts` carries the parsed facts. The `[PROJECT SUBJECTS]` planner line appends
  `; facts: colour silver; make and model Citroen C3; ...`, and the preamble says the planner must not contradict them and
  must not write another colour, make or scale for the subject in a `prompt`.
- The video prompt of every shot (and inline step) that shows an object with facts gets one first-party clause appended
  before sanitising: `The subject must match the reference pictures exactly: colour silver; make and model ...` (text
  from `AvatarIdentityFacts.Clause`, never model-supplied, no ids). The clause goes through `GeneratedPromptSanitizer`
  with the prompt; if the combined prompt would exceed the provider cap the clause is dropped (the shot is never failed
  for it, and the check below still runs). A change request's new prompt gets the clause too.
- The scene-still instruction (`SubjectSceneStill.Instruction`) states the facts too.
- Not built: rewriting a planner prompt that already contradicts the facts (a colour word mismatch is only caught by the
  check below, not edited out).

### Post-generation check (engine)

- After a take is normalised and before it is uploaded, `ISubjectFidelityCheck` runs for a clip that shows at least one
  OBJECT subject and has reference pictures in memory: ONE Vision call per take with up to 3 of the object's pictures, one
  frame of the take (ffmpeg, 1 second in, at most 768 px) and the facts as text. The fixed question asks whether the object
  in the frame is the same object as in the pictures (colour, make, model, marks) and answers strict JSON
  `{"match":"yes"|"no"|"unsure"}`. Metered through the chat-client factory against the step's ambient usage scope, like the
  other engine Vision calls (no separate hold: there is no new hold kind; the call is small and charged as the step's usage).
- Result on the manifest clip, `subjectFidelity: { status }`, status one of `match`, `mismatch`, `unsure`, `unavailable`
  (no Vision provider, error, timeout, unreadable answer, no picture or frame), `skipped` (no object, check switched off).
  The step output clip carries the same `subjectFidelity`. Fail open: `unavailable` never fails or retries the step and
  never rejects the take; a `mismatch` is not retried either (a retry is a paid take; the owner decides).
- A mismatch REJECTS the take for selection: `VideoAnalyze` over `GeneratedClips` copies the status into
  `view.sources[].subjects[].fidelity`, the dailies room drops a selection of a mismatched take when the same shot has a
  take that is not mismatched (`droppedMismatchedTakeCount` on `room`), and `VideoCompile`'s take filter never lets a
  mismatched take claim a shot that has a take that is not (`take_subject_mismatch`). A shot whose every take mismatches
  keeps today's behaviour (the first delivered take) so no shot is silently emptied; the badge below tells the owner.
- A clip that also shows a person is not checked (the sent pictures are one pool and cannot be told apart per subject);
  a talking head is not checked. A take reused from the ledger carries no verdict (it was not generated now).

### Not built

- Web: the avatar editor field for the facts, the "Derive from pictures" button and the "Does not match the object" badge on a
  mismatched take are NOT built in this change (the facts and the verdict are in the REST response and the step output, so
  the app can show them next; the en and it i18n keys go with that change).
- Library items carry no facts (a library import starts without them: derive or type them).
- The check judges the take's one frame, not the whole clip.
- No automatic retry of a mismatched take (it is a paid take; the owner decides).
- The planner prompt is not rewritten when it contradicts the facts; the clause is appended and the check catches the rest.
