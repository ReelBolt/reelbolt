/**
 * Where a background result belongs. A job (magic blur, ...) is launched for some clips; by the time it lands the
 * user may have trimmed, moved, split or deleted them. The result is therefore anchored to STABLE things (the
 * source media, the clip ids, the source-time range analysed), never to the document revision at launch, and is
 * rebased onto the CURRENT document when it lands. Pure: nothing here reads the clock, the network or storage.
 */

import { mediaRefOf } from '../preview/scheduler';
import type { EditTimeline, MediaClip, Track } from '../types';

export interface SourceWindow {
  startSec: number;
  endSec: number;
}

export interface ResultAnchor {
  /** The clip asset as a mediaRef: `file:<guid>` or `step_result:<guid>`. */
  assetKey: string;
  /** The clips the job was started for. */
  clipIds: string[];
  /** Every clip id playing that asset at launch (anything else is a piece made later). */
  assetClipIdsAtLaunch: string[];
  /** Source seconds the result covers; `endSec` null = unknown / the whole source. */
  coverage: { startSec: number; endSec: number | null };
  /** Source window each target played at launch; lets a split-and-deleted original be recognised by its pieces. */
  launchWindows?: Record<string, SourceWindow>;
}

export type RebaseReason = 'targetGone' | 'outsideCoverage' | 'assetChanged' | 'limitReached';

export type RebaseOutcome =
  | { kind: 'apply'; clipIds: string[] }
  | { kind: 'needsReview'; applicable: string[]; reasons: RebaseReason[]; lostCount: number };

const fpsOf = (doc: EditTimeline): number => {
  const { num, den } = doc.settings.rate;
  return den === 0 || num === 0 ? 30 : num / den;
};

/** Every track the document holds, nested sequences included (a compound's own clips are real clips too). */
function allTracks(doc: EditTimeline): Track[] {
  return [...doc.tracks, ...(doc.sequences ?? []).flatMap((s) => s.tracks)];
}

/** Every media clip in the document, top level and inside compounds. */
export function allMediaClips(doc: EditTimeline): MediaClip[] {
  const out: MediaClip[] = [];
  for (const track of allTracks(doc)) for (const item of track.items) if (item.type === 'mediaClip') out.push(item);
  return out;
}

/** Source seconds a clip plays: `sourceIn` for its length at its speed (a hold plays one instant). */
export function clipSourceWindow(clip: MediaClip, fps: number): SourceWindow {
  const start = (clip.sourceIn ?? 0) / fps;
  if (clip.hold) return { startSec: start, endSec: start };
  const rate = clip.playbackRate && clip.playbackRate > 0 ? clip.playbackRate : 1;
  return { startSec: start, endSec: start + ((clip.recordOut - clip.recordIn) * rate) / fps };
}

/** The clip's asset as a mediaRef, or null when it has none. */
export const clipAssetKey = (clip: MediaClip): string | null => (clip.assetRef ? mediaRefOf(clip.assetRef) : null);

/** Builds the anchor for a launch on `clipIds` of `doc`. */
export function anchorForClips(doc: EditTimeline, clipIds: readonly string[], coverage: ResultAnchor['coverage']): ResultAnchor {
  const clips = allMediaClips(doc);
  const fps = fpsOf(doc);
  const targets = clips.filter((c) => clipIds.includes(c.id));
  const assetKey = targets.map(clipAssetKey).find((k): k is string => k !== null) ?? '';
  const launchWindows: Record<string, SourceWindow> = {};
  for (const c of targets) launchWindows[c.id] = clipSourceWindow(c, fps);
  return {
    assetKey,
    clipIds: [...clipIds],
    assetClipIdsAtLaunch: clips.filter((c) => clipAssetKey(c) === assetKey).map((c) => c.id),
    coverage: { startSec: coverage.startSec, endSec: coverage.endSec },
    launchWindows,
  };
}

const touches = (a: SourceWindow, b: SourceWindow, tol: number): boolean => a.startSec <= b.endSec + tol && b.startSec <= a.endSec + tol;

/** The clips the anchor's result belongs to NOW, and how it differs from what was asked for. */
export function rebaseAnchor(doc: EditTimeline, anchor: ResultAnchor, canApply: (clip: MediaClip) => boolean = () => true): RebaseOutcome {
  const fps = fpsOf(doc);
  const tol = 1 / fps;
  const all = allMediaClips(doc);
  const byId = new Map(all.map((c) => [c.id, c]));
  const playing = all.filter((c) => clipAssetKey(c) === anchor.assetKey);
  const launched = new Set(anchor.assetClipIdsAtLaunch);

  const targets = new Map<string, MediaClip>();
  for (const id of anchor.clipIds) {
    const clip = byId.get(id);
    if (clip && clipAssetKey(clip) === anchor.assetKey) targets.set(id, clip);
  }

  // Pieces made after launch (a split, a copy) that share source time with a target, or with where a target played.
  const windows: SourceWindow[] = [...Object.values(anchor.launchWindows ?? {}), ...[...targets.values()].map((c) => clipSourceWindow(c, fps))];
  const candidates = playing.filter((c) => !launched.has(c.id) && !targets.has(c.id));
  let grew = true;
  while (grew) {
    grew = false;
    for (const c of candidates) {
      if (targets.has(c.id)) continue;
      const w = clipSourceWindow(c, fps);
      if (windows.some((x) => touches(x, w, tol))) {
        targets.set(c.id, c);
        windows.push(w);
        grew = true;
      }
    }
  }

  const reasons = new Set<RebaseReason>();
  let lost = 0;
  const pieces = [...targets.values()].filter((c) => !anchor.clipIds.includes(c.id));
  for (const id of anchor.clipIds) {
    if (targets.has(id)) continue;
    const launchWindow = anchor.launchWindows?.[id];
    const replaced = launchWindow !== undefined && pieces.some((p) => touches(launchWindow, clipSourceWindow(p, fps), tol));
    if (replaced) continue;
    lost += 1;
    reasons.add(byId.has(id) ? 'assetChanged' : 'targetGone');
  }

  const applicable: string[] = [];
  for (const clip of targets.values()) {
    const w = clipSourceWindow(clip, fps);
    const inside = w.startSec >= anchor.coverage.startSec - tol && (anchor.coverage.endSec === null || w.endSec <= anchor.coverage.endSec + tol);
    if (!inside) {
      reasons.add('outsideCoverage');
      lost += 1;
    } else if (!canApply(clip)) {
      reasons.add('limitReached');
      lost += 1;
    } else applicable.push(clip.id);
  }
  if (targets.size === 0 && reasons.size === 0) reasons.add('targetGone');

  if (reasons.size === 0) return { kind: 'apply', clipIds: applicable };
  return { kind: 'needsReview', applicable, reasons: [...reasons], lostCount: lost };
}
