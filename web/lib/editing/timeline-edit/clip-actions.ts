import { apply, type EditOp } from '../engine';
import { refusalSentence } from '../refusal';
import { rippleDeletePlan, type RippleDeletePlan } from '../ripple-delete';
import { selectAllOnTrack } from '../selection';
import type { EditTimeline, Item } from '../types';
import type { BuildResult } from './clipboard';

/**
 * The builders behind every clip action (menu, buttons and keys all call these). Each takes the
 * document and the selection and returns the ops for ONE undo step, or a plain sentence saying why
 * not. Edit paths are deliberately funnelled through these small functions so a link-aware wrapper
 * (linked sound and picture) can decorate them without touching the UI.
 */

type Locked = (trackId: string) => boolean;
const never: Locked = () => false;

function locate(doc: EditTimeline, id: string) {
  for (let trackIndex = 0; trackIndex < doc.tracks.length; trackIndex++) {
    const track = doc.tracks[trackIndex];
    const index = track.items.findIndex((i) => i.id === id);
    if (index >= 0) return { track, trackIndex, index, item: track.items[index] };
  }
  return null;
}

/** Runs the ops on a copy; the first the engine refuses becomes a sentence. */
export function simulate(doc: EditTimeline, ops: readonly EditOp[]): { ok: true; doc: EditTimeline } | { ok: false; reason: string } {
  let working = doc;
  for (const op of ops) {
    try {
      working = apply(working, op).doc;
    } catch (error) {
      return { ok: false, reason: refusalSentence(error, doc, op) };
    }
  }
  return { ok: true, doc: working };
}

/** Delete = ripple delete (D7): closes the gap, on the paired sound too. */
export function buildDeleteOps(doc: EditTimeline, selection: readonly string[], isLocked: Locked = never): RippleDeletePlan {
  return rippleDeletePlan(doc, selection, isLocked);
}

/** Shift+Delete = lift: removes the clips and leaves a gap. */
export function buildLiftOps(doc: EditTimeline, selection: readonly string[]): EditOp[] {
  return selection.filter((id) => locate(doc, id) !== null).map((itemId) => ({ type: 'lift', itemId }) as EditOp);
}

/** Move to the nearest track of the same kind above (-1) or below (+1), same position in time. */
export function buildMoveToTrackOps(doc: EditTimeline, selection: readonly string[], direction: -1 | 1, isLocked: Locked = never): BuildResult<{ ops: EditOp[] }> {
  const ops: EditOp[] = [];
  const found = selection.map((id) => locate(doc, id)).filter((l): l is NonNullable<typeof l> => l !== null);
  if (found.length === 0) return { ok: false, reason: 'Select a clip first.' };
  for (const l of found) {
    let target = l.trackIndex + direction;
    while (doc.tracks[target] && doc.tracks[target].kind !== l.track.kind) target += direction;
    const to = doc.tracks[target];
    if (!to) return { ok: false, reason: direction < 0 ? 'There is no track above to move it to.' : 'There is no track below to move it to.' };
    if (isLocked(to.id) || isLocked(l.track.id)) return { ok: false, reason: 'A locked track cannot be edited. Unlock it first.' };
    ops.push({ type: 'move', itemId: l.item.id, toTrackId: to.id, toRecordIn: l.item.recordIn });
  }
  const sim = simulate(doc, ops);
  if (!sim.ok) return { ok: false, reason: 'There is already a clip in that spot on the next track, so it stays where it is.' };
  return { ok: true, ops };
}

/** Join with the next clip (a clip that was split and not changed since). */
export function buildJoinOps(doc: EditTimeline, itemId: string): BuildResult<{ ops: EditOp[] }> {
  const l = locate(doc, itemId);
  if (!l) return { ok: false, reason: 'Select a clip first.' };
  const ops: EditOp[] = [{ type: 'join', itemId }];
  const sim = simulate(doc, ops);
  if (!sim.ok) return { ok: false, reason: 'These two clips cannot be joined. Only the two halves of a split that have not been changed since can be put back together.' };
  return { ok: true, ops };
}

/** Fit to fill: stretch or squeeze the clip (changing its speed) to fill the gap that follows it. */
export function buildFitToFillOps(doc: EditTimeline, itemId: string): BuildResult<{ ops: EditOp[] }> {
  const l = locate(doc, itemId);
  if (!l) return { ok: false, reason: 'Select a clip first.' };
  const next = l.track.items[l.index + 1];
  if (!next || next.recordIn <= l.item.recordOut) return { ok: false, reason: 'There is no gap after this clip to fill.' };
  const ops: EditOp[] = [{ type: 'fitToFill', itemId, recordIn: l.item.recordIn, recordOut: next.recordIn }];
  const sim = simulate(doc, ops);
  return sim.ok ? { ok: true, ops } : { ok: false, reason: sim.reason };
}

/** Disable or enable (a disabled clip stays on the timeline but is skipped on playback and in the render). */
export function buildToggleEnabledOps(doc: EditTimeline, selection: readonly string[]): BuildResult<{ ops: EditOp[]; enabling: boolean }> {
  const items = selection.map((id) => locate(doc, id)?.item).filter((i): i is Item => i !== undefined);
  if (items.length === 0) return { ok: false, reason: 'Select a clip first.' };
  const enabling = items.every((i) => (i as { enabled?: boolean }).enabled === false);
  const ops: EditOp[] = items.map((item) => ({ type: 'setProperty', itemId: item.id, key: 'enabled', value: enabling ? true : false }));
  const sim = simulate(doc, ops);
  return sim.ok ? { ok: true, ops, enabling } : { ok: false, reason: sim.reason };
}

export type SelectScope = 'forward' | 'backward';

/** Select forward/backward from the playhead on the selected clip's track, or on all tracks. */
export function selectFromPlayhead(doc: EditTimeline, playheadFrame: number, scope: SelectScope, trackId: string | null): string[] {
  const ids: string[] = [];
  for (const track of doc.tracks) {
    if (trackId !== null && track.id !== trackId) continue;
    for (const item of track.items) {
      if (scope === 'forward' ? item.recordOut > playheadFrame : item.recordIn < playheadFrame) ids.push(item.id);
    }
  }
  return ids;
}

export { selectAllOnTrack };
