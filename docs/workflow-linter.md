# The workflow linter

The linter reads a workflow before it runs and says, in plain words, what would go wrong: a narration that points at
a step that is not there, a film that is asked to be narrated when nothing speaks, a voice that has no consent on
record. It exists so a run that was going to fail (or quietly come out wrong) is caught before anything is spent.

Every finding has the same shape:

| Field | Meaning |
|---|---|
| `ruleId` | `WL001` to `WL034`. Stable. `WL000` is the save check below. |
| `severity` | `error` (the run would fail or come out broken today), `warning` (probably not what you want), `info` (worth knowing). |
| `stepOrder` | The step it is about, when it is about one step. |
| `message` | The English sentence: what is wrong, in plain language, with step numbers and names, never ids. The assistant, MCP and the run gate read this. |
| `messageKey` | The catalogue key of the sentence (`WL004`, `WL001.comesLater`, `WL018.unreadField`). A client renders `lint.<messageKey>` in the reader's language. |
| `args` | The tokens that sentence takes, as text: `{"stepOrder": "4", "stepLabel": "Draft narration", "problem": "comesLater"}`. Never a clause. |
| `fix` | Optional: `{kind, label, patchId?, labelId?, args?}`. `patchId` names a closed assistant patch when one exists (`addVoiceover`); `labelId` names the button copy (`WL001.retargetSource`, looked up as `lint.fix.<labelId>`) and its `args` carries the tokens for it. |

Only an `error` can ever stop a run, and only when enforcement is on (see "Rolling it out"), save the one always-on
refusal of an open video slot (WL010 below). Warnings and infos never block.

## Where it runs

- `POST /api/v1/projects/{projectId}/workflows/{id}/lint` lints a saved workflow. The optional body is
  `{ "brief": {...}, "steps": [...] }`: `brief` adds the brief rules, `steps` lints that draft in place of the saved
  steps (unsaved editor changes). Viewing the project is enough; it reads and changes nothing.
