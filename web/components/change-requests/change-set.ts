import { ApiError } from '@/lib/api/client';
import type { RevisionCutPolicy } from '@/lib/types/revisions';
import type { ChangeRequestBody, CreateStepChangeRequest, RunBriefChange, StepChangeStepCapability } from '@/lib/types/change-requests';
import { itemFullName, keyCopy, valueLabel, type CrT } from './change-request-copy';
import type { FormState } from './change-request-form';

/** Most changes one request may carry when the server does not say. */
export const DEFAULT_MAX_TARGETS = 6;

/** One pending change: a step's form waiting to be sent with the others. (The film's note is the film draft, not an entry.) */
export interface ChangeEntry {
  stepOrder: number;
  label: string;
  stepType: string;
  /** The API target for this change. */
  request: CreateStepChangeRequest;
  /** The form it came from, to reopen it prefilled. */
  form: FormState;
  /** Plain-language lines shown in the review panel. */
  summary: string[];
}

/**
 * The app's fields and run settings waiting in the same change set (docs/design/app-inputs.md 29): the body's `inputs` and
 * `brief`, and their lines in words for the review panel.
 */
export interface FieldsChange {
  inputs?: Record<string, string | number | boolean | null>;
  brief?: RunBriefChange;
  /** "Label: before → after", one per changed field or setting. */
  lines: string[];
}

export const isFilmCapability = (cap: StepChangeStepCapability): boolean => cap.route === 'composition-revision';

/** A film step can be a target only when the server says it is combinable. */
export const canCombineFilm = (cap: StepChangeStepCapability): boolean => isFilmCapability(cap) && cap.combinable === true;

/**
 * The video's own change as the draft holds it: what to change, optionally narrowed to some scenes, how to treat the cut,
 * and the version to start from. The scenes, cut and version apply only when the video is changed on its own.
 */
export interface FilmDraft {
  instruction: string;
  scenes: string[];
  cutPolicy: RevisionCutPolicy;
  /** Null means the run's own version (or the latest). */
  fromVersionId: string | null;
}

export const EMPTY_FILM_DRAFT: FilmDraft = { instruction: '', scenes: [], cutPolicy: 'KeepCut', fromVersionId: null };

/** The film as one target of a combined request: `{ stepOrder, instruction }` only. Null without a note or a film step. */
export function filmTarget(cap: StepChangeStepCapability | null | undefined, instruction: string): CreateStepChangeRequest | null {
  const text = instruction.trim();
  return cap && text ? { stepOrder: cap.stepOrder, instruction: text } : null;
}

/**
 * The body to send through the change-request route: one change keeps today's single body (byte-identical); anything else
 * uses `targets[]` in the order the changes were added, the film last, so `targets[i]` error paths point at `entries[i]`.
 * Null when there is nothing for this route (no step changes and no fields: a film alone goes through the revision route).
 */
export function buildSetBody(entries: ChangeEntry[], fields?: FieldsChange | null, film?: CreateStepChangeRequest | null): ChangeRequestBody | null {
  const targets = [...entries.map((e) => e.request), ...(film ? [film] : [])];
  if (fields) {
    // With the app's fields or settings the steps always ride in `targets` (the server combines only that form).
    return {
      ...(fields.inputs ? { inputs: fields.inputs } : {}),
      ...(fields.brief ? { brief: fields.brief } : {}),
      ...(targets.length > 0 ? { targets } : {}),
    };
  }
  if (entries.length === 0) return null;
  if (targets.length === 1) return targets[0];
  return { targets };
}

const SHORT = 40;

function valueText(t: CrT, key: string, value: unknown): string {
  if (typeof value === 'string') return valueLabel(t, key, value);
  if (typeof value === 'number') return String(Math.round(value * 100) / 100);
  return '';
}

/** What a step change does, in plain words: which items, which settings (with the value when short), the note. */
export function summarizeRequest(t: CrT, cap: StepChangeStepCapability, req: CreateStepChangeRequest): string[] {
  const lines: string[] = [];
  const ids = req.itemIds ?? [];
  lines.push(ids.length > 0 ? ids.map((id) => itemFullName(t, cap.stepType, id)).join(', ') : t('set.summaryAll'));
  const byKey = new Map<string, string[]>();
  for (const values of Object.values(req.overrides ?? {})) {
    for (const [k, v] of Object.entries(values)) byKey.set(k, [...(byKey.get(k) ?? []), valueText(t, k, v)]);
  }
  if (byKey.size > 0) {
    const parts = [...byKey].map(([k, vs]) => {
      const label = keyCopy(t, cap.stepType, k).label;
      return vs.length === 1 && vs[0] && vs[0].length <= SHORT ? `${label}: ${vs[0]}` : label;
    });
    lines.push(t('set.summaryChanging', { changes: parts.join('; ') }));
  }
  if (req.instruction) lines.push(t('set.summaryNote', { text: req.instruction }));
  return lines;
}

export interface TargetErrors {
  /** Messages by `targets[i]` index (body order). */
  byIndex: Record<number, string[]>;
  /** Messages whose path names no entry (`targets`, or no prefix). */
  general: string[];
}

/** Maps a 400, 404 or 409 body's `errors[{path,message}]` to the entry each belongs to; null when the error is not a 400 with such a body. */
export function targetErrors(error: unknown): TargetErrors | null {
  if (!(error instanceof ApiError) || ![400, 404, 409].includes(error.status)) return null;
  const list = (error.body as { errors?: unknown } | null | undefined)?.errors;
  if (!Array.isArray(list)) return null;
  const out: TargetErrors = { byIndex: {}, general: [] };
  for (const item of list) {
    if (typeof item !== 'object' || item === null) continue;
    const { path, message } = item as { path?: unknown; message?: unknown };
    if (typeof message !== 'string' || !message.trim()) continue;
    const m = typeof path === 'string' ? /^targets\[(\d+)\]/.exec(path) : null;
    if (m) (out.byIndex[Number(m[1])] ??= []).push(message);
    else out.general.push(message);
  }
  return out;
}
