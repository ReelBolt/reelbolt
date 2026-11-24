import type { StyleConfig, StyleTone } from './styles';
import type { DesignSystemPin } from './design-system';

export interface WorkflowDefinition {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  steps: WorkflowStep[];
  requiresUserInput: boolean;
}

export type StepType = 'Agent' | 'Conditional' | 'ForEach' | 'ReviewLoop' | 'Parallel' | 'Extract' | 'VideoAnalyze' | 'VideoCompile' | 'VideoGenerate' | 'EditRoom' | 'GraphicsRoom' | 'ColorGradeRoom' | 'Voiceover' | 'ThumbnailGenerate' | 'Revise' | 'DesignGraphic' | 'DailiesRoom' | 'TimelineRender' | 'DesignSystem' | 'ExtractFrame' | 'AudioGenerate' | 'PinInsert' | 'AudioCleanup' | 'VideoEnhance' | 'ImageGenerate';
export type StepStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Skipped';
export type AgentInputContextMode =
  | 'FullWorkflow'
  | 'PreviousStepOnly'
  | 'SelectedPriorSteps'
  | 'CustomMappedSubset';
/** Per-step override for cross-execution step-result caching. `Default` defers to the built-in StepType/AgentType-driven policy. */
export type CacheMode = 'Default' | 'Always' | 'Never';

export interface WorkflowStep {
  id: string;
  agentDefinitionId: string;
  stepOrder: number;
  label: string;
  edgeConditionJson?: string | null;
  stepType?: StepType;
  conditionExpression?: string | null;
  loopSourceExpression?: string | null;
  loopTargetStepOrder?: number | null;
  maxIterations?: number;
  minScore?: number | null;
  inputMappingJson?: string | null;
  agentInputContextMode?: AgentInputContextMode | null;
  selectedPriorStepOrdersJson?: string | null;
  trueBranchStepOrder?: string | null;
  falseBranchStepOrder?: string | null;
  /** JSON array of AgentDefinition GUIDs to run in parallel (Parallel step type only). */
  parallelAgentIdsJson?: string | null;
  /** JSON-serialized ExtractStepConfig (Extract step type only). Deserialize with JSON.parse. */
  extractConfigJson?: string | null;
  /** JSON-serialized VideoAnalyzeStepConfig (VideoAnalyze step type only). Deserialize with JSON.parse. */
  videoAnalyzeConfigJson?: string | null;
  /** JSON-serialized VideoCompileStepConfig (VideoCompile step type only). Deserialize with JSON.parse. */
  videoCompileConfigJson?: string | null;
  /** JSON-serialized VideoGenerateStepConfig (VideoGenerate step type only). Deserialize with JSON.parse. */
  videoGenerateConfigJson?: string | null;
  /** JSON-serialized VoiceoverStepConfig (Voiceover step type only). Deserialize with JSON.parse. */
  voiceoverConfigJson?: string | null;
  /** JSON-serialized ThumbnailGenerateStepConfig (ThumbnailGenerate step type only). Deserialize with JSON.parse. */
  thumbnailGenerateConfigJson?: string | null;
  /** JSON-serialized TimelineRenderStepConfig (TimelineRender step type only). Deserialize with JSON.parse. */
  timelineRenderConfigJson?: string | null;
  /** JSON-serialized DesignSystemStepConfig (DesignSystem step type only). Deserialize with JSON.parse. */
  designSystemConfigJson?: string | null;
  /**
   * JSON-serialized AudioGenerateStepConfig (AudioGenerate step type only; see {@link AudioGenerateStepConfig}).
   */
  audioGenerateConfigJson?: string | null;
  /** JSON-serialized ImageGenerateStepConfig (ImageGenerate step type only; see {@link ImageGenerateStepConfig}). */
  imageGenerateConfigJson?: string | null;
  /** JSON-serialized ExtractFrameStepConfig (ExtractFrame step type only; see {@link ExtractFrameStepConfig}). */
  extractFrameConfigJson?: string | null;
  /** JSON-serialized PinInsertStepConfig (PinInsert step type only; see {@link PinInsertStepConfig}). */
  pinInsertConfigJson?: string | null;
  /** JSON-serialized AudioCleanupStepConfig (AudioCleanup step type only; see {@link AudioCleanupStepConfig}). */
  audioCleanupConfigJson?: string | null;
  /** JSON-serialized VideoEnhanceStepConfig (VideoEnhance step type only; see {@link VideoEnhanceStepConfig}). */
  videoEnhanceConfigJson?: string | null;
  /** Step option (Agent steps): `{"executionId"}` or `{"versionId"}` — continue from an earlier run's composition. */
  continueCompositionJson?: string | null;
  /** Step option: register this step's delivered output as a project file. */
  registerOutputAsProjectFile?: boolean;
  /** JSON-serialized ConditionalStepConfig (Conditional step type only). Deserialize with JSON.parse. */
  conditionalConfigJson?: string | null;
  /** JSON-serialized ReviewLoopStepConfig (ReviewLoop step type only). Deserialize with JSON.parse. */
  reviewLoopConfigJson?: string | null;
  /** JSON-serialized EditRoomStepConfig (EditRoom step type only). Edited via EditRoomStepConfigEditor as a raw string that preserves unknown fields — see StepData.editRoomConfigJson. */
  editRoomConfigJson?: string | null;
  /** JSON-serialized GraphicsRoomStepConfig (GraphicsRoom step type only). Opaque passthrough, same as editRoomConfigJson. */
  graphicsRoomConfigJson?: string | null;
  /** JSON-serialized ColorGradeRoomStepConfig (ColorGradeRoom step type only). Opaque passthrough, same as editRoomConfigJson. */
  colorGradeRoomConfigJson?: string | null;
  /** JSON-serialized DailiesRoomStepConfig (DailiesRoom step type only). Opaque passthrough, same as editRoomConfigJson. */
  dailiesRoomConfigJson?: string | null;
  /** Per-step step-result-cache override. `null`/absent behaves the same as `'Default'`. */
  cacheMode?: CacheMode | null;
}

export interface CreateWorkflowRequest {
  name: string;
  steps: CreateWorkflowStepRequest[];
  requiresUserInput?: boolean;
}

export interface CreateWorkflowStepRequest {
  agentDefinitionId: string;
  stepOrder: number;
  label: string;
  stepType?: StepType;
  conditionExpression?: string | null;
  loopSourceExpression?: string | null;
  loopTargetStepOrder?: number | null;
  maxIterations?: number;
  minScore?: number | null;
  inputMappingJson?: string | null;
  agentInputContextMode?: AgentInputContextMode | null;
  selectedPriorStepOrdersJson?: string | null;
  trueBranchStepOrder?: string | null;
  falseBranchStepOrder?: string | null;
  /** JSON array of AgentDefinition GUIDs to run in parallel (Parallel step type only). */
  parallelAgentIdsJson?: string | null;
  /** JSON-serialized ExtractStepConfig (Extract step type only). */
  extractConfigJson?: string | null;
  /** JSON-serialized VideoAnalyzeStepConfig (VideoAnalyze step type only). */
  videoAnalyzeConfigJson?: string | null;
  /** JSON-serialized VideoCompileStepConfig (VideoCompile step type only). */
  videoCompileConfigJson?: string | null;
  /** JSON-serialized VideoGenerateStepConfig (VideoGenerate step type only). */
  videoGenerateConfigJson?: string | null;
  /** JSON-serialized VoiceoverStepConfig (Voiceover step type only). */
  voiceoverConfigJson?: string | null;
  /** JSON-serialized ThumbnailGenerateStepConfig (ThumbnailGenerate step type only). */
  thumbnailGenerateConfigJson?: string | null;
  /** JSON-serialized TimelineRenderStepConfig (TimelineRender step type only). */
  timelineRenderConfigJson?: string | null;
  /** JSON-serialized DesignSystemStepConfig (DesignSystem step type only). */
  designSystemConfigJson?: string | null;
  /** JSON-serialized AudioGenerateStepConfig (AudioGenerate step type only). */
  audioGenerateConfigJson?: string | null;
  /** JSON-serialized ImageGenerateStepConfig (ImageGenerate step type only). */
  imageGenerateConfigJson?: string | null;
  /** JSON-serialized ExtractFrameStepConfig (ExtractFrame step type only). */
  extractFrameConfigJson?: string | null;
  /** JSON-serialized PinInsertStepConfig (PinInsert step type only). */
  pinInsertConfigJson?: string | null;
  /** JSON-serialized AudioCleanupStepConfig (AudioCleanup step type only). */
  audioCleanupConfigJson?: string | null;
  /** JSON-serialized VideoEnhanceStepConfig (VideoEnhance step type only). */
  videoEnhanceConfigJson?: string | null;
  /** Step option (Agent steps): `{"executionId"}` or `{"versionId"}`. Omitted when unset. */
  continueCompositionJson?: string | null;
  /** Step option: register this step's delivered output as a project file. Omitted when false. */
  registerOutputAsProjectFile?: boolean;
  /** JSON-serialized ConditionalStepConfig (Conditional step type only). */
  conditionalConfigJson?: string | null;
  /** JSON-serialized ReviewLoopStepConfig (ReviewLoop step type only). */
  reviewLoopConfigJson?: string | null;
  /** JSON-serialized EditRoomStepConfig (EditRoom step type only). Opaque passthrough — see WorkflowStep.editRoomConfigJson. */
  editRoomConfigJson?: string | null;
  /** JSON-serialized GraphicsRoomStepConfig (GraphicsRoom step type only). Opaque passthrough — see WorkflowStep.graphicsRoomConfigJson. */
  graphicsRoomConfigJson?: string | null;
  /** JSON-serialized ColorGradeRoomStepConfig (ColorGradeRoom step type only). Opaque passthrough — see WorkflowStep.colorGradeRoomConfigJson. */
  colorGradeRoomConfigJson?: string | null;
  /** JSON-serialized DailiesRoomStepConfig (DailiesRoom step type only). Opaque passthrough — see WorkflowStep.dailiesRoomConfigJson. */
  dailiesRoomConfigJson?: string | null;
  /** Per-step step-result-cache override. `null`/absent behaves the same as `'Default'`. */
  cacheMode?: CacheMode | null;
}

export interface UpdateWorkflowRequest {
  name?: string;
  steps: CreateWorkflowStepRequest[];
  requiresUserInput?: boolean;
  /** Stores an explicit app manifest; the API checks it against the steps sent (400 APP_MANIFEST_INVALID). */
  appManifest?: AppManifest;
  /** True drops the stored manifest, so the app is derived from the steps again. Not with `appManifest`. */
  clearAppManifest?: boolean;
}

/** Set when a run finished (`Passed`) but its review loop stopped below the quality bar. */
export interface NeedsAttention {
  reason: string;
  stepOrder: number;
  score: number;
  minScore: number;
  iterations: number;
  /** Customer-ready sentence from the server; show it verbatim. */
  message: string;
}

export interface WorkflowExecution {
  id: string;
  workflowDefinitionId: string;
  status: 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Cancelled' | 'Stopped';
  startedAt: string | null;
  completedAt: string | null;
  iterationCount: number;
  resultJson: string | null;
  correlationId?: string;
  errorMessage?: string | null;
  stepResults: WorkflowStepResult[];
  /** The typed brief this run was started with (docs/run-brief.md); absent/null for a run without one. */
  brief?: RunBrief | null;
  /**
   * App inputs (docs/design/app-inputs.md 10.4): the run's app fields in words, labelled by the run's own page. Absent
   * for a run that set none.
   */
  appInputs?: RunAppInput[] | null;
  /** The steps this run executed (its own snapshot's), which `stepResults[].workflowStepId` reference. Absent/null for an older run: then the results reference the workflow's own steps. */
  steps?: WorkflowStep[] | null;
  /** True when Resume can continue this run from the step that stopped; false for an older run, which can only be retried. */
  canResume?: boolean;
  /** How many times this run was resumed. */
  resumeCount?: number;
  reviewScores: ReviewScore[];
  /** Non-null when the film is ready but did not reach the quality bar; status stays `Passed`. */
  needsAttention?: NeedsAttention | null;
  userRequest?: string | null;
  /** The editing style the run was started with (display name), when one was chosen. */
  styleName?: string | null;
  /** The run's video-shape override (`Vertical`, `Square`, `Landscape`, `Portrait`), when one was chosen. */
  outputFormat?: string | null;
  /** The brand/product versions the run was started from (SPEC §3.5); null when it pinned none. */
  designSystemPin?: DesignSystemPin | null;
  /** Plain-language notes about the pinned design (it has no tokens, or a newer draft was waiting); advice only. */
  designSystemWarnings?: string[] | null;
  /** The run's chosen output resolution (`720p`, `1080p`, `2160p`), or null for Auto. */
  outputResolution?: string | null;
}

