import type { T } from '@/i18n/translator';
import { enLabels, enT, labelOf } from '../i18n-default';
import workflowsEn from '../../messages/en/workflows.json';
import type { StepType } from '../types/workflow';

export const STATUS_COLORS: Record<string, string> = {
  Queued: 'gray',
  Running: 'blue',
  Passed: 'green',
  Failed: 'red',
  Pending: 'gray',
  Processing: 'blue',
  Completed: 'green', // StepStatus
  Done: 'green', // SummaryStatus — a distinct enum from StepStatus/ExecutionStatus that uses 'Done'
  Skipped: 'yellow',
  NotIndexed: 'gray',
  Indexed: 'green',
};

/** Maps an AgentType enum value to a display group name. */
export const AGENT_TYPE_GROUP: Record<string, string> = {
  CodeStructureAnalyzer: 'Analysis',
  DependencyAnalyzer: 'Analysis',
  ComponentInventoryAnalyzer: 'Analysis',
  RouteAndApiAnalyzer: 'Analysis',
  StyleAndThemeExtractor: 'Analysis',
  RemotionComponentTranslator: 'Translation',
  AnimationStrategyAgent: 'Translation',
  DirectorAgent: 'Production',
  ScriptwriterAgent: 'Production',
  AuthorAgent: 'Production',
  ReviewAgent: 'Quality',
  FileSummarizerAgent: 'File Processing',
  ExtractTransform: 'Extract',
  VideoStoryEditor: 'Video',
  VideoTransform: 'Video',
  MotionGraphicsPlanner: 'Video',
  MusicSupervisor: 'Video',
  SoundDesigner: 'Video',
  Colorist: 'Video',
  VideoReviewAgent: 'Quality',
  // The room directors: each synthesizes its room's single structured output, reusing the exact
  // schema its solo counterpart above produces.
  VideoEditDirector: 'Video',
  MotionGraphicsDirector: 'Video',
  ColorGradeDirector: 'Video',
  ShotDirector: 'Video',
  NarrationWriter: 'Video',
  PickupPlanner: 'Video',
  NarrationTranslator: 'Video',
  // Makes the changes an owner asks for to a finished video (the "Request changes" panel).
  CompositionReviser: 'Production',
  // Designs one graphic for a picture in the picture editor ("Design a graphic").
  GraphicDesigner: 'Production',
  // The dailies room (issue #93 phase 4): judge seats + the director that writes the take selection.
  TakeSelector: 'Video',
  DailiesDirector: 'Video',
  Custom: 'Custom',
};

export const AGENT_GROUP_COLORS: Record<string, string> = {
  Analysis: 'blue',
  Translation: 'cyan',
  Production: 'violet',
  Quality: 'orange',
  'File Processing': 'teal',
  Extract: 'grape',
  Video: 'indigo',
  Custom: 'pink',
};

/** The agent group's name in the active language. */
export function agentGroupLabel(t: T, group: string): string {
  return labelOf(t, 'agentGroups', group);
}

export function getAgentGroup(agentType: string): string {
  return AGENT_TYPE_GROUP[agentType] || 'Custom';
}

const STEP_TYPE_KEYS = ['Agent', 'Conditional', 'ForEach', 'ReviewLoop', 'Parallel', 'Extract', 'VideoAnalyze', 'VideoCompile', 'VideoGenerate', 'Voiceover', 'EditRoom', 'GraphicsRoom', 'ColorGradeRoom', 'ThumbnailGenerate', 'Revise', 'DesignGraphic', 'DailiesRoom', 'TimelineRender', 'DesignSystem', 'ExtractFrame', 'AudioGenerate', 'PinInsert', 'AudioCleanup', 'VideoEnhance', 'ImageGenerate'] as const satisfies readonly StepType[];

/**
 * `libLabels.stepTypes` predates the namespaced `workflows.stepType` catalogue. A step type added
 * since then carries its English label/description in the latter only, so use that as the fallback
 * instead of showing a raw key (every pre-existing entry keeps resolving from `libLabels`).
 */
function stepTypeText(prefix: 'stepTypes' | 'stepTypeDescriptions', keys: readonly StepType[]): Record<StepType, string> {
  const legacy = enLabels(prefix, keys) as Record<StepType, string>;
  const field = prefix === 'stepTypes' ? 'label' : 'description';
  return Object.fromEntries(keys.map((key) => [
    key,
    enT.has(`libLabels.${prefix}.${key}`)
      ? legacy[key]
      : ((workflowsEn.stepType as Record<string, Record<string, string> | undefined>)[key]?.[field] ?? key),
  ])) as Record<StepType, string>;
}

