import type { StepData } from '@/components/workflows/WorkflowStepList';
import type { AppBindableField, AppInputType, AppManifest, AppManifestInput, StepType } from '@/lib/types/workflow';

/**
 * The builder's side of app inputs (docs/design/app-inputs.md 13.1 and 24): pure edits of the draft manifest and of the
 * draft steps' configs, mirroring what the server's binder and the assistant's patch operations do
 * (`AppInputBinder`, `AssistantWorkflowPatchTools.AppInputs`). The server stays the judge: every save is validated there.
 */

/** `{{input.` opens a placeholder (`AppInputPlaceholders.Opening`). */
export const PLACEHOLDER_OPENING = '{{input.';
/** The inert config key of whole-value bindings (`AppInputPlaceholders.AppInputsKey`). */
export const APP_INPUTS_KEY = 'appInputs';
/** The pseudo-path of a step's model (`AppBindableCatalog.ModelKey`). */
export const MODEL_PATH = '@model';
/** The pseudo-path of a talking head's picture model (`AppBindableCatalog.StillModelKey`, an ImageEdit model). */
export const STILL_MODEL_PATH = '@stillModel';
/** The pseudo-path of a VideoAnalyze step's picture model (`AppBindableCatalog.VisionModelKey`, a Vision model). */
export const VISION_MODEL_PATH = '@visionModel';
/** The pseudo-path of a Voiceover step's own words (`AppBindableCatalog.SpokenTextKey`): one spoken line per paragraph. */
export const SPOKEN_TEXT_PATH = '@spokenText';

/** True for a setting a `model` input fills (`@model`, `@stillModel`). */
export function isModelPath(field: AppBindableField): boolean {
  return field.path.startsWith('@') && Boolean(field.capabilities && field.capabilities.length > 0);
}
/** The branch token that finishes the run (docs/design/app-inputs.md 9.4). */
export const END_BRANCH = 'end';

/** Mirrors `AppInputTypes` caps. */
export const TEXT_CAPS = { shortText: 200, longText: 7000 } as const;
export const MAX_HELP = 160;
export const MAX_EXAMPLE = 300;
export const MAX_OPTIONS = 12;
export const MAX_OPTION_LABEL = 40;
export const MAX_REQUEST_TEMPLATE = 1000;
export const MAX_ALLOWED_MODELS = 12;
export const SHAPES = ['9:16', '1:1', '16:9', '4:5'] as const;
export const NUMBER_UNITS = ['seconds', 'takes', 'percent'] as const;
/** The types an author can add, in the order the menu offers them. */
export const ADDABLE_TYPES: readonly AppInputType[] = ['shortText', 'longText', 'choice', 'toggle', 'number', 'duration', 'shape', 'model'];

/** Ids an input may not take (`AppManifestVocabulary.BriefFields` plus the reserved words). */
const RESERVED_IDS = new Set([
  'language', 'sourceLanguage', 'lengthSec', 'format', 'resolution', 'stylePreset', 'tone', 'designSystem', 'voice',
  'overlayCardAudio', 'avatar', 'notes', 'files', 'inputs',
]);
const ID_PATTERN = /^[a-z][A-Za-z0-9]{0,31}$/;
const PLACEHOLDER_RE = /\{\{input\.([a-z][A-Za-z0-9]{0,31})\}\}/g;
const CONDITION_RE = /\binput\.([a-z][A-Za-z0-9]{0,31})\b/g;

type Config = Record<string, unknown>;
type StepConfigKey =
  | 'videoGenerateConfig' | 'audioGenerateConfig' | 'voiceoverConfig' | 'thumbnailGenerateConfig' | 'videoCompileConfig'
  | 'videoAnalyzeConfig' | 'extractFrameConfig' | 'audioCleanupConfig';