export interface WorkflowTemplateSummary {
  key: string;
  name: string;
  description: string;
  version: number;
  autoCreateOnProject: boolean;
  requiresUserInput: boolean;
  stepCount: number;
  /**
   * The template reads an approved brand and product profile (the `-ds` templates); without one it
   * fails at its first step. Always sent by the API; optional here so older fixtures stay valid.
   */
  requiresProfile?: boolean;
}

export interface WorkflowStepResult {
  id: string;
  workflowStepId: string;
  output: string;
  /** True on a summary-detail result: the heavy fields are left out; load the step result for them. */
  detailOmitted?: boolean;
  tokensUsed: number;
  durationMs: number;
  executedAt: string;
  status?: StepStatus;
  inputJson?: string | null;
  outputJson?: string | null;
  errorDetails?: string | null;
  iterationNumber?: number | null;
  completedAt?: string | null;
  outputStorageKey?: string | null;
  /** Storage key of a large step artifact (e.g. a VideoAnalyze full analysis JSON or a VideoCompile EDL), separate from outputStorageKey so the execution UI never tries to play it as a video. */
  artifactStorageKey?: string | null;
  /** Persisted JSON array of tool-call records, same shape as the live 'step.tool-called' SSE event's data (toolName, argumentsPreview, resultPreview, sequence, ...). Populated once the step completes — see `hydrateHistoricalStepEvents`. */
  toolCallsJson?: string | null;
  /** Persisted JSON array of reasoning entries, same shape as the live 'step.reasoning' SSE event's data. Populated once the step completes — see `hydrateHistoricalStepEvents`. */
  reasoningJson?: string | null;
  /** EditRoom steps only, else null: JSON array of ALL discussion turns in order, full untruncated text. Per-turn shape matches `WorkflowStepChatTurn` (camelCase), except `text` here is never truncated. Populated once the step completes — see `hydrateHistoricalStepEvents`. */
  chatTranscriptJson?: string | null;
  /** True when this step was served from the cross-execution step-result cache instead of actually running. `tokensUsed`/`durationMs` are then 0, because this execution genuinely spent nothing — see `cachedTokensSaved` for what the original run cost. */
  fromCache?: boolean;
  /** Only meaningful when `fromCache` is true: the token count the ORIGINAL execution of this step spent, i.e. what reusing it saved. */
  cachedTokensSaved?: number | null;
  /** Set when a change request (revision) copied this step's result from the run it changed instead of running it again: the id of the result it was copied from. */
  reusedFromStepResultId?: string | null;
  /**
   * Where this step actually ran (E8-UI), from the persisted
   * `workflow_step_results.compute_target` — D6's decision, recorded by D12:
   * `'Local'` (the engine ran it in-process; the only target a deployment without
   * the compute fabric can produce), `'CloudPool'` (one of ReelBolt's workers) or
   * `'OrgRunner'` (a machine this workspace paired).
   *
   * Typed `string`, not the union, for the same reason `RunnerDevice.capabilities`
   * is `unknown`: it is data written by a service this build does not control, and
   * a fourth target added there must render as "not shown" rather than as a wrong
   * label. Read it through `parseComputeTarget` (lib/utils/compute-target.ts).
   */
  computeTarget?: string | null;
  /** Credits charged for this step (3 dp), when the server recorded a charge; null otherwise. */
  creditsCharged?: number | null;
  /** Stable failure code of a failed step (e.g. `COMPOSITION_SOURCE_NOT_FOUND`), when there is one. */
  failureCode?: string | null;
  /** Failure class name (`UserInput`, `Transient`, ...) used for refund and retry decisions. */
  failureClass?: string | null;
  /** 1-based position of the step in its workflow. */
  stepOrder?: number | null;
  stepLabel?: string | null;
  stepType?: string | null;
  /** The persisted tag, DERIVED server-side from `computeTarget` (a runner ran it) so the two cannot disagree. */
  fromRunner?: boolean;
  /** The paired machine that ran the step, when one did. Resolve its name from the workspace's runner list. */
  runnerId?: string | null;
  /** The runner job that carried the step, when one did. */
  runnerJobId?: string | null;
}

/**
 * One turn published by an `EditRoom` step's multi-agent discussion. Mirrors the backend
 * `WorkflowStepChatTurn` integration event field-for-field in camelCase. Delivered live over the
 * execution SSE stream as a `step.chat-turn` event (see `use-execution-stream.ts`); `text` is
 * truncated to ~600 chars at a word boundary (`truncated` flags this) — the full turn lives in the
 * step's transcript artifact (`WorkflowStepResult.artifactStorageKey`).
 */
export interface WorkflowStepChatTurn {
  executionId: string;
  stepId: string;
  stepResultId: string;
  projectId: string;
  workflowDefinitionId: string;
  stepOrder: number;
  stepLabel: string;
  correlationId: string;
  /** 0-based turn index within the room's discussion. */
  turnIndex: number;
  /** Configured turn ceiling, when known. */
  totalTurns?: number | null;
  /** Seat/persona name, e.g. "PacingEditor", "StoryEditor", "CraftEditor", or "Director". */
  speaker: string;
  speakerRole: 'editor' | 'director';
  text: string;
  truncated: boolean;
  /** Offered shot/silence/segment ids this turn referenced, e.g. ["s2", "t7"]. */
  idsMentioned: string[];
  occurredAt: string;
}

export interface ReviewScore {
  id: string;
  iterationNumber: number;
  score: number;
  comments: string;
  createdAt: string;
}

/**
 * Extract step configuration. Mirrors the backend `ExtractStepConfig` record
 * (ReelBolt.Shared/Workflows/ExtractStepConfig.cs) field-for-field in camelCase.
 * Serialized to `WorkflowStep.extractConfigJson` / `CreateWorkflowStepRequest.extractConfigJson`.
 */
export type ExtractOperation = 'Project' | 'Resolve' | 'Files';
export type ExtractInputSource = 'Previous' | 'Step' | 'Accumulated' | 'ProjectFiles';
export type ExtractUnknownIdBehaviour = 'Fail' | 'Skip';

export interface ExtractInputRef {
  from: ExtractInputSource;
  stepOrder?: number | null;
}

export interface ExtractExpectation {
  requiredPaths?: string[] | null;
  minItems?: number | null;
  maxItems?: number | null;
  nonEmptyStringPaths?: string[] | null;
}

export interface ExtractStepConfig {
  version: number;
  operation: ExtractOperation;
  inputs: Record<string, ExtractInputRef>;
  // -- project --
  path?: string | null;
  fields?: string[] | null;
  idField?: string | null;
  idPrefix: string;
  sortBy?: string | null;
  take?: number | null;
  skip: number;
  // -- resolve --
  idsPath?: string | null;
  recordsPath?: string | null;
  onUnknownId: ExtractUnknownIdBehaviour;
  // -- files --
  categories?: string[] | null;
  includeExtensions?: string[] | null;
  excludePathContains?: string[] | null;
  includeSummaries: boolean;
  includeContent: boolean;
  maxCharsPerFile: number;
  // -- universal --
  maxOutputChars: number;
  expect?: ExtractExpectation | null;
}

/**
 * Video editing step configuration. Mirrors the backend `VideoSource.cs`, `VideoAnalyzeStepConfig.cs`,
 * `VideoCompileStepConfig.cs` (ReelBolt.Shared/Workflows/) and the `VideoEditDecisionOutput` schema
 * (ReelBolt.Shared/Data/OutputSchemas.cs) field-for-field in camelCase. Enum values are PascalCase
 * string literals matching the C# member names exactly (see `ExtractOperation` above for precedent —
 * commit 8e4b6a5a fixed exactly this casing mismatch for Extract).
 */
export type VideoSourceKind = 'ProjectFile' | 'StepOutput' | 'PreviousStepOutput' | 'GeneratedClips' | 'StepResult' | 'BriefFiles';

export interface VideoSourceRef {
  kind: VideoSourceKind;
  /** Kind=ProjectFile -> project_files id. */
  projectFileId?: string | null;
  /** Kind=StepOutput -> that step's stepOrder. Ignored for PreviousStepOutput. */
  stepOrder?: number | null;
  /** Use the source only from this second (null = from the start). */
  inSec?: number | null;
  /** Use the source only up to this second (null = to the end). */
  outSec?: number | null;
  /** Kind=StepResult -> the output of that step result of an earlier run of this project. */
  stepResultId?: string | null;
}

export type VideoTranscriptionMode = 'Off' | 'Optional' | 'Required';

export interface VideoAnalyzeExpectation {
  minShots?: number | null;
  minTranscriptSegments?: number | null;
  maxSilenceRatio?: number | null;
  minShotsWithVisuals?: number | null;
}

/**
 * How much per-shot visual/audio detail (Phase 1 descriptors) the bounded view includes per shot.
 * `VideoAnalyzeStepExecutor.BuildBoundedView` degrades Full -> Compact -> None to fit
 * `maxOutputChars` BEFORE ever dropping an offered shot/silence/segment.
 */
export type VideoVisualDetail = 'None' | 'Compact' | 'Full';

export type VideoVisionMode = 'Off' | 'Optional' | 'Required';
export type VideoCaptionSelection = 'PerDuplicateGroup' | 'LongestShots' | 'EvenlySpaced';
/** `VideoSubShotMode` in `ReelBolt.Shared/Workflows/VideoAnalyzeStepConfig.cs`, member-for-member. */
export type VideoSubShotMode = 'Off' | 'Even' | 'Content';

/**
 * The `VideoDerushPrePassMode` members — the SINGLE SOURCE OF TRUTH for this enum on the web side.
 * Mirrors the backend `VideoDerushPrePassMode` enum in
 * `ReelBolt.Shared/Workflows/VideoAnalyzeStepConfig.cs` member-for-member; `derushPrePass` in the
 * schema registry spreads this list rather than re-typing it, so the builder, the assistant/MCP
 * `ListStepSchemas` payload and this type can never drift apart.
 *
 * `Fast` is the wave-2 member.
 * It is declared here ahead of the backend on purpose — see `derushPrePass` in
 * `lib/schemas/workflow-step-configs.ts` for why the schema must tolerate a member this build
 * does not know about.
 */
export const VIDEO_DERUSH_PRE_PASS_MODES = ['Off', 'Prior', 'Fast'] as const;

/**
 * `VideoAnalyzeStepConfig.DerushPrePass` — how much of the per-item derush pre-pass runs before the
 * story-editor agent sees the view. `Off` (the default) is today's behaviour exactly; `Prior` asks
 * a batched `keep` question per offered `s{n}`/`t{n}` and stamps the resulting `pKeep` onto the view
 * row as a prior; `Fast` (wave 2) additionally lets the step skip the editor when every item is
 * confidently decided.
 */
export type VideoDerushPrePassMode = (typeof VIDEO_DERUSH_PRE_PASS_MODES)[number];