- `POST /api/v1/projects/{projectId}/workflows/lint` lints a draft that has no saved workflow yet; `steps` is required.
- Starting a run (Execute, Retry and Resume share one gate; the assistant's chat and MCP `ExecuteWorkflow` tool and its REST twin go through it too, in the same place).

The answer is `{ findings: [...], errors, warnings, infos }`, most serious first.

## Where the rules live

One table, `StepCompatibilityRules`, holds every rule's id, name, severity, description, what it needs, the message
template and the fix kind. The linter reads its checks and messages from it, and the step catalog reads "needs /
produces" from the same rows, so what the assistant is told and what the platform enforces cannot drift apart. The
rules that need only the steps (WL001 to WL005, WL013, WL014, WL019, WL020, WL021, WL022 and WL026) run in `StepGraphLinter`; the rest read facts
about the project (providers, plan, files, voices, design approval) through the facts source, and a fact that cannot
be read is "unknown": a rule never fires on an unknown fact.

### The message keys and their args

`message` stays the English sentence the assistant and MCP read; a client that localises looks up
`lint.<messageKey>` (and `lint.fix.<labelId>` for the button) in `web/messages/en/apps.json` and falls back to
`message`/`label` for a key it does not know. The catalogue entry is the localiser's own sentence, so the client first
turns the tokens below into phrases (a named step, "from step 4", a list, a closed word such as `problem.comesAfter`)
and then fills the entry. `WorkflowLintI18nCatalogueTests` (`inference/tests/ReelBolt.WorkflowEngine.Tests/Workflows/Lint/`)
holds the two sides together: every key below must have its entry with exactly these ICU arguments, and every
`labelId` below must have its `lint.fix.*` entry. It never pins the English wording, and extra catalogue entries (a
rule not on this branch yet) are not drift.

A `?` marks a token that is absent when the fact is unknown (a step with no label, a reference that does not exist).
These are the tokens of `args`; the catalogue entry's ICU arguments are the phrase names in the right-hand column.

| Token | Meaning |
|---|---|
| `stepOrder`, `stepLabel` | the step the finding is about, and its label when it has one |
| `sourceOrder`, `sourceLabel` | the referenced step, where the sentence calls it a source |
| `targetOrder`, `targetLabel` | the same, where it calls it a target |
| `problem` | what is wrong with the reference: `isNotSet`, `notInWorkflow`, `comesAfter`, `wrongKind`, `firstStep`, `previousWrongKind`, WL002's `doesNotNameTheStep`, WL006's `noDefault`/`providerUnavailable` |
| `expectedKind` | the kind the reference needed (`scriptwriter`, `voiceover`, `videoAnalysis`, …), on `wrongKind` only |
| `voiceKind` | `narration` or `pickups` |
| `capability` | `speechSynthesis`, `videoGeneration`, `musicGeneration`, `soundEffectGeneration`, `transcription`, `vision` |
| `feature` | the plan feature: `rooms`, `audioGeneration`, `videoGeneration` |
| `featureCount` + `feature1…featureN` | how many features a stream copy conflicts with, and their codes (`music`, `narration`, `captions`, `fades`, …) |
| `field` | the brief field (`language`, `lengthSec`, `style`, `format`, …) |
| `role` | the file role: `video`, `code`, `music`, `sfx`, `image`, `document`, `insertContent` |
| `slot` | the file slot: `videoFile`, `musicTrack`, `insert` |
| `input`, `inputIndex` | an app input's label and its index; in WL004, `input` is the compile input (`editDecision`, `analysis`, `graphicsPlan`, …) |
| `detail` | the save validator's own step phrase, for a provider-ownership error |
| `language`, `languageCount` + `language1…languageN` | the film's language, and the languages the chosen voice speaks |
| `length`, `available` | the seconds asked for, and the seconds the footage adds up to |
| `loopOrder` | the review loop's step order |
| `reviewOrder`, `reviewLabel`, `lastOrder`, `laterCount`, `laterNKind`, `laterNOrder` | WL020's review and each narration/music/final-cut step after it (`narration`, `music`, `finalCut`) |
| `voice`, `reason` | the voice's name (or `youChose`) and why it cannot be used (`consentMissing`, `consentRevoked`, `retentionExpired`, `notInProject`) |
| `resolution` | the requested resolution |
| `scriptOrder`, `scriptLabel`, `scriptKind` | the earlier step that writes the lines, and its kind (`scriptwriter`, `narrationWriter`, `narrationTranslator`) |

| Message key | `args` sent | Catalogue entry's ICU arguments |
|---|---|---|
| `WL001.isNotSet` | `stepOrder`, `stepLabel?`, `problem` | `actor`, `from`, `problem` |
| `WL001.notInWorkflow` | `stepOrder`, `stepLabel?`, `sourceOrder`, `problem` | `actor`, `from`, `problem` |
| `WL001.comesAfter` | `stepOrder`, `stepLabel?`, `sourceOrder`, `sourceLabel?`, `problem` | `actor`, `from`, `problem` |
| `WL001.wrongKind` | `stepOrder`, `stepLabel?`, `sourceOrder`, `sourceLabel?`, `problem`, `expectedKind` | `actor`, `from`, `problem` |
| `WL002.doesNotNameTheStep` | `stepOrder`, `stepLabel?`, `voiceKind`, `problem` | `actor`, `problem` |
| `WL002.notInWorkflow` | `stepOrder`, `stepLabel?`, `voiceKind`, `problem`, `targetOrder` | `actor`, `problem` |
| `WL002.comesAfter` | `stepOrder`, `stepLabel?`, `voiceKind`, `problem`, `targetOrder`, `targetLabel?` | `actor`, `problem` |
| `WL002.wrongKind` | `stepOrder`, `stepLabel?`, `voiceKind`, `problem`, `targetOrder`, `targetLabel?`, `expectedKind` | `actor`, `problem` |
| `WL003.isNotSet` | `stepOrder`, `stepLabel?`, `problem` | `at`, `to`, `problem` |
| `WL003.notInWorkflow` | `stepOrder`, `stepLabel?`, `problem`, `targetOrder` | `at`, `to`, `problem` |
| `WL003.comesAfter` | `stepOrder`, `stepLabel?`, `problem`, `targetOrder`, `targetLabel?` | `at`, `to`, `problem` |
| `WL003.noImprovement` | `stepOrder`, `stepLabel?`, `targetOrder` | `at`, `over` |
| `WL004.isNotSet` | `stepOrder`, `stepLabel?`, `input`, `problem` | `actor`, `input`, `from`, `problem` |
| `WL004.notInWorkflow` | `stepOrder`, `stepLabel?`, `input`, `problem`, `targetOrder` | `actor`, `input`, `from`, `problem` |
| `WL004.comesAfter` | `stepOrder`, `stepLabel?`, `input`, `problem`, `targetOrder`, `targetLabel?` | `actor`, `input`, `from`, `problem` |
| `WL004.wrongKind` | `stepOrder`, `stepLabel?`, `input`, `problem`, `targetOrder`, `targetLabel?`, `expectedKind` | `actor`, `input`, `from`, `problem` |
| `WL004.firstStep` | `stepOrder`, `stepLabel?`, `input`, `problem` | `actor`, `input`, `from`, `problem` |
| `WL004.previousWrongKind` | `stepOrder`, `stepLabel?`, `input`, `problem`, `targetOrder`, `targetLabel?`, `expectedKind` | `actor`, `input`, `from`, `problem` |
| `WL005` | `stepOrder`, `stepLabel?`, `featureCount`, `feature1…featureN` | `actor`, `features` |
| `WL006` | `stepOrder`, `stepLabel?`, `capability`, `problem` | `actor`, `capability`, `problem` (entry `lint.WL006.message`) |
| `WL006.providerOwnership` | `stepOrder?`, `stepLabel?`, `detail` | `detail` |
| `WL007.stepFeature` | `feature`, `stepOrder`, `stepLabel?` | `feature`, `step` |
| `WL007.resolution` | `resolution` | `resolution` |
| `WL008` | `voice`, `reason` | `voice`, `reason` |
| `WL009` | `language`, `languageCount`, `language1…languageN` | `language`, `languages` |
| `WL010.missingMedia` | `stepOrder`, `stepLabel?`, `role` | `actor`, `role` |
| `WL010.missingFile` | `stepOrder`, `stepLabel?`, `slot` | `stepOrder`, `slot` |
| `WL010.openVideoSlot` | `stepOrder`, `stepLabel?` | `actor` |
| `WL011` | `field` | `field` |
| `WL012.step` | `stepOrder`, `stepLabel?` | `step` |
| `WL012.brief` | none | none |
| `WL013` | `stepOrder`, `stepLabel?`, `loopOrder` | `actor`, `loop` |
| `WL014` | `stepOrder`, `stepLabel?`, `targetOrder` | `actor`, `target` |
| `WL015.tracksAnObjectByName` | `stepOrder`, `stepLabel?` | `actor` |
| `WL015.startsFromFrame` | `stepOrder`, `stepLabel?` | `actor` |
| `WL015.cleansAudioWithAi` | `stepOrder`, `stepLabel?` | `actor` |
| `WL015.editsPictureWithAi` | `stepOrder`, `stepLabel?` | `actor` |
| `WL015.enhancesVideoWithAi` | `stepOrder`, `stepLabel?` | `actor` |
| `WL015.showsAnAvatar` | `stepOrder`, `stepLabel?` | `actor` |
| `WL016` | none | none |
| `WL017` | `length`, `available` | `length`, `available` |
| `WL018.unreadField` | `input`, `inputIndex` | `input` |
| `WL018.unusedRole` | `input`, `inputIndex`, `role` | `input`, `role` |
| `WL019` | `stepOrder`, `stepLabel?` | `actor` |
| `WL020` | `reviewOrder`, `reviewLabel?`, `lastOrder`, `laterCount`, `later1Kind`, `later1Order`, … `laterNKind`, `laterNOrder` | `reviewOrder`, `later`, `lastOrder` |
| `WL021` | `stepOrder`, `stepLabel?`, `sourceOrder`, `sourceLabel?` | `actor`, `over` |
| `WL026` | `stepOrder`, `stepLabel?`, `scriptOrder`, `scriptLabel?`, `scriptKind` | `actor`, `script` |
| `WL027` | `stepOrder`, `stepLabel?`, `avatar`, `reason` | `actor`, `avatar`, `reason` |
| `WL028` | `input`, `inputId?` | `input` |
| `WL029` | `input`, `inputId?` | `input` |
| `WL030` | `input`, `inputId?` | `input` (entry `lint.WL030.message`) |
| `WL030.recommended` | `input`, `inputId?` | `input` |
| `WL031` | `input`, `inputId`, `value` (`on`/`off` for a switch, else the option label or the number), `count`, `skippedCount`, `skipped1…skippedN` | `input`, `value`, `count` |
| `WL032` | `input`, `inputId`, `count` | `input`, `count` |
| `WL033` | `input`, `stepOrder`, `stepLabel?`, `credits`, `cap` | `input`, `actor`, `credits`, `cap` |
| `WL034.useSibling` | `stepOrder`, `stepLabel?`, `model`, `sibling` (the image-capable model of the same family) | `actor`, `model`, `sibling` |
| `WL034.noSibling` | `stepOrder`, `stepLabel?`, `model` | `actor`, `model` |

A `labelId` names one button sentence: two findings that offer different words (the same rule can suggest two
different fixes) carry two ids, never one id reused with different English.

| `labelId` | `fix.args` sent | Catalogue entry's ICU arguments |
|---|---|---|
| `WL001.retargetSource` | `expectedKind` | `expected` |
| `WL002.retargetVoiceover` | none | none |
| `WL003.retargetTarget` | none | none |
| `WL003.retargetModelStep` | none | none |
| `WL004.retargetInput` | `input` | `input` |
| `WL005.reencode` | none | none |
| `WL006.chooseProvider` | none | none |
| `WL006.chooseAvailableProvider` | none | none |
| `WL007.changePlan` | none | none |
| `WL007.changeResolution` | none | none |
| `WL008.chooseVoice` | none | none |
| `WL009.chooseVoice` | none | none |
| `WL010.upload` | `role` | `role` |
| `WL010.pickFile` | none | none |
| `WL012.approveDesign` | none | none |
| `WL013.startLoopAfter` | none | none |
| `WL014.earlierStep` | none | none |
| `WL015.allowEgress` | none | none |
| `WL015.allowOnAppPage` | none | none |
| `WL016.addVoiceover` | none | none |
| `WL017.shorterFilm` | none | none |
| `WL018.removeReference` | none | none |
| `WL019.voiceOrCaptions` | none | none |
| `WL020.moveReview` | none | none |
| `WL021.muteSourceAudio` | none | none |
| `WL026.useScript` | none | none |
| `WL027.chooseAvatar` | none | none |
| `WL028.fillInput` | `input` | `input` |
| `WL029.changeInput` | `input` | `input` |
| `WL030.chooseModel` | none | none |
| `WL033.narrowRange` | none | none |
| `WL034.chooseModel` | none | none |

The known limit is `WL000`: the save check passes a save validator's own sentence through as `message`, with
`messageKey` `WL000` and no `args`, so it stays English. There is deliberately no `lint.WL000` entry and a client falls
back to `message`; the drift test leaves it out.

## WL000 save-check

When it fires: a step's settings would be refused when the workflow is saved (a required setting missing, a number out
of range, a provider that is not yours). It reports the same sentence the save would. Always a warning here, because a
workflow that is already saved passed it once.