/** Where a step type keeps the config an app field can fill, on the builder's `StepData`. */
const CONFIG_KEY: Partial<Record<StepType, StepConfigKey>> = {
  VideoGenerate: 'videoGenerateConfig',
  AudioGenerate: 'audioGenerateConfig',
  Voiceover: 'voiceoverConfig',
  ThumbnailGenerate: 'thumbnailGenerateConfig',
  VideoCompile: 'videoCompileConfig',
  VideoAnalyze: 'videoAnalyzeConfig',
  ExtractFrame: 'extractFrameConfig',
  AudioCleanup: 'audioCleanupConfig',
};

function clone<T>(value: T): T {
  return value === undefined ? value : (JSON.parse(JSON.stringify(value)) as T);
}

/** The step's config as a plain object (null when the type keeps none here or the step has none yet). */
export function configOf(step: StepData): Config | null {
  const key = CONFIG_KEY[step.stepType];
  if (!key) return null;
  const config = step[key] as unknown;
  return config && typeof config === 'object' ? (config as Config) : null;
}

/** The update that stores `config` on the step. */
export function withConfig(step: StepData, config: Config): Partial<StepData> {
  const key = CONFIG_KEY[step.stepType];
  return key ? ({ [key]: config } as Partial<StepData>) : {};
}

export function placeholder(id: string): string {
  return `${PLACEHOLDER_OPENING}${id}}}`;
}

/** The input ids a text names through placeholders, in order, without repeats. */
export function placeholderIds(text: string | null | undefined): string[] {
  const ids: string[] = [];
  for (const m of (text ?? '').matchAll(PLACEHOLDER_RE)) if (!ids.includes(m[1])) ids.push(m[1]);
  return ids;
}

/** The input ids a condition reads as `input.<id>`. */
export function conditionIds(expression: string | null | undefined): string[] {
  const ids: string[] = [];
  for (const m of (expression ?? '').matchAll(CONDITION_RE)) if (!ids.includes(m[1])) ids.push(m[1]);
  return ids;
}

// --------------------------------------------------------------------------- paths

type Segment = { name: string; index: number | null; each: boolean };

function segments(path: string): Segment[] {
  return path.split('.').map((part) => {
    const m = /^([A-Za-z_][A-Za-z0-9_]*)(?:\[(\d*)\])?$/.exec(part);
    if (!m) return { name: part, index: null, each: false };
    const bracket = part.includes('[');
    return { name: m[1], index: m[2] ? Number(m[2]) : null, each: bracket && !m[2] };
  });
}

/**
 * A bindable path's concrete paths in this config: `trackObjects[].label` becomes `trackObjects[0].label`, ... for
 * each item the step has (none when it has none); a plain path is itself.
 */
export function concretePaths(config: Config | null, path: string): string[] {
  if (!path.includes('[]')) return [path];
  let paths = [''];
  let nodes: unknown[] = [config ?? {}];
  for (const seg of segments(path)) {
    const nextPaths: string[] = [];
    const nextNodes: unknown[] = [];
    nodes.forEach((node, i) => {
      const prefix = paths[i] ? `${paths[i]}.` : '';
      const value = node && typeof node === 'object' ? (node as Config)[seg.name] : undefined;
      if (seg.each) {
        if (Array.isArray(value)) value.forEach((item, k) => { nextPaths.push(`${prefix}${seg.name}[${k}]`); nextNodes.push(item); });
      } else {
        nextPaths.push(`${prefix}${seg.name}`);
        nextNodes.push(value);
      }
    });
    paths = nextPaths;
    nodes = nextNodes;
  }
  return paths;
}

/** The index of the item a concrete path names (`regionEffects[1].target` is 1), or null. */
export function itemIndexOf(concrete: string): number | null {
  const m = /\[(\d+)\]/.exec(concrete);
  return m ? Number(m[1]) : null;
}

/** The value at a concrete path (`a.b[0].c`), or undefined. */
export function getPath(config: Config | null, path: string): unknown {
  let node: unknown = config;
  for (const seg of segments(path)) {
    if (!node || typeof node !== 'object') return undefined;
    node = (node as Config)[seg.name];
    if (seg.index !== null) node = Array.isArray(node) ? node[seg.index] : undefined;
  }
  return node;
}

