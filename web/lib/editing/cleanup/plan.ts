import type { EditOp } from '../engine';
import { groupOf, linkIdOf, locate } from '../link/groups';
import type { AssetRef, EditTimeline, MediaClip } from '../types';
import { mediaRefOf } from './cleanup-api';
import type { CleanupOutputKind } from './types';

/** The clip a cleanup works on and what to ask the server to produce. */
export interface CleanupSubject {
  /** The clip whose source is cleaned and whose `assetRef` is switched. For a linked pair: the audio member. */
  clip: MediaClip;
  mediaRef: string;
  outputKind: Exclude<CleanupOutputKind, 'Auto'>;
  /** True when the clip is linked to a picture clip (which keeps its own source). */
  linked: boolean;
}

const isMedia = (item: unknown): item is MediaClip => (item as { type?: string } | undefined)?.type === 'mediaClip';

/**
 * What "Clean this clip" would work on for the current selection, or null. Linked picture + sound:
 * the audio member (audio-only file). A clip alone on an audio track: that clip (audio-only file).
 * A picture clip with its own sound and no audio partner: that clip, and the server hands back the film
 * with clean sound ("with clean audio").
 */
export function cleanupSubject(doc: EditTimeline, selection: readonly string[]): CleanupSubject | null {
  for (const id of selection) {
    const own = locate(doc, id);
    if (!own || !isMedia(own.item)) continue;
    const group = groupOf(doc, id);
    const audioMember = group.find((m) => m.track.kind === 'audio' && isMedia(m.item));
    const chosen = audioMember ?? own;
    if (!isMedia(chosen.item)) continue;
    const mediaRef = mediaRefOf(chosen.item.assetRef);
    if (!mediaRef) continue;
    const onAudio = chosen.track.kind === 'audio';
    return { clip: chosen.item, mediaRef, outputKind: onAudio ? 'Audio' : 'VideoWithCleanAudio', linked: group.length > 1 };
  }
  return null;
}

/** What Apply changed, so "Use original" puts every piece back. */
export interface CleanupRecord {
  cleaned: AssetRef;
  items: Array<{ itemId: string; assetRef: AssetRef; linkId: string | null }>;
}

export interface CleanupPlan {
  ops: EditOp[];
  label: string;
  record: CleanupRecord;
}

const fileRef = (projectFileId: string): AssetRef => ({ kind: 'projectFile', projectFileId });
const sameRef = (a: AssetRef, b: AssetRef): boolean => a.kind === b.kind && (a.projectFileId ?? null) === (b.projectFileId ?? null) && (a.stepResultId ?? null) === (b.stepResultId ?? null);

/**
 * One undo step: point the clip at the cleaned file. A linked group must all play the same source
 * (R08.1), so the audio member leaves its group in the same step (the picture clip keeps its own source
 * and stays where it is); "Use original" puts the link back. No new op: `setProperty assetRef` (+ `linkId`).
 */
export function planApply(doc: EditTimeline, subject: CleanupSubject, outputFileId: string): CleanupPlan | null {
  const live = locate(doc, subject.clip.id);
  if (!live || !isMedia(live.item)) return null;
  const clip = live.item;
  const linkId = linkIdOf(clip);
  const cleaned = fileRef(outputFileId);
  if (sameRef(clip.assetRef, cleaned)) return null;
  const ops: EditOp[] = [{ type: 'setProperty', itemId: clip.id, key: 'assetRef', value: cleaned }];
  if (linkId !== null && groupOf(doc, clip.id).length > 1) ops.push({ type: 'setProperty', itemId: clip.id, key: 'linkId', value: null });
  return { ops, label: 'Clean audio', record: { cleaned, items: [{ itemId: clip.id, assetRef: clip.assetRef, linkId }] } };
}

/** The ops that undo {@link planApply} by hand ("Use original"); items that changed since are left alone. */
export function planRevert(doc: EditTimeline, record: CleanupRecord): { ops: EditOp[]; label: string } | null {
  const ops: EditOp[] = [];
  for (const entry of record.items) {
    const live = locate(doc, entry.itemId);
    if (!live || !isMedia(live.item) || !sameRef(live.item.assetRef, record.cleaned)) continue;
    ops.push({ type: 'setProperty', itemId: entry.itemId, key: 'assetRef', value: entry.assetRef });
    if (entry.linkId !== null) ops.push({ type: 'setProperty', itemId: entry.itemId, key: 'linkId', value: entry.linkId });
  }
  return ops.length > 0 ? { ops, label: 'Use original audio' } : null;
}
