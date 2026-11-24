# SaaS metering: usage taxonomy and failure classification (WP B1)

Status: spec, written 2026-10-05, no code. It fixes the two enums that B2 will add to `Enums.cs` (`UsageKind`, `FailureClass`), the rules that classify a failed step, and how compute is priced. B2, B3a-d and B6a-c implement it. Grep evidence for every mapping is given as `file:line`. Line numbers were taken on branch `saas-launch` at the time of writing and will drift; the quoted code strings are the stable handle. Paths are relative to `inference/src/`. The credit unit, margin and refund policy this spec feeds are in `saas-pricing-and-billing.md`.

## Summary

- `UsageKind` has eleven append-only members, one per billable physical unit. LLM tokens are split three ways (input, output, cached input) because providers price them differently.
- `FailureClass` has eight append-only members. Two are platform-caused and trigger an automatic refund (`PlatformProviderError`, `PlatformInternal`).
- Today **no failure carries a class**. Deterministic steps return a string code inside their output JSON envelope; agent steps surface exception messages; the step retry loop then wraps a failed result in a bare `InvalidOperationException` and discards the code (`WorkflowExecutorService.cs:775`). B6b must therefore classify **where the code is produced** and carry the class on `StepExecutionResult`, not infer it from message text.
- Compute is priced from the platform-side job spec (output seconds times a resolution tier), never from wall-clock or runner-reported time. `DurationMs` stays for COGS analysis only.

## Usage kinds

`UsageKind` is an append-only enum persisted by name (the house rule: `JsonStringEnumConverter` and EF string conversion store member names, so a rename breaks stored rows, an append does not). `usage_events.quantity` is a `decimal` in the unit shown.

| Member | Unit | Produced by | Evidence (where the unit is available today) |
|---|---|---|---|
| `LlmInputTokens` | tokens | every chat call: workflow agents, ReviewLoop, rooms, assistant, file summarizer, decision/guardrail calls on logprob clients, vision captioning | `StepExecutionResult.InputTokens` (`ReelBolt.WorkflowEngine/Execution/StepExecutionContext.cs`, class `StepExecutionResult`, `public int? InputTokens`) is already filled by `ReelBoltAgentBase` but **never persisted**; only `TokensUsed` reaches `workflow_step_results.tokens_used`. B3a adds the split columns. |
| `LlmOutputTokens` | tokens | same calls | `StepExecutionResult.OutputTokens`, same status. |
| `LlmCachedInputTokens` | tokens | the portion of input served from the provider's prompt cache | **Not captured anywhere today.** B3b reads it from `UsageDetails` where the provider reports it. Billed at the (lower) cached-input rate, hence its own kind. Not to be confused with the engine's step-result cache (`FromCache`), which costs zero and is a separate concept. |
| `EmbeddingTokens` | tokens | file indexing, semantic file search queries, platform-docs indexing and per-turn doc retrieval | `Inference.Api/Services/VectorSearch/ProviderEmbeddingGenerator.cs:19` (single choke point, resolves per call); `Inference.Api/Services/PlatformDocs/PlatformDocsIndex.cs:417` (`PlatformDocsIndexingService`) |
| `AsrAudioSeconds` | seconds of audio sent | `VideoAnalyze` transcription, voiceover alignment | `Shared/Inference/ITranscriptionClient.cs:12`; callers `VideoAnalyzeStepExecutor.cs:1379` (`TRANSCRIPTION_FAILED`), `Services/Voiceover/VoiceoverAlignmentService.cs` |
| `TtsInputBytes` | UTF-8 bytes of text sent | `Voiceover` step through `ISpeechSynthesisClient` | `Shared/Inference/ISpeechSynthesisClient.cs:8`, `FishAudioSpeechSynthesisClient.cs:24` (Fish Audio bills per UTF-8 byte), `OpenAICompatibleSpeechSynthesisClient.cs:21`; step guard `VoiceoverStepExecutor.cs:510` (`BYTE_BUDGET_EXCEEDED`) already counts bytes. |
| `VideoGenSeconds` | seconds of generated video | `VideoGenerate` step (MiniMax) | `external_generation_jobs` ledger (`cost_estimate`, `actual_cost`); `Shared/Inference/VideoGenerationPricing.cs`; `VideoGenerateStepExecutor.cs`. B6c moves this ledger onto credits. |
| `RenderOutputSeconds` | seconds of rendered Remotion output | the sandbox Remotion render (`RenderVideoAndUploadToStorage`) | `WorkflowEngine/Agents/Tools/AgentToolProvider.cs`, `Execution/StepExecutionContext.cs`, `VideoCompileStepExecutor.cs` (callers of the tool name). The unit comes from the **job spec** (composition duration), see "Pricing compute". |
| `EncodeOutputSeconds` | seconds of ffmpeg-encoded output | `VideoCompile` | `VideoCompileStepExecutor.cs:1480` (`ENCODE_FAILED` path), output duration is in the EDL the step already computes. |
| `AnalyzeMediaSeconds` | seconds of source media analysed | `VideoAnalyze` extraction phase (probe, silence/shot detection, frame grid, audio, keyframes, chroma tracking) | `VideoAnalyzeStepExecutor.cs:1251` (`DURATION_EXCEEDED` check already reads the probed duration). |
| `StorageGbDay` | gigabyte-days stored | daily sampler (B3d) | no existing source; computed from `project_files` and render output sizes. Metered for COGS visibility; plan quota, no overage charge in v1. |

