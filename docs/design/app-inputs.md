# App inputs: settings, prompts, models and branches on the app page

Status: **COMPLETE: decided (owner, 2026-11-06); lanes L1 to L10 built, follow-up F1 built** (sections 18 to 27; what is
left for the owner is listed in 26 "Deferred across lanes", with the F1 items struck through). **Switch removed 2026-11-10:** app inputs are always on. Written 2026-11-06 against `origin/master` `39cde92af`.
> **Switch removed 2026-11-10.** `Workflows:AppInputs:Enabled` (env `WORKFLOW_APP_INPUTS_ENABLED`) and the `appInputsEnabled` field of `GET .../workflows/{id}/app` are gone. Typed app inputs are the only path: the old run page with a notes-only brief and `ExecuteWithInputModal` are removed, the editor Run dialog shows the typed inputs and no free brief when the app has typed inputs, derived manifests use the same path, and the public API run start accepts `inputs`. Every "off", "dark" and "behind the switch" passage in the lane notes below is history, not current behaviour. The manifest reference is in [`../workflow-apps.md`](../workflow-apps.md) "Typed input reference".

It extends the workflow apps programme ([`workflow-apps.md`](workflow-apps.md), customer-facing contract in
[`../workflow-apps.md`](../workflow-apps.md), brief in [`../run-brief.md`](../run-brief.md), linter in
[`../workflow-linter.md`](../workflow-linter.md)) and replaces its deferred `step` input kind and open question Q8.
Every decision below has a recommendation; building follows the recommendation unless the product owner says otherwise.

Abbreviations: `SH` = `inference/src/ReelBolt.Shared`, `API` = `inference/src/ReelBolt.Inference.Api`,
`ENG` = `inference/src/ReelBolt.WorkflowEngine`, `WEB` = `web`. Symbols are cited by name, not line.

## 1. The request in one paragraph, and what it means

The product owner asked: in a recipe or a custom app, every setting that matters for that recipe must be fillable from
the app's request page, with placeholders inside the settings (for example the prompt of a generated video); the
assistant must be able to set up such request pages and inputs easily; the experience must be as simple as possible.
Two additions followed: (a) the person must be able to pick, on the app page, **the model** a step uses (video, image,
avatar video, music and sound effects, voice, chat agents), with a relative price hint and a recommended default;
(b) inputs must drive **Conditional** steps, so one app can offer a switch such as "Lite mode (cheaper)" that picks
between two branches. The worked example is merging "Spokesperson clip" and "Talking avatar (budget)" into one
"Avatar clip" app.

Read against the code, that is five things:

1. A typed **app input** that is not a run-brief field (a prompt, a number, a choice, a switch, a model).
2. A way to **bind** an input into a step setting: inside a string (a placeholder) or as the whole value.
3. A way for inputs to reach **agents** without editing their system prompts.
4. A way for an input to choose a **branch** of the graph.
5. Authoring, by a person in the builder and by the assistant, with the same validation everywhere.

## 2. What exists today and what is new

| Area | Exists (reused as is) | New in this plan |
|---|---|---|
| Manifest | `AppManifest` v1 (`SH/Workflows/Apps/AppManifest.cs`): `title`, `tagline`, `category`, `suggestedStylePreset`, `inputs` (kinds `brief`, `files`, `notes`, max 8), `result`, `advanced`; stored in `workflow_definitions.app_manifest_json` (engine-owned, API maps it read-only) or derived by `AppManifestDeriver`; checked by `AppManifestValidator`; refused as `APP_MANIFEST_INVALID` | Input kind `input` with typed `type`s; `requestTemplate`; `showWhen`; `more` (progressive disclosure); manifest `version` 2 |
| Values | `RunBrief` (closed field set incl. `avatar`), `RunBriefValidator`, stored in `workflow_executions.run_brief_json` and copied by every run-state copy site (`RunStateCopySiteDriftGuardTests`) | One appended brief member `inputs` (`{inputId: value}`); manifest-aware `AppInputValueValidator` |
| Reaching steps | `RunBriefOverrides` (typed per-field overrides), `[RUN BRIEF]` block from `RunBriefRegistry`, `VideoSourceKind.BriefFiles` | `AppInputBinder` (pure, `SH`): placeholders and whole-value bindings applied to the run snapshot |
| Which settings | `StepCatalog` / `[StepCard]` (generated step cards, `GetStepCatalog`), `StepConfigSchemaExporter` | `[AppBindable]` attribute per config property; `StepCard.BindableFields` |
| Save checks | `Validate()` per config, `StepConfigSaveValidator` (with the inert template key `placeholders`), `StepProviderOwnershipValidator` | Placeholder scan; `appInputs` inert key; binding type checks |
| Linter | `WorkflowLinter`, `StepCompatibilityRules`, `WL000`-`WL027` | `WL028`-`WL033` |
| Run snapshot | `ExecutionSnapshotter` clones the workflow and `AgentSnapshot` (incl. `InferenceProviderId`) at the claim | Binding happens inside the snapshot step |
| Branches | `ConditionalStepExecutor` (`Expression` / `Decision`), `ExpressionEvaluator` extra parameters (today `decision.p_yes`), `WorkflowBranchReach` | `input.<id>` parameters; branch target `"end"`; input-decided branches in estimate, holds and lint |
| Models | `GET /api/v1/inference-providers/selectable?capability=` (`SelectableProviderResponse`, `costIndex` for Chat only), `ModelCapabilityView`, `FalAiVideoCatalog` jobs (`TalkingHead`, reference) | Input type `model`; `AppInputOptionsService`; `costIndex` for every capability; run-time re-resolution |
| Assistant | `ProposePatchWorkflow` ops `setStepConfig`/`renameStep`/`setStepField`, `ProposeCreateFromTemplate` with `RecipePatches`, `GetStepCatalog`, `ExecuteWorkflow` with `brief`, approval cards | Four patch ops for the app page; `appManifest` on `ProposeWorkflow`; `brief.inputs`; no new tool |
| Change one thing | Web "Change one thing and re-run" pre-fills from the previous `brief`; change requests (`step_change_requests`, replay planner) | Re-run reuses cache by input; later a change-request kind `appInputs` |

What Q8 in `workflow-apps.md` proposed (stable `workflow_steps.step_key` before any `step` input) is **not needed**:
bindings live inside the step's own config (they move with the step when steps are inserted or reordered) or name an
agent, never a step order. Q8 is closed by this plan; the `step` input kind is replaced by the `input` kind below.

## 3. Decisions in one table

| # | Decision | Section |
|---|---|---|
| A1 | One new manifest input kind, `input`, with a closed set of `type`s; brief, files and notes inputs stay as they are | 4 |
| A2 | Two binding forms: `{{input.id}}` placeholders inside allowlisted string settings, and whole-value bindings in a per-step `appInputs` member; agents get inputs through a manifest `requestTemplate` composed into the run notes | 5 |
| A3 | Values travel in `brief.inputs` (no new column), are type-checked at queue time against the manifest, and are bound **on the server, onto the run snapshot**, never in the browser | 6 |
| A4 (amended 2026-11-18) | A step spend cap in credits (`VideoGenerate`/`AudioGenerate` `maxSpendCredits`) IS bindable, as a plain always-shown number input with a visible default and a maximum (the validator refuses `showWhen`, a mapped choice, no default or no maximum); it only sets the step's own limit, the credit gate, the daily limits and the operator caps still apply. What is bindable is an allowlist on the config records (`[AppBindable]`); consent, egress, providers (outside the `model` type), ids, step references, timeouts and cache settings are never bindable | 7 |
| A5 | `model` inputs carry a capability id that the server re-resolves at queue and again at the snapshot; options come from the org's selectable models filtered to what the step can use | 8 |
| A6 | Conditions read inputs as `input.<id>`, a reserved namespace no step output can shadow; a condition that reads only inputs is decided before the run, so estimate, holds and lint cover the chosen branch only | 9 |
| A7 | "Spokesperson clip" and "Talking avatar (budget)" become one recipe, "Avatar clip", with a "Lite mode (cheaper)" switch; the two old keys stay as hidden aliases | 9.5 |
| A8 | Step cache: each step keys on the bound values it consumed (appended component, only when non-empty); no `SchemaVersion` bump | 10 |
| A9 | The assistant edits app pages through `ProposePatchWorkflow` ops and `appManifest` on `ProposeWorkflow`; chat stays 116 tools, MCP 78 | 13 |
| A10 | No database migration. Manifest `version` 2 is additive; `"end"` fits the existing string branch columns | 14 |

## 4. App inputs: the model

### 4.1 Shape

An input of kind `input` is a typed question on the app page. It is declared once in the manifest and used in one or
more places (section 5).

```jsonc
{
  "kind": "input",
  "id": "videoPrompt",                 // [a-z][A-Za-z0-9]{0,31}; not a brief field name; unique in the manifest
  "type": "longText",                  // see 4.2
  "label": "What happens in the video",          // <= 40, plain words
  "help": "Describe the scene: who, where, what they do.",   // <= 160, optional
  "example": "A barista pours latte art in a sunny café, slow camera push-in.", // shown as placeholder text, never sent
  "required": true,
  "default": null,                     // typed; must satisfy every target
  "maxLength": 1500,                   // text types; capped by the targets (4.3)
  "more": false,                       // true = under "More options"; absent = the default for its kind and type (4.6)
  "showWhen": null                     // e.g. {"input": "liteMode", "equals": false}; display only, see 4.4
}
```

### 4.2 Types (closed set, append-only)

| `type` | Value on the wire | Control on the page | Extra members |
|---|---|---|---|
| `shortText` | string, one line | text field | `maxLength` (<= 200) |
| `longText` | string, multi-line (prompts, scripts) | textarea with counter | `maxLength` (<= 7000) |
| `number` | number | stepper or slider | `min`, `max`, `step`, `unit` (`seconds`, `takes`, `percent`, none) |
| `duration` | integer seconds | chips (`choices`) or stepper | `min`, `max`, `choices` |
| `choice` | one of `options[].value` | segmented control (<= 4) or select | `options: [{value, label, hint?}]` (<= 12) |
| `toggle` | boolean | switch | `onLabel`, `offLabel` (optional) |
| `shape` | `9:16`, `1:1`, `16:9`, `4:5` | shape chips with pictograms | `choices` (subset) |
| `model` | capability id (GUID) | model cards (section 8) | `capability`, `job`, `allowed`, `recommended` |

Voice, avatar, language, length, style, format, resolution and files stay **brief** inputs (they already have homes,
validators and pickers). An app needs nothing new to offer them.

### 4.3 Limits

- At most **16** inputs in all and at most **8** outside "More options" (the v1 cap of 8 stays the cap of what is
  shown at first). One `requestTemplate`, one notes box.
- A text input's effective `maxLength` is the smallest of: its own `maxLength`, and for each placeholder target, the
  target field's cap minus the length of the template text around the placeholder (computed by the validator, so an
  author cannot declare an input that would overflow a vendor prompt cap).
- A number, duration or choice input's range or options must lie inside every target's own range (the target's
  `[AppBindable]` limits, 7.1). A `default` must pass the same checks.

### 4.4 Conditional display (`showWhen`)

`showWhen` names one other `toggle` or `choice` input and a value. It only hides a field on the page: a hidden input
that is `required` is treated as not required while hidden, and its value is dropped from the request. The validator
requires that the hidden input is not consumed by a step on the branch that runs when it is hidden (section 9.3), so
"hidden but needed" cannot happen. One level only (an input shown by an input that is itself conditional is refused).

### 4.5 Coexistence with the brief

Brief fields keep precedence over everything they already govern (`run-brief.md`, "Precedence"). An `input` may not
bind a setting a brief field overrides when that brief field is exposed on the same app (for example a `duration`
bound to `VideoCompile.targetDurationSec` while `lengthSec` is exposed): the validator refuses it and names the brief
field to use instead. `notes` stays the free "Anything else?" box and is appended after the composed request
(5.4). The derived manifest (`AppManifestDeriver`) never invents `input` inputs; they are authored, curated or set by
the assistant.

### 4.6 Placement: shown or under "More options" (decided by the owner, 2026-11-06)

The essentials are shown and everything else sits under "More options", **and the app creator decides**. `more` is a
member of **every** input, whatever its kind (`brief`, `files`, `notes`, `input`), stored in the manifest, editable in the
builder's App inputs panel (lane L6) and by the assistant (`setAppInput`, lane L8). When `more` is absent the default
is `AppInputPlacement.IsUnderMore`: a `model` input and the brief fields `resolution`, `tone`, `designSystem`,
`overlayCardAudio`, `stylePreset` and `sourceLanguage` go under "More options"; every other input (text, length, shape,
switches, choices, files, notes, avatar, voice, language, format) is shown. The 8-shown / 16-in-all caps of 4.3 count
the effective placement. Any `more` value makes the manifest version 2.

## 5. Bindings: how an input reaches a setting

### 5.1 Placeholders in string settings

A string setting on the allowlist may contain `{{input.<id>}}`, any number of times, mixed with fixed text:

```json
{ "planSource": "Inline",
  "inlinePrompt": "{{input.videoPrompt}} Shot on a gimbal, natural light, {{input.mood}} mood.",
  "durationSeconds": 6 }
```

The sequence `{{input.` is reserved in every config string: outside an allowlisted text field it is refused at save
with a message that names the step and the field. There is no escape syntax (nobody has needed a literal `{{input.`).

### 5.2 Whole-value bindings

A non-string setting is bound by a per-step member `appInputs`, an object mapping the setting's JSON path to an input
id. It is an inert template key (the same treatment `StepConfigSaveValidator.InertTemplateKeys` gives `placeholders`
today), stripped by the binder before any executor reads the config:

```json
{ "planSource": "Inline", "durationSeconds": 6, "ratio": "9:16", "takes": 1,
  "appInputs": { "durationSeconds": "clipLength", "ratio": "clipShape", "@model": "videoModel" } }
```

- Keys are top-level property names of the config (camelCase), or the pseudo-key `@model` (section 8).
- The authored value stays in the config as the **fallback** used when an optional input is left empty, and is what the
  graph builder shows.
- Because the binding lives in the step's own config, inserting, deleting or reordering steps never breaks it. This is
  what removes the need for stable step keys.

### 5.3 Bindings on things that are not configs

| Target | How it is bound |
|---|---|
| A Conditional step's condition | The `ConditionExpression` reads `input.<id>` directly (section 9) |
| An Agent step's model | A `model` input names the agent: `"agent": "<agentDefinitionId>"` (section 8.4) |
| An Agent step's text | Through `requestTemplate` (5.4); never through the system prompt |

Nothing else on `WorkflowStep` (loop targets, iterations, cache mode, input mapping, branch orders) is bindable.

### 5.4 Agents: the request template

Agents already read the run's request (`User Request:` in the step input, `StepExecutionContext`) as data. An app that
wants its agents to see inputs declares one `requestTemplate`:

```jsonc
"requestTemplate": "Make a {{input.videoType}} about {{input.product}} for {{input.audience}}."
```

At queue time the server composes the run's request text as: the template with each value inserted as a quoted span,
then a blank line and `Notes for this run:` plus the person's notes when given (today's `BriefFileResolver`
framing is kept byte for byte). A run of an app with no `requestTemplate` composes exactly today's text. Agents never
get inputs any other way: no system prompt, no custom agent prompt and no `[RUN BRIEF]` sentence is ever built from
an input value.

### 5.5 Escaping and safety: inputs are data

1. **Structure.** The binder edits parsed JSON (`JsonNode`) string values and typed members, never JSON text, so no
   value can add a member or break the config.
2. **Cleaning.** Every text value: Unicode NFC, control, bidi and zero-width characters removed (the same rules as
   `StyleNotesSanitizer` and the design-system `DesignText`), trimmed, then length-checked against 4.3. A value that
   exceeds the cap is a 400 at queue, never silently cut.
3. **Prompts to vendors.** A bound `inlinePrompt` or `AudioGenerate.prompt` still goes through
   `GeneratedPromptSanitizer` in the executor and, when `ContentModeration:Enabled`, the generation-prompt moderation
   before reserve. Nothing about those paths changes.
4. **Prompts to agents.** In the request template each value is wrapped as `"…"` with inner quotes escaped and any
   fence-like token (`[END`, `---`, `User Request:`) neutralised, so a value cannot pose as framing. The step's
   guardrails (`GuardrailScreener`, `instruction_like` screening) see it like any request text.
5. **Expressions.** Conditions receive input values as NCalc **parameters**, never as text spliced into the expression
   (section 9.2).
6. **Never** into: ids, file paths, storage keys, provider refs (the `model` type re-resolves on the server),
   `allowSourceMediaEgress`, consent statements, spend caps, step references.

## 6. Where values live and when they are bound

### 6.1 On the wire and at rest

