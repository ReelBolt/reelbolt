import type { EditOp } from '../engine';
import type { EditTimeline, Item, MediaClip, Track } from '../types';
import { buildFitToFillOps, buildJoinOps, simulate } from './clip-actions';

/**
 * R03 deferred ops (Wave 4): the "can this run, and if not, why" answers behind the Join, Fit to the
 * gap and Replace menu entries. Each is a pure function over the document so the menu can show the
 * reason as plain text before anything is pressed. The engine stays the judge: a case these checks do
 * not name still goes through `simulate`, so an entry is never enabled for an edit the engine refuses.
 */

export type DeferredOpResult = { ok: true; ops: EditOp[] } | { ok: false; reason: string };

function locate(doc: EditTimeline, id: string): { track: Track; index: number; item: Item } | null {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((i) => i.id === id);
    if (index >= 0) return { track, index, item: track.items[index] };
  }
  return null;
}

const rateOf = (clip: MediaClip): number => (typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1);
const sameAsset = (a: MediaClip, b: MediaClip): boolean => JSON.stringify(a.assetRef) === JSON.stringify(b.assetRef);

/** Join with the next clip: same source, the source continues where the first part ends, no gap between. */
export function joinAvailability(doc: EditTimeline, itemId: string): DeferredOpResult {
  const at = locate(doc, itemId);
  if (!at) return { ok: false, reason: 'Select a clip first.' };
  const next = at.track.items[at.index + 1];
  if (!next) return { ok: false, reason: 'There is no clip after this one on its track.' };
  if (next.recordIn > at.item.recordOut) return { ok: false, reason: 'There is a gap before the next clip. Close the gap first.' };
  if (at.item.type === 'mediaClip' && next.type === 'mediaClip') {
    if (!sameAsset(at.item, next)) return { ok: false, reason: 'The two clips come from different files.' };
    if (!at.item.reverse && !at.item.hold && !next.reverse && !next.hold) {
      const rate = rateOf(at.item);
      const expected = (at.item.sourceIn ?? 0) + (at.item.recordOut - at.item.recordIn) * rate;
      if (Math.abs((next.sourceIn ?? 0) - expected) > 0.5) return { ok: false, reason: 'The second clip does not carry on from where the first one stops in the file.' };
    }
  } else if (at.item.type !== next.type) {
    return { ok: false, reason: 'The two clips are not the same kind.' };
  }
  return buildJoinOps(doc, itemId);
}

export interface FitToFillPlan {
  ok: true;
  ops: EditOp[];
  /** The speed the clip will play at afterwards, as a whole percent (media clips only). */
  speedPercent: number | null;
}

/** Fit to the gap: the clip is retimed to fill the gap after it. Shows the resulting speed. */
export function fitToFillAvailability(doc: EditTimeline, itemId: string): FitToFillPlan | { ok: false; reason: string } {
  const at = locate(doc, itemId);
  if (!at) return { ok: false, reason: 'Select a clip first.' };
  const next = at.track.items[at.index + 1];
  if (!next || next.recordIn <= at.item.recordOut) return { ok: false, reason: 'There is no gap after this clip to fill.' };
  if (at.item.type === 'mediaClip' && at.item.hold) return { ok: false, reason: 'A frozen frame has no speed to change. Drag its end to make it longer.' };
  const built = buildFitToFillOps(doc, itemId);
  if (!built.ok) return built;
  let speedPercent: number | null = null;
  if (at.item.type === 'mediaClip') {
    const oldDuration = at.item.recordOut - at.item.recordIn;
    const newDuration = next.recordIn - at.item.recordIn;
    speedPercent = Math.round(((oldDuration * rateOf(at.item)) / newDuration) * 100);
  }
  return { ok: true, ops: built.ops, speedPercent };
}

/** The confirm line after a fit: "Speed becomes 83%". */
export function fitToFillConfirmLine(plan: FitToFillPlan): string {
  return plan.speedPercent === null ? 'The clip now fills the gap.' : `Speed becomes ${plan.speedPercent}%`;
}

/** Whether a clip can be replaced at all (before a source is picked). */
export function replaceAvailability(doc: EditTimeline, itemId: string): { ok: true } | { ok: false; reason: string } {
  const at = locate(doc, itemId);
  if (!at) return { ok: false, reason: 'Select a clip first.' };
  if (at.item.type !== 'mediaClip') return { ok: false, reason: 'Only a video, picture or sound clip can be replaced this way.' };
  return { ok: true };
}

/**
 * Checks a picked file against the clip: the clip keeps its span, so the file must cover the part of
 * the source the clip plays. Stills cover any length. `sourceFrames` null means the length is unknown
 * and the engine decides.
 */
export function replaceSourceCheck(clip: MediaClip, source: { frames: number | null; isStill: boolean }): { ok: true } | { ok: false; reason: string } {
  if (source.isStill || source.frames === null) return { ok: true };
  const needed = Math.round((clip.recordOut - clip.recordIn) * rateOf(clip));
  if (source.frames < needed) return { ok: false, reason: 'That file is shorter than the clip.' };
  return { ok: true };
}

/** Runs a plan through the engine once more (used by tests and the menu to be sure it is accepted). */
export function engineAccepts(doc: EditTimeline, ops: readonly EditOp[]): boolean {
  return simulate(doc, ops).ok;
}