export interface VideoAnalyzeStepConfig {
  version: number;
  /** Absent when `sources` (plural) is used instead — the backend record allows either. */
  source?: VideoSourceRef;
  /** Multi-source analysis. A one-element list behaves identically to a single `source`. Optional — omitted for the (still overwhelmingly common) single-source case. */
  sources?: VideoSourceRef[] | null;
  // -- silence detection --
  detectSilence: boolean;
  silenceThresholdDb: number;
  minSilenceMs: number;
  // -- shot/scene detection --
  detectShots: boolean;
  sceneThreshold: number;
  // -- transcription --
  transcription: VideoTranscriptionMode;
  transcriptionProviderId?: string | null;
  language?: string | null;
  wordTimestamps: boolean;
  maxAsrChunkBytes: number;
  // -- guardrails, checked before any decode --
  maxDurationSeconds: number;
  maxInputBytes: number;
  // -- prompt-view budget --
  maxOutputChars: number;
  maxViewSegments: number;
  maxSegmentTextChars: number;
  // -- Phase 1: visual scene analysis (one low-res raw-frame grid ffmpeg pass + pure C#) --
  analyzeVisuals: boolean;
  visualSampleFps: number;
  visualGridWidth: number;
  visualGridHeight: number;
  maxVisualSampleFrames: number;
  stillMotionThreshold: number;
  minStillWindowMs: number;
  maxStillWindowsPerShot: number;
  /** Now implemented and free from the grid — defaults true. See docs/video-editing.md; under-reports on soft/gradient letterbox edges (§4.3). */
  detectLetterbox: boolean;
  /** One extra ffmpeg invocation per measured shot (capped by `maxSharpnessShots`) — off by default, unlike the other free Phase 1/4 dimensions. */
  detectSharpness: boolean;
  // -- Phase 1: audio loudness (reuses the WAV already extracted for transcription, or extracts it) --
  analyzeAudioLevels: boolean;
  // -- Phase 1: near-duplicate / best-take grouping --
  detectNearDuplicates: boolean;
  duplicateSimilarityThreshold: number;
  duplicateWindowShots: number;
  // -- Phase 1: bounded-view detail level --
  visualDetail: VideoVisualDetail;
  maxViewDuplicateGroups: number;
  // -- Phase 4: semantic visual dimensions (D1-D4/D6 are free, derived from the same grid data) --
  /** Gates D1-D3 (colour temperature, tone curve, saturation character). Free. */
  analyzeColorGrading: boolean;
  /** Gates D4 look grouping (`k{n}`). Free. */
  detectLookGroups: boolean;
  /** Minimum look similarity (0..1) for two shots to share a look group. */
  lookSimilarityThreshold: number;
  /** Caps `view.lookGroups`. */
  maxViewLookGroups: number;
  /** Frames combined into one contact-sheet keyframe per captioned shot (clamped 1..3 server-side). 1 = a single mid-shot still, byte-identical to the pre-Phase-4 vision path. */
  keyframesPerShot: number;
  /** Step-wide ceiling on sharpness measurements when `detectSharpness` is on. */
  maxSharpnessShots: number;
  expect?: VideoAnalyzeExpectation | null;
  // -- Phase 2: vision-LLM shot captioning (default Off) — optional so createDefaultVideoAnalyzeStepConfig need not enumerate them --
  vision?: VideoVisionMode;
  visionProviderId?: string | null;
  captionSelection?: VideoCaptionSelection;
  maxCaptionedShots?: number;
  minCaptionShotSeconds?: number;
  keyframeMaxWidth?: number;
  visionTimeoutSeconds?: number;
  maxCaptionChars?: number;
  /** Persists each captioned shot's keyframe JPEG to storage under the video-analysis prefix. */
  persistKeyframes?: boolean;
  // -- Phase 3: motion-graphics overlay placement candidates --
  emitOverlayPlacements?: boolean;
  maxPlacementsPerShot?: number;
  maxPlacements?: number;
  maxTimeSlicesPerRegion?: number;
  // -- background-music candidate offering --
  offerMusicTracks?: boolean;
  maxMusicTracks?: number;
  // -- sound-effect candidate offering (`x{n}` ids consumed by AgentType.SoundDesigner) --
  offerSfxClips?: boolean;
  maxSfxClips?: number;
  // -- Phase 5: tracked chroma-plate screen-insert regions (`r{n}` ids consumed by the
  //    motion-graphics planner / graphics room). One extra medium-res grid pass per source. --
  detectInsertRegions?: boolean;
  /** Allowlisted plate colour word — constrain to a Select in the UI, never free text. */
  insertRegionColor?: string;
  minInsertRegionSeconds?: number;
  maxInsertRegions?: number;
  // -- Markerless object tracking (docs/video-editing.md "Tracking objects"). --
  /** Objects to find (label only — a vision model looks for them) or follow (label + region at a time). */
  trackObjects?: ObjectTrackTarget[] | null;
  objectTrackSampleFps?: number;
  objectTrackMaxSide?: number;
  objectDetectEverySec?: number;
  maxObjectDetectFrames?: number;
  maxObjectTracks?: number;
  objectVisionProviderId?: string | null;
  // -- Decision-engine phase 3 (decision-engine-p3): per-item derush pre-pass + input screening.
  //    Appended with defaults, exactly like the Phase 1-5 blocks above; all optional so
  //    createDefaultVideoAnalyzeStepConfig (a hand-written editor, not the schema-driven form)
  //    need not enumerate them and so an existing persisted/template config stays valid. --
  /**
   * How much of the per-item derush pre-pass runs. `Off` (the default) is byte-identical to before
   * this field existed. See `VideoDerushPrePassMode`.
   */
  derushPrePass?: VideoDerushPrePassMode;
  /**
   * Confidence at or above which an individual derush `keep` answer is treated as decided, in
   * `[0, 1]`. Deliberately mirrors `DecisionGateConfig.AcceptAt` (0.85) so there is ONE definition
   * of "confident" across the decision engine rather than a second, derush-local threshold.
   */
  derushAcceptAt?: number;
  /**
   * Minimum margin between the top two probabilities for a derush `keep` answer to count as
   * decided, in `[0, 1]`. Mirrors `DecisionGateConfig.MinMargin` (0.25).
   */
  derushMinMargin?: number;
  /**
   * When `true`, instruction-like spans found in on-screen text / ASR captions are quoted and
   * flagged (never dropped) so the editor agent can see them for what they are. Default `false`.
   */
  screenInstructionLikeSpans?: boolean;
  /** Shortest detected span kept as its own shot (ms); shorter flashes join the next shot. Default 200. */
  minShotMs?: number;
  /** Force the source(s) to be treated as a designed composition (null = automatic). */
  sourceIsComposition?: boolean | null;
  /** Locate each shot's main subject with the image AI (two calls per shot) so a Subject-fit compile can crop toward it. Default false. */
  locateSubjects?: boolean;
  /** What to look for ("the phone", "the presenter"); null asks for the main subject. */
  subjectLabel?: string | null;
  /** Most shots located per step (two image-AI calls each). Default 40. */
  maxSubjectShots?: number;
  /** Split detected shots longer than this many seconds into sub-shots ("part"). 0 = never. */
  maxShotSec?: number;
  /** How long shots are cut into sub-shots. Off (default; maxShotSec > 0 then behaves as Even), Even (fixed grid), Content (cut where the picture pauses or changes). */
  subShotMode?: VideoSubShotMode;
  /** Shortest Content sub-shot (0.5..10 s). Default 1.2. */
  minSubShotSec?: number;
  /** Longest sub-shot (clamped to 2 x minSubShotSec..60 s). Default 4. */
  maxSubShotSec?: number;
  /** Most parts one shot is split into by Content (2..64). Default 8. */
  maxSubShotsPerShot?: number;
  /** Measure the beat of offered music tracks and of musicBeatTrackProjectFileId for the editor. */
  analyzeMusicBeats?: boolean;
  /** The music file the edit is cut to. */
  musicBeatTrackProjectFileId?: string | null;
  /** Most offered music tracks measured (0..20, default 6). */
  maxBeatAnalyzedTracks?: number;
  segmentationProviderId?: string | null;
  /** Explicit consent for the clip's picture to leave this host for the segmentation provider. */
  allowSourceMediaEgress?: boolean;
  /** Longest source analysed per target, in seconds (1..600). A longer source fails closed. */
  maxSegmentationSeconds?: number;
  /** Model detection threshold, 0.01..1, sent to the provider verbatim. */
  segmentationDetectionThreshold?: number;
  /** Most objects tracked at once, 1..16. */
  maxSegmentationObjects?: number;
  /** Provider call timeout, in seconds (60..3600). */
  segmentationTimeoutSeconds?: number;
}

export type VideoCompileMode = 'Reencode' | 'StreamCopy';

/**
 * The `RoomDecisionMode` members — the SINGLE SOURCE OF TRUTH for this enum on the web side.
 * Mirrors the backend `RoomDecisionMode` enum on the shared `IRoomStepConfig` surface
 * (`ReelBolt.Shared/Workflows/RoomStepConfig.cs`); `decisionScheduling` in the schema registry
 * spreads this list rather than re-typing it.
 *
 * `Adaptive` is the wave-2 member;
 * it is declared here ahead of the backend on purpose — see `decisionScheduling` in
 * `lib/schemas/workflow-step-configs.ts`.
 */
export const ROOM_DECISION_MODES = ['Off', 'Questions', 'Adaptive'] as const;

/**
 * `decisionScheduling` — the decision-engine knob shared by all three room step configs
 * (`EditRoomStepConfig` / `GraphicsRoomStepConfig` / `ColorGradeRoomStepConfig`), which exposes it
 * through the backend's `IRoomStepConfig` surface. `Off` (the default) is today's round-robin
 * scheduling plus the `ROOM_DECIDED` sentinel, byte-identically; `Questions` additionally asks
 * `converged?` and `next_speaker` after each director turn and records the answers as advisory;
 * `Adaptive` (wave 2) acts on them.
 *
 * The three room configs are, in this file, opaque JSON strings on `WorkflowStep`/`StepData`
 * (`editRoomConfigJson` / `graphicsRoomConfigJson` / `colorGradeRoomConfigJson`) rather than typed
 * interfaces, so this union names the value type of that one shared property — the registry is
 * where the field itself is declared.
 */
export type RoomDecisionMode = (typeof ROOM_DECISION_MODES)[number];

/**
 * How the background-music bed behaves relative to dialogue. Mirrors the backend
 * `MusicDuckingMode` enum. `SpeechEnvelope` lifts the music during non-speech windows via a
 * deterministic keyframed volume envelope computed from the analysis artifact; `Off` is a constant
 * ducked bed throughout.
 */
export type MusicDuckingMode = 'Off' | 'SpeechEnvelope';

/** How a music track's own duration relates to the compiled edit's. Mirrors the backend `MusicFit` enum. */
export type MusicFit = 'LoopToFit' | 'PlayOnce';

/**
 * How much transition treatment VideoCompile applies between cuts. `Off` reproduces today's exact
 * hard-cut behavior byte-for-byte. `AudioOnly` applies a short audio declick at every splice with
 * no visual change. `Auto` lets the compile step pick a treatment per cut from measured shot data.
 * `Expressive` allows more noticeable transitions (dissolves/dip-to-black) where warranted.
 * `Editor` lets the story editor name each seam's transition preset and speed by word.
 */
export type VideoTransitionPolicy = 'Off' | 'AudioOnly' | 'Auto' | 'Expressive' | 'Editor';

export interface VideoCompileExpectation {
  minOutputSeconds?: number | null;
  maxOutputSeconds?: number | null;
  minRetainedRatio?: number | null;
  maxRetainedRatio?: number | null;
}