How to fix: change the setting the sentence names.

## WL001 voiceover-source-missing

When it fires: a Voiceover step reads its lines from a step that does not exist, comes after it, or is not the kind of
step that writes lines (a Scriptwriter for a script, a NarrationWriter or NarrationTranslator for a narration plan, a
PickupPlanner for pickups).

Why: the voiceover would stop when it runs, with nothing to speak. Severity: error.

How to fix: point the voiceover at the earlier step that writes the lines. If you inserted a step above it, the number
moved.

## WL002 compile-voiceover-unwired

When it fires: the final cut is set to mix in narration (or re-recorded lines) but the Voiceover step it names is
missing, comes later, or is not a Voiceover step.

Why: the step does not fail, it just comes out silent, which is the worst way to find out. Severity: error.

How to fix: point the compile at the Voiceover step, or turn the narration off on the compile.

## WL003 reviewloop-target-invalid

When it fires: a review loop has no step to go back to, or points at itself or a later step. It is also reported, as a
warning, when nothing in the repeated span (from the target up to the review) makes anything new on a repeat: a model
step, a room (including the dailies room) or a video generation counts; a span of only fixed steps does not.

Why: the review would never loop, or would only repeat fixed steps and not improve anything. Inserting a step above a
loop is the usual cause: the number it points at is now a different step. Severity: error (warning for a span of
fixed steps).