/** A copy of the config with a concrete path set (objects created on the way; an index must name an existing item). */
export function setPath(config: Config | null, path: string, value: unknown): Config {
  const root = clone(config ?? {});
  const segs = segments(path);
  let node: Config = root;
  segs.forEach((seg, i) => {
    const last = i === segs.length - 1;
    if (seg.index !== null) {
      const list = node[seg.name];
      if (!Array.isArray(list) || seg.index >= list.length) return;
      if (last) list[seg.index] = value;
      else {
        if (!list[seg.index] || typeof list[seg.index] !== 'object') list[seg.index] = {};
        node = list[seg.index] as Config;
      }
      return;
    }
    if (last) node[seg.name] = value;
    else {
      if (!node[seg.name] || typeof node[seg.name] !== 'object') node[seg.name] = {};
      node = node[seg.name] as Config;
    }
  });
  return root;
}

// --------------------------------------------------------------------------- whole-value bindings

/**
 * One `appInputs` entry (`AppValueBinding`): the input id, or `{input, values}` where a switch or a choice lists what each
 * of its values writes into the setting (keys `true`/`false` or the option values; a value not listed keeps the step's own).
 */
export type AppInputsEntry = string | { input: string; values: Record<string, string | number | boolean> };

/** The step's raw `appInputs` map (setting path to entry). */
export function appInputsEntries(config: Config | null): Record<string, AppInputsEntry> {
  const map = config?.[APP_INPUTS_KEY];
  if (!map || typeof map !== 'object' || Array.isArray(map)) return {};
  const out: Record<string, AppInputsEntry> = {};
  for (const [k, v] of Object.entries(map as Config)) {
    if (typeof v === 'string') out[k] = v;
    else if (v && typeof v === 'object' && typeof (v as Config).input === 'string') {
      const values = (v as Config).values;
      out[k] = { input: (v as Config).input as string, values: values && typeof values === 'object' ? clone(values as Record<string, string | number | boolean>) : {} };
    }
  }
  return out;
}

/** The input id of an entry. */
export function entryInput(entry: AppInputsEntry | undefined): string | undefined {
  return entry === undefined ? undefined : typeof entry === 'string' ? entry : entry.input;
}

/** The step's `appInputs` map as setting path to input id (mapped entries included). */
export function appInputsOf(config: Config | null): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(appInputsEntries(config))) out[k] = entryInput(v)!;
  return out;
}

/** The values a mapped entry lists, or null for a plain one. */
export function entryValues(config: Config | null, path: string): Record<string, string | number | boolean> | null {
  const entry = appInputsEntries(config)[path];
  return entry && typeof entry === 'object' ? entry.values : null;
}

/** Binds `path` to `inputId`; with `values` (non-empty) the entry lists what each of the input's values writes. */
export function bindValue(config: Config | null, path: string, inputId: string, values?: Record<string, string | number | boolean> | null): Config {
  const next = clone(config ?? {});
  const entry: AppInputsEntry = values && Object.keys(values).length > 0 ? { input: inputId, values: { ...values } } : inputId;
  next[APP_INPUTS_KEY] = { ...appInputsEntries(config), [path]: entry };
  return next;
}

export function unbindValue(config: Config | null, path: string): Config {
  const next = clone(config ?? {});
  const map = appInputsEntries(config);
  delete map[path];
  if (Object.keys(map).length === 0) delete next[APP_INPUTS_KEY];
  else next[APP_INPUTS_KEY] = map;
  return next;
}

/** The input values a mapped binding can list for `input`: `true`/`false` for a switch, the option values for a choice. */
export function mappableKeys(input: AppManifestInput): { key: string; value: unknown }[] {
  if (input.type === 'toggle') return [{ key: 'true', value: true }, { key: 'false', value: false }];
  if (input.type === 'choice') return (input.options ?? []).map((o) => ({ key: String(o.value), value: o.value }));
  return [];
}