export interface VideoCompileStepConfig {
  version: number;
  /** Only `from: 'Previous'` or `from: 'Step'` are valid here (validated server-side). Reuses ExtractInputRef verbatim. */
  decision: ExtractInputRef;
  /** Which VideoAnalyze step's full artifact to resolve ids against. */
  analysisStepOrder: number;
  /** Cross-execution override: resolve a prior run's artifact instead of this execution's. */
  analysisStepResultId?: string | null;
  mode: VideoCompileMode;
  prePaddingMs: number;
  postPaddingMs: number;
  minSegmentMs: number;
  maxSegments: number;
  /** Required=true whenever mode is StreamCopy. */
  allowKeyframeSnapping: boolean;
  outputFileName: string;
  // Allowlisted at execution time (argv-injection surface) — constrain to selects in the UI, never free text.
  videoCodec: string;
  audioCodec: string;
  crf: number;
  preset: string;
  /** Optional rate control for the video; absent keeps the plain CRF encode. */
  rateControl?: TimelineRenderRateControl | null;
  /** 200..100000; required with `Capped` or `Average`, forbidden otherwise. */
  videoBitrateKbps?: number | null;
  registerProjectFile: boolean;
  // -- Phase 3: optional motion-graphics overlays (see docs/video-editing.md
  //    "Motion graphics (Phase 3)"). enableGraphics=false (default) is byte-identical to the
  //    pre-Phase-3 compile path. --
  /** Which step's resolved MotionGraphicsPlanOutput to apply. `null` = no graphics plan looked up. Only `from: 'Previous'` or `from: 'Step'` are valid, same as `decision`. */
  graphicsPlan?: ExtractInputRef | null;
  enableGraphics: boolean;
  maxOverlays: number;
  overlayShortMs: number;
  overlayMediumMs: number;
  overlayHoldMs: number;
  overlayFadeMs: number;
  /** Percent of frame height, clamped 2..12 server-side. */
  overlayFontSizePct: number;
  // Allowlisted at execution time, same discipline as videoCodec/audioCodec/preset above.
  overlayFontColor: string;
  overlayBoxColor: string;
  maxOverlayTextChars: number;
  maxOverlaySubtextChars: number;
  // -- Program open/close fades (deterministic, no transition treatment required) --
  programFadeInMs?: number | null;
  programFadeOutMs?: number | null;
  programAudioFadeInMs?: number | null;
  programAudioFadeOutMs?: number | null;
  /** Allowlisted ('black' | 'white'), same discipline as videoCodec/audioCodec/preset above — constrain to a Select in the UI, never free text. */
  programFadeColor?: string | null;
  // -- Seam transitions between cuts. transitionPolicy='Off' (default) is byte-identical to the
  //    pre-transitions compile path; the remaining knobs only take effect for non-Off policies. --
  transitionPolicy?: VideoTransitionPolicy | null;
  audioSeamRampMs?: number | null;
  softCutMs?: number | null;
  dissolveMs?: number | null;
  dipToBlackMs?: number | null;
  dipCutMs?: number | null;
  maxTransitionMs?: number | null;
  maxTransitionRatioPct?: number | null;
  maxTransitionSegments?: number | null;
  sectionBreakGapMs?: number | null;
  // -- Background music (see docs/video-editing.md "Background music"). enableMusic=false
  //    (default) is byte-identical to the pre-music compile path. --
  /** Which step's resolved MotionGraphicsPlanOutput-style `MusicPlanOutput` to apply (a MusicSupervisor agent step). Only `from: 'Previous'` or `from: 'Step'` are valid. */
  musicPlan?: ExtractInputRef | null;
  /** Deterministic no-agent path: a project file id used as the music bed directly, no MusicSupervisor step needed. */
  musicTrackProjectFileId?: string | null;
  enableMusic?: boolean;
  musicDucking?: MusicDuckingMode;
  musicFitPolicy?: MusicFit;
  musicFadeInMs?: number;
  musicFadeOutMs?: number;
  // -- Phase 5: tracked screen inserts. Requires mode='Reencode' and a configured graphicsPlan
  //    (overlays and inserts arrive in the same MotionGraphicsPlanOutput). --
  enableInserts?: boolean;
  /** Tracks below this confidence are dropped rather than composited badly (0..1). */
  minInsertConfidence?: number;
  maxInserts?: number;
  /** Outward expansion of the tracked quad before compositing (0..0.1). Matted inserts use at least 0.1. */
  insertOverscan?: number;
  /** Flat corner pin, curved mesh warp, or decide from the measured curvature. */
  insertSurface?: InsertSurfaceMode;
  /** Clip the insert to the plate's keyed silhouette (rounded corners, notch, occluders). */
  enableInsertMatte?: boolean;
  /**
   * Deterministic insert content: a video/image project file composited into every confident
   * tracked plate — no motion-graphics plan needed. The file is CONTENT, never an edit source.
   */
  insertContentProjectFileId?: string | null;
  /** How insert content is shaped to the plate before the warp. */
  insertFit?: InsertContentFit;
  /** Labels of tracked objects to obscure ("*" = every tracked object). */
  censorLabels?: string[] | null;
  censorStyle?: CensorStyle;
  /** Outward growth of each tracked region before obscuring (0..1). */
  censorPadding?: number;
  /** Seconds the first/last position is held around each track (0..3). */
  censorHoldSec?: number;
  /** Ignore `decision` and keep every analyzed source whole, in order. */
  keepWholeSources?: boolean;
  // -- Colour grading (see docs/video-editing.md "Color grading"). Requires mode='Reencode'. --
  /** Which step's resolved `ColorGradePlanOutput` to apply — a solo Colorist agent step or a ColorGradeRoom step, interchangeably. */
  colorGradePlan?: ExtractInputRef | null;
  enableColorGrade?: boolean;
  // -- Sound effects (see docs/video-editing.md "Sound effects"). Requires mode='Reencode' and a
  //    non-copy audio codec. There is deliberately no deterministic no-agent fallback field. --
  /** Which step's resolved `SfxPlanOutput` to apply (a SoundDesigner agent step). */
  sfxPlan?: ExtractInputRef | null;
  enableSfx?: boolean;
  maxSfxCues?: number;
  maxSfxCueSeconds?: number;
  // -- Generated clips (see docs/video-generation.md). enableGeneratedClips=false (default) is
  //    byte-identical to the pre-wave compile path. Requires mode='Reencode'
  //    (GENERATED_CLIPS_REQUIRE_REENCODE). Appended AFTER maxSfxCueSeconds in the C# record, so the
  //    order here matches. There is deliberately no per-purpose placement list: placement derives
  //    from each planned shot's own `purpose`, exactly as graphics/inserts derive from their own
  //    plan's items. --
  /** Which step's generated-clip manifest to place — the VideoGenerate step, whose per-clip manifest is written to that step result's ArtifactStorageKey. `null` = no manifest looked for. Only `from: 'Previous'` or `from: 'Step'` are valid, same as `decision`. */
  generatedClips?: ExtractInputRef | null;
  enableGeneratedClips?: boolean;
  maxGeneratedClips?: number;
  /** Longest stretch of a SeamBridge clip spliced between beats (ms). Default 3000. */
  maxSeamBridgeMs?: number;
  /** Fill beside an aspect-mismatched clip: black bars (default) or a blurred copy. */
  canvasFill?: 'Black' | 'Blur';
  // -- Output format (docs/video-editing.md "Output format"). Appended after canvasFill in the C#
  //    record. 'Source' (default) sizes the canvas from the largest analysed source. --
  /** Aspect ratio / target platform: 'Vertical' 1080x1920, 'Square' 1080x1080, 'Landscape' 1920x1080, 'Portrait' 1080x1350. */
  outputFormat?: VideoOutputFormat;
  /** How a clip of a different aspect fills a fixed format: centre 'Crop' (default) or 'Pad'. */
  outputFit?: VideoOutputFit;
  /** Requested program length in seconds; 0 = no target. Reported, warned on a >20% miss. */
  targetDurationSec?: number;
  expect?: VideoCompileExpectation | null;
  // -- Voiceover (see docs/voiceover.md). enableVoiceover=false (default) is byte-identical to the
  //    pre-voiceover compile path. Requires mode='Reencode'. --
  /** Mixes the lines a Voiceover step synthesized over the program during the same encode. */
  enableVoiceover?: boolean;
  /** The 1-based order of the Voiceover step whose lines to mix. `null` = none looked for. */
  voiceoverStepOrder?: number | null;
  // -- Burned captions (see docs/voiceover.md "Captions"). enableCaptions=false (default) is
  //    byte-identical. Cut from the Voiceover step's own sanitized, actually-synthesized text and
  //    measured word timings — never from the narration plan's raw text. --
  enableCaptions?: boolean;
  captionStyle?: CaptionStyle;
  captionFontSizePct?: number;
  captionFontColor?: string;
  captionBoxColor?: string;
  captionHighlightColor?: string;
  captionMaxCharsPerCue?: number;
  maxCaptionCues?: number;
  // -- Dialogue under narration (see docs/voiceover.md "Pickups and dubs"). OverMusic (default)
  //    is byte-identical to the phase-1 mix. --
  voiceoverMode?: VoiceoverMode;
  voiceoverDuckDb?: number;
  voiceoverDuckRampMs?: number;
  // -- Pickups (see docs/voiceover.md "Pickups"). enablePickups=false (default) is byte-identical. --
  enablePickups?: boolean;
  /** The 1-based order of the pickup-sourced Voiceover step whose lines are spliced over the takes they repair. */
  pickupVoiceoverStepOrder?: number | null;
  // -- Caption typography (docs/voiceover.md "Captions"). Appended after targetDurationSec in the
  //    C# record. captionFontSizePct 0 or 4 (the old default) means the automatic size. --
  /** Burned-caption typeface; 'Auto' (default) uses the style's own font. Only bundled fonts. */
  captionFont?: CaptionFont;
  /** Where captions sit; 'Auto' (default) = lower third for Subtitle/Karaoke, centre for Punch. */
  captionPosition?: CaptionPosition;
  /** Drop the clips' own sound: only music, narration and sound effects are heard. Default false. */
  muteSourceAudio?: boolean;
  // -- Story order (docs/video-editing.md "Playing spans in the editor's order"). Appended after
  //    muteSourceAudio in the C# record. 'SourceOrder' (default) is byte-identical. --
  /** 'AsListed' plays every kept span in the editor's order, even within one clip. */
  keepOrder?: VideoKeepOrder;
  // -- Several insert contents and insert-content dips (docs/video-editing.md "Inserting an
  //    existing video"). Appended after keepOrder in the C# record. --
  /** Different content per source clip / tracked region; insertContentProjectFileId is the fallback. */
  insertContents?: VideoInsertContentAssignment[] | null;
  /** Near-black dips in project-file insert video: 'Freeze' (default), 'Cut' or 'Keep'. */
  insertContentDips?: InsertContentDipMode;
  /** Snap every cut to the music's beat or bar (docs/video-editing.md "Cutting to the beat"). 'Off' (default). */
  beatSync?: VideoBeatSync;
  /** Bring the finished video to a platform loudness (docs/video-editing.md "Program loudness"). 'Off' (default). */
  loudnessTarget?: ProgramLoudness;
  /**
   * Which step's `TakeSelectionOutput` (a DailiesRoom step) picks ONE generated take per planned
   * shot when the generation step made several (`takes` > 1). Null means every delivered take flows
   * exactly as before. Appended LAST in the C# record, after loudnessTarget (issue #93 phase 4).
   */
  takeSelection?: ExtractInputRef | null;
  // -- Sound from an inserted clip (docs/video-editing.md "Sound from an inserted clip"). Appended
  //    after takeSelection in the C# record: mixInsertAudio=false (default) is byte-identical to
  //    the silent-insert compile path. Requires enableInserts and audioCodec other than 'copy'. --
  /** Mixes the sound an inserted clip carries in while its picture is on screen. A clip with no sound stays silent. */
  mixInsertAudio?: boolean;
  /** Volume applied to the inserted clip's own sound, in dB (−24..12). Only read while mixInsertAudio is on. */
  insertAudioGainDb?: number;
  // -- Keyframed reframing, per-source fits, subject safe area and insert content by reference
  //    (docs/video-editing.md "Keyframed reframing"). Appended last in the C# record. All null = byte-identical. --
  /** Keyframed zoom/pan over stretches of source clips. Needs `mode: 'Reencode'`. Edited as JSON; the server validates. */
  reframes?: VideoReframe[] | null;
  /** Per-source override of `outputFit`, keyed by 0-based source index. */
  sourceFits?: Record<string, VideoOutputFit> | null;
  /** `outputFit: 'Subject'` only: frame-edge margin text is kept inside (0..0.25 of the canvas). */
  subjectSafeArea?: number | null;
  /** Insert content by reference — the fallback for a plate no `insertContents` entry matches. */
  insertContentRef?: InsertContentRef | null;
  // -- Prompted region effects: apply a filter to just one tracked object (plan §4/§5). Appended
  //    AFTER DesignSystemFonts in the C# record, so it is last here too. Absent/null is
  //    byte-identical to the pre-feature compile path; any entry requires mode='Reencode'. --
  /** Filters applied to tracked objects, at most 8. Each target is a segmentation track id or prompt. */
  regionEffects?: VideoRegionEffect[] | null;
}

/** Mirrors `ReframeEasing` in VideoReframe.cs — APPEND ONLY. */
export type ReframeEasing = 'Linear' | 'EaseIn' | 'EaseOut' | 'EaseInOut' | 'Hold';

/** Mirrors `ReframeKeyframe`. */
export interface ReframeKeyframe {
  atSec: number;
  zoom?: number;
  centerX?: number;
  centerY?: number;
  easing?: ReframeEasing;
}

/** Mirrors `VideoReframe`. */
export interface VideoReframe {
  sourceIndex: number;
  fromSec?: number | null;
  toSec?: number | null;
  keyframes: ReframeKeyframe[];
}

/** Mirrors `InsertContentRef` (T-106). `from` is `ProjectFile`, `Step` or `StepResult`, with exactly its own id field. */
export interface InsertContentRef {
  from: 'ProjectFile' | 'Step' | 'StepResult';
  projectFileId?: string | null;
  stepOrder?: number | null;
  stepResultId?: string | null;
  // -- Prompted region effects: apply a filter to just one tracked object (plan §4/§5). Appended
  //    AFTER DesignSystemFonts in the C# record, so it is last here too. Absent/null is
  //    byte-identical to the pre-feature compile path; any entry requires mode='Reencode'. --
  /** Filters applied to tracked objects, at most 8. Each target is a segmentation track id or prompt. */
  regionEffects?: VideoRegionEffect[] | null;
}

/** Mirrors `VideoBeatSync` in VideoCompileStepConfig.cs — APPEND ONLY. */
export type VideoBeatSync = 'Off' | 'Beat' | 'Bar';

/** Mirrors `ProgramLoudness` in VideoCompileStepConfig.cs (append-only, persisted by name). */
export type ProgramLoudness = 'Off' | 'Social' | 'Web' | 'Broadcast';

/** Mirrors `VideoInsertContentAssignment` in VideoCompileStepConfig.cs. */
export interface VideoInsertContentAssignment {
  projectFileId: string;
  /** 0-based source clip of the analyze step; null = any clip. */
  sourceIndex?: number | null;
  /** A tracked region id (`r{n}`); null = any region. */
  regionId?: string | null;
  /** This content's own fit; null = the step's insertFit. */
  fit?: InsertContentFit | null;
  /** Names the content by reference instead of `projectFileId` (which may then be empty). */
  insertContentRef?: InsertContentRef | null;
}

/** Mirrors `InsertContentDipMode` in VideoCompileStepConfig.cs — APPEND ONLY. */
export type InsertContentDipMode = 'Freeze' | 'Cut' | 'Keep';

/** Mirrors `VideoKeepOrder` in VideoCompileStepConfig.cs — APPEND ONLY. */
export type VideoKeepOrder = 'SourceOrder' | 'AsListed';

/** Mirrors `CaptionStyle` in VideoCompileStepConfig.cs — APPEND ONLY. */
export type CaptionStyle = 'Subtitle' | 'Karaoke' | 'Punch' | 'Bold';

/** Mirrors `CaptionFont` in VideoCompileStepConfig.cs — APPEND ONLY. Every member but 'Auto' is a font bundled in the workflow-engine image. */
export type CaptionFont = 'Auto' | 'Inter' | 'Montserrat' | 'Poppins' | 'BebasNeue' | 'Anton' | 'DejaVuSans';

