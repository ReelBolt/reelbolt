/**
 * The workspace compute preference (E8-UI): the four values the engine really
 * switches over, the wording for each, and the honesty notes that have to travel
 * with them.
 *
 * The vocabulary is mirrored from `ComputePreference`
 * (inference/src/ReelBolt.Shared/Tenancy/ComputePreference.cs), NOT from the plan
 * section that named this work package — the plan predates the shipped enum and
 * listed only three values. The column is `organizations.compute_preference`,
 * stored as a string, defaulting to `Auto`.
 *
 * Three facts this module exists to state, because a settings screen that left
 * them out would overclaim what the setting does:
 *
 *   1. A preference is a REQUEST, not a guarantee. `ComputeTargetSelector`
 *      consults it only under `Compute:Mode=Fabric`; with the default
 *      `Compute:Mode=Local` — every self-host install, and the shipped
 *      configuration — the selector answers `Local` before it reads the database
 *      or calls the runner gateway at all.
 *   2. `RunnerOnly` FAILS the step with `NO_RUNNER_ONLINE` when no runner is
 *      online. Nothing silently falls back to cloud billing — the run stops there.
 *   3. With `Compute:LocalWhenPoolEmpty` (default on) a cloud-pool decision with no
 *      pool worker online runs on the engine's own host, so every other arm ends on
 *      ReelBolt when no paired machine is online.
 *
 * The text below is copy, not authority. The engine is authoritative, the server
 * validates the value, and a plan that withholds `RunnerAllowed` makes the engine
 * read the preference as `CloudOnly` whatever it says.
 */

/** The four values `ComputePreference` has (`Auto` is the column default and the engine's fail-open answer). */
import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

export type ComputePreference = 'Auto' | 'CloudOnly' | 'PreferRunner' | 'RunnerOnly';

const KNOWN: readonly ComputePreference[] = ['Auto', 'CloudOnly', 'PreferRunner', 'RunnerOnly'];

/**
 * Narrows a preference that came over the wire, or `null` when it is not one this
 * build knows. The API is the same release as this bundle, so `null` in practice
 * means "absent"; it is returned rather than guessed so a caller renders nothing
 * instead of a value the workspace never chose.
 */
export function parseComputePreference(raw: unknown): ComputePreference | null {
  return typeof raw === 'string' && (KNOWN as readonly string[]).includes(raw)
    ? (raw as ComputePreference)
    : null;
}

/**
 * The preference to SHOW when the API did not send one.
 *
 * `Auto` is the honest answer: it is the column's own default, the value
 * `DbComputeEntitlements` falls back to when it cannot read the organization row,
 * and what the Go handler reports for a row written before the column was set.
 */
export function computePreferenceOrDefault(raw: unknown): ComputePreference {
  return parseComputePreference(raw) ?? 'Auto';
}

export interface ComputePreferenceOption {
  value: ComputePreference;
  label: string;
  /** What the engine does under this value. No claim beyond `ComputeTargetSelector`'s own arms. */
  description: string;
  /** Shown as a warning under the option, when choosing it has a consequence worth reading first. */
  warning?: string;
}

/** How many paired machines a workspace has, and how many were heard from recently. */
export interface RunnerAvailability {
  paired: number;
  online: number;
}

/** The one neutral sentence that sits above the choices. */
export const COMPUTE_ROUTING_NOTE = enT('libLabels.compute.routingNote');

/** What the setting covers: media work only. */
export const COMPUTE_SCOPE_NOTE = enT('libLabels.compute.scopeNote');

/** The four choices, in display order, with the engine's own arms written out. */
const PREFERENCE_VALUES = ['Auto', 'CloudOnly', 'PreferRunner', 'RunnerOnly'] as const;

/**
 * The four choices in the active language. Pass the component's `useT()`; the English constants below
 * (and the three notes above) are for callers that have no translator. Catalogue: `libLabels.compute.*`.
 */
export function computePreferenceOptions(t: T): readonly ComputePreferenceOption[] {
  return PREFERENCE_VALUES.map((value) => {
    const base = `libLabels.compute.options.${value}`;
    return {
      value,
      label: t(`${base}.label`),
      description: t(`${base}.description`),
      ...(t.has(`${base}.warning`) ? { warning: t(`${base}.warning`) } : {}),
    };
  });
}

/** The four choices, in display order, with the engine's own arms written out (English). */
export const COMPUTE_PREFERENCE_OPTIONS: readonly ComputePreferenceOption[] = computePreferenceOptions(enT);

/** The label for a preference value, for a read-only view or a notification. */
export function computePreferenceLabel(value: unknown, t: T = enT): string {
  const preference = computePreferenceOrDefault(value);
  return computePreferenceOptions(t).find((option) => option.value === preference)?.label ?? preference;
}

/**
 * What the workspace's paired machines mean for the choice being made.
 *
 * `online` is the E7-UI heartbeat inference (a device heard from within
 * `RUNNER_ACTIVE_WINDOW_MS`), never a live presence check — the gateway's
 * authoritative answer is a per-replica question the dashboard cannot ask — so the
 * sentence says "heard from".
 */
export function runnerAvailabilityNote(availability: RunnerAvailability, t: T = enT): string {
  const { paired, online } = availability;
  if (paired === 0) return t('libLabels.compute.availNone');
  if (online === 0) return t('libLabels.compute.availOffline', { paired });
  return t('libLabels.compute.availOnline', { paired, online });
}
