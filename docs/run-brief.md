# The run brief

The run brief is the typed set of choices a person (or the assistant) makes when starting a workflow run: the
language of the film, the language of the footage, how long it should be, its shape and resolution, its editing
style and tone, which brand it is built in, whether it is narrated and by whom, and which project files are the
run's media. It replaces "write it into the request text and hope the agents notice" with fields every surface
shares. A run that sends no brief behaves exactly as it did before briefs existed.

## What the brief contains

Every field is optional; an absent field means "no preference", the same way an unset editing-style axis does.
Property names are camelCase and enums are written by name, append-only.

| Field | Values | Where the run keeps it |
|---|---|---|
| `language` | A language tag such as `it`, `en`, `es-MX`, `zh-Hant-TW`. Any tag of that shape is accepted; the region is kept for voices. The language of everything the viewer reads or hears. | `workflow_executions.run_brief_json` |
| `sourceLanguage` | Same shape. The spoken language of the source footage, for transcription. Separate from `language` so a dub can be expressed. | `run_brief_json` |
| `lengthSec` | Whole seconds, 5 to 600. A target, not a guarantee. | `run_brief_json` |
| `format` | `Original`, `Vertical`, `Square`, `Landscape`, `Portrait` (or `9:16`, `1:1`, `16:9`, `4:5`). | the existing `output_format_override` |
| `resolution` | `Auto`, `720p`, `1080p`, `2160p` (or `4K`). Checked against the plan when the run is queued. | the existing `output_resolution` |
| `stylePreset`, `styleId`, `style` | At most one: a built-in preset key, a saved style id, or a one-off style. Same rules and same errors as the editing-style run choice. | the existing `style_config_json` |
| `tone` | `Calm`, `Upbeat`, `Energetic`, `Serious`, `Playful`, `Inspirational`. | folded into the run's style snapshot (`StyleConfig.Tone`) |
| `designSystem` | `project` (default: pin the project's approved design) or `none` (run without brand tokens). Anything else is refused in this version. | `run_brief_json` (only `none` is stored); `none` also means no design-system pin is written |
| `voice` | `{ "mode": "none" or "narrated", "projectVoiceId": id, "libraryVoiceId": id }`; the mode is read case-insensitively and echoed as `None` or `Narrated`. A voice id needs `mode` `narrated`, and only one id may be set. | `run_brief_json` |
| `sourceFiles` | `[ { "role": "video" or "code" or "music" or "sfx" or "image" or "document" or "insertContent" or "overlay", "fileIds": [ids] } ]`. Each role at most once, every file must belong to the run's project and must not be quarantined. | `run_brief_json` |
| `overlayCardAudio` | `agent` (default: the picture planner chooses per card), `keep` (the film's sound continues under a full-screen picture) or `mute` (the film's sound is silenced for the card span). Maps to the compile's card-audio default; `agent` leaves it unset. | `run_brief_json` |
| `pace` | `calm`, `medium` or `fast`: how busy the dynamic edit is. Overrides the style's `dynamics`. See [Pace](#pace-choice). | folded into the run's style snapshot (`StyleConfig.Dynamics`) |
| `notes` | Free text, the same thing the run request text carries today. | the existing `user_request` |
| `avatar` | `{ "avatarId": id }`: a project avatar (avatars phase 2). It must be an avatar of the run's project with consent on record, not withdrawn and not expired; a foreign id is "not found". | `run_brief_json` |
| `inputs` | `{ "<inputId>": value }`: the values of the app's typed inputs (docs/design/app-inputs.md). Text, a number, `true`/`false`, or `null` for "left empty"; at most 32 entries, 8 KB each, 32 KB in all. Checked against the workflow's app manifest (see "App inputs" below). | `run_brief_json` (the canonical values, defaults included) |

## Pace choice

`pace` (`calm`, `medium`, `fast`) sets the pace of the dynamic edit for one run and overrides the `dynamics` axis of the run's style. Like `tone`, it is folded into the style snapshot the run stores, so it has no row in the generated field-consumer matrix. The bounds each value sets are in [Dynamic edit and pace](editing-styles.md#dynamic-edit-and-pace).

## Validation rules

One validator (`RunBriefValidator`) serves the REST execute endpoints, the assistant's `ExecuteWorkflow` tool and
therefore MCP, so they cannot disagree. A bad brief is a 400 with a plain message, before the credit preflight, so
it costs nothing and writes nothing.

- Unknown fields and unknown enum names are refused (a typo is an error, never silently ignored).
- A language tag must be shaped like a BCP-47 tag (primary language with optional extended-language subtags, script, region, variants, extensions and a private-use tail), at most 35 characters. The stored form is canonical (`es-MX`, `de-DE-1996`).
- `lengthSec` outside 5 to 600 is refused. At most 50 source files per role and 200 in total. The
  `overlay` role is the exception: at most 4 files, each an image or a video.
- Source file ids must belong to the project being run; a foreign or missing id is refused with "not found", never "forbidden".
- Sending a legacy request member and the same brief field is refused: `styleId`, `stylePresetKey`, `style`, `outputFormat`, `outputResolution` and `userRequest` conflict with `brief.styleId`, `brief.stylePreset`, `brief.style`, `brief.format`, `brief.resolution` and `brief.notes` respectively. Choosing two different ways of picking a style is the existing "choose one style" error.

## App inputs

`brief.inputs` carries the values of an app's own questions (manifest inputs of kind `input`: a prompt, a length, a
shape, a switch, a model). Always on (the `Workflows:AppInputs:Enabled` switch was removed 2026-11-10). The public API run start
accepts the same typed `inputs` as the app's own run page.

