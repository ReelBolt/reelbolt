import { stepRoleLabel } from '@/lib/utils/agent-roles';
import type { StepData } from '@/components/workflows/WorkflowStepList';
import type { WorkflowDefinition, CreateWorkflowStepRequest, WorkflowExecution } from '@/lib/types/workflow';
import type { AgentDefinition } from '@/lib/types/agent';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import type { VideoAnalyzeStepConfig as VideoAnalyzeStepConfigValue, ExtractFrameStepConfig as ExtractFrameStepConfigValue, AudioCleanupStepConfig as AudioCleanupStepConfigValue, VideoEnhanceStepConfig as VideoEnhanceStepConfigValue } from '@/lib/types/workflow';
import { parseContinueComposition, stepOptionsToRequest } from '@/lib/utils/step-options';
import { parseConfigJson, camelizeConfigJsonString } from '@/lib/utils/config-case';

function normalizeVideoSourceKind(kind: unknown): string {
  if (kind === 'ProjectFile' || kind === 'StepOutput' || kind === 'PreviousStepOutput' || kind === 'GeneratedClips' || kind === 'StepResult') return kind;
  const byIndex = ['ProjectFile', 'StepOutput', 'PreviousStepOutput', 'GeneratedClips', 'StepResult'] as const;
  return byIndex[kind as number] ?? 'PreviousStepOutput';
}

// The backend serializes VideoSourceKind as an integer enum (ProjectFile=0, StepOutput=1,
// PreviousStepOutput=2), while the web types use the string form. Normalize at the load boundary so
// the config editors render and save the string form consistently.
function normalizeVideoAnalyzeConfig(raw: string): VideoAnalyzeStepConfigValue {
  const config = parseConfigJson<Record<string, unknown>>(raw) ?? {};
  const normRef = (ref: { kind?: unknown }) => (ref ? { ...ref, kind: normalizeVideoSourceKind(ref.kind) } : ref);
  if (config?.source) config.source = normRef(config.source as { kind?: unknown });
  if (Array.isArray(config?.sources)) config.sources = (config.sources as { kind?: unknown }[]).map(normRef);
  return config as unknown as VideoAnalyzeStepConfigValue;
}

// Same normalization for an ExtractFrame config, whose single `source` may have been stored with the integer enum.
function normalizeExtractFrameConfig(raw: string | null | undefined): ExtractFrameStepConfigValue | null {
  const config = parseConfigJson<Record<string, unknown>>(raw);
  if (!config) return null;
  if (config.source) config.source = { ...(config.source as object), kind: normalizeVideoSourceKind((config.source as { kind?: unknown }).kind) };
  return config as unknown as ExtractFrameStepConfigValue;
}

// Same normalization for an AudioCleanup config, whose single `source` may have been stored with the integer enum.
function normalizeAudioCleanupConfig(raw: string | null | undefined): AudioCleanupStepConfigValue | null {
  const config = parseConfigJson<Record<string, unknown>>(raw);
  if (!config) return null;
  if (config.source) config.source = { ...(config.source as object), kind: normalizeVideoSourceKind((config.source as { kind?: unknown }).kind) };
  return config as unknown as AudioCleanupStepConfigValue;
}

// Same normalization for a VideoEnhance config, whose single `source` may have been stored with the integer enum.
function normalizeVideoEnhanceConfig(raw: string | null | undefined): VideoEnhanceStepConfigValue | null {
  const config = parseConfigJson<Record<string, unknown>>(raw);
  if (!config) return null;
  if (config.source) config.source = { ...(config.source as object), kind: normalizeVideoSourceKind((config.source as { kind?: unknown }).kind) };
  return config as unknown as VideoEnhanceStepConfigValue;
}

/**
 * Pure API→builder mapping for one workflow's steps. Exported so the round-trip
 * (`conditionalConfigJson`/`reviewLoopConfigJson` → parsed config → back to JSON) is pinned by a
 * test without standing up the whole page.
 */