/** True when a switch or a choice may fill `field` by listing a value per answer (never a model, the words or a text). */
export function canMap(field: AppBindableField): boolean {
  return field.mode === 'value' && !field.path.startsWith('@');
}

/** The value typed for the setting from a builder control's text (`true`, a number, a word), or null when it does not fit. */
export function settingValue(field: AppBindableField, text: string): string | number | boolean | null {
  const kind = field.valueKind ?? 'text';
  if (text === '') return null;
  if (kind === 'boolean') return text === 'true' ? true : text === 'false' ? false : null;
  if (kind === 'number') {
    const n = Number(text);
    if (!Number.isFinite(n)) return null;
    if ((field.min ?? null) !== null && n < (field.min as number)) return null;
    if ((field.max ?? null) !== null && n > (field.max as number)) return null;
    return n;
  }
  if (field.choices && field.choices.length > 0) return field.choices.find((c) => c.toLowerCase() === text.toLowerCase()) ?? null;
  return text;
}

/** Replaces every `{{input.id}}` in the config's text (not the appInputs map) with `fill`. */
export function replacePlaceholders(value: unknown, id: string, fill: string): unknown {
  const token = placeholder(id);
  if (typeof value === 'string') return value.split(token).join(fill);
  if (Array.isArray(value)) return value.map((v) => replacePlaceholders(v, id, fill));
  if (value && typeof value === 'object') {
    const out: Config = {};
    for (const [k, v] of Object.entries(value as Config)) out[k] = k === APP_INPUTS_KEY ? v : replacePlaceholders(v, id, fill);
    return out;
  }
  return value;
}

// --------------------------------------------------------------------------- where each input is used

export type InputUseKind = 'placeholder' | 'value' | 'condition' | 'request' | 'agent';

export interface InputUse {
  kind: InputUseKind;
  /** 0-based index of the step (absent for the request template). */
  stepIndex?: number;
  /** The setting's label (bindable label), when the use is a setting. */
  setting?: string;
}

function walkStrings(value: unknown, path: string, visit: (path: string, text: string) => void) {
  if (typeof value === 'string') visit(path, value);
  else if (Array.isArray(value)) value.forEach((v, i) => walkStrings(v, `${path}[${i}]`, visit));
  else if (value && typeof value === 'object') {
    for (const [k, v] of Object.entries(value as Config)) {
      if (k === APP_INPUTS_KEY) continue;
      walkStrings(v, path ? `${path}.${k}` : k, visit);
    }
  }
}

/** A concrete path's pattern (`a[0].b` is `a[].b`), how bindable fields name it. */
export function patternOf(concrete: string): string {
  return concrete.replace(/\[\d+\]/g, '[]');
}

/** Every use of every input: placeholders and value bindings in the steps, conditions, the request template, agents. */
export function inputUses(
  manifest: AppManifest,
  steps: readonly StepData[],
  bindable: Record<string, AppBindableField[]> | null | undefined,
): Map<string, InputUse[]> {
  const uses = new Map<string, InputUse[]>();
  const add = (id: string, use: InputUse) => uses.set(id, [...(uses.get(id) ?? []), use]);
  const labelFor = (step: StepData, path: string) =>
    bindable?.[step.stepType]?.find((f) => f.path === patternOf(path))?.label ?? path;
  steps.forEach((step, stepIndex) => {
    const config = configOf(step);
    if (config) {
      walkStrings(config, '', (path, text) => {
        for (const id of placeholderIds(text)) add(id, { kind: 'placeholder', stepIndex, setting: labelFor(step, path) });
      });
      for (const [path, id] of Object.entries(appInputsOf(config))) add(id, { kind: 'value', stepIndex, setting: labelFor(step, path) });
    }
    if (step.stepType === 'Conditional') for (const id of conditionIds(step.conditionExpression)) add(id, { kind: 'condition', stepIndex });
  });
  for (const id of placeholderIds(manifest.requestTemplate)) add(id, { kind: 'request' });
  for (const input of manifest.inputs ?? []) {
    if (input.kind !== 'input' || input.type !== 'model' || !input.agent || !input.id) continue;
    steps.forEach((step, stepIndex) => {
      if (step.stepType === 'Agent' && step.agentDefinitionId === input.agent) add(input.id!, { kind: 'agent', stepIndex });
    });
  }
  return uses;
}

