# Workflow apps: typed run brief, app view, recipe-first assistant, assistant knowledge

Status: **SPEC. No product code is changed by this document.** Initiative `workflow-apps-spec` (issue #510). The open questions were answered on 2026-10-24 (section 11 records each as decided); building (steps 2 and 3 of section 9) starts only after this PR is merged.

Companion files in `docs/design/workflow-apps/`: [`eval-goals.json`](workflow-apps/eval-goals.json) (the 50-goal set, section 8), [`eval-goals.md`](workflow-apps/eval-goals.md) (how to score it), [`check-eval-goals.mjs`](workflow-apps/check-eval-goals.mjs) (fixture validator), [`wireframes.html`](workflow-apps/wireframes.html) (the same wireframes as section 5, openable in a browser).

Citations are `path:line` against the tree at master `e0ffe67ef`. Abbreviations: `API` = `inference/src/ReelBolt.Inference.Api`, `ENG` = `inference/src/ReelBolt.WorkflowEngine`, `SH` = `inference/src/ReelBolt.Shared`, `WEB` = `web`. Line numbers drift; every cite names a symbol so it can be re-found.

## 1. Why: what the trigger run teaches

Run `f43f292c` on reelbolt.ai (the assistant built "Note spesa bozza - explainer IT v1", the run produced English and failed at Voiceover). Read against the code, three distinct causes, only one of which the sibling initiative `scriptwriter-output-shape` fixes:

1. **Goal specifics have no field to live in.** `ExecuteWorkflowRequest` carries `UserRequest`, `RegenerateVideoClips`, style choice, `OutputFormat`, `OutputResolution`, `AcceptEstimateCredits`, `BriefFileId` and nothing else (`API/Controllers/Dto/ProjectDtos.cs:265`). There is no language, length, voice or design-system field. The brief is one free-text string, assembled as `"{brief file}\n\nNotes for this run:\n{userRequest}"` (`API/Services/Workflows/BriefFileResolver.cs:75`, 64 KB cap at `:31`), stored as `workflow_executions.user_request` and appended to every agent step as `"\n\n---\nUser Request:\n{UserRequest}"` (`ENG/Execution/StepExecutionContext.cs:202`). Agents get no structure, so "Italian" in a workflow name reaches nobody. Only `NarrationTranslatorAgent` reads a language, and only if the free text names one (`ENG/Agents/Production/NarrationTranslatorAgent.cs:15`).
2. **The assistant assembles graphs by hand and has no recipe discipline.** Its whole template guidance is one sentence: "When a request resembles a built-in template, start from it with ListWorkflowTemplates and GetWorkflowTemplate instead of assembling steps from scratch" (`API/Agents/AssistantAgent.cs:105`). There is no tool that creates a workflow from a template (the model copies full step JSON through `ProposeWorkflow`), no step catalog beyond JSON Schema (`ListStepSchemas`, `API/Agents/Tools/AssistantToolProvider.cs:227`), no price lookup (price appears only on the approval card, `API/Services/Assistant/AssistantApprovalCardBuilder.cs` (`RunAsync`, dispatched at `:173`, price at `:737`)), and no check of cross-step compatibility (`ValidateWorkflow` checks agents and config shape only, `AssistantToolProvider.cs:236`).
3. **Nothing checks that a graph can work before it runs.** Real mistakes pass the save validator (`API/Services/Workflows/StepConfigSaveValidator.cs:52`) and fail minutes or credits later: a `Voiceover` whose script source points at a missing or wrong-type step surfaces only at run time, as `SCRIPTWRITER_NOT_FOUND` / `SCRIPT_SOURCE_INVALID` (missing step or order), `SCRIPTWRITER_OUTPUT_INVALID` (wrong-type step, `ENG/Execution/StepExecutors/VoiceoverStepExecutor.cs:338,343`) or the `PICKUP_*` codes (`:472,482`); `NO_LINES` (`:313`) is the separate empty-`Inline` case; `VideoCompile` with `enableVoiceover` and no valid `voiceoverStepOrder` is silently skipped, the film comes out mute (`ENG/Execution/StepExecutors/VideoCompileStepExecutor.cs:6392,6402`); a `ReviewLoop` with a wrong `LoopTargetStepOrder` just does not loop (`ENG/Execution/StepExecutors/ReviewLoopStepExecutor.cs:321`). Section 6.3 lists the rules.

What the trigger run did **not** show: the design system was pinned and read. `DesignSystemPinService` pins the project's approved design on every run automatically (`API/Services/DesignSystems/DesignSystemPinService.cs:20`, called from `API/Services/Workflows/WorkflowExecutionService.cs:118`). "Use our design system" is therefore already the default; what is missing is proof that the late steps build from it (owned by `scriptwriter-output-shape`) and a way to opt out or choose another (section 3.6).

### What exists today and is reused

| Capability | Where | Used by this spec as |
|---|---|---|
| Style presets, saved styles, one-off style; `RunStyleSnapshot` on the run row; role-filtered `[STYLE INSTRUCTIONS]` block; deterministic overrides; cache-key component | `docs/editing-styles.md`; `SH/Styles/StyleConfig.cs:119,166`; `ENG/Services/Styles/StyleNudgeService.cs:49`; `ENG/Services/Styles/RunStyleOverrides.cs:83`; `ENG/Execution/Caching/StepCacheKeyBuilder.cs:68` | The pattern the brief copies, and the home of the brief's style, tone and format fields |
| Design system pin frozen on the run; `[DESIGN SYSTEM v{N}]` / `[PRODUCT PROFILE v{N}]` blocks per role | `SH/DesignSystems/DesignSystemPin.cs:16`; `ENG/Services/DesignSystems/DesignSystemRunState.cs:44`; `SH/DesignSystems/Views/DesignSystemPromptView.cs:79` | Home of the brief's design-system field |
| Cost estimate with "usually A-B", fixed-price "plus up to", per-step lines | `GET /api/v1/projects/{p}/workflows/{id}/estimate`, `API/Controllers/WorkflowsController.cs:351`; `API/Controllers/Dto/WorkflowCostEstimateDto.cs:36` | The app run page price |
| Step schemas endpoint | `GET /api/v1/workflow-step-schemas`, `API/Controllers/WorkflowStepSchemasController.cs`; `SH/Workflows/StepConfigSchemaExporter.cs:17` | Seed of the step catalog (section 7.1) |
| Template catalog (23 templates) | `SH/Workflows/WorkflowTemplateCatalog.cs:87`; provisioning `API/Services/Workflows/WorkflowTemplateProvisioningService.cs` | The recipes |
| Dependency graph (backward step dependencies, "when in doubt it depends") | `SH/Workflows/Replay/WorkflowStepDependencyGraph.cs` | Base of the linter |
| Run gate: email, stale profile, plan resolution cap, concurrency, profile-build estimate, credit preflight | `API/Services/Workflows/RunStartGate.cs:51` | Where lint errors are enforced |
| Platform docs retrieval | `API/Services/PlatformDocs/*`; `docs/platform-docs-search.md` | Base of the RAG upgrade (section 7.4) |

## 2. Decisions in one table

| # | Decision | Section |
|---|---|---|
| D1 | One typed **run brief** with ten fields. Fields that already have a home (style, format, resolution, notes) keep it; new fields get one new engine-owned column. No duplicated state. | 3 |
| D2 | Brief reaches agents as a role-filtered `[RUN BRIEF]` block (same mechanism as the style block) and reaches deterministic steps through typed overrides (same mechanism as `RunStyleOverrides`). | 3.4 |
| D3 | Precedence: explicit brief field > free notes > style preset > design-system voice and tokens > step config as authored > agent default. | 3.2 |
| D4 | A run with no brief fields is byte-identical to today (prompts, step config, cache keys). No `SchemaVersion` bump for the brief itself. | 3.9 |
| D5 | **App view = a second projection of the same workflow.** One new nullable column `workflow_definitions.app_manifest_json`; every workflow without one gets a derived manifest, so all existing workflows are apps on day one. | 4 |
| D6 | Run-time **file slots**: the run page picks the clips, the step config is not edited per run (new `VideoSourceKind.BriefFiles`). | 4.3 |
| D7 | Assistant is **recipe-first**: reuse an existing app, else create from a template server-side, else apply closed patches, else (only on stated conditions) a custom graph, else decline with the nearest alternative. | 6 |
| D8 | A **pre-run linter** (`WL001`-`WL020`) in the API, one rule table shared with the step catalog; errors only for conditions already proven to fail at run time. | 6.3 |
| D9 | Knowledge in two layers: facts from deterministic tools generated from code; know-how from docs with typed metadata. | 7 |
| D10 | Retrieval: payload-filtered hybrid (dense plus sparse, fused by RRF), then a **paid model rerank from the start** (new `Rerank` capability, priced, metered, fail-soft to a deterministic rerank), offline eval before and after (Q10). | 7.4, 7.6 |
| D12 | Subtitles-only is a supported goal: the `video-subtitles` recipe (and `video-subtitles-translated`) with a transcript caption source, translation and SRT/VTT files in v1 (Q16, Q19). Avatars stay a decline. | 9.2 lane 3e |
| D11 | Tool surface moves from 86 to **91** (5 new, 5 changed); count is pinned in many places (section 7.2). | 7.2 |

## 3. The typed run brief

### 3.1 Schema (`RunBrief`, version 1)

One JSON object on the wire (`brief`), one request shape for REST, assistant and MCP. Every field is optional; absent means "no preference", exactly as `StyleConfig` axes behave (`docs/editing-styles.md`, "The style model"). Enums are persisted by name and append-only.

| Field | Type / values | Meaning | Where it is stored |
|---|---|---|---|
| `language` | BCP-47 primary subtag, e.g. `it`, `en`, `es-MX` (region ignored by agents, kept for voices) | The language of everything the viewer reads or hears: narration, on-screen text, captions, thumbnail text | new `run_brief_json` |
| `sourceLanguage` | same | Spoken language of the **source footage**, for transcription. Distinct from `language` so a dub is expressible | `run_brief_json` |
| `lengthSec` | int 5..600 | Target runtime. A target, not a guarantee; reviewers check it within a tolerance (section 3.8) | `run_brief_json` |
| `format` | `Original`, `Vertical`, `Square`, `Landscape`, `Portrait` (and the ratio spellings) | Output shape | existing `output_format_override` (`API/Services/Workflows/WorkflowExecutionService.cs:145`) |
| `resolution` | `Auto`, `720p`, `1080p`, `2160p` | Output short side | existing `output_resolution` |
| `stylePreset` / `styleId` / `style` | exactly one of; same rules as today | Editing style | existing `style_config_json` |
| `tone` | `StyleTone` values: `Calm`, `Upbeat`, `Energetic`, `Serious`, `Playful`, `Inspirational` | Tone of copy and delivery. **Folded into the run's style snapshot** (overrides the preset's tone axis) so the existing nudges and cache key carry it | `style_config_json` (`StyleConfig.Tone`, `SH/Styles/StyleConfig.cs:119`) |
| `designSystem` | `project` (default; today's behaviour: pin the project's approved design), `none` (run without brand tokens), later `{libraryId}` or `{versionId}` | Which brand the run is built in | `run_brief_json`; resolved into the existing `design_system_pin_json` at queue time |
| `voice` | `{mode: none \| narrated, projectVoiceId?, libraryVoiceId?}` | Whether the film is narrated and by whom | `run_brief_json` |
| `sourceFiles` | `[{role: video \| code \| music \| sfx \| image \| document, fileIds: [..]}]` | The run's media, by role (section 4.3) | `run_brief_json` |
| `notes` | text, as today (`userRequest` plus optional `briefFileId`) | Anything the fields do not cover | existing `user_request` |

Rules:

- **One request shape, three homes, zero duplicated state.** `ExecuteWorkflowRequest` gains one member, `Brief` (appended; positional record, null default). The API splits it: style, tone, format, resolution go through the existing `RunStyleResolver.ResolveAsync` (`API/Services/Styles/RunStyleResolver.cs:13`) into the existing columns; `language`, `sourceLanguage`, `lengthSec`, `designSystem`, `voice`, `sourceFiles` go to the new column. The execution response returns the composed view (`WorkflowExecutionResponse.Brief`) so the UI shows what a run used.
- The legacy top-level members (`StyleId`, `StylePresetKey`, `Style`, `OutputFormat`, `OutputResolution`, `UserRequest`, `BriefFileId`) keep working and are read as the same brief fields. Sending both a legacy member and the same brief field is a 400 with a plain message (the same rule as choosing two styles today).
- `language` and `sourceLanguage` are not auto-detected by the engine (determinism, cache keys). The assistant and the app run page fill them; if neither does, the field is absent and today's behaviour holds.
- Unknown fields are a 400. The schema is validated by one `RunBriefValidator` shared by REST, the assistant tool and the MCP bridge, like `EditingStyleRules` is shared for styles.

### 3.2 Precedence

Highest wins, field by field:

1. **Explicit brief field** (the structured value the person chose).
2. **Free notes** (`userRequest` / brief file). They can say anything the fields do not cover. Where they contradict a field, the field wins; the assistant and the run page are responsible for not creating the conflict (the run page shows the field; the assistant writes notes without restating fields).
3. **Style** axes (preset, saved, one-off). "The brief wins" is already the rule in the style block (`docs/editing-styles.md`, "How a style reaches the agents").
4. **Design-system voice and tokens** as defaults for copy tone and look.
5. **Step config as the workflow author set it.**
6. **Agent default.**

