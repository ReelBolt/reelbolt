import type { WorkflowEditDiff, WorkflowStepDiffEntry } from '@/lib/types/workflow-assistant';
import { STEP_TYPE_LABELS } from '@/lib/utils/constants';
import type { StepType } from '@/lib/types/workflow';
import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

export type DiffKind = 'added' | 'removed' | 'changed';

/** The minimum a step needs for the diff to name it: what the user sees on the canvas. */
export interface NamedStep {
  label?: string | null;
  stepType?: string | null;
}

/** One diff entry, resolved to the names a person recognises rather than bare step orders. */
export interface DiffRow {
  kind: DiffKind;
  key: string;
  stepOrder: number;
  /** The step's name after the change (for a removal: the name of the step being removed). */
  name: string;
  /** For a change that renames the step: its current name. Undefined when the name is unchanged. */
  previousName?: string;
  /** Human label of the step type, e.g. "Agent", "Review loop". */
  stepTypeLabel: string;
  /** The server's one-line summary, only when it adds something the name doesn't already say. */
  detail?: string;
}

export const DIFF_KIND_ORDER: DiffKind[] = ['added', 'changed', 'removed'];

/** Catalogue keys of the diff kind words ("Added"); resolve with `t(DIFF_KIND_KEY[kind])`. */
export const DIFF_KIND_KEY: Record<DiffKind, string> = {
  added: 'assistant.diff.kind.added',
  removed: 'assistant.diff.kind.removed',
  changed: 'assistant.diff.kind.changed',
};

/** English kind words, kept for callers that have no translator. */
export const DIFF_KIND_LABEL: Record<DiffKind, string> = {
  added: enT(DIFF_KIND_KEY.added),
  removed: enT(DIFF_KIND_KEY.removed),
  changed: enT(DIFF_KIND_KEY.changed),
};

export function stepTypeLabel(stepType: string | null | undefined, t: T = enT): string {
  if (!stepType) return t('assistant.diff.step');
  return STEP_TYPE_LABELS[stepType as StepType] ?? stepType;
}

function nameOf(t: T, step: NamedStep | undefined, fallbackType: string): string | undefined {
  const label = step?.label?.trim();
  if (label) return label;
  return step ? t('assistant.diff.stepOfType', { type: stepTypeLabel(step.stepType ?? fallbackType, t) }) : undefined;
}

function row(
  t: T,
  kind: DiffKind,
  entry: WorkflowStepDiffEntry,
  name: string,
  previousName?: string,
): DiffRow {
  const summary = entry.summary?.trim();
  const said = [name, previousName].filter(Boolean).map((n) => n!.toLowerCase());
  return {
    kind,
    key: `${kind}-${entry.stepOrder}-${entry.agentDefinitionId}`,
    stepOrder: entry.stepOrder,
    name,
    previousName: previousName && previousName !== name ? previousName : undefined,
    stepTypeLabel: stepTypeLabel(entry.stepType, t),
    detail: summary && !said.includes(summary.toLowerCase()) ? summary : undefined,
  };
}

/**
 * Resolves a proposed diff against the steps currently on the canvas, so the review surface can
 * say "Write the script → Review" instead of "Agent · Step 2".
 *
 * Step orders in the diff are 1-based. Removals and changes are named from the CURRENT steps
 * (that is what the user is looking at); additions and a change's new name come from the
 * proposal's own `proposedSteps`. Any lookup that misses — a stale proposal, a proposal from an
 * older server without `proposedSteps` — falls back to the entry's own summary and type rather
 * than throwing.
 */
export function describeDiff(diff: WorkflowEditDiff, currentSteps: readonly NamedStep[] = [], t: T = enT): DiffRow[] {
  const proposed = diff.proposedSteps ?? [];
  const current = (order: number) => currentSteps[order - 1];
  const next = (order: number) => proposed.find((s) => s.stepOrder === order) ?? proposed[order - 1];
  const fallback = (entry: WorkflowStepDiffEntry) =>
    entry.summary?.trim() || t('assistant.diff.stepOfType', { type: stepTypeLabel(entry.stepType, t) });

  const added = diff.additions.map((e) =>
    row(t, 'added', e, nameOf(t, next(e.stepOrder), e.stepType) ?? fallback(e)),
  );
  const changed = diff.modifications.map((e) => {
    const before = nameOf(t, current(e.stepOrder), e.stepType);
    const after = nameOf(t, next(e.stepOrder), e.stepType);
    return row(t, 'changed', e, after ?? before ?? fallback(e), after ? before : undefined);
  });
  const removed = diff.removals.map((e) =>
    row(t, 'removed', e, nameOf(t, current(e.stepOrder), e.stepType) ?? fallback(e)),
  );

  return [...added, ...changed, ...removed];
}

export function countByKind(rows: readonly DiffRow[]): Record<DiffKind, number> {
  const counts: Record<DiffKind, number> = { added: 0, removed: 0, changed: 0 };
  for (const r of rows) counts[r.kind] += 1;
  return counts;
}