`RunBrief` gains one appended member, `inputs`, an object of `{inputId: value}`. It rides the existing request
(`POST .../execute`, the assistant's `ExecuteWorkflow`, MCP) and the existing column `workflow_executions.run_brief_json`,
so retry, resume, the recovery sweeper, composition revisions, account export and the run responses carry it with no
new copy site (`RunStateCopySiteDriftGuardTests` gets one assertion that `inputs` is run-start state).

```json
{ "brief": { "avatar": { "avatarId": "…" },
             "inputs": { "liteMode": true, "whatTheySay": "Welcome to Caffè Lume!", "clipShape": "9:16" } } }
```

`RunBriefValidator` checks the shape only (at most 32 entries, id pattern, scalar or string values, 8 KB per value,
32 KB in all). The manifest-aware checks are a second validator.

### 6.2 At queue time (API, before any credit)

`AppInputValueValidator` reads the workflow's **effective** manifest and:

1. refuses unknown ids, wrong types, out-of-range numbers, unknown choices, text over its cap;
2. fills defaults for empty optional inputs, drops values of hidden inputs (4.4);
3. refuses a missing required, visible input: `400 { code: "APP_INPUT_MISSING", message: "Fill in “What happens in the video”." }`;
4. re-resolves every `model` value (8.3);
5. runs `AppInputBinder` on a copy of the live steps and then each bound config's own `Validate()` plus
   `StepConfigSaveValidator`, so a value that would make a step invalid is refused now, with the input's label in the
   message, instead of failing minutes later;
6. stores the cleaned, typed values (the canonical form) in `run_brief_json.inputs`.

Order inside `RunStartGate`: authority, stale profile, then **app inputs (400)**, then the existing resolution 402,
concurrency 429, profile-build cap, credit 402. A bad input never costs anything and never writes a run row.

### 6.3 At the snapshot (engine)

`ExecutionSnapshotter` already clones the workflow and its agents at the claim. Right after the clone, it applies the
same pure `AppInputBinder` to the snapshot rows with the stored values and the manifest copied with the snapshot
definition (the snapshot copies `app_manifest_json`; the lane verifies it and adds it if not). The snapshot's configs
then hold **substituted values and no `appInputs` member**, so every executor, the replay planner and the change-request
code read plain configs as today. The binder is idempotent: a snapshot config has nothing left to bind.

If binding fails at the snapshot (the workflow was edited between queue and claim so an input no longer fits), the run
ends `Failed` with `APP_INPUTS_STALE` ("This app changed after you pressed Run. Run it again."), classed as a
platform failure so any hold is released; it fails before the first step, so nothing was spent.

## 7. Which settings are bindable

### 7.1 The allowlist lives on the config records

A new attribute in `SH/Workflows`, beside `StepCardAttribute`:

```csharp
[AppBindable(AppInputType.LongText, MaxLength = 7000, Label = "Video prompt")]
public string? InlinePrompt { get; set; }

[AppBindable(AppInputType.Duration, Min = 2, Max = 15, ChoicesFrom = nameof(VideoModelDurations))]
public int DurationSeconds { get; set; } = 6;
```

`StepCatalog` reflects them into `StepCard.BindableFields` (`{path, types[], min, max, maxLength, choices, label}`), so the
builder, the assistant (`GetStepCatalog`) and the validator read one list. Drift and denial tests:

- every `[AppBindable]` names a property that exists in the exported JSON schema;
- **no** bindable property matches the denylist: `*Egress*`, `*Consent*`, `MaxSpend*`, `*ProviderId` (models go through
  `@model` only), `*Id` / `*FileId` / `*StepOrder` / step references, `Timeout*`, `CacheMode`, `Mode` on
  `VideoCompile` (stream copy vs re-encode), `Version`, `Placeholders`;
- the `@model` pseudo-key is allowed only on step types listed in 8.1.

### 7.2 Initial allowlist

| Step type | Bindable settings (type) | Not bindable on purpose |
|---|---|---|
| `VideoGenerate` | `inlinePrompt` (text), `durationSeconds` (duration, per model), `ratio` (shape), `resolution` (choice, plan-capped), `takes` (number 1..4 on an app page), `maxClips` (number 1..64), `maxSpendCredits` (number, credits), `@model` | `allowSourceMediaEgress`, `avatarId` (brief `avatar`), frame refs |
| `AudioGenerate` | `prompt` (text), `durationSec` (duration), `takes` (number), `maxSpendCredits` (number, credits), `@model` | `kind` |
| `Voiceover` | `Inline` line text as one `longText` (paragraphs become lines), `speed` (choice: slower, normal, faster), `@model` | voice ids (brief `voice`), consent |
| `ThumbnailGenerate` | `overlayText` (shortText), `atTime` (number, seconds) | `fontProjectFileId` (a files input instead) |
| `VideoCompile` | `enableCaptions` (toggle), `captionStyle` (choice), `musicGainDb` (choice: quiet, balanced, loud), `transitionPolicy` (choice), `programFadeOutMs` (toggle "Fade out at the end") | `mode`, `targetDurationSec` (brief `lengthSec`), every step reference, `censorLabels` |
| `VideoAnalyze` | `sceneDetection` (choice), region-effect target prompt (shortText "What to blur"), region effect (choice Blur, Pixelate, …) | provider ids, `allowSourceMediaEgress`, segmentation caps |
| `ExtractFrame` | `at` and time (choice + number) | source refs |
| `AudioCleanup` | `target` (choice, **local targets only**: Hum, Wind, Hiss, Echo, VoiceBasic) | hosted targets (they need in-app consent) |
| `ImageGenerate` | `prompt` and `instruction` (text, placeholders, 4000 characters), `itemList` (text, placeholder; the batch list, one item per line or comma separated, at most 24, replaces `{item}` in the prompt or instruction once per item, so a "colours" input makes one picture per colour), `resolution` (choice 1K / 2K / 4K, the picture tier of a tier-priced model; the price follows it; added 2026-11-21 with the output-size setting). Added 2026-11-21 (Owner-approved, image batch). | `outputWidth` / `outputHeight` (exact pixels, builder only), `source` (an Edit's own source; `Source=BriefFiles` with `Role=Image` takes the run's picture files instead), `allowSourceMediaEgress`, `providerId`, `maxSpendCredits`, `variations`, `mode` |
| `Conditional` | (reads `input.*` in its expression, section 9) | branch orders |
| `Agent` | (model via `agent`, text via `requestTemplate`) | the agent itself, tools, skills |
| Every other type | nothing in v1 | |

A spend cap is the author's unless the app offers it as an input (A4 amended): when the most expensive allowed choices could exceed it, lint `WL033` warns the author
and the run page states the cap ("This app spends at most N credits on AI video per run").

## 8. Model choice as an input

### 8.1 What can be chosen

| Step | Capability | `job` filter | Binds |
|---|---|---|---|
| `VideoGenerate`, plan or inline | `VideoGeneration`; `LipSync` for a talking head (`talkingHead`) | `textToVideo`, `imageToVideo` (first/last frame), `reference` (avatar reference mode), `talkingHead` (still-then-animate, `FalVideoJob.TalkingHead`; its candidates are `LipSync` rows only) | `ProviderId` + `Model` |
| `VideoGenerate` still for a talking head | `ImageEdit` | `avatarStill` | the still model (new optional config member `stillProviderId`, appended, null = today's default) |
| `AudioGenerate` | `MusicGeneration` or `SoundEffectGeneration` by `kind` | none | `ProviderId` + `Model` |
| `Voiceover` | `SpeechSynthesis` | none | `ProviderId` |
| `Agent` | `Chat` | none | the run's agent snapshot (8.4) |
| `ThumbnailGenerate`, `DesignGraphic` | none in v1 (they make no image-model call that a person picks) | | |

The `job` is not chosen by the author by hand: the validator derives it from the step's own config (an inline step with
an avatar and `avatarAudio` is `talkingHead`, one with an avatar and no audio is `reference`, and so on) and refuses an
`allowed` model that does not serve that job.

### 8.2 The options the page shows

`GET /api/v1/projects/{p}/workflows/{id}/app/inputs/{inputId}/options` (View) answers, for one `model` input:

```json
{ "options": [
    { "id": "…", "label": "MiniMax Hailuo 3", "note": "Recommended", "costIndex": 1.0, "recommended": true, "available": true },
    { "id": "…", "label": "Kling 3 Standard", "note": "About 2× the price, sharper motion", "costIndex": 2.1, "available": true },
    { "id": "…", "label": "Veo 4", "costIndex": 6.5, "available": false, "unavailableReason": "Not on your plan" } ] }
```

- Source: the same rows as `selectable?capability=` (`InferenceProviderAdminService`: platform rows fully priced, plus
  the organization's own rows when its plan allows bring-your-own), intersected with the author's `allowed` list when
  set, filtered by the job (catalogue facts such as `FalAiVideoCatalog` jobs and the avatar reference declaration),
  the plan entitlements (`video_generation`, `audio_generation`, resolution cap) and the step's other settings (a model
  that cannot do the bound `ratio` or duration is shown unavailable with the reason).
- `costIndex` is a ratio to the recommended option for a fixed reference unit per capability (Chat: today's token mix;
  video: one 6 s clip at the step's resolution; music and SFX: 30 s; speech: 1,000 characters), from
  `IModelPriceService`. **Never USD and never credits.** The page shows it as words: "Cheapest", "About the same",
  "About 2× the price". Today `costIndex` is filled for Chat only; this extends it to every capability.
- Labels are the model's display name plus one optional plain note written in the catalogue (never a vendor id).
- `recommended`: the author's `recommended`, else the org's per-agent choice or the capability default, else the
  platform recommendation (`agent-model-recommendations.seed.json` for Chat).

### 8.3 Validation at run start

The stored value is a capability id and is **never trusted**: at queue (6.2) and again at the snapshot (6.3) the server
re-reads it through `ModelCapabilityView` and refuses (`400 APP_INPUT_MODEL_UNAVAILABLE`, plain message naming the
input) when it is not enabled, not the platform's or the run organization's own (the `StepProviderOwnershipValidator`
rule, one message, D5), not fully priced, not serving the step's job, outside the author's `allowed` list, or not on the
plan. Resolve-time behaviour after the snapshot is unchanged (fail closed to the capability default, counted
`reelbolt.inference.foreign_provider_ref`).

### 8.4 Agents

A `model` input with `"agent": "<agentDefinitionId>"` sets, for this run only, that agent's `InferenceProviderId` in the
run's `AgentSnapshot`. Precedence for that run: the app input, then the org's per-agent choice
(`organization_agent_provider_overrides`), then the definition, then the Chat default. If the same agent definition is
used by two steps, both use the chosen model; lint `WL032` says so as info. Built-in agents and custom agents alike;
the authority rule for editing agents (org Owner/Admin) is unchanged because nothing is written to the agent row.

### 8.5 Price and holds follow the choice

The estimate (8.6, 11.4) and `RunCreditHolds` price the chosen model: the engine plans holds from the snapshot, which
already carries the substituted `ProviderId`/`Model` and agent provider, and the estimator runs on the binder's dry-run
copy. `VideoGenerate` budget gates (`MaxSpendCredits`, daily caps) price the chosen model exactly as if the author had
set it.

### 8.6 What "re-price" means on the page

Changing a `model`, `duration`, `takes`, `resolution`, `shape` or a branch toggle re-fetches the estimate (debounced
400 ms); changing text does not. Which inputs move the price is derived (an input bound to a priced setting or read by an
input-decided condition), never typed by hand.

## 9. Conditional branching from inputs

### 9.1 What the author writes

A `Conditional` step in `Expression` mode reads inputs by name:

```
input.liteMode
input.quality == 'high' and input.takes > 1
```

### 9.2 How the engine evaluates it

`ConditionalStepExecutor` passes the run's typed input values to `ExpressionEvaluator` as extra parameters named
`input.<id>` (the existing extra-parameter path used for `decision.p_yes`). Two changes:

- **Inputs win.** Today a JSON-derived parameter beats an extra one of the same name. For the reserved `input.`
  namespace it is the other way round: any JSON-derived key starting with `input.` is dropped (logged). A step's model
  output can therefore never set or shadow `input.liteMode`.
- Values are parameters, never text spliced into the expression, so a value cannot change the expression.

### 9.3 Decided before the run

A condition whose only identifiers are `input.*` and literals is **input-decided**. `StaticBranchResolver` (pure, `SH`)
evaluates it with the same evaluator semantics once values are known, and `WorkflowBranchReach` learns to take a value
map: the steps on the branch not taken are left out of

- the estimate (the page shows the chosen branch's price once the switch is set; with no value yet, low = cheaper
  branch, high = dearer branch, as today's branch-aware range);
- the credit holds (`RunCreditHolds` plans only reachable steps);
- the linter (findings on unreachable steps become one `info` per branch, never errors: "In Lite mode this step does
  not run");
- the validator's "required input is used" and `showWhen` checks (4.4).

Conditions that read step outputs behave exactly as today.

### 9.4 Ending a branch: the `"end"` target

The engine continues after a branch with the next step by order, so a branch that is not the last one needs a way to
stop. `TrueBranchStepOrder` / `FalseBranchStepOrder` (string columns already) accept `"end"`: the run finishes there.
`WorkflowBranchReach`, `WorkflowStepDependencyGraph`, the replay planner and lint `WL004`/`WL014` learn the token. A
Conditional with no expression is always true, so a "Finish here" step is a Conditional with no expression and
`trueBranchStepOrder: "end"`; the builder shows it as "Finish here", not as a condition.

### 9.5 The worked example: "Avatar clip"

One recipe, key `avatar-clip`, replaces `spokesperson-clip` and `talking-avatar-budget`:

| Order | Step | Notes |
|---|---|---|
| 1 | Conditional "Lite mode?" | `input.liteMode`; true → 2, false → 6 |
| 2 | Agent `ScriptwriterAgent` "Write what the person says" | reads the request template (`whatTheySay`), `CacheMode: Never` as today |
| 3 | Voiceover "Speak it in the avatar's voice" | `ScriptScenes` from step 2, `voiceFromAvatar: true` |
| 4 | VideoGenerate "Make the talking clip" (still-then-animate) | today's budget config; `avatarAudio` from step 3; `appInputs`: `durationSeconds` ← `liteLength`, `ratio` ← `clipShape`, `@model` ← `liteModel` |
| 5 | Conditional "Finish here" | no expression, true → `"end"` |
| 6 | VideoGenerate "Make the spokesperson clip" (reference mode) | today's spokesperson config with `inlinePrompt: "{{input.whatTheyDo}}"`; `appInputs`: `durationSeconds` ← `clipLength`, `ratio` ← `clipShape`, `@model` ← `videoModel` |

Manifest inputs, in order: Avatar (brief, required); Lite mode (toggle, default off, help "Cheaper: one still picture
of the person, animated to speak your words"); What they do (longText, required, shown when Lite is off, example "Greets
the viewer warmly in a bright studio, looks into the camera"); What they say (longText, required, shown when Lite is
on, feeds `requestTemplate: "The person says: {{input.whatTheySay}}"`); Shape (shape, default 9:16); Length (duration:
6 or 10 s full, 5 to 15 s lite, one input per branch, `showWhen`); under More options: Video model (full branch,
`job: reference`), Lip-sync model (lite branch, `job: talkingHead`). The permission to send the person's media stays
off in the recipe and is given in the app only (WL015), for the step of the chosen branch only: the readiness strip's
fix button names that one step.

**The old keys** stay in `WorkflowTemplateCatalog` with two appended members on `WorkflowTemplateDefinition`:
`Hidden: true` and `SupersededBy: "avatar-clip"`. Existing workflows made from them are untouched and keep running.
The gallery, `ListWorkflowTemplates` and the assistant show only `avatar-clip`; `CreateFromTemplate` with an old key
creates `avatar-clip` and pre-sets the manifest default of `liteMode` (true for the budget key). Recommendation: no data
migration of existing workflows (they are few, small and work; migrating would change runs under people's feet).

### 9.6 How the assistant sets up a branched app

The assistant does not hand-assemble this graph: `avatar-clip` is a recipe. For a custom branched app the procedure is
in the prompt section (13.3): pick the two step lists, add a Conditional reading a `toggle` or `choice` input, end the
first branch with "Finish here", add `showWhen` on the branch-only inputs, validate (zero lint errors), propose. A
closed recipe patch `addLiteBranch` is **not** proposed: the general mechanism plus `ValidateWorkflow` is enough.

## 10. Caching, change requests, replays, snapshots, history

### 10.1 Step cache

The snapshot config already holds the substituted values, and the key hashes the step's config. To be safe even where a
key reads the source workflow (estimates and keys are matched by `StepOrder` on the source), `StepCacheKeyInputs` gains
an appended `AppInputs` component: a canonical string of the `{inputId: value}` pairs **that step consumed** (bound
in its config, read by its condition, or, for agents, the composed request text which is already hashed as
`UserRequest`). It is appended only when non-empty, so a run without inputs and every existing entry hash exactly as
before: no `SchemaVersion` bump. Consequence the product wants: re-running an app with one input changed reuses every
cacheable step that did not consume it.

### 10.2 "Change one thing and re-run"

- **v1:** the page pre-fills the form from the previous run's `brief` (including `inputs`), the person edits one field
  and runs. The step cache (10.1) makes the unchanged cacheable steps free.
- **Later lane (L9):** a change-request kind `appInputs` on the existing route:
  `POST .../executions/{e}/change-requests { "inputs": { "videoPrompt": "…" } }`. The seeds of the replay plan are the
  steps that consumed a changed input (the binder reports consumers per input); everything else is `Reuse` (copied,
  free, including non-cacheable agent steps). A change that flips an input-decided branch is refused with
  `409 INPUT_CHANGES_BRANCH` ("Run it again instead") because a different branch is a different film. Estimate,
  hold and the hourly `revision` bucket are the existing ones. `StepChangeCapabilities` gets an `appInputs` capability
  derived from the manifest.

### 10.3 Replays and revisions

Composition revisions and change requests replay the source run's brief (already true), so `inputs` come along and the
replay snapshot binds identically. A revision's per-revision snapshot built from the source snapshot finds nothing left
to bind (idempotent).

### 10.4 Run history

`WorkflowExecutionResponse.brief.inputs` returns the stored values; the app page's history row shows the first two
non-empty inputs in plain words ("Lite mode · “Welcome to Caffè Lume!”") and the run detail lists all of them with
their labels from the run's own snapshot manifest (labels of the time, not of today). Account export includes them;
the analytics grant keeps excluding `run_brief_json`.

## 11. Validation and lint

### 11.1 Manifest validator (`AppManifestValidator`, save time, `APP_MANIFEST_INVALID`)

New rules, each a plain sentence naming the input and the step:

1. Every `{{input.x}}` in any config and in `requestTemplate` names a declared `input`.
2. Every `appInputs` key is a bindable path of that step type (or `@model` where allowed), and its input's type is
   compatible (7.2); ranges, choices, defaults and `maxLength` fit every target (4.3).
3. Every `input` is used somewhere (a placeholder, a binding, a condition, the request template, an agent model).
4. Every `input.x` in a `ConditionExpression` names a declared `toggle`, `choice`, `number` or `duration` input.
5. `showWhen` refers to a `toggle` or `choice` input, one level, and a hidden input is not consumed on the branch that
   runs while it is hidden (9.3).
6. `model` inputs: the job derives, `allowed` ids are selectable for this org and serve the job, `recommended` is in
   `allowed`.
7. An `input` does not bind a setting an exposed brief field governs (4.5).
8. Limits of 4.3; ids unique and not brief field names.

The existing orphan rule (`WL018`) extends to inputs: a graph edit that removes the last consumer of an exposed input is
refused unless the same save removes the input.

### 11.2 New lint rules

| Id | Name | Fires when | Severity |
|---|---|---|---|
| WL028 | app-input-missing | a required visible input has no value (run page and gate) | error (blocks before credits; it would fail validation anyway) |
| WL029 | app-input-unfit | a value, default or choice no longer fits its target (the target's model or limits changed since save) | error |
| WL030 | model-input-unavailable | the chosen or recommended model is no longer selectable, priced, on the plan or able to do the job | error when chosen, warning for the recommended default |
| WL031 | branch-skips-step | an input-decided branch leaves steps out (one info per branch, with the input's value) | info |
| WL032 | agent-model-shared | an agent `model` input changes an agent used by several steps | info |
| WL033 | input-range-over-spend-cap | the dearest allowed choices (model × duration × takes) exceed the step's `maxSpendCredits` | warning (author) |

Findings use the existing shape (`messageKey`, `args`, `fix`); new fix kinds: `fillInput` (focus the field) and
`chooseModel` (open the model picker). `WL028`-`WL030` errors block only conditions that would fail at run anyway,
the linter's standing rule.

### 11.3 Missing required input

Refused at the gate (6.2) with `APP_INPUT_MISSING` before any credit preflight, hold or run row; the page never lets
Run be pressed (the button reads the fix, "Fill in what happens in the video", and focuses it).

### 11.4 Estimate

`POST /api/v1/projects/{p}/workflows/{id}/estimate` (new verb on the existing route, same handler as `GET`) takes the
`brief`, runs the binder dry-run and the static branch resolver, and prices the result. `GET` stays for callers that
send only `outputResolution`. The assistant's `EstimateWorkflowRun` already takes a `brief`; it gains `inputs` through it.

## 12. The "dumbed down" page

### 12.1 Rules

- **Only the inputs.** No step names, no model ids, no credits arithmetic except the one price line.
- **Plain labels, an example in every text box** (`example` as placeholder text), one help line under hard fields.
- **Sensible defaults** for everything that has one; required fields are only the ones with no sensible default (what
  the video shows, what the person says).
- **Progressive disclosure:** at most 8 fields shown; the rest under "More options" (closed by default, remembered per
  user per app in local storage, best effort).
- **Switches explain their cost** ("Lite mode (cheaper)"), and the price line updates when they change.
- **Model cards** show three things only: name, one note, the relative price in words, with "Recommended" first.
- **Readiness strip** unchanged in behaviour (icon + word, `role="status"`), with the new fixes.
- **Accessibility:** every input has a visible label tied to its control; help via `aria-describedby`; counters
  announced politely; hidden (`showWhen`) inputs are removed from the tab order, not just hidden.
- **Phone:** one column; text boxes full width and growing; model cards as a bottom sheet; sticky Run bar as today;
  no text under 12 px (`no-small-fonts.test.ts`).
- **Languages:** curated recipes' labels, help and examples are catalogue keys (`labelKey`, `helpKey`, `exampleKey`,
  resolved from `web/messages/{en,it}/apps.json`, added in the same commit, glossary-checked); labels an author or the
  assistant writes are content in the author's language and are shown as written.

### 12.2 Inputs per recipe (curated manifests)

Brief inputs that already ship are not repeated unless they change. "+" = new `input`; (M) = under More options.

| Recipe | Inputs (new ones in bold) |
|---|---|
| `quick-win-promo`, `lean-context-promo`, `quick-win-promo-ds` | Language, Length, Shape, Narration, Style; **+ What to highlight** (longText, request template "Focus on: …"); **+ Call to action** (shortText, "End with: …"); (M) **Script model** (agent model on Scriptwriter), **Builder model** (agent model on Author) |
| `quick-win-promo-plates`, `quick-win-promo-plates-ds` | as above; **+ Background look** (shortText, placeholder in the plates' `inlinePrompt`); (M) **AI background model** (`@model`) |
| `cinematic-feature-spotlight`, `story-arc-launch-trailer`, `narrated-story` | Language, Length, Shape, Narration, Style; **+ The story in one line** (longText, request template); **+ Audience** (shortText) |
| `product-profile-from-code` | none new (it builds a profile; the cap acceptance stays) |
| `video-derush-edit` and the `-room`, `-graphics`, `-graphics-room`, `-grade-room` variants | Your clips, Length, Shape, Style; **+ Captions** (toggle → `enableCaptions`); (M) **Caption look** (choice → `captionStyle`), **Ending** (toggle "Fade out at the end") |
| `video-derush-edit-music` | as derush; **+ Music volume** (choice → `musicGainDb`) |
| `video-derush-edit-sfx` | as derush; nothing new (sound files are a files input) |
| `video-derush-edit-broll` | as derush; **+ Extra shots to make** (choice 1 to 4 → plan cap via `takes`); (M) **AI video model** (`@model`) |
| `video-derush-edit-voiceover`, `-pickups`, `-dub` | as derush plus Narration and voice; dub: Language and Spoken language; **+ Captions** (dub: on by default) |
| `video-derush-edit-dailies` | as b-roll; **+ Takes per shot** (number 1 to 4 → `takes`) |
| `video-region-effect` | Your clip; **+ What to hide** (shortText → segmentation prompt); **+ Effect** (choice Blur, Pixelate, …) |
| `green-screen-insert` | Your clip (the content stays named in the workflow, as today) |
| `video-subtitles`, `video-subtitles-translated` | Your clip, Spoken language (translated: + Language); **+ Caption look** (choice) |
| `testimonial-reel` | Your clips, Length, Shape; **+ Captions** (toggle) |
| `avatar-clip` (new, 9.5) | Avatar, Lite mode, What they do / What they say, Shape, Length; (M) Video model, Lip-sync model |
| Any `VideoGenerate` app (custom) | the generic trio the assistant offers by default: **What happens in the video** (longText → `inlinePrompt`), **Length** (duration → `durationSeconds`), **Shape** (shape → `ratio`); (M) **Model** |
| Any `AudioGenerate` app (custom) | **What the music feels like** (longText → `prompt`), **Length** (duration), (M) **Model** |

Template versions bump only where steps change (`avatar-clip` is new; placeholders inside existing configs are a step
change, so the touched templates bump once).

## 13. Authoring

### 13.1 Builder (Advanced view)

- **"Make this an app input"** appears next to every field of `SchemaDrivenStepConfigForm` that `StepCard.BindableFields`
  lists (the per-field wrapper in `config-kit.tsx`). One click creates an `input` with a label from the field's
  `[AppBindable]` label, the current value as default, the type from the attribute, and writes the binding (`appInputs`
  entry, or for a text field replaces the value with `{{input.id}}` and keeps the old text as `example`). A bound
  field shows a chip "From the app: What happens in the video" and is read-only until unbound.
- For text fields, an **"Insert app input"** menu inserts `{{input.id}}` at the cursor (existing inputs, or "New
  input…").
- **Conditional step**: an "App inputs" picker in the expression editor inserts `input.<id>`; "Finish here" is a step
  in the add-step menu.
- **App inputs panel** (exists for brief inputs): edits label, help, example, required, default, choices, ranges,
  "More options", `showWhen`, order (drag and buttons); model inputs get an "Allowed models" multi-select and a
  "Recommended" choice; a **preview** of the app page renders beside it. The panel shows, per input, where it is
  used ("Step 3 · Video prompt"), from the binder's consumer map.
- Save runs the validator; errors appear inline on the input and on the step field.

### 13.2 Assistant and MCP: tools

**No new tool; counts stay 116 chat / 78 MCP.** Changes:

| Tool | Change |
|---|---|
| `ProposePatchWorkflow` / `ApplyPatchWorkflow` (MCP `PatchWorkflow`) | four new ops: `setAppInput {input}` (upsert by id), `removeAppInput {id}` (also unbinds), `bindAppInput {stepOrder, field, inputId, mode: "placeholder" \| "value"}` (placeholder mode may pass `template` with `{{input.id}}`), `setAppPage {title?, tagline?, order?, requestTemplate?}` |
| `ProposeWorkflow` / `ApplyWorkflow` (MCP `SaveWorkflow`) | optional `appManifest` argument (the API already accepts it on create and update) |
| `ProposeCreateFromTemplate` | its `brief` may carry `inputs` defaults to pre-set on the created app (for example `liteMode`) |
| `ExecuteWorkflow` | `brief.inputs` (same validator; card lists each input with its label) |
| `GetWorkflow`, `GetWorkflowTemplate` | return the effective manifest with input usage and, for `model` inputs, the options list (8.2) |
| `GetStepCatalog` | cards carry `bindableFields` |
| `EstimateWorkflowRun` | prices `brief.inputs` and the chosen branch |

The bindable allowlist is the only thing the binder can express, so the assistant **cannot** bind consent, egress,
spend caps or provider ids, whatever the model writes; `AssistantEgressConsentGuard` is unchanged and a
`setStepConfig` patch that writes `{{input.` into a non-bindable field is refused by the same save validator.

### 13.3 Approval cards and prompt

Cards are built from the diff, in plain words, never ids: "Adds the field **What happens in the video** (long text,
required) to the app page and uses it as the video description of **Make the clip**." / "Adds the switch **Lite mode
(cheaper)**; when it is on, the run takes the cheaper path (steps 2 to 4)." / "Lets people choose the video model:
3 models allowed, **MiniMax Hailuo 3** recommended." Every op has an `AssistantApprovalCardBuilder` arm (the catalog
test fails otherwise).

Prompt (one section in `AssistantAgent` and `DatabaseSeeder.BuiltInAgents`, pinned by `AssistantPromptConsistencyTests`,
inside the existing caps): "Setting up a request page": ask yourself which settings a person would change per run;
expose those as inputs with plain labels and an example; keep everything else fixed; prefer brief fields where one
exists; at most 8 visible inputs; use `toggle` + Conditional for a cheaper or alternative path; never bind consent,
egress, spend caps; propose the page together with the workflow in one card. KB: `docs/assistant-kb/app-inputs.md`
(types, bindable fields generated from `StepCatalog` with a drift test like `StepCardsDocDriftTests`, worked examples:
a prompt app, a model choice, a lite switch). Eval: five goals added to `docs/design/workflow-apps/eval-goals.json`
("make an app where my team types the video idea and picks the shape", "add a cheaper mode", …).

## 14. Data model and ownership

- **No migration.** `workflow_definitions.app_manifest_json` (engine-owned, API maps it `ExcludeFromMigrations`)
  holds manifest v2; `workflow_executions.run_brief_json` holds `inputs`; per-step `appInputs` live in the existing
  config jsonb columns of `workflow_steps` (engine-owned); `"end"` fits the string branch columns.
- **Manifest version.** Records stay positional with appended members (`AppManifestInput` gains `Id`, `Type`, `Help`,
  `Example`, `Required`, `MaxLength`, `Step`, `Unit`, `Options`, `More`, `ShowWhen`, `Capability`, `Job`, `Allowed`,
  `Recommended`, `Agent`, plus catalogue keys; `AppManifest` gains `RequestTemplate`). The validator accepts 1 and 2 and
  writes 2 whenever a v2 member is present; v1 manifests are untouched.
- **Append-only enums:** `AppInputType` (persisted by name); `WorkflowTemplateDefinition` gains `Hidden` and
  `SupersededBy` (appended, defaults false/null). New config member `VideoGenerateStepConfig.StillProviderId` appended.
- **Reserved names:** the config key `appInputs`, the string `{{input.`, the expression namespace `input.`, the branch
  token `"end"`.
- **Ownership:** binder, validator rules, static branch resolver: `SH` (pure, used by API and engine). Queue
  validation, options endpoint, estimate verb: API. Snapshot binding, `input.*` parameters, `"end"`: engine.

## 15. Delivery: lanes

Each lane is one reviewable PR with its tests; the final head of every lane runs the full
`ReelBolt.WorkflowEngine.Tests` project (CLAUDE.md "Whole-suite rule"). Sizes: S < 2 days, M 2-5, L 5-10.

| Lane | Content | Size | Depends on |
|---|---|---|---|
| L1 Contract | `AppInputType`, manifest v2 members, `[AppBindable]` + initial allowlist + denylist tests, `StepCard.BindableFields`, `AppInputBinder` (placeholders, `appInputs`, request template composition, consumer map, idempotence), `StaticBranchResolver`, validator rules 11.1, `appInputs` inert key and reserved `{{input.` in `StepConfigSaveValidator`; golden tests per step type | L | none |
| L2 API run path | `brief.inputs` in `RunBriefValidator`; `AppInputValueValidator`; gate order (400 before 402); dry-run bind + `Validate()`; request composition into `user_request`; responses and history; `POST .../estimate`; lint `WL028`-`WL033`; docs `run-brief.md`, `workflow-linter.md` | M | L1 |
| L3 Engine | snapshot binding in `ExecutionSnapshotter` (+ manifest copy), `APP_INPUTS_STALE`; `StepCacheKeyInputs.AppInputs`; `input.*` parameters with inputs-win rule; `"end"` target in executor, `WorkflowBranchReach`, dependency graph, replay planner; holds on the chosen branch | M | L1 |
| L4 Models | options endpoint and service, generalized `costIndex`, job derivation, `@model` binder target, `StillProviderId`, agent snapshot override, queue and snapshot re-resolution, re-priced estimate and holds | L | L2, L3 |
| L5 Run page | new input controls, More options, `showWhen`, model cards, price refetch rules, readiness fixes `fillInput`/`chooseModel`, history, en+it keys, phone, a11y tests | L | L2 (L4 for model cards) |
| L6 Builder | "Make this an app input", "Insert app input", Conditional input picker, "Finish here", App inputs panel editing and preview | M | L1, L5 |
| L7 Recipes | curated v2 manifests and placeholders of 12.2 (template bumps), `avatar-clip` and the hidden aliases, catalog goldens and lint test over every template | M | L3, L4 |
| L8 Assistant and MCP | four patch ops, `appManifest` on `ProposeWorkflow`, `brief.inputs` everywhere, card arms, prompt section, KB page + drift test, eval goals; count tests unchanged | M | L2, L4 |
| L9 Change requests | `appInputs` change-request kind, consumers as seeds, `INPUT_CHANGES_BRANCH`, capability, web "Change one thing" uses it when available | M | L3, L7 |
| L10 Docs | user guide `docs/user-guide/workflows/app-inputs.md` (+ Italian), recipe pages, `docs/workflow-apps.md`, `docs/platform-assistant-mcp.md`, CLAUDE.md lines | S | each lane adds its part; one final consolidation commit |

Order: L1; then L2 and L3 in parallel; then L4; then L5, L7 and L8 in parallel; then L6; then L9; L10 throughout. Total
about six to eight weeks of one backend and one web stream. Feature flag: `Workflows:AppInputs:Enabled` (default on in
development, off in production until L5 and L7 pass QA); with it off, `input` inputs are refused at save and the page
shows only brief, files and notes, exactly as today.

## 16. Risks and how they are handled

| Risk | Handling |
|---|---|
| Inputs used as prompt injection into agents | Only through the request template, quoted and fenced, never into system prompts; existing guardrails see them as request text (5.5) |
| A step output flips a branch | `input.` namespace reserved, inputs win over JSON keys (9.2) |
| A person picks an expensive model | Relative price shown; author's `allowed` list; spend caps stay author-set and are enforced by the gates; `WL033` tells the author |
| Workflow edited between Run and start | Re-bound at the snapshot; `APP_INPUTS_STALE`, nothing spent (6.3) |
| Cache poisoning across inputs | Consumed values in the key; dry-run proves determinism (golden tests: same values, same key) |
| Assistant binds something dangerous | It cannot express it: the allowlist is the binder's only vocabulary (13.2) |
| Old avatar templates confuse | Hidden aliases mapped to `avatar-clip`; existing workflows untouched (9.5) |

## 17. Open points, each with the default taken

All decided by the product owner on 2026-11-06: O1 to O6 as recommended below, and O7 (new).

| # | Decision (owner, 2026-11-06) |
|---|---|
| O1 | Decided as recommended: any member who may run the app may pick any model the organization can select for the job; the author restricts the list with `allowed` |
| O2 | Decided as recommended: an agent `model` input changes every step that uses that agent (`WL032` info) |
| O3 | Decided as recommended: `spokesperson-clip` and `talking-avatar-budget` become hidden aliases of `avatar-clip`; existing workflows keep running; no data migration |
| O4-O6 | Decided as recommended (defaults below) |
| O7 | App page detail: essentials visible, everything else under "More options", configurable by the app creator per input, for every input kind (4.6) |


| # | Point | Default (built unless the owner objects) |
|---|---|---|
| O1 | Who may pick a model on an app that the author did not restrict | Any member who may run the app; the list is the org's selectable models for the job. An author who wants control sets `allowed` |
| O2 | An agent model input changes every step using that agent | Accepted, shown as info (`WL032`); per-step agent models are not in this plan |
| O3 | The two old avatar recipes | Hidden aliases, no data migration (9.5) |
| O4 | `costIndex` beyond Chat | Show it for every capability as words, never numbers or money |
| O5 | Change-request kind for inputs | Ships after the page (L9); v1 relies on re-run plus the step cache |
| O6 | Inputs on the brief vs a new column | On the brief (`brief.inputs`): every copy site already carries it |

## 18. Lane L1 as built (contract)

Built on branch `feat/app-inputs`. **Dark by default:** every save path calls the validators with app inputs off, so
an `input` input, a `requestTemplate`, a `{{input.` anywhere, an `input.*` condition or an `appInputs` map is refused at
save ("not available yet"), and `brief.inputs` is refused by `RunBriefSplitter` until lane L2 wires the run gate. The
switch `Workflows:AppInputs:Enabled` is threaded by L2/L3 (parameters `appInputsEnabled` on
`StepConfigSaveValidator.Validate` and `bindingSteps` on `AppManifestValidator.Validate`).

| Piece | Where (`SH` = `ReelBolt.Shared`) |
|---|---|
| `AppInputType` (append only, wire names `shortText` ...), caps, shapes, jobs, capabilities | `SH/Workflows/Apps/AppInputType.cs` |
| Manifest v2 members (appended), `AppInputOption`, `AppInputShowWhen` (`equals`), `AppManifest.RequestTemplate`, `AppInputPlacement` (4.6) | `SH/Workflows/Apps/AppManifest.cs` |
| `[AppBindable]` and the reflected allowlist (`AppBindableCatalog`, `@model` pseudo-key) | `SH/Workflows/AppBindableAttribute.cs`, `AppBindableCatalog.cs` |
| `StepCard.BindableFields` (`StepCatalog.Get(type, includeBindableFields: true)`; off by default until L8) | `SH/Workflows/StepCatalog.cs` |
| Placeholder syntax, consumer map (`AppBindingAnalysis`), step slice (`AppBindingStep`) | `SH/Workflows/Apps/AppInputPlaceholders.cs`, `AppBindingAnalysis.cs`, `AppBindingStep.cs` |
| Pure binder (placeholders, whole values, fallbacks, idempotence, per-step consumed values and cache component) | `SH/Workflows/Apps/AppInputBinder.cs` |
| Request template composition (quoted, neutralised, `Notes for this run:` framing) | `SH/Workflows/Apps/AppRequestComposer.cs` |
| Typed value checking and cleaning (`APP_INPUT_MISSING`, `APP_INPUT_INVALID`) | `SH/Workflows/Apps/AppInputValueChecker.cs`, `AppInputText.cs` |
| `StaticBranchResolver` (input-decided conditions, engine-parity evaluation with NCalc, reach with `"end"`) | `SH/Workflows/Apps/StaticBranchResolver.cs` |
| Validator rules 11.1 (1-8), the 8-shown/16-total caps, version 1 or 2 | `SH/Workflows/Apps/AppManifestValidator.Inputs.cs` |
| `appInputs` inert key, reserved `{{input.`, allowlist checks at save | API `StepConfigSaveValidator.ValidateAppInputBindings` |
| `brief.inputs` (`RunBrief.Inputs`, `StoredRunBrief.Inputs`, shape check) | `SH/Briefs/RunBrief.cs`, API `RunBriefValidator.ValidateInputsShape` |

Narrowed in L1, left for later lanes (recorded, not dropped):

- **Voiceover text and speed** (7.2): only `@model` is bindable. Turning one long text into Inline lines needs a rule for
  each line's `startSec` (the slot a line may fill), which the plan does not state; L7 decides it with the recipes.
- **VideoAnalyze region effect choice** (7.2): whole-value bindings name top-level members only; the effect lives in
  `VideoCompileStepConfig.RegionEffects[]`. The target prompt (`trackObjects[].label`) is bindable as text. A nested
  whole-value path syntax is L7's if a recipe needs it.
- **Lint `WL028`-`WL033`** are lane L2's (section 15); L1 ships the pure pieces they wrap (value checker, consumer map,
  static branch resolver).
- **Model checks that need the organization** (selectable, priced, plan, job derivation, rule 6 in full) and the
  `@model` target resolution (`AppModelResolver`) are lane L4's; L1 checks the shape and reports an unresolved `@model`.
- **`VideoGenerate` duration ranges per model** (`ChoicesFrom`): L1 uses the widest safe range (1-60 s); the chosen
  model's own range is re-checked by the dry-run `Validate()` with capabilities (L2/L4).
## 19. Lanes L2 and L3 as built (run path and engine)

Built on branch `feat/app-inputs`, behind `Workflows:AppInputs:Enabled` (on in the Inference API's
`appsettings.Development.json`, off by default). Off, every save and run path is lane L1's, byte for byte: nothing new is
read, `brief.inputs` is refused, every app-input member is refused at save. The engine needs no switch: a run whose
snapshot and brief carry no app input is untouched (no binding, no state, no key component).

| Piece | Where |
|---|---|
| Switch | API `AppInputsOptions` (`Workflows:AppInputs:Enabled`, read per call) |
| Run gate check (6.2): checker, model seam, dry-run bind, bound `Validate()` + save validator; `{ code, message }` 400s `APP_INPUT_MISSING`, `APP_INPUT_INVALID`, `APP_INPUT_MODEL_UNAVAILABLE` | API `AppInputValueValidator` (`Evaluate` is pure), seam `IAppInputModelResolverSource` (lane L4) |
| Storage and request composition | `RunBriefSplitter.ResolveAsync(..., AppInputRunStart)`: canonical values to `run_brief_json.inputs`, composed template to `user_request` |
| Gate order | authority, stale profile, (trial grant), the brief (app inputs here, 400), brief file, then `RunStartGate.RefuseAsync`: resolution 402, concurrency 429, profile-build 409/503, lint, footage, credit 402; with inputs the gate judges `RunStartPricing.Charged(bound steps, reachable orders)` |
| Routes | Execute, Retry and Resume (stored values against the run's snapshot and its copied manifest), workflow-assistant execute, assistant `ExecuteWorkflow` (chat, MCP) and its approval card |
| Estimate | `POST .../workflows/{id}/estimate` `{ brief, outputResolution?, videoFileIds? }`, same answer as `GET`, over the bound, reachable steps |
| Save paths | REST create/update pass the switch to `StepConfigSaveValidator` and `bindingSteps` to `AppManifestValidator`; a stored manifest with inputs is re-checked against new steps |
| Lint WL028-WL033 | `WorkflowLinter.AppInputs.cs`, fix kinds `fillInput`/`chooseModel`, WL033 pricer `IAppInputSpendPricer`; en+it catalogue |
| Snapshot binding (6.3), `APP_INPUTS_STALE` | `ExecutionSnapshotter` + `AppInputSnapshotBinding` (bind clones before anything is written; manifest copied when it declares inputs); stale = run Failed before step 1, platform class |
| Engine run state | `AppInputRunState` in `AppInputsAmbient`: values, per-step cache components, reachable steps |
| Step cache (10.1) | `StepCacheKeyInputs.AppInputs` from `AppInputBinder.CacheComponent`, appended only when set; no `SchemaVersion` bump (stays `v10`) |
| Conditions (9.2) | `input.<id>` NCalc parameters from the ambient; `ExpressionEvaluator.EvaluateCore(reservedPrefix: "input.")` drops JSON-derived `input.*` keys, so inputs win; only for a run that carries inputs |
| `"end"` (9.4) | `ConditionalStepExecutor` (next index = past the last step), `WorkflowBranchReach`, `StaticBranchResolver`; the dependency graph and replay planner were already conservative about branches (a Conditional depends on every earlier step and is always re-run) |
| Holds (8.5, 9.3) | `UsageHistoryCreditHoldEstimator` plans over `AppInputRunState.Reachable` |
| Replays, revisions, change requests, resume | carry the stored brief (with `inputs`) as before; their steps come from an already bound snapshot, so nothing is re-bound; the run state still gives their conditions the values |

Left for later lanes (recorded):

- **L4:** the model resolver behind `IAppInputModelResolverSource` (queue) and at the snapshot
  (`AppInputSnapshotBinding.TryBindInPlace(resolveModel:)`); until then every `@model` binding with a value is refused
  (`APP_INPUT_MODEL_UNAVAILABLE`, WL030). The snapshot-side cache component of a `@model` step reads the capability id as
  given.
- **L8:** the assistant's save paths (`ProposeWorkflow`, patches, recipes) still validate with app inputs off; its
  `ExecuteWorkflow` path already runs the app-input check, but its 402 preflight still prices the saved, unbound
  workflow (conservative: both branches).
- **L5:** the run page's `fillInput`/`chooseModel` fix actions.

## 20. Lane L4 as built (models)

Built on branch `feat/app-inputs`, behind `Workflows:AppInputs:Enabled` like L2 and L3. Off, nothing new is read: the model
source is consulted only by the app-input check, the options route answers 404, and every new engine and pricing path is
reached only by a run whose brief carries a `model` value (no value = no choice = byte-identical keys, holds and snapshots).

| Piece | Where |
|---|---|
| Pure rules: the step's capability (`VideoGeneration`, or `LipSync` for a talking head; `MusicGeneration`/`SoundEffectGeneration` by AudioGenerate kind; `SpeechSynthesis`), the job derived from the step's config (8.1: avatar + driving audio = `talkingHead`, avatar alone = `reference`, a frame = `imageToVideo`, else `textToVideo`), job fit from catalogue facts only (`FalVideoCatalog`), `allowed`, the binder's resolver, the agents' chosen Chat models | `SH/Workflows/Apps/AppModelRules.cs` (`AppModelCandidate`) |
| Selectable models, one definition: `InferenceProviderAdminService.SelectableRowsAsync` (extracted from `ListSelectableAsync`, unchanged) | API `AppInputModelResolverSource` (registered `IAppInputModelResolverSource`, now `CandidatesAsync`) |
| Run gate (6.2, 8.3): `@model` resolved to `providerId` + `model`; agent inputs resolved to `AgentModels`; refusals `APP_INPUT_MODEL_UNAVAILABLE` (a value outside `allowed` now carries this code too, from `AppInputValueChecker`), before any plan, estimate or credit gate | API `AppInputValueValidator.Evaluate(..., candidates)` |
| Options route (8.2): `GET /api/v1/projects/{p}/workflows/{id}/app/inputs/{inputId}/options` (View; 404 when off, foreign or missing), recommended first, `costIndex` for every capability by the existing price paths (`VideoGenerateCreditPricing`, `AudioGenerateCreditPricing`, `IModelPriceService`), `available`/`unavailableReason` by the gate's own rules | API `AppInputModelOptionsService`, `WorkflowAppsController.ModelOptions` |
| Snapshot (6.3, 8.3): the chosen models re-resolved against the organization's enabled rows (platform and own only); one that can no longer be used is `APP_INPUTS_STALE` before step 1; agent choices recorded on the run's agent snapshot (`AgentSnapshotEntry.AppModelProviderId`, written only when set) | ENG `ExecutionSnapshotter`, `AppInputSnapshotBinding.LoadModelCandidatesAsync`/`AgentModelsOrThrow`, `AgentSnapshot.WithAppModels` |
| Agent model at run time (8.4): the app's choice wins over the workspace's per-agent choice, the definition and the default (and over cost routing); a choice that no longer resolves falls through like a disabled override | ENG `CostRoutingChatClientProvider.GetAsync` |
| Pricing (8.5): bound VideoGenerate/AudioGenerate settings are priced by their declared pricing (estimate, run gate, WL033, engine budget gates); agents' cold-start chat priced at the chosen model through `AppAgentModelAmbient`, read by `ProfilePriceTable` (shared by the API estimate and the engine holds); `RunStartPricing.AgentModels` carries the choice on Execute, Retry, Resume and the estimate verb | SH `AppAgentModelAmbient`, `ProfilePriceTable`; API `RunStartGate`, `RunBriefSplitter.PricingOr`, `WorkflowsController`; ENG `WorkflowExecutorService` |
| Step cache (10.1): a `@model` choice changes the bound config and its explicit provider reference (the existing fingerprint line); an agent choice replaces the fingerprint's Chat line with the chosen model (`ResolvedProviderFingerprint.ComputeAsync(..., appChatProviderId, ...)`). The line still means "what this step's chat resolves to", so no `SchemaVersion` bump (stays `v10`); with no choice every line is as before | ENG `ResolvedProviderFingerprint`, `WorkflowExecutorService.ResolvedProviderFingerprintAsync` |
| Ownership (D5): candidates are only the platform's rows and the run organization's own, the same rule as `StepProviderOwnershipValidator`; another organization's id is simply not a candidate, so it is refused with the one model message | SH/API/ENG as above |

Narrowed in L4, left for later lanes (recorded, not dropped):
- **History holds:** an agent step with this organization's usage history holds its p90 of past runs, whatever model was
  chosen; only its cold-start profile is priced at the chosen model. Per-model history buckets would need a sample column.
- **Voiceover price:** a chosen speech model binds and runs, but the Voiceover hold still prices the default speech model
  (as it does for an author-set `providerId` today); changing that moves holds of existing workflows.
- **`StillProviderId`** (the still model of a talking head, `ImageEdit` job `avatarStill`) is not built: it needs the
  executor's still-then-animate path to take a provider. A `model` input of capability `ImageEdit` has no binding target yet.
- **Plan entitlements** in the options list (`video_generation`, `audio_generation`, resolution cap) are not marked; the
  existing plan gates still refuse at run start or at the step.
- **Engine re-resolution** checks enabled, own-or-platform, capability, `allowed` and job; the "fully priced" and
  bring-your-own plan rules are checked at queue, and at run time the resolver still fails closed on both.
- **L5:** the model cards (words from `costIndex`), the `chooseModel` fix; **L6:** the "Allowed models" and "Recommended"
  editors; **L8:** the assistant's `GetWorkflow` with options, and its `ExecuteWorkflow` approval card lists the chosen
  model by name.

## 21. Lane L5 as built (run page)

Built on branch `feat/app-inputs`, behind the web switch `WORKFLOW_APPS_ENABLED` (the app page itself) and the server switch
`Workflows:AppInputs:Enabled`, which the page now reads from `GET .../workflows/{id}/app` (`appInputsEnabled`, appended to
`WorkflowAppResponse`, false when off). With the switch off, or for a version 1 manifest that uses no app input and sets no
`more`, the page is today's page exactly: the first 8 inputs, all shown, the `GET` estimate, no `brief.inputs`.

| Piece | Where (`web/components/workflow-apps/` unless named) |
|---|---|
| Layout (4.3, 4.6): `isUnderMore` mirrors `AppInputPlacement.IsUnderMore`; at most 8 shown and 32 in all, an essential past the eighth moves under "More options" | `app-input-model.ts` (`pageLayout`, `usesAppInputLayout`) |
| Values: defaults (a switch with none is off), `showWhen` (a hidden input is not rendered, so it leaves the tab order, and is not sent), `brief.inputs` (visible, non-empty, text trimmed), first missing required input, "Change one thing" puts a previous run's values back | `app-input-model.ts` (`initialValues`, `isVisible`, `briefInputs`, `firstMissing`, `valuesFromBrief`) |
| Controls per type (4.2): `shortText`/`longText` (example as placeholder, never sent; counter, read out only near the cap), `number`/`duration` (chips up to 4 choices, a select beyond, else a stepper with its unit), `choice` (segmented up to 4 with a default, else a select; the chosen option's hint under it), `toggle` (`onLabel`/`offLabel` as its description), `shape` (chips with pictograms), `model` | `AppInputField.tsx` |
| Curated texts (12.1): `labelKey`, `helpKey`, `exampleKey` and option `labelKey` resolve from `web/messages/{en,it}/apps.json` when the build has the key; otherwise the author's words are shown as written | `AppInputField.tsx` (`authored`) |
| "More options": closed by default, count in the label, `aria-expanded`/`aria-controls`, its inputs rendered only while open; remembered per person and app in local storage (best effort) | `AppRunPage.tsx` |
| Model cards (8.2): `GET .../app/inputs/{inputId}/options`; native radios in a `radiogroup`; name, "Recommended" badge, the catalogue note, the price in words from `costIndex` (Cheapest, Cheaper, About the same price, About N× the price; never a figure of credits or money); an unavailable model is disabled and says why from the new stable `unavailableCode`; nothing chosen picks the recommended model once the options arrive; on a phone the cards open in a bottom sheet from one button | `ModelPicker.tsx`, `app-input-model.ts` (`costWords`, `unavailableMessage`) |
| Reason codes (server): `AppModelRules.RefusalCode` (`notInWorkspace`, `wrongKind`, `notAllowed`, `wrongJob`), `Refusal` keeps its English sentence; `AppInputModelOption.UnavailableCode` appended | `SH/Workflows/Apps/AppModelRules.cs`, API `AppInputModelOptionsService` |
| Live price (8.6, 11.4): an app with app inputs is priced by `POST .../estimate` with the brief's inputs minus free text, its shape, its files and resolution, debounced 400 ms, the previous answer kept while the next loads; the breakdown shows the same answer | `lib/hooks/use-billing.ts` (`useWorkflowCostEstimateWithBrief`), `lib/api/billing.ts`, `components/billing/RunCostEstimate.tsx` (`estimate` prop) |
| Run-gate refusals (6.2, 11.3): the 400 body now carries `inputId` when the refusal names one input (`AppInputGateResult.ErrorBody`, `RunBriefResolution.ErrorInputId`); the page shows the code's sentence (`APP_INPUT_MISSING`, `APP_INPUT_INVALID`, `APP_INPUT_MODEL_UNAVAILABLE`, en + it) under that field, focuses it (opening "More options" first) and clears it when the field changes | API `AppInputValueValidator`, `RunBriefSplitter`, `WorkflowsController`; `AppRunPage.tsx` |
| Readiness (11.2, 11.3): a missing required input is a local "Fix needed" item whose fix names the field and goes to it (WL028 is then not listed twice); the linter's `fillInput` (WL028, WL029) and `chooseModel` (WL030) fixes map to the field named by the finding's `inputId` (`chooseModel` opens the sheet on a phone); a blocked Run press focuses the field through `RunBlock.onFocus` | `run-model.ts` (`fixActionFor`, `itemsFromFindings`), `RunBar.tsx`, `AppRunPage.tsx` |
| Catalogue | `apps.run.inputs.*` in `web/messages/{en,it}/apps.json`; dynamic prefixes `apps.run.inputs.{unavailable,unitSuffix,unit,shape}` in `web/i18n/dynamic-keys.ts` |
| Phone (12.1): one column, every card and chip wraps inside the column (`minmax(0, 1fr)`, `overflow-wrap`), controls at least 44 px | `AppRunPage.module.css` |
| Tests | `app-input-model.test.ts`; `AppRunPage.test.tsx` "app inputs on the run page"; .NET `AppModelInputTests`, `AppInputRunGateTests`, `WorkflowAppsApiTests` |

Narrowed in L5, left for later lanes (recorded, not dropped):

- **History:** the run history lists runs as before; showing each run's input values in it is not built (the brief carries
  them, "Change one thing" reads them).
- **Retry and Resume refusals** of app inputs are shown as today's run-level error, not next to a field (they re-check the
  stored values, which the page did not just send).
- **Plan marks on model cards** (8.2) wait for L4's narrowed plan entitlements; a model the plan refuses is still refused at
  run start.
- **The builder's preview** of the page (L6) can reuse `pageLayout` and `AppInputField` as they are.
- **Curated catalogue keys** for recipe inputs (`labelKey`/`helpKey`/`exampleKey`) are resolved but none are written yet (L7).

## 22. Lane L7 as built (recipes and the avatar merge)

Built on branch `feat/app-inputs`, behind `Workflows:AppInputs:Enabled`. The catalog now has two **editions**:
`WorkflowTemplateCatalog.GetAll()` (and `GetAll(appInputs: false)`) is today's catalog, byte for byte
(`LegacyTemplatesUnchangedTests` still pins it; the appended `Hidden` member is not serialized at its default), and
`GetAll(appInputs: true)` is the app-input edition built once by `WorkflowTemplateAppInputs.Edition` from it. With the
switch off nothing new is listed, provisioned or created; existing workflows keep the manifest and steps they were
created with (provisioning copies them, nothing re-reads the template).

| Piece | Where |
|---|---|
| The edition: per recipe, a v2 manifest (curated, or the derived one plus the new inputs), the config edits (parsed JSON), the version bump when a step changed, `avatar-clip`, the two hidden avatar templates | `SH/Workflows/Apps/WorkflowTemplateAppInputs.cs` |
| `WorkflowTemplateDefinition` appended `Hidden`, `SupersededBy`, `AppInputAgents` (input id to agent type: a template names agent types, provisioning writes the built-in agent's id into the input's `agent`); `GetAll(bool)`, `GetListed(bool)`, `GetByKey(key, bool)`, `GetAutoCreateTemplates(bool)`, `GetByKeyAnyEdition` | `SH/Workflows/WorkflowTemplateCatalog.cs` |
| Provisioning reads the switch: `ListRecipesAsync` (gallery, `GET .../templates`) lists the edition without hidden templates; `CreateFromTemplateAsync` creates from the edition, an old avatar key creates `avatar-clip` with that key's defaults (`SupersedeDefaults`: `liteMode` true for `talking-avatar-budget`), and `ManifestForCreate` fills agent ids | API `WorkflowTemplateProvisioningService`, `WorkflowAppsController`, `WorkflowsController` |
| `VideoRegionEffect.Target` is bindable text (`regionEffects[].target`, "What to hide", 120 characters) so one input fills both the tracked label and the effect target | `SH/Workflows/VideoCompileStepConfig.cs` |
| A request-template line whose every input is empty is left out, and a template left empty composes today's request (the notes alone), so an optional "Call to action" left blank changes nothing | `SH/Workflows/Apps/AppRequestComposer.cs` |
| Catalogue keys `recipes.inputs.<id>.label/help/example` and `recipes.inputs.options.<key>` (en + it), dynamic prefix `apps.recipes.inputs` | `web/messages/{en,it}/apps.json`, `web/i18n/dynamic-keys.ts` |
| Tests | `RecipeAppInputsTests` (edition off = today; keys and order; hidden aliases; version bumps exactly where steps changed; every curated manifest validates against its own steps; save validator with app inputs on; defaults bind to valid configs; avatar branches; lint of every listed template; every catalogue key in en and it), `RecipeProvisioningTests`, `AppBindableAllowlistTests`, `RecipesDocTests` (the KB lists both editions' keys) |

### Inputs per recipe (what shipped, against 12.2)

"Shown" are on the page at first, "More" under "More options". Every text input has an example; every model input
has no `allowed` list and no `recommended` (the recommended model is the step's current default, 8.2).

| Recipe (version) | Shown | More | Binds |
|---|---|---|---|
| `quick-win-promo`, `lean-context-promo`, `quick-win-promo-ds` (unchanged, manifest only) | Call to action (shortText, optional) | Script model, Video builder model (agent models: Scriptwriter, Author) | request template "End the video with this call to action: ..." |
| `quick-win-promo-plates`, `quick-win-promo-plates-ds` (v2) | Call to action; Background look (shortText, default "calm, premium colours") | AI background model (`@model`), Script model, Video builder model | placeholder in the plates' `inlinePrompt` |
| `cinematic-feature-spotlight`, `story-arc-launch-trailer` (unchanged) | Who it is for (shortText, optional) | | request template "Made for this audience: ..." |
| `narrated-story` (v2) | Who it is for; Captions (toggle, on) | | request template; `enableCaptions` |
| `video-derush-edit-voiceover`, `video-derush-edit-dub` (v2) | Captions (toggle, on) | | `enableCaptions` |
| `video-derush-edit-music` (v2) | | Music volume (choice Quiet -24, Balanced -16, Loud -10 dB; empty = the editor's level, as today) | `musicGainDb` |
| `video-derush-edit-broll` (v2) | | Video model (`@model`) | `@model` |
| `video-derush-edit-dailies` (v2) | | Takes per shot (number 1 to 3, default 2), Video model | `takes`, `@model` |
| `video-region-effect` (v2) | What to hide (shortText, required) | | `trackObjects[0].label` and `regionEffects[0].target` |
| `video-subtitles`, `video-subtitles-translated` (v2) | Caption look (choice Plain, Word by word, One big word; default Plain) | | `captionStyle` |
| `avatar-clip` (new, v1) | Avatar, Lite mode (cheaper), What they do (full) / What they say (Lite), Shape (9:16, 16:9, 1:1), Length (full: 6 or 10 s) | Video model (full), Lip-sync model (Lite) | see below |

Not changed, on purpose: the plain derush edit and its `-room`, `-graphics`, `-graphics-room`, `-grade-room`, `-sfx`
variants, `testimonial-reel`, `video-derush-edit-pickups`, `green-screen-insert`, `product-profile-from-code`.

Narrowed against 12.2 (recorded, not dropped):

- **"What to highlight" and "The story in one line"** were not added: the existing notes box ("Who is it for and what
  should it show?") already asks it, and two boxes for one question is the opposite of "dumbed down".
- **Captions on the plain derush family and the testimonial reel**: captions there need `captionSource: Transcript`,
  which a template cannot set while captions are off (`CaptionSource=Transcript only applies when EnableCaptions is
  true`), and one toggle cannot bind two settings. Needs a binder rule "a toggle sets several members" (L6/L10 decision).
- **"Ending" (fade out)**: the bindable toggle writes 1000 ms, the templates ship 800 ms, so the default would change
  every film; left out until the toggle's "on" value can be per input.
- **"Extra shots to make" on the b-roll recipe**: `takes` is takes per shot, not the number of shots, and `maxClips` is
  not bindable; only the model is offered.
- **Region effect choice** (Blur, Pixelate, ...): needs a nested whole-value path (`regionEffects[0].effect`); the
  binder has none. Seam for a later lane.
- **Voice and speech models**: not offered. A Voiceover's voice is the brief's `voice` (or the avatar's linked voice),
  which belongs to one speech provider; a different speech model would refuse it. Also the Voiceover hold does not price
  a chosen speech model yet (20).
- **Picture (still) model of the talking head** (`ImageEdit`, `avatarStill`): `StillProviderId` is not built (20).
- **Agent models on the narrated recipes**: only the promo family offers them (Script and Video builder), to keep the
  narrated pages short.

### The avatar merge (9.5, O3)

`avatar-clip` has six steps: 1 Conditional "Lite mode?" (`input.liteMode`, true to 2, false to 6); Lite branch 2
Scriptwriter "Write what the person says", 3 Voiceover (`ScriptScenes` from step 2, `voiceFromAvatar`), 4 VideoGenerate
"Make the talking clip" (today's budget config, `appInputs` `ratio` and `@model`), 5 Conditional "Finish here"
(no expression, true to `"end"`); full branch 6 VideoGenerate "Make the spokesperson clip" (`inlinePrompt`
`"{{input.whatTheyDo}} Medium close-up, steady camera."`, `appInputs` `durationSeconds`, `ratio`, `@model`). The input
decides the branch before the run (L3), so the estimate, the holds and the lint cover only the chosen path. Changes from
the two old templates: the full branch's cap is 460 instead of 400 so the 10 s choice fits with the most input material;
the shapes are the three both models make (4:5 is not a MiniMax-H3 ratio); the Lite branch asks no length, because the
narration sets it (the talking-head model sends no duration; `durationSeconds` 15 only sizes the hold). The permission
to send media still ships off on both clip steps (WL015 on the chosen one only).

`spokesperson-clip` and `talking-avatar-budget` stay in the edition unchanged with `Hidden: true`,
`SupersededBy: "avatar-clip"`: they are not listed by the gallery or `GET .../templates`, but a workflow made from them
still finds its template (`SourceOf`, `GetByKeyAnyEdition`). No data migration. Creating from an old key creates
"Avatar clip v1" (Lite mode on for the budget key).

### The Voiceover text binding decision (for the owner's review)

Chosen: the plan's own path for avatar-clip (9.5): **"What they say" reaches the voice through the request template
and the Scriptwriter**, and the Voiceover reads the script (`ScriptScenes`). The request template says "The person says,
word for word: ..." so the script keeps the person's words. Voiceover `Inline` text stays **not bindable** (as L1 left
it). Why: binding one long text into `Inline` lines needs a rule for each line's `startSec` slot, which nothing in the
plan states, no shipped recipe needs it (the only recipe that speaks a person's words is avatar-clip, and its Voiceover
reads a script), and the Scriptwriter path already respects the 15-second speech limit of the talking-head model.
Trade-off: the Scriptwriter may still trim or re-order words to fit; a "speak exactly this" binding would need the
`startSec` rule first (proposed: all lines in one slot from 0, paragraphs as lines, total length checked against the
step's own cap). Recorded as an open point for L9/L10.

### Seams left

- **L6 (builder):** the "Make this an app input" button can reuse `WorkflowTemplateAppInputs`' shapes; the narrowed
  rows above (several-member toggle, per-input toggle values, nested whole-value paths) are builder-and-binder work.
- **L8 (assistant):** the assistant's recipe tools (`ListWorkflowTemplates`, `GetWorkflowTemplate`,
  `ProposeCreateFromTemplate`, its approval card and recipe estimate) still read today's edition
  (`ListAvailableTemplatesAsync`, `GetByKey(key)`), because its save paths validate with app inputs off (19). When L8
  turns them on it switches them to `ListRecipesAsync` / `GetByKey(key, appInputs)` and `ManifestForCreate`; until then
  the assistant still offers the two old avatar recipes. The KB already has the `avatar-clip` section.
- **L9 (change requests):** the consumers per input come from the binder as for any workflow; `avatar-clip`'s
  `liteMode` is the canonical `INPUT_CHANGES_BRANCH` case.
- **L10 (docs):** the recipe pages describe the fields in one table (`user-guide/recipes/run-settings.md`
  "Recipe fields", Italian copy stamped by hand); the per-recipe pages are unchanged.

## 23. Lane L8 as built (assistant and MCP)

Built on branch `feat/app-inputs`, behind `Workflows:AppInputs:Enabled`. With the switch **off** every tool, schema,
description, approval card and the prompt are exactly as before (the new arguments are not registered, the four patch
operations are unknown, the brief schema has no `inputs`, `GetStepCatalog` has no `bindableFields`, the prompt has no new
section, and every save refuses a binding as lane L1 shipped). **No tool was added: 116 chat, 78 MCP.** Detail for
operators: [`docs/platform-assistant-mcp.md`](../platform-assistant-mcp.md) "App inputs: an app's own fields".

| Piece | Where (API `Agents/Tools`, `Services/Assistant` unless named) |
|---|---|
| Shared helpers: read an `appManifest` argument, the binding steps, the extended legacy token, model names, the card sentences (`DescribeChanges`, `RunLines`) | `AssistantAppInputs.cs` |
| Save paths honour the switch (`StepConfigSaveValidator.Validate(steps, on)` + `WorkflowAppSupport.CheckForSave(..., bindingSteps)`): `ValidateWorkflow`, `ProposeWorkflow`/`ApplyWorkflow` (MCP `SaveWorkflow`, optional `appManifest`), the patch and clone tools, `ProposeSetMediaOverlay`, `ProposeCreateFromTemplate`, the REST twin `WorkflowAssistantController` | `AssistantToolProvider.cs`, `AssistantWorkflowPatchTools*.cs`, `AssistantMediaOverlayTools.cs`, `AssistantRecipeTools.Create.cs`, API `Controllers/WorkflowAssistantController.cs` |
| `ApplyWorkflow` token with a page: `SHA-256(token, page, stored page)`; without one, today's token byte for byte; a page-bearing proposal is not handed to the review tray | `AssistantAppInputs.WorkflowToken`, `AssistantToolProvider.ProposeWorkflow` |
| Patch operations `setAppInput`, `removeAppInput` (unbinds placeholders, `appInputs` entries, template lines, `showWhen`; refused while a Conditional reads the field), `bindAppInput` (placeholder at a path or `appInputs[field]`), `setAppPage`; payload binds the page only when it changes | `AssistantWorkflowPatchTools.AppInputs.cs` (`WorkflowPatchOperation` appended members) |
| Approval cards: `appChanges` sentences on `ApplyWorkflow` and `ApplyPatchWorkflow` (built by the tool from the stored page, the new page and the step labels); a fallback arm per operation; the run card lists each input by label (a model by name), says how many steps the chosen path leaves out, and prices `PriceStepsAsync` over the bound reachable steps with the chosen agent models; the recipe card names the recipe's fields | `AssistantApprovalCardBuilder.cs` |
| `ExecuteWorkflow` (chat and MCP): `brief.inputs` (schema only with the switch on); lint, footage and 402 gates on `RunBriefResolution.PricingOr` (bound steps, charged = reachable, `AppAgentModelAmbient`); the paid-clips question counts only the chosen branch | `AssistantToolProvider.ExecuteWorkflow`; `IWorkflowExecutionPreflight.PriceStepsAsync`/`CheckStepsAsync` (new members) |
| `GetWorkflow` returns `appInputs` (fields, `usedBy` in words, `modelOptions` from `AppInputModelOptionsService` with the price in words); `GetStepCatalog` returns `bindableFields` | `AssistantWorkflowTools.cs`, `AssistantToolProvider.GetStepCatalog` |
| `EstimateWorkflowRun` prices `brief.inputs` on the chosen branch (`CheckAsync(forEstimate)`, `EstimateChargedAsync`); a recipe estimate takes its defaults' branch | `AssistantRecipeTools.cs` |
| Recipes read the edition (`ListRecipesAsync`, `ResolveRecipe`: a hidden avatar key resolves to `avatar-clip` with its defaults, `ManifestForCreate` at create); `brief.inputs` at create become the page's defaults | `AssistantKnowledgeTools.cs`, `AssistantRecipeTools*.cs`, `WorkflowTemplateProvisioningService.BuildDryStepsAsync` |
| WL028 (a required field with no value) never blocks a proposal: it is about a run's values | `Services/Workflows/AssistantLintPolicy.cs` |
| Prompt section "An app's own fields (app inputs)", appended by `PromptFor` with the switch on, verbatim in the seeder | `Agents/AssistantAgent.cs`, `Data/DatabaseSeeder.cs` |
| KB page with generated "Settings a field can fill" blocks | `docs/assistant-kb/app-inputs.md`, `AppInputsKbDocDriftTests` |
| Tests | `AssistantAppInputsTests`, `AssistantAppInputsSaveAndRecipeTests`, `AppInputsKbDocDriftTests` |

Narrowed in L8, left for later lanes (recorded, not dropped):

- **Eval goals.** The five app-input goals of 13.3 are not added to `docs/design/workflow-apps/eval-goals.json`: that
  fixture is the pinned 50-goal recipe-first gate (`check-eval-goals.mjs` and `AssistantEvalHarnessTests` require exactly
  50, routes from a closed set unmeasured). Proposed goals for a separate app-input eval set (L10 or the owner):
  "make an app where my team types the video idea and picks the shape"; "add a cheaper mode to this app"; "let people
  choose the video model, recommend the cheapest"; "run the avatar clip in Lite mode saying 'Welcome to Caffè Lume'";
  "remove the music volume field from this app".
- **No `GetWorkflowTemplate` model options.** A recipe not created yet has no workflow to price its model options against;
  the page shows the fields and the defaults, and `GetWorkflow` lists the options once it is created.
- **Model names on the run card** come from the capability's model row; an option the workspace cannot see reads "a model
  of this workspace" (never an id).
- **MCP collapsed descriptions** (`AssistantMcpToolSurface.Collapsed`) are unchanged; the MCP schemas carry the new
  arguments from the Propose tool's schema when the switch is on.
- **L6 (builder):** the builder can reuse `AssistantAppInputs.DescribeChanges` for its own "what changes" preview.
- **L9 (change requests):** `GetChangeOptions`/`ProposeStepChange` know nothing of inputs yet; the prompt tells the
  assistant to re-run with other values (`brief.inputs`), which the step cache keeps cheap.
- **L10 (docs):** CLAUDE.md lines for the assistant (the new arguments, the patch operations, the prompt section), the
  Italian copy of `docs/user-guide/assistant/claude-code.md` if one is made, the plugin version bump when the skills ship.

## 24. Lane L6 as built (builder)

Built on branch `feat/app-inputs`, behind the web switch `WORKFLOW_APPS_ENABLED` and the server switch
`Workflows:AppInputs:Enabled` (read from `GET .../workflows/{id}/app`, `appInputsEnabled`). With either off the builder is
today's builder: the workflow page provides no draft, so the App inputs panel renders its v1 body unchanged, the step drawer
has no "App fields" and the condition editor has no field picker or "Finish the run here".

| Piece | Where (`WEB` = `web`) |
|---|---|
| Bindable settings for the web: `WorkflowAppResponse.BindableFields` (appended, step type to `StepCatalog.BindableFieldsOf`, null when off) | API `WorkflowAppsController.Get` |
| The builder's model list: `GET .../app/inputs/{inputId}/options?all=true` (Edit permission) ignores the author's `allowed` list so "Allowed models" can widen it; recommended and job marks as before | API `WorkflowAppsController.ModelOptions`, `AppInputModelOptionsService.OptionsAsync(..., ignoreAllowed)` |
| The draft: the workflow page holds the app page beside the steps (`AppBuilderContext`); a drafted page rides every save (`Save Changes` or the panel's own button), as the whole manifest when one is stored, else only `inputs` and `requestTemplate` (the derived title keeps following the steps); refusals `APP_MANIFEST_INVALID` open the panel | `WEB/app/(app)/projects/[id]/workflows/[workflowId]/page.tsx`, `components/workflow-apps/AppBuilderContext.tsx` |
| Pure edits mirroring the binder and the assistant's patch operations: config slot per step type, concrete paths for `a[].b`, `appInputs` bind and unbind, placeholders, the consumer map ("used by"), new ids, a new field shaped from a setting (type, limits, choices, current value as default; old text as example), remove with unbinding (refused while a condition reads it, as `removeAppInput`), member clean-up per type, version 2 only while a v2 member is present, server messages placed under the input they quote | `WEB/lib/workflow-apps/app-input-builder.ts` |
| The panel with app inputs on: one row per field (name, kind, badges, "used by" in words, its server errors), Edit opens the field editor, arrows, remove; "Add a field" (seven types, or a brief field the steps read, probed as before); the request template with "Insert a field" at the caret; Save and Reset | `components/workflow-apps/AppPageEditor.tsx`, `AppInputsPanel.tsx` |
| One field's editor: name, help, example (text), required, More options (every kind), starting value in the type's own control, max length, min/max/step/unit, offered lengths, offered shapes, switch words, choice options (label and value; a numeric value is sent as a number), `showWhen` (one level, switches and choices only) | `components/workflow-apps/AppInputEditor.tsx` |
| Models: "People may pick any model" (no `allowed`), else ticked "Allowed models" (unusable ones disabled with the run page's reason), "Recommended" among them; needs the page saved once (the route reads the stored input) | `components/workflow-apps/AllowedModelsEditor.tsx` |
| Live preview: the run page's `pageLayout` and `AppInputField`, More options, `showWhen`; brief and file inputs as labelled stand-ins, a model as a stand-in line | `components/workflow-apps/AppPagePreview.tsx` |
| Step drawer "App fields": each bindable setting (array items one row each), "Use a field for this setting" (a compatible field, or a new one from the setting), "From the app: …" with "Stop using the field"; a text with fields is edited there with "Insert a field" and "Stop using fields" (placeholders back to the field's default, else its example); a setting a shown brief field governs says so; on an Agent step, "Let people pick this agent's model" (a `model` input with `agent`) | `components/workflows/StepAppFields.tsx`, `StepConfigPanel.tsx` |
| Condition step: "Read an app field" (switches, choices, numbers, lengths; a choice compared with its first option, joined with `and`), and per branch "Finish the run here" (`"end"`) | `components/workflows/ConditionalStepConfig.tsx` |
| Catalogue | `apps.builder.*` in `web/messages/{en,it}/apps.json`; dynamic prefixes `apps.builder.{types,newLabel,kinds,edit.units}` |
| Tests | `lib/workflow-apps/app-input-builder.test.ts`, `components/workflow-apps/AppPageEditor.test.tsx` (panel, step fields, models, condition, switch off); .NET `WorkflowAppsApiTests` (bindable fields on and off), `AppModelInputTests` (`ignoreAllowed`) |

Narrowed in L6, against 13.1 (recorded, not dropped):

- **One "App fields" section per step instead of a button beside every field.** Most step editors are hand-written, not
  `SchemaDrivenStepConfigForm`, so a per-field wrapper in `config-kit.tsx` would reach few settings. The section lists
  exactly `bindableFields`, so every bindable setting has its button, and "Insert a field" works in the section's own text
  box rather than inside the step editor's field. The step's own control still shows the raw text (with `{{input.id}}`).
- **"Finish here" is not an add-step menu item.** A branch of a Conditional gets "Finish the run here" (`"end"`); a plain
  stop is a Conditional with no expression whose true branch finishes.
- **A field's type is chosen once** (when it is added or made from a setting); change it by removing and adding. A
  `model` field is made from a step's Model setting or an agent step, not from "Add a field" (it needs its capability).
- **"What changes" preview** (`AssistantAppInputs.DescribeChanges`) is not reused: it is server code with no route; the
  live preview and each field's "used by" line show the effect. A route for it is left for L10 if wanted.
- **Drag to reorder:** arrows only, as the v1 panel.
- **L7's requests, deferred** (not in L6's row of section 15; each needs binder, validator, engine and cache-key work,
  not builder work): a switch that sets several settings (captions on the plain derush family and the testimonial reel);
  a per-input value for a switch's "on" (the "Ending" fade toggle writes 1000 ms, templates ship 800 ms); nested
  whole-value paths (`regionEffects[0].effect`, the region effect choice). Owner decision or a later lane.

Seams left:

- **L9 (change requests):** nothing in the builder; "Change one thing" on the run page is unchanged.
- **L10 (docs):** the user guide section "Setting up an app's page" in `user-guide/workflows/templates.md` (Italian copy
  stamped by hand) covers the builder; the planned page `user-guide/workflows/app-inputs.md` can take it over; CLAUDE.md
  lines for `bindableFields` on the app response and `?all=true`.

## 25. Lane L9 as built (change requests for inputs)

Built on branch `feat/app-inputs`, behind `Workflows:AppInputs:Enabled`. With the switch **off** nothing new is read: the
body's `inputs` is ignored (the request reads as a step change, exactly as before), the capabilities answer has no
`appInputs` member (`JsonIgnore` when null), the assistant's tools register no `inputs` argument and the card never reads one.
Contract for operators: [`docs/change-requests.md`](../change-requests.md) "Changing an app's fields (app inputs)".

| Piece | Where (`SH` = `ReelBolt.Shared`) |
|---|---|
| Pure rules: changed ids, seeds from the consumer map (`AppBindingMap.ConsumingSteps`: placeholders, whole values and `@model`, the request template for every request-reading step, agent `model` inputs; Conditionals excluded), on the run's path only; refusals; the values of a switch or choice that pick another branch | `SH/Workflows/ChangeRequests/AppInputChanges.cs` (`AppInputChangePlanner`, `AppInputChangeCodes`) |
| Stored shape: one `step_change_requests` row per seed, overrides `{"@appInputs": {"values": {...}}}` (`AppInputChangeRows`), instruction = the summary in words, `scope_fraction` 1. No migration | same file; API `StepChangeRequestService.RequestAsync` |
| Request path: values merged over the run's own and checked by the run gate's `AppInputValueValidator.Evaluate` (with the selectable models); stale check (each seed's authored step bound with the run's values equals the step the run executed); request text recomposed only when a template field changed (notes recovered from `Notes for this run:`); plan with the seeds as targets, off-path steps without a result marked `Skip`; snapshot = the source steps with each seed's config re-bound with the new values and the page copied; the run queued with the NEW `brief.inputs` | API `Services/ChangeRequests/StepChangeRequestService.AppInputs.cs` |
| Capability "derived from the manifest" (10.2): `appInputs` on `GET .../change-capabilities`, one entry per `input` field of the run's page (labels of the time) with the run's value, `supported` and the reason code, `usedBy` (step labels on the run's path), `decidesBranch`, `branchChangingValues`, and the field itself for the form | same file, `AppInputCapabilitiesAsync`; DTOs `StepChangeAppInputsResponse`, `StepChangeAppInputFieldResponse`, `StepChangeInputChangeResponse` |
| Engine: an app-input row is never a step change (`StepChangeRequestContext` returns null, the step runs from its re-bound config); a `CompositionReplay` run whose definition carries the page and whose brief chose an agent's model records the live agents with that choice (`ExecutionSnapshotter.ReplayAgentModelsAsync`), so the choice (old or new) reaches a replay. A model that can no longer be used is `APP_INPUTS_STALE` there too | ENG `Execution/Replay/IStepChangeRequestContext.cs`, `Execution/Snapshots/ExecutionSnapshotter.cs` |
| Assistant and MCP: `GetChangeOptions` `appFields`; `ProposeStepChange` / `ApplyStepChange` (MCP `StepChange`) take `inputs`, token over the sorted values; the card lists each changed field by label, before → after, what is redone and reused, and the price. No tool added: 116 chat, 78 MCP | API `Agents/Tools/AssistantChangeRequestTools.cs`, `Services/Assistant/AssistantApprovalCardBuilder.cs` (`StepInputsAsync`) |
| Web: **Change one thing and re-run** on a finished app run opens `AppInputChangeModal` when a field can be changed (the run's fields in the run page's own controls, the estimate debounced 400 ms, each change in words, what is made again, the price, "Run it again instead" on `INPUT_CHANGES_BRANCH`, "Change other things too" = today's pre-fill); otherwise today's pre-fill. `apps.run.change.*` en + it | `web/components/workflow-apps/AppInputChangeModal.tsx`, `app-input-change.ts`, `AppRunPage.tsx`, `lib/types/change-requests.ts` |
| Tests | .NET `AppInputChangePlannerTests`, `AppInputChangeRequestTests` (seeds, plan, snapshot, brief, request text, refusals, stale, switch off, capabilities), `AssistantAppInputChangeTests` (schemas off/on, options, propose/apply token, card); web `app-input-change.test.tsx` |

Codes: `400 APP_INPUT_MISSING` / `APP_INPUT_INVALID` / `APP_INPUT_MODEL_UNAVAILABLE` (with `inputId`), `400 APP_INPUTS_UNCHANGED`,
`409 INPUT_CHANGES_BRANCH` (with `inputId` when one field is to blame), `409 APP_INPUT_NOT_USED`, `409 APP_INPUTS_STALE`, and every
existing change-request code (`RUN_NOT_FINISHED`, `RUN_CLOSED`, `402 INSUFFICIENT_CREDITS`, `PLAN_NOT_ENTITLED`,
`429 REVISION_RATE_LIMITED`, the shared `revision` bucket).

Decisions taken in L9 (recorded for the owner):

- **The branch rule is about reach, not about the field.** A change is refused when the values' reachable steps differ (the
  avatar's `liteMode`), and also when a changed value is read by a condition the values alone do not decide (it might flip at
  run time). A field a condition reads but whose new value keeps the same branch (a choice with several options on one side) is
  allowed; `branchChangingValues` tells the form which options are refused.
- **Stale is judged on the re-made steps only.** An edit elsewhere in the app does not block a change (the replay keeps the
  source run's own steps for everything it does not re-make); an edit to a step the change re-makes does (`APP_INPUTS_STALE`).
- **New codes** beyond the plan's `INPUT_CHANGES_BRANCH`: `APP_INPUTS_UNCHANGED`, `APP_INPUT_NOT_USED`, `APP_INPUTS_STALE` (the
  engine's existing stale code, now also a `409` at request time).
- **Rows per seed, not a kind column.** The plan's "change-request kind `appInputs`" is the body shape and the stored overrides
  marker; no migration.

Narrowed in L9, left for later lanes (recorded, not dropped):

- **Inputs and step targets in one request** are refused (`400`); combining them needs one plan over both kinds of seed.
- **The change dialog edits `input` fields only**; brief fields (language, length, voice, files, notes) still change through
  "Change other things too" (a new run).
- **Composition revisions** still run without the app's agent-model choice (their snapshot carries no page); only change
  requests do.
- **The list** (`GET .../change-requests`) shows an app-input request by its summary instruction and `kind: "inputs"` targets;
  `inputChanges` is returned by the estimate and the `201` only.
- **History holds and the Voiceover price** of a chosen model are as L4 left them.
- **L10 (docs):** CLAUDE.md "Change requests" lines (the `inputs` body, the codes, the capability and the assistant argument);
  the user-guide page on app inputs can link to "Changing an app's fields" in `user-guide/workflows/request-changes.md`.

## 26. Lane L10 as built (docs and run history)

Built on branch `feat/app-inputs`. With the switch off nothing here is reached: a run whose brief carries no `inputs` gets no
`appInputs` member, and a change-request list without an app-input request gets no `inputChanges`.

| Piece | Where |
|---|---|
| Run history and run detail (10.4, left by L5): `appInputs` on `WorkflowExecutionResponse` (appended, left out when null) on `GET .../workflows/{id}/executions` and `GET .../executions/{id}`: each set field in the page's order, labelled by the run's own snapshot page (labels of the time; the workflow's page for a run without a snapshot), `valueText` in English words, `labelKey`/`valueLabelKey`/`onLabel`/`offLabel`/`unit` for the web, a model by name (`modelName`; one the workspace cannot see reads "a model of this workspace"), never an id. Read in one pass per list (manifests, then model names), only for runs that carry inputs | API `Services/Briefs/RunAppInputView.cs`, `WorkflowsController.RunAppInputsAsync` |
| Web: the history row shows the first two fields ("Lite mode (cheaper) · “Welcome to Caffè Lume!”"); the run page lists every field (a description list) under the run's figures; curated labels and choice labels from the catalogue, switch words, shapes, lengths and takes in the reader's language (`apps.run.history.*`, en + it) | `web/components/workflow-apps/run-app-inputs.ts`, `RunAppInputsList.tsx`, `components/workflows/ExecutionHistory.tsx`, the execution page |
| Change-request list (L9 narrowing): each app-input request carries `inputChanges` (before = the source run's values, after = the request's stored values, labelled by the source run's page, models by name); the web names such a request by its changed fields instead of the re-made steps | API `StepChangeRequestService.ListedInputChangesAsync`; `web/components/change-requests/ChangeRequestHistory.tsx` |
| Customer guide `user-guide/workflows/app-inputs.md` ("App fields": filling, More options, models, price, history, changing a field, setting up, recipes; links to `templates.md` "Setting up an app's page", `request-changes.md` "Changing an app's fields", `recipes/run-settings.md` "Recipe fields"); Italian copy stamped by hand (`i18n_source_sha` = `git hash-object` of the English page, heading ids = English anchors). The sidebar is autogenerated (`sidebar_position` 1.5) | `docs/user-guide/workflows/app-inputs.md`, `docs/user-guide-i18n/it/docusaurus-plugin-content-docs/current/workflows/app-inputs.md` |
| CLAUDE.md: one consolidated "App inputs" entry under Workflow apps (contract, gate codes, estimate and options routes, lint, editions and `avatar-clip`, assistant operations and prompt section, `bindableFields`) and an "App fields" line under Change requests. Tool counts were already 116 chat / 78 MCP | `CLAUDE.md` |
| Internal docs: `workflow-apps.md` (history, change, customer page), `change-requests.md` (the list carries `inputChanges`) | `docs/` |
| Eval goals: the five app-input goals of 13.3 in their own fixture, not read by `check-eval-goals.mjs`, `EvalFixture` or `WorkflowLintEvalGoalsTests` (each reads `eval-goals.json` by name; the 50-goal gate is unchanged) | `docs/design/workflow-apps/app-input-eval-goals.json` |
| Plugin: `plugins/reelbolt` 0.6.0 to 0.7.0 (L8 changed `author-workflow` and `run-and-watch`); as with 0.6.0 only `plugin.json` moves (`marketplace.json` still says 0.5.0, as before this lane) | `plugins/reelbolt/.claude-plugin/plugin.json` |
| Tests | .NET `RunHistoryAppInputsTests` (words, order, no model id, labels of the time, no member for a plain run), `AppInputChangeRequestTests.The_list_says_which_fields_changed_before_and_after_in_words`; web `run-app-inputs.test.tsx`, `change-requests.test.tsx` (app-input row) |

Not done in L10: the docs-site build was not run locally (no `docs-site/node_modules`); the links were checked by hand against
the English anchors. The user-guide section "Setting up an app's page" stays in `templates.md` and is linked, not moved.

### Deferred across lanes (for the owner)

- ~~**Pricing (L4):** an agent step with this organization's history holds its p90 whatever model was chosen (per-model history
  buckets need a sample column); a chosen speech model binds and runs, but the Voiceover hold prices the default speech model;
  plan entitlements are not marked on model cards (still refused at run start).~~ Built in F2 (28), by price ratio rather than
  per-model buckets.
- ~~**Still model (L4):** `StillProviderId` for a talking head's picture (`ImageEdit`, `avatarStill`) is not built, so a `model`
  input of that capability has no binding target.~~ Built in F1 (27).
- ~~**Binder extensions (L7, L6):** a switch that sets several settings (captions on the plain derush family and the
  testimonial reel need `captionSource` too); a per-input "on" value for a switch (the "Ending" fade: 1000 ms vs the
  templates' 800 ms); nested whole-value paths (`regionEffects[0].effect`, the region effect choice). Each needs binder,
  validator, engine and cache-key work.~~ Built in F1 (27).
- ~~**Voiceover "speak exactly this" (L7):** Voiceover `Inline` text is not bindable; avatar-clip's words go through the request
  template and the Scriptwriter, which may trim them. Needs the `startSec` rule (proposed: all lines in one slot from 0,
  paragraphs as lines, total length checked against the step's cap).~~ Built in F1 (27), with the rule the owner approved.
- ~~**Change requests (L9):** inputs and step targets in one request are refused; the change dialog edits `input` fields only
  (brief fields go through "Change other things too"); composition revisions run without the app's agent-model choice~~
  (revisions built in F2, 28; the rest in F3, 29).
- ~~**Run page (L5):** Retry and Resume refusals of app inputs show as a run-level error, not next to a field.~~ Built in F3 (29).
- ~~**Builder (L6):** "What changes" preview has no route (`AssistantAppInputs.DescribeChanges` is server-only); reorder by
  arrows only; a field's type is fixed once chosen.~~ Built in F3 (29), the type change narrowed to kinds with the same answer.
- ~~**Assistant (L8):** no model options for a recipe not yet created (`GetWorkflowTemplate`); MCP collapsed
  descriptions unchanged.~~ Built in F2 (28) and F3 (29).
- **Eval:** ~~the five goals in `app-input-eval-goals.json` are not scored; scoring them needs a scorer for patch operations and
  `brief.inputs`~~ (scorer built in F3, 29), and a paid run, still not made.
- **Switch:** was to stay off until the run page (L5) and recipes (L7) passed QA (15); removed 2026-11-10, app inputs are always on.

## 27. Follow-up F1 as built (binder extensions, "speak exactly this", the still model)

Built on branch `feat/app-inputs-followups` from `930df5ff4`, behind `Workflows:AppInputs:Enabled` like every lane. With the
switch off the catalog is today's (`LegacyTemplatesUnchangedTests` unchanged) and every save still refuses an `appInputs`
member; the two engine changes are inert for every stored config (`VoiceoverSource.Sequential` and
`VideoGenerateStepConfig.StillProviderId` are appended members whose defaults keep the request, the cache key and the hold
byte-identical).

### 27.1 The binding contract, extended

An `appInputs` entry is the input id (as before) or an object (`AppValueBinding`, `SH/Workflows/Apps/AppValueBinding.cs`):

```json
"appInputs": {
  "enableCaptions": "captions",
  "captionSource": { "input": "captions", "values": { "true": "Transcript" } },
  "programFadeOutMs": { "input": "ending", "values": { "true": 800, "false": 0 } },
  "regionEffects[0].effect": "hideWith",
  "@spokenText": "whatTheySay",
  "@stillModel": "pictureModel"
}
```

| Extension | Rule |
|---|---|
| (a) one input, several settings | One entry per setting, on any steps; the consumer map lists every one, so the change planner (L9) seeds every step that uses the field |
| (b) a value per position | `values` maps the input's values (`true`/`false` for a toggle, the option values for a choice; numbers by value) to what the setting gets. Only a toggle or a choice may list values; every listed value must fit the setting (`AppBindableField.ValueKind` text/number/boolean, its choices, min, max, whole numbers), checked at save; a value not listed keeps the authored setting (the fallback of 5.2). A pseudo-key never takes `values` |
| (c) nested whole values | A non-text `[AppBindable]` member of a list item (`regionEffects[].effect`) is bound by naming the item (`regionEffects[0].effect`); the item must exist in the authored config (the binder replaces a member, never adds an item). `field.IsNested`; `[]` in a key is refused |
| `@spokenText` (Voiceover) | A short or long text; the step's source must be `Inline` (save refuses otherwise). See 27.2 |
| `@stillModel` (VideoGenerate) | A `model` input of capability `ImageEdit`, only on a talking-head step (`avatarAudio` set); writes `stillProviderId`. See 27.3 |
| `@visionModel` (VideoAnalyze) | A `model` input of capability `Vision`; writes `visionProviderId`, the one picture model of that step (shot captions, subject location, looks at events). Added 2026-11-10 with `maxDescribedEvents` (number, 1..40, empty = from the video's length) and `sfxOnFootage` (toggle). This changes row 7.2 on purpose: a VideoAnalyze provider id stays unbindable as a raw field, and this one model choice goes through a pseudo-key like every other model |

New allowlist entries (`[AppBindable]`): `VideoCompile.captionSource` (choice), `programAudioFadeOutMs` (toggle or number,
0..10000), `regionEffects[].effect` (choice), `VideoAnalyze.transcription` (choice). None is on the denylist; consent, egress,
spend caps and providers stay unbindable (`AppBindableAllowlistTests`). `StepCard.BindableFields` gains `valueKind`.

Every layer reads the one parser: `AppBindingAnalysis` (shape, item, fit, `@spokenText`/`@stillModel` placement, at save),
`AppManifestValidator` (a mapped binding: toggle or choice only, every key a value the input can take; the type check of rule 2
is skipped because the listed values were checked against the setting), `AppInputBinder` (writes through
`AppValueBinding.TrySet`), the value checker (a `@spokenText` value's paragraphs), WL033 (reads either form), the assistant's
`bindAppInput` (`value` = the per-position map; an item key kept as given) and `removeAppInput`, and the builder
(`appInputsEntries`, `bindValue(…, values)`). The engine binds at the snapshot with the same binder, so nothing engine-side
changed for (a) to (c); the step cache keys on the bound config plus the consumed values (10.1), so two positions that write
different values give different keys, and no `SchemaVersion` bump.

### 27.2 "Speak exactly this" (owner-approved rule)

A text bound to `@spokenText` goes in as one block starting at 0 s: each paragraph (blank-line separated, inner line breaks read
as spaces) is one spoken line, in order (`VoiceoverSpokenText`). The binder writes `source.lines` (each `startSec` 0) and
`source.sequential: true`; the Voiceover executor then places the lines back to back from 0 exactly as it places a narration
plan (`AssignNarrationPlanStarts`), and does not enforce scene fit on them (they declare no slots). Caps: at most 40 paragraphs,
each at most 4096 characters (the sanitizer's own cap), 7000 in all; over them the run gate answers `400 APP_INPUT_INVALID`
naming the field (never cut). An empty optional text keeps the authored lines.

Decision for `avatar-clip`: the plan's path (9.5) allows it cleanly, so **version 2** speaks "What they say" verbatim. The Lite
branch is now Voiceover (`Inline`, `voiceFromAvatar`, `@spokenText`) then the talking-head clip (`avatarAudio` step 2); the
Scriptwriter step and the request template are gone (no agent reads the request any more), so Lite mode is also cheaper by one
model call. Steps: 1 Conditional (true 2, false 5), 2 Voiceover, 3 talking clip, 4 "Finish here" (`end`), 5 spokesperson clip.
The help of "What they say" says a blank line starts a new spoken line. The talking head joins the lines with its own 0.25 s
pause and checks the 15 s limit as before (`AVATAR_AUDIO_LENGTH`).

### 27.3 The still model

`VideoGenerateStepConfig.StillProviderId` (appended): the `ImageEdit` capability that makes a talking head's still.

- **Resolution:** `AvatarStillMaker` calls `ResolveImageJobAsync(ImageEdit, stillProviderId)`: explicit, else the default, as
  every capability; the fal.ai instruction-edit-with-several-pictures rule is unchanged, and a chosen model that is not one is
  `AVATAR_STILL_PROVIDER_MISSING` with its own sentence.
- **Ownership and cache:** a `StepProviderReferences` entry (`ImageEdit`, `stillProviderId`), so the save-time ownership check
  (D5) and the step cache's resolved-provider fingerprint include it (one more line only when set; no `SchemaVersion` bump).
- **Price:** the hold prices the still at the chosen model's billing mode (a bring-your-own model holds nothing) and at the larger
  of its catalogued per-image price and the fixed `AvatarStillHoldUsd`; with no choice the figure is today's.
- **Validation:** a real id, only with `AvatarAudio`.
- **As an input:** `@stillModel` takes a `model` input of capability `ImageEdit`; `AppModelRules.CapabilityFor/JobFor(step, path)`
  give `ImageEdit`/`avatarStill`, `ServesJob` accepts a fal.ai instruct endpoint with an image array (the default one when no model
  is named), the options route prices a still per image. `JobFor` now treats driving speech alone (the brief names the avatar) as
  a talking head, which is what `avatar-clip`'s Lite step is.
- `avatar-clip` v2 offers it under More options as "Picture model" (Lite mode only).

### 27.4 Recipes

| Recipe (version) | New field | Binds |
|---|---|---|
| `video-derush-edit`, `-room`, `-graphics`, `-graphics-room`, `-grade-room`, `-sfx`, `testimonial-reel` (+1) | Captions (toggle, off) | analysis `transcription` → `Required` when on; last compile `enableCaptions` and `captionSource` → `Transcript` when on |
| the same, plus `-voiceover`, `-dub`, `-music`, `-broll`, `-dailies`, `narrated-story` (+1) | Ending fade (toggle, on, More options) | every compile carrying the fade: `programFadeOutMs` on = the shipped value (800), off = 0; `programAudioFadeOutMs` on = shipped (900), off = 0 |
| `video-region-effect` (v2, already bumped by L7) | How to hide it (choice Blur, Pixelate; default Blur) | `regionEffects[0].effect` |
| `avatar-clip` (v2) | Picture model (More options, Lite) | `@stillModel`; and "What they say" now through `@spokenText` (27.2) |

Captions on an encode-once recipe ride the **Final** compile: `EncodeOnceBlockers` now refuses captions on the Plan stage only
(the review reads no caption fact, and the Final stage is an ordinary encode). With the switch's default (off) and "Ending fade"
on, every recipe's bound configs equal the shipped ones value for value (`AppInputFollowUpF1Tests`). The region effect offers only
the two effects that hide the object, to keep "What to hide" true. Catalogue keys `recipes.inputs.{footageCaptions,ending,hideWith,
pictureModel}` and options `blur`/`pixelate`, en + it.

### 27.5 Builder and assistant

- **Builder (`StepAppFields`):** a setting inside a list has one row per item for whole values too; a toggle or a choice is offered
  for any plain whole-value setting, and once bound "What each answer sets" lists each answer with the setting's own control
  (number box, choice list, on/off) or "Keep the step's own". Choosing a switch for a setting of another type starts with each
  "on"/option writing the step's current value, so nothing changes until the author edits it. "Words to speak" makes a long text
  field; "Picture model" an `ImageEdit` model field. `apps.builder.step.answer*` en + it.
- **Assistant:** `bindAppInput` takes `value` (the per-position map) and item paths; the operation note lists `@stillModel`,
  `@spokenText` and the map. No tool added (116 chat, 78 MCP), no prompt change. KB `assistant-kb/app-inputs.md` has two new
  sections and the generated blocks (`valueKind`, item paths).

### 27.6 Tests, and what is still open

Tests: .NET `AppInputFollowUpF1Tests` (mapped, nested, spoken text, still model, encode-once captions, recipes on/off, region
effect, change-planner seeds), `AppBindableAllowlistTests`, `RecipeAppInputsTests`, `RecipeProvisioningTests`,
`AssistantAppInputsTests` (`bindAppInput` with `value`), `VoiceoverStepExecutorTests` (sequential block),
`VideoGenerateStillPathTests` (chosen still model, its hold), `AppInputsKbDocDriftTests`; web `app-input-builder.test.ts`,
`AppPageEditor.test.tsx`.

Still open (recorded): the `StepConfigSchemaExporterTests` VideoGenerate pin and seven `VideoCompileStepExecutorTests` guard tests
already fail on `930df5ff4` (not caused by F1; `stillProviderId` is added to the pin's strip list); the still's price uses the
catalogued per-image price, and token-priced models (the default) still hold the fixed figure; captions from the footage on the
`-pickups` and green-screen recipes were not added (not in the deferred list).

## 28. Follow-up F2 as built (holds follow the chosen models, plan limits, recipe options, revisions)

Built on branch `feat/app-inputs-followups` from `aebc5176c`, behind `Workflows:AppInputs:Enabled` like every lane. With the
switch off no run carries a model choice, so nothing new is priced, read or written: `AppAgentModelAmbient` stays empty, the dry
run asks for no baseline price, a Voiceover's speech quantity carries no provider, the options route and the new recipe route
answer 404, `GetWorkflowTemplate`'s `modelOptions` is null, the gallery card's `modelInputs` is 0 and a revision replay carries no
page. Holds, estimates and step-cache keys are byte-identical (no cache-key change at all in F2).

### 28.1 Holds and the estimate follow the chosen model

| Piece | Rule | Where |
|---|---|---|
| History of an agent step | When the run's app chose the Chat model of an agent the step runs, the p90 hold and the shown history move by `Chosen.Typical / Baseline.Typical`: the step's own cold-start profile priced at the chosen model and at the agent's ordinary model, through the ONE pricer both services build. Rounded up; the hold is never below the chosen profile's P75 (`AppModelPricing.HoldFrom`). A render step's native-resolution p90 moves by the same ratio. Per-model history buckets were not built (they need a sample column); the ratio is the approximation, with the profile floor as the safety | SH `AppModelHoldScaling`, `RunCreditHolds.Plan`; API `WorkflowCostEstimator.DisplayStep` |
| Baseline price | `IProfileQuantityPricer.PriceBaseline` (default = `Price`); `ProfilePriceKey.Baseline` marks a chat key priced at the ordinary chain (the table skips `AppAgentModelAmbient` for it). Asked only for an agent with a choice, so no other run asks for anything new | SH `ProfileQuantityPricing.cs` |
| Voiceover speech | A Voiceover whose bound `providerId` is one of the run's step `model` choices prices `TtsInputBytes` at that model (`ProfileQuantity.ProviderId`, kept in the key like Vision's, resolved through `ResolveSpeechSynthesisAsync(id)`). An author-set provider the app did not choose keeps pricing the default speech model, as before | SH `StepUsageProfiles.WithChosenSpeechModel`, `AppModelRules.StepModelChoices`, `AppAgentModelAmbient.StepModels` |
| Carriage | `AppAgentModelAmbient.Enter(agents, stepModels)`: API `AppInputGateResult.StepModels` → `RunStartPricing.StepModels` (Execute, Retry, Resume, estimate, assistant execute/approval card, recipe estimate); engine `AppInputRunState.StepModels` (from the run's manifest and values) | API, ENG |
| Parity | The API's `RequiredMillis` and the engine's reservation are the same `RunCreditHolds.Plan` over the same table | `AppInputFollowUpF2Tests` |

### 28.2 Plan limits on model choices

`AppModelRules.PlanFeature(capability, job, plan, clipShortSide)` is the one rule: a video model needs `video_generation`; an
avatar's clip (`talkingHead`, `reference`) or still (`avatarStill`) also `avatars`; a music or sound-effect model
`audio_generation`; a video step whose own resolution's short side is above the plan's `maxOutputHeight` `resolution`. It is per
input (the same for every model of the input). The options route marks every option `available: false`, `unavailableCode:
notInPlan`, `planFeature`, a plain sentence; the run gate (`AppInputValueValidator`, entitlements read only when a model input has
a value) refuses a chosen model on a reachable step with `400 APP_INPUT_MODEL_NOT_IN_PLAN {code, message, inputId, feature}`,
before the plan and credit gates. With billing off the entitlements are unlimited, so nothing is marked or refused. The run page
words each feature (`apps.run.inputs.unavailable.plan.*`, `apps.run.inputs.refusal.plan`, en + it) and places the refusal on the
field.

### 28.3 Model options for a recipe not created yet (the L8 narrowing)

`AppInputModelOptionsService.OptionsForAsync`/`AllOptionsAsync` judge and price a manifest over any steps (the saved-workflow
route reads through them). `WorkflowTemplateProvisioningService.BuildDryAppAsync` gives a recipe's page as it would be created
(agent fields naming the built-in agents) and its dry steps, only with the switch on.
- Assistant/MCP: `GetWorkflowTemplate` adds `modelOptions` (per model field: `inputId`, `capability`, options with `name`,
  `recommended`, `price` in words, `available`, `unavailableReason`). No tool added (116 chat, 78 MCP).
- REST: `GET /api/v1/projects/{p}/workflow-apps/recipes/{key}/model-options` (View; 404 off, unknown recipe or foreign project).
  The options response gains `label`/`labelKey`; the gallery's recipe card gains `modelInputs`, and a card with model fields offers
  "Model choices" (`RecipeModelChoices`, read on demand).

### 28.4 Composition revisions keep the agents' chosen models (the L9 narrowing)

`CompositionReplaySources` reads the page the source run ran with (its snapshot's copy, else its workflow's); `BuildReplayWorkflow`
copies it onto the hidden `CompositionReplay` definition only when the source run chose an agent model
(`AppModelRules.HasAgentModelValue` over the run's brief, which the replay already carries). The engine then lays the same
choices on the replayed agents (`ExecutionSnapshotter.ReplayAgentModelsAsync`, as for change requests; a choice that no longer
resolves is `APP_INPUTS_STALE`). The revision's 402 preflight is not priced at the chosen models (as change requests); the engine's
holds are.

### 28.5 The still's price

The chosen still model is priced under the endpoint and tier the call is metered under (`VideoGenerateCreditPricing.StillBillingKey`:
a fal.ai row with no model runs the default instruction edit; a tiered endpoint is billed at its vendor-default tier, nano-banana's
`1K`), so the price book's own card is read rather than the dearest variant. nano-banana's 1K card ($0.044) is below the fixed
$0.063 (two to three pictures), so the hold stays at the fixed figure for it; a model with a dearer per-image card holds that. With no
choice the figure is unchanged. The options route prices a still option the same way, so it now has a `costIndex`.

### 28.6 Builder

"Words to speak" and "Picture model" come from `apps.builder.step.settingLabel.{spokenText,stillModel}` (en + it); every other
setting keeps the step card's label.

### 28.7 Tests, and what is still open

Tests: .NET `AppInputFollowUpF2Tests` (history moved both ways and the profile floor, API/engine parity, no baseline without a
choice, plan rule, options marking, run-gate refusal, `GetWorkflowTemplate` options, dry app, revision page, still tier),
`AppModelInputTests` (baseline key priced at the ordinary model, chosen speech model in the profile and the table); web
`app-input-model.test.ts` (plan words, refusal), `RecipeModelChoices.test.tsx`, `AppPageEditor.test.tsx` (builder labels).

Still open: per-model history buckets (the ratio is an approximation when the workspace's history mixes models); ~~the revision and
change-request preflights price at the ordinary models~~ (built in F3, 29); plan limits are per input, not per model (a model-level
resolution limit would need catalogue facts per model); ~~MCP collapsed descriptions unchanged~~ (built in F3, 29); the full engine
suite was not run for this batch (focused groups only).

## 29. Follow-up F3 as built (combined change requests, run settings, replay prices, field refusals, builder, MCP, eval scorer)

Built on branch `feat/app-inputs-followups` from `bf7e2425b`, behind `Workflows:AppInputs:Enabled` like every lane. With the switch
off nothing new is read or registered: a change body's `inputs` and `brief` are ignored (it reads as a step change, as before), the
capabilities answer has no `appInputs`, the assistant registers neither argument, every MCP description is as before, the new
builder route answers 404, and no run carries a choice to price. Step-cache keys, holds and the engine are unchanged.

### 29.1 Fields, run settings and steps in one change request (the L9 narrowings)

| Piece | Rule | Where (API `Services/ChangeRequests` unless named) |
|---|---|---|
| One request | `inputs` and/or `brief` may come with `targets[]` (never with the single-target fields: `400`). The field and setting seeds are prepared first; the step targets go through the multi-target preparation with those seeds as `extraSeeds` (a target downstream of a seed is re-made whole; the film may be the only step target next to a changed field). One plan over every seed and target, `OffPathSkipped`, one snapshot (seeds re-bound, the page copied), one pricing snapshot (step payloads sized), one estimate, one hold, one rate-limit hit, one run | `StepChangeRequestService.AppInputs.cs` (`PrepareAppInputsAsync`), `StepChangeRequestService.cs` (`PrepareAsync(..., extraSeeds)`) |
| Overlap | A step target that a changed field or setting already re-makes: `400` at `targets[i]` | same |
| Rows | One per seed (`PreparedTarget.IsInputSeed`: the marker, the summary, `kind: "inputs"`), one per step target (its overrides and note, `step`/`film`); unchanged `inputs` next to a step target or a setting are not a refusal | same |
| Run settings | `brief` (the run brief's shape): `language`, `sourceLanguage` (`auto` clears), `lengthSec`, `voice`, `designSystem`, `overlayCardAudio`, `avatar`, `notes`, `sourceFiles` (each listed role replaced). Checked as a run start (`RunBriefValidator.Validate`, `RunBriefSplitter.ProjectProblemAsync`). Seeds: the steps on the run's path that read a changed setting (`BriefFieldConsumers.FieldsReadBy`; the avatar where it is honoured; every request-reading step for the notes; a step that takes the role, or a `BriefFiles` footage source, for files). The replay runs with the source brief, the changed settings laid over; the request is recomposed when the notes change | `StepChangeRequestService.RunSettings.cs` |
| Refusals | `400 BRIEF_FIELD_NOT_CHANGEABLE` (`field`: format, resolution, stylePreset, styleId, style, tone change the whole film; `inputs` inside `brief`; notes of a run whose request was not composed on its page); `409 BRIEF_FIELD_NOT_USED` (`field`: nothing on the path reads it); `400 APP_INPUTS_UNCHANGED` when no field or setting differs | `SH/Workflows/ChangeRequests/AppInputChanges.cs` (`AppInputChangeCodes`) |
| Stored and listed | The settings in words on each seed row (`"brief": [{key, label, before, after}]` under the marker, `AppInputChangeRows.BriefLines`); `inputChanges` lines with `inputId` = `brief:language` / `brief:notes` / `brief:files:Video`, `type: "brief"` (estimate, `201`, list) | same |
| Capabilities | `appInputs.briefFields[]` (`StepChangeBriefFieldResponse`): each run setting the run's page shows, its value in the body's shape, `supported` and why not, `usedBy` | `BriefCapabilitiesAsync` |
| Assistant | `ProposeStepChange`/`ApplyStepChange` take `brief` and accept `inputs`/`brief` with `changes` (the token covers all three; `inputs` alone keeps the L9 payload); `GetChangeOptions` lists `runSettings`; the card lists each field or setting before and after, then each step with its note | `Agents/Tools/AssistantChangeRequestTools.cs`, `Services/Assistant/AssistantApprovalCardBuilder.cs` (`StepInputsAsync`) |
| Web | The change dialog shows **Run settings** (each setting the page shows, in the run page's own control: `RunSettingField`) beside the fields; on the run detail page **Change the app's fields** opens the same dialog in set mode and its change joins the change set (`useChangeSet().fields`, one entry "App fields and settings" in `ChangeSetPanel`, `buildSetBody(entries, fields)`) | `web/components/workflow-apps/AppInputChangeModal.tsx`, `RunSettingField.tsx`, `app-input-change.ts`, `components/change-requests/*` |

Decisions (for the owner): the step targets are always in `targets[]` when fields come too (the single-target body stays a step
change of its own); a setting nothing on the path reads is refused rather than silently ignored; a cleared setting is not offered
(the body sets values; `auto`, `""` and the role's new files are the ways to go back); the avatar cannot be cleared (the brief has
no "no avatar" value).

### 29.2 The price of a replay follows the app's chosen models (F2 28.7)

`ReplayModelPricing.ResolveAsync` gives the agents' Chat choices and the steps' chosen models of a replay whose definition carries the
app page and whose run carries values; `ReplayModelChoices.Enter()` enters `AppAgentModelAmbient` in the caller (an async-local set
inside an awaited method would not reach it). The change request's preflight and estimate, and the revision's request preflight
and estimate, run inside it. This is exactly when the engine holds at the choices (`ExecutionSnapshotter.ReplayAgentModelsAsync`,
`AppInputSnapshotBinding.StateFor`): an app-input change always carries the page; a revision carries it when the run chose an
agent's model (F2 28.4); a plain step change now does too (`AgentModelPageAsync`), so its replay also keeps the agents' models.

### 29.3 Retry and Resume refusals next to the field (the L5 narrowing)

A Retry or Resume refused by the run gate for an app input (`400 APP_INPUT_*` with `inputId`) is placed under that field, as a run
start's is: the form takes the run's values back (`valuesFromBrief`), the field takes focus (opening More options when it sits
there) and the sentence adds "Retry used this run's value, which can no longer be used." (`apps.run.page.retryFieldNote`). It is
no longer shown as a run-level error. `web/components/workflow-apps/AppRunPage.tsx` (`refuseOnField`).

### 29.4 Builder (the L6 narrowings)

- **What changes:** `POST /api/v1/projects/{p}/workflows/{id}/app/changes` (Edit; 404 off, foreign or not a Standard workflow) takes
  `{appManifest?, steps?}` (the draft as Save sends it) and answers `{lines}` from `AssistantAppInputs.DescribeChanges` against
  the stored page and steps. The App inputs panel shows "What changes when you save" above Save, asked 600 ms after the draft
  settles. `Controllers/WorkflowAppsController.Changes.cs`; web `describeAppChanges`, `AppPageEditor.tsx`.
- **Drag to reorder:** a handle per field (native drag and drop, `moveInputTo`); the arrows stay for the keyboard, touch and screen
  readers (no drag library is installed).
- **Kind of a field:** changed in place only between kinds with the same answer (short and long text; number and length), the id
  and every binding kept (`compatibleTypes`, `changeType`, limits fitted); any other kind is a new field, which the editor says.
  Narrowed from the plan's "migrate bindings where compatible": a choice, shape, switch or model has no compatible kind.

### 29.5 MCP descriptions (the L8 narrowing)

`AssistantMcpToolSurface.AppInputNotes` appends one sentence to the collapsed `SaveWorkflow` (`appManifest`), `PatchWorkflow` (the
four app-page operations), `StepChange` (`inputs`, `brief`) and `CreateFromTemplate` (`brief.inputs`) descriptions, only when the
real tool's schema (or description, for the patch operations) carries the argument, which it does only with the switch on. No
tool added: 116 chat, 78 MCP.

### 29.6 Eval scorer (no paid run; owner's choice)

`app-input-eval-goals.json` (version 2) gives each goal an `expected.score`: the proposal tool (`patch`, `patchOrSave`, `execute`),
Apply, the app-page operations (aliases link a field to its binding), a Conditional that reads a field and a finishing branch,
`brief.inputs`, the removes allowed, the must-nots (`egress`, `ids`, `prices`), and `clarify_first` as an alternate for AI02.
`AppInputEvalScoring` (in `inference/tests/.../AssistantEval`) scores a transcript: the operations of the applied (else last
accepted) patch, a page and bindings saved through `ProposeWorkflow` (each field a `setAppInput`, each `appInputs` entry or
`{{input.id}}` a `bindAppInput`), `ExecuteWorkflow` `brief.inputs` (text compared as read: quotes and a final full stop ignored),
the cheapest model among the options any tool answer listed, and the reply. It never reads `eval-goals.json`'s goals (the pinned
50-goal set; a checker asserts it stays at 50).

How to run it (nothing calls a model):

```
node docs/design/workflow-apps/check-app-input-eval-goals.mjs
dotnet test inference/tests/ReelBolt.WorkflowEngine.Tests --filter AppInputEvalScoringTests
```

A paid run would also need stub-world scenes for the five contexts (an app with one VideoGenerate step, avatar-clip with a consented
avatar, video-derush-edit-music's edition) in `EvalStubWorld` and an opt-in test like `Run_the_baseline_against_a_real_model`; not
built (no paid run was asked for).

### 29.7 Tests, and what is still open

Tests: .NET `AppInputFollowUpF3Tests` (fields plus a step target in one plan and run, the overlap refusal, unchanged fields next to
a target, a new language and new notes, refused settings, the switch off, the run-settings capability, the replay model scope),
`AssistantAppInputChangeTests` (brief and changes in one token and one call, the card, the MCP description on and off),
`WorkflowAppsApiTests` (the "What changes" route on, off and not found), `AppInputEvalScoringTests`; web `app-input-change.test.tsx`
(run settings in the body, set mode), `change-set.test.tsx` (fields with steps in one body), `AppRunPage.test.tsx` (a refused Retry
on its field), `AppPageEditor.test.tsx` and `app-input-builder.test.ts` (What changes, drag, kind of a field).

Still open (final list):

- Per-model history buckets (F2: the price ratio approximates a workspace whose history mixes models).
- Plan limits per input, not per model (a model-level resolution limit needs catalogue facts per model).
- The paid eval run and its stub-world scenes.
- Run settings not offered in a change: shape, resolution, style and tone (they change the whole film), clearing an avatar, and a
  v1 page with no app field of its own (the API takes `brief` for any finished app run with fields; the dialog opens only there).
- A field's kind beyond text and number changes only by a new field.
- The switch was to stay off until the run page and recipes passed QA (15); removed 2026-11-10.
- The full engine suite was not run by this batch (focused groups only; the caller runs it).

## 30. Permission to send media as an app field

Several step types carry a per-step permission, `allowSourceMediaEgress`: the customer's own media may be sent to an
outside AI service only when it is on, otherwise the step fails `EGRESS_REFUSED`. Until 2026-11-16 it could only be
switched on inside the step editor, and an app only warned that it was off (WL015). It is now a field of the app itself,
shown on the run page and in the editor's Run dialog. This section stands alone; the code is
`ReelBolt.Shared/Workflows/Apps/MediaEgressPermission.cs`.

**1. One field.** Id `allowMediaEgress`, a manifest v2 `kind: "input"` of type `toggle` that carries the new member
`permission: "mediaEgress"` (a closed vocabulary of one, `AppManifestVocabulary.Permissions`). Label "Allow sending your
media to AI services"; its `help` names the steps and why ("Needed by: Generate Video (shows an avatar), Analyze video
(finds the object)"). `more` is always false: it never sits under "More options". As derived, required:

```json
{"kind":"input","label":"Allow sending your media to AI services","id":"allowMediaEgress","type":"toggle","help":"Needed by: Analyze video (finds the object), Generate Video (shows an avatar)","required":true,"more":false,"labelKey":"permission.mediaEgress.label","permission":"mediaEgress"}
```

and optional:

```json
{"kind":"input","label":"Allow sending your media to AI services","id":"allowMediaEgress","type":"toggle","help":"Used by: Generate b-roll (can start a clip from your footage)","more":false,"labelKey":"permission.mediaEgress.label","permission":"mediaEgress"}
```

**2. Derived, never stored.** `MediaEgressPermission.WithField(manifest, steps)` adds the field wherever a page is read
(the app view `GET .../workflows/{id}/app`, the public API's workflow view, `GetWorkflow` and `GetWorkflowTemplate`, the
run gate) whenever a step has a need, and returns the same instance otherwise, so a workflow with no such step is
byte-identical to before. `AppManifestValidator.Sanitize` drops an echoed copy on save, and `Validate` refuses any
remaining input that uses the id or carries a `permission` member (`APP_MANIFEST_INVALID`,
`AppManifestValidator.ReservedPermissionMessage`): nobody writes, binds, defaults, renames or removes it by hand, the
assistant included. A stored row that somehow carries one is read as the derived field. A published community recipe never
carries it (`PortableRecipeBuilder` drops it); a copy derives its own from its own steps. The app view also returns
`mediaEgress { inputId, required, alreadyAllowed, steps[{ stepOrder, stepName, stepType, reason, need, allowedInStep }] }`
so the page can say it in the reader's language.

*As built, different from the brief:* the config member is NOT marked `[AppBindable]`. `AppBindableAllowlistTests` and
`InputsNeverGrantEgressTests` forbid an egress member on the allowlist, and that guard was kept. The binding is made by
code instead (`MediaEgressPermission.TryGrant`, called by `AppInputBinder.Bind`), so no `appInputs` map can name the
member for any input: a hand-written binding is still refused with "cannot be set from the app". The effect asked for
(only the permission field can set the member) holds, with a stricter guarantee.

**3. One definition of "need".** `MediaEgressNeed.For(step, briefAvatar)` is the predicate behind WL015, the field, the
gate and the binder: `Required` (the step fails `EGRESS_REFUSED` without it: a `VideoAnalyze` with a `Segmentation`
target; a `VideoGenerate` with a first or last frame or an avatar; a hosted
`AudioCleanup` target; a hosted `VideoEnhance`), `Optional` (a plan-sourced `VideoGenerate`: whether its plan names a
frame of the footage is known only when the run reaches it, so the field is offered and not required; without it a plan
that names a frame still stops the step `EGRESS_REFUSED` before anything is bought, as before) or `None`. The reason of
that optional need says what the permission can send: `plannedFrames` ("may start a clip from a frame of your footage")
when the step's `plannedSubjects` is `Off`, and `plannedFramesAndSubjects` when it is `Auto` (the default), because the
planner may then also pick the project's avatars of people and objects and send their pictures to the video model
provider under the same permission; the run page block, the assistant's approval card line and the field's description
use the same words (`MediaEgressNeed.Sends` / `ShortReason`). WL015 reports only `Required` needs, so it never shows
this reason. An inline `VideoGenerate` that names no avatar takes the run brief's avatar: it is
`Optional` while the page offers the avatar picker and `Required` for a run that chose one. A drift test fails when a
config type gains `AllowSourceMediaEgress` without an arm. The field is required when a step the run reaches is
`Required` and its own saved switch is off; an ordinary switch (off) when only optional uses exist; absent when nothing
sends. *As built:* the field is shown on (and the page locks it) when EVERY step with a need already has its saved
switch on, not merely every `Required` one, so that switching it on by default can never turn on an optional step the
customer left off.

**4. Per run, on the run's snapshot.** The value rides `brief.inputs.allowMediaEgress`. Only `true` is ever stored
("off" is the absence of the value; a default is never applied as a value). `AppInputBinder.Bind` turns
`allowSourceMediaEgress` on, on the bound copies of the steps that have a need and have it off; it never switches a saved
`true` off and never writes the workflow's steps. The engine binds the same way onto the snapshot
(`AppInputSnapshotBinding`), only when the stored brief also names who allowed it (`allowMediaEgressBy`, see 6f;
`GrantedValues` drops a value with no known grantor). `StepCacheKeyInputs.AppInputs` gains a component only on a step the
grant actually changed (no `SchemaVersion` bump). Retry and Resume re-bind from the run's own stored brief. A change
request or a revision carries the value over with its grantor; a change request that names the field is refused
(`APP_INPUT_INVALID`, "given when you run the app"). Run history shows it as a labelled field with who allowed it.

**5. Run gate.** `AppInputValueValidator` (inside `RunBriefSplitter`, before every 402) answers
`400 {code: "APP_INPUT_MISSING", inputId: "allowMediaEgress", message: "Allow sending your media to AI services to run
this app."}` when a reachable step needs it, its saved switch is off and the run does not give it. This holds for every
caller, so a run that would have failed at that step now costs nothing. An estimate is never refused for it. A workflow
whose only field is the permission, started without it, is `NotApplicable` exactly as before.
Only a step the run reaches counts (`StaticBranchResolver`): an app whose field picks a branch that sends nothing is not
asked. The pre-run linter follows the same reach for a brief (WL015 of an unreached step is dropped), the app view's
`mediaEgress.required` is the answer for the page's default choices, and `dependsOnChoices` says a field can change it:
the run page then follows the pre-run check for the current choices, and the Run dialog (which has no such check) asks
only when no choice can change it and leaves the rest to the gate.

**6. Who may give it.** The value must be explicit in the run's own request; a grantor gives the ability, never the
value. Each caller passes its grantor to the splitter (`AppInputRunStart.MediaEgressGrantedBy`); with none, a `true` is
refused `400 {code: "APP_INPUT_INVALID", inputId: "allowMediaEgress"}` (fail closed).

- a) **The app session** (`MediaEgressCallers.AppSession`: authenticated, no API-key claim, no `scope` claim, no engine
  `act` claim): the run page and the Run dialog.
- b) **A public API key** with the new scope `media.egress` (`ApiScopes.MediaEgress`, off unless ticked when the key is
  made; scopes are immutable, so an existing key needs a new key). Read from the key's row on each start. Without it:
  `403 {error: {code: "insufficient_scope", message, requiredScopes: ["media.egress"]}}`, the existing scope-refusal
  shape.
- c) **MCP** (stdio and OAuth alike), only while the calling user has switched on the per-user setting
  `application_users.mcp_media_egress_enabled` (migration `AddMcpMediaEgressSetting`, expand-only; `GET`/`PATCH
  /api/v1/auth/settings` field `mcpMediaEgressEnabled`; Settings, Assistant tab). Read from the database on every call.
  Off: `{"error": "media_egress_not_allowed", "message": "Allow connected tools to send your media in Settings >
  Assistant, or run this from the app page.", "inputId": "allowMediaEgress"}` and nothing starts. No OAuth scope is
  involved; `McpScopeGuard` is unchanged.
- d) **Assistant chat**: a run whose brief sets it always gets an approval card, in auto mode and on a woken turn too
  (`ApprovalCard.AllowsMediaEgress` forces Ask in `AssistantApprovalGate`). The card, built from the database and the
  exact arguments, adds "Allows sending your media to AI services for this run: <step> <what it sends>; ...". Approving
  it is the consent: the gate marks that one call (`AssistantMediaEgressApproval`, ambient, set only for a card that was
  shown and allowed) and the tool reads the mark together with `ChatApproved`. Declined or expired, nothing runs.
- e) **Anyone else** (the workflow-assistant REST route, which has no card; an engine on-behalf-of token; an unknown
  principal): refused, with the "open the app page" message.
- f) **Who allowed it** is stored beside the value: `StoredRunBrief.AllowMediaEgressBy` = `app` | `api_key` | `mcp` |
  `assistant_card`. Run history says "Allowed in the app", "Allowed by API key <name>", "Allowed by a connected tool",
  "Approved in the assistant". No new audit stream: a key's run start is already one `api_key_audit_events` request row.
- g) **The answer says what left.** The public API's `201` and the `ExecuteWorkflow` result carry
  `mediaEgress: { allowed: true, steps: [{ stepName, sends }] }` when it was set.

A run never persists the flag or touches the field: the saved step is never written by a run. Turning the saved flag on from
the assistant is a separate step edit with its own card (`AssistantEgressConsentGuard`, see platform-assistant-mcp.md).

*Residual:* a stdio MCP server runs with the user's own unscoped session token (`REELBOLT_MCP_TOKEN`). That token is
the person's session, so a process holding it can do what the person can in the app, the REST run start and the settings
switch included; the MCP rule above binds the `tool-call` path, not a stolen session. An OAuth MCP token is scoped and
reaches neither (`McpScopeGuard`, Go `middleware.Auth`).

**7. Linter.** WL015 uses `MediaEgressNeed`, its fix is `fillInput` on `allowMediaEgress` (label id
`WL015.allowOnAppPage`, the finding's args carry `inputId`), and it is silent for a brief that gives the permission.

**8. Step editor.** The per-step switch stays (the saved default). One line under it: "You can also allow this each
time you run, on the app page."

Web: the run page and the Run dialog show the field in a bordered "Permission" block above Run (`PermissionBlock`), never
among the creative fields; required and off, Run stays pressable and a press shows the sentence under the switch and
moves to it, through the same missing-input path as every required field; the page drops the linter's WL015 sentence so
one place talks about it. The builder lists it locked ("Added by the app"). Tests: `MediaEgressPermissionTests`,
`MediaEgressRunStartTests`, the media cases of `AssistantApprovalGateTests`, `PermissionBlock.test.tsx`.
