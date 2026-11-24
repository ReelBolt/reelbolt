'use client';

import { Stack, Button } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { StepCard } from './StepCard';
import restyle from './graph-restyle.module.css';
import type {
  AgentInputContextMode,
  CacheMode,
  ConditionalStepConfig as ConditionalStepConfigValue,
  ExtractStepConfig as ExtractStepConfigValue,
  ReviewLoopStepConfig as ReviewLoopStepConfigValue,
  StepType,
  VideoAnalyzeStepConfig as VideoAnalyzeStepConfigValue,
  VideoCompileStepConfig as VideoCompileStepConfigValue,
  VideoGenerateStepConfig as VideoGenerateStepConfigValue,
  VoiceoverStepConfig as VoiceoverStepConfigValue,
  ThumbnailGenerateStepConfig as ThumbnailGenerateStepConfigValue,
  TimelineRenderStepConfig as TimelineRenderStepConfigValue,
  DesignSystemStepConfig as DesignSystemStepConfigValue,
  ExtractFrameStepConfig as ExtractFrameStepConfigValue,
  AudioGenerateStepConfig as AudioGenerateStepConfigValue,
  ImageGenerateStepConfig as ImageGenerateStepConfigValue,
  AudioCleanupStepConfig as AudioCleanupStepConfigValue,
  VideoEnhanceStepConfig as VideoEnhanceStepConfigValue,
  PinInsertStepConfig as PinInsertStepConfigValue,
} from '@/lib/types/workflow';

export interface StepData {
  id: string;
  label: string;
  agentDefinitionId: string;
  stepType: StepType;
  conditionExpression: string | null;
  loopSourceExpression: string | null;
  loopTargetStepOrder: number | null;
  maxIterations: number;
  minScore: number | null;
  inputMappingJson: string | null;
  agentInputContextMode: AgentInputContextMode | null;
  /** Per-step step-result-cache override. `null` behaves the same as `'Default'`. */
  cacheMode: CacheMode | null;
  selectedPriorStepOrders: number[];
  trueBranchStepOrder: string | null;
  falseBranchStepOrder: string | null;
  /** Ordered list of AgentDefinition IDs to run in parallel (Parallel step type only). */
  parallelAgentIds: string[];
  /** Extract step configuration (Extract step type only). */
  extractConfig: ExtractStepConfigValue | null;
  /** VideoAnalyze step configuration (VideoAnalyze step type only). */
  videoAnalyzeConfig: VideoAnalyzeStepConfigValue | null;
  /** VideoCompile step configuration (VideoCompile step type only). */
  videoCompileConfig: VideoCompileStepConfigValue | null;
  /** VideoGenerate step configuration (VideoGenerate step type only). */
  videoGenerateConfig: VideoGenerateStepConfigValue | null;
  /** Voiceover step configuration (Voiceover step type only). */
  voiceoverConfig: VoiceoverStepConfigValue | null;
  /** ThumbnailGenerate step configuration (ThumbnailGenerate step type only). */
  thumbnailGenerateConfig: ThumbnailGenerateStepConfigValue | null;
  /**
   * TimelineRender step configuration (TimelineRender step type only — editor v2, P4). Optional-
   * nullable for the same reason `conditionalConfig` below is: every existing `StepData` literal
   * keeps compiling. `null`/absent means "no config", which the save validator rejects for a
   * TimelineRender step (a render needs a timeline).
   */
  timelineRenderConfig?: TimelineRenderStepConfigValue | null;
  /**
   * DesignSystem step configuration (DesignSystem step type only). Optional-nullable like
   * `timelineRenderConfig` above, so every existing `StepData` literal keeps compiling.
   */
  designSystemConfig?: DesignSystemStepConfigValue | null;
  /** ExtractFrame step configuration (ExtractFrame step type only). Optional-nullable like `designSystemConfig`. */
  extractFrameConfig?: ExtractFrameStepConfigValue | null;
  /** AudioGenerate step configuration (AudioGenerate step type only). Optional-nullable like `designSystemConfig`. */
  audioGenerateConfig?: AudioGenerateStepConfigValue | null;
  /** ImageGenerate step configuration (ImageGenerate step type only). Optional-nullable like `designSystemConfig`. */
  imageGenerateConfig?: ImageGenerateStepConfigValue | null;
  /** AudioCleanup step configuration (AudioCleanup step type only). Optional-nullable like `designSystemConfig`. */
  audioCleanupConfig?: AudioCleanupStepConfigValue | null;
  /** VideoEnhance step configuration (VideoEnhance step type only). Optional-nullable like `designSystemConfig`. */
  videoEnhanceConfig?: VideoEnhanceStepConfigValue | null;
  /** PinInsert step configuration (PinInsert step type only). Optional-nullable like `designSystemConfig`. */
  pinInsertConfig?: PinInsertStepConfigValue | null;
  /**
   * Step option (Agent steps): continue from an earlier run's composition. The parsed
   * `continueCompositionJson` (`{executionId}` or `{versionId}`); `null`/absent is off.
   */
  continueComposition?: { executionId?: string | null; versionId?: string | null } | null;
  /** Step option: register this step's delivered output as a project file. `false`/absent is off. */
  registerOutputAsProjectFile?: boolean;
  /**
   * Conditional step decision-arm configuration (Conditional step type only), carried as a parsed
   * object exactly like the extract/video configs above. Optional-nullable so every existing
   * `StepData` construction site outside this change keeps compiling unchanged — the same
   * rationale `editRoomConfigJson` documents below. `null` means "no config", which the Conditional
   * executor treats as `{ mode: 'Expression' }`.
   */
  conditionalConfig?: ConditionalStepConfigValue | null;
  /**
   * ReviewLoop step calibrated early-stop configuration (ReviewLoop step type only). Same
   * optional-nullable discipline as `conditionalConfig` above; `null` means "no config", which is
   * byte-identically the pre-decision-engine reviewer path.
   */
  reviewLoopConfig?: ReviewLoopStepConfigValue | null;
  /**
   * Raw JSON-serialized EditRoomStepConfig (EditRoom step type only). Deliberately carried as a
   * raw string (not a parsed object like the extract/video configs): EditRoomStepConfigEditor
   * parses it, edits the handful of fields it exposes, and re-serializes while spread-preserving
   * every field it does not know about — so a template-provisioned config (seats, temperatures,
   * termination mode…) round-trips losslessly through the builder. Optional so every existing
   * StepData construction site keeps compiling unchanged.
   */
  editRoomConfigJson?: string | null;
  /**
   * Raw JSON-serialized GraphicsRoomStepConfig (GraphicsRoom step type only). Same opaque
   * passthrough discipline as editRoomConfigJson above.
   */
  graphicsRoomConfigJson?: string | null;
  /**
   * Raw JSON-serialized ColorGradeRoomStepConfig (ColorGradeRoom step type only). Same opaque
   * passthrough discipline as editRoomConfigJson above.
   */
  colorGradeRoomConfigJson?: string | null;
  /**
   * Raw JSON-serialized DailiesRoomStepConfig (DailiesRoom step type only). Same opaque
   * passthrough discipline as editRoomConfigJson above.
   */
  dailiesRoomConfigJson?: string | null;
}

