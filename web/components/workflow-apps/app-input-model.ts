import { msg, type Msg } from '@/i18n/msg';
import type { AppManifest, AppManifestInput, AppInputType, RunBrief } from '@/lib/types/workflow';

/**
 * The app inputs on the run page (docs/design/app-inputs.md 4, 8.2, 12 and 21): which inputs are shown and which sit
 * under "More options", the values the page holds for `input` inputs, what is sent as `brief.inputs`, and the plain
 * words a model card shows. Pure: the page and its tests share it.
 */

/** At most this many inputs are shown before "More options" (4.3). */
export const MAX_SHOWN_INPUTS = 8;
/** At most this many inputs in all (4.3). */
export const MAX_TOTAL_INPUTS = 32;

/** Mirrors `AppInputPlacement.BriefFieldsUnderMore` (inference/src/ReelBolt.Shared/Workflows/Apps/AppManifest.cs). */
const BRIEF_FIELDS_UNDER_MORE = new Set(['resolution', 'tone', 'designSystem', 'overlayCardAudio', 'stylePreset', 'sourceLanguage']);

const INPUT_TYPES: readonly AppInputType[] = ['shortText', 'longText', 'number', 'duration', 'choice', 'toggle', 'shape', 'model'];

/** The value of one `input` input on the page. A model is its capability id; null = nothing chosen or typed. */
export type InputValue = string | number | boolean | null;
export type InputValues = Record<string, InputValue>;

/** An `input` input of a known type, with its id. */
export function isAppInput(input: AppManifestInput): input is AppManifestInput & { id: string; type: AppInputType } {
  return input.kind === 'input' && typeof input.id === 'string' && input.id !== '' && INPUT_TYPES.includes(input.type as AppInputType);
}

/** The id and the marker of the one field the app adds itself: the permission to send media to AI services. */
export const MEDIA_EGRESS_INPUT_ID = 'allowMediaEgress';
export const MEDIA_EGRESS_PERMISSION = 'mediaEgress';

/** True for the app's own permission field (derived on the server; never edited, bound or removed by hand). */
export function isPermissionInput(input: AppManifestInput): input is AppManifestInput & { id: string; type: 'toggle' } {
  return input.kind === 'input' && input.permission === MEDIA_EGRESS_PERMISSION && input.id === MEDIA_EGRESS_INPUT_ID;
}

/** A manifest without the app's own permission field: what a save sends (the server derives the field on every read). */
export function withoutPermission<M extends { inputs?: AppManifestInput[] | null }>(manifest: M): M;
export function withoutPermission<M extends { inputs?: AppManifestInput[] | null }>(manifest: M | null | undefined): M | null | undefined;
export function withoutPermission<M extends { inputs?: AppManifestInput[] | null }>(manifest: M | null | undefined): M | null | undefined {
  return manifest?.inputs?.some(isPermissionInput) ? { ...manifest, inputs: manifest.inputs.filter((i) => !isPermissionInput(i)) } : manifest;
}

/** Mirrors `AppInputPlacement.IsUnderMore`: the creator's `more`, else models and fine-tuning brief fields go under it. */
export function isUnderMore(input: AppManifestInput): boolean {
  if (typeof input.more === 'boolean') return input.more;
  if (input.kind === 'brief') return Boolean(input.field && BRIEF_FIELDS_UNDER_MORE.has(input.field));
  if (input.kind === 'input') return input.type === 'model';
  return false;
}

export interface PageLayout {
  /** The inputs shown at once, in the manifest's order. */
  shown: AppManifestInput[];
  /** The inputs under "More options", in the manifest's order. */
  more: AppManifestInput[];
  /** Every input the page renders (shown, then more). */
  all: AppManifestInput[];
  /** The permission to send media: shown in its own block above Run, never among the fields or under "More options". */
  permission: (AppManifestInput & { id: string }) | null;
}

/**
 * The inputs the page renders: up to 32 inputs, the creator's essentials shown (at most 8; any beyond move under
 * "More options"), the rest under "More options".
 */
export function pageLayout(manifest: AppManifest | null | undefined): PageLayout {
  const every = manifest?.inputs ?? [];
  const permission = every.find(isPermissionInput) ?? null;
  const inputs = every.filter((i) => !isPermissionInput(i));
  const all = inputs.filter((i) => i.kind !== 'input' || isAppInput(i)).slice(0, MAX_TOTAL_INPUTS);
  const shown: AppManifestInput[] = [];
  const more: AppManifestInput[] = [];
  for (const input of all) {
    if (!isUnderMore(input) && shown.length < MAX_SHOWN_INPUTS) shown.push(input);
    else more.push(input);
  }
  return { shown, more, all: [...shown, ...more, ...(permission ? [permission] : [])], permission };
}