/** Mirrors `CaptionPosition` in VideoCompileStepConfig.cs — APPEND ONLY. */
export type CaptionPosition = 'Auto' | 'Bottom' | 'Center' | 'Top';

/** Mirrors `VoiceoverMode` in VideoCompileStepConfig.cs — APPEND ONLY. */
export type VoiceoverMode = 'OverMusic' | 'DuckDialogue' | 'ReplaceDialogue';

/** Corners in 0..1 of the source frame, TL/TR/BL/BR like every insert quad. */
export interface QuadCorners01 {
  x0: number; y0: number; x1: number; y1: number; x2: number; y2: number; x3: number; y3: number;
}

/**
 * The `ObjectTrackMethod` members — the SINGLE SOURCE OF TRUTH for this enum on the web side.
 * Mirrors the backend `ObjectTrackMethod` in `ReelBolt.Shared/Workflows/VideoAnalyzeStepConfig.cs`
 * member-for-member (append-only, persisted by name); `method` in the schema registry spreads this
 * list rather than re-typing it, so the form, the assistant/MCP step-schema payload and this type
 * cannot drift apart.
 *
 * `Feature` is the free planar tracker that runs on this host and is the default. `Segmentation`
 * follows the object with a hosted text-prompted segmentation model instead, which is a paid,
 * explicit opt-in (see `VideoAnalyzeStepConfig.allowSourceMediaEgress`).
 */
export const OBJECT_TRACK_METHODS = ['Feature', 'Segmentation'] as const;

/** How one tracked object is followed. See `OBJECT_TRACK_METHODS`. */
export type ObjectTrackMethod = (typeof OBJECT_TRACK_METHODS)[number];

/**
 * How many of the objects a segmentation prompt matches one track covers. `AllInstances` (the default
 * when absent) follows every instance the prompt matches across the clip — what an effect whose job is
 * to hide something usually wants; `SingleTrack` deliberately follows one object only.
 */
export const OBJECT_MATCH_SCOPES = ['AllInstances', 'SingleTrack'] as const;

/** How many of the objects a prompt matches one track covers. See `OBJECT_MATCH_SCOPES`. */
export type ObjectMatchScope = (typeof OBJECT_MATCH_SCOPES)[number];

/** Mirrors `ObjectTrackTarget` in VideoAnalyzeStepConfig.cs. */
export interface ObjectTrackTarget {
  label: string;
  atSec?: number | null;
  region?: QuadCorners01 | null;
  /**
   * How this object is followed. ABSENT means `Feature` — the C# record's own default — so a target
   * persisted before the member existed keeps its meaning, and the form only writes it once the user
   * picks a method.
   */
  method?: ObjectTrackMethod;
  /**
   * How many instances the prompt matches. ABSENT means `AllInstances` — the C# record's own default —
   * so a target persisted before the member existed keeps its meaning. Only meaningful for segmentation.
   */
  matchScope?: ObjectMatchScope;
}

/** Mirrors `CensorStyle` in VideoCompileStepConfig.cs. */
export type CensorStyle = 'Blur' | 'Pixelate' | 'Fill';

/**
 * The `RegionEffect` members — the SINGLE SOURCE OF TRUTH for this enum on the web side. Mirrors
 * `RegionEffect` in `ReelBolt.Shared/Workflows/VideoCompileStepConfig.cs` member-for-member
 * (append-only, persisted by name); `effect` in the schema registry spreads this list rather than
 * re-typing it.
 *
 * Each member maps to one first-party ffmpeg filter chain on the compile side — the enum never
 * carries a filter string, and the target never reaches argv.
 */
export const REGION_EFFECTS = ['Blur', 'Pixelate', 'Highlight', 'Spotlight', 'ColorPop', 'Outline'] as const;

/** What a `VideoRegionEffect` does to its object. See `REGION_EFFECTS`. */
export type RegionEffect = (typeof REGION_EFFECTS)[number];

/**
 * Mirrors `VideoRegionEffect` in VideoCompileStepConfig.cs: ONE effect applied to ONE object found by
 * the analyse step, for as long as that object is on screen. Optional members carry the C# record's
 * own defaults, exactly like `VideoInsertContentAssignment` above.
 */
export interface VideoRegionEffect {
  /** A segmentation track id (`g1`) or the segmentation prompt, matched case-insensitively. Never a path. */
  target: string;
  effect: RegionEffect;
  /** Apply the effect to everything EXCEPT the object. */
  invert?: boolean;
  /** 0..2 — per-effect strength (blur radius, pixelate block size, brighten gain). */
  strength?: number;
  /** 0..64 px of edge softness at output resolution; 0 = a crisp mask edge. */
  featherPx?: number;
  /** 0..1 mask alpha. */
  opacity?: number;
}

/** Mirrors `InsertSurfaceMode` in VideoCompileStepConfig.cs. */
export type InsertSurfaceMode = 'Auto' | 'Planar' | 'Mesh';

/** Mirrors `InsertContentFit` in VideoCompileStepConfig.cs. */
export type InsertContentFit = 'Auto' | 'Stretch' | 'Contain' | 'Cover';

/**
 * How a VideoGenerate step obtains its prompt. Serialized as a string (JsonStringEnumConverter),
 * mirroring `VideoGeneratePlanSource` in VideoGenerateStepConfig.cs. `StepRef` is APPENDED AFTER
 * `Inline` in the C# enum — the numeric values are what persisted payloads key on — so the order
 * here matches: `Inline` is a prompt typed into this step, `StepRef` is a planner step's
 * `GeneratedShotPlanOutput` (one generated clip per planned shot).
 */
export type VideoGeneratePlanSource = 'Inline' | 'StepRef' | 'ProjectFile';

/**
 * Typed shape of `VideoGenerateConfigJson`. Mirrors `VideoGenerateStepConfig.cs`
 * (ReelBolt.Shared/Workflows/) field-for-field; `model`/`resolution`/`ratio` are free strings there,
 * the editor only offers the MiniMax values as UI guidance.
 */
/** Behaviour when the step re-runs after a ReviewLoop loop-back; mirrors `VideoGenerateLoopBackBehavior`. */
export type VideoGenerateLoopBackBehavior = 'Reuse' | 'Regenerate';

export interface VideoGenerateStepConfig {
  planSource: VideoGeneratePlanSource;
  inlinePrompt: string | null;
  /**
   * Which step's resolved `GeneratedShotPlanOutput` to generate clips from, when `planSource` is
   * `StepRef`. REQUIRED in that mode — the backend's Validate() rejects its absence — and ignored
   * when `planSource` is `Inline`. Reuses `ExtractInputRef` verbatim, exactly like the C# property.
   */
  plan?: ExtractInputRef | null;
  /** The saved shot-plan project file, when `planSource` is `ProjectFile`. */
  planProjectFileId?: string | null;
  /**
   * Which step's `VideoAnalyze` artifact resolves a planned shot's `firstFrame`/`lastFrame` ids
   * against. OPTIONAL — only consulted when a planned shot actually names a keyframe. Same
   * `ExtractInputRef` reuse as `plan`.
   */
  analysis?: ExtractInputRef | null;
  model: string;
  resolution: string; // MiniMax-H3: '768P' | '2K'; MiniMax-H3-Max: '480P' | '768P'
  durationSeconds: number; // MiniMax-H3: 4-15; MiniMax-H3-Max: 5-15
  ratio: string;
  /** Inference provider GUID; null selects the default VideoGeneration-capable provider. */
  providerId: string | null;
  /** 1-4 inclusive (enforced by the backend's Validate()). */
  takes: number;
  maxClips: number;
  /** Step spend cap in platform CREDITS; must be > 0 (enforced by the backend's Validate()). */
  maxSpendCredits: number;
  allowSourceMediaEgress: boolean;
  pollIntervalSeconds: number;
  timeoutSeconds: number;
  /** Default `Reuse`: identical, already-delivered clips are served without paying again. */
  loopBackBehavior?: VideoGenerateLoopBackBehavior;
  /** Plan path only: append the anchor shot's MEASURED colour words to each prompt (first-party table, never a number). */
  lookMatching?: boolean;
  /** Plan path only: when the planner left `camera` blank, continue the anchor shot's measured move (Static/Handheld). */
  cameraContinuity?: boolean;
  /** GPU-minute cap for a self-hosted ComfyUI provider (bills $0). Exactly one of this or maxSpendCredits is meaningful per provider kind. */
  maxGpuMinutes?: number;
  /** Conform pass: frame-interpolate a lower-rate clip UP to this fps (0 = keep the clip's own rate; never downsamples). */
  conformFps?: number;
  /** Conform pass: upscale a smaller clip to this height (0 = keep; never downscales). */
  conformHeight?: number;
  /**
   * Inline mode only (T-105): the image sent as the clip's FIRST frame, taken from an earlier step whose output is
   * an image (an ExtractFrame step): `{from: 'Previous'}` or `{from: 'Step', stepOrder}`. Null (default) sends none.
   */
  firstFrame?: ExtractInputRef | null;
  /** The image sent as the LAST frame; same rules as `firstFrame`, and requires a first frame. */
  lastFrame?: ExtractInputRef | null;
  /** A project image file for the first frame instead of a step. Exclusive with `firstFrame`. */
  firstFrameProjectFileId?: string | null;
  /** A project image file for the last frame instead of a step. Exclusive with `lastFrame`. */
  lastFrameProjectFileId?: string | null;
  /**
   * Avatars phase 2: the project avatar (a consented likeness) every take shows. Inline only, never with a
   * first/last frame, and the step needs `allowSourceMediaEgress` (set only in the app). Null (default) shows none.
   */
  avatarId?: string | null;
  /** Optional subset of the avatar's reference roles to send; must keep a picture role. Null sends every role. */
  avatarReferenceRoles?: ('Face' | 'HalfBody' | 'FullBody' | 'Motion' | 'Speech' | 'Front' | 'Angle' | 'Detail')[] | null;
  /** Driving speech for models that declare audio drives speech (none yet: MiniMax is refused). */
  avatarAudio?: ExtractInputRef | null;
  /** A project audio file instead of a step for `avatarAudio`. */
  avatarAudioProjectFileId?: string | null;
}

/** Mirrors `FrameAt` in ExtractFrameStepConfig.cs — APPEND ONLY. */
export type FrameAt = 'Time' | 'Last' | 'First';

/**
 * Typed shape of `extractFrameConfigJson`. Mirrors `ExtractFrameStepConfig.cs` (T-105) field-for-field in camelCase.
 * `source` reuses `VideoSourceRef`; a frame comes from exactly ONE video (`GeneratedClips` is refused).
 */
export interface ExtractFrameStepConfig {
  source: VideoSourceRef;
  at: FrameAt;
  /** At=Time only: seconds from the start of the video (non-negative). */
  atSec?: number | null;
  /** `png` (default) or `jpeg`. */
  format: string;
  /** Longest side in pixels, 16..8192; null keeps the video's size. */
  maxLongSide?: number | null;
  /** Normalised crop rectangle (0..1). Not edited by the builder; round-tripped untouched. */
  crop?: { x: number; y: number; w: number; h: number } | null;
  /** List the image in the project's files (default true). */
  registerProjectFile: boolean;
}

/** Mirrors `AudioCleanupTarget` in AudioCleanupStepConfig.cs — APPEND ONLY. The first five run locally and free; the last two are hosted AI. */
export type AudioCleanupTarget = 'VoiceBasic' | 'Hum' | 'Wind' | 'Hiss' | 'Echo' | 'IsolateVoice' | 'RemoveMusic';

/** Mirrors `AudioCleanupOutputKind` in AudioCleanupStepConfig.cs — APPEND ONLY. */
export type AudioCleanupOutputKind = 'Auto' | 'Audio' | 'VideoWithCleanAudio';

/**
 * Typed shape of `audioCleanupConfigJson`. Mirrors `AudioCleanupStepConfig.cs` field-for-field in camelCase.
 * `source` reuses `VideoSourceRef` (a project file, a step output or a step result; `GeneratedClips` and `BriefFiles` are refused).
 */
export interface AudioCleanupStepConfig {
  target: AudioCleanupTarget;
  source: VideoSourceRef;
  /** IsolateVoice/RemoveMusic only: the AudioIsolation provider id; null uses the default. */
  providerId?: string | null;
  /** Hosted targets only: consent for the source's audio to leave this host. The assistant never sets it. */
  allowSourceMediaEgress: boolean;
  /** List the result in the project's files (default true). */
  registerProjectFile: boolean;
  outputKind: AudioCleanupOutputKind;
  /** Longest source for a hosted target, in minutes; can only lower the deployment's cap. */
  maxSourceMinutes?: number | null;
}

/** Mirrors `VideoEnhanceOp` in VideoEnhanceOp.cs — APPEND ONLY (`Extend` is the hosted-only outpaint op). */
export type VideoEnhanceOp = 'Upscale' | 'Interpolate' | 'Denoise' | 'MotionBlur' | 'Extend';

