# Change requests on a step of a finished run

A change request redoes one step of a finished run (narration lines, video clips, music takes, the thumbnail) and only what depends on it,
as a **new linked run**. Everything else is copied from the finished run and costs nothing. This page is the
developer reference for what ships; the design and the decisions behind it are in
[`design/change-requests.md`](design/change-requests.md). The film itself keeps its own route,
[composition revisions](composition-revisions.md), which this builds on (the same replay planner, the same hidden
run kind). The run page shows both as one feature, the **Change this video** card (see "One panel for the film and the steps").

## What a request is

A `step_change_requests` row (API-owned; the engine maps it `ExcludeFromMigrations()` and only reads it) naming a
finished run (`source_execution_id`), one target step (`target_step_order`), an optional list of items, an instruction
and typed overrides, plus the plan the replay will follow (`plan_json`). Its work is its own hidden run
(`WorkflowDefinitionKind.CompositionReplay`, the same kind a revision uses), so credits, leases, cancel, SSE, refunds
and the run page come from the ordinary path. The request is created together with its snapshot workflow and queued
exactly like a revision; a queueing failure removes both rows.

## Which steps accept one

One pure registry, `StepChangeCapabilities.For(StepType)` (`ReelBolt.Shared/Workflows/ChangeRequests`), with an
explicit arm for every `StepType`: `Supported` (the kind of item it exposes and the overrides it accepts) or
`NotSupported` with a reason. `Revise` points at the composition-revision route. **Voiceover** (a narration line), **VideoGenerate** (a shot or take), **AudioGenerate** (a take, `a{k}`) and **ThumbnailGenerate** (the whole step) are supported
today. An `Agent` step is decided by the agent it hosts: `StepChangeCapabilities.ForAgent(AgentType)` (explicit arm per `AgentType`, drift-guarded) makes the built-in **`ScriptwriterAgent`** supported (by scene, below); `AuthorAgent`, `CompositionReviser` and the other sandbox-tool agents route to a composition revision, and every other agent type is `NotSupported` with a reason. `For(StepType.Agent)` alone stays `NotSupported`. `ImageGenerate` is `NotSupported` (edit the step and run it again). The drift guard (`StepChangeCapabilitiesDriftTests`) fails the build when a `StepType` has no entry, when a
`NotSupported` entry has no reason, and when a `Supported` type has no executor implementing
`IChangeRequestAwareStepExecutor`. A new step type gets change requests by adding its capability entry and
implementing that one interface.

## AudioGenerate and ThumbnailGenerate

