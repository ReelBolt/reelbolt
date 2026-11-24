/**
 * R07.5 "Freeze frame here": split the clip under the playhead and hold the frame there for two
 * seconds, as ONE op batch (one undo step). Existing ops only:
 *
 *   1. `blade` the picture clip (and, link-aware, its partners) at the playhead.
 *   2. `nudge` everything from the playhead on, on the picture's track and on each partner track,
 *      right by the hold length so a gap opens exactly where the freeze goes. Other tracks keep
 *      their time. The paired sound just has that gap, so the freeze is silent.
 *   3. `insert` a hold clip (`hold: true`, one source frame, no link) into the gap.
 */

import type { EditOp } from '../engine';
import { linkedOpsForBlade } from '../link/edit';
import { editableGroup, locate } from '../link/groups';
import type { EditTimeline, MediaClip } from '../types';

export const FREEZE_SECONDS = 2;

export interface FreezePlan {
  ops: EditOp[];
  holdId: string;
}

/** Why a freeze cannot be made at `playheadFrame`, or null when it can. */
export function freezeBlocker(doc: EditTimeline, itemId: string | null, playheadFrame: number, isLocked: (trackId: string) => boolean = () => false): string | null {
  if (!itemId) return 'Select a video clip first.';
  const found = locate(doc, itemId);
  if (!found || found.item.type !== 'mediaClip') return 'Freeze frame works on video clips.';
  if (found.track.kind !== 'video') return 'Freeze frame works on video clips.';
  if (found.item.hold === true) return 'This clip is already a frozen frame.';
  if (isLocked(found.track.id)) return 'This track is locked.';
  if (!(found.item.recordIn < playheadFrame && playheadFrame < found.item.recordOut)) return 'Move the playhead inside the clip.';
  return null;
}

/** The ops, or null when {@link freezeBlocker} says no. */
export function freezeFrameOps(doc: EditTimeline, itemId: string, playheadFrame: number, newId: () => string, isLocked: (trackId: string) => boolean = () => false): FreezePlan | null {
  if (freezeBlocker(doc, itemId, playheadFrame, isLocked) !== null) return null;
  const found = locate(doc, itemId);
  if (!found) return null;
  const clip = found.item as MediaClip;
  const { num, den } = doc.settings.rate;
  const fps = den > 0 && num > 0 ? num / den : 30;
  const hold = Math.max(1, Math.round(FREEZE_SECONDS * fps));
  const p = Math.trunc(playheadFrame);

  const ops: EditOp[] = [];
  const blade = linkedOpsForBlade(doc, itemId, p, newId(), { isLocked });
  ops.push(...blade.ops);

  // Gap: shift everything at or after p on each affected track, right-most first.
  const members = editableGroup(doc, itemId, { isLocked });
  for (const member of members) {
    const later = member.track.items.filter((i) => i.recordIn >= p).sort((a, b) => b.recordIn - a.recordIn);
    for (const item of later) ops.push({ type: 'nudge', itemId: item.id, deltaFrames: hold });
    const rightHalf = blade.rightHalves[member.item.id];
    if (rightHalf && member.item.recordIn < p && p < member.item.recordOut) ops.push({ type: 'nudge', itemId: rightHalf, deltaFrames: hold });
  }

  const rate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const frozen: MediaClip = {
    ...clip,
    id: newId(),
    name: `${clip.name} (freeze)`,
    recordIn: p,
    recordOut: p + hold,
    sourceIn: (clip.sourceIn ?? 0) + Math.round((p - clip.recordIn) * rate),
    playbackRate: 1,
    hold: true,
    provenance: { origin: 'manual', editedByUser: true },
  };
  delete frozen.linkId;
  delete frozen.reverse;
  delete frozen.fadeInFrames;
  delete frozen.fadeOutFrames;
  const index = found.track.items.filter((i) => i.recordIn < p).length;
  ops.push({ type: 'insert', trackId: found.track.id, index, item: frozen });
  return { ops, holdId: frozen.id };
}