/** Mirrors `VideoEnhanceEngine` in VideoEnhanceStepConfig.cs — APPEND ONLY. `Local` is free; `Hosted` is metered AI and needs consent. */
export type VideoEnhanceEngine = 'Local' | 'Hosted';

/** Mirrors `VideoEnhanceDenoiseStrength` in VideoEnhanceStepConfig.cs — APPEND ONLY. */
export type VideoEnhanceDenoiseStrength = 'Light' | 'Strong';

/**
 * Typed shape of `videoEnhanceConfigJson`. Mirrors `VideoEnhanceStepConfig.cs` field-for-field in camelCase.
 * `source` reuses `VideoSourceRef` (a project file, a step output or a step result; `GeneratedClips` and `BriefFiles` are refused).
 */
export interface VideoEnhanceStepConfig {
  op: VideoEnhanceOp;
  engine: VideoEnhanceEngine;
  source: VideoSourceRef;
  /** Hosted only: the VideoUpscale / FrameInterpolation provider id; null uses the default. */
  providerId?: string | null;
  /** Hosted only: consent for the source's video file to leave this host. The assistant never sets it. */
  allowSourceMediaEgress: boolean;
  /** List the result in the project's files (default true). */
  registerProjectFile: boolean;
  /** Longest source in seconds; can only lower the deployment's cap (120 hosted, 600 local). */
  maxSourceSeconds?: number | null;
  /** Upscale: 2 or 4. Exactly one of `upscaleFactor` and `targetHeight`. */
  upscaleFactor?: number | null;
  /** Upscale: 720, 1080, 1440 or 2160 (never above the plan's output height). */
  targetHeight?: number | null;
  /** Interpolate: 2, 4 or 8 (hosted: 2 or 4); the output stays at or below 240 fps. */
  frameMultiplier?: number | null;
  /** Denoise only. */
  denoiseStrength?: VideoEnhanceDenoiseStrength | null;
  /** Denoise only: keep a light grain. */
  keepGrain: boolean;
  /** MotionBlur only: 90..360 degrees, default 180. */
  shutterAngle?: number | null;
  /**
   * Hosted only: the source's measured size and frame rate, written by the enhance route (never the customer or the
   * assistant) so the price is fixed at save. All three or none; width and height 1..16384, fps above 0 up to 240.
   */
  sourceWidth?: number | null;
  sourceHeight?: number | null;
  sourceFps?: number | null;
  /** Extend only: 16:9, 9:16, 1:1 or 4:5. Exactly one of `targetAspect` and the four margins. */
  targetAspect?: string | null;
  /** Extend only: fraction of the width added on the left, 0..0.5. */
  marginLeft?: number | null;
  /** Extend only: fraction of the width added on the right, 0..0.5. */
  marginRight?: number | null;
  /** Extend only: fraction of the height added on the top, 0..0.5. */
  marginTop?: number | null;
  /** Extend only: fraction of the height added on the bottom, 0..0.5. */
  marginBottom?: number | null;
  /** Extend only: what the new edges should show, at most 500 characters; blank uses a neutral default. */
  prompt?: string | null;
}

/**
 * How a ThumbnailGenerate step chooses the moment it turns into the image. Mirrors
 * `ThumbnailSelection` in ThumbnailGenerateStepConfig.cs — APPEND ONLY.
 *
 * `Auto` (the default) is the whole point of the step: the moment comes from the VideoAnalyze
 * artifact's own measured facts (take quality, sharpness, stillness, duplicate grouping), never
 * from a model and never from a caller-supplied timestamp. `AtTime` pins the step to one explicit
 * moment — the "I know the frame I want" escape hatch, and the only way to thumbnail a source the
 * analyze step never looked at.
 */
export type ThumbnailSelection = 'Auto' | 'AtTime';

/** Mirrors `ThumbnailFit` in ThumbnailGenerateStepConfig.cs — APPEND ONLY. */
export type ThumbnailFit = 'Cover' | 'Contain';

/** Mirrors `ThumbnailOverlayPosition` in ThumbnailGenerateStepConfig.cs — APPEND ONLY. */
export type ThumbnailOverlayPosition = 'None' | 'LowerThird' | 'UpperThird' | 'Center';

/**
 * Typed shape of `thumbnailGenerateConfigJson`. Mirrors `ThumbnailGenerateStepConfig.cs`
 * (ReelBolt.Shared/Workflows/) field-for-field in camelCase, with every enum value the C# member
 * name verbatim (JsonStringEnumConverter) — the same contract the other step configs follow.
 *
 * The C# record is POSITIONAL and every new member is APPENDED, so the property order below is
 * load-bearing documentation: stored configs are persisted JSON.
 */
export interface ThumbnailGenerateStepConfig {
  version: number;
  /**
   * Which VideoAnalyze step's full artifact supplies the candidate shots. REQUIRED — the whole
   * selection strategy is built on that artifact's measurements, and a step with nothing to select
   * from could only ever produce an arbitrary frame.
   */
  analysisStepOrder: number;
  /** Cross-execution override: a prior run's analysis artifact instead of this execution's, exactly as VideoCompile's. */
  analysisStepResultId: string | null;
  selection: ThumbnailSelection;
  /** Seconds on the selected source's own timeline. Ignored unless `selection` is `AtTime`. */
  atTimeSec: number;
  /** Which analysed source clip to read. Only meaningful with `selection` = `AtTime`; `Auto` picks the source as part of choosing the shot. */
  sourceIndex: number;
  /** Output width in pixels. Clamped 16..4096 at execution time. */
  width: number;
  /** Output height in pixels. Clamped 16..4096 at execution time. */
  height: number;
  fit: ThumbnailFit;
  /** ffmpeg `-q:v`, clamped 2..31 — 2 is best. */
  jpegQuality: number;
  /** Registered under this name; a bare file name, never a path. */
  outputFileName: string;
  /** Also lists the image in the project's Files (category outputFiles). The derivative row is written either way. */
  registerProjectFile: boolean;
  /** Caps how many shot candidates the selector scores. Clamped 1..500 at execution time. */
  maxCandidates: number;
  /** Shots shorter than this are not considered — a sub-half-second shot is usually a flash frame. */
  minShotSeconds: number;
  /** Prefer the best take of a duplicate group over a repeated attempt; off restores plain best-score selection. */
  preferBestTake: boolean;
  /**
   * Text burned into the image, or null for none. Workflow-author-supplied config (never model
   * output), truncated to `maxOverlayChars` at execution time.
   */
  overlayText: string | null;
  overlayPosition: ThumbnailOverlayPosition;
  /** Overlay font size as a percentage of the image height. Clamped 2..25 at execution time. */
  overlayFontSizePct: number;
  /** `drawtext` font colour — allowlisted at execution time, because it reaches ffmpeg's filter string. */
  overlayFontColor: string;
  /** `drawbox` colour behind the text, or `"none"` for no box — allowlisted exactly like VideoCompile's. */
  overlayBoxColor: string;
  /** Longest overlay text accepted, in characters. Clamped 1..400 at execution time. */
  maxOverlayChars: number;
}

/**
 * Structured output produced by the VideoStoryEditor agent. Mainly useful for typing the
 * execution-page decision viewer — the model produces this, there is no form for it.
 * THE RUSHCUT INVARIANT: no numeric/time-bearing field anywhere here — ids only.
 */
export interface VideoEditKeepSpan {
  fromId: string;
  toId: string;
  reason: string;
}

export interface VideoEditDecisionOutput {
  keep: VideoEditKeepSpan[];
  editRationale: string;
  suggestedTitle: string;
}

/**
 * `'NarrationPlan'` was appended LAST for Wave 1 phase 2: a NarrationPlan source reads its lines
 * from a `NarrationWriter` step's output instead of carrying them inline or pointing at a
 * Scriptwriter step.
 */
export type VoiceoverSourceKind = 'Inline' | 'ScriptScenes' | 'NarrationPlan' | 'PickupPlan';

export interface VoiceoverLine {
  text: string;
  startSec: number;
  voiceId?: string | null;
}

export interface VoiceoverSource {
  kind: VoiceoverSourceKind;
  lines?: VoiceoverLine[] | null;
  scriptwriterStepOrder?: number | null;
  /**
   * Appended for Wave 1 phase 2: the 1-based step order of the `NarrationWriter` step a
   * `kind: 'NarrationPlan'` source reads its lines from. Mirrors `scriptwriterStepOrder`.
   */
  narrationWriterStepOrder?: number | null;
  /** Appended for phase 4: the 1-based order of the `PickupPlanner` step a `kind: 'PickupPlan'` source reads its pickups from. */
  pickupPlannerStepOrder?: number | null;
}

/** Mirrors `PronunciationDictionaryMode` in VoiceoverStepConfig.cs — APPEND ONLY. */
export type PronunciationDictionaryMode = 'Off' | 'Auto';

/** Mirrors `VoiceCastingMode` in VoiceoverStepConfig.cs — APPEND ONLY. */
export type VoiceCastingMode = 'Off' | 'Decision';

export interface VoiceoverStepConfig {
  source: VoiceoverSource;
  providerId?: string | null;
  model?: string | null;
  defaultVoiceId?: string | null;
  maxBytes?: number;
  // -- Phase 3/4 (docs/voiceover.md). Every default is byte-identical to the phase-1 request. --
  /** `Auto` derives a pronunciation dictionary from the run's code-analysis outputs through a first-party phoneme table. */
  pronunciationDictionary?: PronunciationDictionaryMode;
  maxPronunciationEntries?: number;
  /** A consented project voice (Voices tab) every line speaks in; an unusable one fails the step rather than falling back. */
  projectVoiceId?: string | null;
  /** BCP-47 language tag of the narration (dubs) — provenance on the output. */
  language?: string | null;
  /** `Decision` asks the decision provider to cast one of the project's selectable voices. */
  voiceCasting?: VoiceCastingMode;
  /** Avatars phase 3: speak in the voice linked to the run brief's avatar (a brief voice still wins); none fails the step. */
  voiceFromAvatar?: boolean;
}

/**
 * Agent types whose output is an editorial/creative decision (translation, strategy,
 * scriptwriting, directing, authoring/composition, review, and every video-editorial agent)
 * rather than a plain fact extraction. Mirrors the backend's
 * `WorkflowTemplateCatalogCacheModePolicyTests.EditorialAgentTypes` — used here only to warn the
 * user in the builder UI that caching one of these steps risks silently replaying a stale
 * decision; the backend template definitions are what actually enforce `CacheMode: Never` for
 * built-in workflows.
 */
const EDITORIAL_AGENT_TYPES: ReadonlySet<string> = new Set([
  'RemotionComponentTranslator',
  'AnimationStrategyAgent',
  'ScriptwriterAgent',
  'DirectorAgent',
  'AuthorAgent',
  'ReviewAgent',
  'VideoStoryEditor',
  'MotionGraphicsPlanner',
  'MusicSupervisor',
  'SoundDesigner',
  'VideoReviewAgent',
  'Colorist',
]);

export function isEditorialAgentType(agentType: string | null | undefined): boolean {
  return !!agentType && EDITORIAL_AGENT_TYPES.has(agentType);
}

/**
 * Decision-model step configuration (decision-engine phase 2). The web schemas in
 * `lib/schemas/workflow-step-configs.ts` are the second description of these same shapes, and their
 * `default`s are cross-checked against the `createDefault*` factories in the matching editor module.
 *
 * Every property name below is the camelCase JSON name of the C# record property
 * (`JsonNamingPolicy.CamelCase` + `JsonStringEnumConverter`, see `StepConfigSchemaExporter.cs`), and
 * every enum value is the C# member name verbatim — the same contract `ExtractStepConfig` and the
 * video step configs above already follow.
 */

/**
 * How a `Conditional` step decides which branch to take. Mirrors `ConditionMode` in
 * `inference/src/ReelBolt.Shared/Workflows/ConditionalStepConfig.cs`. `Expression` is today's
 * behaviour and the default everywhere: a `Conditional` step with no config at all is an
 * `Expression` step.
 */
export type ConditionMode = 'Expression' | 'Decision';

/**
 * Mirrors `ConditionalStepConfig.cs` field-for-field. Serialized to
 * `WorkflowStep.conditionalConfigJson`; nullable there, and null means "no config" =
 * `{ mode: 'Expression' }`.
 */
export interface ConditionalStepConfig {
  version: number;
  mode: ConditionMode;
  /** The `Decision` question's name; null derives one from `conditionExpression`. */
  decisionQuestion: string | null;
  /** Yes-probability compared against when `conditionExpression` is blank. Ignored in `Expression` mode. */
  threshold: number;
  /** Decision provider GUID; null resolves the default `Capability = Decision` row. */
  providerId: string | null;
  timeoutSeconds: number;
}

/**
 * Mirrors `ReviewLoopStepConfig.cs` field-for-field. Serialized to
 * `WorkflowStep.reviewLoopConfigJson`; `enabled` is `false` by default and every shipped
 * configuration keeps it that way, so a disabled gate is byte-identically today's reviewer path.
 */
