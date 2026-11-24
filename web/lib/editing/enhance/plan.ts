import type { EditOp } from '../engine';
import { locate } from '../link/groups';
import { mediaRefOf } from '../preview';
import type { AssetRef, EditTimeline, MediaClip } from '../types';

const isMedia = (item: unknown): item is MediaClip => (item as { type?: string } | undefined)?.type === 'mediaClip';

const fileRef = (projectFileId: string): AssetRef => ({ kind: 'projectFile', projectFileId });
const sameRef = (a: AssetRef, b: AssetRef): boolean => a.kind === b.kind && (a.projectFileId ?? null) === (b.projectFileId ?? null) && (a.stepResultId ?? null) === (b.stepResultId ?? null);

/** The clip an enhancement works on: a media clip on a picture track whose asset is a project file or a render. */
export interface EnhanceSubject {
  clip: MediaClip;
  mediaRef: string;
}

export function enhanceSubject(doc: EditTimeline, clipId: string): EnhanceSubject | null {
  const found = locate(doc, clipId);
  if (!found || !isMedia(found.item) || found.track.kind !== 'video') return null;
  const mediaRef = mediaRefOf(found.item.assetRef);
  return mediaRef ? { clip: found.item, mediaRef } : null;
}

/** Every media clip in the document that still plays `mediaRef`. */
export function clipsUsingSource(doc: EditTimeline, mediaRef: string): MediaClip[] {
  const out: MediaClip[] = [];
  for (const track of doc.tracks) for (const item of track.items) if (isMedia(item) && mediaRefOf(item.assetRef) === mediaRef) out.push(item);
  return out;
}

/** What Apply changed, so "Use original" puts every clip back. */
export interface EnhanceRecord {
  enhanced: AssetRef;
  items: Array<{ itemId: string; assetRef: AssetRef }>;
}

export interface EnhancePlan {
  ops: EditOp[];
  label: string;
  record: EnhanceRecord;
}

const planFor = (clips: readonly MediaClip[], outputFileId: string): EnhancePlan | null => {
  const enhanced = fileRef(outputFileId);
  const todo = clips.filter((c) => !sameRef(c.assetRef, enhanced));
  if (todo.length === 0) return null;
  return {
    ops: todo.map((c) => ({ type: 'setProperty', itemId: c.id, key: 'assetRef', value: enhanced }) as EditOp),
    label: 'Enhance clip',
    record: { enhanced, items: todo.map((c) => ({ itemId: c.id, assetRef: c.assetRef })) },
  };
};

/**
 * ONE batch: `setProperty assetRef` on every clip that still plays the source. Same duration, so trims stay valid;
 * no new op. Null when nothing uses the source (the result then waits for an OK).
 */
export function planApply(doc: EditTimeline, sourceMediaRef: string, outputFileId: string): EnhancePlan | null {
  return planFor(clipsUsingSource(doc, sourceMediaRef), outputFileId);
}

/** "Apply to this clip" for a result that waits for an OK: the one clip, wherever it points now. */
export function planApplyToClip(doc: EditTimeline, clipId: string, outputFileId: string): EnhancePlan | null {
  const found = locate(doc, clipId);
  return found && isMedia(found.item) ? planFor([found.item], outputFileId) : null;
}

/** The ops that undo {@link planApply} by hand ("Use original"); a clip changed since is left alone. */
export function planRevert(doc: EditTimeline, record: EnhanceRecord): { ops: EditOp[]; label: string } | null {
  const ops: EditOp[] = [];
  for (const entry of record.items) {
    const live = locate(doc, entry.itemId);
    if (!live || !isMedia(live.item) || !sameRef(live.item.assetRef, record.enhanced)) continue;
    ops.push({ type: 'setProperty', itemId: entry.itemId, key: 'assetRef', value: entry.assetRef } as EditOp);
  }
  return ops.length > 0 ? { ops, label: 'Use original video' } : null;
}

/** The part of the source the clip plays, in source frames, for the compare players. */
export function clipSourceRange(clip: MediaClip, rate: { num: number; den: number }): { startSec: number; lengthSec: number } {
  const fps = rate.den > 0 ? rate.num / rate.den : 30;
  const speed = clip.playbackRate && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const startSec = Math.max(0, (clip.sourceIn ?? 0) / fps);
  const lengthSec = Math.max(0.1, ((clip.recordOut - clip.recordIn) / fps) * speed);
  return { startSec, lengthSec };
}
