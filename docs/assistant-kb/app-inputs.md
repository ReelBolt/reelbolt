---
title: An app's own fields (app inputs)
kb:
  kind: how-to
---

<!--
How this file is built. The "Settings a field can fill" sections each have a generated block between the
"bindable:begin" and "bindable:end" comments, rendered from StepCatalog.BindableFieldsOf (the [AppBindable]
allowlist on the step configs, inference/src/ReelBolt.Shared/Workflows/AppBindableCatalog.cs). Never edit between
the markers. AppInputsKbDocDriftTests (inference/tests/ReelBolt.WorkflowEngine.Tests/Docs/AssistantKbDocsTests.cs)
fails when a block differs from the render or a step type with bindable settings has no section.
Regenerate with: UPDATE_APP_INPUTS_DOC=1 dotnet test --filter AppInputsKbDocDriftTests
-->

# An app's own fields (app inputs)

Internal background for the assistant. Never quote this page to a customer; customer wording lives in the user guide. App inputs are always on. Every section stands alone.

## Filling an app's fields to change a run

An app (a workflow seen as an app) can have fields of its own that a person fills in on its page before each run: "What happens in the video", a length, a shape, a "Lite mode (cheaper)" switch, the model to use. GetWorkflow lists them under appInputs: each field's id, type, label, whether it is required, its default, its choices, where it is used, and for a model field its modelOptions (each model's id, name, whether it is recommended, its price in words and whether the workspace can use it).

To change what one run makes, fill the fields in: ExecuteWorkflow takes brief.inputs as {id: value}. Text is text, a number is a number, a switch is true or false, a shape is one of 9:16, 1:1, 16:9 or 4:5, a choice is one of its option values, and a model field is the id of one of its modelOptions. A field left out uses its default; a required field with no default must be filled, or the run is refused with APP_INPUT_MISSING before anything is spent. A value that does not fit is APP_INPUT_INVALID; a model the workspace cannot use is APP_INPUT_MODEL_UNAVAILABLE.

Never edit the workflow's steps to change one run: the steps are the app; the fields are what the person chooses. The run card lists each value by the field's label and names a chosen model, and a run that takes a cheaper path is priced on that path only.

## Field types

- shortText: one line, up to 200 characters. longText: a paragraph, up to 7000.
- number: a number between min and max (step, unit). duration: a length in seconds.
- choice: one of its options (value and label); toggle: an on/off switch (onLabel, offLabel).
- shape: the picture's shape, 9:16, 1:1, 16:9 or 4:5.
- model: the model a step uses. Its capability comes from the step (video, music, sound effects, speech, or the picture model of a video analysis) or, for an agent, Chat with agent naming the agent. allowed narrows the list; recommended picks the default.

Every field has an id (a lower-case letter then letters and digits), a label (at most 40 characters), and may have help, an example (shown as a hint, never sent), required, default, more (under More options) and showWhen ({input, equals}: shown only when another field has that value). At most 8 fields are on view and 32 in all.

## Setting up the request page of a custom app

Ask which settings a person would change from run to run, and make only those into fields; keep everything else fixed in the steps. Give each field a plain label and an example. Use a brief field instead when one exists (language, length, format, voice, style): the page already has those.

With ProposePatchWorkflow, in one proposal:
- setAppInput adds or replaces a field: {op:"setAppInput", input:{id, type, label, …}}.
- bindAppInput makes a step's setting read it: {op:"bindAppInput", stepOrder, field, inputId}. A text setting takes a placeholder ({{input.id}} inside the text, mode placeholder, optional template with the rest of the sentence); a whole setting (a length, a shape, a number, @model for the model) takes mode value.
- setAppPage sets the title, the tagline, the order of the fields and requestTemplate: text added to the request of each run, with {{input.id}} placeholders, so the agents read the field.
- removeAppInput removes a field and every use of it.

ProposeWorkflow takes the whole page as appManifest instead. Consent, spend caps and providers can never become fields: the save refuses them. The permission to send media to AI services is the app's own field, allowMediaEgress: the app adds it where a step sends media, the save refuses a hand-written one (and any binding of a step's own sending switch), and a run gives it by setting brief.inputs.allowMediaEgress to true. In chat that run always asks the user on its card, also in auto mode; over MCP it is taken only after the user allowed connected tools to send media in Settings. Turning on a step's saved switch (allowSourceMediaEgress) is a step edit, not an app field: ProposeWorkflow and ProposePatchWorkflow may set it only when the user asks, in chat through an Apply card that names each step, never over MCP (a connected tool can allow media for one run only).

## Worked examples

A prompt app: a VideoGenerate step whose video prompt is the field "What happens in the video" (longText, required, example "A barista pours latte art"): setAppInput, then bindAppInput on inlinePrompt with template "{{input.videoPrompt}} Medium close-up, steady camera.".

A model choice: setAppInput {id videoModel, type model, label "Video model", more true}, then bindAppInput field @model on the VideoGenerate step. People then pick among the workspace's video models, recommended first, each with its price in words.