export interface ReviewLoopStepConfig {
  version: number;
  enabled: boolean;
  /** Minimum `p(level >= minScore)` for an early stop. */
  acceptAt: number;
  /** Minimum gap between the top two rubric levels; narrower escalates to the reviewer. */
  bimodalMargin: number;
  /** Decision provider GUID; null resolves the default `Capability = Decision` row. */
  providerId: string | null;
  timeoutSeconds: number;
  /** The frame-sampling visual check run before the reviewer each iteration. Null/absent or `enabled: false` is off. */
  visualCheck?: VisualCheckConfig | null;
}

/** Mirrors `VisualCheckConfig` (VisualCheckConfig.cs). Defaults: off, 8 frames, 5 %, 2.5 %, cap 6. */
export interface VisualCheckConfig {
  enabled: boolean;
  /** 2..16. */
  frames: number;
  /** Frame-edge safe margin, percent of the frame (0..20). */
  safeMarginPercent: number;
  /** Smallest legible on-screen text height, percent of the frame (0.5..20). */
  minTextHeightPercent: number;
  /** Score ceiling applied when a high-severity finding exists (0..10). */
  failScoreCap: number;
  /** Vision provider id; null uses the default Vision row. */
  visionProviderId?: string | null;
}

/**
 * How a room step uses the convening gate. Mirrors `ConveneGateMode` in
 * `inference/src/ReelBolt.Shared/Workflows/RoomStepConfig.cs`: `Off`/`Shadow`/`Gate` mean the same
 * three things they mean on `DecisionGateMode`. `Off` is the default on all three room records.
 */
export type ConveneGateMode = 'Off' | 'Shadow' | 'Gate';

/**
 * The convening-gate members every room config record exposes (on `IRoomStepConfig` and on
 * `EditRoomStepConfig`/`GraphicsRoomStepConfig`/`ColorGradeRoomStepConfig`) — one shape covering all
 * three, exactly as the backend interface does. Only read when `conveneGate` is not `Off`.
 *
 * NOTE (media decision 7): the gate is implemented for the **ColorGrade room only**. EditRoom and
 * GraphicsRoom still ACCEPT these fields — so enabling them there is a no-op rather than an error —
 * because neither `VideoEditDecisionOutput` (an id-anchored keep/drop list) nor
 * `MotionGraphicsPlanOutput` (an open, generative overlay list) decomposes into the fixed option set
 * a System One question can answer.
 */
export interface RoomConveneConfig {
  conveneGate: ConveneGateMode;
  conveneAcceptAt: number;
  conveneMinMargin: number;
  /** Decision provider GUID; null resolves the default `Capability = Decision` row, or none — the normal local case. */
  conveneProviderId: string | null;
}

export function getDefaultAgentInputContextMode(agentType: string | null | undefined): AgentInputContextMode {
  switch (agentType) {
    case 'CodeStructureAnalyzer':
    case 'RemotionComponentTranslator':
    case 'DirectorAgent':
      return 'FullWorkflow';
    case 'DependencyAnalyzer':
    case 'ComponentInventoryAnalyzer':
    case 'RouteAndApiAnalyzer':
    case 'StyleAndThemeExtractor':
    case 'AnimationStrategyAgent':
    case 'ScriptwriterAgent':
    case 'AuthorAgent':
    case 'ReviewAgent':
      return 'PreviousStepOnly';
    default:
      return 'FullWorkflow';
  }
}

/** Mirrors C# `VideoOutputFormat` (append-only, persisted by name). */
export type VideoOutputFormat = 'Source' | 'Vertical' | 'Square' | 'Landscape' | 'Portrait';
/** Mirrors C# `VideoOutputFit` (append-only, persisted by name). */
export type VideoOutputFit = 'Crop' | 'Pad' | 'Subject';

// ─────────────────────────────────────────────────────────────────────────────
// TimelineRender (editor v2, P4) — mirrors TimelineRenderStepConfig.cs
// ─────────────────────────────────────────────────────────────────────────────

/** Mirrors `TimelineRenderPreset` in TimelineRenderStepConfig.cs — APPEND ONLY. */
export type TimelineRenderPreset = 'Hd1080p16x9' | 'Uhd4k16x9' | 'Vertical9x16' | 'Square1x1' | 'Portrait4x5' | 'Master' | 'AudioOnly';

/** Mirrors `TimelineRenderVideoCodec` — APPEND ONLY. */
export type TimelineRenderVideoCodec = 'H264' | 'H265';

/** Mirrors `TimelineRenderEncoderSpeed` — APPEND ONLY. */
export type TimelineRenderEncoderSpeed = 'Fast' | 'Medium' | 'Slow';

/** Mirrors `TimelineRenderRateControl` (by name) — APPEND ONLY. */
export type TimelineRenderRateControl = 'Quality' | 'Capped' | 'Average';

/** Mirrors `TimelineRenderRange`: timeline frames, `[inFrame, outFrame)`. */
export interface TimelineRenderRange {
  inFrame: number;
  outFrame: number;
}

/** Mirrors `TimelineRenderEncodeSettings`. Every value is clamped by the server's `Validate()`. */
export interface TimelineRenderEncodeSettings {
  /** Even, 16..7680; null keeps the preset's. Supplied together with `height` or not at all. */
  width: number | null;
  /** Even, 16..4320; null keeps the preset's. */
  height: number | null;
  codec: TimelineRenderVideoCodec;
  /** 0..51, lower is better quality. */
  crf: number;
  speed: TimelineRenderEncoderSpeed;
  /** 64..320. */
  audioBitrateKbps: number;
  /** Omitted or `Quality` is the plain quality encode; `Capped` and `Average` need `videoBitrateKbps`. */
  rateControl?: TimelineRenderRateControl;
  /** 200..100000; only with `Capped` or `Average`. */
  videoBitrateKbps?: number | null;
}

/** Mirrors `TimelineRenderLoudness` (two-pass loudnorm targets). */
export interface TimelineRenderLoudness {
  /** -36..-5. */
  targetLufs: number;
  /** -9..0. */
  truePeakDbtp: number;
  /** 1..30. */
  loudnessRange: number;
}

/**
 * Typed shape of `timelineRenderConfigJson`. Mirrors `TimelineRenderStepConfig.cs`
 * (ReelBolt.Shared/Workflows/) field-for-field in camelCase, enum values the C# member names
 * verbatim. The C# record is POSITIONAL and every new member is APPENDED, so the property order
 * below is load-bearing documentation: stored configs are persisted JSON.
 */