How to fix: point the loop at the earlier step that makes the thing being reviewed.

## WL004 compile-ref-invalid

When it fires: the final cut reads an input (the edit decision, the analysis it cuts against, a take selection, a
graphics, music, colour or sound-effects plan, generated clips) from a step that is missing, comes later, or is the
wrong kind of step. "The step before" counts too: if the step right before it is a voiceover, it is not a decision.

Why: the cut would be wrong or the step would stop. Severity: error.

How to fix: point the input at the earlier step of the right kind.

## WL005 compile-mode-conflict

When it fires: the final cut is set to copy the footage without re-encoding, and also asks for something that changes
the picture or sound: music, narration, graphics, sound effects, a colour grade, inserted screens, generated clips,
captions, region effects, a different output format, ducked or replaced dialogue, censoring, fades or transitions. It
also fires when stream copy is chosen without allowing cuts between keyframes.

Why: those need the picture to be re-made, or the cuts to land on keyframes, so the step would stop. Severity: error.

How to fix: switch the compile to re-encode, or drop the extras.

## WL006 provider-missing-for-capability

When it fires: a step needs a provider for a capability (a voice for a Voiceover, video generation, music or sound
effect generation, lip-sync for a step that makes an avatar talk, transcription marked Required, vision marked Required, the audio isolation of an AI audio cleanup)
and none is chosen on the step or set as the
organization default, or the one chosen is not available to your organization.

Why: the step would stop after earlier steps had already been paid for. Severity: error.

How to fix: connect a provider for that capability, or choose one on the step.

Variant `WL006.modelUnsupported`: a VideoGenerate step names a `model` that the video provider it resolves to (its own, else the
default) does not know, as that provider's offline model lookup says (the same one the engine prices with, and fails the step on:
"Model ... is not supported"). Unknown when there is no such provider or it prices through a network oracle. At run start it refuses
with `400 VIDEO_MODEL_UNSUPPORTED`. The model picker never recommends a model that cannot do the step's job.

Variant `WL006.lipSyncModelNotSet`: a VideoGenerate step carries avatar audio (a talking head) and no lip-sync model is set up
(no LipSync row on the step and no default LipSync row; a video-generation row never stands in). The message tells the person
to ask a workspace admin to add one under Lip-sync. Like `modelUnsupported`, it is a run-start blocker: the run is refused with
`400 LIP_SYNC_MODEL_NOT_SET`. When the step names a provider of its own, `modelUnsupported` judges its `model` against the
lip-sync catalog; with no provider of its own the default lip-sync model runs, with its own resolution, ratio and duration where the step's are not
offered, and there is no step model to judge. A step bound to a lip-sync row whose model does not offer the step's resolution
or ratio is refused at run start with `VIDEO_MODEL_UNSUPPORTED`.

## WL007 plan-not-entitled

When it fires: a step uses a feature the plan does not include (rooms, video generation, audio generation), or the
chosen resolution is above what the plan renders.

Why: the run would be refused. Severity: error.

How to fix: choose a different step or resolution, or change the plan.

## WL008 voice-consent-missing

When it fires: the project voice chosen on a Voiceover step (or in the brief) cannot be used: consent was never
recorded, was withdrawn, or has expired, or the voice is not in this project.

Why: the narration step would stop. Severity: error for a voice named on a Voiceover step. A voice named only in the
brief is checked only when the workflow has a Voiceover step, and is a warning, because the step can still carry its
own voice.

How to fix: choose a voice whose consent is on record, or record it.

## WL009 voice-language-mismatch

When it fires: the film's language is not one of the languages the chosen voice is known to speak.

Why: the narration may sound wrong or garbled. Severity: warning. A voice that states no languages is "unknown" and
does not trigger this rule.