interface WorkflowStepListProps {
  steps: StepData[];
  onChange: (steps: StepData[]) => void;
  /** Threaded into StepCard's VideoAnalyzeStepConfig so its VideoSourceRef picker can list this project's video files. */
  projectId?: string;
}

let nextId = 1;

/** Returns a new array with the item at `from` moved to `to`. Mirrors dnd-kit's `arrayMove` utility
 * without depending on it — this mobile fallback list is reorderable by button tap, not drag. */
function arrayMove<T>(array: T[], from: number, to: number): T[] {
  const next = [...array];
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);
  return next;
}

export function WorkflowStepList({ steps, onChange, projectId }: WorkflowStepListProps) {
  const t = useT('workflows.stepList');
  const addStep = () => {
    onChange([
      ...steps,
      {
        id: `new-${nextId++}`,
        label: '',
        agentDefinitionId: '',
        stepType: 'Agent',
        conditionExpression: null,
        loopSourceExpression: null,
        loopTargetStepOrder: null,
        maxIterations: 3,
        minScore: null,
        inputMappingJson: null,
        agentInputContextMode: null,
        cacheMode: null,
        selectedPriorStepOrders: [],
        trueBranchStepOrder: null,
        falseBranchStepOrder: null,
        parallelAgentIds: [],
        extractConfig: null,
        videoAnalyzeConfig: null,
        videoCompileConfig: null,
        videoGenerateConfig: null,
        voiceoverConfig: null,
        thumbnailGenerateConfig: null,
        conditionalConfig: null,
        reviewLoopConfig: null,
      },
    ]);
  };

  const updateStep = (index: number, updates: Partial<StepData>) => {
    const newSteps = [...steps];
    newSteps[index] = { ...newSteps[index], ...updates };
    onChange(newSteps);
  };

  const removeStep = (index: number) => {
    onChange(steps.filter((_, i) => i !== index));
  };

  const moveStep = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= steps.length) return;
    onChange(arrayMove(steps, index, target));
  };

  return (
    <Stack gap="sm" className={restyle.stepList}>
      {steps.map((step, index) => (
        <StepCard
          key={step.id}
          step={step}
          stepNumber={index + 1}
          allSteps={steps}
          currentStepIndex={index}
          projectId={projectId}
          onChange={(updates) => updateStep(index, updates)}
          onRemove={() => removeStep(index)}
          onMoveUp={() => moveStep(index, -1)}
          onMoveDown={() => moveStep(index, 1)}
          canMoveUp={index > 0}
          canMoveDown={index < steps.length - 1}
        />
      ))}
      <Button variant="outline" leftSection={<IconPlus size={16} />} onClick={addStep}>
        {t('addStep')}
      </Button>
    </Stack>
  );
}
