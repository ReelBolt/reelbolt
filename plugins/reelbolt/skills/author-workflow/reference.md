# Author a workflow: reference

All project-scoped tools accept an optional `projectId`; otherwise the project in `context` is used.

## Recipe tools

| Tool | Arguments | Notes |
|---|---|---|
| `ListWorkflowTemplates` | `goal?`, `projectId?` | Read-only. Per recipe: `title`, `tagline`, `category`, `forGoals`, `needs`, `costClass`, `briefFieldsRead`, `suggestedStylePreset`, steps. With a known project also `satisfied` and `missing`. `goal` sorts matches first and sets `matchesGoal`. |
| `GetWorkflowTemplate` | `templateKey` | Read-only. Ready-to-save steps, `appManifest`, `briefBindings` (which steps read each brief field, and fields no step reads) and `availablePatches` (each closed patch and whether it fits). |
| `GetProjectReadiness` | `projectId?` | Read-only, free. Files by kind, design-system status, voices, AI capabilities the workspace has a provider for, plan limits, existing workflows and their recipe. |
| `EstimateWorkflowRun` | `workflowId` or `templateKey` (exactly one), `brief?`, `projectId?` | Read-only, free. Cost class (never a vendor price), usual credit range, what the workspace must have available to start, paid items, one-line `summary`. The estimate is of the plain recipe: patches are not priced. |
| `CreateFromTemplate` | `templateKey`, `brief?`, `patches?` (array of ids), `name?`, `projectId?` | Creates the workflow in one call. Returns `workflowId`, `workflowName`, `projectId`, `projectName`, `recipe`, `patchesApplied`, `stepCount`, `costClass`, `brief`, `needsBeforeRunning` and `hint`. The name is `name` if given, otherwise the recipe title, made unique. Starts no run. A lint error refuses the call with `findings` and writes nothing. |

Closed patch ids, applied in this canonical order whatever order you list them: `swapToRoom`,
`addVoiceover`, `addCaptions`, `addMusic`, `addSfx`, `addColorGrade`, `addGraphics`. A patch is
refused, with a reason, when the recipe has no cut to edit, the feature is already on, the compile is
a stream copy, `addCaptions` has no narration, or the project has no audio files for `addMusic` /
`addSfx`.

### The brief

An object, every field optional, unknown fields refused: `language`, `sourceLanguage`, `lengthSec`
(5 to 600), `format` (`Original`, `Vertical`, `Square`, `Landscape`, `Portrait`), `resolution` (`Auto`,
`720p`, `1080p`, `2160p`), `stylePreset` or `styleId` (one of them), `tone` (`Calm`, `Upbeat`,
`Energetic`, `Serious`, `Playful`, `Inspirational`), `designSystem` (`project` or `none`),
`voice` (`{ "mode": "none" | "narrated", "projectVoiceId": "..." }`) and
`sourceFiles` (`[{ "role": "...", "fileIds": [...] }]`). Every field is optional and unknown fields
are refused. `ExecuteWorkflow` also takes
`briefFileId` (a text file of the same project, up to 64 KiB) as the brief.

Where the workspace has app inputs, the brief also takes `inputs`: the values of the app's own fields,
`{ "<id>": value }` (text, a number, `true`/`false`; a model field takes the `id` of one of the
`modelOptions` `GetWorkflow` lists). A required field left empty (`APP_INPUT_MISSING`), a value that
does not fit (`APP_INPUT_INVALID`) or a model the workspace cannot use (`APP_INPUT_MODEL_UNAVAILABLE`)
is refused before anything is spent. On `CreateFromTemplate`, `brief.inputs` pre-fills the recipe's
fields.

`projectVoiceId` is a voice `id` from `ListProjectVoices` (only a `selectable` voice narrates). To use
a voice the project does not have yet, find it with `SearchVoiceLibrary` (`q?`, `language?`, `page?`;
Fish Audio only) and add it with `AddLibraryVoice` (`vendorVoiceId`, `displayName?`), which returns
the new voice's `id`. Adding a library voice is free and needs no consent.