// --------------------------------------------------------------------------- creating and editing inputs

/** True for an `input` input. */
export function isTyped(input: AppManifestInput): input is AppManifestInput & { id: string; type: AppInputType } {
  return input.kind === 'input' && typeof input.id === 'string' && typeof input.type === 'string';
}

/** A fresh id from a label: camelCase letters and digits, unique in the manifest, never a reserved word. */
export function newInputId(label: string, manifest: AppManifest): string {
  const words = label
    .normalize('NFKD')
    .replace(/[^\x00-\x7F]/g, '')
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean);
  let base = words.map((w, i) => (i === 0 ? w.toLowerCase() : w[0].toUpperCase() + w.slice(1).toLowerCase())).join('');
  if (!/^[a-z]/.test(base)) base = `field${base}`;
  base = base.slice(0, 28);
  const taken = new Set((manifest.inputs ?? []).map((i) => i.id).filter(Boolean));
  let id = base;
  for (let n = 2; RESERVED_IDS.has(id) || taken.has(id) || !ID_PATTERN.test(id); n++) id = `${base}${n}`;
  return id;
}

/** Makes the manifest a version 2 one (any app input or `more` needs it). */
export function asVersion2(manifest: AppManifest): AppManifest {
  return manifest.version >= 2 ? manifest : { ...manifest, version: 2 };
}

/**
 * The manifest's version after an edit: 2 while it uses anything of version 2 (an app input, a placement, a request
 * template), else what it was, so a page that only renames brief fields stays a version 1 page.
 */
export function settleVersion(manifest: AppManifest): AppManifest {
  const v2 = (manifest.inputs ?? []).some((i) => i.kind === 'input' || typeof i.more === 'boolean') || Boolean(manifest.requestTemplate);
  return v2 && manifest.version < 2 ? { ...manifest, version: 2 } : manifest;
}

/** Upsert by id (an `input`) or by index. */
export function upsertInput(manifest: AppManifest, input: AppManifestInput, index?: number): AppManifest {
  const inputs = [...(manifest.inputs ?? [])];
  const at = index ?? (input.kind === 'input' ? inputs.findIndex((i) => i.kind === 'input' && i.id === input.id) : -1);
  if (at >= 0 && at < inputs.length) inputs[at] = input;
  else inputs.push(input);
  return asVersion2({ ...manifest, inputs });
}

/** Moves the input at `from` to `to` (a drag in the builder, docs/design/app-inputs.md 29); out-of-range is a no-op. */
export function moveInputTo(manifest: AppManifest, from: number, to: number): AppManifest {
  const inputs = [...(manifest.inputs ?? [])];
  if (from === to || from < 0 || to < 0 || from >= inputs.length || to >= inputs.length) return manifest;
  const [moved] = inputs.splice(from, 1);
  inputs.splice(to, 0, moved);
  return { ...manifest, inputs };
}

/**
 * The app page as it is saved: the author's whole manifest when one is stored, else only what the author edits (its inputs
 * and request template), so the title and the rest keep following the steps as before.
 */
export function manifestForSave(manifest: AppManifest, hasExplicit: boolean): AppManifest {
  if (hasExplicit) return manifest;
  return {
    version: manifest.version,
    inputs: manifest.inputs,
    ...(manifest.requestTemplate ? { requestTemplate: manifest.requestTemplate } : {}),
  };
}

