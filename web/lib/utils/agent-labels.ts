/**
 * Display helpers for agents on the execution page, the Renders/Editor screens and the Agents page.
 *
 * The plain-language role and progress phrase of every agent type live in ONE place,
 * `agent-roles.ts` (shared with the workflow builder), so a step never reads differently on two
 * pages. This file only adds what those surfaces need on top of it:
 *  - resolving an engine identifier as it arrives in an event payload (`NARRATIONWRITER`,
 *    `video_transform`) to its canonical agent type before looking the role up;
 *  - a one-line description per built-in agent for the Agents page cards;
 *  - customer-facing headings for the Agents page groups.
 */
import {
  agentProgressPhrase as roleProgressPhrase,
  agentRoleLabel as roleLabel,
  hasAgentRole,
  stepProgressPhrase as roleStepProgressPhrase,
  stepRoleLabel as roleStepLabel,
} from './agent-roles';
import type { T } from '@/i18n/translator';
import { enT, labelOf, labelSlug } from '@/lib/i18n-default';
import type { StepType } from '../types/workflow';

/** Every `AgentType` enum member (ReelBolt.Shared `Enums.cs`), for case-insensitive resolution. */
const AGENT_TYPES = [
  'CodeStructureAnalyzer', 'DependencyAnalyzer', 'ComponentInventoryAnalyzer', 'RouteAndApiAnalyzer', 'StyleAndThemeExtractor',
  'RemotionComponentTranslator', 'AnimationStrategyAgent', 'DirectorAgent', 'ScriptwriterAgent', 'AuthorAgent', 'ReviewAgent',
  'FileSummarizerAgent', 'Custom', 'ExtractTransform', 'VideoStoryEditor', 'VideoTransform', 'MotionGraphicsPlanner',
  'VideoReviewAgent', 'MusicSupervisor', 'VideoEditDirector', 'MotionGraphicsDirector', 'Colorist', 'ColorGradeDirector',
  'SoundDesigner', 'Assistant', 'ShotDirector', 'NarrationWriter', 'PickupPlanner', 'NarrationTranslator',
  'CompositionReviser', 'GraphicDesigner', 'TakeSelector', 'DailiesDirector',
] as const;

const BY_NORMALIZED = new Map<string, string>(AGENT_TYPES.map((t) => [t.toLowerCase(), t]));

/** `NARRATIONWRITER` / `narration_writer` / `NarrationWriter` → `NarrationWriter`; null when unknown. */
export function canonicalAgentType(raw: string | null | undefined): string | null {
  if (!raw) return null;
  return BY_NORMALIZED.get(raw.replace(/[\s_-]/g, '').toLowerCase()) ?? null;
}

/** Splits `PascalCase` / `ALL_CAPS_WITH_UNDERSCORES` into "Sentence case words". */
export function humanizeIdentifier(raw: string): string {
  const spaced = raw
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .trim()
    .toLowerCase();
  return spaced ? spaced[0].toUpperCase() + spaced.slice(1) : raw;
}

/**
 * The customer-facing role for an agent identifier as it appears in a payload or row. Built-ins
 * resolve (case-insensitively) to the shared role; anything else — a custom agent's own name, a
 * room seat like `LayoutArtist` — is kept, humanized when it looks like an identifier.
 */
export function agentRoleLabel(agentTypeOrName: string | null | undefined, fallbackName?: string | null, t: T = enT): string {
  const type = canonicalAgentType(agentTypeOrName);
  if (type && type !== 'Custom' && hasAgentRole(type)) return roleLabel(type, t);
  if (fallbackName && fallbackName !== agentTypeOrName) return fallbackName;
  if (!agentTypeOrName) return roleLabel(null, t);
  return /^[A-Za-z0-9_]+$/.test(agentTypeOrName) ? humanizeIdentifier(agentTypeOrName) : agentTypeOrName;
}

/** "Writing the narration…" for any spelling of a built-in agent type. */
export function agentProgressPhrase(agentType: string | null | undefined, t: T = enT): string {
  return roleProgressPhrase(canonicalAgentType(agentType) ?? agentType, t);
}