Sound effects: `SearchSoundEffects` (`query?`, `category?`, `maxDurationMs?`, `limit?`) lists the built-in
library by `name`; `AddSoundEffect` (`name`, or `starterSet: true` for about 24 varied sounds) copies it into
the project as an ordinary audio file that `OfferSfxClips` and the timeline can use. Free, no credits.

### `needsBeforeRunning`

Errors about the project's state rather than the graph: a missing provider, voice, file, design
system, or a consent the customer gives in the app. They are reported by `ValidateWorkflow`,
`SaveWorkflow` and `CreateFromTemplate` and do not make `valid` false or block the save.
Relay each one before offering to run.

## Custom graphs

| Tool | Writes? | Notes |
|---|---|---|
| `ListAgents` | no | Source of `agentDefinitionId`. |
| `ListStepSchemas` | no | JSON Schema for every step type's config. |
| `GetStepCatalog` | no | `stepType?`. Generated from code: purpose, cost class, needs and the lint rule that checks each, what it produces, brief fields read, pairings, common mistakes; one step type also returns its config schema. |
| `ValidateWorkflow` | no | Steps, optional `projectId` and `brief`. Returns `valid`, `errors`, `findings` (`ruleId`, `severity`, `stepOrder`, `message`, `fix`) and `needsBeforeRunning`. Warnings and notes are worth fixing or telling the user. |
| `SaveWorkflow` | writes | Omit `workflowId` for a new workflow. Lint errors about the graph refuse the save with `code: WORKFLOW_LINT_ERRORS` and no change is written. Saving the same steps again answers `alreadyApplied`. |

`GetWorkflow` returns steps in exactly the shape these tools accept: copy and change it rather than
hand-writing JSON.

## Small edits and copies

- `PatchWorkflow`: `workflowId`, `operations` (up to 50). Each operation
  has `op`: `setStepConfig` (with `stepOrder` and `patch`, a JSON merge patch of that step's config;
  `null` deletes a key), `renameStep` (`stepOrder`, `label`) or `setStepField` (`stepOrder`, `field`
  from a fixed list of scalar fields, `value`). One call applies the patch and returns the workflow's
  id and name.
- App page operations of `PatchWorkflow` (where the workspace has app inputs):
  `setAppInput` (`input`: `{ id, type, label, help?, example?, required?, default?, min?, max?, unit?,
  options?, more?, showWhen? }`; type `shortText`, `longText`, `number`, `duration`, `choice`,
  `toggle`, `shape` or `model`) adds or replaces a field; `bindAppInput` (`stepOrder`, `field` from
  `GetStepCatalog`'s `bindableFields`, `inputId`, `mode` `placeholder` with an optional `template`
  containing `{{input.<inputId>}}`, or `value`; `@model` for the step's model) makes a step's setting
  read it; `setAppPage` (`title?`, `tagline?`, `order?`, `requestTemplate?`) and `removeAppInput`
  (`id`). Consent, sending footage out, spend caps and providers can never read a field.
  `SaveWorkflow` takes the whole page as `appManifest` instead.
- `CloneWorkflow`: `workflowId`, optional `name`, optional
  `targetProjectId` (another project you can edit). A workflow that continues a composition or reads
  step results of its source project cannot move to another project.

## Running

`ExecuteWorkflow` arguments: `workflowId`, `brief?`, `userRequest?`, `briefFileId?`,
`regenerateVideoClips?`, `styleId?` / `stylePresetKey?` (one), `outputFormat?`, `outputResolution?`,
`projectId?`, `acceptEstimateCredits?`. It is rate-limited per user and project per rolling hour; a
refusal is a normal result, so tell the user rather than retrying. A resolution above the plan's limit
returns `planGate`. `acceptEstimateCredits` applies only to the "Build brand and product profile"
workflow over MCP: without it the tool refuses with `ESTIMATE_NOT_ACCEPTED` and the estimate; call
again with a figure at least as high as the estimate's high figure only after the user agrees.
