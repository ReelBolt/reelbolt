import type { T } from '@/i18n/translator';
import { enT, labelOf } from '@/lib/i18n-default';
import type { AppManifest, AppManifestInput } from '../types/workflow';

/** Mirrors `AppManifestVocabulary` (inference/src/ReelBolt.Shared/Workflows/Apps/AppManifest.cs). */
export const MAX_APP_INPUTS = 8;
export const MAX_APP_LABEL = 40;

/** The run-brief fields an app may expose, with the plain words the derived manifest uses. */
const BRIEF_FIELD_KEYS = ['language', 'sourceLanguage', 'lengthSec', 'format', 'resolution', 'stylePreset', 'tone', 'designSystem', 'voice', 'avatar'] as const;

/**
 * English labels (catalogue: `libLabels.appBriefFields`); components use `briefFieldLabel(t, field)`.
 * @deprecated Use `briefFieldLabel(t, field)` (translator-taking) so the text follows the active language.
 */
export const APP_BRIEF_FIELDS: ReadonlyArray<{ field: string; label: string }> = BRIEF_FIELD_KEYS.map((field) => ({
  field,
  label: enT(`libLabels.appBriefFields.${field}`),
}));

/** The brief field's plain word in the active language (an unknown field shows as it is). */
export function briefFieldLabel(t: T, field: string): string {
  return labelOf(t, 'appBriefFields', field);
}

/**
 * The English words the server stores in `app_manifest_json` for a brief input (derived manifests and
 * the shipped templates), each with its catalogue key under `apps.briefLabels`. A stored label that is
 * one of these is a default, not something the author typed, so it follows the active language.
 */
const STORED_BRIEF_LABELS: Readonly<Record<string, string>> = {
  Language: 'apps.briefLabels.language',
  'Narration language': 'apps.briefLabels.narrationLanguage',
  'Spoken language': 'apps.briefLabels.spokenLanguage',
  'Source language': 'apps.briefLabels.sourceLanguage',
  Length: 'apps.briefLabels.length',
  Shape: 'apps.briefLabels.shape',
  Resolution: 'apps.briefLabels.resolution',
  Quality: 'apps.briefLabels.quality',
  Narration: 'apps.briefLabels.narration',
  Style: 'apps.briefLabels.style',
  Design: 'apps.briefLabels.design',
  Tone: 'apps.briefLabels.tone',
  'Under a full-screen picture': 'apps.briefLabels.overlayCardAudio',
};

/**
 * The label to show for an input: for a `brief` input whose stored label is a known English default,
 * its translation; otherwise the stored label (an author's own wording is never replaced); else undefined.
 */
export function inputLabel(t: T, input: AppManifestInput): string | undefined {
  const stored = input.label?.trim();
  if (!stored) return undefined;
  if (input.kind !== 'brief') return stored;
  const key = STORED_BRIEF_LABELS[stored];
  return key && t.has(key) ? t(key) : stored;
}

/** One row of the "App inputs" panel: an input and whether it is shown. */
export interface AppInputRow {
  key: string;
  input: AppManifestInput;
  shown: boolean;
}

/** What to call an input in a sentence: its label, else the field or role, else its kind. */
export function inputName(input: AppManifestInput, t: T = enT): string {
  const label = inputLabel(t, input);
  if (label) return label;
  if (input.kind === 'brief' && input.field) {
    return briefFieldLabel(t, input.field);
  }
  if (input.kind === 'files') return input.role ? t('libLabels.appInput.filesWithRole', { role: input.role }) : t('libLabels.appInput.files');
  if (input.kind === 'notes') return t('libLabels.appInput.notes');
  return input.kind;
}

export function rowsFromManifest(manifest: AppManifest): AppInputRow[] {
  return (manifest.inputs ?? []).map((input, i) => ({ key: `row-${i}`, input, shown: true }));
}

/** The inputs the author kept, in order: hidden rows are left out of the saved manifest. */
export function inputsFromRows(rows: AppInputRow[]): AppManifestInput[] {
  return rows.filter((r) => r.shown).map((r) => r.input);
}

export function moveRow(rows: AppInputRow[], index: number, direction: -1 | 1): AppInputRow[] {
  const target = index + direction;
  if (target < 0 || target >= rows.length) return rows;
  const next = [...rows];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

/** Brief fields not yet exposed (shown or hidden): the candidates to probe for "Add a field". */
export function addableBriefFields(rows: AppInputRow[]): string[] {
  const used = new Set(rows.filter((r) => r.input.kind === 'brief').map((r) => r.input.field));
  return APP_BRIEF_FIELDS.map((f) => f.field).filter((f) => !used.has(f));
}

/** A default typed into a text box, in the type the field validates as (`lengthSec` is a number). */
export function parseDefault(field: string | null | undefined, raw: string): AppManifestInput['default'] {
  const text = raw.trim();
  if (!text) return null;
  if (field === 'lengthSec') {
    const n = Number(text);
    return Number.isFinite(n) ? n : text;
  }
  return text;
}

export function manifestWithInputs(manifest: AppManifest, inputs: AppManifestInput[]): AppManifest {
  return { ...manifest, inputs };
}

/** The manifest-orphan refusal of the API (`APP_MANIFEST_INVALID`), with its messages. */
export function manifestErrorsOf(err: unknown): string[] | null {
  if (!(err instanceof Error)) return null;
  const e = err as Error & { code?: string | null; errors?: string[] };
  if (e.code !== 'APP_MANIFEST_INVALID') return null;
  return e.errors && e.errors.length > 0 ? e.errors : [e.message];
}

/** The app's cost class as the API sends it ("Uses credits"), in the active language; unknown values show as they are. */
export function appCostClassLabel(t: T, costClass: string): string {
  return labelOf(t, 'appCostClass', costClass, costClass);
}
