# Workflow apps

Every workflow is also an app: a title, a handful of inputs, a Run button, a cost badge and a result. The graph of steps
is the same data behind an "Advanced" switch. Nothing is copied, so an app can never drift from the workflow it presents.

## What an app is

An app is the presentation of one workflow. It shows a title and a one-line description, up to eight inputs from top to
bottom, what the run costs in words, and which result the run page leads with. A workflow with no hand-written
presentation still gets one: the app is worked out from the workflow's steps, so every existing workflow and every
template is an app today, with nothing to migrate.

## The app manifest

The manifest is the optional, hand-written part. It is one JSON object stored on the workflow (`version` 1, names in
camelCase):

| Member | Meaning |
|---|---|
| `title` | Up to 60 characters. Falls back to the workflow's name. |
| `tagline` | Up to 140 characters, one line under the title. |
| `category` | `promo`, `footage`, `brand` or `utility`; the gallery filter. |
| `suggestedStylePreset` | A built-in style key. Shown as a chip and pre-selected, never forced. |
| `inputs` | Ordered, at most 8 (see below). |
| `result` | `{ "kind": "video" \| "image" \| "audio" \| "files" }`: the output the run page leads with. |
| `advanced` | `{ "allowGraph": false }` hides the graph switch for a locked, curated app. |

An input has a `kind`:

- `brief`: one field of the run brief (`language`, `sourceLanguage`, `lengthSec`, `format`, `resolution`, `stylePreset`, `tone`, `designSystem`, `voice`, `overlayCardAudio`, `avatar`; see [`run-brief.md`](run-brief.md); `avatar` takes no default or choices). The `label`, an optional `default` and optional `choices` are presentation only; the value is still checked by the run brief's own rules.
- `files`: a slot filled from the project's files by `role` (`video`, `code`, `music`, `sfx`, `image`, `document`, `insertContent`), with optional `min` and `max`.
- `notes`: the free-text request box.

Unknown members are refused, so a typo is an error and never silently ignored.

From manifest `version` 2 (always on; design in [`design/app-inputs.md`](design/app-inputs.md), field reference in "Typed input reference" below) an
input may also be of kind `input`: a typed question of the app (`shortText`, `longText`, `number`, `duration`, `choice`,
`toggle`, `shape`, `model`) bound into step settings by `{{input.id}}` placeholders or a step's `appInputs` map, read by a
Conditional as `input.id`, or composed into the request by the manifest's `requestTemplate`. Every input may set `more`
(under "More options"); at most 8 are shown and 32 in all. The person's values travel in `brief.inputs`
([`run-brief.md`](run-brief.md) "App inputs"): checked and dry-run bound at the run gate, bound onto the run's snapshot by
the engine. A branch an input decides is priced, held and linted on its own (`WL031`); the branch target `"end"` finishes
the run there. A `model` input lets the person pick the model of a VideoGenerate, AudioGenerate or Voiceover step (bound
through `appInputs` key `@model`) or the Chat model of an agent (`agent`; every step of that agent in the run): anyone who
may run the app may pick any model the workspace can select for the job, and the author can narrow the list with
`allowed` and set `recommended`. The page reads the choices from `GET .../workflows/{id}/app/inputs/{inputId}/options`;
the run gate and the engine re-check the choice, and the estimate and holds price it.
A run's values come back on the run list and the run (`appInputs`: each field labelled by the run's own page, a model by
name, never an id), and "Change one thing and re-run" changes them as a linked change request
([`change-requests.md`](change-requests.md) "Changing an app's fields (app inputs)"). Customer guide:
[`user-guide/workflows/app-inputs.md`](user-guide/workflows/app-inputs.md).

## Typed input reference

App inputs are always on (the `Workflows:AppInputs:Enabled` switch was removed 2026-11-10). A manifest that declares any
`input`-kind input, a `requestTemplate` or a `more` member is written at `version` 2. Run starts take the values as
`brief.inputs` (`{ "<id>": value }`) on every path: the app's run page, the editor Run dialog, the public API run
start, retry and resume, and the assistant. An app with typed inputs shows no free brief box in the editor Run dialog.