How to fix: choose a voice that speaks the film's language.

## WL010 source-media-missing

When it fires: a step needs media the project does not have: video footage for an analysis step with no file chosen,
source code for the code analyzers, a music track for the music steps, sound effect files for the sound designer. It
also fires when a step names a file (a music track, a file to insert, a video) that is not in the project. Media named
in the brief's source files counts as present.

Why: the run would be empty or stop. Severity: error for footage and code; a missing music track or missing sound
effects is a warning, because the film is still made without them. Roles are worked out from the file type (there is no stored
role), with the same counts the app page's readiness strip uses: audio counts as both music and sound effects, and code
is source files, text, archives and files synced from GitHub.

How to fix: upload the media to the project, or pick a file that is in it.

**An open video slot at run start (`WL010.openVideoSlot`).** An analysis step whose source is `ProjectFile` with no file
id (the "no file selected" slot of the footage recipes) is filled by the engine from the run brief's `video` files and
never from the project, so a run whose brief names no video fails that step with `SOURCE_UNRESOLVED`. Only the run-start
gate asks this (`WorkflowLintRequest.AtRunStart`); the lint endpoints and the editor, which have no run yet, do not.
At run start it is an error on that step even when the project holds videos, and it is the one finding that refuses
the start whatever `Workflows:Lint:Enforce` says: `400 VIDEO_SOURCE_NOT_CHOSEN` with `message` (names the step, says to
choose a video, no ids), `stepOrder`, `stepLabel` and the finding, on Execute, Retry, Resume, the workflow assistant's
execute and the assistant chat/MCP `ExecuteWorkflow` (Retry and Resume judge the run's own steps and stored brief; a
resume ignores steps it will not run). A brief that names a video, or a step that pins a file, is unaffected.

## WL011 brief-field-unread

When it fires: you set a brief field (language, source language, length, voice, style or tone, format, resolution,
design system, source files) and no step in this workflow reads it.

Why: the choice would be silently ignored. Severity: info. Which step reads which field comes from the one consumption
table (`BriefFieldConsumers`, section 3.8 of the design), the same one the app page uses; a custom agent is assumed to read everything it
could be told.

How to fix: use a workflow with a step that reads it, or leave the field out.

## WL012 design-system-missing

When it fires: a DesignSystem Require step that needs the design (not one that asks for the product map only) is in the workflow, or the brief says to build with the project's design
system, and the project has no approved design system.

Why: a Require step stops the run, so that case is an error. A brief that only expects the design system does not stop
a run, so it is a warning. Severity: error (warning for the brief alone).

How to fix: approve the project's design system.

## WL013 paid-step-in-loop

When it fires: a video generation or voiceover step sits inside a review loop's repeat span (from the loop's target up
to the review).

Why: every extra round of the loop pays for it again. Severity: warning.

How to fix: start the loop after the paid step, or accept the cost.

## WL014 forward-step-reference

When it fires: a step reads from a step that comes after it (or from itself) in a setting the specific rules above do
not already cover.

Why: the input does not exist yet when the step runs. Severity: error.

How to fix: point the input at an earlier step.

## WL015 egress-permission-missing

When it fires: a step would send your footage to an outside service and the step's permission switch is off: a
video analysis that tracks an object by name, a video generation that starts from a frame of your footage (a step
frame or an image file from the project), an audio cleanup with an AI target (`IsolateVoice`, `RemoveMusic`), which
uploads the source's audio to a hosted model, or a video enhancement with a hosted engine (AI upscale or AI smooth slow
motion), which uploads the source video to a hosted model. The free local targets of an audio cleanup (hum, wind, hiss,
echo, basic voice) and the free local video operations (denoise, motion blur, local upscale, local interpolation) never
ask for it.

The rule reads the one shared definition of a step's need (`MediaEgressNeed`), the same one the app's permission field
and the run gate use. Its fix is `fillInput` on the app's own field `allowMediaEgress` (fix label
`WL015.allowOnAppPage`, "Allow sending your media on the app page"; the finding's args carry `inputId`), so the run page
takes the person to the switch instead of to the step editor. A run whose brief gives the permission
(`brief.inputs.allowMediaEgress = true`) does not report WL015. With a brief of an app whose fields pick a branch, a step the
run does not reach does not report it either. The older fix label `WL015.allowEgress` is no longer
emitted.

