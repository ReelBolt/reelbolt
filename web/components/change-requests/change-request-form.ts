import type {
  CreateStepChangeRequest,
  OverrideValues,
  PronunciationEntry,
  StepChangeItem,
  StepChangeOverrideSpec,
  StepChangeStepCapability,
} from '@/lib/types/change-requests';
import { crCopy, itemFullName, itemKindOf, keyCopy, type CrT } from './change-request-copy';

/** What a person typed for one key. Empty text, `null`, `false` and an empty list all mean "leave as it was". */
export type DraftValue = string | number | boolean | PronunciationEntry[] | null;

/** What a person typed for one item (or for "all"), by override key. */
export type ItemDraft = Record<string, DraftValue>;

export interface FormState {
  scope: 'items' | 'all';
  selected: string[];
  drafts: Record<string, ItemDraft>;
  all: ItemDraft;
  instruction: string;
}

export const emptyDraft = (): ItemDraft => ({});
export const emptyForm = (): FormState => ({ scope: 'items', selected: [], drafts: {}, all: emptyDraft(), instruction: '' });

/** The value kinds the form knows how to draw and check. */
export type OverrideKind = 'text' | 'number' | 'flag' | 'enum' | 'pronunciation';

/** The kind of a catalogue entry, from its `ChangeOverrideType` name; null for one this build does not know (it is left out). */
export function overrideKind(spec: StepChangeOverrideSpec): OverrideKind | null {
  switch (spec.type.toLowerCase()) {
    case 'text': case 'string': return 'text';
    case 'number': return 'number';
    case 'flag': case 'boolean': return 'flag';
    case 'enum': return 'enum';
    case 'pronunciationlist': case 'list': return 'pronunciation';
    default: return null;
  }
}

/** Keys whose value is the item's own wording: prefilled from it, skipped when unchanged, and not offered for "all". */
const ITEM_WORDING_KEYS = new Set(['text', 'prompt']);
export const isItemWording = (key: string) => ITEM_WORDING_KEYS.has(key);

/** The wording shown in the item list ends in "..." when the server cut it short; that is not the full text. */
export const isCutShort = (text: string | null) => !!text && text.endsWith('...');

/** True when the step has no items to pick from and is changed straight away (the thumbnail). */
export const isDirect = (cap: StepChangeStepCapability) => cap.itemKind === 'None';

/** The keys a form shows: the catalogue's own order, without any this build cannot draw. In "all" mode a key about one item's own wording is left out. */
export function visibleSpecs(cap: StepChangeStepCapability, mode: 'item' | 'all' | 'direct'): StepChangeOverrideSpec[] {
  return cap.overrides.filter((o) => overrideKind(o) !== null && (mode !== 'all' || !isItemWording(o.key)));
}

const fmt = (n: number) => String(Math.round(n * 100) / 100);

function checkRange(t: CrT, label: string, who: string, spec: StepChangeOverrideSpec, value: number, errors: string[]): boolean {
  const { min, max } = spec;
  if (!Number.isFinite(value) || (min !== null && value < min) || (max !== null && value > max)) {
    errors.push(t('errors.range', { who, label: label.toLowerCase(), min: fmt(min ?? 0), max: fmt(max ?? 0) }));
    return false;
  }
  return true;
}

