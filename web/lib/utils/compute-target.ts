/**
 * Executed-by (E8-UI): which target actually ran a step, rendered from the value
 * the engine PERSISTED — `workflow_step_results.compute_target` (D6's decision,
 * copied onto the row by D12 and exposed on the step-result DTO).
 *
 * Two rules this module keeps, borrowed from the E7-UI runner surface:
 *
 *   - The value is narrowed, never trusted. `computeTarget` arrives as a string;
 *     a value this build does not recognise renders NOTHING rather than being
 *     relabelled as one of the three it knows.
 *   - Nothing is inferred from the absence of a value. A row written before D12,
 *     or a response from a build that does not send the field, has no target: the
 *     honest answer is silence, not "the platform ran it".
 *
 * And the vocabulary is the engine's, not the plan's: `Local` is NOT "ReelBolt
 * cloud". `Local` means the workflow engine process that handled the run did the
 * work itself — on a self-host install that is the customer's own server, and on
 * the cloud deployment it is our control plane, but the storage is the same in
 * both, so the badge says where the work ran ("this server"), never who owns it.
 */

import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/** `ComputeTargetKind` (ReelBolt.Shared/Data/Models/ComputeTargetKind.cs). */
export type ComputeTarget = 'Local' | 'CloudPool' | 'OrgRunner';

const KNOWN: readonly ComputeTarget[] = ['Local', 'CloudPool', 'OrgRunner'];

export interface ComputeBadge {
  label: string;
  color: string;
  /** Hover text. Explains what the badge is evidence of, never more than the data supports. */
  tooltip: string;
}

/**
 * The persisted target, or `null` when there is nothing to show.
 *
 * `fromRunner` is the second chance: the engine derives that tag from the target
 * (`FromRunner => ComputeTarget == OrgRunner`), so a payload carrying the tag but
 * not the target still says a paired machine ran the step. It is only consulted
 * when the target itself is unusable, and only when it is exactly `true` — a
 * missing tag is not evidence of anything.
 */
export function parseComputeTarget(raw: unknown, fromRunner?: unknown): ComputeTarget | null {
  if (typeof raw === 'string' && (KNOWN as readonly string[]).includes(raw)) {
    return raw as ComputeTarget;
  }
  return fromRunner === true ? 'OrgRunner' : null;
}

/** An id shortened for a tooltip: enough to match against a row in the runners page. */
function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}

/** The slice of a step result this module reads. */
export interface StepComputeFacts {
  computeTarget?: unknown;
  fromRunner?: unknown;
  runnerId?: unknown;
  runnerJobId?: unknown;
}

/** The slice of an execution this module reads. */
export interface ExecutionComputeFacts {
  stepResults?: readonly StepComputeFacts[] | null;
}

/**
 * The badge for one step, or `null` when the run's data does not say where it ran.
 *
 * `runnerNames` maps a device id to its name, resolved by the caller from the
 * workspace's runner list (the same "the page fetches, the presentational
 * component renders" split the runners page uses). A device that has since been
 * revoked is gone from that list, so the badge falls back to "your machine" and
 * the tooltip still carries the id — which is true, where "a machine you can see
 * in the list" would not be.
 */
export function stepComputeBadge(
  step: StepComputeFacts,
  runnerNames?: Record<string, string>,
  t: T = enT,
): ComputeBadge | null {
  const target = parseComputeTarget(step.computeTarget, step.fromRunner);
  if (target === null) {
    return null;
  }

  if (target === 'OrgRunner') {
    const runnerId = typeof step.runnerId === 'string' && step.runnerId !== '' ? step.runnerId : null;
    const runnerJobId = typeof step.runnerJobId === 'string' && step.runnerJobId !== '' ? step.runnerJobId : null;
    const name = runnerId ? runnerNames?.[runnerId] : undefined;

    const details: string[] = [];
    if (runnerId) details.push(t('libLabels.compute.detailDevice', { id: shortId(runnerId) }));
    if (runnerJobId) details.push(t('libLabels.compute.detailJob', { id: shortId(runnerJobId) }));

    return {
      label: name ? t('libLabels.compute.ranOnNamed', { name }) : t('libLabels.compute.ranOnYours'),
      color: 'teal',
      tooltip: t('libLabels.compute.tipRunner', { details: details.length > 0 ? ` (${details.join(', ')})` : '' }),
    };
  }

  if (target === 'CloudPool') {
    return {
      label: t('libLabels.compute.ranCloud'),
      color: 'blue',
      tooltip: t('libLabels.compute.tipCloud'),
    };
  }

  return {
    label: t('libLabels.compute.ranServer'),
    color: 'gray',
    tooltip: t('libLabels.compute.tipServer'),
  };
}

/**
 * The chip for a whole run: present only when at least one of its steps ran on a
 * machine the workspace paired.
 *
 * Deliberately runner-only. A chip for "this run used the platform", or for the
 * cloud pool, would appear on every run and on every deployment (including
 * self-host, where none of it applies), which is noise rather than information.
 */
export function executionComputeChip(execution: ExecutionComputeFacts, t: T = enT): ComputeBadge | null {
  const ranOnRunner = (execution.stepResults ?? []).some(
    (step) => parseComputeTarget(step.computeTarget, step.fromRunner) === 'OrgRunner',
  );
  if (!ranOnRunner) {
    return null;
  }
  return {
    label: t('libLabels.compute.ranOnYours'),
    color: 'teal',
    tooltip: t('libLabels.compute.tipChip'),
  };
}