### Input members (kind `input`)

| Member | Meaning |
|---|---|
| `id` | Required. `[a-z][A-Za-z0-9]{0,31}`, not a brief field name, unique in the manifest. |
| `type` | Required. One of the types below. |
| `label` | Up to 40 characters, plain words. |
| `help` / `example` | Up to 160 / 300 characters. `example` is placeholder text and is never sent. |
| `required` | Run refused with `APP_INPUT_MISSING` when empty and no `default`. |
| `default` | Typed like the value; must satisfy every setting the input is bound to. |
| `maxLength` | Text types. Capped by the bound settings. |
| `min`, `max`, `step`, `unit` | `number` and `duration`. `unit` is `seconds`, `takes` or `percent`. |
| `options` | `choice`: `[{ "value", "label", "hint" }]`, at most 12. |
| `choices` | `shape` (a subset of the four shapes) and `duration` (chips). |
| `onLabel` / `offLabel` | `toggle` wording. |
| `capability`, `job`, `allowed`, `recommended`, `agent` | `model` inputs (see below). |
| `more` | `true` puts the input under "More options". Absent: models and fine-tuning go under it, everything else is shown. |
| `showWhen` | `{ "input": "<id>", "equals": value }`. See below. |
| `labelKey`, `helpKey`, `exampleKey` | Translation keys for built-in recipes. |

at most 8 inputs are shown at first and 32 in all (`APP_MANIFEST_INVALID` beyond that).

### Input types

| `type` | Value | Notes |
|---|---|---|
| `shortText` | string | One line, up to 200 characters. |
| `longText` | string | Up to 7000 characters. |
| `number` | number | Range must lie inside every bound setting's range. |
| `duration` | integer seconds | |
| `choice` | one of `options[].value` | |
| `toggle` | `true` / `false` | |
| `shape` | `9:16`, `1:1`, `16:9` or `4:5` | |
| `model` | the id of a model the workspace can use | `capability` is one of `VideoGeneration`, `ImageEdit`, `MusicGeneration`, `SoundEffectGeneration`, `SpeechSynthesis`, `Chat`; `job` narrows video (`textToVideo`, `imageToVideo`, `reference`, `talkingHead`, `avatarStill`); a `talkingHead` model input lists `LipSync` models, not `VideoGeneration` ones; `allowed` (at most 12) narrows the list; `recommended` is listed first; `agent` (Chat only) overrides that agent's model for the run. |

### Bindings

An input does nothing until a step uses it. Only settings marked bindable can be bound
(`bindableFields` on `GetStepCatalog` and on `GET .../workflows/{id}/app`).

- **Placeholder.** A bindable string setting may contain `{{input.<id>}}`, mixed with fixed text.
- **Whole value.** A step's `appInputs` map sends an input into a setting: `{ "durationSeconds": "clipLength", "@model": "videoModel" }`. `@model` is the step's model; on a talking-head step (avatar audio) it is a lip-sync model, a `LipSync` capability row, and an older manifest that names `VideoGeneration` there is read as `LipSync`. The authored value stays as the fallback when an optional input is left empty.
- **Value table.** An entry may be `{ "input": "<id>", "values": { "true": 800, "false": 0 } }`: a `toggle` or `choice` lists what each value writes; a value left out keeps the authored setting. One input may bind several settings on several steps.
- **Item path.** `regionEffects[0].effect` binds one item of a list.
- **Special targets.** `@spokenText` (Voiceover `Inline`: one line per paragraph) and `@stillModel` (the `ImageEdit` model of a talking head).
- **Conditions.** A Conditional step reads `input.<id>`; a branch may target `"end"`. A branch decided by inputs is priced, held and linted on its own.
- **Agents.** Agents never read an input directly. The manifest's `requestTemplate` (up to 1000 characters, `{{input.<id>}}` placeholders) composes the request text they see; the person's notes follow it.

### showWhen