A cheaper path: a toggle "Lite mode (cheaper)" and a Conditional step whose condition is input.liteMode, true to the cheap steps and false to the full ones; a second Conditional with no condition and "end" as its true branch finishes the cheap path. The run is priced on the chosen path only. The Avatar clip recipe is built this way.

A recipe's fields can be pre-filled when the workflow is created: ProposeCreateFromTemplate's brief.inputs become the fields' defaults.

## A switch or a choice that sets other settings

A toggle or a choice can fill a setting of another kind by listing what each of its values writes: bindAppInput with mode value and value:{"true": 800, "false": 0} (a toggle's keys are true and false, a choice's keys are its option values). A value left out of the list keeps the step's own setting, so value:{"true": "Transcript"} changes the setting only when the switch is on. One field can be bound to several settings, even on different steps, with one bindAppInput each: a "Captions" switch can set the analysis step's transcription to Required and, on the compile step, enableCaptions (the switch itself) and captionSource (value:{"true": "Transcript"}). Every listed value must be one the setting takes; the save refuses anything else.

An ImageGenerate exact size works this way: a "Size" choice (Square, Wide) can set outputWidth and outputHeight with one bindAppInput each, value:{"Square": 1024, "Wide": 1920} and value:{"Square": 1024, "Wide": 1080}. A number field can bind each one directly instead. Set both or neither.

A setting inside a list is bound on one item, named with its index: field regionEffects[0].effect (a choice of Blur or Pixelate, for example). The item must already be in the step.

## Words spoken exactly, and a talking avatar's picture model

On a Voiceover step whose source is Inline, field @spokenText (mode value) with a text field speaks the person's words exactly: each paragraph (separated by a blank line) is one spoken line, in order, one after the other from the start. At most 40 paragraphs. Use it when the words must not be rewritten; a writer agent in between may change them.

On a VideoGenerate step that animates an avatar with speech (avatarAudio set), field @stillModel binds a model field of capability ImageEdit: the picture model that makes the still the clip animates. It must be a fal.ai instruction-edit model that takes several pictures; GetWorkflow's modelOptions marks the others as not usable.

## Settings a field can fill: VideoGenerate

<!-- bindable:begin VideoGenerate -->
Generated from the step catalog; do not edit by hand.

- `inlinePrompt` (Video prompt): text, through a {{input.id}} placeholder; type shortText or longText; at most 7000 characters.
- `resolution` (Resolution): whole value (appInputs); type choice; the brief's resolution field decides it when the page has that field.
- `durationSeconds` (Clip length): whole value (appInputs); type duration or number; from 1 to 60.
- `ratio` (Shape): whole value (appInputs); type shape or choice; one of 9:16, 1:1, 16:9, 4:5; the brief's format field decides it when the page has that field.
- `takes` (Takes): whole value (appInputs); type number or choice; from 1 to 4.
- `maxClips` (Most clips): whole value (appInputs); type number or choice; from 1 to 64.
- `maxSpendCredits` (Most credits to spend): whole value (appInputs); type number; from 1 to 100000.
- `@model` (Model): whole value (appInputs); type model; a VideoGeneration model.
- `@stillModel` (Picture model): whole value (appInputs); type model; a ImageEdit model.
<!-- bindable:end VideoGenerate -->

## Settings a field can fill: VideoAnalyze

<!-- bindable:begin VideoAnalyze -->
Generated from the step catalog; do not edit by hand.

- `transcription` (Transcribe the speech): whole value (appInputs); type choice; one of Off, Optional, Required.
- `emitOverlayPlacements` (Room for titles and callouts): true or false (a switch, or a choice mapping its values).
- `offerMusicTracks` (Offer the project music): true or false (a switch, or a choice mapping its values).
- `offerSfxClips` (Offer the project sound effects): true or false (a switch, or a choice mapping its values).
- `trackObjects[].label` (What to follow): text, through a {{input.id}} placeholder; type shortText; at most 160 characters.
- `sceneDetection` (Scene detection): whole value (appInputs); type choice; one of Standard, Sensitive, Soft.
- `maxDescribedEvents` (Most looks at the picture): whole value (appInputs); type number; from 0 to 40.
- `sfxOnFootage` (Sounds on filmed footage too): true or false (a switch, or a choice mapping its values).
- `@visionModel` (Picture model): whole value (appInputs); type model; a Vision model.
<!-- bindable:end VideoAnalyze -->

## Settings a field can fill: VideoCompile

<!-- bindable:begin VideoCompile -->
Generated from the step catalog; do not edit by hand.