The one subtlety is 1 over 5 for deterministic steps: a run field overrides the step's own config for the property it is bound to, never the other way, matching the existing precedent (`RunStyleOverrides.ApplyToCompile` replaces the author's `OutputFormat`, `docs/editing-styles.md`, "What the style changes in the deterministic steps"). The bound properties are listed in 3.8; nothing else is overridden. An author-side "pin so a run field cannot override" switch is deliberately not in v1 (open question Q9).

### 3.3 Storage and transport

- **Column:** `workflow_executions.run_brief_json` (jsonb, nullable), **engine-owned**, mapped by the API with `ExcludeFromMigrations()`, exactly like `output_resolution` and `admitted_run_rate_bucket` (`CLAUDE.md`, "Startup order"; the ownership rule is pinned by `ApiMigrationOwnershipGuardTests`). Migration `AddExecutionRunBrief` in `ENG` (`dotnet-ef migrations add AddExecutionRunBrief --project src/ReelBolt.WorkflowEngine --context WorkflowEngineDbContext`).
- **Every site that copies run state must copy the brief.** The scout map of today's copy sites: write at queue (`API/Services/Workflows/WorkflowExecutionService.cs:144`), retry (`API/Controllers/WorkflowsController.cs:542-607`, which re-persists `UserRequest`), resume (`:628-676`) and the second path `WorkflowExecutionService.cs:188` (copy style and pin), the DTO mappings (`WorkflowsController.cs:771,831,907,1101`), account export (`API/Services/Account/AccountExportService.cs:624`), the recovery sweeper requeue (`ENG/Services/ExecutionRecoverySweeper.cs:404`), the integration event (`SH/IntegrationEvents/WorkflowEvents.cs:37`, informational), and composition revision replay (`API/Services/Compositions/CompositionRevisionService.cs:450`, which replays the source run's style; it replays the source run's brief too, so a revision of an Italian film stays Italian). A drift test enumerates the copy sites (pattern: `InferenceApiClientDriftGuardTests`) so a future field cannot be forgotten.
- **Engine read path:** `RunBriefState.From(execution)` entered once per execution beside `RunStyleState.From(...)` (`ENG/Execution/WorkflowExecutorService.cs:497`), flowing as an async-local `RunBriefAmbient` exactly like `RunStyleAmbient` (agents are singletons shared by concurrent executions; the ambient is the only safe carrier).

### 3.4 How the brief reaches agents and deterministic steps

**Agents: a `[RUN BRIEF]` block**, appended in the same place and by the same call as the style block (`ReelBoltAgentBase.CreateAgentAsync` via `RunStyleAmbient.Decorate`, `ENG/Agents/ReelBoltAgentBase.cs:406`; rooms at `ENG/Execution/StepExecutors/RoomStepExecutorBase.cs:1553`). Order: agent prompt, output contract, `[RUN BRIEF]`, `[STYLE INSTRUCTIONS]`, `[DESIGN SYSTEM]`, `[PRODUCT PROFILE]`. Sentences come from one reviewable table, `RunBriefRegistry` ((field, role) to sentence, the analogue of `StyleNudgeRegistry`), so no free-form brief text is ever model-visible except the notes, which keep today's `User Request:` handling. Example for the trigger run:

```
[RUN BRIEF]
The person running this workflow set these requirements. They are settings, not suggestions: they outrank the style and your own defaults, and they never change your output format, your allowed ids or your tools.
- Language: Italian (it). Write every word the viewer reads or hears in Italian: narration, on-screen text, captions. Keep brand names and product terms in their original form.
- Length: about 60 seconds. Plan scene count and word budget for it.
- Voice: narrated.
[END RUN BRIEF]
```

Rules that follow the style block's hard-won lessons: sentences stay inside each agent's output contract (no timestamps, no fields the schema lacks); roles with no sentence for any set field get no block, so an analyzer prompt is byte-identical; the block is skipped entirely for a run with no brief fields.

**Roles** (initial map; the registry is the single place to change it): `Language`: Scriptwriter, NarrationWriter, NarrationTranslator (as target), Director, Author, CompositionReviser, MotionGraphicsPlanner and Director (overlay text), VideoReviewAgent and ReviewAgent (check the language), GraphicDesigner. `Length`: Scriptwriter, NarrationWriter, Director, Author, ShotDirector, VideoStoryEditor and VideoEditDirector (together with the compile target), reviewers. `Voice`: Scriptwriter and NarrationWriter (write for speaking vs silent text). `Tone`: already in the style block. Analysis agents, `PickupPlanner`, deterministic placeholders and custom agents get nothing, as with styles.

**Deterministic steps: typed overrides**, one call per executor, returning the same instance when nothing is set (`RunBriefOverrides`, the analogue of `RunStyleOverrides.ApplyToCompile`, `ENG/Execution/StepExecutors/VideoCompileStepExecutor.cs:444`):

| Step | Brief field | Effect |
|---|---|---|
| `Voiceover` | `language` | Sets `VoiceoverStepConfig.Language` when null. **Today that field is provenance only** (`ENG/Execution/StepExecutors/VoiceoverStepExecutor.cs:996`, `SH/Workflows/VoiceoverStepConfig.cs:130`), it selects no voice or model. v1 passes the language to the speech provider where its API takes one and checks voice compatibility through a generated `SpeechLanguageSupport` fact table (unknown = pass through and lint `WL009` as a warning; never claim support the provider does not state) |
| `Voiceover` | `voice` | `projectVoiceId` / `libraryVoiceId` override `ProjectVoiceId` / `DefaultVoiceId` when set; `mode: none` makes the step a skipped step with a plain reason, never a failure |
| `VideoCompile` | `lengthSec` | Sets `TargetDurationSec` when it is 0 (`SH/Workflows/VideoCompileStepConfig.cs:611`); `TargetDurationMode` stays as the author set it |
| `VideoCompile` | `format`, `resolution`, captions look | unchanged: `RunStyleOverrides` |
| `VideoAnalyze` | `sourceLanguage` | Sets the transcription language hint when the step has none (`ENG/Execution/StepExecutors/VideoAnalyzeStepExecutor.cs:3210`) |
| `VideoAnalyze` etc. | `sourceFiles[role=video]` | New `VideoSourceKind.BriefFiles` (appended): the source resolves at run time to the run's video files, so the run page chooses clips without editing the graph (section 4.3) |
| `ThumbnailGenerate` | `language` | Text is whatever the upstream agent produced; no override |
| `TimelineRender`, `ExtractFrame`, `PinInsert`, `DesignSystem`, `AudioGenerate`, `VideoGenerate` | none in v1 | `VideoGenerate` / `AudioGenerate` prompts are written by upstream agents that already see the brief |

### 3.5 Style presets are part of the brief

The requirement, stated precisely. Style presets and saved styles are first-class brief fields, not a parallel feature:

1. **Same field, same resolver.** `brief.stylePreset` / `styleId` / `style` are the existing `ExecuteWorkflowRequest` members surfaced inside the brief. `RunStyleResolver` stays the only resolver, so the 400s and the plan check behave identically (`docs/editing-styles.md`, "Starting a run with a style and a format").
2. **Presets suggest brief defaults.** `StylePresetCatalog` presets already carry a suggested shape. The app run page and the assistant use the preset to pre-fill `format` (still only a suggestion, never silently applied) and, new, to pre-fill `tone` from the preset's tone axis. A preset chosen in the app does not change which template the assistant recommends; it changes the brief.
3. **Tone is one axis, not two.** `brief.tone` writes `StyleConfig.Tone` into the run's snapshot, overriding the preset's axis, so there is exactly one tone in the run record, one nudge sentence and one cache-key component. (If the run has no style at all, a tone alone creates a one-axis style snapshot named "Custom".)
4. **Recipes carry a recommended preset.** Each template's app manifest may name a `suggestedStylePreset` (the corporate-explainer template suggests `corporate-explainer`, the reel templates `fast-social-reel`); the gallery card shows it as a chip and the run page pre-selects it.
5. **The style pipeline's reach is honest.** Today style nudges reach only agents with a role in `StyleNudgeRegistry` (the editors, graphics, music, colour, sound, shots, narration, the promo trio, the video reviewer) and deterministic compile overrides. A style on `green-screen-insert` or `video-region-effect` does nothing; lint `WL011` says so on the run page instead of accepting it silently.
6. **Cache and retry:** unchanged. The style is already in the step cache key (`StepCacheKeyBuilder.cs:68`), retry and composition replay already copy it.

### 3.6 Design system

- `project` (default) is what happens today for every run: the pin is built once at queue time from the project's approved design plus the linked org library (`DesignSystemPinService.cs:20`), and a read failure yields no pin and never blocks queueing. The brief makes this explicit and visible ("Design system: Northwind Staffing v6" on the run page and card).
- `none` skips pin building for the run (no `[DESIGN SYSTEM]` or `[PRODUCT PROFILE]` blocks), for a deliberately unbranded film.
- `{libraryId}` / `{versionId}` (later, decided Q5: v1 ships `project` and `none` only) needs an optional override on `IDesignSystemPinService.BuildPinJsonAsync(projectId)` (it takes only the project today) and the choice persisted inside the pin, since the pin is the freeze point.
- Linter `WL012` fires when the brief or the workflow requires a design system and none is approved (`DesignSystem` step in `Require` mode already fails at run time otherwise).
- **Design system voice locale** (sibling asked "if the schema allows"): the schema allows it with one line. `DesignDocumentValidator.ValidateVoice` validates known keys only (`SH/DesignSystems/DesignDocumentValidator.cs:408`), so a `voice.locale` string (BCP-47) is an additive field plus the renderer line in `DesignSystemPromptView` (`:169`). Precedence: it is the **default** for `brief.language` when the brief sets none and the design is pinned; the brief always wins. **Decided (Q6, 2026-10-24): yes, add it.** Ownership: `scriptwriter-output-shape` if it lands the field first; otherwise P2-2 adds the validator line, the renderer line and a test.

### 3.7 Language, end to end

`language` is the one field whose absence caused the trigger failure, so its path is stated in full: assistant or run page sets `brief.language` (default offered from the user's UI language and the design system's `voice.locale`) to `run_brief_json` to `RunBriefAmbient` to the `[RUN BRIEF]` block for the writing agents and to `RunBriefOverrides` for `Voiceover`. Reviewers get a sentence to flag copy in the wrong language, which gives the review loop teeth. Captions are burned from the sanitized synthesized Voiceover text (`docs/voiceover.md`, "Captions"), so they follow the narration language with no extra work. Footage workflows that transcribe use `sourceLanguage`; `dub` reads `language` as the target and `sourceLanguage` as the source, replacing "the target language named in your brief" prose (`NarrationTranslatorAgent.cs:15`) with the field. Overlap with `scriptwriter-output-shape` is explicit in 3.11.

### 3.8 Consumption matrix: step type by brief field

`R` = reads through `[RUN BRIEF]` (agent roles), `O` = typed deterministic override, `S` = through the style snapshot, `-` = ignores. Cost class: `Free` (compute only), `Tokens`, `Paid` (voice or generation spend beyond tokens).

| Step type (agent) | Cost | language | lengthSec | voice | tone | format/res | designSystem | sourceFiles |
|---|---|---|---|---|---|---|---|---|
| `Agent`: 5 analyzers | Tokens | - | - | - | - | - | - | reads project files |
| `Agent`: RemotionComponentTranslator, AnimationStrategy | Tokens | - | - | - | S | S (author line) | pin blocks | - |
| `Agent`: Scriptwriter, Director, Author | Tokens | R | R | R | S | S | pin blocks | - |
| `Agent`: NarrationWriter, NarrationTranslator | Tokens | R (target) | R | R | S | - | - | - |
| `Agent`: VideoStoryEditor, MotionGraphicsPlanner (overlay text); ShotDirector (length); MusicSupervisor, SoundDesigner (none) | Tokens | R (overlay text, where listed) | R (cutters) | - | S | S | - | - |
| `EditRoom`, `GraphicsRoom`, `ColorGradeRoom`, `DailiesRoom` (rooms) | Tokens | R | R | - | S | S | - | - |
| `ReviewLoop` (reviewers) | Tokens | R (check) | R (check within 15%, proposed tolerance) | - | S | S | - | - |
| `VideoAnalyze` | Free (ASR/vision optional) | O `sourceLanguage` | - | - | - | - | - | O `BriefFiles` |
| `VideoCompile` | Free | - | O `TargetDurationSec` | - | S (caption look) | S/O | brand overrides exist | - |
| `Voiceover` | Paid | O | - | O | - | - | - | - |
| `VideoGenerate`, `AudioGenerate` | Paid | - | - | - | - | S | - | - |
| `DesignSystem` (Require/Compose) | Free | - | - | - | - | - | gate | - |
| `Extract`, `ExtractFrame`, `PinInsert`, `TimelineRender`, `Conditional`, `ForEach`, `Parallel`, `ThumbnailGenerate` | Free/Tokens | - | - | - | - | - | - | `PinInsert`/`ExtractFrame` may use `BriefFiles` |

**As built (T-004).** The roles paragraph of 3.4 is authoritative for agents; the generated table in `docs/run-brief.md` is the exact map. Language precedence is brief, then the style's language, then the design's `voice.locale` (the brand default), stated once in `[RUN BRIEF]`. `SpeechLanguageSupport` is a hand-written static table of what a provider publishes (Fish Audio only), a language off a published list adds a `voice_language_unlisted` warning, and `SpeechRequest.Language` is carried but neither shipped client sends it. `libraryVoiceId` resolves as a project-voice row through `IProjectVoiceResolver`. Colour-grade and dailies rooms read no brief field. `BriefFiles` is read by `VideoAnalyze` only.

Reading the matrix gives each later ticket its exact edit list. Cells marked `R` are each one registry row plus a test in the style of `StyleNudgeRegistry` tests; cells marked `O` are the only engine code paths with real logic (Voiceover language/voice, VideoCompile length, BriefFiles).