`showWhen` names one other `toggle` or `choice` input and a value. It only hides the field: while hidden, a required input
counts as not required and its value is dropped. The hidden input must not be needed by the branch that runs while it
is hidden, and only one level is allowed (a conditional input cannot control another).

### The video apps and old template keys

`edit-my-video`, `languages` and `effects-on-a-clip` (`VideoAppTemplates`) are the only footage apps in the gallery. Each extra
of "Edit my video" sits on a branch an input-decided Conditional leaves out, and the compile's `enable*`, `beatSync`, `cutOnEvents`
and the analysis' `offer*` flags are bound from the same inputs (they are marked bindable; consent, egress and spend caps never are).
The 18 earlier footage template keys are hidden aliases: creating from one makes the app with the matching fields preset, and a
workflow already made from one keeps running from its own steps. The assistant's recipe patches do not apply to these apps: the
fields replace them.

### Lite mode

Lite mode is the pattern for a cheaper variant, not a separate feature. Declare a `toggle` (for example id `liteMode`,
label "Lite mode (cheaper)", default off), branch on it with a Conditional whose condition is `input.liteMode`, and
use `showWhen` to show the inputs that belong to one side only.

```json
{
  "version": 2,
  "title": "Avatar clip",
  "inputs": [
    { "kind": "input", "id": "liteMode", "type": "toggle", "label": "Lite mode (cheaper)", "default": false },
    { "kind": "input", "id": "whatTheySay", "type": "longText", "label": "What they say", "required": true, "maxLength": 600 },
    { "kind": "input", "id": "clipShape", "type": "shape", "label": "Shape", "default": "9:16" },
    { "kind": "input", "id": "videoModel", "type": "model", "label": "Model", "capability": "VideoGeneration",
      "job": "talkingHead", "more": true, "showWhen": { "input": "liteMode", "equals": false } }
  ]
}
```

A step then binds these with `appInputs`, for example `{ "ratio": "clipShape", "@model": "videoModel" }`.

### Checks at save and at run

The save validator refuses an unknown member, a binding on a setting that is not bindable, an undeclared input, a value
outside a bound setting's range and an input used in a branch it hides from (`APP_MANIFEST_INVALID`). The run gate
(`AppInputValueValidator`, before any credit is held) answers `400` with `APP_INPUT_MISSING`, `APP_INPUT_INVALID`,
`APP_INPUT_MODEL_UNAVAILABLE` or `APP_INPUT_MODEL_NOT_IN_PLAN`, and `inputId` names the field. The linter rules are
`WL028` to `WL033` ([`workflow-linter.md`](workflow-linter.md)).

## A b-roll film's settings as app inputs

Bindable on a `VideoGenerate` step: `maxClips` (number 1..64, "Most clips"), `takes`, `@model`; on a `VideoCompile` step:
`beatSync` (choice Off/Beat/Bar), `muteSourceAudio` (toggle), `enableMusic` (toggle), `musicGainDb`. Also bindable:
`maxSpendCredits` ("Most credits to spend", number 1..100000, VideoGenerate and AudioGenerate): the page shows it next to the
estimate. The manifest validator refuses a spend input that has a `showWhen`, sits under "More options" (`more: true`; those two are the only ways the vocabulary hides an input), is a choice or mapped switch, has no label, no default
or no maximum, so an author cannot hide a high cap in a default. It only sets the step's own limit; credits, the person's daily
limit and the operator's VideoGeneration daily/global caps still apply, and the estimate, the hold and the 402 price the bound
`maxClips`, `takes`, model and cap, so a bigger run is refused up front. Not bindable on purpose: `allowSourceMediaEgress` (consent is given only in the app UI) and
`musicTrackProjectFileId` (ids are never bindable; the music bed is a `files` input of role `music`, which the compile and the
music planner read as a project music file, with `enableMusic` as the switch). A music switch on a `StreamCopy` or
`audioCodec: copy` compile is refused at the run gate (and at save when `enableMusic` is true) with `MUSIC_REQUIRES_REENCODE` /
`MUSIC_REQUIRES_AUDIO_REENCODE`, never after the paid shots. "Number of shots": the manifest's `requestTemplate` tells the planner
("Plan at most {{input.mostClips}} b-roll shots.") and the same input is bound to `maxClips` where one clip is one shot.