At every run start (Execute, Retry, Resume, the workflow-assistant execute, the assistant's `ExecuteWorkflow` in chat and
over MCP, and the approval card that prices it) the API checks the values with `AppInputValueValidator`, after the
authority and stale-profile checks and before the plan, estimate and credit gates:

1. the typed check against the workflow's manifest (`AppInputValueChecker`): unknown ids, wrong types, ranges, choices
   and text caps are `400 APP_INPUT_INVALID`; defaults are filled and the values of hidden (`showWhen`) inputs dropped; a
   required, visible input with no value is `400 APP_INPUT_MISSING` ("Fill in “What happens in the video”.");
2. a `model` value (a model capability id) is re-resolved against the organization's selectable models, the same list as
   `GET /api/v1/inference-providers/selectable` (enabled; the platform's only when fully priced; the organization's own only
   while its plan allows bring-your-own; never another organization's). It must be of the capability the step uses
   (`VideoGeneration` for VideoGenerate, `MusicGeneration`/`SoundEffectGeneration` by an AudioGenerate step's kind,
   `SpeechSynthesis` for Voiceover, `Chat` for an agent input), inside the author's `allowed` list when set, and able to do
   the job the step's own config asks for (a talking head needs a talking-head model). Anything else is
   `400 APP_INPUT_MODEL_UNAVAILABLE` naming the input. A `@model` binding writes the step's `providerId` and `model`; an
   agent input (`agent`) sets that agent's Chat model for every step of the agent in this run, over the workspace's
   per-agent choice and the agent's own model (nothing is written to the agent);
3. a dry-run bind (`AppInputBinder`, the binder the engine runs on the snapshot) onto detached copies of the steps, then
   the save validator and each bound config's own `Validate()`: a value that would make a step invalid is
   `400 APP_INPUT_INVALID` naming the input ("The value of “Takes” does not fit: Step 1 (VideoGenerate): ...").

A refusal body is `{ code, message }`; it costs nothing and writes no run row. On success the canonical values (cleaned
text, typed numbers, defaults) are stored in `run_brief_json.inputs`, and when the manifest has a `requestTemplate` the
run's request text (`user_request`) is the template with each value quoted, then `Notes for this run:` and the notes (a
brief file, when one is named, still comes first). The plan, lint, footage and credit gates then judge the bound steps on
the branch the inputs choose, priced at the chosen models: a VideoGenerate or AudioGenerate step is priced from its bound
settings (so a dearer model raises the estimate and the hold), and an agent's cold-start chat at the model chosen for it. Retry and Resume re-check the stored values against the run's own snapshot (and the
manifest copied onto it); they never recompose the request. The engine binds the same values onto the run's snapshot
(see "Step cache" and docs/design/app-inputs.md 6.3).