/**
 * The types a field may change to in place (docs/design/app-inputs.md 29, the L6 narrowing): only between kinds whose value
 * has the same shape, so every setting that uses it keeps reading a value it takes (text to text, number to number). Any other
 * change is a new field. Empty for a field whose type cannot change.
 */
export function compatibleTypes(type: AppInputType | null | undefined): AppInputType[] {
  switch (type) {
    case 'shortText':
    case 'longText':
      return ['shortText', 'longText'];
    case 'number':
    case 'duration':
      return ['number', 'duration'];
    default:
      return [];
  }
}

/** The field as the other type: limits fitted to it (a short text's cap, a duration's own range), id and bindings kept. */
export function changeType(input: AppManifestInput, type: AppInputType): AppManifestInput {
  if (input.kind !== 'input' || input.type === type || !compatibleTypes(input.type).includes(type)) return input;
  const next: AppManifestInput = { ...input, type };
  if (type === 'shortText' || type === 'longText') {
    const cap = TEXT_CAPS[type];
    next.maxLength = Math.min(input.maxLength ?? cap, cap);
    if (typeof next.default === 'string' && next.default.length > cap) next.default = next.default.slice(0, cap);
  }
  return normalizeInput(next);
}

export function moveInput(manifest: AppManifest, index: number, direction: -1 | 1): AppManifest {
  const inputs = [...(manifest.inputs ?? [])];
  const target = index + direction;
  if (target < 0 || target >= inputs.length) return manifest;
  [inputs[index], inputs[target]] = [inputs[target], inputs[index]];
  return { ...manifest, inputs };
}

const blank = (v: unknown) => v === undefined || v === null || (typeof v === 'string' && v.trim() === '');

/** Drops the members a type may not carry and empty text, so the save validator sees only what the type takes. */
export function normalizeInput(input: AppManifestInput): AppManifestInput {
  if (input.kind !== 'input') return input;
  const type = input.type;
  const text = type === 'shortText' || type === 'longText';
  const out: AppManifestInput = { ...input };
  const drop = (key: keyof AppManifestInput, when: boolean) => { if (when) delete out[key]; };
  drop('maxLength', !text);
  drop('example', !text);
  drop('min', type !== 'number' && type !== 'duration');
  drop('max', type !== 'number' && type !== 'duration');
  drop('step', type !== 'number');
  drop('unit', type !== 'number');
  drop('options', type !== 'choice');
  drop('choices', type !== 'duration' && type !== 'shape');
  drop('onLabel', type !== 'toggle');
  drop('offLabel', type !== 'toggle');
  for (const key of ['capability', 'job', 'allowed', 'recommended', 'agent'] as const) drop(key, type !== 'model');
  for (const key of ['help', 'example', 'onLabel', 'offLabel', 'label'] as const) if (blank(out[key])) delete out[key];
  if (blank(out.default)) delete out.default;
  if (out.allowed && out.allowed.length === 0) delete out.allowed;
  if (out.choices && out.choices.length === 0) delete out.choices;
  if (out.required !== true) delete out.required;
  return out;
}

/** The model kind a step's `@model` picks: an AudioGenerate step by its kind, else the setting's first. */
export function modelCapabilityFor(step: StepData, field: AppBindableField): string | null {
  if (step.stepType === 'AudioGenerate') {
    const kind = configOf(step)?.kind;
    return kind === 'SoundEffect' ? 'SoundEffectGeneration' : 'MusicGeneration';
  }
  return field.capabilities?.[0] ?? null;
}

/**
 * A new input that fills `field` of `step`, shaped from the setting (docs/design/app-inputs.md 13.1): its label, its
 * type, its limits and choices, the current value as the default (text becomes the example instead).
 */