### 3.9 Step cache impact

- Current key inputs: `UserRequest` is hashed always (`ENG/Execution/Caching/StepCacheKeyBuilder.cs:64`), `RunStyle` only when present (`:68`), design-system block digests last and only when present (`:80`). `SchemaVersion` is `v9` (`ENG/Execution/Caching/StepCacheKeyInputs.cs:94`).
- **Rule for the brief:** add `RunBrief` as an optional key input holding a canonical string of **only the brief fields that step's role or type reads** (per the matrix), appended only when non-empty. A run with no brief fields hashes exactly as today, so existing entries stay valid and **no bump is needed for the brief by itself**. A cached step is therefore reused across runs that differ only in a field it ignores (an analyzer is reused across languages; that is the point).
- **What would force a bump:** changing the existing framing of `User Request:` / `Notes for this run:`, or changing a prompt text (the system prompt is hashed). `scriptwriter-output-shape` changes the Scriptwriter prompt and may bump `v9` to `v10`; the spec's implementation tickets must **not** bump a second time. Coordination rule in section 9.4.
- `DeterministicStepRevisions` is bumped for `VideoCompile`, `Voiceover` and `VideoAnalyze` when their override logic ships (`docs/workflow-execution-optimization.md`).
- Not cached today and unaffected: `Revise`, `ReviewLoop`, steps with active decision gates.

### 3.10 Surfaces

| Surface | Change |
|---|---|
| REST | `POST .../workflows/{id}/execute` (and retry/resume which reuse the stored brief): `brief` member. `POST .../workflows/{id}/lint` (new, 6.3). `GET .../workflows/{id}/app` returns manifest plus derived defaults. `GET .../workflows/{id}/estimate` unchanged (cost depends on resolution, not language) plus an optional `lengthSec` echoed only for display. `GET /api/v1/workflow-apps` (gallery list, 4.2). |
| Assistant | `ExecuteWorkflow` takes `brief` (`API/Agents/Tools/AssistantToolProvider.cs:251`); resolved by the same `RunBriefValidator` before the credit preflight, beside `RunStyleResolver`, so a bad brief costs nothing; the approval card lists each set field (`AssistantApprovalCardBuilder.RunAsync`, `:173`). Details in section 6. |
| MCP | The bridge is a thin proxy over the tool catalog (`mcp/src/server.ts`), so new and changed tools appear in `tools/list` automatically; `ExecuteWorkflow` over MCP keeps `acceptEstimateCredits` and gets `brief` like any argument. No card, so nothing else changes. |
| Run record | `GET execution` returns `brief`; run history shows language, length and voice next to style and shape (`WEB/components/workflows/ExecutionHistory.tsx`). |

### 3.11 Migration of existing workflows and templates

- **Existing workflows run unchanged**: no `run_brief_json`, no block, no override, same keys. Nothing is rewritten.
- **Templates:** the catalog stays the source. `WorkflowTemplateDefinition` gets an appended `AppManifest` member (null default, `SH/Workflows/WorkflowTemplateCatalog.cs:48`); provisioning copies it onto the new definition (section 4.1). Template `Version` bumps only for templates whose step shape changes; the manifest alone does not bump.
- **Template hygiene the brief makes possible:** `quick-win-promo` says "Silent unless you add a Voiceover step" (`WorkflowTemplateCatalog.cs:93`). With `voice.mode: narrated`, the linter says exactly that (`WL016`) and the assistant applies the closed patch `addVoiceover` (section 6.2) instead of improvising.
- **Hard-coded step references in the catalog** (review loop targets 10, 11, 6, 7, 9, 2; `Previous` refs): template edits must go through the `WL003`/`WL004` linter in a catalog test so inserting a step can no longer silently mis-target a loop. This is a test, not a template rewrite.

### 3.12 Boundary with `scriptwriter-output-shape` (no duplicated code work)

| Concern | Owner |
|---|---|
| Structured-output validation and repair turn; lenient `OnScreenText`; Voiceover non-retry; analyzer prompts for `.razor` | `scriptwriter-output-shape` |
| Scriptwriter, Director, Author, Translator **check they build from the design system** | `scriptwriter-output-shape` |
| A **minimal** language honouring through free text in its own scope, and the assistant prompt fix "goal specifics into `userRequest`/`briefFileId`" | `scriptwriter-output-shape` (interim) |
| The typed `language` field, `RunBriefRegistry`, `Voiceover` language and voice, `run_brief_json`, the assistant's `brief` argument | **this programme, step 2** |
| `voice.locale` in the design schema (Q6: yes) | whichever lands first; precedence fixed in 3.6 |
| `SchemaVersion` bump `v9` to `v10` | one bump only, by whichever initiative merges a prompt or framing change first; the other rebases |

The interim prompt fix does not conflict: when step 2 lands, the assistant's "goal specifics" sentence is *replaced* by "set the brief fields" in the same prompt section, in one commit that updates `AssistantAgent.cs`, `DatabaseSeeder.cs` and the consistency tests (`AssistantPromptConsistencyTests.cs:75`).

## 4. The app view

**One definition, two projections.** A workflow stays one `workflow_definitions` row with its steps. The *app* is a presentation of it: a title, a few inputs, Run, a price, a result. The graph is the same data behind an "Advanced" toggle. Nothing is copied, so there is no app that can drift from its graph.

### 4.1 The app manifest

New nullable column `workflow_definitions.app_manifest_json` (jsonb), next to `RequiresUserInput` and `Kind` (`SH/Data/Models/WorkflowDefinition.cs:19,26`; those two are the only workflow-level metadata that exists today). **Engine-owned table**, so the migration is an `ENG` migration (`AddWorkflowAppManifest`), the API maps it `ExcludeFromMigrations()`; DTO members append to `CreateWorkflowRequest` / `UpdateWorkflowRequest` (`API/Controllers/Dto/ProjectDtos.cs:249,318`) with null defaults.

```jsonc
{
  "version": 1,
  "title": "Explainer video",            // <= 60 chars; defaults to the workflow name
  "tagline": "A narrated walkthrough of your product, in your brand.",   // <= 140
  "category": "promo",                   // promo | footage | brand | utility  (gallery filter)
  "suggestedStylePreset": "corporate-explainer",   // optional; a chip and a pre-selection, never forced
  "inputs": [                            // ordered, <= 8 (the example shows 8), rendered top to bottom
    { "kind": "brief", "field": "language",  "label": "Language", "default": "auto" },
    { "kind": "brief", "field": "lengthSec", "label": "Length",   "default": 60, "choices": [30, 60, 90] },
    { "kind": "brief", "field": "format",    "label": "Shape" },
    { "kind": "brief", "field": "voice",     "label": "Narration" },
    { "kind": "brief", "field": "stylePreset", "label": "Style" },
    { "kind": "brief", "field": "designSystem", "label": "Design" },
    { "kind": "files", "role": "video", "label": "Your clips", "min": 1, "max": 20 },
    { "kind": "notes", "label": "Anything else?" }
  ],
  "result": { "kind": "video" },         // video | image | audio | files ; which output the run page leads with
  "advanced": { "allowGraph": true }     // false hides the toggle for locked, curated apps
}
```

Input kinds:

- `brief`: a field of the run brief (section 3.1). The label and `choices` are presentation only; the value goes through the same validators.
- `files`: a slot filled from project files by role. Resolved at run time through `VideoSourceKind.BriefFiles` (4.3), never by editing step config.
- `notes`: the free-text `userRequest`.
- `step` (**phase 3b, not v1**): a single scalar or enum setting of one step (`stepOrder` plus a JSON path into that step's config) offered as an input, for example "takes" on `VideoGenerate`. It is restricted to an allowlist generated from the config schema (scalars and enums only, no refs, no providers) and validated by `AppManifestSaveValidator` on every save. It is deferred because `stepOrder` is not stable under insert and delete (the same fragility that makes hard-coded review-loop targets risky); a stable per-step key (`workflow_steps.step_key`, engine migration) is its prerequisite (open question Q8).

**Derived manifest.** `AppManifestDeriver` (pure function, in `SH`) computes a manifest for any workflow with a null column from its steps: the brief fields some step reads (the matrix in 3.8: if a Scriptwriter step exists, `language` and `lengthSec` appear; if a `Voiceover` step exists, `voice`), a `files` slot per `VideoAnalyze` step whose source is `ProjectFile` (role `video`), `notes` when `RequiresUserInput`, and `format`/`stylePreset` when any compile or style-aware agent exists. Result: **all existing workflows and all 23 templates are apps on day one**, no migration, no authoring. An explicit manifest wins over the derived one field by field; the derived one is never persisted, so it improves for free when the registry improves.

**Templates ship curated manifests** (`WorkflowTemplateDefinition.AppManifest`, appended, null default, `SH/Workflows/WorkflowTemplateCatalog.cs:48`); provisioning copies it (`WorkflowTemplateProvisioningService.cs`). Curated manifests for the ten most used templates in phase 3c; the rest use the derived one.

**Validation (`AppManifestValidator`, shared by REST, the builder and the assistant):** every `brief` field must be one the workflow can read (a field no step consumes is rejected at save with the lint explanation, `WL011`, instead of being shown and ignored); `files` roles must match a step that can take them; at most 8 inputs; label length caps; `choices` must be valid values for the field.

### 4.2 The gallery of apps

Replaces the plain Workflows tab (`ProjectWorkflowsTab`, `WEB/components/projects/ProjectSections.tsx:191`, rendered at `WEB/app/(app)/projects/[id]/page.tsx:225`; `.../workflows/page.tsx` already just redirects there). The closest existing pattern is the template picker's radiogroup card grid (`WEB/components/workflows/TemplatePicker.tsx:57`, a `role="radiogroup"` `SimpleGrid`).

- Data: `GET /api/v1/projects/{p}/workflow-apps` returns, per workflow, `{workflowId, title, tagline, category, costClass, inputsCount, lastRun {status, finishedAt, thumbnail?}, source: template key | custom}; the recipe list carries `fit` (`ready` or `needs` plus a `missing[]` list with an action each, e.g. "Add clips") computed from the same readiness facts as `GetProjectReadiness` (6.1)` with the manifest (derived if needed). One request, no per-card fan-out.
- **Cost class badge** on every card, in words a marketer reads as cost: `No credits` (compute only; processing time is still charged, the tooltip says so), `Uses credits`, `Uses credits and a voice`, `Uses credits and AI shots` (the disclosure that real money is capped per run). Success and failure on a card carry an icon and a word as well as colour ("Finished", "Failed"). A `Needs` line is always a link that does the fix ("Add clips"). Derived from the step types (`SH` catalog, 7.1), never typed by hand. A numeric price is shown on the run page, not the card, because the estimate endpoint needs a saved workflow and depends on resolution (`WorkflowsController.cs:351`); cards for not-yet-created recipes show the class only.
- Sections: **Your apps** (the project's workflows, newest run first) and **Start from a recipe** (the template catalog; picking one provisions the workflow through the existing provisioning service and opens its run page, so there is no empty "new workflow" form between the person and a result).
- Filters: category chips; search by title/tagline. Empty project state: three recipe cards matched to the project's contents (files present decide which: code archive then promo recipes, video clips then footage recipes), computed from `GetProjectReadiness` data (section 6.1).
- The graph builder entry points move: "New workflow" becomes "Custom workflow (advanced)" at the end of the recipe list.

### 4.3 The app run page

Route: `/app/projects/{id}/workflows/{workflowId}` (the existing detail route, `WEB/app/(app)/projects/[id]/workflows/[workflowId]/page.tsx`, today the builder; it becomes the app page with the builder behind the toggle, so every existing link keeps working).

Layout (desktop, two columns; wireframe in section 5): left, the inputs in manifest order; right, the result area (last run's video, or an empty state that says what will appear) and run history underneath. Below the inputs, one **Run** button with the price line. Everything the person can change is a brief field, so the page and the assistant write the same record.

- **Inputs render from the manifest** with the existing controls: `RunStylePicker` for style, shape and resolution (`WEB/components/styles/RunStylePicker.tsx`, options and plan caps from `components/styles/style-options.ts`), a language select, a length stepper or choice chips, a narration switch plus voice picker, a file picker for each slot, a notes textarea. `ExecuteWithInputModal` (193 lines, today the only place these are asked, `WEB/components/workflows/ExecuteWithInputModal.tsx`) is retired for apps: its logic (format suggestion from the preset, plan caps through `useOutputHeightCap`, voice-provider check, silent-run notice) moves into the page's form. The modal remains for the Advanced/graph view's Run button until phase 3d removes it. To avoid two code paths drifting, 3c **first extracts that logic into one shared hook** (`useRunForm`: format suggestion, plan caps, voice-provider check, silent-run notice) used by both, so `ExecuteWithInputModal.silent.test.tsx` and the new page test exercise the same code. `RunStylePicker` bundles style, shape and resolution in one component; the manifest renders them as independent inputs, so 3c splits it (or adds a prop selecting which controls to show) before promising independent inputs.
- **File slots:** `files` inputs resolve at run time. New `VideoSourceKind.BriefFiles` (append-only enum, `SH/Workflows/VideoSource.cs`) with a `role`; when the step runs it expands to the run's `brief.sourceFiles[role]` (the same expansion pattern as `GeneratedClips`, which "expands to one source per delivered clip"). Static `ProjectFile` sources keep working. Linter `WL010` fires when a required slot is empty or the project has no files of that role; the page disables Run with a plain sentence ("Add at least one video clip") and a one-click upload, never a generic error.
- **Readiness strip** above Run, from the linter (6.3). Each state has its own icon **and** a word, never colour alone: "Fix needed" (error, with a fix button when one exists: "Add a voice step", "Choose a voice you have consent for"), "Check" (warning), "Ready" (all clear), "Note" (info). The strip is a labelled region with `role="status"` (`aria-live="polite"`) so a new blocker is announced; amber and grey text meet 4.5:1 on the pane background. Run is `aria-disabled` while an error stands, stays in the tab order, and links the reason with `aria-describedby`; a warning never disables it. The page never lets the person press Run into a known failure.
- **States the page must specify** (each is a row in the build ticket's acceptance list):

| State | What the person sees | Primary action | Run |
|---|---|---|---|
| First visit, no run | Result pane: "Your video will appear here" plus what the app needs | Fill the inputs | Enabled when ready |
| Required file slot empty / no files of that role | "Add at least one video clip" | Upload or pick files | Disabled with reason |
| No voice provider, or voice without consent | "Narration needs a voice: ask your admin to connect one" / "You need consent on file for this voice" | Choose another voice, or open Voices | Disabled with reason |
| Plan does not allow the choice (resolution, AI shots, audio generation) | The plan sentence from the 402 body, the allowed alternative preselected | Choose the allowed option, or see plans | Disabled with reason |
| Not enough credits | The shortfall sentence ("about 40 needed, you have 12") | Add credits | Disabled with reason |
| Estimate unavailable (endpoint failed) | "We could not work out the price just now" | Retry; Run stays available, the server's 402 remains the authority | Enabled |
| Recipe not yet created | Run page opens after provisioning; a skeleton while it is created | None | Disabled until ready |
| Running | Plain-language progress (below) | Stop | Replaced by Stop |
| Failed | A banner on the page with the plain reason from the failure code, and Retry or Resume (the existing gates) | Retry / Resume / Ask the assistant | Enabled for a new run |
| Finished | Result leads | Download, Edit in the editor, Change one thing and re-run | Enabled |

- **Focus and announcements:** focus order is inputs, readiness strip, Run, result; a failed validation moves focus to the first field with a problem; completion is announced in the result region. The "App | Advanced" control is a real radiogroup (`role="radiogroup"`) kept in sync with `?view=advanced`.
- **Result:** the run page leads with the result kind from `manifest.result`: the playable video, with download, "Edit in the editor" (the existing handoff; shown only when the video editor is enabled, `VIDEO_EDITOR_ENABLED`, `WEB/lib/editor/config.ts:21`: the editor is off in 0.1.0-alpha and its paths redirect to `/editor-coming-soon`, `docs/user-guide/editor/index.md:11`), "Change one thing and re-run" (pre-fills the form from the previous run's `brief`) and "Ask the assistant to change something" (opens the assistant with the run in context). A running run shows the existing step progress as a plain-language list ("Writing the script", "Recording the narration", "Rendering"), not step type names; the technical step list and results stay one click away (`StepResultPanel`).
- **Runs, errors and retry** reuse the existing execution page and gates unchanged (`.../executions/[executionId]/page.tsx`, retry, resume, `RegenerateVideoClipsModal`); the brief is copied by retry and resume (3.3).

### 4.4 Price display

One component, one source: `GET .../estimate` (`WorkflowCostEstimateResponse`, `API/Controllers/Dto/WorkflowCostEstimateDto.cs:36`), rendered by the existing `RunCostEstimate` (`WEB/components/billing/RunCostEstimate.tsx`): "about X credits (usually A to B)", "plus up to N for AI shots" for fixed-price generation, the shortfall sentence, and the per-step breakdown behind a disclosure. The run button carries the number ("Run · about 40 credits") and the range sits on the line below it ("usually 30 to 55"), so the label does not wrap on a phone. The query takes `outputResolution` today and so the price line re-fetches when the resolution field changes; `language`, `style`, `tone` and `voice` do not change the numbers and do not trigger a refetch, which is stated here so nobody wires a refetch to every field. The "shown is not held" rule stands: the line shows the typical range, the 402 is judged on `RequiredMillis` (`docs/metering-and-billing.md`, "The pre-run estimate"). `Free` apps say "No credits used".

### 4.5 The Advanced toggle and the graph editor restyle

- **Toggle:** a segmented control "App | Advanced" at the top of the workflow page, persisted per workflow in the URL (`?view=advanced`) and remembered per user. Advanced is the existing builder (`FlowchartBuilder`, 642 lines, React Flow; below 768 px `WorkflowStepList`, `FlowchartBuilder.tsx:624`) with its step config drawer (`StepConfigSheet`). Editing the graph can invalidate the manifest (a deleted step an input points at); the builder shows the manifest's input list as a panel and re-validates it on every save (`AppManifestValidator`), blocking a save that would orphan an exposed input with a message that names it.
- **Exposing a setting** (v1 authoring): in Advanced, a panel "App inputs" lists the derived inputs with switches and reorder handles; an author can hide a derived input, add a `brief` field (only readable ones are offered), relabel, set a default. Step-level "Expose as input" on a field (the `step` kind) is phase 3b: the hook is the per-field wrappers `config-kit.tsx` already provides to `SchemaDrivenStepConfigForm` (`WEB/components/workflows/SchemaDrivenStepConfigForm.tsx`), whose schema registry is static (`WEB/lib/schemas/workflow-step-configs.ts`, no backend schema fetch).
- **Restyle to the editor's design** (scope fixed: gallery, run page, graph editor). Today the graph uses inline Mantine styles and a per-type accent (`WEB/components/workflows/nodes/StepNodeShell.tsx:68`), only two CSS modules exist for the folder, and the global theme is minimal (`WEB/app/theme.ts:1`). The editor has a token set: `--rb-ed-surface`, `-studio-bg`, `-pane-bg`, `-inspector-bg`, `-header-bg`, `-line`, `-line-strong`, `-subtle`, `-chip-bg`, `-lane` and the rest, light and dark (`WEB/app/global.css:84-103`). The restyle **consumes those tokens read-only**: node cards, canvas background, toolbar, drawer and step list use `--rb-ed-*` and the editor's chip/pane patterns; no editor file is edited, no token is added to the editor's set (collision rule, section 9.4). Contrast and the existing a11y contract of `docs/design/workflow-builder-ux/interaction-spec.md` (fit view `F`, minimap above 5 steps, tab order, drawer on desktop and bottom sheet on mobile) stay as shipped and are not re-opened. Mantine v9 remains the component library; the "Early preview" label rule applies to editor surfaces only, not to these.
- **Tests that must stay green** (list from the web scout): `WEB/app/(app)/projects/[id]/workflows/**/page*.test.tsx`, `components/workflows/{FlowchartBuilder*,StepConfigSheet,SchemaDrivenStepConfigForm,StepCard*,TemplatePicker,RunAndTemplates,ExecuteWithInputModal.silent,nodes/*}.test.tsx`, `components/billing/RunCostEstimate.test.tsx`, `components/styles/RunStylePicker.test.tsx`, and e2e `workflow-builder-core.spec.ts`, `workflow-builder-regressions.spec.ts`, `assistant-diff-flow.spec.ts`, `render-resolution.spec.ts`.

### 4.6 Phone layout

The existing breakpoint is 48 em (768 px) for the workflows pages (`FlowchartBuilder.tsx:624`, executions page `:107`); the editor uses its own phone rule (under 600 px wide, or under 500 px tall with a coarse pointer, `PHONE_MAX_WIDTH`, `WEB/lib/editing/device-class.ts:19`). The app view uses the **workflows** breakpoint (the editor's rule is editor-only and stays so). Below it: single column, inputs first (language, length, shape, narration as full-width rows; file slots as a button that opens the project file sheet), a sticky bottom bar (safe-area inset padding, `dvh` so the keyboard never covers the focused input) holding a one-line status, the price line and a full-width **Run** at least 48 px tall; when an error blocks, the button reads the fix ("Add a clip") and does it. Result below inputs, history as a list. The gallery is one card per row with the cost badge on the card. Segmented choices (length, shape) are 44 px minimum per segment. **Advanced on a phone:** the App | Advanced control is not shown; "Edit steps (advanced)" sits in the overflow menu and opens the existing `WorkflowStepList` editor (tap to reorder, no drag), unchanged, because a step list is a poor first screen for a marketer.

## 5. Wireframes (low fidelity)

HTML versions of these three screens: [`workflow-apps/wireframes.html`](workflow-apps/wireframes.html).

### 5.1 Gallery (desktop)

```
+--------------------------------------------------------------------------------------+
| Project: Northwind Staffing portal                         [ + Start from a recipe ] |
| [Apps] [Files] [Voices] [Outputs] [Design] ...                                       |
+--------------------------------------------------------------------------------------+
| Your apps                                   filter: [All] [Promo] [Footage] [Brand]  |
|                                                                                      |
| +--------------------------+  +--------------------------+  +----------------------+ |
| | [thumb of last result]   |  | [thumb of last result]   |  |  (no run yet)        | |
| | Explainer video          |  | Highlight edit           |  |  Brand profile       | |
| | A narrated walkthrough   |  | Tight cut from your      |  |  Build it once, reuse| |
| | of your product.         |  | clips.                   |  |  it in every video.  | |
| | [Uses credits + voice]   |  | [No credits]             |  |  [Uses credits]      | |
| | (ok) Finished 2h ago     |  | (!) Failed               |  |  Not run yet         | |
| | [ Open ]                 |  | [ Open ]                 |  |  [ Open ]            | |
| +--------------------------+  +--------------------------+  +----------------------+ |
|                                                                                      |
| Start from a recipe                                                                  |
| +--------------------------+  +--------------------------+  +----------------------+ |
| | Quick vertical reel      |  | Narrated launch trailer  |  | Dubbed video         | |
| | [Uses credits + voice]   |  | [Uses credits + voice]   |  | [Uses credits+voice] | |
| | (ok) Your code is here   |  | (ok) Your code is here   |  | (!) Add video clips >| |
| | [ Use this recipe ]      |  | [ Use this recipe ]      |  | [ Use this recipe ]  | |
| +--------------------------+  +--------------------------+  +----------------------+ |
| +--------------------------+                                                         |
| | Custom workflow          |  (advanced: build your own steps)                       |
| +--------------------------+                                                         |
+--------------------------------------------------------------------------------------+
```

The "(ok)" and "(!)" lines come from the readiness facts (6.1): code present, clips present, design approved, voice configured; a "(!)" line is a link that fixes it.

### 5.2 App run page (desktop)

```
+--------------------------------------------------------------------------------------+
| < Apps    Explainer video                                  ( App ) ( Advanced )      |
+------------------------------------------+-------------------------------------------+
| Language        [ Italiano           v ] |                                           |
| Length          (30s) [60s] (90s)        |        +---------------------------+      |
| Shape           [ 16:9 ] ( 9:16 ) ( 1:1 )|        |                           |      |
| Narration       [x] Narrated  [Aria v]   |        |   result video            |      |
| Style           [ Corporate explainer v ]|        |   (or: "Your video will   |      |
| Design          Northwind v6 [Change]    |        |    appear here")          |      |
| Anything else?  [ keep intro under 5 s ] |        +---------------------------+      |
|                                          |        [Download] [Edit in the editor]    |
| Ready: Voice is set up                   |        [Change one thing and re-run]      |
| (!) Check: Aria may not speak Italian    |        [Ask the assistant]                |
|                                          |                                           |
| [ Run · about 40 credits ]               |   Runs                                    |
|   usually 30 to 55 · 1080p · why? v      |   Today 10:12  (ok) Finished  60s  [open] |
|                                          |   Today 09:40  (!) Failed          [open] |
+------------------------------------------+-------------------------------------------+
```

States (not drawn): empty slot, no voice provider, plan limit, not enough credits, estimate unavailable, running, failed banner with Retry: see the table in 4.3.

### 5.3 App run page (phone, below 768 px)

```
+---------------------------+
| < Apps  Explainer video   |
|                   ( ... ) |  <- overflow: "Edit steps (advanced)"
+---------------------------+
| Language   [ Italiano  v ]|
| Length     (30)[60](90)   |
| Shape   [16:9](9:16)(1:1) |
| Narration  [x]  Aria  >   |
| Style      Corporate   >  |
| Design     Northwind v6   |
| Anything else?            |
| [..................]      |
|                           |
| Last result               |
| [  video  ]               |
| Runs: Today 10:12 Finished|
+---------------------------+
| (!) Check Italian voice   |   sticky bar: status line,
| [ Run · about 40 credits ]|   Run >= 48 px, full width,
|   usually 30 to 55        |   safe-area padding
+---------------------------+
```

When an error blocks, the Run button reads the fix ("Add a clip") and performs it.

### 5.4 Graph, "Advanced" (desktop)

```
+--------------------------------------------------------------------------------------+
| < Apps    Explainer video                                  ( App ) ( Advanced )      |
+--------------------------------------------------------------------------------------+
| [+ Add step] [Fit F] [Auto layout]            [ App inputs (8 shown) > ]             |
+--------------------------------------------------------------+-----------------------+
|  canvas (editor tokens: studio background, lanes, minimap)   |  inspector drawer     |
|                                                              |  Step 4: Write script |
|  [1 Check profile]--[2 Plan scenes]--[3 Plan animation]      |   What it uses from   |
|                                   |                          |   your brief:         |
|  [4 Write script]-[5 Record voice]-[6 Plan shots]-[7 Build]  |   language, length,   |
|        -[8 Review]  (loops back to 7)                        |   narration           |
|  badges: [No credits] [Uses credits] [Uses a voice]          |                       |
|                                                              |   (later: Show as     |
|                                                              |    an app input)      |
+--------------------------------------------------------------+-----------------------+
| Checks: 1 thing to review (the Italian voice)                            [ Details ] |
+--------------------------------------------------------------------------------------+
```

"App inputs" opens a panel (not drawn) listing the derived inputs with a switch, a relabel field, a default and reorder handles, plus "Add a field" offering only fields some step can read.

Notes on the wireframes: every label is plain language (no step type, model, id or credit arithmetic beyond the one price line); technical step names in the Advanced canvas are the builder's existing step labels, replaced here by plain names for the sketch; colours in the HTML are placeholders, the build uses the editor's `--rb-ed-*` tokens and re-checks contrast on every new pairing (badge text on chip background, amber on pane background). "What it uses from your brief" is generated from the registry (3.8), which is what makes the manifest trustworthy to an author. "Show as an app input" on a step field is the phase 3b `step` kind and is marked later.

## 6. Assistant behaviour: recipe-first

### 6.1 Principle and procedure

The assistant is a **recipe selector and brief filler**, not a graph author. Its job for a goal is to produce a validated brief for the right recipe and let the platform do the rest. The procedure (this text goes into the assistant prompt as one section, mirrored in `DatabaseSeeder.BuiltInAgents` and pinned by consistency tests, 6.5):

1. **Extract the brief** from the user's message and the page context: language (the language they wrote in unless they ask otherwise), length, shape, tone, narrated or not, design system mention, which files. Anything a field can hold goes in a field; nothing run-affecting lives only in a workflow name or in prose.
2. **Look at the project once**: `GetProjectReadiness` (new, read-only): files by kind and count (code archive, video clips, images, music, sound effects, documents), design system status (none, draft, approved version), voices (library, consented), configured providers by capability (voice, video generation, transcription, vision), plan limits (resolution cap, entitlements), existing workflows with the template key each came from. The assistant never asks a question this call can answer.
3. **Fit**: `ListWorkflowTemplates(goal)` (changed): each recipe returns `{key, title, forGoals[], needs[], cost class, briefFieldsRead[], satisfied: bool, missing: [..]}` computed against the readiness data, so the model reads a fit signal rather than judging descriptions.
4. **Decide** (ordered; the first that applies):
   1. **Reuse.** The project already has a workflow from the fitting recipe: do not create another. Call `ExecuteWorkflow` with the brief. (Today every goal creates a new workflow; the trigger run's name "Note spesa bozza - explainer IT v1" is a symptom.)
   2. **Create from recipe.** `ProposeCreateFromTemplate(templateKey, brief, patches[])` / `ApplyCreateFromTemplate` (new): the server instantiates the template, applies the brief's bound settings and the **closed patches** deterministically, names the workflow from the recipe title, and returns the diff plus lint. The model never copies step JSON.
   3. **Recipe plus patch.** The recipe fits except for a known gap. The patch catalog is closed and small (initial ids, each a deterministic graph transform with its own tests and lint): `addVoiceover` (the Voiceover step, `ScriptScenes` or `NarrationPlan` by family, with the review loop's target re-pointed), `addCaptions`, `addMusic` (needs a track), `addSfx` (needs files), `swapToRoom` (edit room variants), `addColorGrade`, `addGraphics`. A patch is refused (not improvised) if the linter says it cannot apply.
   4. **Custom graph.** Only when **all** hold: no recipe has `satisfied` or patchable fit; every step the goal needs exists in `GetStepCatalog`; the assistant states in its lead-in why no recipe fits. It builds with `ProposeWorkflow`, must call `ValidateWorkflow` (now the linter) and reach **zero errors** before proposing, and the workflow still gets a derived app manifest, so even custom work shows as an app.
   5. **Decline with an alternative.** The capability does not exist (a talking avatar, an image carousel, reformatting a finished video). One sentence on what ReelBolt cannot do, then the nearest real option. (Subtitles-only used to be here; since Q16 it is the shipped recipe `video-subtitles`, or `video-subtitles-translated` when the brief names another language.) Never invent a graph to approximate it.
   6. **Clarify first.** At most two questions, only for a required brief field the project data and the message cannot supply (how long? which clips?). Never ask what `GetProjectReadiness` answers, never ask about fields with sensible defaults.
5. **Price before commit.** `EstimateWorkflowRun(workflowId, brief)` (new, read-only) gives the typical range and the paid-item disclosure; the assistant says it in one line (existing rule: "say in one line what it will use", `AssistantAgent.cs:235`). The run card shows the same numbers (6.4).
6. **Never** (kept from today and sharpened): ids never appear in replies (names only); no question in prose before an `Apply*` or `ExecuteWorkflow` (the card is the confirmation); a workflow is never made or run without the brief fields the user stated.

### 6.2 What changes in the tool surface (summary; full list in 7.2)

New: `GetProjectReadiness`, `GetStepCatalog`, `ProposeCreateFromTemplate`, `ApplyCreateFromTemplate`, `EstimateWorkflowRun`. Changed: `ListWorkflowTemplates`, `GetWorkflowTemplate`, `ValidateWorkflow`, `ExecuteWorkflow`, `SearchPlatformDocs`.

### 6.3 The pre-run linter

`WorkflowLinter` in the Inference API (`API/Services/Workflows/`), because its rules need database facts the pure `SH` code cannot see (providers, plan, files, voices, design approval). It composes: the existing save validators (`StepConfigSaveValidator`, `StepOptionsSaveValidator`, `StepProviderOwnershipValidator`), `WorkflowStepDependencyGraph` (`SH/Workflows/Replay/WorkflowStepDependencyGraph.cs`) for reference analysis, the readiness facts, and the new rules below. **One rule table** (`StepCompatibilityRules`, `SH`) feeds both the linter and the step catalog's "needs / produces / pairs with" so the facts the assistant reads and the checks the platform enforces cannot diverge.

Entry points: `POST /api/v1/projects/{p}/workflows/{id}/lint` (saved workflow plus optional brief), the same with a `steps` body for an unsaved draft, `ValidateWorkflow` (assistant), `RunStartGate` (enforcement), the app run page (readiness strip), and the builder's save (inline findings). Finding shape: `{ruleId, severity: error|warning|info, stepOrder?, message, messageKey, args, fix?: {kind, label, patchId?, labelId?, args?}}` — `message`/`label` stay the English sentences the assistant and MCP read, while `messageKey`/`labelId` name the catalogue copy (`lint.<messageKey>`, `lint.fix.<labelId>`) and `args` carries its tokens (docs/workflow-linter.md, "The message keys and their args").

**Enforcement policy.** `error` only for conditions already proven to fail at run time today (a run that would have failed anyway now fails before spending anything: `409 WORKFLOW_LINT_ERRORS` from `RunStartGate`, with the findings). Everything else is `warning` or `info` and never blocks. Rolled out behind a flag, first as warn-only with telemetry of what would have blocked (`Workflows:Lint:Enforce`, default off until the false-positive review in phase 2 exit criteria).

Initial rules (Q2: warn first; later block only the errors that would fail the run anyway), derived from the real step types and the real mistakes the scout found:

| Id | Name | Fires when | Sev | Existing check (today) | Run-time symptom it prevents |
|---|---|---|---|---|---|
| WL001 | voiceover-source-missing | `Voiceover` source (`ScriptScenes` / `NarrationPlan` / `PickupPlan`) refers to a step that does not exist, is later, or is not a Scriptwriter / NarrationWriter / PickupPlanner | error | only shape (`StepConfigSaveValidator.cs:992`): `ScriptScenes` order has no check at all, `NarrationPlan`/`PickupPlan` only `>= 1` | `SCRIPTWRITER_NOT_FOUND`, `SCRIPT_SOURCE_INVALID`, `SCRIPTWRITER_OUTPUT_INVALID`, `PICKUP_STEP_NOT_FOUND`, `PICKUP_OUTPUT_INVALID` (`VoiceoverStepExecutor.cs:338,472,482`) |
| WL002 | compile-voiceover-unwired | `VideoCompile` `enableVoiceover` (or `enablePickups`) with no earlier `Voiceover` at the given order | error | none; silently skipped (`VideoCompileStepExecutor.cs:6392,6402`) | mute film, no error |
| WL003 | reviewloop-target-invalid | `ReviewLoop.LoopTargetStepOrder` missing, forward, or not the producing step | error | none (`ReviewLoopStepExecutor.cs:321`) | loop never loops |
| WL004 | compile-ref-invalid | `decision`, `analysisStepOrder`, `takeSelection`, `Previous` refs missing, forward or wrong type | error | none | wrong cut or `INPUT_MISSING` |
| WL005 | compile-mode-conflict | music, voiceover, graphics, sfx, grade, region effects, captions with `Mode = StreamCopy` | error | only music gain range | `*_REQUIRE_REENCODE` codes |
| WL006 | provider-missing-for-capability | step needs a capability (SpeechSynthesis, VideoGeneration, MusicGeneration, Transcription, Vision) with no default or chosen provider | error | `NO_AUDIO_PROVIDER` etc. at run time | step fails after earlier spend |
| WL007 | plan-not-entitled | step feature or the chosen resolution is not on the plan (video generation, audio generation, segmentation, resolution) | error | `PLAN_NOT_ENTITLED` at start or run | refused late |
| WL008 | voice-consent-missing | chosen project voice not selectable (`IsSelectable`) | error | `VOICE_CONSENT_MISSING` at run time | step fails |
| WL009 | voice-language-mismatch | `brief.language` not in the chosen provider/voice's stated languages (unknown support = this rule, not silence) | warning | none (language is not even read today) | wrong-language or garbled narration |
| WL010 | source-media-missing | a required file slot is empty, or the project has no files of the needed role (clips, code archive, music track, SFX) | error | `INPUT_MISSING` / empty analysis at run time | failed or empty run |
| WL011 | brief-field-unread | a brief field is set (or exposed in the manifest) but no step in this workflow reads it (matrix 3.8): language on `green-screen-insert`, style on a no-agent graph | info | none | silently ignored choice |
| WL012 | design-system-missing | `DesignSystem` `Require` step, or `brief.designSystem` expects a pin, and no approved version exists | error | `DesignSystem` step fails at run | late failure |
| WL013 | paid-step-in-loop | `VideoGenerate` / `Voiceover` inside a review loop's repeat span | warning | only a spread reason in the estimate (`video_generation_in_review_loop`) | cost multiplier surprise |
| WL014 | forward-step-reference | any `*StepOrder` reference to a later step | error | only in `DesignSystem` Compose (`StepConfigSaveValidator.cs:942`) | missing input |
| WL015 | egress-permission-missing | step sends source media to a vendor (region effects, keyframes) with `AllowSourceMediaEgress` unset | error | `EGRESS_REFUSED` at run | refused late |
| WL016 | silent-output | `brief.voice.mode = narrated` but the workflow has no voice step (the `quick-win-promo` family is "silent unless you add a Voiceover step") | warning with fix `addVoiceover` | none | a mute video the user asked to be narrated |
| WL017 | length-unreachable | `lengthSec` longer than the source footage or a compile with no target wiring | warning | none | missed length |
| WL018 | manifest-orphan | an app input refers to a field/slot/step the graph no longer has | error at save | none (no manifest yet) | dead control |
| WL019 | captions-without-text-source | `VideoCompile` `enableCaptions` with no applied `Voiceover` and no `captionSource = Transcript` with a transcribing analysis | error | `CaptionSource.Voiceover` (default) burns only the Voiceover step's own text and needs `EnableVoiceover`; `CaptionSource.Transcript` (lane 3e) needs a transcribing analysis; not checked at save | captions flag with nothing to burn |
| WL020 | review-before-final-cut | a `ReviewLoop` ordered before a later `Voiceover`, `AudioGenerate` or `VideoCompile` | warning | none | the review judges a silent picture |

`WL011` and `WL016` are what turn the trigger run into a visible problem at the right time instead of an English silent film. The linter's own test corpus is the 18 `lint` expectations in the eval set plus one golden failing workflow per rule.

### 6.4 Approval cards

The existing card machinery is reused (`AssistantApprovalCatalog.cs:17`: every `Apply*` and `ExecuteWorkflow` is gated by name; `AssistantApprovalCardBuilder.cs` (`RunAsync`, `:173`, body to about `:760`) builds the run card from the database and the exact arguments). Changes:

- **Run card** lists each brief field that is set, in plain words, ahead of the price: "Language: Italian", "Length: about 60 seconds", "Narration: Aria", "Design system: Northwind Staffing v6", plus the existing style, shape, resolution and clip lines. A set field the workflow cannot read (`WL011`) appears as "Not used by this app: language" so the card is honest.
- **Create card** (`ApplyCreateFromTemplate`, covered automatically by the "every `Apply*`" rule): "Create {title} from the {recipe} recipe", step count, patches applied, cost class (never vendor prices), any lint warnings.
- **Lint errors never reach a card**: a proposal with errors is refused to the model with the findings so it fixes the proposal; the card is only for decisions a person can make.
- Auto mode unchanged (no card; deletes and paid runs included, `docs/platform-assistant-mcp.md`). The linter's errors still apply in auto mode because they run in the tools, not in the card.

### 6.5 Guardrails kept, and how new behaviour is pinned

- Names, never ids: `AssistantReplySanitizer` (`API/Services/Assistant/AssistantReplySanitizer.cs`) maps ids back to names it learned from tool results, so every new tool must return **names** for everything the model might repeat (recipe titles, voice names, file names, design system names), never only ids.
- `AssistantInputGuard` unchanged. `Propose*`/`Apply*` token pattern unchanged (`IConfirmationTokenService`; the confirmation payload for `ApplyCreateFromTemplate` binds `{templateKey, normalized brief, patch ids, workflow name, userId}` recomputed server-side on apply).
- Pinned with `AssistantPromptConsistencyTests`-style tests (`inference/tests/ReelBolt.WorkflowEngine.Tests/Agents/AssistantPromptConsistencyTests.cs`): the new "Choosing a recipe" section is in `AssistantAgent.cs` and `DatabaseSeeder.cs` byte-identically (`:75`), names every new tool (`:115`), declares the new count (`:163`), contains the key phrases of the procedure (`Contain` assertions like `:220`), and carries no future-tense language (`:243`). Each new tool has a description (`AssistantAgentToolScopeTests`) and an `AssistantToolProgress` entry (`AssistantToolProgress.cs:16`).
- Behaviour itself is pinned by the 50-goal eval (section 8), which is the regression gate for prompt edits.
- No thread-to-workflow link exists today (`AssistantThread` has no workflow reference, nothing records who created a workflow). v1 adds `workflow_definitions.created_via` (`user | assistant | template`) and the assistant thread id on creation through the tool context, so a run can be traced to the conversation that made it (today it has to be found by hand). Engine-owned migration, folded into `AddWorkflowAppManifest`.

## 7. Knowledge: facts from tools, know-how from docs

Two layers, two sources of truth, kept apart on purpose. **Facts** (what steps exist, what each needs and produces, what pairs with what, what a recipe contains, what a run will cost, what the project has) come from deterministic tools generated from code, so they cannot be stale and the model cannot misremember them. **Know-how** (which recipe for which goal, how to write a good brief, how work flows between steps, common mistakes and fixes) lives in docs, retrieved by search. A fact that appears in both is generated into the docs from the same source, never typed twice.

### 7.1 Facts: the step catalog (generated from code)

`StepCatalog` in `SH`, built at runtime from code, with a drift-guard test that fails the build if a `StepType` has no card (same pattern as `ToolScopingDriftGuardTests`). Sources: the `StepType` enum (`SH/Data/Models/Enums.cs:296`), `StepConfigSchemaExporter.GetAllSchemas()` (`SH/Workflows/StepConfigSchemaExporter.cs:17`, one entry per type; field shapes only today, which is what `ListStepSchemas` returns), output contracts in `SH/Data/OutputSchemas.cs`, `ToolGroupCatalog` (`SH/Agents/ToolGroupCatalog.cs:27`), `WorkflowTemplateCatalog` (`:54`), `StepCompatibilityRules` (new, shared with the linter), the run-brief registry (3.4) and step-cost metadata. A small attribute on each config type, `[StepCard(Purpose, Needs, Produces, CostClass)]`, carries what code cannot infer; everything else is reflected.

A card (`GetStepCatalog(stepType?)` returns one or all): `{stepType, purpose (one plain sentence), costClass, needs: [{kind, description, lintRule}], produces: {shape, readableBy[]}, readsFromRun: [brief fields], pairsWith: [{next, via}], commonMistakes: [{ruleId, fix}], configFields: [schema]}`. Output contracts are named by schema, not pasted. **Prices are not in the catalog**: cost class only; numbers come from `EstimateWorkflowRun` at call time (vendor cost lists stay out of every index, as `model-catalog-prices.md` is, `PlatformDocsCorpus.cs:69`).

### 7.2 Tool changes: 86 to 91

Today: exactly 86 tools. The prompt says "You have exactly 86 tools" (`AssistantAgent.cs:127`); `AssistantPromptConsistencyTests.cs:163` only checks that the prompt's number equals the provider's registered count (it follows the count); the literal 86 is pinned in `AssistantAgentToolScopeTests.cs:73`, `AssistantAuthorityTests.cs:403` and the prompt copy in `DatabaseSeeder.cs:2872`.

| Tool | Kind | Change |
|---|---|---|
| `GetProjectReadiness` | new, read-only | Files by kind, design status, voices, providers by capability, plan limits, existing workflows with template keys. Names only |
| `GetStepCatalog` | new, read-only | The cards of 7.1 |
| `ProposeCreateFromTemplate` / `ApplyCreateFromTemplate` | new pair | Server-side instantiation with brief and closed patches (6.1) |
| `EstimateWorkflowRun` | new, read-only | Wraps the estimate (`WorkflowsController.cs:351`) for a workflow and brief; for a recipe not yet created, estimates from the template's cold-start plan (the endpoint needs a saved workflow today, `WorkflowTemplateProvisioningService` must offer a dry instance) |
| `ListWorkflowTemplates` | changed | Adds `goal` argument and per-recipe `forGoals`, `needs`, `satisfied`, `missing`, `costClass`, `briefFieldsRead`, `suggestedStylePreset` (`AssistantKnowledgeTools.cs:99`) |
| `GetWorkflowTemplate` | changed | Adds `appManifest` and the brief bindings (`:103`) |
| `ValidateWorkflow` | changed | Runs the linter; optional `projectId`, `brief`; returns findings with fixes (`AssistantToolProvider.cs:236`) |
| `ExecuteWorkflow` | changed | `brief` argument; card lists fields (`:251`) |
| `SearchPlatformDocs` | changed | Filters `stepType`, `kind`, `capability`, `costClass`; hits carry the new metadata (`AssistantKnowledgeTools.cs:94`) |
| `ListStepSchemas` | unchanged | Kept for config shape; the prompt points to `GetStepCatalog` for meaning |

Every site that moves with the count: the prompt, `DatabaseSeeder.BuiltInAgents` (byte-identical), `AssistantPromptConsistencyTests`, `AssistantAgentToolScopeTests` (literal at `:73`), `AssistantAuthorityTests` (literal at `:403`), `AssistantToolProgress`, the MCP tool catalog (automatic through `tool-catalog`; `docs/design/mcp-tool-inventory.md` and `docs/platform-assistant-mcp.md` updated), and `CLAUDE.md` (86 at its tool-count lines; a stale 77 survives in prose at lines 166 and 197, the docs ticket fixes both once). `CLAUDE.md` is edited **once, at the end** of step 3 (cache-prefix rule), not per ticket.

### 7.3 Know-how: the docs set to write

Written for the assistant first, with customers able to read the recipe pages. **Placement matters:** the indexer includes `user-guide/**` at any depth and **top-level** `docs/*.md` only; `docs/design/` and every other subfolder are excluded (`API/Services/PlatformDocs/PlatformDocsCorpus.cs:69,115`). So this spec itself is deliberately not retrievable. New knowledge docs go in a new top-level folder admitted by one added prefix in `IsIncluded` (`docs/assistant-kb/**`, audience Developer, i.e. INTERNAL BACKGROUND, never quoted to customers, `docs/platform-docs-search.md`), or, for customer-facing recipes, in `docs/user-guide/recipes/` which already exists. Rule from `CLAUDE.md`: each `##` section stands alone (chunking splits at `##`, `ChunkerVersion` `"2"`, 2400-char cap, `PlatformDocsCorpus.cs:61`).

| Doc | Content | Audience | Size |
|---|---|---|---|
| `assistant-kb/recipes.md` | One `##` per recipe (23 templates): the goals it serves in the user's words, needs, what to put in the brief, patches that apply, cost class, 2 worked examples with a brief, typical mistakes | Developer | ~23 sections |
| `assistant-kb/step-cards.md` | One `##` per step type (22): purpose, needs, produces, what to say in the brief for it, common failures and fixes. Generated parts (needs/produces/cost) are **rendered from `StepCatalog`** by a build step, hand-written parts are the explanations | Developer (generated block + prose) | 22 sections |
| `assistant-kb/how-steps-connect.md` | How work passes between steps (context modes `FullWorkflow`/`PreviousStepOnly`/`SelectedPriorSteps`, `Extract` refs, `Previous` pitfalls, review loops, voiceover to compile wiring, the run brief and style/design blocks), with the 6 canonical graphs drawn as text | Developer | ~10 sections |
| `assistant-kb/writing-a-brief.md` | Fields, what each changes, examples in several languages, the precedence rule, what notes are for | Developer + user-guide twin | ~6 sections |
| `assistant-kb/goal-to-recipe.md` | Worked decisions for common goals (distilled from the 50-goal set's `why` lines), including honest declines and alternatives | Developer | ~15 sections |
| `assistant-kb/project-readiness.md` | What files/providers/voices each recipe needs and how to fix a missing one | Developer | ~8 sections |
| `user-guide/recipes/*.md` | Customer pages for the ten curated recipes (extends the existing `derush-edit.md` pattern) | User | 10 pages |

Authoring discipline: a doc line that states a number a tool can return (a price, a limit, a step count) is a defect; the docs say how to find it. A CI check greps the new folder for credit and dollar figures.

### 7.4 Retrieval: chunk metadata, hybrid search, rerank

**Today** (`docs/platform-docs-search.md`; `API/Services/PlatformDocs/`): chunks split at `##`; payload is `docPath, audience, title, section, url, content, embedHash` and nothing else (`PlatformDocsIndex.cs:240`); one cosine vector per point; **no payload indexes, no filters**; one `SearchAsync` call (`PlatformDocsIndex.cs:310`); injection takes the latest user message (<= 2000 chars), fetches `InjectTopK*2`, applies a 0.35 floor and a +0.05 User-audience boost, keeps 4 within 7000 chars (`PlatformDocsContextBuilder.cs:46`). The Qdrant client is 1.19.0 (`API/ReelBolt.Inference.Api.csproj:47`), which has `QueryAsync`/`QueryBatchAsync` (the Query API: prefetch, sparse vectors, fusion; verified in the package's XML docs). The server image is `qdrant/qdrant:latest` (`docker-compose.yml:366`), **unpinned**: pinning a tested tag is a prerequisite ticket, because hybrid needs a server that supports the Query API and the behaviour must not change under us.

**Chunk metadata schema** (payload, additive to today's fields; `ChunkerVersion` becomes `"3"`, which changes the corpus hash and rebuilds the collection by the existing mechanism):

| Field | Type | Source |
|---|---|---|
| `kind` | keyword: `recipe`, `step-card`, `concept`, `how-to`, `reference`, `user-guide`, `generated-fact` | front matter `kb.kind`, else folder rule |
| `stepTypes` | keyword[] | front matter, else exact `StepType` names found in the section (deterministic scan) |
| `agentTypes` | keyword[] | same, `AgentType` names |
| `capabilities` | keyword[] | `InferenceProviderCapability` names found or declared |
| `templateKeys` | keyword[] | recipes declare; scan for catalog keys |
| `goals` | text[] | recipes: the user-word goal phrases (what the BM25 side matches best) |
| `costClass` | keyword: `free`, `credits`, `voice`, `generation` | front matter or the step cards' generated block |
| `audience` | keyword (existing) | folder rule (existing) |
| `locale` | keyword | `en` for all docs v1 |
| `headingPath` | text | `# title > ## section` |
| `prevId` / `nextId` | id | adjacency inside one doc (for neighbour expansion) |
| `docVersion` | keyword | content hash of the file (diagnostics) |

Front matter block (`kb:` YAML, parsed by the existing front-matter reader which today reads `title:` only, `PlatformDocsCorpus.cs:128`) is optional; absent metadata falls back to the deterministic scan, so all existing docs get usable metadata without edits.

**Collection layout:** named vectors `dense` (cosine, as today, dimension from the resolved embedding model) and `bm25` (sparse, `Modifier.Idf` so the server applies IDF and we send term frequencies of a Unicode-folded, lowercased token stream, no stemming v1). Payload indexes (keyword) on `kind`, `stepTypes`, `agentTypes`, `capabilities`, `templateKeys`, `costClass`, `audience`. Collection naming and swap-after-complete stay exactly as today (`platformdocs_{model-slug}_{hash8}_{corpus12}`, `docs/platform-docs-search.md`, "Collection naming"), including embedHash reuse of unchanged chunks.

**Query pipeline** (replaces the single search call; same timeouts, same fail-soft null):
1. **Filter derivation:** explicit arguments of `SearchPlatformDocs`; or, for injection, from session context (the open workflow's step types and template key come from `AssistantSessionContext` `EntityIds`) and from enum names mentioned in the message. Filters are `should`-style boosts for injection (never hide results), `must` for explicit tool arguments.
2. **Prefetch** dense top 30 and sparse top 30 with the same filter, **RRF fusion** (k = 60) to the top 20 (`QueryAsync` with two `Prefetch` entries).
3. **Rerank, two stages (Q10: a paid model rerank is included from the start).** Stage A, deterministic and free, runs always: kind priority for the goal at hand (recipe > step-card > how-to > concept for a build question), exact step/template match with the open workflow, audience boost (existing +0.05), duplicate-doc suppression (one doc contributes at most 2 chunks), then the existing floor. Stage B, the model rerank, takes the top 20 stage-A candidates and the query and returns relevance scores; the top `InjectTopK` after stage B are injected. Any failure, timeout (default 3 s), missing provider or unpriced model **falls back to stage A's order**; retrieval never fails because the reranker did. Design in 7.6.
4. **Neighbour expansion:** if budget remains, add `prevId`/`nextId` chunks of the top hit so a split section is whole.
5. **Query language:** the model writes `SearchPlatformDocs` queries in English (tool description says so), because the corpus is English and the sparse side is lexical; the dense side handles cross-lingual user messages for injection. Not solved in v1: a lexical match for non-English injection (the dense half carries it); measured in the eval's multilingual slice.
6. **Speed:** the query embedding is computed once per turn and cached (LRU 256 keyed on the text and model); request only the payload fields used (`content` excluded from the prefetch stage); payload indexes make filtered prefetch cheap. Target: injection p95 under the existing 5 s timeout with a hit rate not lower than baseline; no latency number is promised, the eval records it, we measure first and tune after.

**Facts into the index:** `generated-fact` chunks are produced at index time from `StepCatalog` and `WorkflowTemplateCatalog` (one chunk per step type and per template), so retrieval covers facts that tools also return, from the same source; they are rebuilt whenever the code changes because they enter the corpus hash.

### 7.5 Retrieval eval (offline, before any retrieval change)

There is none today: all PlatformDocs tests use fakes and pin chunk shape, never relevance (`PlatformDocsCorpusTests.cs`, `AssistantKnowledgeToolTests.cs`).

- **Set:** ~150 queries in `docs/design/workflow-apps/retrieval-eval.json` (produced in phase 3b, not in this spec): the question each of the 50 goals implies, one per step type, one per recipe, paraphrases, and an Italian/Spanish/German/French slice. Each has gold chunks as `docPath#section` (stable under re-chunking; ids are not).
- **Metrics:** recall@5, MRR@10, nDCG@10, injection hit rate at the 0.35 floor, latency p50/p95, and per-slice numbers (English, non-English, step-question, goal-question).
- **Baseline first:** run the current vector-only retrieval and record it before changing anything (phase 3b exit criterion 1).
- **Harness:** a test project running against the local `embeddings` service (`Qwen/Qwen3-Embedding-0.6B`, free, `docker-compose.yml:106`) and a Qdrant container, nightly and on demand, never per-PR and never against a paid provider.
- **Gates for shipping hybrid:** recall@5 up by at least 10 points over baseline on the whole set, non-English slice not worse, p95 not worse than 2x baseline. The rerank stage is reported separately (stage A alone vs A plus B on recall@5, MRR@10, nDCG@10, latency, cost per turn). It ships enabled because it was decided so (Q10), with one hard guard: **stage B must not score below stage A on nDCG@10** on the whole set or on the non-English slice; if it does, the model choice or the candidate count is changed until it does not. The measured gain and cost per turn are recorded in the lane PR.

### 7.6 The `Rerank` capability (priced, metered, tested)

Q10 asked for a paid rerank from the start. It follows the pattern of the newest capability, `ContentModeration`, so no new mechanism is invented.

- **Capability and provider.** `InferenceProviderCapability.Rerank` (append-only enum, persisted by name). Served by `OpenAICompatible` rows only, with the de-facto rerank route `POST {base}/rerank` (`{query, texts[], top_n}` to scored indexes), which Hugging Face text-embeddings-inference (the image behind our `embeddings` service, `docker-compose.yml:106`) and hosted rerankers expose. Two supported deployments: a **self-hosted** reranker container beside `embeddings` (free per call, no data leaves the host; the operator adds the service and registers the row, as for embeddings), or a **hosted** reranker (priced per search). Resolution `ResolveRerankAsync`: explicit, then the default `Rerank` capability, else none (stage A only). `InferenceProvidersController.IsUnsupportedCombination` gets an explicit allowlist entry (never a catch-all), and the admin form offers the capability.
- **Pricing.** One new `UsageKind.RerankSearches` (one query plus up to 20 candidate texts is one unit; append-only), mapped in `ProviderCapabilityUsage` (`SH/Metering/ProviderCapabilityUsage.cs:126`, where `ContentModeration` maps to `ModerationCalls`) and priced like every other capability through `ModelCapabilityPrice` (platform rows auto-priced from the price book, `ModelPriceCatalog`, `SH/Metering/ModelPriceCatalog.cs:159` shows the `$0` self-hosted precedent). **Priced or refused:** an unpriced platform rerank model is treated like the embedding case (`ModelUnpricedException` becomes "rerank unavailable", stage A runs); `GET /api/v1/pricing/coverage` lists a Rerank capability with no price.
- **Metering and charging.** Each call writes a `usage_events` row (`UsageSource.Assistant` for the chat injection and tool path, so cost is attributable per org and visible in the admin views). Charging follows the "non-workflow calls are charged by source" policy (`NonWorkflowChargeService`, `docs/metering-and-billing.md`, "Charging policy work outside workflows"): the call is **inside the assistant turn's hold and allowance** (`Billing:NonWorkflow:AssistantTurnHoldCredits`), so the user sees no extra line; with billing off nothing is charged. This is a new decision for the maintainers (Q18). Hard bounds per turn: one rerank call, at most 20 candidates of at most 600 characters each, 3 s timeout.
- **Data handling.** The query (the user's latest message, truncated) and corpus chunk text leave the host only with a hosted reranker, the same exposure class as a hosted embedding provider (`docs/platform-docs-search.md`, data section); `docs/provider-data-handling.md` gets the row. The corpus includes INTERNAL BACKGROUND developer docs, which are company-owned and never quoted to customers; sending them to a vendor is a Security-review item for lane 3b (opus), with the self-hosted deployment as the zero-egress option and the default recommendation for any deployment that cares.
- **Tests (all offline, no paid call).** A fake `IRerankClient`: reorders as scripted; times out; returns malformed scores; returns fewer scores than candidates (fall back, never drop); unpriced model (stage A, `coverage` lists it); metering row written once per call with the right unit and source; charge lands in the turn hold; billing off writes the usage row and no charge; disabled provider and no provider (stage A order identical to the no-rerank path, byte for byte); the 20-candidate and 600-character bounds; the privacy truncation. Plus the retrieval eval's stage A vs A+B comparison (7.5) against a local TEI reranker, nightly.
- **Docs.** `docs/platform-docs-search.md` (pipeline and config keys `PlatformDocs:Rerank:Enabled|Candidates|TimeoutSeconds`), `docs/metering-and-billing.md` (usage kind), `docs/provider-data-handling.md`, `docs/builtin-agents.md` is untouched. `CLAUDE.md` gets the capability in the enum list and the endpoint table once, at the end of step 3.

## 8. The 50-goal eval set

Fixture: [`workflow-apps/eval-goals.json`](workflow-apps/eval-goals.json); validator [`check-eval-goals.mjs`](workflow-apps/check-eval-goals.mjs) (JSON shape, unique ids, every `templateKey` exists in `WorkflowTemplateCatalog.cs`, vocabulary of routes, briefs and lint ids); scoring in [`eval-goals.md`](workflow-apps/eval-goals.md).

Composition (as written, validated by the script): 50 goals, 18 in a language other than English (Italian, Spanish, German, French, Portuguese), routes: `template` 34, `template_with_patch` 6, `clarify_first` 5, `decline_with_alternative` 4, `custom_graph` 1; 19 carry linter expectations; every template goal lists `alternates` (other keys that are also correct). G01 is the trigger case (Italian, Blazor, approved design, 60 s 16:9 narrated explainer: `quick-win-promo-ds` plus the `addVoiceover` patch, brief `language it`, `designSystem project`, `voice narrated`). Four goals have code **and** clips in one project. G40 (subtitles only) is a `template` goal on the shipped `video-subtitles` recipe (Q16; alternate `video-subtitles-translated`); `plannedTemplates` in the fixture is now empty (the mechanism stays for future planned recipes). The set was revised after a product review (realism, coverage of testimonials, release notes, localisation, slideshows, ad variants, aspect-ratio changes, honest declines checked against the user guide). Each goal records the project state it assumes (code kind, clips, images, music, SFX, consented voice, design status, providers, plan), the expected route, template and brief, the questions that must be asked, the lint rules a naive route would trip, whether the run is paid, and one `why` sentence that cites the template or the missing capability.

What the set already says about the catalog (useful before any code): the template catalog is concentrated on two families (promo from code, footage edits), ten of the 50 goals have no template key (decline, custom, clarify), and several common asks (narration on a promo, captions on existing footage) are not one setting but a patch or a decline. That is the honest case for the closed patch catalog and for a decline path.

**Method** (details in `eval-goals.md`): a harness drives the assistant with the goal and a stub project built from `project` (no paid provider calls, providers stubbed by capability), records the proposal tool calls (not rendered video), and scores: route accuracy; template accuracy on template routes; brief field accuracy (language and format exact, length within 10 %); lint recall; clarification precision (asks only when expected, at most 2 questions); paid disclosure. Three samples per goal, majority vote, on the production assistant model. **Targets** for the assistant initiative's exit: route >= 85 %, template >= 80 % of template routes, language and format >= 95 %, lint recall >= 90 %, no clarifying question on a non-clarify goal in more than 5 % of goals. Today's assistant is the baseline, measured in phase 3a before it is changed.

Limits stated plainly: 50 goals measure regressions and broad correctness, not every phrasing, and per-metric intervals are wide (about plus or minus 12 points on the template metric), so the targets are a regression floor, not a launch statistic (`eval-goals.md`, "How to read the numbers"). Goals that rest on an assumption need a one-line confirmation from code before they gate anything: G26 (voice language support), G29 (b-roll on a trial plan), G32 (a photo slideshow through `video-derush-edit-music`, assumed from the still-image docs), G13 (the Scriptwriter following the brief language, which is exactly what step 2 delivers) and G44 (its alternative is the video editor, which is off in 0.1.0-alpha, so the honest answer today names a re-run or declines).

## 9. Phased execution plan (steps 2 and 3)

Sizes are engineer-days for one backend or frontend executor on the pinned models, **estimates to be re-based after phase 2** (S = 1-2, M = 3-5, L = 6-10). Department mix: backend and frontend lead; design reviews wireframes and the restyle; docs writes the knowledge set; QA and Security (opus) review once per PR on the PR head, with Security looking hard at the assistant tool surface, at the `Rerank` outbound data flow (7.6), and the app manifest validator (user-supplied labels rendered into the UI) and at anything that touches `run_brief_json` (prompt-injection reach: brief text goes only through the registry's fixed sentences; notes stay quoted as today).

### 9.1 Step 2: brief and linter backend (one stream, API and engine)

| Ticket | Content | Size | Depends on | Main files |
|---|---|---|---|---|
| P2-1 | `RunBrief` model, `RunBriefValidator`, `brief` member on `ExecuteWorkflowRequest`, split into existing columns plus `run_brief_json` (engine migration `AddExecutionRunBrief`), response DTO, every copy site (3.3) plus drift test, `RunStyleResolver` tone fold-in | M | none | `API/Controllers/Dto/ProjectDtos.cs`, `API/Controllers/WorkflowsController.cs`, `API/Services/Workflows/WorkflowExecutionService.cs`, `API/Services/Styles/RunStyleResolver.cs`, `SH/Data/Models/WorkflowExecution.cs`, `ENG/Migrations/*`, `API/Services/Account/AccountExportService.cs`, `ENG/Services/ExecutionRecoverySweeper.cs`, `SH/IntegrationEvents/WorkflowEvents.cs` |
| P2-2 | Engine consumption: `RunBriefAmbient`/`RunBriefState`/`RunBriefRegistry` and role sentences, `RunBriefOverrides` (Voiceover language and voice, VideoCompile length, VideoAnalyze `sourceLanguage`), `VideoSourceKind.BriefFiles`, cache-key component, `DeterministicStepRevisions` bumps, `SpeechLanguageSupport` fact table | L | P2-1 | `ENG/Agents/ReelBoltAgentBase.cs` (decorate, near `:406`), `ENG/Execution/WorkflowExecutorService.cs` (near `:497`), `ENG/Execution/StepExecutors/{Voiceover,VideoCompile,VideoAnalyze}StepExecutor.cs`, `ENG/Execution/Caching/StepCacheKeyBuilder.cs`, `SH/Workflows/VideoSource.cs`, new `ENG/Services/RunBrief/*` |
| P2-3 | `StepCompatibilityRules` (`SH`), `WorkflowLinter` + rules `WL001`-`WL020`, `POST .../lint`, `RunStartGate` enforcement behind `Workflows:Lint:Enforce`, golden test per rule, catalog test (all 23 templates lint with zero errors), read-only scan of staging workflows for false positives | L | P2-1 (brief rules) | `API/Services/Workflows/WorkflowLinter.cs` (new), `API/Services/Workflows/RunStartGate.cs`, `SH/Workflows/Replay/WorkflowStepDependencyGraph.cs` (read), `API/Controllers/WorkflowsController.cs` |
| P2-4 | `app_manifest_json` + `created_via` + assistant thread link (engine migration `AddWorkflowAppManifest`), `AppManifestDeriver`, `AppManifestValidator`, `WorkflowTemplateDefinition.AppManifest`, `GET workflow-apps` and `GET .../app`, `WL018` | M | P2-1 | `SH/Data/Models/WorkflowDefinition.cs`, `SH/Workflows/WorkflowTemplateCatalog.cs`, `API/Services/Workflows/WorkflowTemplateProvisioningService.cs`, `API/Controllers/Dto/ProjectDtos.cs`, new `SH/Workflows/AppManifest*.cs` |

Exit criteria for step 2: contract frozen (OpenAPI note in the PR) so frontend and assistant lanes can start; a run with no brief is byte-identical (golden prompt and cache-key tests); the trigger scenario runs end to end with stubbed providers: an Italian brief produces Italian scripts and a Voiceover that receives `it` and the chosen voice; lint enforcement still off, with a false-positive report; all migrations are engine-owned and `ApiMigrationOwnershipGuardTests` is green. P2-1 and P2-4 can overlap after P2-1's DTO lands; P2-2 and P2-3 are independent of each other.

### 9.2 Step 3: four lanes after the step 2 contract is frozen

| Lane | Content | Size | Depends on | Main files |
|---|---|---|---|---|
| 3a Assistant | Baseline the current assistant on the 50 goals first; `GetProjectReadiness`, `EstimateWorkflowRun`, `ProposeCreateFromTemplate`/`Apply...`, closed patch catalog, changed `ListWorkflowTemplates`/`GetWorkflowTemplate`/`ValidateWorkflow`/`ExecuteWorkflow`, prompt section (replaces the interim "goal specifics" sentence), run and create cards, eval harness and score | L | P2-1, P2-3, P2-4 | `API/Agents/AssistantAgent.cs`, `API/Agents/Tools/{AssistantToolProvider,AssistantKnowledgeTools}.cs`, new `AssistantRecipeTools.cs`, `API/Services/Assistant/AssistantApprovalCardBuilder.cs`, `API/Agents/Tools/AssistantToolProgress.cs`, `API/Data/DatabaseSeeder.cs`, `inference/tests/.../Agents/*`, new eval harness project |
| 3b Knowledge | Pin the Qdrant tag; `StepCatalog` + `[StepCard]` + drift guard; `GetStepCatalog`; docs set (7.3) incl. the generated step-card block; metadata and `ChunkerVersion` 3; hybrid retrieval and stage-A rerank; **the `Rerank` capability end to end (7.6: enum, `IRerankClient`, resolver, usage kind and prices, coverage, admin allowlist, metering and charge, config, tests, docs)**; `SearchPlatformDocs` filters; retrieval eval set, baseline, gates, with the stage A vs A+B report | L (about 4 of its days are the rerank capability) | P2-3 (shared rule table); otherwise independent | `SH/Workflows/StepCatalog*.cs` (new), `API/Services/PlatformDocs/*`, `API/Services/VectorSearch/*`, `API/Agents/Tools/AssistantKnowledgeTools.cs`, `docs/assistant-kb/**` (new), `docs/user-guide/recipes/**`, `docker-compose.yml` (qdrant tag; an optional reranker container beside `embeddings`), `SH/Metering/{ProviderCapabilityUsage,ModelPriceCatalog}*.cs`, `SH/Data/Models/Enums.cs` (capability and usage kind, append-only), `API/Controllers/InferenceProvidersController.cs`, new `SH/Inference/Rerank*`, `docs/platform-docs-search.md`, `docs/provider-data-handling.md` |
| 3c App view UI | Gallery, app run page and form from the manifest, readiness strip, file slots, price line, result area, phone layout, curated manifests for the top recipes, retire `ExecuteWithInputModal` for apps | L | P2-1, P2-3, P2-4 | `WEB/app/(app)/projects/[id]/page.tsx`, `WEB/components/projects/ProjectSections*`, `WEB/app/(app)/projects/[id]/workflows/[workflowId]/page.tsx`, new `WEB/components/workflows/apps/**`, `WEB/lib/api/workflows.ts`, `WEB/lib/types/workflow.ts`, `WEB/components/workflows/ExecuteWithInputModal.tsx` |
| 3e Subtitles recipe (Q16) | `VideoCompileStepConfig.CaptionSource` (append-only: `Voiceover` default, `Transcript`), burning cues cut from the `VideoAnalyze` transcript instead of Voiceover text (captions burned only from Voiceover text and needing `EnableVoiceover` before this lane), the `video-subtitles` template (`VideoAnalyze` with transcription `Required` and `KeepWholeSources`, then `VideoCompile` Reencode with captions from the transcript; no agent), save validation and `WL019` extended, caption language from `brief.sourceLanguage`/`language`, golden argv test (default unchanged byte for byte), `DeterministicStepRevisions` bump, curated manifest, user-guide recipe page, flip the fixture's G40 from planned to available. **Built, per Q19:** translation (a second template `video-subtitles-translated`: an `Extract` projection of the transcript segments plus `NarrationTranslator`, keyed by segment id so timing stays the transcript's) and SRT/VTT sidecar files with a download endpoint are in v1; the run-page download button is a follow-up web ticket | M | P2-2 (brief), P2-3 (`WL019`) | `SH/Workflows/VideoCompileStepConfig.cs`, `ENG/Execution/StepExecutors/VideoCompileStepExecutor.cs`, `ENG/Services/Video/*Caption*`, `SH/Workflows/WorkflowTemplateCatalog.cs`, `API/Services/Workflows/StepConfigSaveValidator.cs`, `docs/voiceover.md`, `docs/video-editing.md` |
| 3d Graph restyle | "App | Advanced" toggle, app-inputs panel, graph, nodes, drawer and list restyled with `--rb-ed-*` tokens (read-only), `step` input kind if Q8 is approved | M | 3c (toggle lives on its page) | `WEB/components/workflows/{FlowchartBuilder,StepConfigSheet,StepCard}*`, `WEB/components/workflows/nodes/**`, `workflows/**/page.module.css` |

Parallelism: 3a, 3b, 3c and 3e run together (disjoint trees except the shared `AssistantKnowledgeTools.cs`, which 3a and 3b both change; assign `ListWorkflowTemplates` and `GetWorkflowTemplate` to 3a and `SearchPlatformDocs` to 3b, and merge 3b's tool change first). 3d follows 3c. Web lanes 3c and 3d start only when editor wave 1 is on master (Q15: wait). Lane 3e is engine work under `Services/Video/` and touches no editor file, but it shares `VideoCompileStepExecutor.cs` with P2-2 (language/length override): land P2-2 first. Docs lane work inside 3b is independent of code. Each lane's PR carries its own tests; the QA verdict covers the lane's eval or Playwright result; `CLAUDE.md` and `docs/platform-assistant-mcp.md` count updates happen in one final docs commit (7.2).

### 9.3 Suggested order and rough calendar

Step 2 first (about two weeks of one backend stream, P2-1 and P2-4 early so lanes can start), then 3a, 3b and 3e in parallel (about two to three weeks), 3c once editor wave 1 is on master, then 3d (about one week). The only hard serial dependency is the step 2 contract freeze. Feature flags: `Workflows:Lint:Enforce` (off), `Workflows:Apps:Enabled` (web gallery and run page; off falls back to today's page), `Assistant:RecipeFirst` (off falls back to today's prompt section); the knowledge lane ships behind the eval gates, not a flag.

### 9.4 Collisions and coordination

| Running work | What it touches that we touch | Rule |
|---|---|---|
| `scriptwriter-output-shape` | `AgentStepExecutor`/`ReelBoltAgentBase.cs:127-143`; Scriptwriter prompt in `DatabaseSeeder.cs` and `DefaultPrompt`; `VoiceoverStepExecutor` (non-retry); analyzer prompts; the assistant prompt "goal specifics" sentence; `AssistantPromptConsistencyTests`; possibly `StepCacheKeyInputs.SchemaVersion` v10 | P2-2 starts after their PR merges (it edits the same agent base and Voiceover executor); 3a's prompt section replaces their sentence in one commit and rebases on theirs; **one** `SchemaVersion` bump, theirs if it lands first; we assert "no bump needed" in P2-2's tests |
| `editor-v2-wave1` ... `wave4` (stacked PRs) | `WEB/components/editor/**`, `WEB/lib/editing/**`, `WEB/app/global.css` tokens, `docs/editor.md`, `CLAUDE.md` editor bullets, `e2e/tests/editor-v2/**`, `e2e/scripts/editor-v2-stack.sh`, Outputs/Workspaces hub pieces of the project page, editor-owned migrations, Wave 4 guest-review notifications | We never edit editor files and add no token to the editor's set; **3c/3d start only after wave 1 is on master (Q15)** and then branch from it before touching `projects/[id]/page.tsx` or `ProjectSections`; engine migrations (`AddExecutionRunBrief`, `AddWorkflowAppManifest`) are serialized with any editor engine migration because the EF model snapshot conflicts (see `reference-dotnet-ef-and-test-quirks` memory: hand-apply the snapshot hunk); `CLAUDE.md` is edited once by us, at the end; our e2e specs use their own project and never the editor stack |
| `docs/design/workflow-builder-ux/*` (shipped) | The builder a11y contract | Preserved (4.5); not re-opened |

## 10. Risks

| Risk | Mitigation |
|---|---|
| The brief becomes a dumping ground (more fields than anyone reads) | Ten fields, each tied to a consumer in the matrix; the registry is the only way a field becomes model-visible; a field with no consumer is a lint info, not a control |
| `[RUN BRIEF]` sentences misfire on weaker models (DeepSeek ignores structure, as the trigger run showed for schemas) | Language is also enforced deterministically where possible (Voiceover input, reviewer check), and the sibling's output validation catches shape failures; the eval records per-model results |
| Lint false positives block valid runs | Enforcement flag off first; warn-only telemetry; errors only for proven run-time failures; every template and a staging scan must lint clean before enabling |
| Closed patches feel limiting | Patch catalog is data; the custom-graph route stays for what recipes cannot do; the eval's `custom_graph` and `decline` goals keep it honest |
| Derived manifests look generic | Curated manifests for the top recipes; derived only as a fallback |
| Hybrid retrieval operational weight (sparse vectors, unpinned Qdrant, rebuild time) | Pin first; rebuild already swap-after-complete; eval gates; dense-only fallback path kept behind the same interface |
| Rerank adds per-turn cost and an outbound data flow | Bounded (one call, 20 candidates, 600 chars, 3 s), priced or refused, metered per org, charged inside the turn hold, fail-soft to the free stage; self-hosted option sends nothing out; Security review in lane 3b |
| Assistant cost grows (readiness plus catalog plus estimate calls per goal) | Readiness and catalog are cheap reads (no model); prompt says call once per goal; the eval records tool-call counts and tokens per goal |
| Spec drift from code | Every claim cites a symbol; the first ticket of each lane re-verifies its cites and amends this document in the same PR |

## 11. Decisions and open questions

The questions below were answered on 2026-10-24. **Decided** rows are binding and the spec above already reflects them. **Open** rows were not put to the maintainers; the recommendation is assumed unless they object, and they are engineering-level calls the owning lane may settle. Building starts only after this PR is merged.

| # | Question | Status | Answer (decided) or recommendation (open) |
|---|---|---|---|
| Q1 | Should every existing workflow appear as an app (derived manifest), or only marked ones? | **Decided 2026-10-24: yes, all** | Every workflow, derived manifest, Advanced one click away (4.1). |
| Q2 | Block runs on lint errors, or warn only? | **Decided 2026-10-24: warn first** | Warn first; later block only errors that would fail the run anyway (6.3, `Workflows:Lint:Enforce` off until the false-positive review). |
| Q3 | Should the assistant reuse an existing workflow from the same recipe instead of creating one per goal? | **Decided 2026-10-24: yes** | Reuse by default; create only when none exists or a separate one is requested (6.1 step 4.1). |
| Q4 | Closed patch catalog vs free editing of recipes | Open | Closed catalog of 7 patches for v1; `ProposePatchWorkflow` stays for anything else, subject to lint. |
| Q5 | Per-run design system choice (another version or an org library): v1 or later? | **Decided 2026-10-24: later** | v1 ships `project` and `none` only (3.6). |
| Q6 | Design system carries a default language (`voice.locale`)? | **Decided 2026-10-24: yes; the brief still wins** | Added as the default for `brief.language` (3.6, 3.12). |
| Q7 | Language set: any BCP-47 tag, or a curated list tied to voice support? | Open | Any tag accepted for text; a curated supported-voice list only warns (`WL009`). |
| Q8 | Stable step keys (`workflow_steps.step_key`) and the "expose a step setting" input kind | Open | Yes, as a prerequisite to `step` inputs; defer both until after the first app release. |
| Q9 | Can an author pin a step setting so a run field cannot override it? | Open | Not in v1; run fields override bound step config, as styles do today. |
| Q10 | Paid rerank in the retrieval pipeline | **Decided 2026-10-24: yes, include it from the start** (not "decide after data") | New `Rerank` capability, priced, metered, charged inside the assistant turn, fail-soft to a free deterministic stage, tested (7.4 step 3, 7.6, lane 3b). The retrieval eval compares stage A and A plus B and guards against regression; it no longer decides whether the rerank exists. |
| Q11 | Customer-facing docs in non-English: translate recipe pages or rely on dense retrieval? | Open | Rely on dense for v1; translate the top recipe pages only if the non-English slice fails its gate. |
| Q12 | Numeric price on gallery cards before a recipe exists | Open | Cost class on cards, number on the run page. |
| Q13 | Store the assistant thread link on workflows (`created_via`, thread id)? | Open | Store it; org-scoped like the thread and removed with it. |
| Q14 | Eval assumptions to confirm before they gate (G26, G29, G32) | Open | Confirm from code in lane 3a week one; adjust the fixture, not the product. |
| Q15 | Do the web lanes (3c, 3d) wait for editor wave 1 on master? | **Decided 2026-10-24: yes, wait** | 3c/3d start after wave 1 is on master (9.2, 9.4). 3a, 3b, 3e do not wait. |
| Q16 | Roadmap: subtitles-only and avatars | **Decided 2026-10-24: subtitles-only YES; avatars NO** | Subtitles-only is a supported goal: lane 3e builds the `video-subtitles` recipe (transcript caption source). Avatars stay a decline. G40 moved from decline to a planned template (8). |
| Q17 | Who owns the eval, and what is the ship gate? | **Decided 2026-10-24: the assistant lane owns it; zero-tolerance rows are a hard release gate** | Targets stay a regression floor reported with intervals; the zero-tolerance rows (hallucinated template, unrequested paid step, id shown to the user) must be 0 for release (`eval-goals.md`). |
| Q18 | New, from Q10: who bears the rerank cost? | Open | Absorbed inside the existing assistant-turn hold and allowance (no new line for the user, nothing charged with billing off); revisit with the first cost-per-turn figures from the eval. |
| Q19 | New, from Q16: scope of subtitles-only v1 | **Decided: in v1** | Burned subtitles from the clip's own transcript, translated subtitles into the brief language, and SRT/VTT files are all in v1 (as built in lane 3e). Editable caption files and non-burned tracks are follow-ups, and so is the run-page download button. |