`GET /api/v1/projects/{projectId}/workflows/{id}/app/inputs/{inputId}/options` (View) lists the models one `model` input
offers: the selectable models of its capability, narrowed to the author's `allowed` list, recommended first (the author's
`recommended`, else the workspace's per-agent choice or the agent's own model for an agent input, else the capability's
default), each with `costIndex` (a ratio to the recommended option for a reference unit: one 6 s take at the step's
resolution, 30 s of music or sound, 1,000 characters of speech, the step editor's token mix for chat; never money; null
when it cannot be priced) and `available`/`unavailableReason` by the same rules the run gate applies.

`POST /api/v1/projects/{projectId}/workflows/{id}/estimate` takes `{ brief, outputResolution?, videoFileIds? }` and answers
exactly like `GET .../estimate`, for the steps as the brief's inputs bind them and only on the branch they choose (a
missing required input is not a refusal there; a value that cannot be used is the same 400). The resolution and the clips
default to the brief's.

## Precedence

Highest wins, field by field: an explicit brief field; then the free notes (where notes contradict a field, the
field wins); then the style's axes; then the design system's voice and tokens as defaults; then the step
configuration as the workflow author set it; then the agent's default. For a deterministic step, a run field
overrides the step's own configuration for the property it is bound to, never the other way round. This version
stores and transports the brief, and the engine reads it (see "What reads the brief" below).

## Request and response shapes (OpenAPI note)

`POST /api/v1/projects/{projectId}/workflows/{id}/execute` (and the assistant REST twin) accept the existing
`ExecuteWorkflowRequest` plus one appended member:

```json
{
  "userRequest": null,
  "brief": {
    "language": "it",
    "sourceLanguage": "en",
    "lengthSec": 45,
    "format": "Vertical",
    "resolution": "1080p",
    "stylePreset": "fast-social-reel",
    "tone": "Calm",
    "designSystem": "project",
    "voice": { "mode": "narrated", "libraryVoiceId": "00000000-0000-0000-0000-000000000000" },
    "sourceFiles": [
      { "role": "video", "fileIds": ["00000000-0000-0000-0000-000000000000"] },
      { "role": "overlay", "fileIds": ["00000000-0000-0000-0000-000000000001"] }
    ],
    "overlayCardAudio": "keep",
    "notes": "A launch film for the new feature."
  }
}
```

`brief` is nullable and defaults to null; when it is null the request is handled exactly as before. Retry and resume
take no brief: they re-queue the same run and reuse the stored one.

`WorkflowExecutionResponse` gains a nullable `brief` member with the same shape: the stored fields plus the style
(`stylePreset`, `styleId`, or the one-off `style`), `tone`, `format` and `resolution` read back from their own columns.
The run's request text stays in `userRequest` and is not repeated in `brief.notes`. `brief` is null when the run used
none of these.

The assistant's `ExecuteWorkflow` tool takes an optional `brief` argument with the same object. Over MCP it appears in
the tool list like any other argument.

## Where the brief is carried

`run_brief_json` is an engine-owned column (engine migration `AddExecutionRunBrief`) that the API maps with
`ExcludeFromMigrations`. Every place that copies run state copies the brief:

- the queue write and the published `WorkflowExecutionRequested` (`RunBriefJson`, informational; the engine reads the row);
- retry and resume, which re-queue the same row and republish its stored brief;
- the recovery sweeper's re-queue;
- a composition revision, which replays the source run's brief so a revision of an Italian film stays Italian;
- the run list and detail responses, and the account export.

`RunStateCopySiteDriftGuardTests` classifies every property of a run as run-start state or runtime state and checks that
each copy site names the run-start state it carries, so a future run-state field cannot be forgotten.

The column is not part of the read-only analytics grant: it can carry file ids and is withheld like the run's request text.

## Errors and what is not checked yet