function typedDefault(input: AppManifestInput): InputValue {
  const value = input.default;
  if (value === undefined || value === null) return null;
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') return value;
  return null;
}

/** The value an input starts with: its default; a switch with none is off; anything else starts empty. */
export function initialValue(input: AppManifestInput): InputValue {
  const value = typedDefault(input);
  if (input.type === 'toggle') return value === true;
  if ((input.type === 'number' || input.type === 'duration') && typeof value === 'string') {
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }
  return value;
}

export function initialValues(inputs: readonly AppManifestInput[]): InputValues {
  const values: InputValues = {};
  for (const input of inputs) if (isAppInput(input)) values[input.id] = initialValue(input);
  return values;
}

/** `showWhen` (4.4): shown while the named switch or choice has the value; everything else is always shown. */
export function isVisible(input: AppManifestInput, values: InputValues): boolean {
  const when = input.showWhen;
  if (!when) return true;
  const current = values[when.input];
  if (typeof when.equals === 'boolean') return (current === true) === when.equals;
  return current !== null && current !== undefined && String(current) === String(when.equals);
}

/** True when the value counts as not filled in (empty or blank text, nothing chosen). */
export function isEmptyValue(value: InputValue | undefined): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.trim() === '');
}

/**
 * `brief.inputs`: the values of the visible `input` inputs that hold something, text trimmed. Hidden inputs are left
 * out (the server drops them too); undefined when there is nothing to send.
 */
export function briefInputs(inputs: readonly AppManifestInput[], values: InputValues): RunBrief['inputs'] | undefined {
  const out: Record<string, string | number | boolean> = {};
  for (const input of inputs) {
    if (!isAppInput(input) || !isVisible(input, values)) continue;
    const value = values[input.id];
    if (isEmptyValue(value)) continue;
    // A permission is only ever sent as a grant: "off" is the absence of the value.
    if (isPermissionInput(input) && value !== true) continue;
    out[input.id] = typeof value === 'string' ? value.trim() : (value as number | boolean);
  }
  return Object.keys(out).length > 0 ? out : undefined;
}

/** The first visible required input with nothing in it, if any. */
export function firstMissing(inputs: readonly AppManifestInput[], values: InputValues): (AppManifestInput & { id: string }) | null {
  for (const input of inputs) {
    if (!isAppInput(input) || !input.required || !isVisible(input, values)) continue;
    // A required permission is missing until it is switched on.
    if (isPermissionInput(input) ? values[input.id] !== true : isEmptyValue(values[input.id])) return input;
  }
  return null;
}

/**
 * The part of the values that can move the price (8.6): every value except free text. Changing a model, a length, a
 * shape, a number or a branch switch re-prices; typing does not.
 */
export function pricingValues(inputs: readonly AppManifestInput[], values: InputValues): RunBrief['inputs'] | undefined {
  const priced = inputs.filter((i) => i.type !== 'shortText' && i.type !== 'longText');
  return briefInputs(priced, values);
}

/** The id the page gives the control of an `input` input (focus, labels, field errors). */
export function inputFieldId(inputId: string): string {
  return `app-input-in-${inputId}`;
}

/** The values a previous run used, for "Change one thing and re-run": only ids this app still declares. */
export function valuesFromBrief(inputs: readonly AppManifestInput[], previous: RunBrief['inputs'] | null | undefined, current: InputValues): InputValues {
  const next: InputValues = { ...current };
  for (const input of inputs) {
    if (!isAppInput(input)) continue;
    const value = previous?.[input.id];
    // A permission is never carried over from an earlier run: each run is asked again.
    next[input.id] = value === undefined || isPermissionInput(input) ? initialValue(input) : value;
  }
  return next;
}

// --------------------------------------------------------------------------- model cards

/** One option of `GET .../app/inputs/{inputId}/options` (docs/design/app-inputs.md 8.2). */
export interface ModelOption {
  id: string;
  label: string;
  note?: string | null;
  /** A ratio to the recommended option for a fixed reference unit; never money. Null when it cannot be priced. */
  costIndex?: number | null;
  recommended: boolean;
  available: boolean;
  unavailableReason?: string | null;
  /** `notInWorkspace`, `wrongKind`, `notAllowed`, `wrongJob`, `notInPlan`. */
  unavailableCode?: string | null;
  /** With `notInPlan`: the plan feature the model needs (`video_generation`, `audio_generation`, `avatars`, `resolution`). */
  planFeature?: string | null;
}