**AudioGenerate.** Items are the takes (`a1`, `a2`, ...; text = the take's prompt, shortened). Overrides per take or `*`:
`prompt` (up to 4000 characters, the step's ordinary prompt validation), `durationSec` (the catalogue range; the
chosen model's own range is checked at run time) and `retake` (flag: `true` = a new version of the same music or sound
effect even when prompt and duration are unchanged: a new ledger row, the request id salts the request hash so the
content-addressed reuse is bypassed for that take only; `false`/absent = today's behaviour). `takes` is not offered. A request naming take(s) re-makes only
those: each is a new ledger row through the same budget gate (`Audio` scope) with a fresh object; every other take is
carried over verbatim from the earlier run's manifest (same file, same ledger row, no submit, no new row), so
`m{n}`/`x{n}` ids and the registered project files of carried takes stay the same. A named take whose prompt and duration
end up unchanged is the existing content-addressed reuse and costs nothing. A request with no item ids, or a `*` override,
re-makes the whole step. The step spend limit counts only the takes actually bought. The manifest is read project-scoped
(`projects/{p}/agentFiles/`), each carried file must lie under `projects/{p}/outputFiles/`; anything else refuses the
request (`CHANGE_NOT_APPLIED`). The output carries `meta.changeRequest` (`redoneTakeIds`, `carriedTakeCount`).

**ThumbnailGenerate.** Whole step only (one item, `thumbnail`). Overrides: `overlayText` (up to 400 characters; the text goes
through `OverlayTextSanitizer` and reaches ffmpeg only through the session text file; with no overlay position set the
position becomes `LowerThird`), `atTimeSec` (switches the selection to `AtTime`), `overlayPosition`
(`None`/`LowerThird`/`UpperThird`/`Center`), `overlayFontColor` (`white`/`black`/`yellow`), `overlayBoxColor`
(`black@0.45`/`black@0.6`/`white@0.4`/`none`). The step re-runs (deterministic ffmpeg, no credits) and publishes a new
object under the new execution; the old thumbnail is untouched. The time must still fit the source (checked at run time).

## Voiceover

Items are the narration lines (`vo{n}`, listed from the step result's `view.lines[]`, which carries each line's spoken
`text`). Overrides, per line or `*` for all lines:

| Key | Meaning |
|-----|---------|
| `text` | New wording (up to 1000 characters; goes through `VoiceoverTextSanitizer`) |
| `voiceId` | A different voice (a project voice goes through the consent guard and fails, never falls back) |
| `speed` | 0.5 to 2.0; sent as Fish `prosody.speed` only when set, so every other request is byte-identical |
| `pronunciation` | Up to 16 `{term, spelling}`: the word written with an accent on the stressed syllable (`Novara` -> `Novàra`). It changes the spoken text of that line only, with the project glossary's rules (`PronunciationGlossaryValidator`) and precedence over the glossary. IPA is not offered: it was measured not to work for Italian on Fish ([`voiceover.md`](voiceover.md)) |

In the engine, `StepChangeRequestContext` finds the request for the run being executed (only for a `CompositionReplay`
run, same project, target step order) and `VoiceoverStepExecutor` applies it: a line not in scope and not touched by a
`*` override is carried over verbatim from the finished run (storage key, duration, words, alignment, fit), with no
object-store probe, no speech call and no alignment call; a line whose previous entry is missing or failed is made again, and
carried voices are re-checked for consent. A line the earlier run shortened to fit keeps the words it said. A line in scope runs
the normal path, so identical audio
is still free through the content-addressed store. A request that matches no line fails `CHANGE_NOT_APPLIED` (a
`UserInput` failure, nothing refunded). The fit solver (see "Fit" in [`voiceover.md`](voiceover.md)) runs over the final
durations; wording the owner wrote is never rewritten by the narration shortener: it is refused before synthesis only
when physically impossible (more than 4.0 words per second), and otherwise it is synthesized and its measured speech
against the window decides. The step's per-step admitted-word ceiling (`Voiceover:MaxAdmittedNarrationWords`) counts
only the lines it synthesizes, so carried-over lines never bring a change request over it. The output gains
`meta.changeRequest {requestId, redoneLineIds[], reusedLineCount}`.

### Pronunciations saved after the run

A Voiceover change carries untouched lines over with the audio they have. That would keep an old pronunciation, so a line the request does not name is made again when the project's pronunciations (the glossary) give it a different spoken form than the run used:

- **How it is judged.** Each `ok` line of the output records `spoken` (what the project glossary made of the line, not a per-line respelling an earlier request asked for) when the glossary respelled it. The change compares that with the line's text under the glossary as it stands now (`NarrationGlossaryStaleness`, shared by the API and the engine). A pronunciation added, edited or removed after the run makes the lines holding the term stale. Terms the step's own `pronunciationOverrides` name are left out, as at run time. When the output's `meta.pronunciation.glossaryDigest` equals the current glossary's, nothing is stale.
- **What it costs.** The API names those lines in the request (`itemIds`) when it is made, so the stored scope, the hold, the exact price and the listed items all count them; a request for line 3 may therefore show lines 1 and 5 as well. Their cost is the lines' own size, like any named line. The engine repeats the check at run time, so the audio is never older than the glossary it reads.
- **Older outputs.** A run made before `spoken` was recorded is judged on its written text, so a line holding a term the glossary now respells is made again once, even if it was already respelled the same way; a removed entry cannot be seen on such a run.
- **Other routes.** A change to the script, which re-makes the narration after it, and a review loop's re-runs speak with the current glossary already (the step-result and per-line caches key on it). A film revision reuses the narration as it is.

## VideoGenerate

"If an AI-generated clip does not come out well, redo that clip (a new prompt or another take), keep the rest exactly as
it was, spend as few credits as possible." Items are read from the step result's `clips[]`: one per shot (`v{n}`) and,
when the step ran with `Takes > 1`, one per take (`v{n}t{k}`; the shot item `v{n}` is derived and stands for all its
takes). Each item carries its status, its duration and the prompt it was made from (shortened). Overrides, per shot or
take, or `*` for all clips:

| Key | Meaning |
|-----|---------|
| `prompt` | The new prompt (up to 2000 characters; goes through `GeneratedPromptSanitizer` and the generation-prompt moderation before anything is reserved). It replaces the planned prompt of that shot; look-matching words are not appended to it |
| `retake` | `true` = make a fresh take even when the prompt is unchanged (a new ledger row and a fresh provider idempotency key). `false` or absent = the existing content-addressed reuse: an identical earlier clip is served for free |

Example: `{"stepOrder": 4, "itemIds": ["v2"], "overrides": {"v2": {"prompt": "A slower drone shot over the bay"}}}`;
another take of the same prompt: `{"overrides": {"v2": {"retake": true}}}`; every clip again: `{"overrides": {"*": {"retake": true}}}`.

The executor (`VideoGenerateStepExecutor`, `IChangeRequestAwareStepExecutor`) reads the previous result's clips manifest
(the step's `ArtifactStorageKey`, handed over by `IStepChangeRequestContext`, project-scoped: a key outside
`projects/{projectId}/agentFiles/video-analysis/` is refused and never read). A clip the request does not name is carried
over **verbatim** from that manifest (its stored key must lie under `projects/{projectId}/agentFiles/generated/`): no
ledger row, no budget reservation, no submit, no poll, no download. A clip it names goes through the normal path with the
overrides applied; the budget gate and `MaxSpendCredits` see only those new takes, and the generation-prompt moderation
runs on each new prompt before the reservation. The output manifest holds every clip (carried and new) in the original
order, so `VideoCompile` and the dailies room read it unchanged. `meta.changeRequest {requestId, redoneClipIds[],
reusedClipCount}`; the step's cost totals count only the new takes. A request that matches no clip fails
`CHANGE_NOT_APPLIED` (a `UserInput` failure, nothing refunded). A retake is hashed with the request id, so it never
collides with an earlier row; a changed prompt that equals an earlier one is a free content hit.

## Script (by scene)

A `ScriptwriterAgent` step is changed **by scene** (`sc{n}`, or the scene's own `sceneId` in its stored `scenes[]`; item text is the
voiceover, shortened). Downstream still re-runs (narration, author, render): the planner marks everything that reads the
script `Run`, so the form and the approval card label it the most expensive option (`costNote: "script"` on the
capability, `StepChangeStepCapabilityResponse.CostNote`; the card still falls back to a step label containing "script").
The service resolves the agent type from the step's `AgentDefinition` (the run's snapshot) for capabilities and for
validation.

Overrides per scene or `*`: `voiceover` (text, at most 1000), `onScreenText` (text with line breaks or a list of text, at most 600
characters and 6 lines), `visualDescription` (text, at most 1000). Text goes through the sanitisers the pipeline uses
(voiceover: `VoiceoverTextSanitizer`; on-screen: `OnScreenCopySanitizer` markdown strip; control characters out). A request
may also be words only: a non-empty `instruction` **and** at least one named scene (`itemIds`); an instruction with no scene is
refused. Example bodies:

```json
{ "stepOrder": 2, "overrides": { "sc2": { "voiceover": "It does the whole job for you.", "onScreenText": ["All in one", "Nothing to install"] } } }
{ "stepOrder": 2, "instruction": "make scene 2 calmer", "itemIds": ["sc2"] }
```

`AgentStepExecutor` applies it (`IChangeRequestAwareStepExecutor`, `SupportedAgentTypes = [ScriptwriterAgent]`):
1. **Structured overrides are written straight into the previous script**: no model call, free, deterministic. The result is checked as a
   `ScriptwriterOutput` (what `Voiceover` `ScriptScenes` and the author read) and carries `changeRequest {requestId, redoneSceneIds[], reusedSceneCount}` at its root.
2. **A scene named with an instruction and no override** costs ONE model call (the normal agent usage, no cache) with the previous
   script, the scene ids and the instruction, asking for the full script back. The executor then enforces it in code: every scene not named
   is the previous scene verbatim, ids, `startTime` and `duration` of every scene stay, and so do title, length and narrative; only the
   voiceover, on-screen text and visual description of the named scenes are taken from the model. A scene count change by the model
   fails `SCRIPT_REWRITE_INVALID` (a platform failure, refunded, no model text kept); a request naming no scene of the script
   is `CHANGE_NOT_APPLIED` (a `UserInput` failure).
3. Cost: a structured-only change holds about nothing for the script step (`ScriptChange.NoModelCallFraction`) and prices everything downstream
   in full; a model rewrite holds the whole step (not the named scenes' share of the text).

## One panel for the film and the steps

The run page has one card, **Change this video**: a section for the video itself (instruction, optional scene choice,
keep-the-cut or re-edit, start-from version), the step changes picked from a step's detail (narration lines, video clips,
music takes, the thumbnail, script scenes) and an app's fields, one price estimate and one confirm. The API does not change;
the card routes by what is in the change set:

- **The video alone** goes through `POST .../revisions` (composition revisions), so scene choice, cut policy and start version
  apply.
- **The video together with any other change** goes through `POST .../change-requests` with the film as one target
  (`instruction` only, built on the latest version). Scene choice, cut policy and start version are off in the card, with a note.
- **Steps only** go through `POST .../change-requests` as before.

The card never sends the film alone to `change-requests`; the API still answers `409 USE_COMPOSITION_REVISION` for that, and
the codes of both routes are unchanged. The history under the card is one list: the revision history and the change-request
history merged by execution id, so a run that combined the film with steps is one line in the changes list (it also shows in the versions list when it made a new film version).

## Minimal recompute and price

`CompositionReplayPlanner.Plan` (with `targetLabel` and `TargetStepType`, so the reasons read for any step) gives every
step `Reuse` (a copied result, no hold, no cache), `Revise` (the target), `Run` (reads the target's output, e.g. the
render), `KeepIfCompatible` or `Skip`. Steps before the target and sibling branches that do not read it are `Reuse`.
The estimate and the 402 preflight cover only `Revise` and `Run` steps (plus the high end of `KeepIfCompatible`);
`POST .../change-requests/estimate` returns the range, the required figure and the per-step plan without writing
anything. A review of what the request re-made runs once.

**The target step is priced by its cost share.** The service computes `ScopeFraction`, the COST-WEIGHTED share of the
target step the request re-makes (`StepChangeScope.Compute`), once, from the stored output, and stores it on the
`step_change_requests` row (nullable `scope_fraction`, migration `AddStepChangeRequestScopeFraction`); the engine reads
that stored value and never recomputes it. An item weighs what it costs to make: a clip its seconds, a narration line
its characters (its seconds when it has no text), an audio take its seconds. Four clips of 2, 2, 2 and 14 s: redoing the
14 s one is 0.7, a 2 s one 0.1 (not a count-based 0.25). An override that makes an item bigger bills the new size (a
longer `text`, a longer `durationSec`); a prompt or retake bills the old clip duration. FALLBACK: `*`, the whole step,
a thumbnail, an output that cannot be read, or ANY item whose size is unknown (a failed clip with no duration, a line
with neither text nor duration) is 1, the full-step hold; never a count-based guess. The engine's credit hold for the
target step (`WorkflowExecutorService.ResolveStepReserveMillisAsync`, VideoGenerate's own priced hold included) and the
API estimate and 402 preflight (`PreflightChargedAsync` with the step's scale) are multiplied by it: rounded up, never
below 1 milli and never above the whole-step hold; an unpriced step (hold 0) stays unscaled. The credit gate's overdraft
headroom is still derived from the FULL-step hold (`ReserveAsync`'s `overdraftBasisMillis`), so a scaled-down hold keeps
the whole step's tolerance. Every other step of the replay holds as
before; settlement is the metered actual either way.

The fraction, the API estimate and the exact price are taken on the EFFECTIVE payload (the new request merged with the
overrides carried from the earlier one; the merge never widens which items are redone). An AudioGenerate take re-made
LONGER than configured is held at the longer price: the hold and the API preflight/estimate are priced from the step
config with the longest override `durationSec` applied (`StepChangePricing`), while the run still executes the unchanged
config and each take reads its own override. VideoGenerate has no duration override (a retake uses the clip's old seconds)
and the Voiceover hold comes from the step's usage history, not its lines, so neither needs this.

Texts a structured script change writes (`voiceover`, `onScreenText`, `visualDescription`) go through the content
moderation gate before any output is stored (fail-open when unavailable; a block fails the step `CONTENT_BLOCKED`).

### Exact price of the redone part

Next to the range, the estimate carries two additive fields, `targetExactCredits` and `targetExactNote`, for the target
step only, and only when the figure can be computed exactly from prices we hold, with no model or vendor call
(`StepChangeExactPriceCalculator`). The named items are priced at the settlement's own price row and markup:
VideoGenerate the re-made clips' seconds (`VideoGenSeconds` for the step's provider, model and resolution); AudioGenerate
the re-made takes' billed units (started minutes of music, started seconds of a sound effect); Voiceover the re-made
lines' UTF-8 bytes (`TtsInputBytes` for the speech provider and model). Everything else is null and the UI shows only
the range: an unknown model price, an oracle-priced Higgsfield clip, an unreadable output, an item of unknown size, a
request that names no item (the whole step), the thumbnail and the script, and billing off. The figure is capped at the
top of the range. The assistant's `ProposeStepChange` answer carries it as `targetExact` (`credits`, `note`) and the
approval card adds "This part costs exactly about N credits" next to the range; the card's cost stays the required figure.

## Changing a change

A change request can be made on the run an earlier request produced. That run re-makes the item from the
snapshot's unchanged config, so the service carries the earlier request's overrides forward: for every item the
new request names or overrides (and, for a `*` request, every item the earlier one overrode), the earlier keys
(its per-item overrides and its `*` ones) are added unless the new request sets the same key. A speed-only
request on a line whose text was changed keeps the text. Items outside the new request keep nothing extra, and
scope and price come from the new request's items only. The stored `overrides_json` is the effective payload
(one hop is enough because the earlier row is already effective); keys the step no longer accepts, or a merge
that would not validate, are dropped rather than failing the request. A script request with overrides plus an
instruction must name the scene the instruction is about. A script rewrite whose model answer is not a usable
script fails with `SCRIPT_REWRITE_INVALID` (platform error, refunded, not retried); `CHANGE_NOT_APPLIED` stays
for requests that match nothing. Video clips carried over report cost 0 and basis `carried`, like audio takes.

## API

All under `/api/v1/projects/{projectId}/executions/{executionId}` (404 across organizations, 403 for a View-only member
on a write):

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/change-capabilities` | Per step result: supported or not (with reason and route), items, override catalogue |
| `GET` | `/change-requests` | Requests whose source or own run is this execution, with run status and plan summary |
| `POST` | `/change-requests/estimate` | Validates and prices a request; writes nothing |
| `POST` | `/change-requests` | `201`; body `{stepOrder \| stepResultId, itemIds?, instruction?, overrides?}` or `{targets: [...]}` (see "Several changes in one request") or, for an app run, `{inputs: {...}}` (see "Changing an app's fields") |

Refusals: `400 {message, errors[{path,message}]}` for a bad request, `402 INSUFFICIENT_CREDITS`, `402 PLAN_NOT_ENTITLED`,
`409 RUN_NOT_FINISHED` / `STEP_NOT_COMPLETED` / `USE_COMPOSITION_REVISION` / `RUN_CLOSED` ("This run can no longer be changed. Start a new run.": the run first started at or before its project's content marker, or the marker cannot be read; checked before anything is spent, and also for the estimate), `429 REVISION_RATE_LIMITED`. Change
requests and revisions share one hourly bucket (the `revision` run-rate source, paid).

`POST …/executions/{id}/resume` on a change-request or revision run answers `409 REVISION_RESUME_NOT_SUPPORTED`: the run is
not resumed, the recovery sweeper never auto-resumes it, and `canResume` is false. Ask for the change again.
A chained request ("changing a change") carries the earlier overrides of the same step from the LAST request that
targeted it, found by walking the source runs back (at most 8 hops, project-scoped, cycle-safe).

## Several changes in one request

One request may change up to 6 steps and is reviewed, priced and run once ("change the graphics AND the voiceover").

```json
POST /change-requests          (and /change-requests/estimate)
{ "targets": [
    { "stepOrder": 3, "itemIds": ["vo0"], "instruction": "Softer", "overrides": { "vo0": { "speed": 0.9 } } },
    { "stepResultId": "…", "instruction": "Make the title bigger" }      // the film (see below)
] }
```

Each target has the single-request fields (`stepOrder | stepResultId`, `instruction?`, `itemIds?`, `overrides?`) and is
validated exactly like a single request. Rules (`400 {message, errors[{path,message}]}`, paths like
`targets[1].overrides.vo2.speed`): `targets` together with any top-level target field; 0 or more than 6 targets; two targets on
the same step; a target the single rules refuse (each violation under its own `targets[i].` path). A target that names an
unknown or unfinished step keeps the single request's status (`404`, `409 STEP_NOT_COMPLETED`, ...) and its body also carries
`errors: [{path: "targets[i]", message}]` so a client knows which entry failed. A `targets` array of one element behaves as the
single body.

- **A target that reads another target is re-made whole.** When one target is downstream of another (for example the script and
  the narration), its untouched items were built from the other target's OLD output, so carrying them over would be stale: the
  request names every item of that step (each keeps its own overrides), its price share is the whole step, and the stored row
  lists all the items. The executors' content-addressed reuse keeps genuinely unchanged items free. Independent targets keep
  their narrow scope. A script (an Agent step) downstream of another target is the exception: it keeps the owner's named
  scenes, because its item list is also the scope their instruction applies to, and everything after it is made again anyway.
- **One run.** One plan (every target is `Revise`; whatever reads any target runs again; whatever comes before the first target
  or does not read a target is reused; a review that only reviews the targets is skipped), one snapshot, one preflight / `402`,
  one rate-limit hit, one queued run. `step_change_requests` keeps one row per target, all sharing `executionId` and the same plan.
- **The film as a target.** The film render (the step whose route is `composition-revision`) can be one target of a request with
  at least one other target: it takes an `instruction` only (no `itemIds`, no `overrides`; `400` otherwise), and the run also
  writes a composition revision row, so the video's history shows the combined run as a new film version. The reviser is told
  which earlier steps were re-made in the same request (step types and item ids only, never the owner's text) and re-stages
  the new narration or clips before rendering. The film alone still answers `409 USE_COMPOSITION_REVISION`; a film that cannot be
  revised answers its usual `409 WORKSPACE_UNAVAILABLE` / `503 REVISION_AGENT_UNAVAILABLE`. Capabilities mark it `combinable: true`.
- **Which film version it builds on.** The film target builds on the same version a composition revision does: the newest
  version of the video whose render finished and whose run passed (`WorkspaceHeadAsync`), else the run's own version
  (`CurrentVersion`), from the same lineage list, so tenancy and lineage are judged as before. Production run 22f79d02 showed
  why: a request opened on the origin run rebuilt the film from that run's version and dropped the fix a later revision had
  made. Every other target's items still come from the run the request is opened from. The estimate and the `201` carry
  `filmBase: {versionNumber, runVersionNumber, newerThanRun, label}` (numbers only, never ids; null without a film target), and
  the assistant's approval card adds "The video starts from version N, the latest, not version M this run made" (or "..., the
  version this run made").
- **Responses.** `StepChangeRequestResponse` and the estimate gain `targets: [{stepOrder, stepLabel, stepType, itemIds,
  instruction, kind: "step"|"film"}]`, ordered by step order; the top-level fields (`targetStepOrder`, `stepType`, `instruction`,
  `itemIds`) are those of the first target. The estimate has one `estimate` and one `replay` (the combined plan). The capabilities
  answer gains `maxTargets: 6`.
- **List.** `GET /change-requests` returns one entry per request (per run), with its status and cost once and its `targets`;
  a single-target request is today's entry plus a `targets` of one.

## Changing an app's fields (app inputs)

A finished run of an app with its own fields (docs/design/app-inputs.md, section 25) can be redone with one or more field
values changed, as a new linked run through the same machinery. Always on (the `Workflows:AppInputs:Enabled` switch was removed 2026-11-10).

```json
POST /change-requests          (and /change-requests/estimate)
{ "inputs": { "whatTheyDo": "Waves hello", "callToAction": null } }
```

- **Values.** The run's own canonical values (`run_brief_json.inputs`) with the request's laid over (`null` clears an optional
  field), checked by the run gate's own `AppInputValueValidator.Evaluate` against the run's copied page and the app's authored
  steps: `400 APP_INPUT_MISSING` / `APP_INPUT_INVALID` / `APP_INPUT_MODEL_UNAVAILABLE` with `inputId`. `inputs` never comes
  with the single-target fields (`400`); since F3 it may come with `targets[]` (see "Fields, run settings and steps in one
  request" below).
- **Seeds.** `AppInputChangePlanner` (`ReelBolt.Shared/Workflows/ChangeRequests/AppInputChanges.cs`): the steps on the run's
  path that consume a changed value (the binder's consumer map: placeholders, `appInputs` whole values and `@model`, the
  request template for every step that reads the request, an agent `model` input for that agent's steps; Conditionals
  excluded, the replay re-evaluates them). The seeds are the plan's targets (`Revise`), what reads them runs again, everything
  else is reused free; a step the values' branches never reach and that has no source result is marked `Skip` (not priced).
- **Refusals.** `400 APP_INPUTS_UNCHANGED` (no value differs); `409 INPUT_CHANGES_BRANCH` (the reachable steps would differ, or a
  changed value is read by a condition the values alone do not decide: a different branch is a different film, run it again);
  `409 APP_INPUT_NOT_USED` (nothing on the run's path uses the changed fields); `409 APP_INPUTS_STALE` (the app's steps, bound
  with the run's own values, no longer equal every step the request re-makes, the app was deleted, or the request text needs
  recomposing and does not have the composed shape); plus `RUN_NOT_FINISHED`, `RUN_CLOSED`, `402`, `429` as for any request.
- **The new run.** The request's snapshot clones the source run's steps; each seed's config is replaced by the authored step
  bound with the new values, and the run's page is copied onto it. The run's brief carries the NEW values (its conditions and a
  later change read them); its request text is recomposed only when a request-template field changed (the notes are recovered
  from the source request under `Notes for this run:`). A replay whose brief chose an agent's model gets that model (the engine
  records the live agents with the choices for a `CompositionReplay` run that carries the page).
- **Rows.** One `step_change_requests` row per seed, `scope_fraction` 1, `instruction` = the summary ("Changed “What they do”:
  Pours latte art → Waves hello"), `overrides_json` = `{"@appInputs": {"values": {id: value}}}` (`AppInputChangeRows`). The
  engine's `StepChangeRequestContext` never hands such a row to an executor; the step runs from its re-bound config.
  "Changing a change" never carries one forward as overrides.
- **Responses.** The estimate, the `201` and each entry of the list gain `inputChanges: [{inputId, label, type, before, after, value}]` (words: a choice
  by its label, a switch by its words, a model by its name); targets carry `kind: "inputs"`. The capabilities answer gains
  `appInputs: {workflowId, fields: [{inputId, label, type, value, valueText, supported, notSupportedReason, reasonCode, usedBy,
  decidesBranch, branchChangingValues, input}]}` for an app run (absent otherwise).
- **Assistant.** `GetChangeOptions` adds `appFields`; `ProposeStepChange` / `ApplyStepChange` (MCP `StepChange`) take
  `inputs` (registered only with the switch on; the token binds the exact values); the approval card names each changed field by
  label, before and after, what is redone and reused, and the price. No tool added.
- **Web.** On the app page, **Change one thing and re-run** opens a dialog of the run's fields when at least one can be
  changed; otherwise the form is pre-filled as before.

### Run settings (brief fields) in a change (F3)

Same switch (docs/design/app-inputs.md 29). The body's `brief` (the run brief's own shape) sets run settings; the fields set are
the new values:

```json
{ "brief": { "language": "it", "notes": "Make it cosy", "sourceFiles": [{ "role": "Video", "fileIds": ["…"] }] } }
```

- **Honoured:** `language`, `sourceLanguage` (`"auto"` clears either), `lengthSec`, `voice`, `designSystem`, `overlayCardAudio`,
  `avatar`, `notes` (`""` clears) and `sourceFiles` (each role listed replaces that role's files). Checked as a run start checks
  its brief (`RunBriefValidator.Validate`, then `RunBriefSplitter.ProjectProblemAsync`: files of this project, picture/video
  roles, the avatar's consent); a problem is `400` with `errors[{path: "brief", message}]`.
- **Refused:** `format`, `resolution`, `stylePreset`, `styleId`, `style`, `tone` change the whole film:
  `400 BRIEF_FIELD_NOT_CHANGEABLE` with `field`; `brief.inputs` is the same code (`field: "inputs"`, send them under `inputs`);
  notes of a run whose request was not composed on its page: `400 BRIEF_FIELD_NOT_CHANGEABLE`, `field: "notes"`.
- **Seeds.** The steps on the run's path that read a changed setting: `BriefFieldConsumers.FieldsReadBy` for a brief field, the
  avatar only where it is honoured (`TakesBriefAvatar`), every request-reading step for the notes, a step that takes the role
  (`AcceptsFileRole`, or a `VideoAnalyze` `BriefFiles` source of that role) for files. None: `409 BRIEF_FIELD_NOT_USED` with
  `field`. Nothing changed at all (fields and settings): `400 APP_INPUTS_UNCHANGED`.
- **The new run** runs with the source run's stored brief, the changed settings laid over (and the app's values), so every
  re-made step reads them through the engine's own brief overrides; the request is recomposed when the notes change (the page's
  composed text, then `Notes for this run:` and the new notes).
- **Rows and lines.** The settings are stored in words on each seed's row (`{"@appInputs": {"values": {...}, "brief":
  [{key, label, before, after}]}}`, `AppInputChangeRows.BriefLines`); `inputChanges` carries them with `inputId` = the key
  (`brief:language`, `brief:notes`, `brief:files:Video`) and `type: "brief"`, on the estimate, the `201` and the list.
- **Capabilities.** `appInputs.briefFields[]`: each run setting the run's page shows (`key`, `kind` brief|notes|files, `field`,
  `role`, `label`, `value` in the body's shape, `valueText`, `supported`, `notSupportedReason`, `reasonCode`, `usedBy`, `input`);
  absent when the page shows none. Only an app run with its own fields has `appInputs` at all.

### Fields, run settings and steps in one request (F3)

With the switch on, `inputs` and/or `brief` may come with `targets[]` (never with the single-target fields: `400`). One plan
(`CompositionReplayPlanner.Plan` over every seed and target, the film target as for several targets), one estimate, one hold,
one rate-limit hit, one run. The step targets are prepared exactly as a multi-target request (`PrepareAsync` with the field
seeds as `extraSeeds`: a target downstream of a seed is re-made whole, and the film may be the only step target next to a
changed field). A target a changed field or setting already re-makes is refused at its own path (`400`, `errors[{path:
"targets[i]"}]`). Rows: one per seed (the app-input marker, the summary as instruction, `kind: "inputs"`) and one per step
target (its own overrides and instruction, `kind: "step"` or `"film"`); unchanged `inputs` next to a step target or a setting
are not a refusal. The assistant's `ProposeStepChange` / `ApplyStepChange` take `inputs`, `brief` and `changes` together (the
token covers all three; `inputs` alone keeps the L9 token byte for byte); the card lists each field or setting before and after
and each step with its note. On the run detail page, **Change the app's fields** adds the fields and settings to the change set
beside the step changes: one review panel, one price, one submit.

### The price follows the app's chosen models (F3)

The 402 preflight and the estimate of a change request and of a composition revision price the app's chosen models
(`ReplayModelPricing`: `AppAgentModelAmbient` with the agents' Chat choices and the steps' chosen models) exactly when the
replay's definition carries the app page, which is when the engine holds at them: an app-input change (always), a revision or a
plain step change of a run that chose an agent's model (the page is copied, F2 28.4, now also for plain step changes). Otherwise
nothing changes.

## Asked by a review loop

A review loop with `allowAdjustments` on (every shipped template) may ask for these same changes INSIDE the run it
reviews, as a review-loop re-run rather than a new linked run (`docs/design/review-loop-adjustments.md` "Change
requests inside the run"). The reviewer's entries for one step become one `ChangeRequestPayload` that passes the same
`ChangeRequestValidator` (same catalogue, same limits, same messages) against the step's latest output in the run;
`StepChangeRequestContext` hands it to the step's executor on its next dispatch (through `ReviewStepChangeRegistry`)
before looking for a `step_change_requests` row, so carry-over, consent checks (`voiceId`) and `CHANGE_NOT_APPLIED`
behave exactly as for a customer. Differences: no row, no linked run, no `scope_fraction` (the step holds its normal
full hold and settles at what it metered), no `revision` rate-limit hit (the loop's `maxIterations` and the credit gate
bound it), and a reviewer may never set a `prompt` override (nor anything naming egress, a provider, a model, a spend
cap or the take count). The review record lists each entry (`kind: "change"`, `item`) and, on the next pass, what the
re-run charged.

## Assistant and MCP

`GetChangeOptions` (read-only), `ProposeStepChange` and `ApplyStepChange` (Propose/Apply with an `IConfirmationTokenService`
token over the canonical payload; the apply is approval-gated, and its card shows the step, the items, the price and
what is redone and reused). The tools are in `AssistantChangeRequestTools`; over MCP they ride the assistant tool
catalog. With app inputs on they also change an app run's fields (`inputs`, see "Changing an app's fields"). See
[`platform-assistant-mcp.md`](platform-assistant-mcp.md).

## Not built yet

A retry of a step that failed (use Resume or Retry on the run) and change requests on the other agent steps and the rooms (`EditRoom`, `GraphicsRoom`, `ColorGradeRoom`, `DailiesRoom`).