VideoEnhance note (WL006 and WL015 for VideoEnhance): a `VideoEnhance` step with `engine=Hosted` is checked by WL006 for a
`VideoUpscale` (op `Upscale`), `FrameInterpolation` (op `Interpolate`) or `VideoOutpaint` (op `Extend`, token `capability.videoOutpaint`) provider (tokens `capability.videoUpscale`,
`capability.frameInterpolation`; the step's `providerId` or the default row) and by WL015 for `allowSourceMediaEgress`
(message variant `WL015.enhancesVideoWithAi`). A local operation raises neither. The plan feature `video_enhance` and a
source longer than the cap are NOT linter findings (the source's length is not known when the workflow is linted): they are
refused at run time by the step (`PLAN_NOT_ENTITLED`, `ENHANCE_SOURCE_TOO_LONG`), before anything is uploaded. No new WL
number was needed.

Why: the run would be refused. A template may ship with the switch off on purpose, so that nothing leaves the host
without the author's say-so. Severity: error.

How to fix: turn the permission on for that step, if you agree to the footage being sent.

AudioCleanup note (WL006 and WL015 for AudioCleanup): an `AudioCleanup` step with a hosted target (`IsolateVoice`,
`RemoveMusic`) is checked by WL006 for an `AudioIsolation` provider (token `capability.audioIsolation`, the step's
`providerId` or the default row) and by WL015 for `allowSourceMediaEgress` (message variant `WL015.cleansAudioWithAi`).
A local target raises neither.

ImageGenerate note (WL006 and WL015 for ImageGenerate): an `ImageGenerate` step is checked by WL006 for an `ImageGeneration`
provider (`mode` Generate) or an `ImageEdit` provider (`mode` Edit), using the step's `providerId` or the default row. In
Edit mode WL015 fires while `allowSourceMediaEgress` is off (message variant `WL015.editsPictureWithAi`, "sends the
picture to an outside AI service to edit it"); Generate sends no source and never asks for it. The plan feature `audio_cleanup` is NOT a linter finding: it is refused at run time by the
step (`PLAN_NOT_ENTITLED`, before the consent check and before anything is uploaded). An AudioCleanup source that
is an audio file is also accepted from a `StepResult` of an earlier run (`.wav`, `.mp3`, `.m4a`, `.aac`, `.ogg`, `.flac`).

## WL016 silent-output

When it fires: the brief asks for a narrated film (or names a voice) and the workflow has no Voiceover step.

Why: the result would be a silent film. Severity: warning. The fix is the closed patch `addVoiceover`.

How to fix: add a voiceover step.

## WL017 length-unreachable

When it fires: the length you asked for is longer than your source footage adds up to.

Why: the film cannot be that long. Severity: warning. The total length of the footage is not stored for every file, so
when it is unknown the rule is silent. The second clause of the design, a compile with no length target wired, is not
checked yet: the run's length reaches the compile through the run brief once the engine reads it.

How to fix: ask for a shorter film, or add more footage.

## WL018 manifest-orphan

When it fires: an app input points at a field, a file slot or a step the workflow no longer has.

Why: the control on the app page would do nothing. Severity: error on the lint endpoints and where a manifest is saved; at a run start it never blocks (a dead control
does not fail a run), and a retry or resume does not compare its snapshot with the live manifest. It reports what the
app manifest validator finds, for the manifest in the request body (a draft) or else the saved workflow's stored one.

How to fix: remove the control, or point it at a step that exists.

## WL019 captions-without-text-source

When it fires: the final cut burns captions but there is nothing to take the text from: captions on, narration off, and
no transcript caption source (`captionSource` Transcript over an analysis step that transcribes the speech; an analysis
with transcription off does not count).

Why: nothing would appear. By default captions are cut from the narration the voiceover actually produced; with the
transcript source they are cut from the words spoken in the footage. Severity: error.

How to fix: turn on the narration for the compile (with a Voiceover step above it), switch the captions to the
transcript of an analysis step that transcribes, or turn the captions off.

## WL020 review-before-final-cut
When it fires: a review step sits before a narration (Voiceover), music (AudioGenerate) or final cut (VideoCompile) step
that runs later in the workflow, and the review's loop target produces the film (an Author, a compile, a render or a
generate step). A review of a script or an edit decision ahead of the voice is a legitimate text review and does not
fire. One finding per review, naming the steps, for example "The review at step 7 runs before
the narration (step 8) and the final cut (step 10), so it checks a silent picture. Move the review after step 10."
Why: the review judges the film it is given. Placed early, it sees a picture without its final voice and mix, so it can
approve or send back the wrong thing. Severity: warning.
How to fix: move the review (and point its loop target at the step it should send back) so it comes after the last
narration, music and compile step. The finding offers no one-click patch: it is a move, not a reference to retarget.
## WL021 voiceover-over-narrated-film
When it fires: a VideoCompile has `enableVoiceover` on, does not set `muteSourceAudio`, and uses `voiceoverMode`
OverMusic (the default) or DuckDialogue, while the film it is built on already carries narration. The film is found from
the compile's analysis step (its sources: StepOutput n or PreviousStepOutput) and counts as narrated when it is a Revise
step, an Agent step with a continued composition, an Agent, Revise or TimelineRender step that runs after a Voiceover
step, or another VideoCompile with its own voiceover. ReplaceDialogue is an informed choice and never fires. Uploaded
files, brief files, generated clips, results of earlier runs, video generation, and an Agent render with no earlier
Voiceover and no continued composition do not fire (the runtime guard in VideoCompile covers those). Example: "The final
cut (step 9) lays a voiceover over the step 6 film, a film that already has its own narration, so the narration would be
heard twice. Turn on muteSourceAudio so only the new voiceover, music and sound effects play, or switch this compile's
voiceover off to keep the film's own narration."
Why: OverMusic keeps the source sound and lays the new voiceover on top, so a film that already speaks is heard twice,
offset by the different line timings. Severity: error. It is not a project-state rule, so the assistant's
ProposeWorkflow refuses it as a structural problem and the run gate counts it like any other error.
How to fix: the finding offers a ChangeSetting fix: turn on `muteSourceAudio` (only the new voiceover, music and sound
effects play); or switch the compile's voiceover off to keep the film's own narration.
## WL022 censor-needs-feature-track
When it fires: a VideoCompile has `censorLabels` and its analysis step (`analysisStepOrder`) tracks one of those labels
only with `method` Segmentation (no feature-method track carries the label; a track with no `method` counts as Feature).
The censor reads feature tracks only, so a segmentation-only label matches nothing and the censor does nothing. Example:
"The censor won't apply to digital speedometer: censoring needs a feature track, and this label is tracked with
segmentation only. Use a region effect on that track instead." One finding per label.
Why: nothing else reports it; the label only shows up as `unmatchedLabels` in the EDL after the render.
Severity: warning, so it reaches the run page and ProposeWorkflow's `findings` (warnings are not project-state, so they
are not in `needsBeforeRunning`, which holds project-state errors only).
How to fix: add a region effect on the same track (the segmentation mask follows the object), or track the label with
method Feature.
## WL026 voiceover-ignores-script
When it fires: a Voiceover step's source kind is Inline (the lines are typed into the step) and an earlier Agent step is
a Scriptwriter, NarrationWriter or NarrationTranslator, so the script that step writes is never spoken. A ScriptScenes
or NarrationPlan source, a PickupPlan source (a pickup step is deliberately its own source), an inline voiceover with no
earlier script writer, and a script writer that comes after the voiceover do not fire. Example: "The narration at step 2
is typed inline even though step 1 writes the lines, so that script is never spoken. Point the voiceover at the script."
Why: the workflow paid for a script it then ignores. A deliberate inline narration is still legal, so this is a warning,
not an error. Severity: warning.
How to fix: point the voiceover at the step that writes the lines (`source.kind: ScriptScenes` with
`scriptwriterStepOrder`, or `NarrationPlan` with `narrationWriterStepOrder`); keep inline lines only for words the user
supplied exactly. The finding offers a ChangeSetting fix (label `WL026.useScript`).

## WL027 avatar-unusable
When it fires: a VideoGenerate step names an `avatarId` whose avatar is not in this project, or whose consent is missing,
withdrawn or past its keep-until date (the consent half of the avatar rule, read through
`IWorkflowLintFacts.ProjectAvatarAsync`; the reference files are hashed only by the engine at run time). An unknown
fact never fires. Example: "The step 1 shows the avatar "Anna presenter", but its consent was withdrawn. The step would
stop." The `avatar` arg is the avatar's own display name, never the consent subject's name. Why: the engine fails such a
step closed (`AVATAR_*`, never "no avatar"). Severity: error.
How to fix: choose an avatar that is ready, or re-attest this one in the Avatars tab (label `WL027.chooseAvatar`).
The avatar step without the permission to send media is WL015's `showsAnAvatar` variant.

Several subjects (see [`design/avatar-objects.md`](design/avatar-objects.md) "Subjects in a video generation step"): a
step may name up to three avatars in `subjects`, people and objects. WL027 judges EVERY one of them (one finding per
unusable avatar), and WL015's `showsAnAvatar` variant fires once for the step whichever way it names them. An avatar of a
kind that is switched off in this deployment (`Avatars:Enabled` for people, `Avatars:Objects:Enabled` for objects) is
treated as not in the project.

Variant `WL027.noAvatarChosen` (run start only): a VideoGenerate step names the avatar's speech (`avatarAudio`) but neither the step
nor the run brief chooses an avatar; the engine refuses it on every attempt, so the run start answers `400 AVATAR_NOT_CHOSEN`.

## WL034 subject-model-unsupported
When it fires: a VideoGenerate step shows an avatar or an object (its own `avatarId` or `subjects`, or the run brief's
avatar on a plain inline step) and the model it resolves to takes no pictures at all: no reference pictures and no first
frame (a text-to-video route). Judged from the model's own declarations (`VideoSubjectModels`, the same tables the engine
reads), with the provider kind read through `IWorkflowLintFacts.VideoGenerationProviderKindAsync`; an unknown provider or
model never fires. Example: "The step 1 shows an avatar or an object, but its model 'alibaba/wan-3.0/text-to-video' takes
no pictures. Use 'alibaba/wan-3.0/image-to-video' instead." Message key `WL034.useSibling` carries the `sibling` arg, the
image-capable model of the same family (`VideoSubjectSiblings`); `WL034.noSibling` (no sibling known) says "Choose a
model that takes pictures." instead. Why: the engine refuses such a step
before anything is charged (`AVATAR_NOT_SUPPORTED_BY_MODEL`) and never changes the model itself. Severity: error.
How to fix: choose a model that takes pictures (label `WL034.chooseModel`): a model with its own reference mode, or any
image-to-video model, which is shown the avatar through one composed first frame.