function draftToValues(
  t: CrT, cap: StepChangeStepCapability, specs: StepChangeOverrideSpec[], d: ItemDraft, currentText: string | null, errors: string[], who: string,
): OverrideValues {
  const out: OverrideValues = {};
  for (const spec of specs) {
    const kind = overrideKind(spec);
    const raw = d[spec.key];
    const label = keyCopy(t, cap.stepType, spec.key).label;
    if (kind === 'text' && typeof raw === 'string') {
      const text = raw.trim();
      if (!text) continue;
      if (isItemWording(spec.key) && text === (currentText ?? '').trim()) continue;
      if (spec.multiLine && spec.maxEntries && text.split(/\r?\n/).length > spec.maxEntries) errors.push(t('errors.tooManyLines', { who, label: label.toLowerCase(), max: spec.maxEntries }));
      else if (spec.maxLength && text.length > spec.maxLength) errors.push(t('errors.tooLong', { who, label: label.toLowerCase(), max: spec.maxLength }));
      else out[spec.key] = text;
    } else if (kind === 'number' && typeof raw === 'number') {
      if (checkRange(t, label, who, spec, raw, errors)) out[spec.key] = raw;
    } else if (kind === 'flag') {
      if (raw === true) out[spec.key] = true;
    } else if (kind === 'enum' && typeof raw === 'string' && raw) {
      if (spec.values && !spec.values.includes(raw)) errors.push(t('errors.chooseOption', { who, label: label.toLowerCase() }));
      else out[spec.key] = raw;
    } else if (kind === 'pronunciation' && Array.isArray(raw)) {
      const entries = raw
        .map((e) => ({ term: e.term.trim(), spelling: e.spelling.trim() }))
        .filter((e) => e.term || e.spelling);
      if (entries.some((e) => !e.term || !e.spelling)) errors.push(t('errors.giveBoth', { who }));
      else if (spec.maxEntries && entries.length > spec.maxEntries) errors.push(t('errors.atMostWords', { who, max: spec.maxEntries }));
      else if (entries.length > 0) out[spec.key] = entries;
    }
  }
  return out;
}

export interface BuiltRequest {
  request: CreateStepChangeRequest | null;
  errors: string[];
}

/** The text an untouched item draft starts with: the item's own wording, unless the list only holds a shortened copy. */
export function startingDraft(cap: StepChangeStepCapability, item: StepChangeItem): ItemDraft {
  const d: ItemDraft = {};
  if (item.text && !isCutShort(item.text)) {
    for (const spec of cap.overrides) if (overrideKind(spec) === 'text' && isItemWording(spec.key)) d[spec.key] = item.text;
  }
  return d;
}

/**
 * Turns the form into the API request, validating against the capability's catalogue by value type. At least one
 * change is required: an instruction on its own changes nothing, so it is reported rather than sent.
 */
export function buildChangeRequest(cap: StepChangeStepCapability, form: FormState, t: CrT): BuiltRequest {
  const errors: string[] = [];
  const overrides: Record<string, OverrideValues> = {};
  let itemIds: string[] | undefined;
  const kind = itemKindOf(cap.stepType, cap.itemKind);
  if (isDirect(cap)) {
    const v = draftToValues(t, cap, visibleSpecs(cap, 'direct'), form.all, null, errors, t('errors.whoThisChange'));
    if (Object.keys(v).length > 0) overrides['*'] = v;
  } else if (form.scope === 'all') {
    const v = draftToValues(t, cap, visibleSpecs(cap, 'all'), form.all, null, errors, t('errors.whoAll', { kind }));
    if (Object.keys(v).length > 0) overrides['*'] = v;
  } else {
    itemIds = form.selected;
    if (itemIds.length === 0) return { request: null, errors: [] };
    const specs = visibleSpecs(cap, 'item');
    for (const id of itemIds) {
      const item = cap.items.find((i) => i.id === id);
      const who = t('errors.whoItem', { kind, id });
      const v = draftToValues(t, cap, specs, form.drafts[id] ?? emptyDraft(), item?.text ?? null, errors, cap.stepType === 'Voiceover' ? who : itemFullName(t, cap.stepType, id));
      if (Object.keys(v).length > 0) overrides[id] = v;
    }
  }
  const instruction = form.instruction.trim();
  if (errors.length > 0) return { request: null, errors };
  if (Object.keys(overrides).length === 0 && cap.allowsInstructionOnly && instruction && itemIds && itemIds.length > 0) {
    // A scene picked and a free instruction, nothing else: the server rewrites only those scenes.
    return { request: { stepResultId: cap.stepResultId, instruction, itemIds }, errors: [] };
  }
  if (Object.keys(overrides).length === 0) {
    return { request: null, errors: instruction ? [crCopy(t).nothingChosen] : [] };
  }
  return {
    request: {
      stepResultId: cap.stepResultId,
      ...(instruction ? { instruction } : {}),
      ...(itemIds ? { itemIds } : {}),
      overrides,
    },
    errors: [],
  };
}