/** Customer-facing job of a step (shared with the builder). */
export function stepRoleLabel(stepType: StepType | string | null | undefined, agentType?: string | null, t: T = enT): string {
  return roleStepLabel(stepType, canonicalAgentType(agentType) ?? agentType, t);
}

/** "Watching your footage…" — the running-state phrase of a step (shared with the builder). */
export function stepProgressPhrase(stepType: StepType | string | null | undefined, agentType?: string | null, t: T = enT): string {
  return roleStepProgressPhrase(stepType, canonicalAgentType(agentType) ?? agentType, t);
}

const DESCRIBED_AGENTS = [
  'CodeStructureAnalyzer', 'DependencyAnalyzer', 'ComponentInventoryAnalyzer', 'RouteAndApiAnalyzer', 'StyleAndThemeExtractor',
  'RemotionComponentTranslator', 'AnimationStrategyAgent', 'DirectorAgent', 'ScriptwriterAgent', 'AuthorAgent', 'ReviewAgent',
  'FileSummarizerAgent', 'ExtractTransform', 'VideoStoryEditor', 'VideoTransform', 'MotionGraphicsPlanner', 'VideoReviewAgent',
  'MusicSupervisor', 'VideoEditDirector', 'MotionGraphicsDirector', 'Colorist', 'ColorGradeDirector', 'SoundDesigner', 'Assistant',
  'ShotDirector', 'NarrationWriter', 'PickupPlanner', 'NarrationTranslator', 'CompositionReviser', 'GraphicDesigner',
  'TakeSelector', 'DailiesDirector',
] as const;

/**
 * English one-liner per built-in agent (catalogue: `libLabels.agentDescriptions`); components use `agentRoleDescription(type, t)`.
 * @deprecated Use `agentRoleDescription(type, t)` (translator-taking) so the text follows the active language.
 */
export const AGENT_DESCRIPTIONS: Record<string, string> = Object.fromEntries(
  DESCRIBED_AGENTS.map((k) => [k, enT(`libLabels.agentDescriptions.${k}`)]),
);

/** The one-line description for an agent type, or null when it is not a built-in. */
export function agentRoleDescription(agentType: string | null | undefined, t: T = enT): string | null {
  const type = canonicalAgentType(agentType);
  return type && (DESCRIBED_AGENTS as readonly string[]).includes(type) ? t(`libLabels.agentDescriptions.${type}`) : null;
}

/** Display name for an agent row: the role for built-ins, the customer's own name for custom ones. */
export function agentDisplayName(agent: { name: string; agentType: string; isBuiltIn: boolean }, t: T = enT): string {
  if (!agent.isBuiltIn) return agent.name;
  return agentRoleLabel(agent.agentType, agent.name, t);
}

/** One plain line for an agent row: the built-in description, or the custom agent's own. */
export function agentDisplayDescription(agent: { description: string; agentType: string; isBuiltIn: boolean }, t: T = enT): string {
  if (agent.isBuiltIn) return agentRoleDescription(agent.agentType, t) ?? agent.description;
  return agent.description;
}

const AGENT_GROUP_KEYS = ['Analysis', 'Translation', 'Production', 'Video', 'Quality', 'File Processing', 'Extract', 'Custom'] as const;

/**
 * English headings for the agent groups; components use `agentGroupTitle(t, group)`.
 * @deprecated Use `agentGroupTitle(t, group)` (translator-taking) so the text follows the active language.
 */
export const AGENT_GROUP_TITLES: Record<string, string> = Object.fromEntries(
  AGENT_GROUP_KEYS.map((k) => [k, enT(`libLabels.agentGroupTitles.${labelSlug(k)}`)]),
);

/** Customer-facing heading of an agent group in the active language (unknown groups show as they are). */
export function agentGroupTitle(t: T, group: string): string {
  return labelOf(t, 'agentGroupTitles', group);
}
