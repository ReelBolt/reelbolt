import type { EditOp, TrimEdge, TrimMode } from '../engine';
import type { EditTimeline, MediaClip } from '../types';
import type { EditorStore } from '../store';
import { linkedOpsForRetime, linkedOpsForSetProperty, linkedOpsForTrim, type LinkOptions } from '../link';
import { checkVolumePercent, planSpeed } from '../clip-depth';
import { simulate } from './clip-actions';
import type { BuildResult } from './clipboard';

/**
 * RM.5: the pure builders behind the phone action bar, trim sheet and quick chips. Each returns the ops for
 * ONE undo step (or a plain sentence saying why not) by composing the same builders the desktop uses
 * (R03.1-R03.3, linked sound and picture R08.1). No new op is introduced.
 */

type Locked = (trackId: string) => boolean;

function locate(doc: EditTimeline, id: string) {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((i) => i.id === id);
    if (index >= 0) return { track, index, item: track.items[index] };
  }
  return null;
}

/**
 * Move earlier / later: the clip trades places with its neighbour on the same track (the neighbour keeps
 * its length; the gap between them stays between them). Refused at the ends and on a locked track.
 */
export function buildMoveInOrderOps(doc: EditTimeline, selection: readonly string[], direction: -1 | 1, isLocked: Locked = () => false): BuildResult<{ ops: EditOp[] }> {
  const found = selection.map((id) => locate(doc, id)).filter((l): l is NonNullable<typeof l> => l !== null);
  if (found.length === 0) return { ok: false, reason: 'Select a clip first.' };
  const ops: EditOp[] = [];
  let working = doc;
  // Each clip trades places on its own track; the later ones first when moving later so a run of clips keeps its order.
  const ordered = [...found].sort((a, b) => (direction > 0 ? b.item.recordIn - a.item.recordIn : a.item.recordIn - b.item.recordIn));
  for (const f of ordered) {
    const now = locate(working, f.item.id);
    if (!now) continue;
    if (isLocked(now.track.id)) return { ok: false, reason: 'A locked track cannot be edited. Unlock it first.' };
    const neighbour = now.track.items[now.index + direction];
    if (!neighbour) return { ok: false, reason: direction < 0 ? 'This clip is already first on its track.' : 'This clip is already last on its track.' };
    if (selection.includes(neighbour.id)) continue; // moves with the rest of the selection
    const a = direction < 0 ? neighbour : now.item; // earlier of the two
    const b = direction < 0 ? now.item : neighbour; // later of the two
    const gap = b.recordIn - a.recordOut;
    const bLen = b.recordOut - b.recordIn;
    const trackEnd = now.track.items[now.track.items.length - 1].recordOut;
    const step: EditOp[] = [
      { type: 'move', itemId: a.id, toTrackId: now.track.id, toRecordIn: trackEnd + 1 }, // out of the way
      { type: 'move', itemId: b.id, toTrackId: now.track.id, toRecordIn: a.recordIn },
      { type: 'move', itemId: a.id, toTrackId: now.track.id, toRecordIn: a.recordIn + bLen + gap },
    ];
    const sim = simulate(working, step);
    if (!sim.ok) return { ok: false, reason: 'That clip cannot swap places with its neighbour here.' };
    ops.push(...step);
    working = sim.doc;
  }
  if (ops.length === 0) return { ok: false, reason: 'Select a clip first.' };
  return { ok: true, ops };
}

export interface TrimStepOptions extends LinkOptions {
  /** "Edit sound separately": the partners are left alone. */
  separately?: boolean;
}

/** One frame stepper press on the trim sheet: the edge moves by `deltaFrames` in `mode` (partners follow when linked). */
export function buildTrimStepOps(doc: EditTimeline, itemId: string, edge: TrimEdge, mode: TrimMode, deltaFrames: number, options: TrimStepOptions = {}): BuildResult<{ ops: EditOp[]; label: string }> {
  const own = locate(doc, itemId);
  if (!own) return { ok: false, reason: 'Select a clip first.' };
  if (deltaFrames === 0) return { ok: false, reason: 'That is already where it is.' };
  const label = `Trim ${edge === 'in' ? 'start' : 'end'} ${deltaFrames > 0 ? '+' : ''}${deltaFrames}f`;
  let ops: EditOp[] = [{ type: 'trim', itemId, edge, mode, deltaFrames }];
  if (!options.separately && (own.item as MediaClip).linkId) {
    const linked = linkedOpsForTrim(doc, itemId, edge, mode, deltaFrames, options);
    if (linked.length === 0) return { ok: false, reason: 'The linked sound cannot follow that change. Use Edit sound separately to change just this clip.' };
    ops = linked;
  }
  const sim = simulate(doc, ops);
  if (!sim.ok) return sim;
  // The engine clamps a trim instead of refusing; a step that moved nothing is told plainly.
  const after = locate(sim.doc, itemId);
  const moved = after !== null && (after.item.recordIn !== own.item.recordIn || after.item.recordOut !== own.item.recordOut || ((after.item as MediaClip).sourceIn ?? 0) !== ((own.item as MediaClip).sourceIn ?? 0));
  if (!moved) return { ok: false, reason: edge === 'in' ? 'The start cannot move that way: the clip is at the limit of its source or its neighbour.' : 'The end cannot move that way: the clip is at the limit of its source or its neighbour.' };
  return { ok: true, ops, label };
}