Applying it to an existing workflow (for example "Reel C3 con b-roll AI"): nothing is written for you. In the editor open the app's
"App inputs" panel and paste `docs/examples/reel-c3-app-manifest.json` (or `PUT` it as `appManifest`), then bind each input on its
step; the assistant does the same with `ProposePatchWorkflow` ops that set the step's `appInputs`:

- VideoGenerate step: `"appInputs": { "maxClips": "mostClips", "maxSpendCredits": "mostCredits", "takes": "takes", "@model": "videoModel" }`
- VideoCompile step (each one that encodes): `"appInputs": { "beatSync": "beat", "muteSourceAudio": "mute", "enableMusic": "music" }`

The app pages keep their own smaller ranges on purpose: "Takes" and the curated "Most extra shots" top out at 4 (the dailies
recipe at 2 takes, so 4 shots x 2 takes fit its 8-clip ceiling and credits cap), because the default credits cap is sized to
that range; in the builder the step settings still take 1..64. The run gate prices the bound step (the clips it may buy, the
upper bound when the plan is not known yet) and refuses with `APP_INPUT_SPEND_CAP_TOO_LOW`, naming the spend input, when the
bound credits cap is below that price, so `BUDGET_EXCEEDED` never comes after the paid analysis steps.

Step orders depend on that workflow's graph; the bindings live in each step config, so they must be added per step.

## What is worked out when there is no manifest

The app is derived from the steps. If a step writes a script or directs the film, the app offers language, length, shape,
narration, style and design. A voice-over step adds narration. A step that takes a project video file adds a clips slot
(`video`). The notes box appears when the workflow asks for a request. Shape and style appear whenever a step cuts, renders or
generates picture. The derived manifest is never saved, so it improves by itself as steps learn to read more of the brief.

A manifest you write wins member by member: whatever you set is used, whatever you leave out is derived. A manifest that
lists `inputs` (even an empty list) replaces the derived inputs as a whole.

## What a manifest may not do

One check is shared by the REST API, the workflow builder and the assistant, so they cannot disagree:

- Every `brief` input must be a field some step of the workflow actually reads. An input nothing reads would be shown and ignored, so it is refused when saved.
- Every `files` role must match a step that can take those files.
- At most 8 inputs, no brief field or file role twice, one notes box.
- Titles, taglines and labels are capped (60, 140 and 40 characters) and may not contain links, markup or control characters. Markup characters and control characters are removed before the check.
- `choices` (at most 12) and `default` must be valid values for the field, for example a length from 5 to 600 seconds, a language tag or `auto`, a built-in style key.
- `suggestedStylePreset` must be one of the built-in styles.

Messages are plain sentences that name the input.

## Editing a workflow that has an app

Changing the steps of a workflow must not leave an exposed input pointing at nothing. If an edit would remove the last
step that reads an exposed input, the save is refused with a message naming the input (check `WL018`, manifest-orphan).
Remove the input from the manifest in the same save, or keep a step that uses it. The pre-run check reuses the same
rule, so a workflow that slipped through (for example by an older edit) is reported before it runs.

## Cost badge

Every app and recipe carries one of four badges, worked out from its steps and never typed by hand:

| Badge | When |
|---|---|
| No credits | No step spends credits (processing time is still charged). |
| Uses credits | At least one step uses an AI model. |
| Uses credits and a voice | The workflow also generates a voice-over. |
| Uses credits and AI shots | The workflow also generates video or audio; this spend is capped per run. |

Generated shots outrank a voice, which outranks plain credits. A price in numbers is shown on the run page, not on the card.

## The gallery

`GET /api/v1/projects/{projectId}/workflow-apps` returns, in one request, everything the gallery needs:

- **Your apps**: each workflow with title, tagline, category, cost badge, number of inputs, its last run (status and finish time) and where it came from (a template key, or `custom`). Newest run first.
- **Recipes**: every template the project may start from, with a fit. `ready` means the project has what the recipe needs. `needs` lists what is missing, each with the action that fixes it (`add-files` for clips or code, `build-profile` for the brand profile, `connect-provider` for a voice or video-generation provider).

