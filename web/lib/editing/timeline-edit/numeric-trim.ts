import type { EditOp } from '../engine';
import type { EditTimeline, MediaClip } from '../types';
import { simulate } from './clip-actions';

/**
 * R03.3: typed in / out / duration for the selected clip. In is the SOURCE in-point, out is the
 * record-side out-point, duration is the clip's length on the timeline. Each is composed from
 * `trim` ops so the engine's clamps and the undo inverse keep applying; the result is checked
 * against what was asked and refused with a plain sentence when it cannot be made exactly.
 */
export type NumericField = 'sourceIn' | 'recordOut' | 'duration';

export type NumericTrimResult = { ok: true; ops: EditOp[]; label: string } | { ok: false; reason: string };

/**
 * @param sourceFrames length of the clip's source in frames, or null when it is not known (then only the engine's own limits apply)
 */
export function buildNumericTrimOps(doc: EditTimeline, itemId: string, field: NumericField, value: number, sourceFrames: number | null): NumericTrimResult {
  const track = doc.tracks.find((t) => t.items.some((i) => i.id === itemId));
  const item = track?.items.find((i) => i.id === itemId);
  if (!track || !item) return { ok: false, reason: 'Select a clip first.' };
  if (!Number.isInteger(value)) return { ok: false, reason: 'Use a whole number of frames.' };
  const clip = item.type === 'mediaClip' ? (item as MediaClip) : null;
  const rate = clip?.playbackRate && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const duration = item.recordOut - item.recordIn;
  const sourceIn = clip?.sourceIn ?? 0;

  if (field === 'sourceIn') {
    if (!clip) return { ok: false, reason: 'Only a media clip has a source in-point.' };
    if (value < 0) return { ok: false, reason: 'The in-point cannot be before the start of the source.' };
    const delta = Math.round((value - sourceIn) / rate);
    if (delta === 0) return { ok: false, reason: 'That is already the in-point.' };
    const newDuration = duration - delta;
    if (newDuration < 1) return { ok: false, reason: 'The in-point has to leave at least one frame in the clip.' };
    if (sourceFrames !== null && value >= sourceFrames) return { ok: false, reason: `The source is only ${sourceFrames} frames long, so the in-point has to be before frame ${sourceFrames}.` };
    if (sourceFrames !== null && value + Math.round(newDuration * rate) > sourceFrames) return { ok: false, reason: `That would run past the end of the source (${sourceFrames} frames). Choose an earlier in-point.` };
    const ops: EditOp[] = [{ type: 'trim', itemId, edge: 'in', mode: 'ripple', deltaFrames: delta }];
    return verified(doc, itemId, ops, (after) => (after.sourceIn ?? 0) === value, `Set in-point to frame ${value}`, `That in-point cannot be hit exactly at ${rate}x speed. Try a nearby frame.`);
  }

  const newDuration = field === 'duration' ? value : value - item.recordIn;
  if (newDuration < 1) return { ok: false, reason: field === 'duration' ? 'The clip has to be at least one frame long.' : 'The out-point has to be after the clip starts.' };
  if (newDuration === duration) return { ok: false, reason: field === 'duration' ? 'That is already the duration.' : 'That is already the out-point.' };
  if (clip && clip.reverse !== true && sourceFrames !== null && sourceIn + Math.round(newDuration * rate) > sourceFrames) {
    const most = Math.max(1, Math.floor((sourceFrames - sourceIn) / rate));
    return { ok: false, reason: `The source only has ${sourceFrames - sourceIn} frames after the in-point, so this clip can be at most ${most} frames long.` };
  }
  const ops: EditOp[] = [{ type: 'trim', itemId, edge: 'out', mode: 'ripple', deltaFrames: newDuration - duration }];
  return verified(doc, itemId, ops, (_after, rec) => rec.recordOut - rec.recordIn === newDuration, field === 'duration' ? `Set duration to ${newDuration} frames` : `Set out-point to frame ${item.recordIn + newDuration}`, 'That length is not possible here.');
}

function verified(doc: EditTimeline, itemId: string, ops: EditOp[], exact: (clip: { sourceIn?: number }, record: { recordIn: number; recordOut: number }) => boolean, label: string, inexact: string): NumericTrimResult {
  const sim = simulate(doc, ops);
  if (!sim.ok) return sim;
  const after = sim.doc.tracks.flatMap((t) => t.items).find((i) => i.id === itemId);
  if (!after) return { ok: false, reason: inexact };
  if (!exact(after as { sourceIn?: number }, after)) return { ok: false, reason: inexact };
  return { ok: true, ops, label };
}