- `enableGraphics` (Titles and callouts): true or false (a switch, or a choice mapping its values).
- `enableMusic` (Background music): true or false (a switch, or a choice mapping its values).
- `programFadeOutMs` (Fade out at the end): whole value (appInputs); type toggle or number; from 0 to 10000.
- `programAudioFadeOutMs` (Fade the sound out at the end): whole value (appInputs); type toggle or number; from 0 to 10000.
- `transitionPolicy` (Transitions): whole value (appInputs); type choice; one of Off, AudioOnly, Auto, Expressive, Editor.
- `enableColorGrade` (Colour look): true or false (a switch, or a choice mapping its values).
- `enableSfx` (Sound effects): true or false (a switch, or a choice mapping its values).
- `enableGeneratedClips` (AI extra shots): true or false (a switch, or a choice mapping its values).
- `enableVoiceover` (Voice-over): true or false (a switch, or a choice mapping its values).
- `enableCaptions` (Captions): true or false (a switch, or a choice mapping its values).
- `captionStyle` (Caption look): whole value (appInputs); type choice; one of Subtitle, Karaoke, Punch, Bold.
- `enablePickups` (Fix flubbed lines): true or false (a switch, or a choice mapping its values).
- `muteSourceAudio` (Mute the clips' own sound): true or false (a switch, or a choice mapping its values).
- `beatSync` (Cut on the beat): whole value (appInputs); type choice; one of Off, Beat, Bar.
- `musicGainDb` (Music volume): whole value (appInputs); type number or choice; from -40 to 6.
- `regionEffects[].target` (What to hide): text, through a {{input.id}} placeholder; type shortText; at most 120 characters.
- `regionEffects[].effect` (Effect): a whole value of one item, through an appInputs entry naming the item (for example regionEffects[0].effect); type choice; one of Blur, Pixelate, Highlight, Spotlight, ColorPop, Outline, Flash, Glow.
- `captionSource` (Caption text from): whole value (appInputs); type choice; one of Voiceover, Transcript.
- `cutOnEvents` (Cut on the action): true or false (a switch, or a choice mapping its values).
- `enableMotion` (Zooms and camera moves): true or false (a switch, or a choice mapping its values).
<!-- bindable:end VideoCompile -->

## Settings a field can fill: Voiceover

<!-- bindable:begin Voiceover -->
Generated from the step catalog; do not edit by hand.

- `@model` (Model): whole value (appInputs); type model; a SpeechSynthesis model.
- `@spokenText` (Words to speak): whole value (appInputs); type shortText or longText; at most 7000 characters.
<!-- bindable:end Voiceover -->

## Settings a field can fill: ThumbnailGenerate

<!-- bindable:begin ThumbnailGenerate -->
Generated from the step catalog; do not edit by hand.

- `atTimeSec` (Moment): whole value (appInputs); type number or duration; from 0 to 86400.
- `overlayText` (Text on the cover): text, through a {{input.id}} placeholder; type shortText; at most 90 characters.
<!-- bindable:end ThumbnailGenerate -->

## Settings a field can fill: ExtractFrame

<!-- bindable:begin ExtractFrame -->
Generated from the step catalog; do not edit by hand.

- `at` (Which frame): whole value (appInputs); type choice; one of Time, Last, First.
- `atSec` (At second): whole value (appInputs); type number or duration; from 0 to 86400.
<!-- bindable:end ExtractFrame -->

## Settings a field can fill: AudioGenerate

<!-- bindable:begin AudioGenerate -->
Generated from the step catalog; do not edit by hand.

- `prompt` (What it sounds like): text, through a {{input.id}} placeholder; type shortText or longText; at most 4000 characters.
- `durationSec` (Length): whole value (appInputs); type duration or number; from 0.5 to 300.
- `takes` (Takes): whole value (appInputs); type number or choice; from 1 to 4.
- `maxSpendCredits` (Most credits to spend): whole value (appInputs); type number; from 1 to 100000.
- `@model` (Model): whole value (appInputs); type model; a MusicGeneration or SoundEffectGeneration model.
<!-- bindable:end AudioGenerate -->

## Settings a field can fill: AudioCleanup

<!-- bindable:begin AudioCleanup -->
Generated from the step catalog; do not edit by hand.

- `target` (What to clean): whole value (appInputs); type choice; one of VoiceBasic, Hum, Wind, Hiss, Echo.
<!-- bindable:end AudioCleanup -->

## Settings a field can fill: ImageGenerate

<!-- bindable:begin ImageGenerate -->
Generated from the step catalog; do not edit by hand.

- `prompt` (What to draw): text, through a {{input.id}} placeholder; type shortText or longText; at most 4000 characters.
- `instruction` (What to change): text, through a {{input.id}} placeholder; type shortText or longText; at most 4000 characters.
- `itemList` (List of items (one per line)): text, through a {{input.id}} placeholder; type shortText or longText; at most 4000 characters.
- `resolution` (Picture resolution): whole value (appInputs); type choice; one of 1K, 2K, 4K.
- `outputWidth` (Output width (px)): whole value (appInputs); type number; from 64 to 8192.
- `outputHeight` (Output height (px)): whole value (appInputs); type number; from 64 to 8192.
<!-- bindable:end ImageGenerate -->