Fit is worked out from the project's files (video, audio, images, code) and the providers its organization has, using the
same facts the assistant's project-readiness answer uses.

## Reading one app

`GET /api/v1/projects/{projectId}/workflows/{id}/app` returns the effective manifest of one workflow: your members over
the derived ones, with the title and tagline filled in, plus the cost badge, the source and whether you wrote a manifest.

## Starter manifests on templates

Templates can ship a hand-written manifest, copied onto the workflow when the template is applied. Applying a template
never changes the template's version because of a manifest. Templates without one use the derived manifest. Today
ten ship one: the three promo templates from your code (plain, with your brand profile, lighter, and with AI
backgrounds), "Highlight edit from footage" and its editing-team, music, narration and AI-extra-shots variants, and
"Green-screen insert". The footage ones expose a video slot ("Your clips") filled from the project at run time; the chosen clips are what the analyze step reads (its open file slot takes the brief's video files). Every curated file slot states a `max` (the validator caps any slot at 50 files). "Green-screen insert" exposes only its clip: the content to place is named in the workflow itself, so the page does not offer a second file slot for it.

## Who created a workflow

Each workflow records how it was made: `user` (the builder or the API), `assistant` (the assistant proposed it and you
approved) or `template` (applied from a recipe). A workflow the assistant made also records the chat it was made in, so a
run can be traced back to the conversation. That link is cleared if you delete the chat or your account.

## The permission to send media on the app page

A step that sends the customer's media to an outside AI service needs a permission (`allowSourceMediaEgress` on the
step). An app shows that permission as its own field, **Allow sending your media to AI services**, in a "Permission"
block above Run on the run page and in the editor's Run dialog. The app adds the field itself wherever a step needs it
(it is derived from the steps on every read and never stored in the manifest), names the steps and why, and marks it
required when a step cannot run without it and has its own switch off. Switching it on applies to that one run: the
saved steps do not change. A run that needs it and does not give it is refused before anything is spent
(`400 APP_INPUT_MISSING`, `inputId: "allowMediaEgress"`). For a plan-sourced video step the block says what the permission
may send: a frame of the footage, and, unless the step's planned subjects are off, the pictures of the project's
avatars of people and objects that the planner may pick, which go to the outside AI company that makes the video.
Besides the app, a run may give it through a public API key
with the `media.egress` scope, through MCP when the user allowed connected tools in Settings, and through an assistant
chat card the person approves. Full rules: [`design/app-inputs.md`](design/app-inputs.md) "Permission to send media as an
app field".

## The footage-sharing permission on assistant edits

