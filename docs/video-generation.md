# Video Generation

Generate new video clips from a text prompt inside a workflow. You register a provider —
`MiniMax`, `Higgsfield`, `FalAi` or the self-hosted `ComfyUI` — with a `VideoGeneration` model capability, add a
`StepType.VideoGenerate` step with a prompt or a
plan plus a spend cap, and the step submits one clip per take, polls it, downloads it, normalizes
it with ffmpeg, and stores it in the project's object storage. Every submission passes a pre-submit
budget gate first.

Phase 1 of issue #93 in the project's private tracker built the provider layer:
one provider (MiniMax), one operator-written prompt per step. Phase 2 added the planner. A
`ShotDirector` agent reads the same bounded `VideoAnalyze` view the story editor decides from and
plans the shots; the same deterministic step then buys one clip per planned shot under the same
spend caps. A `VideoGenerate` step therefore has two plan sources: `Inline`, phase 1's single
prompt, and `StepRef`, a planner step's plan.

Higgsfield is the second provider kind, and one thing separates the two: Higgsfield publishes no
price table, so its price is fetched from the vendor's own authenticated estimate endpoint through a
bounded, cached price oracle. [The budget
gate](#the-budget-gate) states what that costs the pricing contract and what it degrades. For the
surrounding engine (step types, executors, provider resolution) see `CLAUDE.md`; for the editing
steps that consume a generated clip, including where the compile step places one, see
[`video-editing.md`](video-editing.md).

---

## Table of Contents

- [How a generated clip reaches the rest of a workflow](#how-a-generated-clip-reaches-the-rest-of-a-workflow)
- [Planning the shots](#planning-the-shots)
- [MiniMax facts this feature relies on](#minimax-facts-this-feature-relies-on)
- [Higgsfield facts this feature relies on](#higgsfield-facts-this-feature-relies-on)
- [Set up a MiniMax provider](#set-up-a-minimax-provider)
- [Set up a Higgsfield provider](#set-up-a-higgsfield-provider)
- [Step config reference](#step-config-reference)
- [The budget gate](#the-budget-gate)
- [Egress: sending the project's own frames to a provider](#egress-sending-the-projects-own-frames-to-a-provider)
- [Starting a generated clip from a frame](#starting-a-generated-clip-from-a-frame)
- [Resolutions per model, and the run's resolution](#resolutions-per-model-and-the-runs-resolution)
- [Job lifecycle and crash-resume](#job-lifecycle-and-crash-resume)
- [Step output](#step-output)
- [How a planned clip is addressed](#how-a-planned-clip-is-addressed)
- [Where the clips go in the edit](#where-the-clips-go-in-the-edit)
- [Dailies: choosing between takes](#dailies-choosing-between-takes)
- [Look matching and camera continuity](#look-matching-and-camera-continuity)
- [Remotion background plates](#remotion-background-plates)
- [Self-hosted ComfyUI](#self-hosted-comfyui)
- [fal.ai facts this feature relies on](#falai-facts-this-feature-relies-on)
- [Set up a fal.ai provider](#set-up-a-falai-provider)
- [Accepted risks](#accepted-risks)
- [Step cache](#step-cache)
- [Storage layout and normalization](#storage-layout-and-normalization)
- [Configuration](#configuration)
- [Workflow templates](#workflow-templates)
- [Out of scope for phases 3, 4 and 5](#out-of-scope-for-phases-3-4-and-5)

---

## How a generated clip reaches the rest of a workflow

`VideoGenerateStepExecutor` sets the step result's `OutputStorageKey` to the storage key of the
**first** take that was delivered. A later `VideoAnalyze` step reads it through its
`VideoSourceRef` like any other rendered output:

- `Source: PreviousStepOutput` picks the latest step in the execution with a non-null
  `OutputStorageKey`.
- `Source: StepOutput` with `StepOrder` set to the `VideoGenerate` step picks it explicitly.

See [Picking the source video](video-editing.md#picking-the-source-video). Clips after the first —
further takes on the `Inline` path, further planned clips on the `StepRef` path — are stored and
listed in the step's `clips` output, but no `VideoSourceRef` kind addresses them. On the plan path
the manifest is what maps a clip id to its object (see
[How a planned clip is addressed](#how-a-planned-clip-is-addressed)).

`VideoGenerate` is a deterministic, non-LLM step: it has no agent of its own and reuses the seeded
`AgentType.VideoTransform` row to satisfy `WorkflowStep.AgentDefinitionId`, like
`VideoAnalyze`/`VideoCompile`. It never throws; every failure is a structured JSON output.

## Planning the shots

`AgentType.ShotDirector` is an LLM agent that plans generated shots from the same bounded
`VideoAnalyze` view the story editor decides from. It is an ordinary `Agent` step — there is no new
step type for it — implemented in
`inference/src/ReelBolt.WorkflowEngine/Agents/Production/ShotDirectorAgent.cs`, with
`OutputSchemaName = "GeneratedShotPlanOutput"`. It is seeded as a built-in agent row; its system
prompt exists in two places, the class's own `DefaultPrompt` and `DatabaseSeeder.BuiltInAgents`,
and `ShotDirectorPromptConsistencyTests` asserts the two stay byte-identical.

It reads two things, which is why a template runs it with `AgentInputContextMode.FullWorkflow`:

- the bounded `VideoAnalyze` view, whose `view` offers the `s{n}` (shot), `g{n}` (silence gap) and
  `t{n}` (transcript segment) cut-anchor ids a planned shot may hang off;
- the story editor's decision, so it can avoid anchoring a shot to a moment the edit cut away.

It emits `GeneratedShotPlanOutput` — `{ shots, planRationale }`, where each shot is:

```json
{
  "purpose": "Cutaway",
  "anchor": "s3",
  "firstFrame": null,
  "lastFrame": null,
  "camera": "PushIn",
  "duration": "Short",
  "prompt": "A slow push across a sunlit desk…",
  "reason": "Covers the weakest stretch of picture inside a kept run."
}
```

`ShotPlan` is exactly those eight properties plus the optional `subjects` (below), and every one of
them is a plain string (`firstFrame`, `lastFrame` and `subjects` are nullable). `purpose` is one of
`Cutaway`, `SeamBridge`, `Extend`, `ColdOpen`, `EndCard` or `ScreenContent`; `duration` is one of
`Short`, `Medium` or `Long`. An empty `shots` list is a fully valid outcome — an edit needs no
generated shot.

### Avatars and objects in planned shots

Design record: [`design/avatar-objects.md`](design/avatar-objects.md) "Avatars and objects in planned shots". The
planner may show the project's own people and objects in a shot when it fits the brief, as it sees fit. It is told about
them in a `[PROJECT SUBJECTS]` block (`ProjectSubjectsBlock`, `Services/Avatars/PlannedSubjects.cs`), appended after the
run's other blocks, one line per subject: `a1b2c3d4 (person) "Anna": presenter, dark hair`. The first word is the offer
id (`SubjectOfferId`: `a` plus the first 8 hex digits of the avatar id); names and descriptions are cleaned (no quotes,
brackets or control characters, names at most 80 characters, descriptions 200) and quoted as untrusted text. People come
first, at most 12, and an offer id two subjects share is offered for neither.

The block is computed once at run start (`ProjectSubjectsRunState.LoadAsync`) and given to `ShotDirector` only when the
run has a `VideoGenerate` step that reads its plan from a planner step (`PlanSource = StepRef`) with
`PlannedSubjects = Auto`, its own `allowSourceMediaEgress` on and a model that can show subjects at all (the path table
below; a text-only model offers none). The subjects are the usable ones by the one rule, of a kind whose switch is on (a
person in the `PlatformAdminOnly` smoke mode only in a platform admin's run), limited to the steps' `PlannedSubjectIds`
(a step without one allows every subject). Otherwise there is no block, and the prompt, the step-cache key and every
output are byte-identical. When present, the block joins the planner step's cache key (`StepCacheKeyInputs.ProjectSubjects`,
appended only when set; no schema bump).

The planner names them per shot in `subjects`: offer ids separated by spaces or commas, at most three, at most one person,
and it describes in `prompt` what each does, by its kind and look, never by its name. The plan-sourced step resolves each
shot's ids against the project's usable subjects before anything is read or bought, and reports what it drops in
`meta.plannedSubjects.shots[].dropped` (`{subject, reason}`):

| Reason | When |
|---|---|
| `unknown_subject` | the id names no usable subject this run may show |
| `subject_not_allowed` | the step's `PlannedSubjectIds` does not list it |
| `too_many_subjects` | a fourth subject |
| `second_person` | a second person |
| `planned_subjects_off` | the step has `PlannedSubjects = Off` |
| `egress_off` | the step's permission to send media is off |

An id of an avatar this run could show that is not usable now (revoked, expired, a reference changed) fails the step with
the avatar's own code, as an inline step does. A shot with subjects never also takes a planned `firstFrame`/`lastFrame`:
the subjects win, the frame is not cut (no Analysis needed for it) and its clips report `keyframeReason:
first_frame_dropped_for_subjects`. The kept subjects then take the same path table as an inline step (references, or one
composed still made from that shot's own screened prompt), prepared once per distinct set of subjects and re-resolved
before each take's submit; the take's ledger row records its own `avatar_id` and `subject_ids_json`, and the subjects join
that take's request hash only (a shot without subjects keeps its hash).

Recorded: each shot that named a subject has an entry in `meta.plannedSubjects.shots[]` (`shotIndex`, `dropped`,
`frameDropped`, and once shown, `subjects`, `subjectPath` and `subjectFacts` exactly as an inline step records them);
`meta.subjects[]` lists every subject a clip shows, once, which is what the reuse guard checks before a recorded result is
used again (only while the step still shows planned subjects, allows each one and each is unchanged). Each manifest clip
that shows a subject carries `subjects: [{name, kind}]` and `subjectsFingerprint` (an inline step's clips too). The run's
approval card in the assistant says "May show the project's people and objects" without naming any; the hold was already
sized for them (below).

### The no-number invariant

No property anywhere in `ShotPlan` or `GeneratedShotPlanOutput` is a numeric or time-bearing type.
`GeneratedShotPlanOutputInvariantTests` is a reflection test that fails if one is added, fails if
any `ShotPlan` property stops being a `string`, and pins both exact property sets. The planner is
therefore structurally incapable of emitting a timestamp, a frame index, a duration in seconds, a
resolution, a pixel coordinate or a URL.

The reason matters more than the test. The `prompt` is the only value the model authors that leaves
the building; every other value it emits is either an opaque id that the analysis artifact offered
it, or an enum word. First-party C# turns those words into provider parameters:
`ShotPlanParameterMapper` appends an English camera clause to the prompt and snaps the duration
word to a number of seconds, while `ShotPlanCostEstimator` and `VideoGenerationPricing` do the
money. So nothing the model writes is interpreted as a number, a time or a provider parameter.

`ShotPlanParameterMapper.CameraWords` is the canonical 11-word camera vocabulary. `SnapDurationSeconds`
maps `Short` to the model's minimum duration, `Long` to its maximum and `Medium` to the midpoint,
clamped to the model's range in every case. An unknown duration word **throws** rather than
defaulting, because the snapped duration drives the spend estimate and the ledger row; an unknown
camera word is simply omitted from the prompt, which is a documented degradation because nothing
downstream depends on it.

### The prompt sanitizer

`GeneratedPromptSanitizer` (`ReelBolt.Shared/Inference/GeneratedPromptSanitizer.cs`) is the one
shared prompt-hygiene pass for model-authored prose; phase 1's private
`VideoGenerateStepExecutor.SanitizePrompt` and its duplicate length constant are gone.
`DefaultMaxPromptLength` is `7000`, and `MaxPromptLengthFor(InferenceProviderKind)` is total over
every provider kind (MiniMax `7000`, default `7000`) so a future kind cannot silently receive a zero
cap and disable itself. `StripControlCharacters` drops every control character except `\n`, `\r`
and `\t`.

`SanitizedPrompt` carries `Text`, `MaxLength`, `IsEmpty` and `ExceedsMaxLength`, and it **does not
truncate**. An empty or over-length prompt fails closed with `INVALID_PROMPT` instead, so a provider
is never sent a prompt the model did not write. On the `Inline` path that fails the step; on the
`StepRef` path it degrades that one clip and the step continues.

### The planner's tool scope, and the money boundary

`ToolGroupCatalog` grants `AgentType.ShotDirector` exactly `ToolGroup.ProjectRead` plus
`ToolGroup.WorkflowControl` — the same minimal read-only scope as `VideoStoryEditor`,
`MusicSupervisor` and `SoundDesigner`. There is no sandbox tool, no render tool and no project-write
tool, and `ToolScopingDriftGuardTests.ShotDirector_is_granted_no_sandbox_render_or_spending_tool`
asserts the grant rather than leaving it to inspection.

The conclusion this is built for: **the planner cannot spend money.** The planning step is an
ordinary agent call, cheap and bounded, and it has nothing that can reach a provider. Only the
deterministic `VideoGenerate` step buys anything, and only within its own config — its `MaxSpendCredits`
and the budget gate below.

## MiniMax facts this feature relies on

Checked against MiniMax's own documentation on 2026-09-28. Each fact links the page that states
it.

| Fact | Value | Source |
|---|---|---|
| Base URL | `https://api.minimax.io` | [create][mm-create] |
| Auth | `Authorization: Bearer <api key>` | [create][mm-create] |
| Create task | `POST /v2/video_generation`, body `model`, `content`, `resolution`, `duration`, `ratio`; returns `task_id` | [create][mm-create] |
| Text-to-video input | `content` holds a single `{"type": "text", "text": ...}` item | [create][mm-create] |
| Image input | `content` also carries one image item per frame: `{"type": "image_url", "image_url": {"url": <uri>}, "role": "first_frame"}` — the `image_url` object is **nested**, and `role` is one of `first_frame` or `last_frame`. `url` may be a public URL, `mm_file://{file_id}`, or a `data:image/<format>;base64,…` data URI whose `<format>` is lower case | [create][mm-create] |
| Image limits | JPG, JPEG, PNG, WEBP, HEIC, HEIF; ≤ 30 MB per file; 256..5760 px per side; aspect ratio (w/h) 0.4..2.5; `first_frame` ≤ 1 and `last_frame` ≤ 1; whole request body ≤ 64 MB | [create][mm-create] |
| Ratio with an image | on the image-to-video path "the aspect ratio is determined by the input image and `ratio` is always `adaptive`; passing another valid value does not error but is ignored", so ReelBolt sends **no** `ratio` when the request carries an image | [create][mm-create] |
| Prompt length | at most 7000 characters per `text` item | [create][mm-create] |
| `MiniMax-H3` | `768P` or `2K`; duration integer 4–15 s | [create][mm-create] |
| `MiniMax-H3-Max` | `480P` or `768P` (no `2K`); duration integer 5–15 s | [create][mm-create] |
| `MiniMax-H3-Highspeed`, `MiniMax-H3-Lite` | `480P` or `768P` (pricing page, 2026-11-05); duration and ratios NOT published there, set to 5–15 s and the H3 ratio list (unverified) | [pricing][mm-pricing] |
| Reference mode (avatars) | `reference_image` ≤ 9, `reference_video` ≤ 3, `reference_audio` ≤ 3, ≤ 12 files; video MP4/MOV 2..15 s each and ≤ 15 s in total; audio WAV/MP3 2..15 s each and ≤ 15 s in total; never with `first_frame`/`last_frame` (docs/design/avatars.md M2..M6) | [create][mm-create] |
| Ratio for text-to-video | required, one of `21:9`, `16:9`, `4:3`, `1:1`, `3:4`, `9:16`; `adaptive` is not allowed | [create][mm-create] |
| Prices (pay-as-you-go) | `MiniMax-H3`: 768P $0.08/s, 2K $0.13/s. `MiniMax-H3-Max`: 480P $0.05/s, 768P $0.08/s. `MiniMax-H3-Highspeed`: 480P $0.04/s, 768P $0.06/s. `MiniMax-H3-Lite`: 480P $0.025/s, 768P $0.04/s. Input material: input video per second at the output rate (H3-Max $0.0553 / $0.143), reference pictures beyond the first 5 at $0.04 (Highspeed $0.03, Lite $0.02; H3-Max beyond 2 at $0.074), audio free | [pricing][mm-pricing] |
| Query task | `GET /v2/query/video_generation/{task_id}` (`task_id` is a path parameter) | [query][mm-query] |
| Task statuses | `queued`, `running`, `succeeded`, `failed`, `cancelled` | [query][mm-query] |
| Download | on `succeeded`, `task.content.url` is a time-limited download URL; query again for a fresh one | [query][mm-query] |
| Query window | only tasks from the last 7 days are queryable | [query][mm-query] |
| Metered usage | `task.usage.total_seconds`, "input seconds + output seconds", returned only on success | [query][mm-query] |
| Poll cadence | 10 seconds recommended | [guide][mm-guide] |
| Errors | `400` bad_request, `401` authorized, `402` insufficient_balance, `422` unprocessable_entity, `429` rate_limit, `500` server | [create][mm-create] |
| Idempotency | the create endpoint documents no idempotency key | [create][mm-create] |

The per-model resolutions, durations, ratios and prices above are hard-coded in
`MiniMaxVideoGenerationClient.Describe` (`ReelBolt.Shared/Inference/MiniMaxVideoGenerationClient.cs`).
If MiniMax changes them, that method is the one place to update.

### Image input on MiniMax

Both models the repository offers — `MiniMax-H3` and `MiniMax-H3-Max` — accept input images, so
`MiniMaxVideoGenerationClient` declares `FirstAndLastFrame`. The request's image list is positional:
`Images[0]` is the first frame, `Images[1]` the last, and each becomes one content item in that order
behind the single required `text` item.

Every image is sent as an inline `data:image/<format>;base64,…` data URI. ReelBolt has no public URL
to hand the vendor, `mm_file://` addresses the vendor's own file space, and the third documented form
is the data URI — so the data URI is the one that fits, with its cost stated rather than hidden:
base64 inflates a payload by about a third, against a 64 MB whole-body ceiling.

**The vendor's own limits are checked locally, before the request leaves the process**, and each
refusal names the limit it broke: media type in the documented list, ≤ 30 MB per file, 256..5760 px per
side and a 0.4..2.5 aspect ratio read from the image's own header (`ImageHeader`, PNG/JPEG/WebP), at
most one first and one last frame, and a serialized body of ≤ 64 MB. Two consequences are worth
stating:

- **HEIC and HEIF are on the vendor's format list and are refused here.** No header reader in this
  repository covers them, so the two limits that depend on the pixel size cannot be checked; sending
  such an image would silently skip a documented guard, which is the one thing this client does not do.
  A caller with HEIC input re-encodes it as PNG or JPEG.
- **The `MB` figures are read as decimal bytes** (30,000,000 and 64,000,000), the smaller of the two
  readings of an ambiguous unit, so nothing is sent above a byte count the vendor published.

### Showing an avatar (reference mode)

Avatars phase 2 (internals in [`avatars.md`](avatars.md) "Generating a clip with an avatar"). A step with
`AvatarId` sends the avatar's pictures, videos and speech samples as `reference_image` / `reference_video` /
`reference_audio` items (`VideoGenerationRequest.References`, declared per model by `IVideoReferenceInputSupport`),
each as a data URI, with the step's `ratio` (reference mode is not frame-based). The client re-checks the
declaration, each item's documented type, size and length, and the 64 MB body before anything is sent. The vendor's
input-material charge is part of every take's price; ledger rows carry `avatar_id`; each take re-checks the
avatar's consent before its submit; avatar clips are marked as AI-generated in their metadata (and with a visible
label by default). Must-measure items stay open: whether reference audio drives the words, how reference pictures
and video are billed, and whether video and audio data URIs are accepted.

### Talking heads: animating a still of an avatar (phase 3)

Avatars phase 3, the cheaper path (internals in [`avatars.md`](avatars.md#the-still-then-animate-path-phase-3), vendor
schemas in [`fal-ai-models.md`](fal-ai-models.md) section 14). On a fal.ai talking-head model
(`minimax/h3-max/lip-sync/image-to-video` by default, `fal-ai/kling-video/ai-avatar/v2/standard` as the alternative,
job `TalkingHead`) a step with `AvatarId` and `AvatarAudio` does not send the avatar's references to the video model:
the engine first makes ONE still of the avatar from up to three of its pictures with a fal.ai instruction edit
(default `google/nano-banana-2.1/edit`), stored under the avatar's stills folder and reused while the avatar is
unchanged, then submits that still as the first frame with the step's narration (a `Voiceover` step's lines joined
into one track) as the driving audio. The clip lasts as long as the speech (5..15 s); the take is reserved and
billed at the speech's whole seconds, and the hold adds one still. Every avatar request carries `X-Fal-Store-IO: 0`.

The talking-head model lives under its own capability, `LipSync`, not `VideoGeneration`. A step with `AvatarAudio` resolves
its provider as the step's own LipSync row, else the default LipSync row, else none; it never falls back to
`VideoGeneration`. With none set up, the run is refused at start with `LIP_SYNC_MODEL_NOT_SET` (lint
`WL006.lipSyncModelNotSet`). An admin adds the fal.ai lip-sync model under Lip-sync and marks it default; a talking-head
model added under Video generation is refused. Usage and price rows stay in seconds (`VideoGenSeconds`).

A step not bound to a LipSync row runs the default LipSync row's model, and where that model does not offer the step's
resolution, ratio or duration it uses the model's own, so the template's model and resolution are only hints. A step bound
to a LipSync row keeps its own settings and is refused at run start (`VIDEO_MODEL_UNSUPPORTED`) when its model does not offer its resolution or ratio (duration is not checked).
A talking-head step's provider must be a LipSync row when the step is saved; an older saved step naming a Video
generation row runs on the default LipSync row. Project readiness asks for a lip-sync model ("Add a lip-sync model") for
talking-avatar recipes.

### Several subjects per clip: people and objects, on every model

Design record: [`design/avatar-objects.md`](design/avatar-objects.md) "Subjects in a video generation step". An inline
step may show up to three project avatars, people and objects, and at most one of them a person. It names them in
`Subjects` (`[{avatarId, roles?}]`); the older `AvatarId` stays and means "one subject". The run brief's avatar fills
only a step that names none.

Every subject is held to the one rule (resolved before anything is reserved, re-resolved before each take's submit), the
generation switch is asked per kind (`Avatars:Enabled` + `Avatars:Generation` for a person, `Avatars:Objects:Enabled`
for an object), and the step's permission to send media is checked once. A second person fails `CONFIG_INVALID`.

How the subjects reach the model is decided from the model's own declarations (`VideoSubjectPaths`, one table for the
engine, the save check, the linter and the estimate), before any byte is read:

| Path | When | What is sent |
|---|---|---|
| `TalkingHead` | the model animates a still and the step names the speech (`AvatarAudio`) | the section above, unchanged; one subject, a person |
| `References` | the model has a reference mode and `SubjectMode` is not `FirstFrame` | each subject's pictures (on MiniMax also videos and a person's speech samples), shared in turns: every subject gets one picture before any gets a second |
| `FirstFrame` | the model takes an image input | ONE composed still that shows every subject in the scene of the prompt, as the first frame; no reference item |
| refused | a text-only route | `AVATAR_NOT_SUPPORTED_BY_MODEL`: "'<model>' takes no pictures. Use '<sibling>' to show an avatar or an object." |

| Provider and model family | Path |
|---|---|
| MiniMax direct (`MiniMax-H3*`) | `References`; `FirstFrame` with `SubjectMode = FirstFrame` |
| fal.ai `alibaba/wan-3.0/reference-to-video`, `fal-ai/veo3.1/reference-to-video`, `fal-ai/veo3.1/fast/reference-to-video` | `References` (pictures only; [`fal-ai-models.md`](fal-ai-models.md) section 15) |
| fal.ai image-to-video routes | `FirstFrame` |
| fal.ai talking-head routes | `TalkingHead` (needs `AvatarAudio`) |
| Higgsfield Kling `…/image-to-video` | `FirstFrame` (the vendor's `elements` take vendor-registered ids, not pictures: not built) |
| ComfyUI `wan2.2-ti2v-5b-i2v` | `FirstFrame` |
| fal.ai text-to-video routes, Higgsfield `…/text-to-video`, ComfyUI `wan2.2-ti2v-5b-t2v` | refused, with the sibling (`VideoSubjectSiblings`): the image-to-video route of the same model (`fal-ai/veo3.1` names its reference-to-video route) |

The composed still (recipe `subject-scene-still-v1`, `ISubjectStillMaker`) is one fal.ai instruction edit over up to
three pictures of each subject and six in all, downscaled to 1536 px and sent as data URIs with `X-Fal-Store-IO: 0`. Its
instruction is fixed first-party text that names each subject by kind and by the positions of its pictures, adds a
subject's own description as quoted text, then the step's prompt. It is made after the prompt was screened, kept under
the person's stills folder when a person is shown (else the first object's), reused while every subject, the model, the
ratio and the prompt are unchanged, and removed again if a subject changes while it is being made. A ratio word that is
not an aspect ratio (`native`, `adaptive`) is not sent to the picture model. Codes: `AVATAR_STILL_PROVIDER_MISSING`,
`AVATAR_STILL_MODEL_UNSUPPORTED`, `AVATAR_STILL_FAILED`.

Money: on `References` the hold takes the input material of every subject, worst case, on every take (one subject =
the figure a single avatar always held); on `FirstFrame` it adds ONE still per inline step
(`VideoGenerateCreditPricing.AvatarStillHoldUsd`), and settlement charges the metered edit (a reused still meters
nothing). A plan-sourced step holds the same per planned shot while planned subjects can apply (`PlannedSubjects = Auto`,
the step's permission on, and the project holds an avatar, a fact the API and the engine read the same way through
`ProjectSubjectsPricing`; a project that cannot be read is held as if it may).

What is recorded: a step that names `Subjects` writes `meta.subjects[]` (`avatarId`, `name`, `kind`, `fingerprint`,
`referencesSent`, `referencesSkipped`), `meta.subjectPath` (`references`, `firstFrame`, `talkingHead`) and
`meta.subjectFacts` (the fingerprint of the set, marking, `c2pa`, the still); each clip carries `subjectsFingerprint`.
The ledger row's `avatar_id` holds the person when there is one, else the first object, and `subject_ids_json` every
avatar the take showed (engine migration `AddExternalGenerationJobSubjects`). The one legacy `AvatarId` keeps
`meta.avatar`, `avatarFingerprint` and its exact request hash and cache key (pinned by
`VideoGenerateLegacyAvatarPinTests`); on a model with no reference mode it now takes the `FirstFrame` path and records
`meta.avatar.path = "firstFrame"`. A clip that shows a person is marked as that avatar asks; a clip that shows only
objects gets the neutral marking (`ai_generated=true`, no label). The reuse guard and the step-cache key follow every
subject: a clip is reused only while the same subjects, in the same order, are each usable and unchanged.

Plan-sourced steps: `PlannedSubjects` (`Auto` | `Off`) and `PlannedSubjectIds` decide whether the shot planner may name
subjects per shot; see [Avatars and objects in planned shots](#avatars-and-objects-in-planned-shots).

[mm-create]: https://platform.minimax.io/docs/api-reference/video-generation-v2-create.md
[mm-query]: https://platform.minimax.io/docs/api-reference/video-generation-v2-query.md
[mm-guide]: https://platform.minimax.io/docs/guides/video-generation.md
[mm-pricing]: https://platform.minimax.io/docs/guides/pricing-paygo.md

## Set up a MiniMax provider

Admin → Inference Providers → New, or `POST /api/v1/inference-providers`:

| Field | Value |
|---|---|
| Kind | `MiniMax` |
| Capability | `VideoGeneration` |
| Endpoint | `https://api.minimax.io` |
| API key | your MiniMax API key |
| Timeout | per-HTTP-request timeout in seconds, applied to submit, poll and download calls |
| IsDefault | set it to make this the capability a step uses when its `ProviderId` is unset |

The step's `Model` field picks the model; the executor does not read the provider row's model name.

The admin form prefills the endpoint as `https://api.minimax.io`, the base URL MiniMax documents
for the v2 API, when you pick `MiniMax`.

The API validates the kind/capability pair on create, update and test and returns `400` for:

- `Capability: VideoGeneration` with any kind other than `MiniMax`, `Higgsfield` or `ComfyUI`.
- `Kind: Higgsfield` or `Kind: ComfyUI` with any capability other than `VideoGeneration` (MiniMax
  additionally serves the two image capabilities, which are its own and not this feature's).

That allowlist is the explicit set `{MiniMax, Higgsfield, ComfyUI}`. It was deliberately not loosened into a
catch-all such as "any kind that is not a chat kind", which would silently admit every future chat
kind the day it is added.

There is no `env:` sentinel for MiniMax. The stored key is decrypted and sent verbatim as the
bearer token.

### Test a provider without spending

`POST /api/v1/inference-providers/{id}/models/{modelId}/capabilities/{capId}/test` (or `POST /api/v1/inference-providers/test` for an unsaved config) never creates a
task, so it costs nothing. `MiniMaxVideoGenerationClient.ProbeAsync` sends one
`GET /v2/query/video_generation/{random GUID}` with the configured key, bounded by a 15-second
timeout (the provider row's `Timeout`, if set lower, cuts it shorter), and classifies the answer:

| Answer | Result |
|---|---|
| `401` or `403`, or a MiniMax error body whose `error.type` is `authorized_error` (any status) | `ok: false`, `error` "Provider rejected authentication." |
| `500` with `error.type` `server_error` and a message starting "record not found" — what MiniMax actually answers for a task id that does not exist | `ok: true` |
| A network error, the timeout, `408`, `429` or any other `5xx` | `ok: false`, `error` "Provider test failed: …" naming what happened |
| A non-JSON body, or JSON that is neither MiniMax envelope below | `ok: false`, same `error` form |
| Any other status whose body is `{"task": {...}}` | `ok: true` |
| Any other status whose body is MiniMax's error envelope `{"type": "error", "error": {"type": ..., "message": ...}}` with an `error.type` other than `authorized_error` (for example `bad_request_error`, "invalid task_id") | `ok: true` |

The result's `responsePreview` (on `ok: true` and on an auth rejection) carries the status and at
most a short, key-scrubbed vendor `error.type`/`error.message`, never the raw body. On the saved
provider path the outcome is persisted to `LastTestAt`/`LastTestOk`/`LastTestError`.

A passing test means the endpoint answered with MiniMax's query envelope and did not reject the
key. MiniMax does not document what the query endpoint returns for a task id that does not exist,
but observed behaviour (2026-10-04) is that it checks the key first — an invalid key gets `401`
`authorized_error` — and then answers a valid key's lookup of an unknown id with HTTP `500`,
`server_error`, "record not found (1000)". That `500` is why the probe special-cases it: treating
every `5xx` as a failure made the test fail for every working key. Any other `5xx`, including a
`server_error` with a different message, still fails.

## Higgsfield facts this feature relies on

Checked against Higgsfield's own documentation on 2026-10-02 — the vendor's docs site and its
`higgsfield-js` SDK README — with the errors and billing pages re-read on 2026-10-03. Each fact
links the page that states it.

| Fact | Value | Source |
|---|---|---|
| Base URL | `https://api.higgsfield.ai` | [billing][hf-billing] |
| Auth | `Authorization: Key <KEY_ID>:<KEY_SECRET>` — a **two-part** credential, not a Bearer token | [billing][hf-billing] |
| Submit | endpoint-addressed: `POST https://api.higgsfield.ai/<endpoint-id>`; the body is **flat**, the model parameters at the top level with no `input` wrapper | [model][hf-model], [sdk][hf-sdk] |
| Model identity | the vendor's per-workflow **Endpoint ID**, a relative path such as `kling-video/v3.0/std/text-to-video` — not a model name | [model][hf-model] |
| Text-to-video input | `prompt`, `duration` (integer), `aspect_ratio`, all **top-level** — the page's schema carries no `input` object | [model][hf-model] |
| Image input (per endpoint, **verified**) | a **flat** `image_url` (required, format uri) plus an optional flat `last_image_url`, with the URL being the presigned upload's `public_url`. Verified for `kling-video/v3.0/std/image-to-video`, `…/pro/image-to-video` and `…/4k/image-to-video` (both members) and for `kling-video/v3.0-turbo/image-to-video` (`image_url` only). Those pages document **no** `aspect_ratio` | [image input][hf-image-model], [models][hf-models] |
| Poll | `GET /requests/{request_id}/status` | [status][hf-status] |
| Status values | `queued`, `in_progress`, `completed`, `failed`, `nsfw`, `canceled` | [status][hf-status], [requests][hf-requests] |
| Estimate | `POST /estimate/{endpoint_id}` with a **flat** body (`duration`, `aspect_ratio`), returning `{credits, usd}` where the vendor's own sample writes **both members as strings** (`{"credits": "1.500", "credits": "0.094"}`) | [billing][hf-billing] |
| Presigned upload | `POST /files/generate-upload-url` with `{"content_type": <type>}` → `{public_url, upload_url, content_type, upload_headers}`, then a `PUT` to `upload_url` carrying those headers verbatim and **no** Higgsfield credentials | [files][hf-files] |
| Synchronous errors | `400` invalid params or concurrency reached, `401` missing or invalid credentials, `403` insufficient credits, `404` request or model not found for this account, `422` validation failed, `423` model temporarily blocked, `500` server, `503` model disabled or not ready | [errors][hf-errors] |
| Failed and moderated requests | `failed` and `nsfw` are not charged, and reserved credits are refunded automatically | [billing][hf-billing], [errors][hf-errors] |
| Cancelable window | a request can be canceled only before processing starts | [billing][hf-billing] |
| Metered usage | none reported — there is no equivalent of MiniMax's `usage.total_seconds` | client `PollAsync` |
| Idempotency | the vendor's model-page cURL sends an `Idempotency-Key` header on generation submissions (re-fetched 2026-10-04); ReelBolt sends it on every submit, carrying the take's own ledger row id, so a retry after a crash between the vendor accepting the task and ReelBolt recording its `request_id` re-presents the SAME key. A request with no token sends no header | [model][hf-model], [errors][hf-errors] |
| Result retention | the vendor documents results as retrievable for at least 7 days; ReelBolt downloads a delivered clip immediately and relies on nothing past that | [requests][hf-requests] |
| Presigned upload expiry | the `upload_url` from `POST /files/generate-upload-url` expires one hour after issue, and the `PUT` carries no Higgsfield credential; accepted content types are `image/jpeg`, `image/png`, `image/webp`, `image/gif`, `audio/wav`, `video/mp4` | [files][hf-files] |
| Webhooks | a `?hf_webhook=<url>` **query parameter** on the endpoint, not a header | [sdk][hf-sdk] |
| Per-model price table | none published; the estimate endpoint is the only authoritative price | [billing][hf-billing] |

**Neither wire shape is verified against a live call.** No Higgsfield credential exists in this
deployment and none may be requested, so the submit body, the estimate body and the estimate's
response parsing all follow the vendor's own documented cURL and nothing else. They are **not
confirmed empirically**; a mismatch surfaces as a 4xx the caller sees, never as a wrong charge —
see [Every failure is fail-closed](#pricing-purity-and-the-price-oracle).

#### The `input` wrapper was never the wire format

`{"input": {…}}` is not Higgsfield's request body. It comes from the vendor's own TypeScript
snippet — `higgsfield.subscribe(endpoint, { input: {…}, withPolling: true })` — where `input` and
`withPolling` are **siblings in the client-side SDK's own options object** and the SDK is what
writes the body. Reading `input` as a wire envelope describes a body the vendor does not document,
so both endpoints ReelBolt calls are sent flat: the submit as `{"prompt": …, "duration": …,
"aspect_ratio": …}`, plus the endpoint's own flat image members when it has any, and the estimate as
`{"duration": …, "aspect_ratio": …}`. Each shape rests on its **own** primary source — the model
page's schema for the submit, the billing page's cURL for the estimate — and neither page is
evidence for the other.

#### Image input is the documented flat shape, mapped per endpoint

The image-to-video pages' own cURL prints the real body — a **flat `image_url`** string (required),
plus an optional flat `last_image_url` — and their schemas carry no `{"type", "image_url"}` array at
all; that array was read off the vendor's JavaScript SDK options surface, the same client-side
surface whose `input` wrapper was corrected above ([image input][hf-image-model]). ReelBolt now sends
what those pages document, and it does so **per endpoint**, because the vendor's own instruction is
to use the exact schema on the selected model's page: "Endpoint fields vary by model and workflow"
([models][hf-models]). The mapping is explicit and closed:

| Endpoint | `image_url` | `last_image_url` |
|---|---|---|
| `kling-video/v3.0/std/image-to-video` | yes | yes |
| `kling-video/v3.0/pro/image-to-video` | yes | yes |
| `kling-video/v3.0/4k/image-to-video` | yes | yes |
| `kling-video/v3.0-turbo/image-to-video` | yes | **no** — that page documents no last frame |
| every other endpoint | **refused** | **refused** |

Three rules follow, and each is tested against a recording handler, never a live call:

- An image offered to an endpoint whose image schema nobody has verified is **refused before the
  upload**, naming the gap. A blind rename of one field to another would be exactly the mistake the
  `input` wrapper correction was about.
- An image offered to one of the four `…/text-to-video` endpoints is refused as *that endpoint's* own
  fact: those pages document `prompt`, `duration` and `aspect_ratio` and no image member.
- `aspect_ratio` is **not** sent with an image. The image-to-video pages document no such member —
  the frame's geometry decides the output — and a text-to-video member is not carried over by
  assumption. `prompt` and `duration` are documented by both and keep their names.

The no-image body is unchanged on **every** endpoint, byte for byte: `{"prompt", "duration",
"aspect_ratio"}`. That is the regression the client's own test pins.

`Describe(model)` throws `NotSupportedException` for every Higgsfield model, and deliberately so:
the vendor publishes no per-second or per-model price table and no offline capability data either,
so a synchronous, no-network lookup has nothing to return that would not be an invented vendor fact.
The priced answer comes from the vendor's own estimate instead — see
[Pricing purity and the price oracle](#pricing-purity-and-the-price-oracle).

**A submission is addressed by Endpoint ID, and each endpoint documents its own schema.** The
vendor's instruction is to use the exact schema on the selected model's page, so ReelBolt sends the
documented common shape — `prompt`, `duration`, `aspect_ratio` — plus, on the mapped image-to-video
endpoints, the flat `image_url`/`last_image_url` pair instead of the ratio (see
[Image input is the documented flat shape](#image-input-is-the-documented-flat-shape-mapped-per-endpoint)).
On the reference endpoint,
`kling-video/v3.0/std/text-to-video`, that is `duration` 3–15 and `aspect_ratio` one of `16:9`,
`9:16` or `1:1`. Note the one place where a vendor limit is tighter than ReelBolt's: that page caps
its prompt at 2,500 characters and **truncates** beyond it, while ReelBolt's own prompt cap is 7,000
for every kind — so on that endpoint an over-long prompt is shortened by the vendor rather than
refused here.

**Status mapping.** `PollAsync` switches over the six status literals **exhaustively** and throws
`NotSupportedException` on anything else rather than defaulting: `queued` → `Queued`, `in_progress`
→ `Running`, `completed` → `Succeeded`, `failed` → `Failed`, `nsfw` → `Failed`, `canceled` →
`Cancelled`. `nsfw` is a terminal, **non-billable** moderation rejection whose credits the vendor
refunds. `ExternalJobState` has no `Nsfw` member, so the client reports the distinction as the job's
failure reason instead:

```text
nsfw: content rejected by moderation; the provider refunds this request.
```

That text reaches the clip as its `detail`: the executor maps a vendor failure to the code
`PROVIDER_FAILED` and puts the vendor's own words beside it, so an `nsfw` clip reads
`failureReason: "PROVIDER_FAILED"` with the moderation explanation in `detail`. The refund is the
vendor's; on ReelBolt's side the row ends `Failed` and is released, so nothing is charged against
the step, project or global caps.

A `completed` response carrying no `video.url` throws rather than returning an empty download URL.
Higgsfield reports **no metered seconds**, so `UsageTotalSeconds` is always `null` for it, and a
Higgsfield clip's `actualCost` is never derived from `usage.total_seconds` the way MiniMax's is
(see [Actual cost](#actual-cost)).

**Webhooks are not implemented because polling is the posture.** Issue #93's draft described an
`X-Webhook-URL` header and that is wrong: the vendor's own SDK appends a `?hf_webhook=<url>` query
parameter. The correct shape is recorded here so that the reason this feature polls stays a scope
choice rather than a wire-shape limitation.

[hf-billing]: https://docs.higgsfield.ai/docs/concepts/billing-and-retention.md
[hf-status]: https://docs.higgsfield.ai/docs/api-reference/requests/get-request-status.md
[hf-requests]: https://docs.higgsfield.ai/docs/concepts/requests.md
[hf-errors]: https://docs.higgsfield.ai/docs/concepts/errors.md
[hf-files]: https://docs.higgsfield.ai/docs/concepts/file-uploads.md
[hf-model]: https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md
[hf-models]: https://docs.higgsfield.ai/docs/models
[hf-image-model]: https://docs.higgsfield.ai/docs/models/kling-3/standard-image-to-video.md
[hf-sdk]: https://raw.githubusercontent.com/higgsfield-ai/higgsfield-js/main/README.md

## Set up a Higgsfield provider

Admin → Inference Providers → New, or `POST /api/v1/inference-providers`:

| Field | Value |
|---|---|
| Kind | `Higgsfield` |
| Capability | `VideoGeneration` |
| Endpoint | `https://api.higgsfield.ai` |
| API key | the two-part credential, entered as `KEY_ID:KEY_SECRET` |
| Timeout | per-HTTP-request timeout in seconds, applied to submit, poll, download and the presigned upload — but not to the price lookup, which has its own 10-second bound |
| IsDefault | set it to make this the capability a step uses when its `ProviderId` is unset |

The admin form prefills the endpoint as `https://api.higgsfield.ai`, the base URL Higgsfield
documents, when you pick `Higgsfield` — and clears it again if you switch away, unless you typed
something else yourself.

The step's `Model` field is the vendor's **Endpoint ID** here, not a model name: a relative path
such as `kling-video/v3.0/std/text-to-video`, with or without a leading slash. The executor does not
read the provider row's model name. A value that would change which host the request is sent to —
one containing `//`, `://` or a `..` segment, which `HttpClient` would read as an authority override
— is rejected before anything leaves the process, with the credential and the prompt still in the
request. `Model` is step-config-authored, so that check is a real boundary rather than a formality.

**The credential is two-part.** The admin form's API key helper text for this kind reads:

> Higgsfield credentials are two-part: enter them as `ID:SECRET`, joining the Key ID and Key Secret
> from your Higgsfield account with a colon. The whole `ID:SECRET` string is stored as this row's
> key — it is not a single opaque token.

The pair is split on the **first** colon only, so a secret may itself contain a colon. There is no
vendor key in this repository and none is needed to use this document: the credential is a runtime,
admin-entered value, and nothing here asks you to obtain one for testing.

`Higgsfield` is the one video-generation kind with an `env:` sentinel. Saving the literal string
`env:` makes the client read the credential from the environment instead of the stored value:
`HF_CREDENTIALS` (`KEY_ID:KEY_SECRET`, preferred), otherwise both `HF_API_KEY` and `HF_API_SECRET`.
That is an explicit admin opt-in, not an ambient fallback. An **empty or whitespace-only** stored
key, a stored value with no colon, a value with an empty half, and an `env:` sentinel with no usable
environment value all **throw** — none of them quietly reads the host environment.

The API validates the kind/capability pair exactly as it does for MiniMax: a `Higgsfield` row is
rejected with `400` for every capability other than `VideoGeneration`, and `VideoGeneration` is
rejected for any kind outside the explicit `{MiniMax, Higgsfield, ComfyUI}` set. No other change was
made to that allowlist to make room for this kind.

There is no separate data-terms contract to accept on the provider row. What the admin form does
instead is show a caution when, and only when, the kind is `Higgsfield` — titled "Check Higgsfield's
data terms before sending footage". It reports that Higgsfield's terms have been **reported** to
grant the vendor a broad, perpetual licence over the inputs and outputs sent to it, including for
training and marketing, and then states — in the same breath — that that specific clause could not
be confirmed against Higgsfield's own current terms when this provider kind was added. It is a
caution and a pointer at the vendor's own terms of service, deliberately not an assertion of a
settled legal fact, because a warning that overstates the terms is its own defect. Read them before
sending footage that is not yours or that shows other people.

Separately and mechanically, a `VideoGenerate` step requires `AllowSourceMediaEgress` before it may
submit a plan that names a keyframe at all; without it the step is refused. No frame currently
leaves the host either way, since the transport is not delivered — see
[Egress: sending the project's own frames to a provider](#egress-sending-the-projects-own-frames-to-a-provider).

### Test a Higgsfield provider without spending

`POST /api/v1/inference-providers/{id}/models/{modelId}/capabilities/{capId}/test` (or `POST /api/v1/inference-providers/test` for an unsaved config) costs nothing:
`HiggsfieldVideoGenerationClient.ProbeAsync` sends exactly one `GET /requests/{random GUID}/status`
with the configured credential, bounded by a 15-second timeout (the provider row's `Timeout`, if
set lower, cuts it shorter). It never submits and never calls `/estimate`, which the vendor does not
document as free.

The decision table deliberately differs from MiniMax's, because Higgsfield's own documented meanings
do:

| Answer | Result |
|---|---|
| `401` | `ok: false`, `authRejected: true`, "Provider rejected authentication (HTTP 401)." |
| `403` | `ok: false` with `authRejected: false`, "Provider returned HTTP 403 (insufficient credits)." — Higgsfield documents `403` as insufficient credits, not as bad credentials, so reporting it as an auth failure would send an admin to re-issue a working key |
| `404` carrying the documented `{"detail": "<string>"}` body | **`ok: true`** — `404` means "the request does not exist or belongs to another account", so it proves both that this is the status endpoint and that the credential was accepted; an unauthenticated call is `401`, not `404` |
| `200` whose body parses as a request-status object (a string `status`) | `ok: true` |
| `400`, `422` or `423` | `ok: false`, the reason naming the status and, when present, the vendor's bounded `detail` |
| `408`, `429`, any `5xx`, a network error or our own timeout | `ok: false`, the reason naming what was seen |
| A body that does not parse as JSON | `ok: false`, the reason naming the status **and** the content type: "Provider returned a non-JSON body (HTTP `<status>`, content-type `<type>`)." |
| A body that parses but is neither envelope | `ok: false`, the reason naming the status only: "Provider returned an unrecognised JSON body (HTTP `<status>`)." |

`401` is the only answer that sets `authRejected`. Reasons carry at most a short, **both-halves
credential-scrubbed** vendor `detail`, never the raw body, and never the credential itself. On the
saved provider path the outcome is persisted to `LastTestAt`/`LastTestOk`/`LastTestError`.

A passing test proves the endpoint and the credential, not that a given Endpoint ID will accept a
submit: the probe asks about a random request id and never prices or submits anything.

## Step config reference

`WorkflowStep.VideoGenerateConfigJson` (column `video_generate_config_json`, `jsonb`) holds a
JSON-serialized `VideoGenerateStepConfig` (`ReelBolt.Shared/Workflows/VideoGenerateStepConfig.cs`).
Property names are camelCase in JSON; enums are strings.

| Property | Type | Default | Notes |
|---|---|---|---|
| `PlanSource` | `VideoGeneratePlanSource` | `Inline` | `Inline` generates `Takes` clips from `InlinePrompt`; `StepRef` generates one clip per planned shot. `StepRef` is appended after `Inline` so stored payloads keep their meaning. |
| `InlinePrompt` | `string?` | `null` | The prompt. Required on the `Inline` path, see below. Ignored on the `StepRef` path. |
| `Plan` | `ExtractInputRef?` | `null` | The planner step's `GeneratedShotPlanOutput`. Required when `PlanSource` is `StepRef`, see below. |
| `Analysis` | `ExtractInputRef?` | `null` | The `VideoAnalyze` step whose artifact offered the `firstFrame`/`lastFrame` ids. Required when any planned shot names one, see below. |
| `Model` | `string` | `"MiniMax-H3"` | The provider's model identity. For `MiniMax`, `MiniMax-H3` or `MiniMax-H3-Max`; any other value fails `CONFIG_INVALID`. For `Higgsfield`, the vendor's **Endpoint ID** — a relative path such as `kling-video/v3.0/std/text-to-video` — which is both the submission path and the id `POST /estimate/{endpoint_id}` is addressed by. |
| `Resolution` | `string` | `"768P"` | Must be one of the model's resolutions, matched exactly (case-sensitive, no trimming). For an oracle-priced model that is the single resolution the step's own configuration was priced at — see [An oracle-priced model is priced at one duration](#an-oracle-priced-model-is-priced-at-one-duration). |
| `DurationSeconds` | `int` | `6` | Seconds per clip on the `Inline` path. On the `StepRef` path each shot's own duration word is snapped instead, so this value does not drive the clips — but it is still validated against the model's range, and it is still part of the ledger's `request_hash` on both paths. On an oracle-priced model it is also the duration the price was asked about, and therefore the duration **every** shot snaps to. |
| `Ratio` | `string` | `"16:9"` | Must be one of the model's ratios, matched exactly. |
| `ProviderId` | `Guid?` | `null` | A `VideoGeneration` model capability id. If unset, disabled, deleted or of another capability, the default `VideoGeneration` capability is used. |
| `Takes` | `int` | `1` | `Inline`: clips to generate, `clip-1`..`clip-N`. `StepRef`: takes per planned shot, and the clip ids become `v{n}t{k}`. |
| `MaxClips` | `int` | `1` | Author-set hard ceiling on clips for the whole step, validated independently of `Takes`. There is no product cap: you choose it (technical ceiling 64). Bindable as an app input ("Most clips"); `MaxSpendCredits` is bindable too, as an always-shown credits number, see [workflow apps](workflow-apps.md). |
| `MaxSpendCredits` | `decimal` | `0` | Step spend cap, in **credits** (1 credit = $0.006 of provider cost, `PricingDefaults.CreditUsdValue`). No usable default: `0` fails validation. A vendor's own USD quote is converted into credits by `CreditCurrency` at the boundary, so this cap is never compared in dollars — see [The unit](#the-unit-credits-not-dollars). |
| `AllowSourceMediaEgress` | `bool` | `false` | Consent to send frames of the project's own footage to the provider as keyframes. See [Egress](#egress-sending-the-projects-own-frames-to-a-provider). |
| `PollIntervalSeconds` | `int` | `10` | Delay between polls, 1 to 300 inclusive. MiniMax recommends 10. |
| `TimeoutSeconds` | `int` | `1800` | Per-take polling budget, 60 to 7200 inclusive and at least `PollIntervalSeconds`, counted from when the take holds a provider slot. A take still unfinished when it elapses ends `POLL_TIMEOUT`. |
| `LookMatching` | `bool` | `false` | Plan path only. Appends the anchor shot's MEASURED colour words to each shot's prompt — see [Look matching and camera continuity](#look-matching-and-camera-continuity). Requires `Analysis`. |
| `CameraContinuity` | `bool` | `false` | Plan path only. When the planner left `camera` blank, uses the anchor shot's measured move (`Static`/`Handheld` only). Requires `Analysis`. |
| `MaxGpuMinutes` | `decimal` | `0` | GPU-time cap for a self-hosted `ComfyUI` provider, which bills nothing — see [Self-hosted ComfyUI](#self-hosted-comfyui). Exactly one of this or `MaxSpendCredits` is meaningful per provider kind; `ValidateForKind` enforces the one that fits. |
| `ConformFps` | `int` | `0` | Conform pass: frame-interpolate a delivered clip UP to this rate when the provider's is lower (`0` keeps the clip's rate; `1..120`). Never downsamples. |
| `ConformHeight` | `int` | `0` | Conform pass: upscale a delivered clip to this height when smaller (`0` keeps; `16..4320`). Never downscales. |
| `FirstFrame` | `ExtractInputRef?` | none | Inline mode only: the image sent as the clip's first frame (`Images[0]`), from an earlier step whose output is an image (an `ExtractFrame` step): `From=Previous` or `From=Step` with `StepOrder`. See [Starting a generated clip from a frame](#starting-a-generated-clip-from-a-frame). |
| `LastFrame` | `ExtractInputRef?` | none | Inline mode only: the image sent as the last frame (`Images[1]`). Requires a first frame. |
| `FirstFrameProjectFileId` / `LastFrameProjectFileId` | `Guid?` | none | A project image file instead of a step, for the same slot. Mutually exclusive with the step form of that slot. |
| `AvatarId` | `Guid?` | none | Avatars phase 2: the project avatar every take shows, sent as reference material. Inline only, never with a first/last frame, needs `AllowSourceMediaEgress`. See [Showing an avatar](#showing-an-avatar-reference-mode). |
| `AvatarReferenceRoles` | `AvatarReferenceRole[]?` | none | Subset of the avatar's roles to send (`Face`, `HalfBody`, `FullBody`, `Motion`, `Speech`; for an object `Front`, `Angle`, `Detail`, `Motion`); must keep a picture role. |
| `Subjects` | `VideoGenerateSubject[]?` | none | Up to three avatars to show in every take (`{avatarId, roles?}`), people and objects, at most one person. Inline only, never with `AvatarId` or a first/last frame. Written only when set. See [Several subjects per clip](#several-subjects-per-clip-people-and-objects-on-every-model). |
| `SubjectMode` | `VideoSubjectMode` | `Auto` | `Auto` (the model's reference mode when it has one, else one composed first frame), `References` or `FirstFrame`. Written only when not `Auto`. |
| `PlannedSubjects` / `PlannedSubjectIds` | `VideoPlannedSubjects` / `Guid[]?` | `Auto` / none | Plan sources only: whether the shot planner may use the project's avatars, and an optional allowlist. Stored and validated; no behaviour yet. Written only when not the default. |
| `AvatarAudio` / `AvatarAudioProjectFileId` | `ExtractInputRef?` / `Guid?` | none | Driving speech, only for a model that declares audio drives speech: required on the phase 3 talking-head models (a `Voiceover` step's lines, joined), refused on MiniMax (unmeasured). See [Talking heads](#talking-heads-animating-a-still-of-an-avatar-phase-3). |

`Validate()` returns every rule that fails; at run time any error ends the step `CONFIG_INVALID`
before the budget gate reserves anything, so nothing is submitted or charged. A missing
`VideoGenerateConfigJson`, invalid JSON or a JSON `null` also ends the step `CONFIG_INVALID`.

- At least one of `MaxSpendCredits` and `MaxGpuMinutes` must be greater than 0 (the message is still
  `MaxSpendCredits must be positive (greater than 0)` when both are zero, so every existing caller sees
  the same text). `ValidateForKind(kind)` then requires the one that fits the RESOLVED provider:
  `MaxGpuMinutes > 0` for `ComfyUI`, `MaxSpendCredits > 0` for every priced vendor — a mismatch ends the
  step `CONFIG_INVALID` before any reservation.
- `MaxGpuMinutes` must not be negative; `ConformFps` is `0` or `1..120`; `ConformHeight` is `0` or `16..4320`.
- `Analysis` is required when `LookMatching` or `CameraContinuity` is on with `PlanSource = StepRef`.
- `Takes` must be between 1 and 64 inclusive (`MaxTakesCeiling`, a technical sanity bound, not a product limit).
- `MaxClips` must be between 1 and 64 inclusive (`MaxClipsCeiling`, same). The real limits are the credit balance, `MaxSpendCredits`, the budget gate, daily caps and the up-front hold.
- If `MaxClips` is in range, `Takes` must not exceed `MaxClips`.
- `DurationSeconds` must be greater than 0.
- `PollIntervalSeconds` must be between 1 and 300 inclusive.
- `TimeoutSeconds` must be between 60 and 7200 inclusive.
- If both are in range, `TimeoutSeconds` must be at least `PollIntervalSeconds`; otherwise the
  take would be paid for and time out before its first poll.
- `Plan` is required when `PlanSource` is `StepRef`.
- `Plan` and `Analysis`, when set, must be sourced `From = Previous` or `From = Step`, and
  `From = Step` requires a `StepOrder`.

`MaxClips` is a ceiling counted across the whole step, spent in plan order — shot major, take
minor. On the `StepRef` path every clip past it is reported `skipped` with
`skipReason: MAX_CLIPS_EXCEEDED` and consumes no ledger row, and its bound is 1..64, set by the user.

`Validate(capabilities)` runs after the provider resolves and its price is established, against
that model's capabilities, and adds:

- `DurationSeconds` must lie within the model's minimum and maximum.
- `Resolution` must be a key of the model's price table (ordinal match).
- `Ratio` must be one of the model's allowed ratios (ordinal match).

For `MiniMax` those capabilities are the full published table from `Describe(Model)`. For
`Higgsfield` they are the single configuration the price oracle answered for, so all three checks
collapse onto the step's own `DurationSeconds`, `Resolution` and `Ratio`. On an oracle-priced model
that is the same condition as "the vendor priced this configuration" — see
[An oracle-priced model is priced at one duration](#an-oracle-priced-model-is-priced-at-one-duration).

It deliberately adds no `StepRef` rule: `DurationSeconds` stays the `Inline` path's per-clip
duration, while the plan path snaps each shot's own duration word, so it is in range by
construction. On an oracle-priced model the second half of that still holds — every shot snaps,
just to a single point rather than a range.

On the `Inline` path the executor resolves the provider first — the prompt's length cap is per
provider kind — and then sanitizes the prompt:

- An empty or whitespace-only `InlinePrompt` fails `INVALID_PROMPT`.
- A prompt over the resolved kind's cap (7000 characters for MiniMax, the same cap for Higgsfield)
  fails `INVALID_PROMPT`; it is not truncated.
- Control characters other than `\n`, `\r` and `\t` are stripped first, so a prompt made only of
  control characters is rejected as empty.

On the `StepRef` path each planned shot's `prompt` goes through the same pass, but an empty or
over-length prompt fails only that clip.

The `Analysis` reference is not required by `Validate()`, because the config alone cannot know what
the plan will contain. The executor enforces it: a plan in which any shot names `firstFrame` or
`lastFrame` and no `Analysis` is configured ends the step `CONFIG_INVALID`, naming the offending
shots, before anything is reserved.

### Save-time validation

The Inference API rejects a `VideoGenerate` step whose config would fail `Validate()` before it is
stored, using `StepConfigSaveValidator` with the executor's own JSON options. A missing or blank
`videoGenerateConfigJson`, invalid JSON and a JSON `null` are rejected too. Each error reads
`Step <order> (VideoGenerate): <rule>`.

| Path | Rejection |
|---|---|
| `POST` and `PUT /api/v1/projects/{projectId}/workflows[/{id}]` | `400 {"message": "<errors, one per line>", "errors": [...]}`, nothing written |
| `POST /api/v1/projects/{projectId}/workflows/assistant/apply` (assistant Apply) | same `400 {message, errors}`, before the create or update branch writes anything |
| Assistant `ApplyWorkflow` tool | tool result `{"error": "<errors, one per line>", "errors": [...]}`, nothing written |
| Assistant `ValidateWorkflow` tool and `POST .../workflows/assistant/validate` | a result with `valid` false and the same errors, so validation never passes a step Apply would reject |

Save time checks only the provider-free rules — which now include the structural `Plan` and
`Analysis` rules, so a plan-sourced config with no `Plan` is rejected before it is stored. The
per-model duration, resolution and ratio checks and the prompt checks need the resolved provider or
run in the executor, so they still fail the step at run time.

The web workflow builder (`web/lib/utils/video-generate-validation.ts`) blocks Save client-side
and shows `Step N: <first error>`. It applies the `Validate()` rules it can check without a resolved
provider, and adds the prompt rules, which the server checks only at run time:

- `InlinePrompt` must be non-blank and at most 7000 characters.
- `InlinePrompt` must contain no control characters other than `\n`, `\r` and `\t`. The executor
  strips those at run time instead of failing.
- A `VideoGenerate` step with no config is reported as "Max Spend (credits) must be greater than 0",
  the first error its default config would produce.

Example `Inline` step config:

```json
{
  "planSource": "Inline",
  "inlinePrompt": "A slow dolly shot across a sunlit desk with a laptop showing a dashboard",
  "model": "MiniMax-H3",
  "resolution": "768P",
  "durationSeconds": 6,
  "ratio": "16:9",
  "takes": 2,
  "maxClips": 2,
  "maxSpendCredits": 500
}
```

That step estimates 2 × 6 s × $0.08/s = $0.96, which is **160 credits** at the platform's credit
price, and passes the 500-credit step cap.

Example plan-sourced step config:

```json
{
  "planSource": "StepRef",
  "plan": { "from": "Step", "stepOrder": 3 },
  "analysis": { "from": "Step", "stepOrder": 1 },
  "model": "MiniMax-H3",
  "resolution": "768P",
  "durationSeconds": 6,
  "ratio": "16:9",
  "takes": 1,
  "maxClips": 4,
  "maxSpendCredits": 5.0
}
```

`plan` names the `ShotDirector` step and `analysis` the `VideoAnalyze` step whose offered ids
resolve any keyframe a shot asks for. `durationSeconds` is not read for pricing on this path — each
shot's own duration word is snapped instead — but it still has to be a duration the model offers,
because `Validate(capabilities)` checks it unconditionally.

## The budget gate

`IVideoGenerationBudgetGate.ReserveAsync` (`WorkflowEngine/Services/VideoGeneration/`) checks
every cap and writes every take's ledger row as one atomic operation, before anything is
submitted.

**Pricing is fail-closed.** The executor prices each take with
`VideoGenerationPricing.EstimateClipCostUsd`: the model's per-second price for `Resolution` times
`DurationSeconds`. An unknown model, an unpriced resolution, or a non-positive estimate throws,
and the step fails `CONFIG_INVALID`. An estimate is never 0. Where the price itself came from —
a published table, or the vendor's own quote fetched by the price oracle — is
[Pricing purity and the price oracle](#pricing-purity-and-the-price-oracle); either way the
arithmetic below is unchanged and the gate still sees a number that is never 0.

### The unit: credits, not dollars

Every figure the gate compares and every figure it writes is **credits**. One credit is **$0.006**
of platform cost (`PricingDefaults.CreditUsdValue`, decision D9), and the ledger's own amounts have
always been integer **milli**credits, so the video-generation spend cap is the last place that had
to stop being the odd one out.

A vendor prices in its own currency and that is not abandoned: `MiniMaxVideoGenerationClient.Describe`
and `VideoGenerationPriceQuote` still publish the vendor's USD figures exactly as the vendor
stated them — see [MiniMax facts this feature relies on](#minimax-facts-this-feature-relies-on).
What changed is that a USD figure crosses into a cap or a ledger row through exactly one place,
`Metering.CreditCurrency.CreditsFromUsd`, and nowhere else. So a take the vendor quotes at $0.96
is a 160-credit take: it fits a 160-credit `MaxSpendCredits` exactly and is refused by 159.

The ledger's unit tag keeps the two quantities apart rather than converting them into each other:
`external_generation_jobs.cost_unit` is `"credits"` for a priced vendor and `"gpuseconds"` for a
self-hosted `ComfyUI` provider, and no cap, daily total or query ever mixes the two. See
[Budgeting GPU time](#budgeting-gpu-time).

**The reservation, in order:**

1. No takes, a take estimate ≤ 0, `MaxSpendCredits` ≤ 0, or a `Guid.Empty` step-result id → deny
   `CONFIG_INVALID`.
2. A take whose `(ExecutionId, RequestHash)` already has a `Reserved` or `Submitted` row is
   returned as existing: not inserted again and not charged again.
3. **Step cap:** the sum of every take's estimate (new and existing) > `MaxSpendCredits` → deny.
4. **The user's own daily limit** (credits only): the run owner's charged video spend today (their executions'
   rows, `workflow_executions.initiated_by_user_id`) + the new takes' estimate > the owner's
   `user_video_spend_limits.daily_limit_credits` → deny, naming that limit and where to change it. No row, a null
   limit or a run with no initiating user (a schedule) has no such limit: the default is **no daily limit** beyond the
   credit balance and the step's `MaxSpendCredits`. Read and counted inside the same advisory-lock transaction as the
   insert. See [User daily video limit](#user-daily-video-limit).
   An optional operator per-project cap (`VideoGeneration:DailyBudgetCredits`) is **unset by default**; when set it
   is checked first (≤ 0 denies everything).
5. **Operator safety ceiling:** charged spend today across all projects + the new takes' estimate >
   `VideoGeneration:GlobalDailyBudgetCredits` → deny. This is runaway-spend protection (default: the credit
   equivalent of $5000, far above any normal day), not a product limit. A ceiling ≤ 0 denies everything.
6. Otherwise one `Reserved` row is inserted per new take and every take is returned in order.

**Shared ledger, not shared caps, with `AudioGenerate`.** Music and sound-effect takes reserve through this same
gate and ledger with `VideoGenerationBudgetScope.Audio` (product decision 6): only step 3 (the step cap) applies to
them, steps 4, 4b and 5 (and the user's limit) are skipped, and their rows (clip id `music-{k}` / `sfx-{k}`) are left out of every
video daily sum (`VideoBudgetAccounting.TodayJobs`), so audio never draws on or counts against a video cap (see
[video-editing.md, Generating music and sound effects](video-editing.md#generating-music-and-sound-effects)).

## Generating pictures

`StepType.ImageGenerate` (`ImageGenerateStepExecutor`) shares this ledger and gate with `VideoGenerationBudgetScope.Image`,
the same way audio does: only the step cap (`MaxSpendCredits`) and the credit limit apply, no daily cap, and every
picture is its own `external_generation_jobs` row (clip id `img-{k}`). All pictures of the step are reserved together
or none are. Edit takes `Variations` 1 to 4 like Generate. Generate uses the `ImageGeneration` provider, Edit the `ImageEdit` provider; Edit uploads the source
picture to the vendor and needs `AllowSourceMediaEgress`, or the step fails with `EGRESS_REFUSED` before anything is
reserved. The step is not step-cached; an identical earlier Succeeded picture in the same project is reused without a
new charge, unless the project has a quarantine marker newer than that picture. A picture generated but lost in delivery is
`Abandoned`: the user is not charged, but the row still counts toward `MaxSpendCredits`. When no cap is set the step cap is
the price x (1 + `VideoGeneration:MaxAbandonsPerTake`), which leaves room for a retry. A retry never re-submits an unknown outcome (`RESERVATION_ORPHANED`).

**Batch (one picture per item).** `ItemList` (one item per line, or comma or semicolon separated; at most 24 items of 120 characters, duplicates dropped) makes the step run once per item: `{item}` in the `Prompt`/`Instruction` (required when `ItemList` is set) is replaced by each item. Edit with `Source=BriefFiles`, `Role=Image` instead edits each picture file chosen for the run, one edit per file (`{item}` is the file name; `ItemList` is refused with it; egress consent as for any Edit). Pictures = items x `Variations`, at most 24 (`MaxTotalImages`); the hold, estimate and reservation use that count. For `BriefFiles` the file count is unknown until the run, so the hold is the worst case (24 pictures, rounded down to whole variations). Picture index `k = item x Variations + variation` is the clip id and part of the request hash, so a retry or resume never double-submits. Each output entry carries `item`, `itemIndex` and `variation`; files are named `{prefix}-{exec}-{nn}-{item-slug}[-vN]`. `MaxSpendCredits` stays the cap of the whole step, and a partial failure leaves the delivered pictures as today. `ItemList`, `Prompt` and `Instruction` can be bound to app inputs (docs/design/app-inputs.md 7.2).

**All or nothing.** A denial writes no rows and submits nothing; the step fails
`BUDGET_EXCEEDED` (or `CONFIG_INVALID`) with the gate's reason in `meta.error`. There is no
partial fulfilment.

**The failure always says why.** Every `BUDGET_EXCEEDED` path (`VideoBudgetMessages.Describe`) fills
`ErrorDetails` and the run's `ErrorMessage` with one plain sentence; it is never blank, and
`VideoBudgetMessages.GenericSentence` covers a denial that arrives without text. The gate's refusal
(`BudgetRefusal`) names the cap that refused, its value, the amount already used today, the request's
estimate and the reset instant. The cap is named in words: "this step's spending limit" (or "GPU-minutes
limit"), "the daily GPU-minutes budget" and
"the platform's daily video budget". For example: "This needs about 120 credits of video
generation but the project's daily video budget is 300 credits and 290 are already used today (10 credits left); it
resets at 00:00 UTC (in 5 h 12 min). Generate fewer clips or wait." **A personal daily limit shows no figures:**
the sentence is stored as the step error and seen by every project member, so it only says "A personal daily video
limit was reached for this run", when it resets, and where the person who started the run changes it (Settings >
Usage > Daily video limit); that person sees their own limit and use on the Usage settings card. A daily cap resets at the next
00:00 UTC; the step's own limit has no reset and says to raise the step's limit instead. GPU figures are
shown in minutes. **A tenant never sees a platform-wide figure:** when the platform cap refuses, the text
only says "Video generation has reached the platform's daily limit; it resets at 00:00 UTC (in ...)".
The code, the caps and the plan figures are unchanged.

**What counts as spent.** "Today" is rows with `CreatedAt` on or after UTC midnight. Every row
except `Failed` and `Cancelled` is charged — `Reserved`, `Submitted`, `Running` and `Succeeded`.
A charged row costs `max(CostEstimate, ActualCost, 0)`, so a vendor-reported actual below
the estimate never frees headroom, and no row can credit the ledger. `Failed` and `Cancelled`
rows are released.

**Concurrency.** The read-then-insert runs under a process-wide lock, inside a transaction, and
on PostgreSQL under `pg_advisory_xact_lock` with a single global key. Two concurrent steps,
in the same or different projects and engine processes, cannot both pass a cap only one of them
fits under.

### Pricing purity and the price oracle

`Describe(model)` is still **pure and still makes no network call**. For Higgsfield it still throws
`NotSupportedException`, because the vendor publishes no per-second and no per-model price table and
no offline capability data either — a synchronous, no-network lookup has nothing to return that
would not be an invented vendor fact, and returning a zero-valued price table is the `$0` failure
this feature exists to prevent.

What changed is the **pricing path**. It may now make one bounded, cached network call through
`IVideoGenerationPriceOracle.QuoteAsync`, which is implemented **only** by the Higgsfield client and
is deliberately **not** a member of `IVideoGenerationClient` — that interface is frozen, and
membership would imply every provider has a network price when most publish a table. The
provider-agnostic entry point is the `QuoteClipAsync` extension: a client that does not implement
the oracle falls back to its own `Describe` table, so asking is always safe and costs no network for
MiniMax. The executor resolves what it prices against through `VideoGenerationPricingBasis`, which
carries the capabilities **and** the vendor's own quote when there is one.

For a provider with a published table, nothing about this is visible: the basis takes the full
`Describe` capability set and no quote, and MiniMax behaves exactly as it always has. For an
oracle-priced model the price is one `POST /estimate/{endpoint_id}` whose body mirrors what the step
will submit — **flat**, the model parameters at the top level, with no `input` wrapper:
`{"duration": <int>, "aspect_ratio": "<ratio>"}`. (The wrapper this document previously showed was
the SDK's client-side option object, not the wire format — see
[The `input` wrapper was never the wire format](#the-input-wrapper-was-never-the-wire-format).)

The prompt is deliberately **not** sent, and the reason is cost rather than secrecy. The vendor's
worked example carries one, but the price query holds none, and adding one would put the prompt into
the oracle's configuration cache key — turning the pre-submit price into a **per-shot network call**
on a pricing hot path, which the pricing design forbids. The consequence is worth stating plainly: if
the vendor requires `prompt`, the estimate `4xx`es, the model stays unofferable, and **nothing is
priced wrongly** — the same fail-closed outcome as any other malformed estimate.

The oracle's contract, in full:

| Property | Value |
|---|---|
| Cache key | the four configuration components, joined into one string in the order model, resolution, ratio, duration — where the three **string** components (model, resolution, ratio) are each emitted as `<length>:<value>` and the duration is emitted bare, as its own decimal text with no length prefix. The length prefix makes that join injective: a resolution whose own text contains the join character cannot be confused with a model whose text does, so two distinct configurations can never share an entry |
| Success TTL | 10 minutes (`PriceQuoteTtl`) — long enough that pricing a whole plan is one call per distinct configuration and a vendor price change is still picked up within a working cadence |
| Failure TTL | 60 seconds (`PriceQuoteFailureTtl`) — shorter on purpose: a failure must not be sticky enough to hide a recovered vendor, but it must be remembered, so a plan against a broken oracle makes one attempt per minute rather than one per take |
| Cache ceiling | 256 entries (`MaxPriceQuoteCacheEntries`). On overflow, expired entries are dropped first, then the entry closest to expiry |
| Attempts | exactly **one** HTTP attempt per miss, under the oracle's own `DefaultPriceOracleTimeout` of **10 seconds** — never the provider row's `TimeoutSeconds`, which bounds a multi-minute generation poll — and always linked to the caller's cancellation token |
| Cache scope | the client instance. `VideoGenerationClientFactory` is a process singleton handing out one client per resolved provider, so a quote is served across executions within its TTL and can never cross provider rows |
| Values | `VideoGenerationPriceQuote` has **no representation of "free" or "unknown"**: its constructor rejects a non-positive total or rate, so an oracle failure can never be mistaken for a `$0` price anywhere the value travels |

**Every failure is fail-closed.** A network error, the 10-second timeout, a non-2xx status, a
non-JSON body, a missing `usd`, a `usd` that is neither a JSON number nor an invariant-culture
numeric string, and a `usd` that is not strictly positive all produce
`NotSupportedException` — the same exception `Describe` has always thrown for an unpriceable model,
so no call site needed a new catch. The model therefore stays **unofferable**: the step ends
`CONFIG_INVALID` carrying the oracle's reason, and **no ledger row is written**, because the price
is resolved before any row can exist. A fail-closed spend gate must not become fail-open because a
price could not be fetched, and it does not.

**Both defects this section used to record are resolved.** The vendor documents the estimate's
response shape but not its request schema, which is why an unexpected answer here is self-limiting
rather than dangerous — a 4xx makes the oracle throw and the model stays unofferable, exactly as it
was before this path existed, never at a wrong price. Two concrete instances of that abstraction
were once recorded here as open observations; both now follow the vendor's own primary source.

- **The request body is flat.** The vendor's billing page illustrates the estimate **request** as a
  top-level object — `{"prompt": "…"}` — with no `input` wrapper, and
  `HiggsfieldVideoGenerationClient` now sends that flat shape: `{"duration": …, "aspect_ratio": …}`.
  The wrapper this document previously described was a misreading of the SDK's client-side options
  object, and the submit body was corrected in the same pass, from its own cURL. Each shape rests on
  its **own** primary source, and the two now agree without either being evidence for the other.
- **`usd` is read as a number *or* a string.** The vendor's documented sample writes the response's
  members as JSON **strings** — `{"credits": "1.500", "credits": "0.094"}` — and that exact body is now
  a passing test. Parsing is invariant-culture, so a decimal point stays a decimal point whatever
  the host locale. This is a wire-dialect fact taken from the primary source, not a loosening of the
  fail-closed rule: a string that is not a number, a blank string, a zero or negative value and a
  null are all still failures. `credits` deliberately keeps its original number-only, integral-only
  reading — it never affects the price, and the documented `"1.500"` is not an integer either way,
  so widening it would change nothing about what is charged.

Neither shape is verified against a live call, and no credential exists or may be requested; both
follow the vendor's documented cURL and are not confirmed empirically. A mismatch is a 4xx that
leaves the model unofferable — never a wrong price.

### An oracle-priced model is priced at one duration

`VideoGenerationPriceQuote.ToCapabilities()` reports a **point**, not a range: minimum and maximum
duration are both the duration the quote was actually taken at, and the resolution and ratio lists
each hold exactly the one value asked about. That is the honest statement of what was priced — a
provider with no published capability data is offerable at precisely the configuration its oracle
answered for, and at nothing else. Widening the range would invent a vendor fact; narrowing it
further is impossible.

The consequence is worth saying plainly because it will surprise an operator. The plan path snaps
each shot's own `Short`/`Medium`/`Long` word through those capabilities, so on an oracle-priced
model **every planned shot gets the same duration** — the step's `DurationSeconds`, which is also
the configuration `POST /estimate` was asked about. There is no range for the words to snap into, so
`Short`, `Medium` and `Long` all resolve to that one point.

It also means the price lookup and the go/no-go decision are the same call: a Higgsfield plan step
whose inline `DurationSeconds` the vendor will not price **refuses at `ResolveAsync`** with
`CONFIG_INVALID`, before any ledger row exists, because that is the configuration the estimate was
asked about and the same request that prices the clip is what decides whether the step may run at
all. This is intended and covered by tests, not a defect — but it does mean that on this provider a
plan whose shots were written for a range of lengths loses that range, and a step that cannot be
priced cannot be inspected by running it.

### Cancelling a step while the oracle is pricing

**A known, pinned degradation.** If the step's cancellation token is **already cancelled** while the
oracle is in flight, `OperationCanceledException` propagates out of the oracle. That exception is not
in the executor's widened catch — `when (ex is NotSupportedException or ArgumentException)` — so it
reaches the outer `catch (Exception ex)` and the step records **`UNEXPECTED_ERROR`** rather than the
`CONFIG_INVALID` an oracle refusal produces. The step still fails cleanly and still writes nothing;
only the code on the step result is less specific than it could be. Nothing is charged on that path:
no ledger row exists yet when the price is being fetched.

The **execution-level cancellation registry remains authoritative** for what actually happened — it
records why the execution was cancelled, and that is what the execution's own status reports. Do not
read `UNEXPECTED_ERROR` on a stopped step as evidence of a bug in pricing. A cancellation that
arrives *after* the price is resolved is unaffected: the oracle has returned, and the ordinary
stopped-step paths in [Status per exit path](#status-per-exit-path) apply.

### Pricing a whole plan

On the `StepRef` path the executor prices the whole plan before reserving anything.
`ShotPlanCostEstimator.EstimateWholePlanUsd` sums, over every planned shot,
`Describe(model).PricePerSecond × snapped duration × takes`. Every per-clip price comes from
`VideoGenerationPricing`, the one pricing path, so the estimator cannot drift from the ledger.

On an oracle-priced model the same sum runs over the vendor's own quote instead of a table rate:
each shot's snapped duration is priced by `VideoGenerationPricingBasis.ClipCostUsd`, which returns
the quote's total for the configuration it was taken at and **throws** for any other — a
re-multiplied per-second rate would drift from the vendor's own figure. Since an oracle-priced
model's capabilities are a point, every shot snaps to that same duration and the sum is simply
`quote total × shot count × takes`. The guards are mirrored rather than reused: a `takes` below 1
throws with "a plan is never priced as free", and a null shot entry throws rather than being
counted as free. It stays exactly one price lookup for the whole plan, because the oracle is asked
once per step attempt and its cache serves the rest.

It fails closed the same way: an unpriceable resolution or an unresolvable duration word throws and
the step ends `CONFIG_INVALID` rather than contributing a silent `0`, because a zero estimate passes
every cap. An empty plan is the one legitimate zero — there is genuinely nothing to buy.

The estimate of the plan **trimmed to `MaxClips`** (what the step will actually buy; a take counts as a clip) is
compared against `MaxSpendCredits` **before** the gate's reservation. Exceeding it ends the step `BUDGET_EXCEEDED` with
zero submits and zero ledger rows. A change-request run checks the clips it redoes.

**A plan larger than `MaxClips` is trimmed first, and the cap is checked against what is left.** A clip past
`MaxClips` is skipped, never submitted or reserved, so the person's "most clips" and "most credits" agree with the run
gate, which prices `MaxClips` clips. The gate call that follows (`IVideoGenerationBudgetGate.ReserveAsync`) still
enforces the step cap and the daily and global caps on what is actually reserved, and `meta.planEstimated` is that
effective number.

## Egress: sending the project's own frames to a provider

A keyframe is not metadata about the user's footage — it *is* the user's footage, and once it has
been posted to a provider it cannot be recalled. `AllowSourceMediaEgress` (default `false`) is the
workflow author's explicit consent to send it, and `GeneratedShotEgressPolicy` decides it per shot.

The executor evaluates that policy against **every** shot in the plan first. A single refusal fails
the whole step with code `EGRESS_REFUSED` and the policy's reason, with zero submits and zero ledger
rows — and, because the pre-scan runs before the media session is even opened, with no source object
staged and no frame decoded. A partially generated plan is a half-edit the operator cannot use, so the
refusal has to be actionable — grant consent, or edit the plan — rather than silently dropping one shot.

With consent granted, a planned `firstFrame`/`lastFrame` really is transported, in three first-party
steps. Nothing in the chain involves a number the model supplied: the plan's reference stays an opaque
offered id, and everything numeric is derived from the analysis artifact's own measurements.

1. **Resolve the id.** `ShotKeyframeResolver` (a pure function, no I/O) walks the artifact's own
   `Shots` and `Sources`. The id must be one the planner was actually shown (`OfferedIds` — the offer,
   not the artifact, is the authorization); it must name a **shot**, because a silence gap or a
   transcript segment is a perfectly good anchor and not a frame; and its `SourceIndex` must resolve to
   a `Sources` entry with a storage key. A `firstFrame` is cut at the shot's `StartSec`; a `lastFrame`
   one frame-length BEFORE its exclusive `EndSec`, computed from that source's own probed rational rate
   (`FpsNum`/`FpsDen`) — so a closing image is a frame the shot actually contains and never the first
   frame of the shot that follows. The pair is all-or-nothing: an unreadable id costs both frames.
2. **Cut the frame.** `ShotKeyframeCapture` stages that object into the step's media session and asks
   `IKeyframeExtractor` for ONE frame at that moment — at most 1280 px wide, encoded `image/jpeg`, read
   back through the session's bounded read. The decode runs wherever the media is, on a runner when the
   compute fabric is on (`video.analyze.extract`, the same op set the analyzer's own keyframes use), and
   only the small encoded frame crosses back to the engine, which is the only party that can put it on a
   provider request. Frames are cut only for clips that survive the `MaxClips` trim, so a plan larger
   than its cap does not pay to decode frames nothing will send.
3. **Carry it.** The bytes ride `VideoGenerationRequest.Images`
   (`VideoGenerationImageInput(ContentType, Bytes)`), in order: first frame, then last frame. Putting them
   on the request is this step's half of the contract; transporting them to the vendor is the resolved
   client's half, and a client that ignores the list — or reads only the part of it its vendor's shape
   supports — drops what it does not use. `keyframeApplied` therefore records what THIS step did, and
   never claims anything about the wire.

A still photo the project holds is a frame like any other. It is a `VideoAnalyze` **source**, which the
analyzer renders into a short clip with one scene declared per beat (see
[Still images](video-editing.md#still-images)), so its beats are ordinary offered `s{n}` shot ids and
resolve through exactly the path above. That is why no second id namespace exists for stills.

**What is still refused, and why.** A plan that names a frame is refused with
`KEYFRAME_NOT_SUPPORTED` in two cases, both checked before a frame is cut and before any ledger row can
exist: the resolved provider kind has no image input for video generation at all (see
`VideoGenerationImageSupport`), or the resolved client's own per-model declaration
(`VideoImageInput.EnsureAccepts`) says that model cannot carry the frames the plan asks for — a
text-only ComfyUI template, a Higgsfield text-to-video endpoint, or a first-frame-only model asked for a
last frame. Submitting anyway would bill the operator for a clip that silently drops the frame they
consented to send.

Per clip, the manifest records what happened:

| Field | Value |
|---|---|
| `keyframeRequested` | `true` when the shot named a frame |
| `keyframeApplied` | `true` when the request this step submitted carried it |
| `keyframeReason` | `keyframe_unresolved`, `keyframe_extraction_failed`, `keyframe_not_dispatched`, or null |

`meta.keyframesRequested` and `meta.keyframesNotApplied` count them. Those three reasons are the only
ways a requested frame can still be recorded as not applied, and each is something this step can really
observe: the id was never offered (or the artifact cannot place it), the frame could not be cut (ffmpeg,
or the media session), or the clip never reached a ledger row at all because the cap trim dropped it.
`keyframeApplied` is a claim about the REQUEST the step submitted — it is never set for a frame that was
not cut, and it is never set on the strength of a model's word.

The transport itself is credential-free where the vendor requires it and validated before it does
anything. Higgsfield: the content type must be one of `image/jpeg`,
`image/jpg`, `image/png`, `image/webp` or `image/gif` *before* any network call; the presigned URL
must be an absolute `https:` URI; every `upload_headers` entry the vendor returns is applied verbatim
with **no** `Authorization` header, and a response that asks for one is refused rather than followed.
MiniMax: the vendor's own format list except HEIC/HEIF, ≤ 30 MB per file, 256..5760 px per side, a
0.4..2.5 aspect ratio read from the image header, at most one first and one last frame and a ≤ 64 MB
serialized body — all checked locally, each refusal naming the limit. ComfyUI: the bytes go to the
server's own `POST /upload/image` and only the returned name enters the graph, and a frame the
template's graph cannot hold is refused.

When a shot names a frame at all, the config's `Analysis` reference is required. An id that was never
offered records `keyframeReason: "keyframe_unresolved"` on that clip, which is likewise not fatal.


## Starting a generated clip from a frame

An Inline `VideoGenerate` step can start from an image: set `FirstFrame` (and optionally `LastFrame`) to an earlier step whose output is an image, typically an [`ExtractFrame`](video-editing.md#grabbing-a-frame-the-extractframe-step) of the previous clip's last frame, or to a project image file id. The image bytes ride the provider request as `Images[0]` / `Images[1]`; every take of the step uses the same images, and pricing is unchanged.

```json
{"inlinePrompt":"The camera keeps pushing in","firstFrame":{"from":"Step","stepOrder":2},"allowSourceMediaEgress":true}
```

The order is fail-closed and entirely before the budget reservation, so a refusal buys nothing:

1. **Egress.** An image of the author's own footage leaves the platform only on an explicit `AllowSourceMediaEgress: true`; otherwise the step fails `EGRESS_REFUSED` (the same `GeneratedShotEgressPolicy` the plan path uses) and no byte is read.
2. **Capability.** The resolved provider kind and model must accept the number of images asked for (first frame only, or first and last), else `KEYFRAME_NOT_SUPPORTED`.
3. **Resolution.** A step reference resolves through the run's own output history, a project file through the project's own file list, and the key must sit under `projects/{project}/`. A reference that names nothing is `FRAME_INPUT_NOT_FOUND`; an object that is not a PNG or JPEG (sniffed from its bytes, at most 20 MB) is `FRAME_INPUT_INVALID`. The 20 MB cap is enforced BY the download: the object's own length is refused before a byte of the body is read, and the copy stops at the bound if that length lies, so an oversized object is never written to local scratch first. Any other read failure (an auth error, a transport error, an object our storage no longer has) is `FRAME_INPUT_UNAVAILABLE` — our side, so the step is refunded — with one fixed sentence and the exception's own text only in the server log.

`Validate()` refuses frames unless `PlanSource` is `Inline` (a plan names its own keyframes per shot), a step reference that is not `Previous`/`Step` with an order, a slot that names both a step and a file, and a `LastFrame` without a `FirstFrame`. The output meta lists the images used under `frameInputs` as `{role, contentType, bytes}` — what was sent, never where it is stored: the step output is tenant-visible and carries no storage key.

---

## Resolutions per model, and the run's resolution

`Resolution` is a free string checked, ordinally and case-sensitively, against the chosen model's price card: MiniMax-H3 accepts `768P` and `2K`, MiniMax-H3-Max `480P` and `768P`, and each fal endpoint its own tiers (up to `4K`). The step's schema (`ListStepSchemas`, `StepConfigSchemaExporter`) lists the allowed values per model in the `resolution` description; the list is generated from the price cards (`VideoGenerationResolutionCatalog`), not hand-written. A value the card does not key is refused with the allowed values named. Higgsfield (live quote) and ComfyUI (templates) have no static card and are checked when the step runs.

`ConformHeight` defaults to the run's chosen output resolution. When it is `0` and the run carries an explicit output resolution (`720p`, `1080p`, `2160p`; a run on Auto has none), each delivered clip is conformed up to that short side (its height for landscape and square ratios; for a portrait ratio the height that makes the width the short side). The conform pass only ever upscales a smaller clip, so a clip never goes beyond what the run chose. An explicit `ConformHeight` always wins. The applied value is reported in the output meta as `conformHeight: {height, source: "run"}`; a run on Auto, or a config that sets its own, adds nothing.

---

## Job lifecycle and crash-resume

Each take has one row in `external_generation_jobs` (WorkflowEngine-owned, no foreign keys, so a
row outlives its execution, step result and project). Columns: `id`, `execution_id`,
`step_result_id`, `project_id`, `clip_id`, `request_hash`, `provider_id`, `remote_job_id`,
`status`, `cost_estimate`, `actual_cost`, `storage_key`, `created_at`, `updated_at`.
`status` is one of `Reserved`, `Submitted`, `Running`, `Succeeded`, `Failed`, `Cancelled`; the
executor never writes `Running`. `step_result_id` is the id of the step result for the attempt
that reserved the row. A retry that reuses the row (see [Attempts and resume](#attempts-and-resume))
keeps the original attempt's id, so the column does not point at the attempt that last polled.
The gate refuses a reservation whose step-result id is `Guid.Empty` with `CONFIG_INVALID`.

`request_hash` is SHA-256 over model, resolution, ratio, duration, take index and the sanitized
prompt.

### Status per exit path

Takes run one after another. Every exit writes `UpdatedAt` and saves with
`CancellationToken.None`, so a stopped step still records its outcome. `failureReason` in the
clip output is the code; `detail` carries the human explanation.

| Exit path | Row status afterwards | `failureReason` | Charged? |
|---|---|---|---|
| Delivered | `Succeeded`, `StorageKey` set | — | yes |
| Vendor reports `failed`, or Higgsfield reports `nsfw` | `Failed` | `PROVIDER_INSUFFICIENT_BALANCE` if the reason contains `402` or `insufficient`, else `PROVIDER_FAILED`; the vendor's own words, including the moderation text for `nsfw`, go in `detail` | released |
| Vendor reports `cancelled` | `Cancelled` | `PROVIDER_CANCELLED` | released |
| Submit returns 4xx other than 408 | `Failed` | `PROVIDER_INSUFFICIENT_BALANCE` (402), `PROVIDER_AUTH_FAILED` (401/403), else `PROVIDER_REJECTED` | released |
| Submit fails otherwise (5xx, 408, network, malformed 2xx, cancellation) or returns no task id | `Reserved`, unchanged | `SUBMIT_OUTCOME_UNKNOWN` | yes |
| Poll throws, or the step is stopped while a submitted take waits or polls | `Submitted`, unchanged | `POLL_ERROR` | yes |
| `TimeoutSeconds` elapses | `Submitted`, unchanged | `POLL_TIMEOUT` | yes |
| Vendor `succeeded` with no download URL | `Submitted`, `ActualCost` set | `NO_DOWNLOAD_URL` | yes |
| Vendor `succeeded`, then download, normalize or upload throws | `Submitted`, `ActualCost` set | `DELIVERY_FAILED` | yes |
| Fresh take never submitted because the step was stopped | `Cancelled` | `NOT_SUBMITTED` | released |
| Take reserved by an earlier attempt with no `RemoteJobId` | `Reserved`, untouched | `RESERVATION_ORPHANED` | yes |
| Any other exception | `Cancelled` if the take was fresh and never submitted; otherwise unchanged | `UNEXPECTED_ERROR` | released / yes |

Only a vendor-confirmed failure or cancellation, a definite 4xx submit rejection, or a take that
never reached the vendor is released. Everything that might have been billed stays charged.

### Actual cost

When the vendor reports `succeeded`, before downloading, the executor records `ActualCost`. On a
table-priced model that is `usage.total_seconds × the model's per-second price for Resolution`. On an
oracle-priced model the vendor's own quote is honoured **only** when the reported usage equals the
duration the quote was taken at, in which case the actual is the quote's total; any other reported
usage records `null`, because a re-multiplied rate would be a different number from the one the
vendor quoted.

A usage of `0` records `0`; a missing, null, non-integer or negative usage records `null`. The
executor never copies the estimate into `ActualCost`.

Higgsfield reports **no metered seconds at all**, so its usage is always `null` and a Higgsfield
clip's `ActualCost` is `null` in practice — which leaves that clip's `costBasis` at `estimate`.
That is the honest outcome rather than a gap: the vendor does not report consumption, so there is
nothing to reconcile the charge against.

### Attempts and resume

`VideoGenerate` is retried like an `Agent` step: up to `WorkflowHardening:MaxStepRetries`
attempts (default 3), each calling `ExecuteAsync` again with the same `ExecutionId`. On the
dispatch path the step is `Failed` only when every dispatched take failed, so that is the only
take-driven retry. Every pre-gate refusal is `Failed` too (`CONFIG_INVALID`, `INVALID_PROMPT`,
`PROVIDER_NOT_FOUND`, `EGRESS_REFUSED`, `BUDGET_EXCEEDED`, `UNEXPECTED_ERROR`) and is retried the
same way, to the same limit: `VideoGenerate` is deliberately not one of the deterministic step
types capped at a single attempt. Each attempt goes through the budget gate again, within every
cap.

On a retry, the gate hands back the earlier attempt's `Reserved`/`Submitted` rows as existing:

- **Row has a `RemoteJobId`:** the take is polled only, never re-submitted. This is how a
  `POLL_TIMEOUT` or `POLL_ERROR` take gets another chance, and how a `DELIVERY_FAILED` take gets
  a fresh download URL.
- **Row has no `RemoteJobId`:** the earlier attempt may have reached MiniMax without recording
  the task id. The take ends `RESERVATION_ORPHANED` with zero submit calls and the row untouched.
- **`Failed`/`Cancelled` rows** are not returned as existing, so those takes get a new row and a
  new submission.

#### A new attempt: Resume and Retry

Resume and Retry start a new attempt of the same execution. There, an earlier attempt's `Reserved`
take with no `RemoteJobId` is not left as `RESERVATION_ORPHANED`:

- The old row is marked `Abandoned`, and a new row is reserved and submitted under a **fresh
  idempotency key**.
- The user is charged once for the clip and never for the `Abandoned` row.
- The vendor may have been paid for the old submission, so an `Abandoned` row **keeps counting** at
  its reserved amount toward the step's `MaxSpendCredits` and the daily, organization and global
  caps. It is only excluded from the user's charge.
- `VideoGeneration:MaxAbandonsPerTake` (default 1) caps how often one take is abandoned per
  execution. Beyond it the take ends `RESERVATION_ORPHANED`.
- An earlier same-step orphan with a different request hash is abandoned and counted the same way.
- `Status` is a concurrency token, so a stale writer cannot overwrite an `Abandoned` row.

Auto-resume never takes this path: the recovery sweeper does not re-queue a lost run that has a
`Reserved`, `Submitted` or `Running` row. That run is `Failed` and waits for a manual Resume. See
[Workflow execution optimization, section 8](./workflow-execution-optimization.md#8-run-snapshots-resume-and-retry).

### Handling an orphaned reservation

1. Read the clip's `detail`: it names the ledger row id and its `CreatedAt` (UTC).
2. In the MiniMax console, look for a video task created near that time.
3. If a task exists, the money was spent and the clip is not in ReelBolt. If none exists,
   nothing was billed.
4. Either way the reservation stays charged against the project and global caps until the UTC
   day rolls over. Do not edit the row to free budget unless you have confirmed no task exists.

### Per-provider concurrency

`VideoGeneration:MaxConcurrentPerProvider` (default 2, minimum 1) bounds how many takes hold a
slot on one provider at once, keyed by provider id. A take acquires the slot before it submits
(or, when resuming, before its first poll) and releases it when polling ends, before download
and normalization, so a slow download never blocks another submit. The `TimeoutSeconds` clock
starts only once the slot is held. The limiter is per engine process and shared across all runs on it. The hold and estimate are linear in clip count (8 clips cost twice 4). Wall-clock: 8 clips at 2 concurrent at about 5-10 minutes per MiniMax clip is 4 waves, about 20-40 minutes.

MiniMax documents no concurrency limit for the v2 H3 API; the default of 2 is ReelBolt's own
conservative choice. Higgsfield's error table lists "concurrency reached" among the meanings of its
`400`, so the vendor does enforce something — but it publishes no number, so ReelBolt's default of
2 is a conservative choice there too.

## Step output

A step that reached the gate emits:

```json
{
  "clips": [
    { "clipId": "clip-1", "status": "succeeded", "stored": true, "cost": 0.48, "costBasis": "actual" },
    { "clipId": "clip-2", "status": "failed", "failureReason": "POLL_TIMEOUT", "detail": "…", "cost": 0.48, "costBasis": "estimate" }
  ],
  "meta": {
    "totalCost": 0.96,
    "actualCost": 0.48,
    "unconfirmedCost": 0.48,
    "estimatedCost": 0.96,
    "clipsGenerated": 1,
    "clipsFailed": 1,
    "clipsSkipped": 0
  }
}
```

Both plan sources share this projection, so the `Inline` path's `clip-{n}` clips and the plan path's
`v{n}` clips are shaped identically. A clip carries `{clipId, status, failureReason?, detail?,
skipReason?, stored, cost, costBasis}`.
`stored` is a boolean, not a path — `OutputJson` reaches later agents under
`AgentInputContextMode.FullWorkflow`, so no storage key may appear in it (see
[How a planned clip is addressed](#how-a-planned-clip-is-addressed)). `shotIndex`, `take`,
`purpose`, `anchor` and `durationSeconds` are manifest-only and do not appear here.

`costBasis` labels each clip's `cost`:

| `costBasis` | When | `cost` |
|---|---|---|
| `actual` | `ActualCost` is known | `ActualCost` |
| `estimate` | row charged, actual unknown | the row's `CostEstimate`, never 0 |
| `released` | row `Failed` or `Cancelled` | `0` |

`meta.totalCost` sums every clip's `cost`; `actualCost` and `unconfirmedCost` sum the
`actual` and `estimate` clips; `estimatedCost` sums every take's pre-submit estimate;
`clipsGenerated`, `clipsFailed` and `clipsSkipped` count clips by status.

A plan-sourced step adds:

| Key | Meaning |
|---|---|
| `planEstimated` | The effective, post-trim plan's estimate — the exact sum of the reservations handed to the gate. |
| `shotsPlanned` | How many shots the planner offered, before the `MaxClips` trim. |
| `keyframesRequested` | Clips whose planned shot named `firstFrame` or `lastFrame`. |
| `keyframesNotApplied` | Of those, the ones whose keyframe was not applied. |
| `note` | `EMPTY_PLAN` when the planner legitimately offered no shots at all. |

If every take failed, the step status is `Failed`, `meta.failed` is `ALL_TAKES_FAILED`, and
`ErrorDetails` lists `clipId:code` pairs, e.g. `clip-1:POLL_TIMEOUT, clip-2:PROVIDER_FAILED`. With
at least one delivered take the step is `Completed`.

A step that fails before the gate reserves anything (`CONFIG_INVALID`, `INVALID_PROMPT`,
`PROVIDER_NOT_FOUND`, `BUDGET_EXCEEDED`, `EGRESS_REFUSED`, `UNEXPECTED_ERROR`) emits
`{"clips": [], "meta": {"failed": "<code>", "error": "<message>"}}`.

## How a planned clip is addressed

`OutputStorageKey` still holds only the first succeeded clip, which is what the UI player and a
later `VideoAnalyze` step read. Every other planned clip is addressed through the per-clip manifest:

```text
projects/{projectId}/agentFiles/video-analysis/{executionId}/step-{order}-generated-clips.json
```

`VideoGenerateStepExecutor` writes it and points `WorkflowStepResult.ArtifactStorageKey` at it. It
carries `clips[].{clipId, shotIndex, take, purpose, anchor, firstFrame, lastFrame, camera, duration,
durationSeconds, status, storageKey, cost, costBasis, keyframeRequested, keyframeApplied,
keyframeReason, failureReason, detail, skipReason}` plus the step's `meta`, and the compile step
resolves `generatedClips` → that step's `ArtifactStorageKey` → the manifest.

Clip ids are `v{n}` in 1-based plan order when `Takes` is `1`, and `v{n}t{k}` otherwise, so a later
take-selection pass has distinct ids to choose between. The `Inline` path keeps phase 1's `clip-{n}`.

**Why the manifest exists at all.** The step's output JSON no longer carries `clips[].storageKey`.
It carries `clips[].stored` instead, so that no storage path is ever model-visible: the output
reaches later agents under `AgentInputContextMode.FullWorkflow`, and a test asserts the output
contains no `storageKey` and none of `projects/`, `agentFiles/` or `outputFiles/`, while the
manifest behind `ArtifactStorageKey` carries the real keys.

Because the manifest sits under the `video-analysis/` prefix and not `outputFiles/`, the existing
artifact endpoint serves it with **no backend change**:

```text
GET /api/v1/projects/{projectId}/step-results/{stepResultId}/artifact
```

It already validates that prefix, so the manifest reads through it exactly like a `VideoAnalyze` or
`VideoCompile` artifact does.

## Where the clips go in the edit

The compile side takes five settings for this feature. Three were appended in one block:
`generatedClips` (`ExtractInputRef?`, default `null`), `enableGeneratedClips` (bool, default `false`)
and `maxGeneratedClips` (int, default `8`). `maxSeamBridgeMs` (int, default `3000`, floor `500`)
caps how much of a `SeamBridge` clip plays, and `takeSelection` (`ExtractInputRef?`, default `null`,
appended LAST — after `loudnessTarget`) names the dailies room's answer (see
[Dailies](#dailies-choosing-between-takes)). Placement derives from each planned shot's own `purpose`
— there is deliberately no per-purpose placement config, because a second config surface would only
let the two disagree.

| `purpose` | What the compile step does |
|---|---|
| `Cutaway` | Replaces **picture only** over the anchor's window on the OUTPUT timeline. The base audio chain is untouched, so dialogue runs continuously across the cutaway — a J/L-cut. |
| `ColdOpen` | Prepended as its own concat segment, pairing with `ProgramFadeIn`. |
| `EndCard` | Appended as its own concat segment, pairing with `ProgramFadeOut`. |
| `SeamBridge` | Spliced into the cut as its own **picture-only** segment immediately BEFORE the offered anchor it leads into — see [Seam bridges](#seam-bridges). |
| `Extend` | Accepted vocabulary that degrades explicitly to `not_supported_in_this_wave` rather than being silently misplaced — see [Seam bridges](#seam-bridges) for why. |
| `ScreenContent` | **Wave C:** routed through the existing tracked-insert path — the clip is composited INTO the offered, non-feature insert region (`r{n}`) whose tracked window overlaps the clip's anchor on the anchor's own source, as one more `InsertRequest` (origin `generated`) riding every validation, warp, mesh and matte rule a plan insert does. Zero new compositing code. Requires `enableInserts: true`; spends an INSERT slot (`maxInserts`), never `maxGeneratedClips`. Its key must sit under this project's own `agentFiles/generated/` root or it is refused before any fetch. |

A cutaway's window is `[anchorStart, min(anchorStart + clipDurationSeconds, anchorEnd))`, on the
output timeline. Every placement failure degrades **per clip** and never fails the compile. One hard
config error exists: `enableGeneratedClips` with `Mode: StreamCopy` ends the step
`GENERATED_CLIPS_REQUIRE_REENCODE`, because a picture replacement and a concat segment both need a
filtergraph.

### Seam bridges

A `SeamBridge` clip is **spliced into the cut**, not painted over it: it becomes a span of its own
synthetic source placed immediately before the offered anchor (`s{n}`/`g{n}`/`t{n}`) its plan names,
so the viewer sees the generated footage *between* two beats rather than a blend of them, and every
downstream stage — the output timeline, seams, overlays, music length, the program fade — already
works in source-indexed spans and needs no special case. Bridges are **picture only** (the clip's own
audio never plays) and fill the canvas (cover and centre-crop, never letterboxed). At most
`maxSeamBridgeMs` of the clip plays, from its first frame: a bridge is punctuation between scenes, not
a scene, and an uncapped 10 s clip between every pair of beats would double a promo's length with
footage that carries no message. Bridges count toward `maxGeneratedClips` like every other applied
clip, and are resolved BEFORE the output timeline is built — which is also why the dailies room's
`takeSelection` does not filter them (see [Dailies](#dailies-choosing-between-takes)).

This anchor-based splice is what shipped on `master`. An earlier branch of this work had bridged seams
differently — a dedicated `Generated` transition policy, a `maxGeneratedSeams` spend cap and a
`firstFrame`/`lastFrame` seam-matching rule, plus an `Extend` continuation for a kept shot whose
narration runs past its end. That design was **superseded** when master's splice landed and is not in
this tree: there is no `Generated` policy member, no `maxGeneratedSeams`, and `Extend` degrades
explicitly as `not_supported_in_this_wave` again rather than being re-implemented against a second
seam model. It is recorded here so the vocabulary the shot planner still emits (`Extend`) is not
mistaken for a placement.

Every unusable clip degrades per clip and the cut proceeds; nothing here fails the step. The reasons:

| Reason | Meaning |
|---|---|
| `anchor_not_in_output` | The clip's `anchor` is not an offered id, or that moment did not survive the cut (a `SeamBridge` or `Cutaway`, and a `ScreenContent` clip whose anchor is unknown). |
| `max_generated_clips_exceeded` | The clip lost to the global applied-clip budget (`maxGeneratedClips`), which bridges and placements share. |
| `not_supported_in_this_wave` | An `Extend` clip — accepted vocabulary, deliberately not placed (see above). |
| `unknown_purpose` | A purpose word outside the planner's vocabulary. |
| `inserts_disabled` | A `ScreenContent` clip while `enableInserts` is `false` (the default) — the insert path it rides is off. |
| `clip_key_outside_generated_prefix` | A `ScreenContent` clip whose manifest key is not under `projects/{projectId}/agentFiles/generated/`, contains `..`, or has a non-video extension — refused before any fetch. |
| `no_insert_region_at_anchor` | A `ScreenContent` clip whose anchor overlaps no offered, non-feature insert region on its own source. |
| `take_not_selected` | With a `takeSelection` configured, a take the dailies room did not choose for a shot it decided on, or a later-than-first delivered take of a shot it left undecided — see [Dailies](#dailies-choosing-between-takes). |
| `clip_not_available` | The clip's own manifest record disqualifies it — its `status` is not `succeeded` (case-insensitively), or its `storageKey` is blank — or it won its placement and then failed to download. Its sibling `clip_unusable` covers the other half of that same stage: the clip downloaded but failed to normalize or to probe, or probed to no positive duration and no video codec. |

The compile output records what actually applied: `generatedClips` carries `seamBridgesApplied`
beside its placement counters, and — only when present — `screenContentRoutedToInserts` and
`takeSelection`. A compile with no generated clips configured is unchanged key for key.

For the filtergraph-level detail — how the replacement is gated, how the concat segments are built,
and what the EDL's `generatedClips` node reports — see
[`video-editing.md`](video-editing.md#generated-clips-b-roll).

## Dailies: choosing between takes

A `VideoGenerate` step with `takes > 1` produces several takes per planned shot (`v{n}t{k}` ids).
Phase 4 adds the pass that chooses between them, built entirely from existing parts:

1. **A second `VideoAnalyze` step** with a `GeneratedClips` source (`VideoSourceKind.GeneratedClips`,
   `stepOrder` = the generation step) expands, before any download, into one analyzed source per
   DELIVERED clip of that step's manifest — a row whose `status` is `succeeded` and whose
   `storageKey` is set — in manifest order, each tagged with the manifest's own `clipId`. The
   expansion reads the manifest from the step result's `ArtifactStorageKey`, the only place a clip's
   key is written for a consumer; a manifest naming no deliverable clip, or an unresolvable step, is
   `SOURCE_UNRESOLVED`. Every other source kind is untouched and carries a null clip id, so a step
   with no `GeneratedClips` reference runs byte-identically. The bounded view's `sources` list is
   emitted whenever any source is a generated take (even a single one) and carries `clipId` only on
   tagged entries, so every pre-existing multi-source view stays byte-identical.
2. **A `DailiesRoom` step** (`StepType.DailiesRoom`, `DailiesRoomConfigJson`,
   `DailiesRoomStepExecutor : RoomStepExecutorBase<TakeSelectionOutput>`) — the fourth room on the
   shared room infrastructure (see `docs/video-editing.md` "The shared room infrastructure"). Three
   `TakeSelector` judge seats (`ContinuityJudge`/`MotionJudge`/`ImageJudge` by default) and a
   `DailiesDirector` compare the takes' MEASURED words and captions; the director then synthesizes
   one `TakeSelectionOutput` — a `selected` list of `{takeId, reason}` plus `planRationale`, every
   property a string (pinned by `TakeSelectionOutputInvariantTests`). The offered vocabulary is
   exactly `view.sources[].clipId`; a selection naming anything else is dropped, a second selection
   for an already-chosen shot (same `v{n}`) is dropped, and an EMPTY selection is a valid outcome
   (unlike the edit room, it never triggers the solo fallback). The convening gate is not supplied
   (the same structural reason as the edit room). Falls back to one solo `TakeSelector` call
   (`fallbackToSoloSelector`, default `true`).
3. **`VideoCompileStepConfig.TakeSelection`** (an `ExtractInputRef`, appended after
   `KeepWholeSources`) resolves that output and filters the manifest in the generated-clip prepass,
   BEFORE purpose classification, so a non-selected take of any purpose is dropped with one reason
   and never spends a download or a slot: a selected `clipId` is admitted; a clip belonging (by the
   manifest's own zero-based `shotIndex`, or the one-based `v{n}` number when a row has none) to a
   shot the room decided on but not the chosen take drops `take_not_selected`; a shot the room left
   undecided keeps its FIRST delivered take and drops the rest the same way. An unresolvable or
   malformed selection degrades to "no filter" and says so on the node (`generatedClips.takeSelection
   = {configured, applied, reason|selectedTakeIds}`); with no `TakeSelection` configured the node
   carries no `takeSelection` key at all, byte-identical to before.

A take that shows some of the project's people and objects carries them on its manifest clip
(`subjects: [{name, kind}]`), the analysis copies them into `view.sources[].subjects`, and the
room's judges are told their names and kinds in a `[PROJECT SUBJECTS]` block, to check that each
looks like itself across the takes (see [Avatars and objects in planned shots](#avatars-and-objects-in-planned-shots)).

The shipped template is `video-derush-edit-dailies` — see [Workflow templates](#workflow-templates).

## Look matching and camera continuity

Two plan-path-only switches, both default `false` (byte-identical prompt and camera word when off),
both reading the anchor's REAL shot through the config's `Analysis` reference:

- **`LookMatching`** appends a first-party clause to each shot's prompt before the shared sanitizer
  runs: `Match the look of the surrounding footage: <temperature>, <tone>, <saturation>.` The three
  phrases come from `LookMatchingPromptWords`, a fixed table over the analyzer's three CLASSIFIED
  words only — `ColorTemperatureClass` (Warm/Cool/Neutral), `ToneClass`
  (Contrasty/Flat/Crushed/Blown/Normal), `SaturationClass` (Muted/Vivid/Natural) — never the raw
  `Warmth`/`BlackPoint`/`SaturationMean` numbers, because a number in a prompt is exactly what this
  pipeline refuses to emit. An unclassified anchor yields no clause. The anchor's shot is the shot
  itself for an `s{n}` anchor, or the same-source shot containing the moment a `g{n}`/`t{n}`
  anchor begins. The manifest records the clause under `lookWords`.
- **`CameraContinuity`** fills a BLANK `camera` word with the anchor shot's measured move, and only
  when that move carries no direction the analyzer did not measure: `Static` and `Handheld` map;
  `Pan`/`Tilt`/`Zoom` do not (guessing `PanLeft` vs `PanRight` would put an unmeasured move into
  the clip). A camera word the planner chose is never overridden. The manifest records
  `cameraDefaulted`.

## Remotion background plates

A promo workflow can now generate abstract background plates and hand them to the `AuthorAgent`:

- `StageGeneratedClips(clipIds?)` (a `SandboxAuthoring` tool, `ReactRemotionSandboxTools.Staging.cs`) is
  run-wide: it reads the manifest of EVERY Completed `VideoGenerate` step of THIS execution (an earlier
  step's clips are no longer hidden when a later step delivered none), and copies each delivered clip
  whose key sits under this project's own `agentFiles/generated/` root into the sandbox at
  `public/generated/{clipId}.mp4`, the folder `staticFile()` reads. It reports the `staticFile()` path
  `generated/{clipId}.mp4` (clip ids are allowlisted to the `v{n}`/`v{n}t{k}` namespace before a path is
  built from them). `clipIds` stages only those clips; omitted stages all. When two steps made a clip
  with the same id the later step's keeps `generated/{clipId}.mp4` and the earlier one is staged as
  `generated/s{stepOrder}-{clipId}.mp4`. It does not reach other runs (a file from an earlier run is
  `StageProjectFile`, after `SaveOutputAsFile` for a run output). A success is
  `{ok: true, staged: [{clipId, step, path, purpose, prompt, durationSeconds}], failed: [{clipId, step?, reason}]}`.
  **It never returns a silent empty list**: when nothing stages it answers `{ok: false, error, staged: [], failed}`
  where `error` names the steps searched, how many clips each held and how many were failed, skipped or
  pruned, any requested `clipIds` that had no deliverable clip, and what to do instead (`StageProjectFile`).
  The `AuthorAgent` prompt (identical in `AuthorAgent.DefaultPrompt` and `DatabaseSeeder.BuiltInAgents`) tells
  it to use the clips only as full-bleed backgrounds behind a scene's own content. No screen-content guard (refusing the tool while the render is pinned as an insert) was added:
  the template carries no reliable marker for it.
- `quick-win-promo-plates` (opt-in) is the starter chain with an `Inline` `VideoGenerate` step
  inserted before the Author: an operator-written prompt (no agent authors it, nothing
  project-derived leaves the host), one take, at most two clips, `maxSpendCredits: 3.0`.

## Self-hosted ComfyUI

`InferenceProviderKind.ComfyUI` (appended after `Higgsfield`) targets a ComfyUI server you run —
the optional `comfyui` compose service under the `gpu` profile, or any reachable instance — through
`ComfyUiVideoGenerationClient`, plain `HttpClient` + `System.Text.Json`, no new NuGet package. It
serves `VideoGeneration` only (the controller returns 400 for any other pairing) and, being
self-hosted, is exempt from the private-address endpoint guard the cloud kinds keep, so
`http://comfyui:8188` is a valid endpoint.

### Only vetted first-party workflows are ever submitted

ComfyUI executes whatever graph it is handed, and custom nodes execute arbitrary code, so a provider
that accepted a free-form graph would be remote code execution on the GPU host for every
authenticated user. The "model" on a ComfyUI row is therefore an exact template NAME from a closed
enum (`ComfyUiWorkflowTemplate`), the graph is built here from literals plus typed slots
(`ComfyUiWorkflowTemplates.Build`), and the caller-authored prompt lands in exactly one place: a
`CLIPTextEncode.text` string input. A path, a URL, serialized JSON, a near-miss or differently-cased
name is refused before any request leaves the process (`ComfyUiWorkflowTemplatesTests`,
`ComfyUiVideoGenerationClientTests`).

| Template name (`Model`) | Graph | Resolutions | Ratios | Duration |
|---|---|---|---|---|
| `wan2.2-ti2v-5b-t2v` | Wan 2.2 TI2V-5B text-to-video (`UNETLoader` → `ModelSamplingSD3` → `KSampler` over a `Wan22ImageToVideoLatent`, `CLIPLoader type=wan`, `VAELoader`, `SaveVideo`) | `480P` (832×480), `720P` (1280×704) | `16:9`, `9:16`, `1:1` | 1..5 s, 24 fps (`length = seconds × 24 + 1`) |
| `wan2.2-ti2v-5b-i2v` | The same graph with a `LoadImage` feeding the latent's `start_image`; the one input image is uploaded first via `POST /upload/image` into the `reelbolt` input subfolder | same | same | same |

The weights the templates name — `wan2.2_ti2v_5B_fp16.safetensors`, `umt5_xxl_fp8_e4m3fn_scaled.safetensors`,
`wan2.2_vae.safetensors` — are the Comfy-Org repackaging of Wan 2.2, released under the
**Apache-2.0** licence (`Wan-AI/Wan2.2` on Hugging Face and GitHub; checked 2026-10-04). **LTX-2 is
deliberately not offered.** Its "LTX-2 Open Weights License 0.X" (Lightricks, licence date
2026-01-05, applicable to every LTX-2 release since 2025-10-23) grants free commercial use only to
an Entity whose aggregated annual revenue is below US$10,000,000 and requires a separately
negotiated Commercial Use Agreement above that, so shipping it as a default would put a revenue
condition on every deployment. It can be added later as a further template once that condition
is documented in the admin form.

### Image input: a first frame only

The image-to-video template fills exactly one image node. The request's `Images[0]` is uploaded to
the server's own `POST /upload/image` (multipart `image`, `overwrite=true`, `type=input`,
`subfolder=reelbolt`), the returned `name`/`subfolder` is what the graph's `LoadImage` references,
and that node feeds `Wan22ImageToVideoLatent.start_image`. Nothing else in either graph can hold a
frame.

Two refusals, both before any request leaves the process — the graph decides them, not a caller's
intent, and a frame is never silently dropped:

- an image on `wan2.2-ti2v-5b-t2v` is refused with the reason that graph carries no image node;
- a **last frame** is refused on `wan2.2-ti2v-5b-i2v` too. Wan 2.2 TI2V-5B's latent node documents a
  start image only, and inventing a last-frame slot would be exactly the unverified wire fact this
  closed template set exists to prevent — so `Images[1]` is a refusal naming that, never a second
  upload the graph would ignore. A shot that needs a first *and* a last frame belongs on a provider
  that documents both: MiniMax, or one of the mapped Higgsfield image-to-video endpoints.

`ComfyUiWorkflowTemplates.ImageInputSupport` answers the same question offline from the template enum
that builds the graph, so a caller can refuse before the budget gate reserves any GPU time.

### ComfyUI facts this feature relies on

Checked against the ComfyUI repository's server code and its published API documentation on
2026-10-04:

| Fact | Value |
|---|---|
| Submit | `POST /prompt` with `{"prompt": <API-format graph>, "client_id": "reelbolt-workflow-engine", "prompt_id": <uuid>}` → `{"prompt_id", "number", "node_errors"}`; a bad graph is `400` with `{"error": {"type", "message", "details", "extra_info"}, "node_errors": {...}}` |
| Prompt id | a caller-supplied `prompt_id` must be a UUID (`invalid_prompt_id` otherwise); ReelBolt passes the take's ledger row id, so the server files the history under the key ReelBolt already persisted, and a retry polls rather than re-queues |
| Poll | `GET /history/{prompt_id}` → `{}` while queued or running (no per-prompt percent without the websocket, so `Running` is the honest state); once done, `{"<id>": {"outputs": {"<node>": {"images"|"videos"|"gifs": [{"filename", "subfolder", "type"}]}}, "status": {"status_str": "success"|"error", "completed", "messages"}}}` |
| Download | `GET /view?filename=&subfolder=&type=`; the server rejects a `/`-prefixed or `..` component, and so does the client before building the URL. Only a clearly video-typed output (`.mp4`/`.webm`/`.mov`/`.mkv`) is a clip — a PNG preview is `Failed`, not delivered |
| Upload | `POST /upload/image` multipart (`image`, `overwrite`, `type=input`, `subfolder=reelbolt`) → `{"name", "subfolder", "type"}`; `LoadImage` addresses it as `<subfolder>/<name>` |
| Input images | one start image, on the i2v template only (`POST /upload/image` → `LoadImage` → `Wan22ImageToVideoLatent.start_image`); neither template documents a last-frame node |
| Probe | `GET /system_stats` → `{"system": {"comfyui_version", ...}, "devices": [...]}`, then `GET /object_info/{class}` for every node class the templates instantiate |
| Auth | none in stock ComfyUI; the row's key, when non-blank, is sent as `Authorization: Bearer` for a reverse proxy, and a blank key sends no header |
| Price | **none** — `Describe` returns GPU-SECONDS per output second (`480P` → 20, `720P` → 45), a planning figure, not a vendor price |

**None of these shapes is verified against a live server.** No ComfyUI instance exists in this
deployment's CI, so every test is a recording `HttpMessageHandler`, exactly as for the two paid
vendors.

### Budgeting GPU time

A ComfyUI provider bills nothing, so a credit cap is meaningless for it. The step budgets it in
**GPU minutes** instead — `MaxGpuMinutes` on the step, `VideoGeneration:DailyGpuMinutes` (default
`120`) per project and `VideoGeneration:GlobalDailyGpuMinutes` (default `240`) across projects —
through the SAME atomic `VideoGenerationBudgetGate`: the request carries
`VideoGenerationCostUnit.GpuSeconds`, the gate compares in seconds (`minutes × 60`), filters
today's ledger by `external_generation_jobs.cost_unit` (`"gpuseconds"` vs the column default
`"credits"`), and writes each new row with its unit. Credit spend never counts against the GPU
caps and vice versa. Denials use
GPU-second vocabulary (`step estimate 60 GPU-seconds exceeds step limit 6 GPU-seconds`), the step
reports `meta.costUnit: "gpuSeconds"`, and `ValidateForKind` rejects a ComfyUI step with no
`MaxGpuMinutes` as `CONFIG_INVALID` before anything is reserved.

### The conform pass

Open models deliver 16/24 fps clips at 480P/720P; a program is typically 30/60 fps at 1080P.
`ConformFps`/`ConformHeight` make `GeneratedClipNormalizer` conform a delivered clip during the
normalization it already performs: a lower-rate clip is frame-interpolated with ffmpeg's
`minterpolate` (`mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1`, motion-compensated — honestly
**not** RIFE, and no new dependency), a smaller clip is upscaled with `scale=-2:H:flags=lanczos`.
Both are strict no-ops on conforming input: a clip already at or above the target gets the exact
pre-phase-5 filter chain byte for byte (`GeneratedClipConformTests`), and neither ever downsamples
or downscales.

### Set up a ComfyUI provider

| Field | Value |
|---|---|
| Kind | `ComfyUI` |
| Capability | `VideoGeneration` |
| Endpoint | the server root, e.g. `http://comfyui:8188` (the compose service) or `http://host.docker.internal:8188` |
| Model | `wan2.2-ti2v-5b-t2v` or `wan2.2-ti2v-5b-i2v` — nothing else is accepted |
| API key | blank, or a bearer token for a reverse proxy in front of the server |

The compose service: `docker compose --profile gpu up -d comfyui`, with `COMFYUI_IMAGE` and
`COMFYUI_PORT` in `.env`. **The image is operator-supplied.** ComfyUI publishes no official
container image that this repository has verified; the community images found by web search (for
example `ghcr.io/ai-dock/comfyui`) have not been pulled or run here, so none is baked in as a
default that would silently pull a stranger's image — the default tag is a local one that fails
loudly at `up`. The container must serve the HTTP API on 8188 and hold the three Wan 2.2 weight
files above under its `models/` directory. The service declares the NVIDIA device reservation
because the upstream Wan 2.2 recipe is CUDA; a ROCm host replaces that block with `/dev/kfd` +
`/dev/dri` mounts, for the reason the `decision` service's comment spells out.

**Test a ComfyUI provider without spending GPU time:** `POST /api/v1/inference-providers/{id}/models/{modelId}/capabilities/{capId}/test`
reads `/system_stats` and then `/object_info/{class}` for every node class the templates need,
bounded by 15 seconds, and never queues a prompt. `ok: true` requires a `system` object in the stats
answer AND every class present; a missing class is named in the reason, so a wrong image fails
here rather than mid-run after GPU time was reserved. `401`/`403` is `authRejected`.

## fal.ai facts this feature relies on

fal.ai is the third paid vendor here and the first that serves **image-to-video** as well as
text-to-video: one queue contract over many hosted endpoints, and the endpoint is chosen per take by the
id in the step's `Model`. Every endpoint was transcribed from its own OpenAPI schema, and what the
vendor documents beside what was measured is in [fal-ai-models.md](fal-ai-models.md) section 10. These are
the facts the CLIENT depends on.

- **The submit/poll split is what makes a fal take crash-resumable.** `FalAiVideoGenerationClient` uses
  `FalQueueClient`'s three halves (`SubmitAsync` / `ReadStatusAsync` / `FetchResultAsync`) rather than its
  single submit-and-poll entry point, because a fal video job runs for minutes while the single entry
  point's poll budget is three. The take's ledger row carries the vendor's request id, so a retry polls
  the same job instead of buying a second clip. The image and analysis arms keep the single entry point
  and its 3-minute budget, unchanged.
- **The handle carries the vendor's tracking URLs, not just its request id**, because they are
  **app**-scoped and cannot be rebuilt: fal's own queue page shows an endpoint-shaped `status_url`, and
  that URL answers HTTP 405 (measured without a credential, 2026-10-16); the live route is
  `https://queue.fal.run/{owner}/{app}/requests/{id}/status`. So `RemoteJobId` is
  `{requestId}|{statusUrl}|{responseUrl}` for this kind, and a poll after a restart issues GETs only.
- **The connection test cannot bill.** The empty-body probe the other fal arms use is unsafe here:
  `fal-ai/kling-video/v3/turbo/pro/text-to-video` declares `required: []`, so `{}` is a valid 5 s request
  and a real charge. This arm submits a body that is not JSON at all
  (`FalQueueClient.UnparseableProbeBody`), which no endpoint's schema can satisfy, so nothing can
  dispatch or generate. An uncatalogued model id fails the test without a request, because the client
  would refuse to use it anyway.
- **A frame travels as a base64 data URI.** `VideoGenerationRequest.Images` carries bytes and this
  service has no upload path, so the first frame goes in the endpoint's own first-frame field
  (`image_url` or `start_image_url`, per endpoint) and, where the endpoint documents one, the
  second image goes in `end_image_url`. fal documents the data-URI form platform-wide while warning
  that a URL is the universal one; per-endpoint acceptance is on the fact sheet's must-measure list.
  The executor now produces those bytes: a planned `firstFrame`/`lastFrame` is resolved against the
  analysis artifact and cut through the step's media session, and the client transports it as the
  per-endpoint data URI above. The path is exercised end to end by the plan-sourced executor tests as
  well as this client's own tests.
- **`native` is the platform's token for an axis the vendor does not have.** Kling v3 Turbo Pro has
  no resolution field at all (one flat price per second), and Vidu Q4, MiniMax H3 Max Turbo and Grok
  Imagine Lite have no aspect-ratio field (the canvas follows the input frame). Those endpoints
  therefore offer exactly one resolution token and one ratio token, `native`, and the client sends
  NOTHING for that axis. A step that asks for `1080p` or `16:9` on such an endpoint is refused at
  save/validate time rather than silently given whatever the vendor chose.
- **The prices are the post-promotion figures, and two endpoints have a surcharge the capability cannot
  express.** Vidu Q4 is 30% off until 2026-11-30 and MiniMax H3 Max Turbo was 40% off to 2026-10-15; the
  rate AFTER each promotion is the price, so a take is never under-charged. Grok Imagine Lite adds $0.01
  per input image and `fal-ai/veo3.1*` bills an audio tier that `generate_audio: true` (never
  forwarded) puts every take in; both are named in the price's notes. The budget gate's estimate is
  per take and per resolution, from the same table `Describe` returns, and the metered seconds are
  priced per resolution too: each resolution is a price `variant` on the capability.
- **Retention is 30 days and the platform does not opt out silently.** fal stores request inputs and
  outputs for 30 days unless `X-Fal-Store-IO: 0` is sent; this client does not send it, so what the
  vendor keeps about a prompt and a frame stays the operator's decision. See
  [fal-ai-models.md](fal-ai-models.md) section 10.

### Set up a fal.ai provider

| Field | Value |
|---|---|
| Kind | `FalAi` |
| Capability | `VideoGeneration` |
| Endpoint | `https://queue.fal.run` — the vendor host; another base URL may be registered |
| Model | an endpoint id from the table below, or blank for the default image-to-video endpoint |
| API key | the fal key. Sent as `Authorization: Key <key>` (not a bearer token) |

Then point a `VideoGenerate` step at the same id in its `Model` field: the row's model is only
the connection test's target, and a take's endpoint is the step's. The step's `Resolution` and
`Ratio` must be values that endpoint offers (below; the fact sheet transcribes every schema field),
`DurationSeconds` must be inside its range, and `MaxSpendCredits` is required as for every priced kind.
The schema link is each endpoint's own OpenAPI document.

| Endpoint id | Direction | Role | Resolution / ratio tokens | Duration | USD/s at the default resolution | Schema |
|---|---|---|---|---|---|---|
| `fal-ai/vidu/q4/image-to-video` | image-to-video | **default** | `540p` `720p` `1080p` `2K` `4K` / `native` (no ratio field) | 3–16 s, JSON integer | 0.095 (720p) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/vidu/q4/image-to-video) |
| `minimax/h3-max-turbo/image-to-video` | image-to-video | alternative | `480P` `768P` `1080P` / `native` (no ratio field) | 1–15 s, JSON integer | 0.04 (768P) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=minimax/h3-max-turbo/image-to-video) |
| `fal-ai/kling-video/v3/turbo/pro/image-to-video` | image-to-video | alternative | `native` (flat; no resolution field) / `native` (no ratio field) | 3–15 s, JSON string `"5"` | 0.14 (flat) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/kling-video/v3/turbo/pro/image-to-video) |
| `alibaba/wan-3.0/image-to-video` | image-to-video | alternative | `480p` `720p` `1080p` / `adaptive` `16:9` `4:3` `1:1` `3:4` `9:16` | 2–30 s, JSON integer | 0.20 (1080p) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=alibaba/wan-3.0/image-to-video) |
| `blackforestlabs/flux-3/first-last-frame-to-video` | image-to-video | alternative (both frames required) | `720p` `1080p` / `auto` `21:9` `2:1` `16:9` `4:3` `1:1` `3:4` `9:16` | 5–20 s, JSON integer | 0.17 (720p) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=blackforestlabs/flux-3/first-last-frame-to-video) |
| `fal-ai/veo3.1/fast/image-to-video` | image-to-video | alternative | `720p` `1080p` `4k` / `auto` `16:9` `9:16` | `4s` `6s` `8s`, JSON string | 0.15 (720p, with audio) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/veo3.1/fast/image-to-video) |
| `xai/grok-imagine-video/v1.5/lite/image-to-video` | image-to-video | alternative | `480p` `720p` `1080p` / `native` (no ratio field) | 1–15 s, JSON integer | 0.03 (720p) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=xai/grok-imagine-video/v1.5/lite/image-to-video) |
| `fal-ai/kling-video/v3/turbo/pro/text-to-video` | text-to-video | **default** | `native` (flat; no resolution field) / `16:9` `9:16` `1:1` | 3–15 s, JSON string `"5"` | 0.14 (flat) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/kling-video/v3/turbo/pro/text-to-video) |
| `minimax/h3-max-turbo/text-to-video` | text-to-video | alternative | `480P` `768P` `1080P` / `21:9` `16:9` `4:3` `1:1` `3:4` `9:16` | 1–15 s, JSON integer | 0.04 (768P) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=minimax/h3-max-turbo/text-to-video) |
| `alibaba/wan-3.0/text-to-video` | text-to-video | alternative | `480p` `720p` `1080p` / `adaptive` `16:9` `4:3` `1:1` `3:4` `9:16` | 2–30 s, JSON integer | 0.20 (1080p) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=alibaba/wan-3.0/text-to-video) |
| `fal-ai/veo3.1` | text-to-video | alternative | `720p` `1080p` `4k` / `16:9` `9:16` | `4s` `6s` `8s`, JSON string | 0.40 (720p, with audio) | [OpenAPI](https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/veo3.1) |

**Test a fal.ai provider without spending:** `POST /api/v1/inference-providers/{id}/models/{modelId}/capabilities/{capId}/test` submits the
row's model id with a body that is deliberately not JSON (see above), bounded by the shared 20-second
follow, and never a valid request. `ok: true` requires the queue answer to prove the endpoint is
routed and the key was accepted — a `404` means the id is not an endpoint fal routes, `401`/`403` (or
an authorization-shaped body) is `authRejected`, and a 2xx is followed to its result so a job that
actually ran fails closed. A model id outside the catalogue fails the test without a request.
## Accepted risks

- **No vendor idempotency key is sent to MiniMax.** MiniMax's create endpoint documents none, so
  exactly-once submission is not achievable client-side there. If a submit reaches MiniMax but its
  task id is never recorded (`SUBMIT_OUTCOME_UNKNOWN`, or a crash mid-submit), the take is charged
  at its estimate and never re-submitted, but ReelBolt cannot track or deliver that clip. The operator
  reconciles through the console (see [Handling an orphaned reservation](#handling-an-orphaned-reservation)).
  Higgsfield and ComfyUI both accept an idempotency token and ReelBolt now sends the take's ledger
  row id as it (the `Idempotency-Key` header, the `prompt_id` respectively), so that gap is closed
  for those two kinds — but neither wire shape is verified against a live call.
- **A step cancelled while its price is being fetched records `UNEXPECTED_ERROR`.** See
  [Cancelling a step while the oracle is pricing](#cancelling-a-step-while-the-oracle-is-pricing).
  Nothing is charged, and the execution-level cancellation registry stays authoritative for what
  actually happened.
- **False orphans err on the budget side.** A take reserved but not yet submitted when the
  process died (for example while waiting for a provider slot) looks the same as one whose submit
  reached MiniMax. On Resume or Retry the orphan is `Abandoned` and the take is submitted again under
  a fresh key: the user is not charged for the abandoned row (see
  [Attempts and Resume](#attempts-and-resume)), but it keeps counting toward the project and global
  budget caps, since the vendor may have been paid. A take orphaned again after
  `VideoGeneration:MaxAbandonsPerTake` abandons ends `RESERVATION_ORPHANED`.
- **A cache hit validates only the first take.** See [Step cache](#step-cache).

## Change requests (re-take one clip)

A finished VideoGenerate step can be redone for some of its clips only ([`change-requests.md`](change-requests.md)):
`itemIds` / overrides name a shot (`v2`) or a take (`v2t1`) and give a new `prompt` and/or `retake: true`. The clips not
named are carried over verbatim from the previous clips manifest (no ledger row, no reservation, no submit); the named
ones go through the same moderation, sanitiser, step cap and ledger as any run, with the budget gate seeing only them.
An unchanged prompt without `retake` is the content-addressed reuse and costs nothing; a retake is hashed with the
request id and always buys a new take. The output manifest keeps every clip in its original order, so `VideoCompile`
and the dailies room are unaffected. The credit hold for that step is the full-step hold times the share of clips
re-made.

## Step cache

`StepCachePolicy` treats `VideoGenerate` as cacheable. The cache key includes
`VideoGenerateConfigJson`, so the prompt, model, resolution, duration, ratio, takes and provider
id (when set) are all part of it. A repeated execution with identical inputs replays the prior `Completed`
result, including a partial one, instead of paying again. A `Failed` result is never cached.

A plan-sourced step's key also covers the planner's output, transitively: the key is taken over the
fully resolved step input, which for a deterministic step type is the preceding step's accumulated
output. A plan that changed is therefore a cache miss, and an unchanged one replays without paying
twice.

Before serving a hit, the cache HEADs the result's `OutputStorageKey`, which is only the first
delivered take — on the plan path that means only `clips[0]`'s object. Objects for other clips are
not checked, so a hit can reference a deleted clip object.

Setting the step's `CacheMode` to `Never` only skips the step cache: identical clips are still reused from the spend ledger (next section). To force fresh, paid takes, run with **Regenerate** (`regenerateVideoClips: true`).

### Clip reuse on re-run (ledger-level)

The step cache only hits when the whole resolved input is byte-identical, so a ReviewLoop
loop-back (the upstream output changed) or a retry that follows a partial failure would still pay
again. Independently of the cache, `VideoGenerateStepExecutor` therefore reuses clips at the
**ledger** level. Before the budget gate is called, each take's `request_hash` (model, resolution,
ratio, duration, take index, sanitized prompt) is looked up in `external_generation_jobs` for the
same project and provider: a `Succeeded` row with a `StorageKey` whose object still exists (HEAD via
`ArtifactExistsAsync`) is served as the clip, preferring this execution's own rows (loop-back, and
retry, which re-queues the same execution) and then the most recent earlier execution's (a re-run).

- **Reused takes reserve and submit nothing.** They are removed from the request handed to
  `IVideoGenerationBudgetGate.ReserveAsync`, write no ledger row, count against no cap, and show as
  `reused: true`, `cost: 0`, `costBasis: released` in the manifest; `meta.clipsReused` and
  `meta.reusedSavedUsd` summarise them. A fully-reused step never calls the gate. Takes that miss
  (different prompt, no success, object deleted, lookup error) buy as before, through the same gate.
- **Default is reuse.** Two things force fresh generation:
  1. The per-execution flag `regenerateVideoClips` (default `false`) on `WorkflowExecutionRequested`,
     set from the `execute` and `retry` request bodies (`{ "regenerateVideoClips": true }`). It is
     held in memory (`ExecutionRunFlags`) for the life of the execution, so a hard process kill
     degrades to reuse, never to an unrequested spend. The assistant execute path never sets it.
  2. The step config's `loopBackBehavior` (`Reuse` default | `Regenerate`): `Regenerate` pays for
     fresh takes on every ReviewLoop iteration after the first.
- **UI.** Run and Retry on a workflow that has a `VideoGenerate` step open a modal offering "reuse
  previous clips" (default) or "Regenerate (costs money)", showing the sum of the video steps' Max
  Spend caps as an upper bound (not a quote). The step form exposes `loopBackBehavior`.

## Storage layout and normalization

Each delivered take is uploaded as a bare artifact (no `project_files` row) under:

```text
projects/{projectId}/agentFiles/generated/{executionId}/step-{stepOrder}-v{n}.mp4
```

`n` is the take number on the `Inline` path (`clip-n`). On the plan path it is the ordinal of the
clip within the step's dispatched list — `1` for the first clip the step actually bought — which is
not necessarily the clip's own `v{n}` id. The manifest is what maps a clip id to its object. The
same key is written to the row's `storage_key`.

Before upload, `GeneratedClipNormalizer` validates the download with ffprobe (it must contain a
video stream) and re-encodes it to the same canonical settings `VideoCompile` uses: libx264,
CRF 20, `veryfast`, `yuv420p`, constant frame rate at the probed rate (30 fps if unknown), and
AAC audio if the clip has audio. The probe uses `VideoEditing:AnalyzeTimeoutSeconds` and the
encode `VideoEditing:CompileTimeoutSeconds`. A failure here ends the take `DELIVERY_FAILED`. With
`ConformFps`/`ConformHeight` set, the same encode also conforms the clip's rate and size — see
[The conform pass](#the-conform-pass).

## User daily video limit

The daily limit on generated video is the **user's own setting**, not a platform number (design:
[`docs/design/video-spend-limit.md`](design/video-spend-limit.md)). Each person sets it under **Settings > Usage > Daily
video limit**: a limit in credits per UTC day and an optional alert level. Empty = no limit; only the credit balance and
each step's `MaxSpendCredits` apply. Per user, not per organization (credits stay the organization's ledger, and the
organization's plan budget still caps it). Stored in `user_video_spend_limits` (Inference API owned; the engine reads it).

- API: `GET`/`PUT /api/v1/video-spend-limit` (`{dailyLimitCredits, alertThresholdCredits}`, both nullable; a negative number
  or an alert above the limit is `400 {error}`); the answer adds `usedTodayCredits`, `remainingTodayCredits`, `alertReached`,
  `limitReached` and `resetsAt`.
- Counts the owner's video rows of the UTC day only (audio and enhance excluded, as everywhere). GPU-minutes are not part of it.
- The estimate (`GET`/`POST .../estimate`) carries `videoSpendLimit` for a workflow that generates video;
  `GetWorkflowExecution.videoBudget` names the user's limit (`limitedBy: "user"`), used and left.
- The alert is shown in the app (settings card, run page); no email or notification is sent.

## Configuration

WorkflowEngine `appsettings.json`, section `VideoGeneration` (`VideoGenerationOptions`, bound by
`AddVideoGeneration`). Override with environment variables such as
`VideoGeneration__DailyBudgetCredits`.

| Key | Default | Description |
|---|---|---|
| `VideoGeneration:DailyBudgetCredits` | unset | Optional per-project daily cap in **credits**, for a self-host operator who wants one. Unset by default: no per-project figure ships; the daily limit is each user's own setting. ≤ 0 denies every reservation. |
| `VideoGeneration:GlobalDailyBudgetCredits` | `833333.33…` | **Operator safety ceiling** across all tenants and projects per UTC day, in **credits** (the equivalent of $5000, replacing the old $50 cap): runaway-spend protection, never presented to users as their limit. ≤ 0 denies every reservation. Set the same value on the Inference API so the app agrees. |
| `VideoGeneration:DailyGpuMinutes` | `120.0` | Per-project daily GPU-time cap for `ComfyUI` providers, in minutes. ≤ 0 denies every GPU reservation. Credit spend does not count against it. |
| `VideoGeneration:GlobalDailyGpuMinutes` | `240.0` | Daily GPU-time cap across all projects, in minutes. ≤ 0 denies every GPU reservation. |
| `VideoGeneration:MaxConcurrentPerProvider` | `2` | Takes holding a slot per provider at once (per engine process, shared across runs); values below 1 act as 1. |

`WorkflowHardening:MaxStepRetries` (default 3) sets how many attempts a failed `VideoGenerate`
step gets.

## Workflow templates

`WorkflowTemplateCatalog` ships three templates for this feature, all `AutoCreateOnProject: false`
(opt-in). `video-derush-edit-broll` derives a source video, decides the edit, plans generated
b-roll, buys it, and places it in the same encode:

| # | Step | Agent | Notes |
|---|---|---|---|
| 1 | `VideoAnalyze` | `VideoTransform` | Source: `ProjectFile`. The bounded view this emits offers the `s{n}`/`g{n}`/`t{n}` ids. |
| 2 | `Agent` | `VideoStoryEditor` | Decides which spans to keep. |
| 3 | `Agent` | `ShotDirector` | `AgentInputContextMode.FullWorkflow`, so it sees step 1's offered ids and step 2's decision. |
| 4 | `VideoGenerate` | `VideoTransform` | `planSource: "StepRef"`. |
| 5 | `VideoCompile` | `VideoTransform` | `enableGeneratedClips: true`. |
| 6 | `ReviewLoop` | `VideoReviewAgent` | Loops back to step 2, `MinScore: 8`, `MaxIterations: 3`. |

The generate step's config:

```json
{
  "planSource": "StepRef",
  "plan": { "from": "Step", "stepOrder": 3 },
  "analysis": { "from": "Step", "stepOrder": 1 },
  "model": "MiniMax-H3",
  "resolution": "768P",
  "durationSeconds": 6,
  "ratio": "16:9",
  "takes": 1,
  "maxClips": 4,
  "maxSpendCredits": 5.0,
  "pollIntervalSeconds": 10,
  "timeoutSeconds": 1800
}
```

The `VideoStoryEditor` (step 2) and `ShotDirector` (step 3) steps both opt out with
`CacheMode: Never`, since re-asking them is the point of a re-run. The generate step is left at the
default, so an identical re-run does not pay twice.

Every step reference in the template is an explicit `StepOrder`, never `Previous`. Two of them
would resolve to the wrong step if they were not:

- On the compile step, `Previous` would resolve to step 4 — the `VideoGenerate` step's own output —
  rather than step 2, the story editor's decision.
- On the generate step, `Previous` would resolve to step 3. `plan` would survive that by accident,
  since step 3's output is the plan; `analysis` would not, because the keyframe ids a planned shot
  names have to resolve against step 1's `VideoAnalyze` artifact.

The compile step names `decision: {from: "Step", stepOrder: 2}`, `analysisStepOrder: 1`,
`generatedClips: {from: "Step", stepOrder: 4}` and `maxGeneratedClips: 4`. It leaves
`transitionPolicy` at `Auto`; a `SeamBridge` shot the planner produces under this template is spliced
before its anchor when that anchor survives the cut (`anchor_not_in_output` otherwise), capped at the
default `maxSeamBridgeMs` of 3000 ms — see [Seam bridges](#seam-bridges).

`video-derush-edit-dailies` extends it with dailies (see [Dailies](#dailies-choosing-between-takes)):

| # | Step | Agent | Notes |
|---|---|---|---|
| 1 | `VideoAnalyze` | `VideoTransform` | Source: `ProjectFile`. |
| 2 | `Agent` | `VideoStoryEditor` | `CacheMode: Never`. |
| 3 | `Agent` | `ShotDirector` | `CacheMode: Never`. |
| 4 | `VideoGenerate` | `VideoTransform` | `planSource: StepRef`, `plan: Step 3`, `analysis: Step 1`, **`takes: 2`**, `maxClips: 4`, `maxSpendCredits: 8.0`, `lookMatching: true`, `cameraContinuity: true`. |
| 5 | `VideoAnalyze` | `VideoTransform` | `sources: [{kind: GeneratedClips, stepOrder: 4}]`, transcription `Off`, `detectSilence: false` — one analyzed source per delivered take. |
| 6 | `DailiesRoom` | `VideoTransform` | `view: Step 5`, `CacheMode: Never`. |
| 7 | `VideoCompile` | `VideoTransform` | `decision: Step 2`, `analysisStepOrder: 1`, `enableGeneratedClips: true`, `generatedClips: Step 4`, **`takeSelection: Step 6`**, `maxGeneratedClips: 4`. |
| 8 | `ReviewLoop` | `VideoReviewAgent` | Loops back to step 2. |

`quick-win-promo-plates` is the promo starter plus an `Inline` `VideoGenerate` step before the
Author — see [Remotion background plates](#remotion-background-plates).

## Out of scope for phases 3, 4 and 5

Section 7 of issue #93 in the project's private tracker lays out the full phasing. Not
built yet:

- **LTX-2 as a ComfyUI template** — see [Self-hosted ComfyUI](#self-hosted-comfyui) for the
  licence condition that keeps it out of the shipped set.
- **A verified ComfyUI container image.** The compose service exists, under the `gpu` profile and
  off by default, but its image is operator-supplied because no official one could be verified.
- **A plan larger than `MaxClips`.** `MaxClips` is yours to set (1..64), so a plan beyond it is
  trimmed — the excess clips are reported `skipped` with `MAX_CLIPS_EXCEEDED` — rather than fully
  generated. The step cap is checked against the trimmed plan (at most `MaxClips` clips).
- **The `Inline` path is otherwise unchanged.** Apart from three things it now shares with the plan
  path, a phase 1 step behaves exactly as this document originally described: the output
  projection, the per-clip manifest written to `ArtifactStorageKey`, and `meta.clipsSkipped` — all
  three of them new for an `Inline` step in this wave.
- **MiniMax `callback_url`.** The step polls; it registers no callback.
