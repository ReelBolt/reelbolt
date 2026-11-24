import type { RunBriefChange, StepChangeAppInputField, StepChangeAppInputs, StepChangeBriefField } from '@/lib/types/change-requests';
import { isEmptyValue, type InputValue, type InputValues } from './app-input-model';

/**
 * "Change one thing and re-run" as a change request (docs/design/app-inputs.md 25): the fields of a finished app run, the
 * values the person changed, and whether a change request can make it at all.
 */

/** The run's own value of each field, as the form edits it. */
export function runValues(fields: readonly StepChangeAppInputField[]): InputValues {
  const values: InputValues = {};
  for (const field of fields) values[field.inputId] = field.value ?? null;
  return values;
}

/** Two values are the same when both are empty or they are equal. */
function same(a: InputValue | undefined, b: InputValue | undefined): boolean {
  if (isEmptyValue(a) && isEmptyValue(b)) return true;
  return a === b;
}

/**
 * The body's `inputs`: only the fields the person changed, text trimmed, an emptied field sent as null (it clears an
 * optional field; a required one is refused by the server with APP_INPUT_MISSING). Empty when nothing changed.
 */
export function changedInputs(fields: readonly StepChangeAppInputField[], values: InputValues): Record<string, InputValue> {
  const changed: Record<string, InputValue> = {};
  for (const field of fields) {
    if (!field.supported) continue;
    let value = values[field.inputId];
    if (typeof value === 'string') value = value.trim();
    if (same(value, field.value)) continue;
    changed[field.inputId] = isEmptyValue(value) ? null : (value ?? null);
  }
  return changed;
}

/** True when this value would pick another branch of the app (the server refuses it with INPUT_CHANGES_BRANCH). */
export function picksAnotherBranch(field: StepChangeAppInputField, value: InputValue | undefined): boolean {
  return (field.branchChangingValues ?? []).some((v) => v === value);
}

/** True when the run can be changed field by field: an app run with at least one field a change request can change. */
export function canChangeFields(appInputs: StepChangeAppInputs | null | undefined): boolean {
  return (appInputs?.fields ?? []).some((f) => f.supported) || (appInputs?.briefFields ?? []).some((f) => f.supported);
}

// --------------------------------------------------------------------------- run settings (docs/design/app-inputs.md 29)

/** The value of each run setting as the form edits it, by key: the run's own (a language code, seconds, a voice, file ids, notes). */
export type SettingValues = Record<string, unknown>;

/** The run's own value of each run setting, by key. */
export function runSettingValues(fields: readonly StepChangeBriefField[] | null | undefined): SettingValues {
  const values: SettingValues = {};
  for (const field of fields ?? []) values[field.key] = field.value ?? null;
  return values;
}

function sameSetting(a: unknown, b: unknown): boolean {
  const norm = (v: unknown) => (typeof v === 'string' ? v.trim() : v ?? null);
  return JSON.stringify(norm(a)) === JSON.stringify(norm(b));
}

/**
 * The body's `brief`: only the settings the person changed, in the brief's own shape (a language `auto` clears it, notes
 * trimmed, a files slot as its role's ids). Empty when nothing changed.
 */
export function changedBrief(fields: readonly StepChangeBriefField[] | null | undefined, values: SettingValues): RunBriefChange {
  const brief: RunBriefChange = {};
  for (const field of fields ?? []) {
    if (!field.supported || !(field.key in values)) continue;
    const value = values[field.key];
    if (sameSetting(value, field.value)) continue;
    if (field.kind === 'notes') brief.notes = typeof value === 'string' ? value.trim() : '';
    else if (field.kind === 'files' && field.role && Array.isArray(value) && value.length > 0) {
      brief.sourceFiles = [...(brief.sourceFiles ?? []), { role: field.role, fileIds: value as string[] }];
    } else if (field.kind === 'brief' && field.field && value != null) {
      (brief as Record<string, unknown>)[field.field] = value;
    }
  }
  return brief;
}

/** True when the brief sets anything. */
export const hasBriefChange = (brief: RunBriefChange): boolean => Object.keys(brief).length > 0;