## App inputs: WL028 to WL033

The app-input rules (docs/design/app-inputs.md 11.2) run only for an app
that uses inputs (an `input` input or a request template in its manifest, or a binding in a step). They wrap the same
pure pieces the run gate uses (`AppInputValueChecker`, `AppInputValueValidator.Evaluate`, `StaticBranchResolver`), so the
linter cannot call a run fine that the gate then refuses. With a brief the values are the run's; without one (the
author's view) they are the defaults. The fix kinds `fillInput` and `chooseModel` are appended to `LintFixKind`.

### WL028 app-input-missing
When it fires: a required input that is shown has no value and no default (only with a brief). Severity: error; the gate
refuses the same run with `APP_INPUT_MISSING`. How to fix: fill in the field (label `WL028.fillInput`).

### WL029 app-input-unfit
When it fires: a value, a default or a choice no longer fits the setting it fills: a wrong type or range, or the bound
config fails its own checks (the target's limits changed since the app was saved). The validator's own sentence follows
the message. Severity: error. How to fix: change the value or the input (label `WL029.changeInput`).

### WL030 model-input-unavailable
When it fires: the model chosen for a model input cannot be used (error, key `WL030`), or, without run values, the model
the input starts with cannot be used (warning, key `WL030.recommended`). Until the model seam lands (lane L4) every
`@model` binding with a value is unavailable. How to fix: choose another model (label `WL030.chooseModel`).

### WL031 branch-skips-step
When it fires: a Conditional decided by inputs alone leaves steps out with these values: one note per such Conditional,
counting the steps that condition alone skips. Example: "When “Lite mode” is on, 3 steps do not run." Severity: info.

### WL032 agent-model-shared
When it fires: a model input names an agent that more than one step uses, so all of them use the chosen model (owner
decision O2). Severity: info.

### WL033 input-range-over-spend-cap
When it fires: a VideoGenerate or AudioGenerate step whose length or takes come from inputs would, at the dearest values
those inputs allow, hold more than its own `maxSpendCredits` (priced as the estimate prices the step). Such a run would
stop at the cap. Severity: warning (for the author). How to fix: narrow the input's range or raise the cap (label
`WL033.narrowRange`).