export function inputForSetting(
  step: StepData,
  field: AppBindableField,
  concretePath: string,
  manifest: AppManifest,
  label: string,
): AppManifestInput {
  const config = configOf(step);
  const id = newInputId(label, manifest);
  const current = field.path.startsWith('@') ? undefined : getPath(config, concretePath);
  const base: AppManifestInput = { kind: 'input', id, label: label.slice(0, 40) };
  if (isModelPath(field)) {
    return { ...base, type: 'model', capability: modelCapabilityFor(step, field) };
  }
  if (field.path === SPOKEN_TEXT_PATH) {
    // "Speak exactly this": the words are spoken as written, one line per paragraph.
    return { ...base, type: 'longText', maxLength: Math.min(field.maxLength ?? TEXT_CAPS.longText, TEXT_CAPS.longText) };
  }
  const type = field.types[0];
  if (field.mode === 'placeholder') {
    const long = field.types.includes('longText') && (field.maxLength ?? 0) > TEXT_CAPS.shortText;
    const textType: AppInputType = long ? 'longText' : 'shortText';
    const cap = Math.min(field.maxLength ?? TEXT_CAPS[textType], TEXT_CAPS[textType]);
    const old = typeof current === 'string' ? current.trim() : '';
    return {
      ...base,
      type: textType,
      maxLength: cap,
      ...(old && !old.includes(PLACEHOLDER_OPENING) ? { example: old.slice(0, MAX_EXAMPLE) } : {}),
    };
  }
  switch (type) {
    case 'toggle':
      return { ...base, type, default: typeof current === 'boolean' ? current : typeof current === 'number' ? current > 0 : false };
    case 'shape': {
      const choices = (field.choices ?? [...SHAPES]).filter((c) => (SHAPES as readonly string[]).includes(c));
      return { ...base, type, choices, ...(typeof current === 'string' && choices.includes(current) ? { default: current } : {}) };
    }
    case 'choice': {
      const values = field.choices && field.choices.length > 0 ? field.choices : current !== undefined && current !== null ? [String(current)] : [];
      return {
        ...base,
        type,
        options: values.slice(0, MAX_OPTIONS).map((v) => ({ value: v, label: v.slice(0, MAX_OPTION_LABEL) })),
        ...(current !== undefined && current !== null && values.includes(String(current)) ? { default: String(current) } : {}),
      };
    }
    case 'duration':
    case 'number': {
      let min = field.min ?? null;
      let max = field.max ?? null;
      if (type === 'duration') {
        min = Math.max(1, Math.ceil(min ?? 1));
        max = max === null ? null : Math.floor(max);
      }
      const value = typeof current === 'number' && (min === null || current >= min) && (max === null || current <= max) ? current : undefined;
      return {
        ...base,
        type,
        ...(min !== null ? { min } : {}),
        ...(max !== null ? { max } : {}),
        ...(value !== undefined && (type !== 'duration' || Number.isInteger(value)) ? { default: value } : {}),
      };
    }
    default:
      return { ...base, type };
  }
}

/**
 * The inputs a setting could read: the same type family, and for a model the same kind; any switch or choice for a
 * setting that takes a listed value per answer (`canMap`).
 */
export function compatibleInputs(manifest: AppManifest, field: AppBindableField, capability: string | null): AppManifestInput[] {
  return (manifest.inputs ?? []).filter((i) => {
    if (!isTyped(i)) return false;
    if (!field.types.includes(i.type)) return canMap(field) && (i.type === 'toggle' || i.type === 'choice');
    if (i.type === 'model') return !i.agent && (!capability || i.capability === capability);
    return true;
  });
}

export type RemoveResult =
  | { ok: true; manifest: AppManifest; steps: StepData[] }
  | { ok: false; reason: 'condition'; stepIndex: number };

/**
 * Removes an `input` and every use of it (as `removeAppInput` does): its `appInputs` entries, its placeholders (replaced
 * by its default text, else nothing), its request-template lines and any `showWhen` naming it. Refused while a
 * Conditional reads it: that would change what the workflow does.
 */
