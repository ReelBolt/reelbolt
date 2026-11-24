import { msg, type Msg } from '@/i18n/msg';
import type { UnplacedItem } from './sync/api';
import type { TimelineDiff, ItemChangeKind } from './compare/diff';
import type { GuestDecision } from './review/guest-links';

/** i18n counterparts of one-line summaries: review decisions, unplaced items, preset descriptions, link and diff summaries. */

const K = 'editor-lib';

/** guest-links.ts decisionSentence(d); the date is formatted by the catalogue (`Date` value). */
export function decisionSentenceMsg(d: GuestDecision): Msg {
  if (d.decision === 'approved') return msg(`${K}.guest.approved`, { name: d.guestName, when: new Date(d.at) });
  const note = d.note?.trim();
  return note ? msg(`${K}.guest.changesNote`, { name: d.guestName, note }) : msg(`${K}.guest.changes`, { name: d.guestName });
}

/** open-timeline.ts unplacedMessage(items): parts (one per kind, with its count) + the sentence that takes the joined parts. */
export function unplacedPartMsgs(items: readonly UnplacedItem[]): Msg[] {
  const counts = new Map<string, number>();
  for (const item of items) counts.set(item.track, (counts.get(item.track) ?? 0) + 1);
  return [...counts.entries()].map(([track, n]) => msg(`${K}.open.unplaced.${track.toLowerCase()}`, { n }));
}
export const unplacedSentenceMsg = (total: number, partsText: string): Msg => msg(`${K}.open.unplaced.sentence`, { total, parts: partsText });
export const unplacedTitleMsg = (): Msg => msg(`${K}.open.unplaced.title`);

/** preset-manage.ts describePreset(kind, payload). `easing` / `type` are the preset's own words. */
export function describePresetMsg(kind: 'mastering' | 'transition' | 'effects' | string, payload: unknown): Msg {
  const obj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object';
  if (!obj(payload)) return msg(`${K}.preset.none`);
  if (kind === 'mastering') {
    const base = { lufs: Number(payload.targetLufs), peak: Number(payload.truePeakDbtp) };
    return typeof payload.loudnessRange === 'number' ? msg(`${K}.preset.masteringRange`, { ...base, range: payload.loudnessRange }) : msg(`${K}.preset.mastering`, base);
  }
  if (kind === 'transition') {
    const frames = Number(payload.framesBefore ?? 0) + Number(payload.framesAfter ?? 0);
    const type = String(payload.transitionType);
    return typeof payload.easing === 'string' ? msg(`${K}.preset.transitionEasing`, { type, frames, easing: payload.easing }) : msg(`${K}.preset.transition`, { type, frames });
  }
  const effects = Array.isArray(payload.effects) ? payload.effects.filter(obj) : [];
  if (effects.length === 0) return msg(`${K}.preset.noEffects`);
  return msg(`${K}.preset.adds`, { effects: effects.map((e) => `${String(e.type)}${typeof e.amount === 'number' ? ` ${e.amount}` : ''}`).join(', ') });
}
/** presets.ts: the mastering preset names by id ('social' | 'podcast' | 'broadcast'). */
export const masteringPresetNameMsg = (id: 'social' | 'podcast' | 'broadcast'): Msg => msg(`${K}.preset.names.${id}`);

/** link/migrate.ts summarize(linked, added) */
export function linkSummaryMsg(linked: number, added: number): Msg {
  if (linked === 0 && added === 0) return msg(`${K}.link.nothing`);
  if (linked > 0 && added > 0) return msg(`${K}.link.linkedAdded`, { n: linked, added });
  return linked > 0 ? msg(`${K}.link.linked`, { n: linked }) : msg(`${K}.link.added`, { added });
}

/** compare/diff.ts summarizeDiff(diff) as parts (join the resolved parts with `listText(parts, locale, 'unit')`); empty = `diffNoneMsg()`. */
export function summarizeDiffMsgs(diff: TimelineDiff): Msg[] {
  const parts = (Object.keys(diff.counts) as ItemChangeKind[]).filter((k) => diff.counts[k] > 0).map((k) => msg(`${K}.diff.counts.${k}`, { n: diff.counts[k] }));
  if (diff.tracks.length > 0) parts.push(msg(`${K}.diff.tracks`, { n: diff.tracks.length }));
  if (diff.settingsChanged.length > 0) parts.push(msg(`${K}.diff.settings`));
  const markerTotal = diff.markers.added + diff.markers.removed + diff.markers.changed;
  if (markerTotal > 0) parts.push(msg(`${K}.diff.markers`, { n: markerTotal }));
  return parts;
}
export const diffNoneMsg = (): Msg => msg(`${K}.diff.none`);

/** viewer-transform/frame-handles.ts */
export const trackedWindowNoteMsg = (): Msg => msg(`${K}.frameHandles.tracked`);
export const frameHandleLabelMsg = (id: 'mask' | 'reframe' | 'window'): Msg => msg(`${K}.frameHandles.${id}`);
/** review/review-state.ts: the label when a render has no date. */
export const finishedRenderMsg = (): Msg => msg(`${K}.review.finishedRender`);