- A brief that does not fit the shape (an unknown field, an unknown enum name, a wrong type) is refused. Over REST the model binder answers with the standard `ValidationProblemDetails` 400; the assistant tool and the card answer with a plain `error` sentence. A rule broken inside a well-shaped brief (length range, language tag, conflicts) is a 400 `{ "message": "..." }`.
- Retry and resume reuse the run's stored brief. A retry that sends a `brief` in its body is refused with a 400 rather than silently ignoring it.
- The assistant's approval card lists each brief field that is set in plain words, and prices and plan-checks the brief's resolution through the same splitter the tool uses.
- A composition revision, whether it replays the cut or runs the instruction reviser, carries the source run's stored brief; `designSystem: none` therefore stays unpinned.
- Voice ids are only shape-checked at queue time. The Voiceover step resolves them against the project when it runs (see "What reads the brief"); a foreign or unusable voice fails the step with `VOICE_NOT_FOUND` or `VOICE_CONSENT_MISSING` and never falls back to another voice.

## What reads the brief

The engine enters the run's brief once per execution (`RunBriefState.From`, an async-local `RunBriefAmbient`, beside the style) and two things read it.

### Agents: the `[RUN BRIEF]` block

An agent whose role has a sentence for a set field gets a `[RUN BRIEF]` block appended to its system prompt, in this order: prompt, output contract, `[RUN BRIEF]`, `[STYLE INSTRUCTIONS]`, `[DESIGN SYSTEM]`, `[PRODUCT PROFILE]`. The sentences are one reviewable table (`RunBriefRegistry`), so no free-form brief text is ever model-visible except the run notes. Language is shown as a name from a fixed table plus the validated tag, never the raw string. An agent with no sentence for any set field, and every agent of a run with no brief, gets its prompt unchanged. The table of which agent reads which field (generated from the registry; a test pins it):

<!-- run-brief-agent-matrix:start -->
| Agent | language | sourceLanguage | lengthSec | voice |
|---|---|---|---|---|
| `AuthorAgent` | Writer | - | Writer | - |
| `CompositionReviser` | Writer | - | - | - |
| `DirectorAgent` | Writer | - | Writer | - |
| `GraphicDesigner` | OnScreenText | - | - | - |
| `MotionGraphicsDirector` | OnScreenText | - | - | - |
| `MotionGraphicsPlanner` | OnScreenText | - | - | - |
| `NarrationTranslator` | Translator | Translator | - | - |
| `NarrationWriter` | Writer | - | Writer | Writer |
| `ReviewAgent` | Reviewer | - | Reviewer | - |
| `ScriptwriterAgent` | Writer | - | Writer | Writer |
| `ShotDirector` | - | - | Cutter | - |
| `VideoEditDirector` | OnScreenText | - | Cutter | - |
| `VideoReviewAgent` | Reviewer | - | Reviewer | - |
| `VideoStoryEditor` | OnScreenText | - | Cutter | - |
<!-- run-brief-agent-matrix:end -->

Analysis agents, the pickup planner, deterministic placeholders and custom agents read nothing. Rooms decorate each participant by its agent type; a room's cache key reads only what its built-in participants read (a colour-grade or dailies room reads none). A per-line voice id from upstream still beats the voice the brief named. Tone is not in this table: it is folded into the style.