export function removeInput(manifest: AppManifest, steps: readonly StepData[], id: string): RemoveResult {
  const conditionAt = steps.findIndex((s) => s.stepType === 'Conditional' && conditionIds(s.conditionExpression).includes(id));
  if (conditionAt >= 0) return { ok: false, reason: 'condition', stepIndex: conditionAt };
  const removed = (manifest.inputs ?? []).find((i) => i.kind === 'input' && i.id === id);
  const fill = typeof removed?.default === 'string' || typeof removed?.default === 'number' ? String(removed.default) : '';
  const inputs = (manifest.inputs ?? [])
    .filter((i) => !(i.kind === 'input' && i.id === id))
    .map((i) => (i.showWhen?.input === id ? { ...i, showWhen: null } : i));
  const template = manifest.requestTemplate
    ?.split('\n')
    .filter((line) => !placeholderIds(line).includes(id))
    .join('\n');
  const nextSteps = steps.map((step) => {
    const config = configOf(step);
    if (!config) return step;
    const json = JSON.stringify(config);
    if (!json.includes(placeholder(id)) && !Object.values(appInputsOf(config)).includes(id)) return step;
    let next = replacePlaceholders(config, id, fill) as Config;
    for (const [path, target] of Object.entries(appInputsOf(next))) if (target === id) next = unbindValue(next, path);
    return { ...step, ...withConfig(step, next) };
  });
  return {
    ok: true,
    manifest: { ...manifest, inputs, requestTemplate: template && template.trim() ? template : null },
    steps: nextSteps,
  };
}

/** Inserts `token` into `text` at the caret (or at the end), with a space before it when the text runs into it. */
export function insertAt(text: string, caret: number | null | undefined, token: string): { text: string; caret: number } {
  const at = caret === null || caret === undefined || caret < 0 || caret > text.length ? text.length : caret;
  const before = text.slice(0, at);
  const pad = before && !/\s$/.test(before) ? ' ' : '';
  const next = `${before}${pad}${token}${text.slice(at)}`;
  return { text: next, caret: at + pad.length + token.length };
}

/** The inputs a condition may read (switches, choices, numbers and lengths). */
export function conditionReadable(manifest: AppManifest): (AppManifestInput & { id: string })[] {
  return (manifest.inputs ?? []).filter(
    (i): i is AppManifestInput & { id: string } => isTyped(i) && ['toggle', 'choice', 'number', 'duration'].includes(i.type),
  );
}

/** What the condition editor inserts for an input: a switch as is, a choice compared with its first option. */
export function conditionSnippet(input: AppManifestInput & { id: string }): string {
  if (input.type === 'choice' && input.options && input.options.length > 0) {
    const value = input.options[0].value;
    return typeof value === 'number' ? `input.${input.id} == ${value}` : `input.${input.id} == '${String(value).replace(/'/g, "\\'")}'`;
  }
  return `input.${input.id}`;
}

// --------------------------------------------------------------------------- server refusals next to their field

/** The display name the server's messages quote (`AppManifestValidator.DisplayName`, clipped to 40). */
function displayName(input: AppManifestInput): string {
  const name = input.label?.trim() || input.field || input.role || input.id || input.kind;
  return name.slice(0, 40);
}

/**
 * Places each `APP_MANIFEST_INVALID` message under the input it names (the first quoted name that is an input's label or
 * id); the rest stay general.
 */
export function errorsByInput(manifest: AppManifest, errors: readonly string[] | null | undefined): { byIndex: Map<number, string[]>; general: string[] } {
  const byIndex = new Map<number, string[]>();
  const general: string[] = [];
  const inputs = manifest.inputs ?? [];
  for (const message of errors ?? []) {
    const quoted = [...message.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
    let index = -1;
    for (const q of quoted) {
      index = inputs.findIndex((i) => displayName(i) === q || (i.id && i.id === q));
      if (index >= 0) break;
    }
    if (index >= 0) byIndex.set(index, [...(byIndex.get(index) ?? []), message]);
    else general.push(message);
  }
  return { byIndex, general };
}