Rules:

1. **Append-only.** New kinds go at the end; none are renamed or removed.
2. `usage_events.source` (`Workflow`, `Assistant`, `Indexing`, `DocsIndex`, `Storage`) and `billing_mode` (`Platform`, `Byo`, `Runner`) are separate columns, not part of the kind. The same `LlmInputTokens` event is `Platform` or `Byo` depending on the resolved provider's owner (decision D3). BYO LLM kinds are recorded with `credits_millis = 0` but still carry `cost_usd = 0`/unknown and the quantity, so usage dashboards stay complete.
3. Vision captioning, decision (`Decision`), guardrail and object-locator calls are ordinary chat/vision calls and use the LLM kinds; no separate kind. Decision-model calls through the TypeSafe/Open-Jev `POST /v1/systemone` contract return no token counts (`Shared/Inference/TypeSafeDecisionClient.cs:12`), so B3c records them as `LlmInputTokens` only if the response reports usage, otherwise they are unmetered and listed as a known gap.
4. Quantity is what the provider or job spec says, not an estimate. If a provider reports no usage, the event is recorded with the platform's own count (for example UTF-8 byte length of the request text) and flagged in `rate_card_id`'s notes, never silently zero.

## Failure classes

`FailureClass` is an append-only enum persisted by name. It answers one question: **who should bear the cost of the work this step consumed before it failed?**

| Member | Meaning | Credit treatment |
|---|---|---|
| `None` | The step completed. | Charge actual usage. |
| `UserInput` | The user's configuration, files or prompt made the step impossible (bad config, missing source, invalid plan reference, an agent that cannot do what was asked with the given input). | Release unused reservation. Keep charge for work actually consumed. |
| `UserCancelled` | The user pressed Stop. | Release unused reservation. Keep charge for work actually consumed. |
| `ByoProviderError` | The user's own (bring-your-own) provider failed: bad key, quota, outage, rejected request. | LLM kinds were free anyway. Compute credits for the part that ran are kept. |
| `PlatformProviderError` | A **platform-managed** provider failed (ReelBolt Inference, platform video generation, platform TTS/ASR). | **Auto-refund** the step's consumed credits. |
| `PlatformInternal` | Our own fault: engine exception, ffmpeg/encode failure on valid input, storage failure, restart, timeout of our own agent run, unparseable model output after retries. | **Auto-refund.** |
| `InsufficientCredits` | The credit gate refused to reserve (B6a). Nothing ran. | No charge. |
| `EntitlementDenied` | The plan does not include the feature (BYO on Trial, video generation off Pro, rooms off Pro). Nothing ran. | No charge. |

The last two are produced by the gate and entitlement service before a step starts, so no existing failure code maps to them.

### Where a failure surfaces today (the classification point)

Four distinct shapes exist, and a class must be assigned at each:

1. **Deterministic executors return a coded failure result.** `Failure(context, sw, "<CODE>", message, ...)` writes `{status:"failed", error:{code,message}}` into the step output and sets `Status = StepStatus.Failed`. Examples: `ExtractStepExecutor.cs:57`, `VideoCompileStepExecutor.cs:253`, `VideoAnalyzeStepExecutor.cs:254`, `VoiceoverStepExecutor.cs:276`, `RoomStepExecutorBase.cs:500`. Codes are strings, not an enum.
2. **Video generation returns coded take outcomes.** `TakeOutcome.Failed("<CODE>", ...)` (`VideoGenerateStepExecutor.cs:1206-1485`) plus step-level `CreateFailedResult(... "<CODE>" ...)`.
3. **Agent steps return a failed result with prose** (`AgentStepExecutor.cs:192-209`, no code) or **throw** (`TimeoutException`, `AgentWorkflowException`, SDK/HTTP exceptions, `InvalidOperationException`).
4. **The retry wrapper converts everything into `InvalidOperationException`.** `WorkflowExecutorService.cs:775`: "failed after {maxRetries} attempts. Last error: {result.ErrorDetails}". The execution then fails with that message (`WorkflowExecutorService.cs:586`). Original code and exception type are lost at this boundary.

**Rule for B6b:** add `FailureClass FailureClass` (default `None`) and `string? FailureCode` to `StepExecutionResult` and to `WorkflowStepResult` (B3a/B6b migration), assign them at the point of production (shapes 1-3), and make the retry wrapper and `BuildFailureStepResult` (`WorkflowExecutorService.cs:866`) **propagate** them rather than re-deriving. Message-substring classification is only a fallback for exceptions with no class (class `PlatformInternal`, so an unknown failure refunds, never silently bills).

Default for an unmapped code or unknown exception: **`PlatformInternal`**. Refunding by default is deliberate: charging a user for a failure we cannot explain is the worse error. A `FailureClass` guard test (B6b) fails the build when a new code constant has no mapping.

### Mapping: coded failures from deterministic executors

Config and shape errors are the user's workflow definition; they are `UserInput` even though the save-time validators (`StepConfigSaveValidator`) should have caught them. A config that fails at run time after passing save validation is still `UserInput` (the template is user data), but is logged as a validator gap.

Grep used (reproducible): `grep -rnE '"(CONFIG_INVALID|INPUT_MISSING|...)"' --include=*.cs ReelBolt.WorkflowEngine ReelBolt.Shared`.