export function mapWorkflowStepsToStepData(workflow: WorkflowDefinition): StepData[] {
  return workflow.steps
    .sort((a, b) => a.stepOrder - b.stepOrder)
    .map((s) => ({
      id: s.id,
      label: s.label,
      agentDefinitionId: s.agentDefinitionId,
      stepType: s.stepType || 'Agent',
      conditionExpression: s.conditionExpression ?? null,
      loopSourceExpression: s.loopSourceExpression ?? null,
      loopTargetStepOrder: s.loopTargetStepOrder ?? null,
      maxIterations: s.maxIterations ?? 3,
      minScore: s.minScore ?? null,
      inputMappingJson: s.inputMappingJson ?? null,
      agentInputContextMode: s.agentInputContextMode ?? null,
      selectedPriorStepOrders: s.selectedPriorStepOrdersJson ? JSON.parse(s.selectedPriorStepOrdersJson) : [],
      trueBranchStepOrder: s.trueBranchStepOrder ?? null,
      falseBranchStepOrder: s.falseBranchStepOrder ?? null,
      parallelAgentIds: s.parallelAgentIdsJson ? JSON.parse(s.parallelAgentIdsJson) : [],
      extractConfig: parseConfigJson(s.extractConfigJson),
      videoAnalyzeConfig: s.videoAnalyzeConfigJson ? normalizeVideoAnalyzeConfig(s.videoAnalyzeConfigJson) : null,
      videoCompileConfig: parseConfigJson(s.videoCompileConfigJson),
      videoGenerateConfig: parseConfigJson(s.videoGenerateConfigJson),
      voiceoverConfig: parseConfigJson(s.voiceoverConfigJson),
      thumbnailGenerateConfig: parseConfigJson(s.thumbnailGenerateConfigJson),
      timelineRenderConfig: parseConfigJson(s.timelineRenderConfigJson),
      designSystemConfig: parseConfigJson(s.designSystemConfigJson),
      extractFrameConfig: normalizeExtractFrameConfig(s.extractFrameConfigJson),
      audioGenerateConfig: parseConfigJson(s.audioGenerateConfigJson),
      imageGenerateConfig: parseConfigJson(s.imageGenerateConfigJson),
      audioCleanupConfig: normalizeAudioCleanupConfig(s.audioCleanupConfigJson),
      videoEnhanceConfig: normalizeVideoEnhanceConfig(s.videoEnhanceConfigJson),
      pinInsertConfig: parseConfigJson(s.pinInsertConfigJson),
      continueComposition: parseContinueComposition(s.continueCompositionJson),
      registerOutputAsProjectFile: s.registerOutputAsProjectFile ?? false,
      conditionalConfig: parseConfigJson(s.conditionalConfigJson),
      reviewLoopConfig: parseConfigJson(s.reviewLoopConfigJson),
      editRoomConfigJson: camelizeConfigJsonString(s.editRoomConfigJson),
      graphicsRoomConfigJson: camelizeConfigJsonString(s.graphicsRoomConfigJson),
      colorGradeRoomConfigJson: camelizeConfigJsonString(s.colorGradeRoomConfigJson),
      dailiesRoomConfigJson: camelizeConfigJsonString(s.dailiesRoomConfigJson),
      cacheMode: s.cacheMode ?? null,
    }));
}

/**
 * One plain-language line per step for the run confirmation: the step's own name when it has one,
 * plus what it does ("Watches your footage"), never an internal step type or agent enum.
 */
export function describeStepsForRun(workflow: WorkflowDefinition, agents: AgentDefinition[] | undefined): string[] {
  return [...workflow.steps]
    .sort((a, b) => a.stepOrder - b.stepOrder)
    .map((s) => {
      const agentType = agents?.find((a) => a.id === s.agentDefinitionId)?.agentType;
      const role = stepRoleLabel(s.stepType || 'Agent', agentType);
      const label = s.label?.trim();
      return label && label.toLowerCase() !== role.toLowerCase() && !/^step \d+$/i.test(label) ? `${label} — ${role}` : role;
    });
}

/**
 * Puts a just-started run at the top of the cached run list, so "Run history" shows it the moment
 * the request returns instead of reading "No runs yet" until the next refetch.
 */
export function prependExecution(list: WorkflowExecution[] | undefined, execution: WorkflowExecution): WorkflowExecution[] {
  return [execution, ...(list ?? []).filter((e) => e.id !== execution.id)];
}

/** What a proposal is applied to: the workflow on screen, another existing one, or a new one. */
export interface ApplyTarget {
  kind: 'page' | 'other' | 'create';
  /** Undefined only for a create. */
  workflowId: string | undefined;
  /** What to send as the name. For an update, an empty name means "keep the name it has". */
  name: string;
}

/** The name a new workflow gets when its proposal did not bring one (the server refuses a nameless create). */
const NEW_WORKFLOW_NAME = 'New workflow';

export const sameId = (a: string | null | undefined, b: string | null | undefined) =>
  typeof a === 'string' && typeof b === 'string' && a.toLowerCase() === b.toLowerCase();

/**
 * Which workflow a proposal is for, from what the proposal itself says.
 *
 * Its confirmation token hashes the steps of the workflow it was computed against, so it can only
 * ever verify against THAT workflow — applying it anywhere else is a guaranteed refusal. `null` is
 * a proposal for a workflow that does not exist yet; a proposal that says nothing (an older server)
 * is read as being for the workflow on screen, which is what every proposal used to mean.
 */