export interface TimelineRenderStepConfig {
  /** The editor timeline to render. Required; an empty string is a save-time rejection. */
  editTimelineId: string;
  version: number;
  /** The revision to render, or null for the latest when the step starts (the run pins what it used). */
  revision: number | null;
  /** The part of the timeline to render, or null for all of it. */
  range: TimelineRenderRange | null;
  preset: TimelineRenderPreset;
  /** Explicit encoder refinements over the preset, or null for the preset's own. */
  encode: TimelineRenderEncodeSettings | null;
  /** A bare file name, never a path. */
  outputFileName: string;
  /** Also list the render in the project's Files. */
  registerProjectFile: boolean;
  /** Loudness normalisation targets, or null to measure only. */
  loudness: TimelineRenderLoudness | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// DesignSystem (brand and product profile) — mirrors DesignSystemStepConfig.cs
// ─────────────────────────────────────────────────────────────────────────────

/** Mirrors `DesignSystemOp` in DesignSystemStepConfig.cs — APPEND ONLY. */
export type DesignSystemOp = 'Parse' | 'Compose' | 'Require';

/** Mirrors `DesignSystemParseOptions`. Every numeric bound is enforced by the server's `Validate()`. */
export interface DesignSystemParseOptions {
  includeDesign: boolean;
  includeProductMap: boolean;
  /** 1..5000. */
  maxFiles: number;
  /** 1024..1048576. */
  maxBytesPerFile: number;
  /** 1048576..67108864. */
  maxTotalBytes: number;
  /** At most 20 entries, each 1..100 characters of `[A-Za-z0-9_./*-]`. */
  extraGlobs?: string[] | null;
}

/** Mirrors `DesignSystemComposeOptions`. */
export interface DesignSystemComposeOptions {
  /** 1-based order of the earlier Parse step whose artifact is composed. Must be at least 1. */
  parseStepOrder: number;
  /** StyleAndThemeExtractor step orders used as the LLM design fallback, or null for none. */
  designFallbackStepOrders?: number[] | null;
  /** The four analyzer step orders used as the product-map fallback, or null for none. */
  productFallbackStepOrders?: number[] | null;
  /** Save the composed profile for this project. */
  ingest: boolean;
}

/** Mirrors `DesignSystemRequireOptions`. */
export interface DesignSystemRequireOptions {
  requireDesign: boolean;
  requireProductMap: boolean;
}

/**
 * Typed shape of `designSystemConfigJson`. Mirrors `DesignSystemStepConfig.cs`
 * (ReelBolt.Shared/Workflows/) field-for-field in camelCase, enum values the C# member names
 * verbatim. Exactly the options object matching `op` is present; the other two are null/absent.
 */
export interface DesignSystemStepConfig {
  op: DesignSystemOp;
  /** 1 is the only version this build writes. */
  version: number;
  parse?: DesignSystemParseOptions | null;
  compose?: DesignSystemComposeOptions | null;
  require?: DesignSystemRequireOptions | null;
}


// ─────────────────────────────────────────────────────────────────────────────
// AudioGenerate (music and sound effects, T-108) — mirrors AudioGenerateStepConfig.cs
// ─────────────────────────────────────────────────────────────────────────────

/** Mirrors `AudioGenerationKind` in IAudioGenerationClient.cs — APPEND ONLY. */
export type AudioGenerationKind = 'Music' | 'SoundEffect';

/**
 * Typed shape of `audioGenerateConfigJson`. Mirrors `AudioGenerateStepConfig.cs` (ReelBolt.Shared/Workflows/)
 * field-for-field in camelCase. Every bound is the server's `Validate()` (music 3-300 s, sound effects 0.5-30 s, the
 * chosen model's own range at run time; prompt at most 4000 characters; 1-4 takes).
 */
export interface AudioGenerateStepConfig {
  kind: AudioGenerationKind;
  /** Empty when `compositionPlanJson` is set. */
  prompt: string;
  /** Seconds per take; 0 with a composition plan. */
  durationSec: number;
  /** Music, prompt mode only. */
  instrumental?: boolean | null;
  /** Music only: the vendor's composition plan JSON. */
  compositionPlanJson?: string | null;
  /** Sound effects only, where the model supports it. */
  loop?: boolean | null;
  /** Sound effects only, 0..1, where the model supports it. */
  promptInfluence?: number | null;
  /** Provider row id; null uses the kind's default row. */
  providerId?: string | null;
  /** Model or endpoint id; null uses the row's model, else the catalogue default. */
  model?: string | null;
  /** 1..4. */
  takes?: number;
  /** Step spend limit in credits; null leaves only the user's own daily video limit (if any) and the balance. */
  maxSpendCredits?: number | null;
  /** Register each take as an audio project file (default true). */
  registerProjectFile?: boolean;
  /** 1-40 of `[A-Za-z0-9_-]`; null uses `music` / `sfx`. */
  fileNamePrefix?: string | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// ImageGenerate (text to image / instruction edit) — mirrors ImageGenerateStepConfig.cs
// ─────────────────────────────────────────────────────────────────────────────

/** Mirrors `ImageGenerateMode` in ImageGenerateStepConfig.cs (serialized by name). */
export type ImageGenerateMode = 'Generate' | 'Edit';

/** Generation tier of an ImageGenerate step; null means automatic. */
export type ImageGenerateResolution = '1K' | '2K' | '4K';

/**
 * Typed shape of `imageGenerateConfigJson`. Mirrors `ImageGenerateStepConfig.cs` field-for-field in camelCase.
 * Bounds are the server's `Validate()`: prompt/instruction at most 4000 characters, 1-4 variations (Generate and Edit).
 */
export interface ImageGenerateStepConfig {
  mode: ImageGenerateMode;
  /** Generate only: what to draw. */
  prompt?: string | null;
  /** Edit only: what to change in the source picture. */
  instruction?: string | null;
  /** Edit only: the one picture to change. */
  source?: VideoSourceRef | null;
  /** 1..4 pictures, for Generate and Edit alike. */
  variations?: number;
  /** Step spend limit in credits; null limits it to the priced total. */
  maxSpendCredits?: number | null;
  /** Provider row id; null uses the default ImageGeneration / ImageEdit row. */
  providerId?: string | null;
  aspectRatio?: string | null;
  width?: number | null;
  height?: number | null;
  /** Generation tier (long edge 1K=1024, 2K=2048, 4K=4096); null is automatic. Generate and Edit. */
  resolution?: ImageGenerateResolution | null;
  /** Exact final picture width in pixels, 64..8192; set together with outputHeight. Not with width/height/aspectRatio. */
  outputWidth?: number | null;
  /** Exact final picture height in pixels, 64..8192; set together with outputWidth. */
  outputHeight?: number | null;
  /** Register each picture as an image project file (default true). */
  registerProjectFile?: boolean;
  /** Edit only: consent to upload the source picture to the hosted model. */
  allowSourceMediaEgress?: boolean;
  /** 1-40 of `[A-Za-z0-9_-]`; null uses `image`. */
  fileNamePrefix?: string | null;
  /** Batch: one item per line or comma separated, at most 24. `{item}` in the prompt or instruction is replaced per item. */
  itemList?: string | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// PinInsert (the deterministic pinning step, T-206) — mirrors PinInsertStepConfig.cs
// ─────────────────────────────────────────────────────────────────────────────

/** One pin: content on one tracked screen (`r{n}`), or on every offered screen when `regionId` is null. */
export interface PinInsertEntry {
  regionId?: string | null;
  content: InsertContentRef;
}

/**
 * Typed shape of `pinInsertConfigJson`. Mirrors `PinInsertStepConfig.cs` field-for-field in camelCase. The server's
 * `Validate()` is the authority (1-16 pins, `r{n}` region ids, at most one pin without a region).
 */
export interface PinInsertStepConfig {
  /** The VideoAnalyze step whose tracked screens are pinned: `{from: 'Step', stepOrder}` or `{from: 'Previous'}`. */
  analysis: ExtractInputRef;
  pins: PinInsertEntry[];
  /** True (default): fail when a pin does not resolve; false: skip it and report it. */
  requireApplied?: boolean;
}

// --------------------------------------------------------------------------- run brief (docs/run-brief.md)

export type RunBriefVoiceMode = 'None' | 'Narrated';
export type RunBriefFileRole = 'Video' | 'Code' | 'Music' | 'Sfx' | 'Image' | 'Document' | 'InsertContent' | 'Overlay';

/** `voice`: a voice id needs `mode: 'Narrated'`, and only one id may be set. */
export interface RunBriefVoice {
  mode?: RunBriefVoiceMode | null;
  projectVoiceId?: string | null;
  libraryVoiceId?: string | null;
}

export interface RunBriefSourceFiles {
  role?: RunBriefFileRole | null;
  fileIds?: string[] | null;
}

/** The run brief, mirrored 1:1 (every field optional; absent = no preference). */
export interface RunBrief {
  language?: string | null;
  sourceLanguage?: string | null;
  /** Whole seconds, 5 to 600. */
  lengthSec?: number | null;
  format?: string | null;
  resolution?: string | null;
  stylePreset?: string | null;
  styleId?: string | null;
  style?: StyleConfig | null;
  tone?: StyleTone | null;
  /** `project` (default) or `none`. */
  designSystem?: string | null;
  voice?: RunBriefVoice | null;
  sourceFiles?: RunBriefSourceFiles[] | null;
  notes?: string | null;
  /** Under a full-screen picture: let the planner choose, keep the film's sound, or mute it. Absent = `agent`. */
  overlayCardAudio?: 'agent' | 'keep' | 'mute' | null;
  /**
   * Avatars phase 2: the project avatar a VideoGenerate step that names none shows in this run. Never turns on the
   * step's permission to send media (given only in the app).
   */
  avatar?: { avatarId?: string | null } | null;
  /**
   * App inputs (docs/design/app-inputs.md 6.1): values of the app's `input` inputs by id. Text, a number or a boolean
   * (a model is its capability id). Checked against the app's manifest on the server.
   */
  inputs?: Record<string, string | number | boolean | null> | null;
  /** How busy the dynamic edit is; overrides the style's dynamics. Absent = the style's (or none). */
  pace?: 'calm' | 'medium' | 'fast' | null;
}

/** One app field a past run was started with, as the server describes it (docs/design/app-inputs.md 10.4). */
export interface RunAppInput {
  id: string;
  label: string;
  labelKey?: string | null;
  type: AppInputType;
  /** The value as sent; absent for a model (its value is an id). */
  value?: string | number | boolean | null;
  /** The value in English words (the fallback when no catalogue key applies). */
  valueText: string;
  valueLabelKey?: string | null;
  onLabel?: string | null;
  offLabel?: string | null;
  unit?: string | null;
  /** A chosen model's name; absent when the workspace cannot see it. */
  modelName?: string | null;
  /** For the permission to send media: who allowed it (`app`, `api_key`, `mcp`, `assistant_card`). */
  allowedBy?: string | null;
}

// --------------------------------------------------------------------------- workflow apps (docs/workflow-apps.md)

export type AppCategory = 'promo' | 'footage' | 'brand' | 'utility';
export type AppResultKind = 'video' | 'image' | 'audio' | 'files';
export type AppFileRole = 'video' | 'code' | 'music' | 'sfx' | 'image' | 'document' | 'insertContent' | 'overlay';
export type AppInputKind = 'brief' | 'files' | 'notes' | 'input';
/** The type of an `input` input (docs/design/app-inputs.md 4.2). Append only. */
export type AppInputType = 'shortText' | 'longText' | 'number' | 'duration' | 'choice' | 'toggle' | 'shape' | 'model';

/** One option of a `choice` input: the value bound into the step and its plain label. */
export interface AppInputOption {
  value: string | number;
  label?: string | null;
  hint?: string | null;
  labelKey?: string | null;
}

/** Display only: the input is shown while the `toggle` or `choice` input `input` has the value `equals`. */
export interface AppInputShowWhen {
  input: string;
  equals: string | number | boolean;
}
/** Words, exactly as the API sends them. */
export type AppCostClass = 'No credits' | 'Uses credits' | 'Uses credits and a voice' | 'Uses credits and AI shots';

export interface AppManifestInput {
  kind: AppInputKind;
  /** For `brief`: a run-brief field name. */
  field?: string | null;
  /** For `files`: the project-file role. */
  role?: AppFileRole | null;
  label?: string | null;
  default?: string | number | boolean | null;
  choices?: Array<string | number | boolean> | null;
  min?: number | null;
  max?: number | null;
  // ---- manifest version 2 (docs/design/app-inputs.md 4.1) ----
  /** For `input`: the id placeholders and bindings name (`{{input.id}}`). */
  id?: string | null;
  type?: AppInputType | null;
  help?: string | null;
  /** Shown as placeholder text, never sent. */
  example?: string | null;
  required?: boolean | null;
  maxLength?: number | null;
  step?: number | null;
  unit?: 'seconds' | 'takes' | 'percent' | null;
  options?: AppInputOption[] | null;
  /**
   * Every kind: true = under "More options". Absent = the default for the kind and type (models and fine-tuning under
   * "More options", essentials shown); the app creator decides.
   */
  more?: boolean | null;
  showWhen?: AppInputShowWhen | null;
  /** For `model`: the capability, the job filter, the author's allowed list and recommendation, or the agent it sets. */
  capability?: string | null;
  job?: string | null;
  allowed?: string[] | null;
  recommended?: string | null;
  agent?: string | null;
  onLabel?: string | null;
  offLabel?: string | null;
  labelKey?: string | null;
  helpKey?: string | null;
  exampleKey?: string | null;
  /**
   * `mediaEgress` on the one field the app adds itself: the permission to send the person's media to AI services for a
   * run. Derived on the server from the steps, never stored, and refused when written by hand.
   */
  permission?: string | null;
}

export interface AppManifest {
  version: number;
  title?: string | null;
  tagline?: string | null;
  category?: AppCategory | null;
  suggestedStylePreset?: string | null;
  inputs?: AppManifestInput[] | null;
  result?: { kind: AppResultKind } | null;
  advanced?: { allowGraph: boolean } | null;
  /** Version 2: the request agents read, with `{{input.id}}` placeholders, composed on the server. */
  requestTemplate?: string | null;
}

/** `GET .../workflows/{id}/app`: the effective manifest of one workflow. */
export interface WorkflowAppResponse {
  workflowId: string;
  name: string;
  manifest: AppManifest;
  hasExplicitManifest: boolean;
  costClass: AppCostClass;
  /** A template key, or `custom`. */
  source: string;
  /**
   * The settings an app field may fill, per step type that has any (`StepCatalog.BindableFieldsOf`,
   * docs/design/app-inputs.md 7 and 24). Absent when the step types have none.
   */
  bindableFields?: Record<string, AppBindableField[]> | null;
  /** The permission to send media, when a step sends something: what the page needs to say it in words. */
  mediaEgress?: AppMediaEgress | null;
}

/** Why a step sends media (closed codes, the WL015 variants plus `plannedFrames` and `plannedFramesAndSubjects`). */
export type MediaEgressReason =
  | 'tracksAnObjectByName'
  | 'startsFromFrame'
  | 'showsAnAvatar'
  | 'plannedFrames'
  | 'plannedFramesAndSubjects'
  | 'enhancesVideoWithAi'
  | 'cleansAudioWithAi';

export interface AppMediaEgressStep {
  stepOrder: number;
  stepName: string;
  stepType: string;
  reason: MediaEgressReason | string;
  need: 'required' | 'optional';
  /** The step's own saved switch is already on. */
  allowedInStep: boolean;
}

export interface AppMediaEgress {
  inputId: string;
  /** A run with the page's default choices cannot start without the permission. */
  required: boolean;
  /** A field of the page picks a branch that changes whether it is needed: follow the pre-run check for the current choices. */
  dependsOnChoices?: boolean;
  /** Every step that sends something already allows it in its own settings: the field changes nothing. */
  alreadyAllowed: boolean;
  steps: AppMediaEgressStep[];
}

/** One setting an app field may fill (`StepCardBindableField`): a text setting takes a placeholder, the rest a whole value. */
export interface AppBindableField {
  /** camelCase JSON name; nested text written `trackObjects[].label`; `@model` is the step's model. */
  path: string;
  mode: 'placeholder' | 'value';
  /** The input types it accepts, first = the natural one. */
  types: AppInputType[];
  label: string;
  min?: number | null;
  max?: number | null;
  maxLength?: number | null;
  choices?: string[] | null;
  /** The brief field that decides this setting when the page shows it. */
  governedByBriefField?: string | null;
  /** For `@model` / `@stillModel`: the model kinds it takes. */
  capabilities?: string[] | null;
  /** The JSON kind the setting holds: what a switch or a choice may list for it (docs/design/app-inputs.md 27). */
  valueKind?: 'text' | 'number' | 'boolean';
}

export interface WorkflowAppLastRun {
  status: WorkflowExecution['status'];
  finishedAt: string | null;
}

export interface WorkflowAppCard {
  workflowId: string;
  title: string;
  tagline: string | null;
  category: AppCategory;
  costClass: AppCostClass;
  inputsCount: number;
  lastRun: WorkflowAppLastRun | null;
  source: string;
}

/**
 * What fixes a recipe's missing thing. `upgrade-plan` is a feature the workspace's plan does not
 * include (`RecipeFit.PlanNeeds` on the API): the fix is the plans page.
 */
export type RecipeNeedAction = 'add-files' | 'build-profile' | 'connect-provider' | 'upgrade-plan';

export interface WorkflowAppMissing {
  need: string;
  message: string;
  action: RecipeNeedAction;
}

export interface WorkflowRecipeCard {
  key: string;
  title: string;
  tagline: string | null;
  category: AppCategory;
  costClass: AppCostClass;
  inputsCount: number;
  fit: 'ready' | 'needs';
  missing: WorkflowAppMissing[];
  /** Model fields of the page the recipe would be created with (0, or absent, with app inputs off). */
  modelInputs?: number;
}

/** `GET .../workflow-apps`: the whole gallery in one response. */
export interface WorkflowAppsResponse {
  apps: WorkflowAppCard[];
  recipes: WorkflowRecipeCard[];
}

// --------------------------------------------------------------------------- workflow linter (docs/workflow-linter.md)

export type LintSeverity = 'error' | 'warning' | 'info';

export interface LintFix {
  kind: string;
  label: string;
  patchId?: string | null;
  /** Catalogue key under `lint.fix.` (not including the prefix), when the fix was built by a rule. */
  labelId?: string | null;
  /** Tokens the fix label template takes. */
  args?: Record<string, string> | null;
}

export interface LintFinding {
  ruleId: string;
  severity: LintSeverity;
  stepOrder?: number | null;
  message: string;
  /** Catalogue key under `lint.` (not including the prefix); absent on older payloads and server sentences. */
  messageKey?: string | null;
  /** Tokens the message template takes (digits, user labels, enum names), never prose. */
  args?: Record<string, string> | null;
  fix?: LintFix | null;
}

export interface WorkflowLintResponse {
  findings: LintFinding[];
  errors: number;
  warnings: number;
  infos: number;
  /** Videos the run would refuse for size or length; absent from an older server. */
  sourceProblems?: {
    code: string;
    message?: string;
    fileName?: string | null;
    limitGb?: number | null;
    limitMinutes?: number | null;
    fileGb?: number | null;
    fileMinutes?: number | null;
  }[];
}

export interface LintWorkflowRequest {
  brief?: RunBrief;
  steps?: unknown[];
  appManifest?: AppManifest;
}