/** A speed chip: the same plan the inspector uses (fades shrink to fit), as ONE undo step, partners following. */
export function buildSpeedOps(doc: EditTimeline, itemId: string, speed: number, options: TrimStepOptions = {}): BuildResult<{ ops: EditOp[]; label: string; fadesShrunk: boolean }> {
  const own = locate(doc, itemId);
  if (!own || own.item.type !== 'mediaClip') return { ok: false, reason: 'Select a video or sound clip first.' };
  const clip = own.item as MediaClip;
  if ((clip.playbackRate ?? 1) === speed) return { ok: false, reason: 'The clip is already at that speed.' };
  const plan = planSpeed(speed, clip);
  if (!plan.ok) return { ok: false, reason: plan.sentence };
  const ops: EditOp[] = [];
  if (plan.value.fadesShrunk) {
    ops.push({ type: 'setProperty', itemId, key: 'fadeInFrames', value: plan.value.fadeInFrames > 0 ? plan.value.fadeInFrames : undefined });
    ops.push({ type: 'setProperty', itemId, key: 'fadeOutFrames', value: plan.value.fadeOutFrames > 0 ? plan.value.fadeOutFrames : undefined });
  }
  const recordOut = clip.recordIn + plan.value.durationFrames;
  ops.push(...(options.separately ? [{ type: 'retime', itemId, playbackRate: plan.value.playbackRate, recordOut } as EditOp] : linkedOpsForRetime(doc, itemId, plan.value.playbackRate, recordOut, options)));
  const sim = simulate(doc, ops);
  if (!sim.ok) return sim;
  return { ok: true, ops, label: `Retime ${plan.value.playbackRate}x`, fadesShrunk: plan.value.fadesShrunk };
}

/** A volume percent (0 to 200) for a clip on a sound track, written like the inspector does. */
export function buildVolumeOps(doc: EditTimeline, itemId: string, percent: number, options: TrimStepOptions = {}): BuildResult<{ ops: EditOp[]; label: string }> {
  const own = locate(doc, itemId);
  if (!own || own.item.type !== 'mediaClip') return { ok: false, reason: 'Select a video or sound clip first.' };
  if (own.track.kind !== 'audio') return { ok: false, reason: 'The sound of a clip on a picture track is not played. Use Extract audio, then set the volume there.' };
  const clip = own.item as MediaClip;
  const checked = checkVolumePercent(percent);
  if (!checked.ok) return { ok: false, reason: checked.sentence };
  const value = { value: checked.value, keys: clip.gainDb?.keys ?? null };
  const ops = options.separately ? ([{ type: 'setProperty', itemId, key: 'gainDb', value }] as EditOp[]) : linkedOpsForSetProperty(doc, itemId, 'gainDb', value, options);
  const sim = simulate(doc, ops);
  if (!sim.ok) return sim;
  return { ok: true, ops, label: 'Set volume' };
}

/**
 * The trim sheet applies every press live, then, when it closes, folds the presses of that session into ONE
 * undo step: the entries since `startCursor` are undone and their ops replayed as a single batch.
 * Returns how many steps were folded (0 or 1 means nothing to fold).
 */
export function foldHistorySince(store: EditorStore, startCursor: number, label: string): number {
  const s = store.getState();
  if (s.cursor - startCursor < 2) return s.cursor - startCursor;
  const forward = s.history.slice(startCursor, s.cursor).flatMap((e) => e.forward);
  const count = s.cursor - startCursor;
  store.jumpTo(startCursor);
  store.dispatchBatch(forward, label);
  return count;
}