export function resolveApplyTarget(diff: WorkflowEditDiff, page: { workflowId: string; name: string }): ApplyTarget {
  if (diff.workflowId === undefined || sameId(diff.workflowId, page.workflowId)) {
    return { kind: 'page', workflowId: page.workflowId, name: page.name };
  }
  const proposed = diff.name?.trim() ?? '';
  if (diff.workflowId === null) {
    return { kind: 'create', workflowId: undefined, name: proposed || NEW_WORKFLOW_NAME };
  }
  return { kind: 'other', workflowId: diff.workflowId, name: proposed };
}

/**
 * Pure builder→API mapping for one step, the exact inverse of `mapWorkflowStepsToStepData`.
 * Extracted from the save handler (and exported) so a test can pin that an edited decision field
 * actually reaches the payload as `conditionalConfigJson` / `reviewLoopConfigJson`, rather than
 * only asserting that a control rendered.
 *
 * A step whose config is `null` stays `null` in the payload: `null` and the C# defaults are the
 * same thing to the executor (`Expression` mode / disabled early stop), so re-encoding the
 * defaults here would only churn the column.
 */
export function stepDataToCreateRequest(s: StepData, stepOrder: number): CreateWorkflowStepRequest {
  return {
    agentDefinitionId: s.agentDefinitionId,
    stepOrder,
    label: s.label || `Step ${stepOrder}`,
    stepType: s.stepType,
    conditionExpression: s.conditionExpression,
    loopSourceExpression: s.loopSourceExpression,
    loopTargetStepOrder: s.loopTargetStepOrder,
    maxIterations: s.maxIterations,
    minScore: s.minScore,
    inputMappingJson: s.inputMappingJson,
    agentInputContextMode: s.agentInputContextMode,
    selectedPriorStepOrdersJson: s.selectedPriorStepOrders.length > 0 ? JSON.stringify(s.selectedPriorStepOrders) : null,
    trueBranchStepOrder: s.trueBranchStepOrder,
    falseBranchStepOrder: s.falseBranchStepOrder,
    parallelAgentIdsJson: s.parallelAgentIds.length > 0 ? JSON.stringify(s.parallelAgentIds) : null,
    extractConfigJson: s.extractConfig ? JSON.stringify(s.extractConfig) : null,
    videoAnalyzeConfigJson: s.videoAnalyzeConfig ? JSON.stringify(s.videoAnalyzeConfig) : null,
    videoCompileConfigJson: s.videoCompileConfig ? JSON.stringify(s.videoCompileConfig) : null,
    videoGenerateConfigJson: s.videoGenerateConfig ? JSON.stringify(s.videoGenerateConfig) : null,
    voiceoverConfigJson: s.voiceoverConfig ? JSON.stringify(s.voiceoverConfig) : null,
    thumbnailGenerateConfigJson: s.thumbnailGenerateConfig ? JSON.stringify(s.thumbnailGenerateConfig) : null,
    timelineRenderConfigJson: s.timelineRenderConfig ? JSON.stringify(s.timelineRenderConfig) : null,
    designSystemConfigJson: s.designSystemConfig ? JSON.stringify(s.designSystemConfig) : null,
    extractFrameConfigJson: s.extractFrameConfig ? JSON.stringify(s.extractFrameConfig) : null,
    audioGenerateConfigJson: s.audioGenerateConfig ? JSON.stringify(s.audioGenerateConfig) : null,
    imageGenerateConfigJson: s.imageGenerateConfig ? JSON.stringify(s.imageGenerateConfig) : null,
    audioCleanupConfigJson: s.audioCleanupConfig ? JSON.stringify(s.audioCleanupConfig) : null,
    videoEnhanceConfigJson: s.videoEnhanceConfig ? JSON.stringify(s.videoEnhanceConfig) : null,
    ...stepOptionsToRequest(s),
    conditionalConfigJson: s.conditionalConfig ? JSON.stringify(s.conditionalConfig) : null,
    reviewLoopConfigJson: s.reviewLoopConfig ? JSON.stringify(s.reviewLoopConfig) : null,
    editRoomConfigJson: s.editRoomConfigJson ?? null,
    graphicsRoomConfigJson: s.graphicsRoomConfigJson ?? null,
    colorGradeRoomConfigJson: s.colorGradeRoomConfigJson ?? null,
    dailiesRoomConfigJson: s.dailiesRoomConfigJson ?? null,
    cacheMode: s.cacheMode,
  };
}