## Rolling it out

Enforcement is behind one setting, `Workflows:Lint:Enforce`, which is **off by default**.

- Off: the linter still runs when a run starts, in warn-only mode. It writes one log line and counts every error finding
  it would have blocked on (`reelbolt.inference.lint_would_block`, tagged with the rule and `enforced=false`), and the
  run goes on exactly as before. It is bounded in time and fails open: a lint that is slow or throws never delays or
  fails a run start.
- Always, flag on or off, a `400` before anything is held or queued, because the engine would fail the step for certain
  and the person fixes it in one move: a video step with an open source and no video in the run's brief
  (`WL010.openVideoSlot`, above, `VIDEO_SOURCE_NOT_CHOSEN`); a VideoGenerate step that names the avatar's speech while the
  step and the brief choose no avatar (`WL027.noAvatarChosen`, run start only, `AVATAR_NOT_CHOSEN`); a VideoGenerate step
  whose model the video provider it resolves to does not know (`WL006.modelUnsupported`, `VIDEO_MODEL_UNSUPPORTED`; judged
  from the provider's own offline model lookup, unknown for a provider that prices through a network oracle).
  Like every lint finding it is bounded in time and fails open: a lint that cannot answer in its budget lets the run start.
- On: any other `error` finding is a `409` with code `WORKFLOW_LINT_ERRORS` and the findings in the body, before the balance
  is looked at and before anything is held or spent. The plan's resolution and concurrency limits and the profile-build
  estimate gate still answer first, with their own codes. A resume only judges the steps it will run.

Turn it on once the false-positive review is done (below). The metric shows which rules would block in real use.

## Limits

The lint endpoints are bounded: a request body is at most 1 MB, a draft can have at most 200 steps and name at most 500
different files or results (more is a 400 `LINT_TOO_LARGE`), the files a workflow names are looked up in one query,
and a check that takes longer than 5 seconds is stopped with a 503 `LINT_TIMEOUT` and a plain message.

## Reviewing false positives

The test project has a scan utility (`WorkflowLintScan`) that lints a JSON export of real workflows and prints, per
rule, how many workflows it fires on and which carry an error. It reads a file; it connects to nothing. Run it on an
export made with read access:

```
WORKFLOW_LINT_SCAN_EXPORT=/path/to/export.json dotnet test inference/tests/ReelBolt.WorkflowEngine.Tests --filter FullyQualifiedName~WorkflowLintScanTests
```

Run it on your own machine, never in CI: its output quotes real workflow names and step labels.

The export is an array of `{ id, name, steps: [...] }`; a step has the members of a workflow step request in camelCase
plus an optional `agentType`. A scan has no project facts, so it measures the rules that need only the steps (WL001 to
WL005, WL013, WL014, WL015, WL019, WL020, WL021, WL022, WL026), which are the ones that can raise an error on their own.

## What the catalog taught

All 23 built-in templates lint with no errors except one, on purpose: `video-region-effect` ships with the permission
to send footage out switched off, so WL015 fires until the author turns it on, which is exactly what the template's
description tells them to do. Six templates (narrated-story, the pickups, b-roll, voiceover, dub and dailies footage edits) have a voiceover or video generation inside their review loop and show WL013
as a warning (the loop repeats a paid step by design).