/**
 * English names of the step types. Components that render them should use `stepTypeLabel(t, type)`.
 * @deprecated Use `stepTypeLabel(t, type)` (translator-taking) so the text follows the active language.
 */
export const STEP_TYPE_LABELS: Record<StepType, string> = stepTypeText('stepTypes', STEP_TYPE_KEYS);

/** The step type's name in the active language (an unknown type shows as it is). */
export function stepTypeLabel(t: T, stepType: string): string {
  return labelOf(t, 'stepTypes', stepType, t.has(`workflows.stepType.${stepType}.label`) ? t(`workflows.stepType.${stepType}.label`) : undefined);
}

// Note: 'cyan'/'teal' were the plan's suggested colors for VideoAnalyze/VideoCompile, but both are
// already taken by ForEach/Parallel in this exact map — picked 'blue'/'indigo' instead so every
// step type badge in the flowchart builder is visually distinct, not just distinct from Extract.
export const STEP_TYPE_COLORS: Record<StepType, string> = {
  Agent: 'violet',
  Conditional: 'orange',
  ForEach: 'cyan',
  ReviewLoop: 'green',
  Parallel: 'teal',
  Extract: 'grape',
  VideoAnalyze: 'blue',
  VideoCompile: 'indigo',
  VideoGenerate: 'red',
  Voiceover: 'red',
  EditRoom: 'pink',
  GraphicsRoom: 'lime',
  // 'orange' is already Conditional's — the step-type badge palette is deliberately collision-free
  // so two step types are never the same colour in the flowchart.
  ColorGradeRoom: 'yellow',
  // Mantine's remaining palette slot ('dark' renders near-invisible on a light card at `variant="light"`).
  // A still-image step is the one genuinely neutral member of the family, so gray reads as deliberate
  // rather than as a missing colour.
  ThumbnailGenerate: 'gray',
  // A system step (never offered to authors), so it shares Agent's colour: it IS one agent doing one job.
  Revise: 'violet',
  // A system step too, and also one agent doing one job.
  DesignGraphic: 'violet',
  // The dailies room judges generated takes; 'grape' is Extract's hue but the two never share a picker row.
  DailiesRoom: 'grape',
  // The editor's delivery step and VideoCompile's sibling (both turn a decided cut into a file), so
  // it shares indigo: Mantine's palette has no distinct slot left that reads on a light card.
  TimelineRender: 'indigo',
  // A system-flavoured deterministic step that reads no model; it shares the brand hue the other
  // non-model steps Revise/DesignGraphic use — Mantine's palette has no distinct light-card slot left.
  DesignSystem: 'violet',
  // A still-image step like ThumbnailGenerate, so it shares its neutral gray (the palette has no free slot).
  ExtractFrame: 'gray',
  // A sound step: shares Voiceover's red, the audio family's hue.
  AudioGenerate: 'red',
  // Another sound step; the palette has no free slot left and the audio family is red.
  AudioCleanup: 'red',
  // A picture-making step; blue is the image family's hue (ThumbnailGenerate and the editor picture tools).
  ImageGenerate: 'blue',
  PinInsert: 'teal',
  // A picture-quality step; cyan is shared with ForEach, the palette has no free slot left.
  VideoEnhance: 'cyan',
};

/**
 * English one-line explanations of the step types. Components should use `stepTypeDescription(t, type)`.
 * @deprecated Use `stepTypeDescription(t, type)` (translator-taking) so the text follows the active language.
 */
export const STEP_TYPE_DESCRIPTIONS: Record<StepType, string> = stepTypeText('stepTypeDescriptions', STEP_TYPE_KEYS);

/** The step type's one-line explanation in the active language. */
export function stepTypeDescription(t: T, stepType: string): string {
  return labelOf(t, 'stepTypeDescriptions', stepType, t.has(`workflows.stepType.${stepType}.description`) ? t(`workflows.stepType.${stepType}.description`) : undefined);
}

/**
 * Step types the platform creates itself and an author can never add or switch to: `Revise` is the
 * one step of the hidden "Video revisions" workflow a "Request changes" run executes. Every
 * `Record<StepType, …>` still carries an entry (so a revision run renders like any other), but the
 * add-step picker and the step-type Select both leave these out.
 */
export const SYSTEM_STEP_TYPES: ReadonlySet<StepType> = new Set<StepType>(['Revise', 'DesignGraphic']);

/** True for a step type an author may add to a workflow (i.e. not a system step). */
export function isAuthorableStepType(stepType: StepType): boolean {
  return !SYSTEM_STEP_TYPES.has(stepType);
}
