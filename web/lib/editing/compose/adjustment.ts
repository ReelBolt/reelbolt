import type { EditOp } from '../engine';
import type { AdjustmentClip, EditTimeline, Track } from '../types';

/**
 * "Add adjustment clip": one new clip on a new top video track, over the marked range or two seconds from the
 * playhead. Shared by the Compose list's button and the `addAdjustmentClip` command so they cannot drift.
 */
export function adjustmentClipOps(
  doc: EditTimeline,
  at: { playheadFrame: number; inOut: { inFrame: number | null; outFrame: number | null } },
  newId: () => string,
  names: { clip: string; track: string },
): { ops: EditOp[]; clipId: string } {
  const { playheadFrame, inOut } = at;
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const marked = inOut.inFrame !== null && inOut.outFrame !== null && inOut.outFrame > inOut.inFrame;
  const start = marked ? (inOut.inFrame as number) : playheadFrame;
  const end = marked ? (inOut.outFrame as number) : playheadFrame + Math.round(2 * fps);
  const clip: AdjustmentClip = { id: newId(), type: 'adjustmentClip', name: names.clip, recordIn: start, recordOut: Math.max(start + 1, end), enabled: true, grade: null, effects: null, provenance: null };
  const track: Track = { id: newId(), kind: 'video', name: names.track, height: 40, locked: false, enabled: true, mute: false, solo: false, items: [clip] };
  const videoTracks = doc.tracks.filter((tr) => tr.kind === 'video').length;
  return { ops: [{ type: 'addTrack', index: videoTracks, track }], clipId: clip.id };
}