| Code | Class | Evidence (`file:line`) | Reason |
|---|---|---|---|
| `CONFIG_INVALID` (36 sites, all executors) | `UserInput` | `ExtractStepExecutor.cs:57,63,67,78,84`; `VideoGenerateStepExecutor.cs:139-189,325-415,518,592`; `VideoAnalyzeStepExecutor.cs:254,260,264`; `VideoCompileStepExecutor.cs:253-327`; `VoiceoverStepExecutor.cs:276-288` | Step config missing, not JSON, or invalid |
| `{PREFIX}_CONFIG_INVALID` (`EDIT_ROOM`, `GRAPHICS_ROOM`, `COLOR_GRADE_ROOM`) | `UserInput` | `RoomStepExecutorBase.cs:500,506,510` with prefixes at `EditRoomStepExecutor.cs:111`, `ColorGradeRoomStepExecutor.cs:107` | same, room config |
| `INPUT_MISSING`, `PATH_NOT_FOUND`, `SHAPE_MISMATCH`, `UNKNOWN_ID` (Extract) | `UserInput` | `ExtractStepExecutor.cs:99,104,116,196,210-255,281,324` | The wiring between steps does not match the data. Note that `SHAPE_MISMATCH`/`PATH_NOT_FOUND` can also be caused by a **model** returning an unexpected shape upstream; the cause is the template wiring, so it stays `UserInput` (the refund exception below applies if the upstream step's own failure already classified). |
| `EXPECT_FAILED` | `UserInput` | `ExtractStepExecutor.cs:717`, `VideoAnalyzeStepExecutor.cs:1005`, `VideoCompileStepExecutor.cs:805` | An author-declared expectation on the output was not met |
| `VIEW_UNRESOLVED` | `UserInput` | `RoomStepExecutorBase.cs:517,526,534` | Room's `View` input does not resolve to an offered-id view |
| `SOURCE_UNRESOLVED`, `SOURCE_INVALID`, `SOURCE_TRIM_INVALID` | `UserInput` | `VideoAnalyzeStepExecutor.cs:1208,1891`; `VideoCompileStepExecutor.cs:947,953`; `VoiceoverStepExecutor.cs:489` | The source reference points at nothing usable |
| `INPUT_TOO_LARGE`, `DURATION_EXCEEDED`, `BYTE_BUDGET_EXCEEDED`, `MAX_CLIPS_EXCEEDED` | `UserInput` | `VideoAnalyzeStepExecutor.cs:1224,1251`; `VoiceoverStepExecutor.cs:510`; `VideoGenerateStepExecutor.cs:59` | A configured or plan limit was exceeded by the user's input. Plan-level limits should additionally surface as `EntitlementDenied` when B8a gates them up front. |
| `ANALYSIS_NOT_FOUND`, `ANALYSIS_INVALID`, `DECISION_UNRESOLVED`, `DECISION_INVALID`, `EMPTY_KEEP`, `UNKNOWN_ID`, `MIXED_SOURCE_SPAN`, `INVALID_SPAN`, `NO_SEGMENTS_REMAINING` (compile) | `UserInput` for wiring (`ANALYSIS_NOT_FOUND`, `DECISION_UNRESOLVED`); `PlatformInternal` where a first-party artifact is corrupt (`ANALYSIS_INVALID`); see note | `VideoCompileStepExecutor.cs:510,526,530,546,554,565,585,601,610,723` | `ANALYSIS_INVALID` means our own analysis artifact failed to deserialize: our fault. `DECISION_INVALID`, `EMPTY_KEEP`, `UNKNOWN_ID`, `MIXED_SOURCE_SPAN`, `INVALID_SPAN` come from the editor agent's decision: the model's output, so the agent step that produced it failed to meet its schema; treat as `PlatformProviderError` when the editor ran on a platform provider and `ByoProviderError` when BYO (a model that cannot follow the contract is a provider-quality failure, not the user's input). `NO_SEGMENTS_REMAINING` is `PlatformProviderError`/`ByoProviderError` by the same rule when caused by the decision, `UserInput` when caused by source trimming. |
| `CODEC_NOT_ALLOWED` | `UserInput` | `VideoCompileStepExecutor.cs:389,396,501,896-900` | Codec/preset not in the allowlist |
| `*_REQUIRES_REENCODE` (`GRAPHICS`, `MUSIC`, `SFX`, `INSERTS`, `TRANSITIONS`, `CAPTIONS`, `CENSOR`, `COLOR_GRADE`, `MULTI_SOURCE`, `REORDER`, `OUTPUT_FORMAT`, `VOICEOVER_MODE`, `GENERATED_CLIPS`, `PICKUPS`, plus `*_REQUIRES_AUDIO_REENCODE`, `STREAMCOPY_REQUIRES_KEYFRAME_SNAPPING`, `PROGRAM_AUDIO_FADE_REQUIRES_AUDIO_REENCODE`) | `UserInput` | `VideoCompileStepExecutor.cs` (config-combination checks run up front with every other pure config error) | Incompatible option combination |
| `CENSOR_UNVERIFIED` | `PlatformInternal` | `VideoCompileStepExecutor.cs:1268,1278` | Our censor verification could not confirm obscuring: our pipeline's failure |
| `ENCODE_FAILED` | `PlatformInternal` | `VideoCompileStepExecutor.cs:1480` | ffmpeg failed on a validated cut list; the argv is built by first-party code. If a source is genuinely corrupt this misclassifies as ours; accepted (refund-by-default). |
| `SOURCE_TRIM_FAILED`, `SOURCE_TRIM_UNAVAILABLE` | `PlatformInternal` | `VideoAnalyzeStepExecutor.cs:1237,1883,1901` | ffmpeg trim failed / runner missing |
| `TRANSCRIPTION_FAILED`, `TRANSCRIPTION_UNAVAILABLE`, `NO_TRANSCRIPTION_PROVIDER` | `PlatformProviderError` (platform ASR) or `ByoProviderError` (user's ASR row) ; `NO_TRANSCRIPTION_PROVIDER` is `UserInput` on a BYO-only org, `PlatformInternal` where the platform default is missing | `VideoAnalyzeStepExecutor.cs:1352,1379`; `Services/Voiceover/IVoiceoverAlignmentService.cs:85,91` | Class follows the resolved provider's owner (decision D3) |
| `VISION_FAILED`, `VISION_UNAVAILABLE` | same provider-owner rule | `VideoAnalyzeStepExecutor.cs:669,826,843` | |
| `NO_WORDS`, `DOWNLOAD_FAILED` (voiceover alignment) | `PlatformInternal` | `Services/Voiceover/IVoiceoverAlignmentService.cs:88,94` | Alignment ran on our own synthesized audio |
| `NO_LINES`, `SCRIPT_SOURCE_INVALID`, `SCRIPTWRITER_NOT_FOUND`, `SCRIPTWRITER_OUTPUT_INVALID`, `NARRATION_SOURCE_INVALID`, `NARRATION_STEP_NOT_FOUND`, `NARRATION_OUTPUT_INVALID`, `PICKUP_SOURCE_INVALID`, `PICKUP_STEP_NOT_FOUND`, `PICKUP_OUTPUT_INVALID` | `UserInput` for the `*_SOURCE_INVALID`/`*_STEP_NOT_FOUND`/`NO_LINES`/`SCRIPT_SOURCE_INVALID` wiring codes; provider-owner rule for the `*_OUTPUT_INVALID` codes (an upstream agent produced unusable output) | `VoiceoverStepExecutor.cs:297-345,356-408,451-476` | |
| `PROVIDER_NOT_RESOLVED`, `PROVIDER_NOT_FOUND` | `UserInput` if the referenced provider id was set by the user; `PlatformInternal` if it was a platform default | `VoiceoverStepExecutor.cs:527`; `VideoGenerateStepExecutor.cs:169` | |
| `VOICE_NOT_FOUND`, `VOICE_CONSENT_MISSING` | `UserInput` | `VoiceoverStepExecutor.cs:551-556` | |
| `VOICE_RESOLVER_UNAVAILABLE`, `VOICEOVER_PAYLOAD_NOT_WAV`, `VOICEOVER_PAYLOAD_NOT_DECODABLE` | `PlatformInternal` | `VoiceoverStepExecutor.cs:184,187,551` | Engine capability gap, or the synthesis provider returned a bad payload (platform provider: `PlatformProviderError`) |
| `INVALID_PROMPT` | `UserInput` | `VideoGenerateStepExecutor.cs:235,240,467` | |
| `EGRESS_REFUSED` | `UserInput` | `Shared/Inference/GeneratedShotEgressPolicy.cs:39`; `VideoGenerateStepExecutor.cs:384` | Plan names source-media keyframes under `AllowSourceMediaEgress: false` |
| `KEYFRAME_NOT_SUPPORTED` | `UserInput` | `Shared/Inference/VideoGenerationImageSupport.cs:44`; `VideoGenerateStepExecutor.cs:415,420` | The resolved provider kind has no image input to send a planned `firstFrame`/`lastFrame` on, so the plan is refused before any frame is cut. The step's provider is the workflow author's choice, exactly like `EGRESS_REFUSED` beside it |
| `KEYFRAME_MEDIA_UNAVAILABLE` | `PlatformInternal` | `VideoGenerateStepExecutor.cs:614` | The media session the frames are cut in could not be opened at all (no runner online, a session lease already held). Our infrastructure, and nothing was bought |
| `BUDGET_EXCEEDED` (video generation) | `UserInput` today; **superseded by `InsufficientCredits`** once B6c moves the step onto the credit reservation lifecycle | `VideoGenerateStepExecutor.cs:690` | The step's `MaxSpendCredits` or daily caps (both in credits since the video-generation credits wave) refused the reservation; nothing was bought |
| `PROVIDER_INSUFFICIENT_BALANCE` | `PlatformInternal` for a platform-managed provider (our vendor account is empty: the user did nothing wrong), `ByoProviderError` for a BYO row | `VideoGenerateStepExecutor.cs:1345,1404` | Easy to get backwards. It reads like "insufficient credits" but is the vendor's balance. |
| `PROVIDER_AUTH_FAILED` | `PlatformInternal` (platform key broken) / `ByoProviderError` | `VideoGenerateStepExecutor.cs:1346` | |
| `PROVIDER_REJECTED` | `UserInput` if the prompt was refused on content grounds; otherwise provider-owner rule | `VideoGenerateStepExecutor.cs:1347` | 4xx other than 401/402/403; cannot be told apart without parsing vendor text, so default is provider-owner rule |
| `PROVIDER_FAILED`, `PROVIDER_CANCELLED`, `POLL_ERROR`, `POLL_TIMEOUT`, `SUBMIT_OUTCOME_UNKNOWN`, `NO_DOWNLOAD_URL`, `ALL_TAKES_FAILED` | `PlatformProviderError` (platform) / `ByoProviderError` (BYO) | `VideoGenerateStepExecutor.cs:1206,1388,1411,1422,1428,1355,1362,1273,750` | Vendor-side failure. `SUBMIT_OUTCOME_UNKNOWN` may have been billed by the vendor; the existing ledger keeps the row `Submitted` and a retry re-polls (CLAUDE.md, video generation). The credit refund must wait for the ledger to settle, not fire at classification. |
| `NOT_SUBMITTED`, `RESERVATION_ORPHANED`, `DELIVERY_FAILED` | `NOT_SUBMITTED`: `UserCancelled` when the step was stopped, else `PlatformInternal`; `RESERVATION_ORPHANED`, `DELIVERY_FAILED`: `PlatformInternal` | `VideoGenerateStepExecutor.cs:1228,1234,1217,1297` | |
| `UNEXPECTED_ERROR` (every executor's catch-all) | `PlatformInternal` | `VideoGenerateStepExecutor.cs:204,1459,1464,1485`; `RoomStepExecutorBase.cs:812`; `VoiceoverStepExecutor.cs:1007`; `VideoAnalyzeStepExecutor.cs:351,1030`; `VideoCompileStepExecutor.cs:1718` | Unclassified exception inside our code. |
| `{PREFIX}_FAILED` (`EDIT_ROOM_FAILED`, `GRAPHICS_ROOM_FAILED`, `COLOR_GRADE_ROOM_FAILED`) | `PlatformProviderError`/`ByoProviderError` when the cause is model failure, `PlatformInternal` otherwise | `RoomStepExecutorBase.cs:646,725` | Room degraded after exhausting synthesis retries and the solo fallback. The room reason string (`degradeReason`) names the cause. |

Resolution of the "provider-owner rule": the engine already resolves the provider per step (`IAgentChatClientProvider`, `IInferenceProviderResolver`). B3b records `billing_mode` (`Platform`/`Byo`) on the call; the failure class takes that value of the failing call. When the call never resolved a provider, the class is `PlatformInternal`.

### Mapping: exceptions and non-coded paths

| Situation | Class | Evidence | Reason |
|---|---|---|---|
| Agent returned `!result.Success` after the run (the model gave up, or the output failed its schema check) | provider-owner rule | `Execution/StepExecutors/AgentStepExecutor.cs:189-209`, `:232-250` ("response contained no parseable JSON object") | The model failed to produce a usable answer. For BYO that is the user's model; for platform models it is ours. |
| `AgentWorkflowException` (the agent called `FailWorkflow`) | `UserInput` | `Agents/Tools/AgentWorkflowException.cs:8`; caught at `WorkflowExecutorService.cs:~790`; tool described in e.g. `Agents/Analysis/DependencyAnalyzerAgent.cs:52` ("missing data, inconsistent state") | The agent determined the input is unworkable. A model that abuses `FailWorkflow` would be misclassified; B6b adds a review sample to B13. |
| `TimeoutException` from an agent run | `PlatformInternal` | `Agents/ReelBoltAgentBase.cs:146`; not retried, `WorkflowExecutorService.cs:~807` | Our own `AgentRunTimeoutSeconds` budget expired. The cost of a 3-hour timed-out run is real and this class refunds it. Operators raise `Agents:<name>:RunTimeoutSeconds`. |
| `OperationCanceledException` with the execution token cancelled | `UserCancelled` when the registry reason is `Stopped by user`, else `PlatformInternal` | `WorkflowExecutorService.cs:577` (`MarkCancelledAsync`), `:638-652`; `Execution/ExecutionCancellationRegistry.cs:37-58` ("Cancelled by the workflow engine (no stop request was recorded...)") | The registry already records **why**; reuse its provenance. `Stopped by user {id}` is set at `WorkflowExecutorService.cs:673,711`. |
| Execution cancelled by restart | `PlatformInternal` | `Inference.Api/Services/StartupCleanupService.cs:50-59` (`Status = Cancelled`, `ErrorMessage = "Service restarted"`). F4 makes this lease-aware (D21). | A deploy must not cost the user credits |
| `HttpRequestException`/SDK client exception from a chat or embedding call | provider-owner rule | `Inference.Api/Consumers/ProjectFileIndexingConsumer.cs:200-215` (the only place status codes 408/409/425/429/>=500 are already treated as transient) | 4xx auth/quota errors on a BYO key are `ByoProviderError`. |
| `DbUpdateException` and other persistence failures | `PlatformInternal` | `WorkflowExecutorService.cs:~565-575`, `:586-611` | |
| `InvalidOperationException` from the retry wrapper (`failed after N attempts`) | **inherit the class of the last attempt**; `PlatformInternal` if absent | `WorkflowExecutorService.cs:775` | See "classification point" above |
| `EnsureAuthorArtifactProducedAsync` (an Author step passed but produced no artifact) | `PlatformProviderError`/`ByoProviderError` (model failed to render) | `WorkflowExecutorService.cs:524,1106`; `AgentStepExecutor.cs:217-250` (`TryDetectAuthorOutputFailure`, empty `outputStorageKey`) | |
| Sandbox render failure (Remotion build/render error in the sandbox) | provider-owner rule when the model's code is at fault, else `PlatformInternal` | `Agents/Tools/ReactRemotionSandboxTools.cs` | The model authored the code; but a sandbox outage is ours. Distinguish by tool result: a compile error in model code is the first, an executor/transport error the second. |
| Credit gate refusal (B6a) | `InsufficientCredits` | new | |
| Entitlement refusal (B8a) | `EntitlementDenied` | new | |
| `StepStatus.Skipped` (conditional branch not taken) | `None`, zero usage | `Shared/Data/Models/Enums.cs` (`StepStatus`) | |

Cache hits (`WorkflowStepResult.FromCache`, `CachedTokensSaved`) are `None`, **zero credits**, and emit no usage events; the saving is shown to the user, not billed.

### What to refund, precisely

- `PlatformProviderError` and `PlatformInternal`: **refund every credit the failed step consumed**, including failed retry attempts, and mark the matching `usage_events` rows (B2 adds a `refunded_at`/reversal event, never an update-in-place; the ledger stays append-only).
- Steps that completed before the failure keep their charges. The execution fails but the user received those step outputs and the step cache reuses them on a re-run (the cache key self-invalidates on any upstream change).
- The refund is idempotent: keyed on the `usage_events.idempotency_key` of the original events.
- Fraud guard: refunds apply only to platform-billed events. A user cannot manufacture `PlatformInternal` failures cheaply because the class is assigned by our code, not user input, and B12 reconciliation reports refund rate per user.

## Pricing compute (decision)

Compute kinds (`RenderOutputSeconds`, `EncodeOutputSeconds`, `AnalyzeMediaSeconds`) are priced from the **platform-side job spec**:

- **`RenderOutputSeconds` = composition duration x resolution tier.** Duration is `durationInFrames / fps` from the composition the job asks to render, and the tier (for example 720p, 1080p, 4K) is the output width/height in the job. Both are known to the platform before and after the job, independent of where it runs.
- **`EncodeOutputSeconds` = output duration of the compiled EDL x resolution tier.** `VideoCompile` computes this duration from the cut list before encoding.
- **`AnalyzeMediaSeconds` = probed duration of each analysed source**, taken from the platform's own probe (the same value `DURATION_EXCEEDED` checks).
- **Never** from wall-clock time and **never** from a runner-reported figure. A desktop runner (decisions D13-D16) is untrusted: reported time would let a buggy or malicious runner inflate or deflate billing. A runner job is billed from the job spec with `billing_mode = Runner` (a multiplier of 0 or a small fraction, set in `rate_multipliers`, so a runner costs the user no compute credits by default per D8).
- `DurationMs` (already on `WorkflowStepResult`) is retained **only for COGS analysis** (B13): comparing job-spec units against real elapsed time per template to correct the rate cards.
- Resolution tiers are an input to the rate card (`model_pattern`/tier in B2's `rate_cards`), so a 4K render costs more per output second than 720p without code change.

### As built (B3c)

- **Resolution tier = `usage_events.model_name` = the rate card's `model_pattern`.** `ComputeMetering.TierFor(width, height)` takes the SHORT side of the probed frame: `<= 720` is `720p`, `<= 1080` is `1080p`, anything larger is `uhd` (so a vertical 1080x1920 reel is `1080p`, and an unknown size such as audio only prices as `720p`). A rate card prices a tier by exact `model_pattern`, or `*` as a catch-all, so no schema change was needed and a new tier is a data row plus one branch in `TierFor`.
- **Provider kind for compute rows is `OpenAICompatible`** (`ComputeMetering.RateCardProviderKind`). `rate_cards` keys on a provider kind and compute has none; a new `InferenceProviderKind` member would appear in every provider picker and in the `IsUnsupportedCombination` allowlists (whose fall-through is ALLOW), so the cards live under an existing kind whose other usage kinds never collide. Revisit in B13 or with the runner work if that proves confusing.
- **Where the quantity comes from.** `EncodeOutputSeconds`: `IMediaProbe` on the file `VideoCompile` just encoded (after loudness normalisation, before upload), duration and frame size from that probe. `RenderOutputSeconds`: `RenderVideoAndUploadToStorage` fetches the object it just stored into scratch and probes it with the engine's `IMediaProbe` (ffmpeg is deliberately not in the sandbox); stills are neither probed nor metered. `AnalyzeMediaSeconds`: one row per analysed source, the probed duration `VideoAnalyze` already holds (one call site, one method, so the D10a split moves it as a unit). None reads a clock.
- **Failures are not metered yet.** Compute is recorded on success only. A failed encode or render is not charged here; billing a failed job and refunding by failure class is B6a/B6b's reservation work, which will price from the job spec.
- **Runner jobs** will record with `UsageBillingMode.Runner` (`ComputeMetering.RecordSecondsAsync` takes the mode); until E4 every compute row is `Platform`.
- **Seed:** `RateCardSeed.ComputeCards` writes 9 PLACEHOLDER rows (3 kinds x 3 tiers, USD per media second) for B13 to replace. `DatabaseSeeder` adds them once to an install that already has rate cards but no compute card.
- **Coverage (added 2026-10-09, after an unpriced Fish Audio transcription row on staging).** The same seeder also tops up any exact `(provider_kind, usage_kind, model_pattern)` triple an existing install has no row for at all — a retired card is an admin's intent and is never resurrected — and logs an `Error` naming every `(provider kind, capability, usage kind)` the platform can meter but cannot price. `ModelPriceCatalog` replaces the hand-written kind arrays with per-model published vendor prices, `ProviderCapabilityUsage` derives the wildcard rows from the same capability table the admin boundary uses, and `RateCardCoverageTests` fails the build when either drifts. docs/metering-and-billing.md "Rate-card coverage" is the authority.

Other B3c rules: embeddings, ASR and TTS are metered by decorators handed out by their factories (wrapped per `Get`, like chat), so every caller is covered with no call-site change. Embedding tokens use the provider's reported count and fall back to `ceil(chars / 4)` rather than recording zero. ASR seconds come from the WAV header the platform produced (a stream that is not a seekable, well-formed WAV is logged and not metered). TTS records the UTF-8 byte length of the text and the per-request model override when there is one. File indexing runs under a `source=Indexing` scope for the project's organization; the docs index runs under `source=DocsIndex` charged to the platform organization; video generation records `VideoGenSeconds` (the take's requested seconds) with key `videogen:{ExternalGenerationJob.Id}`, a metering record separate from the video-generation spend ledger (which is credited-denominated since the credits wave). Provider test pings and project-search query embeddings that run outside any scope land as `Unattributed` on the platform org, exactly as chat pings do.

Consequence for failures: because a compute charge is a function of the spec, a failed render is billed (before refund) the same as a successful one; the failure class decides whether it is refunded. Partial outputs are not prorated in v1.

## Interfaces this spec requires of later WPs

| WP | Obligation from this spec |
|---|---|
| B2 | Append `UsageKind` (11 members, order as listed above) and `FailureClass` (8 members, order as listed above) to `Shared/Data/Models/Enums.cs`; update `EnumShapeTests`. |
| B3a | Persist `InputTokens`/`OutputTokens`/cached-input split on `workflow_step_results`; add `FailureClass`/`FailureCode`. |
| B3b | Record `billing_mode` and the resolved provider owner on every chat call so failure classification can apply the provider-owner rule. Capture cached-input tokens. |
| B3c | Meter embeddings, ASR (audio seconds), TTS (UTF-8 bytes), video generation and compute using the job-spec rule above. |
| B6b | Assign `FailureClass` where codes are produced; propagate it through the retry wrapper; add a drift-guard test that every `"[A-Z_]+"` failure-code constant has a mapping; make unmapped default to `PlatformInternal`. |
| B6c | Map the video-generation ledger onto credits; `BUDGET_EXCEEDED` becomes `InsufficientCredits`. |
| F4 | Lease-aware startup cleanup, so a deploy does not produce the `Service restarted` refund path at all. |

## Founder decisions needed

None blocking. One policy worth confirming: **unmapped failures refund by default** (class `PlatformInternal`). It trades some revenue leakage for never billing users for failures we cannot explain; B12 reconciliation will report the refund rate so the trade can be revisited with data.
