import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import type { StepType } from '../types/workflow';

/**
 * Plain-language names for what each built-in agent DOES, for customer-facing surfaces (the
 * workflow builder's step cards, the execution page's "what is happening now" line).
 *
 * The enum names (`VideoStoryEditor`, `NarrationWriter`…) are implementation names; a customer
 * reads "Picks the best clips". One map, used everywhere, so a step never has two different
 * descriptions on two pages. Keep every entry short — it is a card title, not a sentence.
 *
 * `role` is the present-tense job ("Writes the narration"); `progress` is what the execution page
 * shows while the step runs ("Writing the narration…").
 */
const AGENT_TYPES_WITH_ROLE = new Set([
  'CodeStructureAnalyzer', 'DependencyAnalyzer', 'ComponentInventoryAnalyzer', 'RouteAndApiAnalyzer', 'StyleAndThemeExtractor',
  'RemotionComponentTranslator', 'AnimationStrategyAgent', 'ScriptwriterAgent', 'DirectorAgent', 'AuthorAgent', 'ReviewAgent',
  'CompositionReviser', 'GraphicDesigner', 'VideoStoryEditor', 'VideoEditDirector', 'MotionGraphicsPlanner',
  'MotionGraphicsDirector', 'MusicSupervisor', 'SoundDesigner', 'Colorist', 'ColorGradeDirector', 'ShotDirector',
  'VideoReviewAgent', 'NarrationWriter', 'NarrationTranslator', 'TakeSelector', 'DailiesDirector', 'PickupPlanner',
  'ExtractTransform', 'VideoTransform', 'FileSummarizerAgent', 'Assistant',
]);

const STEP_TYPES_WITH_ROLE = new Set<string>([
  'Conditional', 'ForEach', 'ReviewLoop', 'Parallel', 'Extract', 'VideoAnalyze', 'VideoCompile', 'VideoGenerate', 'Voiceover',
  'EditRoom', 'GraphicsRoom', 'ColorGradeRoom', 'ThumbnailGenerate', 'Revise', 'DesignGraphic', 'DailiesRoom', 'TimelineRender',
  'DesignSystem', 'ExtractFrame', 'AudioGenerate', 'AudioCleanup', 'PinInsert', 'VideoEnhance', 'ImageGenerate',
]);

// Catalogue: libLabels.agentRoles.<AgentType>.{role,progress}, libLabels.stepRoles.<StepType>.{role,progress}, libLabels.customRole.
// Every helper takes an optional translator (`useT()` / `getT()`); without it the English phrase is returned.
function agentPhrase(t: T, agentType: string | null | undefined, field: 'role' | 'progress'): string {
  return t(agentType && AGENT_TYPES_WITH_ROLE.has(agentType) ? `libLabels.agentRoles.${agentType}.${field}` : `libLabels.customRole.${field}`);
}

/** "Picks the best clips" — the customer-facing job of an agent type. Unknown/custom types get a generic phrase. */
export function agentRoleLabel(agentType: string | null | undefined, t: T = enT): string {
  return agentPhrase(t, agentType, 'role');
}

/** "Picking the best clips…" — what to show while a step backed by this agent type is running. */
export function agentProgressPhrase(agentType: string | null | undefined, t: T = enT): string {
  return agentPhrase(t, agentType, 'progress');
}

/** True when the agent type has its own plain-language role (i.e. is a known built-in). */
export function hasAgentRole(agentType: string | null | undefined): boolean {
  return Boolean(agentType && AGENT_TYPES_WITH_ROLE.has(agentType));
}

function stepPhrase(t: T, stepType: StepType | string | null | undefined, agentType: string | null | undefined, field: 'role' | 'progress'): string {
  if ((stepType === 'Agent' || !stepType) && agentType) return agentPhrase(t, agentType, field);
  if (stepType === 'ReviewLoop' && agentType && hasAgentRole(agentType)) return agentPhrase(t, agentType, field);
  if (stepType && STEP_TYPES_WITH_ROLE.has(stepType)) {
    const key = `libLabels.stepRoles.${stepType}.${field}`;
    if (t.has(key)) return t(key);
    // A step type newer than the role table falls back to its one-line step-type description.
    const description = `workflows.stepType.${stepType}.description`;
    if (t.has(description)) return t(description);
  }
  return agentPhrase(t, agentType, field);
}

/**
 * The customer-facing job of a whole step: for an Agent (or ReviewLoop/ForEach, which also run an
 * agent) step it is the agent's role; otherwise the step type's own job.
 */
export function stepRoleLabel(stepType: StepType | string | null | undefined, agentType?: string | null, t: T = enT): string {
  return stepPhrase(t, stepType, agentType, 'role');
}

/** "Watching your footage…" — the running-state phrase for a whole step. */
export function stepProgressPhrase(stepType: StepType | string | null | undefined, agentType?: string | null, t: T = enT): string {
  return stepPhrase(t, stepType, agentType, 'progress');
}