The step's own saved switch (`allowSourceMediaEgress`) is switched on in the app only. The assistant and MCP never turn
it on, but an edit through them keeps a permission already given on every
step whose own settings, the steps it reads, its position and the workflow's loops are unchanged; only the steps the
edit touches are reset and reported as WL015 in `needsBeforeRunning`. The full rule is in
[`platform-assistant-mcp.md`](platform-assistant-mcp.md#the-footage-sharing-consent-on-workflow-edits).

## Request and response shapes

`CreateWorkflowRequest` and `UpdateWorkflowRequest` each gain one appended member, `appManifest` (the object above,
null by default). On create, a supplied manifest is cleaned and checked against the steps and saved with the workflow. On
update, a supplied manifest replaces the stored one; leaving it null keeps the stored manifest, which is still checked
against the new steps. A refused manifest is a 400 `{ "code": "APP_MANIFEST_INVALID", "message": "...", "errors": ["..."] }`
and nothing is written. Another organization's project or workflow is a 404 on both read endpoints; both need View access.

```json
{
  "name": "Narrated film",
  "steps": [ ... ],
  "appManifest": {
    "version": 1,
    "title": "Narrated film",
    "category": "promo",
    "inputs": [
      { "kind": "brief", "field": "lengthSec", "label": "Length", "default": 60, "choices": [30, 60, 90] },
      { "kind": "notes", "label": "Anything else?" }
    ],
    "result": { "kind": "video" }
  }
}
```

## The web app view

The project's Apps tab (`AppsGallery`, `web/components/workflow-apps/`) reads `GET .../workflow-apps` once and draws "Your apps" and "Start from a recipe". Picking a recipe calls the template provisioning endpoint (`POST .../workflows/templates/{key}/apply`, skip if it exists) and opens the workflow page. A recipe's `Needs` lines link to the fix: `add-files` to the Files tab, `build-profile` to the Brand and product tab, `connect-provider` to the organization's providers page. The web server reads `WORKFLOW_APPS_ENABLED` per request (default on; `false` brings back the plain Workflows tab and workflow page exactly), like `VIDEO_EDITOR_ENABLED`. The run form logic (`useRunForm`: shape suggested by the style, plan caps, voice check, silent-run notice, brief building) is shared by the run confirmation modal and the app run page.

### The app run page

`/projects/{id}/workflows/{workflowId}` is the app run page (`AppRunPage`, `web/components/workflow-apps/`); the step
builder sits behind `?view=advanced` (a "Back to the app" link returns), and "Edit steps (advanced)" is in the page's
overflow menu. With `WORKFLOW_APPS_ENABLED=false` the route is the builder page, as before.

- **Inputs** render from `GET .../workflows/{id}/app` in manifest order: language and spoken language, length (chips when the manifest has `choices`, a stepper otherwise), shape, resolution and style (the split `RunStylePicker`), narration switch plus voice, design (`project` or `none`), file slots and notes. Everything lands in one `brief`; the run is `POST .../execute` with that `brief` and no legacy members.
- **File slots** list the project's files that fit the role (worked out from the file type), start with all of them selected up to the slot's maximum, and upload in one click. The choice is sent as `brief.sourceFiles`. An empty required slot disables Run with "Add at least one video clip".
- **Readiness strip** is `POST .../lint` with the brief (debounced 400 ms) plus what the page already knows (empty slot, no voice service, voice without consent). Each state has an icon and a word ("Fix needed", "Check", "Ready", "Note"), the region is `role="status"` with `aria-live="polite"`. Only an error blocks Run; Run is `aria-disabled` (not `disabled`), stays in the tab order and links the reason with `aria-describedby`. A fix button is shown when the finding has a `fix`: `uploadFiles` opens the upload, `chooseVoice` focuses the voice picker (or opens the project's Voices tab), `approveDesignSystem` opens the Brand and product tab, `changePlanOrChoice` opens plans, `chooseProvider` opens the providers page, and every fix that edits the graph (`addStep`, `retargetReference`, `changeSetting`, `allowEgress`, `removeReference`) opens the Advanced view.
- **Price** is `GET .../estimate`, requested with the resolution only: language, length, style, narration and voice do not change the numbers and never refetch. The button reads "Run · about N credits", the range sits below ("usually A to B", "plus up to N for AI shots"), a free app says "No credits used", and the per-step breakdown is behind "Why this price?". A failed estimate says so and leaves Run available (the server's 402 stays the authority); a shortfall disables Run with "About 40 credits needed, you have 12." and a link to add credits; a plan refusal shows the server's sentence, preselects Auto resolution and stays disabled until a choice changes.
- **Result** leads with the video of the newest run: plain-language progress while it runs (Stop replaces Run), a Failed banner with the plain reason and Retry / Resume (the same calls as the run page), then Download, "Edit in the editor" (only when `VIDEO_EDITOR_ENABLED`), "Change one thing and re-run" (the form takes the previous run's `brief` back) and "Ask the assistant to change something" (opens the assistant with the run in context). Apps whose result is not a video link to the run.
- **Phone** (under 768 px): one column, file slots as a button that opens a bottom sheet, a sticky bottom bar (safe-area padding) with the one-line status, the price and a full-width Run of at least 48 px; when an error blocks, the button reads the fix ("Add a clip") and does it. Segments are 44 px.