export interface ModelOptionsResponse {
  inputId: string;
  capability: string;
  job?: string | null;
  options: ModelOption[];
  /** The field's own label (and a recipe's catalogue key), so a list of several fields names each one. */
  label?: string | null;
  labelKey?: string | null;
}

/** `GET .../workflow-apps/recipes/{key}/model-options`: a recipe's model fields before it is created (docs/design/app-inputs.md 28). */
export function recipeModelOptionsPath(projectId: string, recipeKey: string): string {
  return `/api/v1/projects/${projectId}/workflow-apps/recipes/${encodeURIComponent(recipeKey)}/model-options`;
}

const UNAVAILABLE_CODES = new Set(['notInWorkspace', 'wrongKind', 'notAllowed', 'wrongJob']);
const PLAN_FEATURES = new Set(['video_generation', 'audio_generation', 'avatars', 'avatar_objects', 'resolution']);

/**
 * Why a model cannot be chosen, in the page's own words (an unknown code reads as "cannot be used here"). A model the plan
 * does not include says which part of the plan it needs (docs/design/app-inputs.md 28).
 */
export function unavailableMessage(option: Pick<ModelOption, 'unavailableCode' | 'planFeature'>): Msg {
  if (option.unavailableCode === 'notInPlan') {
    const feature = option.planFeature && PLAN_FEATURES.has(option.planFeature) ? option.planFeature : 'other';
    return msg(`apps.run.inputs.unavailable.plan.${feature}`);
  }
  const code = option.unavailableCode && UNAVAILABLE_CODES.has(option.unavailableCode) ? option.unavailableCode : 'other';
  return msg(`apps.run.inputs.unavailable.${code}`);
}

/**
 * The relative price of a model in words (8.2, 12.1), never a number of credits or money: the cheapest of several,
 * cheaper, about the same, or about N times the price of the recommended one. Null when the price is not known.
 */
export function costWords(option: Pick<ModelOption, 'costIndex'>, options: readonly Pick<ModelOption, 'costIndex'>[]): Msg | null {
  const index = option.costIndex;
  if (typeof index !== 'number' || !Number.isFinite(index) || index <= 0) return null;
  const priced = options.map((o) => o.costIndex).filter((v): v is number => typeof v === 'number' && Number.isFinite(v) && v > 0);
  const cheapest = Math.min(...priced);
  const distinct = new Set(priced.map((v) => Math.round(v * 100))).size > 1;
  if (distinct && index === cheapest && index < 0.9) return msg('apps.run.inputs.cost.cheapest');
  if (index < 0.8) return msg('apps.run.inputs.cost.cheaper');
  if (index < 1.4) return msg('apps.run.inputs.cost.same');
  // Halves up to 3x ("About 1.5x"), whole numbers above, so the word stays a rough guide.
  const times = index < 3 ? Math.round(index * 2) / 2 : Math.round(index);
  return msg('apps.run.inputs.cost.times', { times });
}

// --------------------------------------------------------------------------- run-gate refusals

/** The app-input refusal of a run start (`400 { code, message, inputId }`), placed next to its field. */
export interface FieldRefusal {
  inputId: string;
  code: string;
  /**
   * True when Retry or Resume was refused (docs/design/app-inputs.md 29, the L5 narrowing): the value is the run's own, which
   * the form takes back, so the person changes it there and runs the app again.
   */
  fromRun?: boolean;
}

const REFUSAL_CODES = new Set(['APP_INPUT_MISSING', 'APP_INPUT_INVALID', 'APP_INPUT_MODEL_UNAVAILABLE', 'APP_INPUT_MODEL_NOT_IN_PLAN']);

/** The refusal an error carries, when it is an app-input refusal that names a field. */
export function fieldRefusalOf(body: unknown, code: string | null | undefined): FieldRefusal | null {
  if (!code || !REFUSAL_CODES.has(code)) return null;
  const inputId = typeof body === 'object' && body !== null ? (body as { inputId?: unknown }).inputId : null;
  return typeof inputId === 'string' && inputId !== '' ? { inputId, code } : null;
}

/** True for any of the app-input refusal codes. */
export function isAppInputRefusal(code: string | null | undefined): boolean {
  return Boolean(code && REFUSAL_CODES.has(code));
}

/** The sentence under the field for a refusal code. */
export function refusalMessage(code: string): Msg {
  switch (code) {
    case 'APP_INPUT_MISSING':
      return msg('apps.run.inputs.refusal.missing');
    case 'APP_INPUT_MODEL_UNAVAILABLE':
      return msg('apps.run.inputs.refusal.model');
    case 'APP_INPUT_MODEL_NOT_IN_PLAN':
      return msg('apps.run.inputs.refusal.plan');
    default:
      return msg('apps.run.inputs.refusal.invalid');
  }
}
