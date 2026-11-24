import { msg, type Msg } from '@/i18n/msg';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';

/**
 * The plain words for an item kind, keyed by the engine's C# item type name (the step output's
 * `skippedItems[].type` is `item.GetType().Name`, e.g. `InsertClip`, not the document's `insertClip`
 * discriminator). An unknown kind gets no prefix rather than its internal name.
 */
const KIND_WORDS: ReadonlyMap<string, string> = new Map([
  ['MediaClip', 'Clip'],
  ['TitleClip', 'Title'],
  ['GeneratorClip', 'Generator'],
  ['InsertClip', 'Insert'],
  ['Caption', 'Caption'],
  ['Transition', 'Transition'],
  ['CompoundClip', 'Compound clip'],
  ['MulticamClip', 'Multicam clip'],
  ['AdjustmentClip', 'Adjustment clip'],
]);

/** The engine's counted form of a repeated skip warning: "<message> (3 items)". */
const COUNTED_WARNING = /^([^]*) \(\d+ items\)$/;

/** The shape `itemNamesOf` reads — an `EditTimeline` fits it. */
interface NamedItemsTracks {
  readonly tracks: readonly { readonly items: readonly { readonly id: string; readonly name?: string | null }[] }[];
}
export interface NamedItemsSource extends NamedItemsTracks {
  readonly sequences?: readonly NamedItemsTracks[] | null;
}

/** Item id → its non-blank name, across the timeline's tracks and its nested sequences. */
export function itemNamesOf(doc: NamedItemsSource): Map<string, string> {
  const names = new Map<string, string>();
  for (const owner of [doc, ...(doc.sequences ?? [])]) {
    for (const track of owner.tracks) {
      for (const item of track.items) {
        const name = item.name?.trim();
        if (name) names.set(item.id, name);
      }
    }
  }
  return names;
}

export interface RenderNotes {
  /** One line per item the render left out, prefixed with the item's name or kind when known. */
  skipped: string[];
  /** The render's warnings, one sentence each. */
  warnings: string[];
  /** "2 items were left out of this render · 1 note". */
  summary: string;
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * Whether a warning only repeats a skipped item's sentence. The engine's `warnings` now carry only
 * notes that are not about one item, but older stored renders copied every distinct skip message
 * there (with " (n items)" when it repeated); dropping those keeps such renders from saying it twice.
 */
function repeatsSkipped(warning: string, skippedMessages: ReadonlySet<string>): boolean {
  if (skippedMessages.has(warning)) return true;
  const counted = COUNTED_WARNING.exec(warning);
  return counted !== null && skippedMessages.has(counted[1].trim());
}

/**
 * What a finished render left out and warned about, in plain sentences, or null when there is
 * nothing to say (including an older render whose summary lacks the fields, or carries null).
 */
export function describeRenderNotes(render: EditTimelineRenderSummary, itemNames: ReadonlyMap<string, string>): RenderNotes | null {
  const skipped: string[] = [];
  const skippedMessages = new Set<string>();
  for (const entry of Array.isArray(render.skippedItems) ? render.skippedItems : []) {
    const message = typeof entry?.message === 'string' ? entry.message.trim() : '';
    if (!message) continue;
    skippedMessages.add(message);
    const prefix = (entry.itemId ? itemNames.get(entry.itemId) : undefined) ?? (entry.type ? KIND_WORDS.get(entry.type) : undefined);
    skipped.push(prefix ? `${prefix}: ${message}` : message);
  }
  const warnings = (Array.isArray(render.warnings) ? render.warnings : [])
    .map((w) => (typeof w === 'string' ? w.trim() : ''))
    .filter((w) => w.length > 0 && !repeatsSkipped(w, skippedMessages));
  if (skipped.length === 0 && warnings.length === 0) return null;
  const parts: string[] = [];
  if (skipped.length > 0) parts.push(`${plural(skipped.length, 'item was', 'items were')} left out of this render`);
  if (warnings.length > 0) parts.push(plural(warnings.length, 'note', 'notes'));
  return { skipped, warnings, summary: parts.join(' · ') };
}

/** {@link RenderNotes} with the engine-independent words as Msgs; the engine's own sentences stay strings. */
export interface RenderNotesMsg {
  skipped: (Msg | string)[];
  warnings: string[];
  summary: Msg;
}

const N = 'editor-helpers-1.deliver.notes';

/** {@link describeRenderNotes} with the item kind, the line shape and the summary as catalogue Msgs. */
export function describeRenderNotesMsg(render: EditTimelineRenderSummary, itemNames: ReadonlyMap<string, string>): RenderNotesMsg | null {
  const notes = describeRenderNotes(render, itemNames);
  if (!notes) return null;
  const skipped: (Msg | string)[] = [];
  for (const entry of Array.isArray(render.skippedItems) ? render.skippedItems : []) {
    const message = typeof entry?.message === 'string' ? entry.message.trim() : '';
    if (!message) continue;
    const named = entry.itemId ? itemNames.get(entry.itemId) : undefined;
    const kind = entry.type && KIND_WORDS.has(entry.type) ? msg(`${N}.kind.${entry.type}`) : undefined;
    const prefix = named ?? kind;
    skipped.push(prefix ? msg(`${N}.skippedLine`, { prefix, message }) : message);
  }
  const skippedPart = msg(`${N}.skippedPart`, { count: notes.skipped.length });
  const warningsPart = msg(`${N}.warningsPart`, { count: notes.warnings.length });
  const summary = notes.skipped.length > 0 && notes.warnings.length > 0 ? msg(`${N}.summaryBoth`, { skipped: skippedPart, warnings: warningsPart }) : notes.skipped.length > 0 ? skippedPart : warningsPart;
  return { skipped, warnings: notes.warnings, summary };
}