**One language sentence.** The run's language is, in order: the brief's `language`, the language carried by the run's style, then the pinned design's `voice.locale` (the brand's default, used only when nothing else says). Whichever wins is stated once, in the `[RUN BRIEF]` block; a style language is not repeated in `[STYLE INSTRUCTIONS]` and the design's `Locale:` line is left out of `[DESIGN SYSTEM]`, so an agent never sees two language statements. A language that is only a default (style or brand) is worded as "Default language" under a preamble that does not attribute it to the person.

### Deterministic steps: typed overrides

| Step | Field | Effect |
|---|---|---|
| `Voiceover` | `language` | Fills the step's `Language` when it has none; sent to the speech client as `SpeechRequest.Language`. The shipped clients' APIs take no language, so nothing changes on the wire. A language the provider's published list does not contain adds a `voice_language_unlisted` warning; a provider with no published list passes through silently |
| `Voiceover` | `voice` | `projectVoiceId` or `libraryVoiceId` replaces the step's `ProjectVoiceId` and is resolved by the consent guard within the run's project; voice casting does not replace it. `mode: none` skips the step with a plain reason, never a failure |
| `VideoCompile` | `lengthSec` | Fills `TargetDurationSec` when it is 0; `TargetDurationMode` stays as authored |
| `VideoAnalyze` | `sourceLanguage` | Fills the transcription language hint (primary subtag) when the step has none |
| `VideoAnalyze` | `sourceFiles` | A source of kind `BriefFiles` with a `role` expands at run time to the brief's files for that role, in order. Each id is re-checked against the run's project; a missing, foreign or empty selection fails the step with `INPUT_MISSING` and a plain message |
| `VideoCompile` | `overlayCardAudio` | Sets `MediaOverlayCardAudioDefault` (`keep` or `mute`); `agent` leaves it null so the planner chooses per card |
| `VideoGenerate` | `avatar` | Sets `AvatarId` on an inline step that names no avatar and no first/last frame (`RunBriefOverrides.ApplyToVideoGenerate`). Never touches `AllowSourceMediaEgress`: a step whose permission is off still fails `EGRESS_REFUSED`. No agent sentence reads it in this phase. See [`avatars.md`](avatars.md) |
| `Voiceover` | `avatar` | Phase 3: a step with `voiceFromAvatar` speaks in the voice linked to the brief's avatar, unless the brief names a `voice` itself; no avatar or no linked voice fails `AVATAR_VOICE_MISSING`. See [`avatars.md`](avatars.md#the-still-then-animate-path-phase-3) |

The `overlay` file role is deliberately different from the other `sourceFiles` roles: it writes no
step config. When a `VideoAnalyze` step has `EmitOverlayOffers` on, the brief's `overlay` files
become the pictures and videos offered to the picture planner, in the brief's order (and only
those). With no `overlay` files named, the step offers every usable project picture and video and
the planner chooses freely. Each file must be an image or a video of the run's project; a foreign,
missing or wrong-kind file is refused before the run starts.

An open `ProjectFile` source (no pinned file id, the "no file selected" slot every footage template ships) takes the brief's `video` files the same way: when the brief names video files, that source is replaced by a `BriefFiles` source for the video role (further open slots are dropped so no file is read twice, pinned files and other kinds are untouched); with no video files in the brief the step is exactly as before. The cache key keys those ids for such a step too, and lint `source-media-missing` counts the brief's files for the role. 

`BriefFiles` is read by `VideoAnalyze` only; its cache key is decided from the parsed config (enum names and numbers alike), so the chosen file ids are always keyed; other steps that take a source refuse it. Each override returns the same config instance when nothing applies.

### Step cache

`StepCacheKeyInputs.RunBrief` holds a canonical string of only the brief fields the step's role or type reads, and is appended to the key only when non-empty, so a run with no brief hashes exactly as before and `SchemaVersion` is unchanged. An analyzer is therefore reused across languages. `DeterministicStepRevisions` carries a `run-brief-*` tag for `VideoCompile`, `Voiceover` and `VideoAnalyze`.

App inputs are not part of that component. The engine binds `brief.inputs` onto the run's snapshot (the snapshot's
configs hold the bound values, which the key already hashes), and `StepCacheKeyInputs.AppInputs` adds, per step, only the
values that step consumed (bound into its config or read by its condition; an agent's share is the composed request,
already hashed as `UserRequest`). It is appended only when non-empty, so `SchemaVersion` stays `v10` and re-running an app
with one input changed reuses every cacheable step that did not consume it.

### Reviewers and dubbing

The video reviewer and the review agent are told to report copy in the wrong language and a film more than 15% off the target length. The platform also measures the rendered file itself and, when it is more than 15% off `lengthSec`, caps the review score at 6 so the loop goes back for a fix (see "Length gate" in `docs/video-editing.md`). The dub narration translator reads `language` as the target and `sourceLanguage` as the source through its registry sentences.
