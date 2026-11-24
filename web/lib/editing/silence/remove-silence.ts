/**
 * Remove-silence (P4): compiles a reviewed set of silence spans into a program of the engine's own
 * primitive ops — `blade`s at the span edges and an `extract` of the piece between — so the whole
 * clean-up lands as ONE undo step through `EditorStore.dispatchBatch`, and nothing here mutates a
 * document directly. Every op is produced by running the engine (`apply`) against the document as
 * it will be when that op executes, so the ids a later op names are the ids the earlier blades
 * really created.
 *
 * ## Where the spans come from
 *
 * A `VideoAnalyze` step's artifact carries silence gaps (`g{n}`) in SOURCE seconds. The timeline's
 * clips carry `sourceIn` (frames) and `playbackRate`, so a source-second span maps onto a clip's
 * record frames as `recordIn + (sourceFrame - sourceIn) / playbackRate`. The caller does that
 * mapping ({@link mapSourceSilenceToRecord}, with a {@link SourceMatcher} so a span only lands on
 * clips of the file it was measured in) and hands this module record-frame spans; the module never
 * reads an artifact and never guesses which source a clip is.
 *
 * ## What is cut, and what is not
 *
 * A span is applied to every `mediaClip` on the chosen track it overlaps. The overlapping part is
 * removed with ripple (`extract`), so later material closes up — that is what "remove silence"
 * means in a derush. A span shorter than `minFrames` is skipped (a one-frame pause is not a gap a
 * viewer notices); `padFrames` keeps that many frames of the silence on each side so cuts breathe.
 * Spans are processed from the END of the timeline backwards, so an earlier cut's ripple never
 * moves a later span before it is cut. A span that covers a whole clip removes the whole clip.
 */

import { ApplySession, type EditOp } from '../engine';
import type { EditTimeline, MediaClip } from '../types';

/** A silence span in record (timeline) frames, `[startFrame, endFrame)`. */
export interface RecordSpan {
  startFrame: number;
  endFrame: number;
}

/** A silence span in source seconds, as a VideoAnalyze artifact reports it. */
export interface SourceSilenceSpan {
  id: string;
  startSec: number;
  endSec: number;
  /** Which of the analysis's sources the seconds are in (`sources[].sourceIndex`). Default 0. */
  sourceIndex?: number;
}

/**
 * Whether `clip` shows the analysed source `sourceIndex` (same project file). Built per analysis by
 * `sourceMatcher` in `lib/editing/api/analysis.ts`; a span or word is only mapped onto clips it
 * matches, so on a two-file track file A's seconds never land on file B.
 */
export type SourceMatcher = (clip: MediaClip, sourceIndex: number) => boolean;

export interface RemoveSilenceOptions {
  /** The track whose media clips are cut. */
  trackId: string;
  /** Spans in record frames; overlapping or unordered input is tolerated. */
  spans: readonly RecordSpan[];
  /** Spans shorter than this many frames (after padding) are left alone. Default 1. */
  minFrames?: number;
  /** Frames of the silence kept on each side of a cut. Default 0. */
  padFrames?: number;
  /** Fresh item ids for the blades, in the order they are needed. */
  newItemId: () => string;
  /**
   * R09.2: also cut the sound (or picture) LINKED to the cut clips, in the same program, so a linked
   * pair stays in sync. Default true; only clips linked to a clip on `trackId` are touched.
   */
  includeLinked?: boolean;
  /**
   * R09.9: instead of throwing the cut pieces away, park them on a switched-off holding track (one
   * per kind of track cut), at their old positions, so nothing is lost. `newTrackId` mints the
   * holding track ids; `name` names the picture one (the sound one adds " (sound)").
   */
  setAside?: { name: string; newTrackId: () => string };
}

export interface RemoveSilencePlan {
  /** The ops, in execution order. Empty when nothing qualifies. */
  ops: EditOp[];
  /** How many cuts (removed pieces) the plan makes. */
  cuts: number;
  /** Frames removed from the timeline in total (on the chosen track). */
  removedFrames: number;
  /** R09.2: how many linked clips on OTHER tracks were cut along with them. */
  linkedCuts: number;
  /** The document after the plan, for a preview. */
  preview: EditTimeline;
}

/**
 * Maps a clip's source-second silence onto its record frames, clipped to the clip's span. Returns
 * null when the silence does not touch the clip's source range, or when `matches` is given and the
 * clip does not show the span's source.
 */
export function mapSourceSilenceToRecord(clip: MediaClip, span: SourceSilenceSpan, fps: number, matches?: SourceMatcher): RecordSpan | null {
  if (matches && !matches(clip, span.sourceIndex ?? 0)) return null;
  // Source seconds do not map linearly onto a reversed clip, and a freeze frame shows one source frame: no span.
  if (clip.reverse === true || clip.hold === true) return null;
  const rate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const sourceIn = typeof clip.sourceIn === 'number' ? clip.sourceIn : 0;
  const duration = clip.recordOut - clip.recordIn;
  const sourceEnd = sourceIn + duration * rate;
  const silenceStart = span.startSec * fps;
  const silenceEnd = span.endSec * fps;
  if (silenceEnd <= sourceIn || silenceStart >= sourceEnd) return null;
  // A silence that reaches the clip's own start or end to within a frame covers it: rounding inward there
  // would leave a one-frame sliver of silence at the head or tail.
  const startFrame = silenceStart <= sourceIn + 1 ? clip.recordIn : clip.recordIn + Math.max(0, Math.ceil((silenceStart - sourceIn) / rate));
  const endFrame = silenceEnd >= sourceEnd - 1 ? clip.recordIn + duration : clip.recordIn + Math.min(duration, Math.floor((silenceEnd - sourceIn) / rate));
  return endFrame > startFrame ? { startFrame, endFrame } : null;
}

/** Merges overlapping/adjacent spans and drops empty ones; the result is sorted by start. */
export function normalizeSpans(spans: readonly RecordSpan[]): RecordSpan[] {
  const sorted = spans
    .filter((s) => Number.isInteger(s.startFrame) && Number.isInteger(s.endFrame) && s.endFrame > s.startFrame && s.startFrame >= 0)
    .map((s) => ({ ...s }))
    .sort((a, b) => a.startFrame - b.startFrame);
  const merged: RecordSpan[] = [];
  for (const span of sorted) {
    const last = merged[merged.length - 1];
    if (last && span.startFrame <= last.endFrame) last.endFrame = Math.max(last.endFrame, span.endFrame);
    else merged.push(span);
  }
  return merged;
}

/**
 * Builds the op program. Pure: the input document is never written to.
 */
export function compileRemoveSilence(doc: EditTimeline, options: RemoveSilenceOptions): RemoveSilencePlan {
  const minFrames = Math.max(1, options.minFrames ?? 1);
  const pad = Math.max(0, options.padFrames ?? 0);
  const track = doc.tracks.find((t) => t.id === options.trackId);
  if (!track) return { ops: [], cuts: 0, removedFrames: 0, linkedCuts: 0, preview: doc };

  // `padFrames` keeps a little silence next to the speech it borders. An edge that sits on the clip's own
  // start or end (a silent head or tail, or a gap between clips) borders no speech, so it is not padded:
  // padding it left a sliver of silence at the very start or end of the clip.
  const clipsOnTrack = track.items.filter((i): i is MediaClip => i.type === 'mediaClip');
  const padStart = (frame: number): number => {
    const c = clipsOnTrack.find((m) => m.recordIn <= frame && frame < m.recordOut);
    return c && frame > c.recordIn ? pad : 0;
  };
  const padEnd = (frame: number): number => {
    const c = clipsOnTrack.find((m) => m.recordIn < frame && frame <= m.recordOut);
    return c && frame < c.recordOut ? pad : 0;
  };
  const spans = normalizeSpans(options.spans)
    .map((s) => ({ startFrame: s.startFrame + padStart(s.startFrame), endFrame: s.endFrame - padEnd(s.endFrame) }))
    .filter((s) => s.endFrame - s.startFrame >= minFrames)
    // Rightmost first: an extract ripples everything after it to the left, so a span to its right
    // would move; cutting from the end keeps every remaining span's frames valid.
    .sort((a, b) => b.startFrame - a.startFrame);

  const ops: EditOp[] = [];
  let current = doc;
  let cuts = 0;
  let removed = 0;

  // One working copy for the whole program (not a clone per op); `current` is that copy, so no reference into it is kept across a `run`.
  let session: ApplySession | null = null;
  const run = (op: EditOp) => {
    session ??= new ApplySession(doc);
    session.apply(op);
    current = session.doc;
    ops.push(op);
  };

  const holding = new Map<string, string>();
  const holdingFor = (kind: 'video' | 'audio'): string | null => {
    if (!options.setAside) return null;
    const known = holding.get(kind);
    if (known) return known;
    const existing = current.tracks.find((t) => t.kind === kind && t.name === (kind === 'video' ? options.setAside?.name : `${options.setAside?.name} (sound)`));
    if (existing) {
      holding.set(kind, existing.id);
      return existing.id;
    }
    const id = options.setAside.newTrackId();
    const index = kind === 'video' ? current.tracks.filter((t) => t.kind === 'video').length : current.tracks.length;
    run({ type: 'addTrack', index, track: { id, kind, name: kind === 'video' ? options.setAside.name : `${options.setAside.name} (sound)`, height: 40, locked: false, enabled: false, mute: false, solo: false, items: [] } });
    holding.set(kind, id);
    return id;
  };
  const parkPiece = (kind: 'video' | 'audio', piece: MediaClip) => {
    const holdingId = holdingFor(kind);
    if (!holdingId) return;
    const items = (current.tracks.find((t) => t.id === holdingId) as { items: Array<{ recordIn: number; recordOut: number }> }).items;
    const length = piece.recordOut - piece.recordIn;
    const free = !items.some((i) => i.recordIn < piece.recordOut && i.recordOut > piece.recordIn);
    const recordIn = free ? piece.recordIn : items.reduce((end, i) => Math.max(end, i.recordOut), 0);
    run({ type: 'placeOnTop', trackId: holdingId, item: { ...piece, id: options.newItemId(), recordIn, recordOut: recordIn + length, linkId: null } });
  };

  /** Cuts `cutSpans` (rightmost first) out of `trackId`; `only` limits it to those clip ids. Returns [cuts, frames]. */
  const cutTrack = (trackId: string, cutSpans: readonly RecordSpan[], only: ReadonlySet<string> | null): [number, number] => {
    let trackCuts = 0;
    let trackRemoved = 0;
    for (const span of cutSpans) {
      const live = current.tracks.find((t) => t.id === trackId);
      if (!live) break;
      // Clips overlapping the span, rightmost first for the same ripple reason.
      const clips = live.items
        .filter((i): i is MediaClip => i.type === 'mediaClip' && i.recordIn < span.endFrame && i.recordOut > span.startFrame && (only === null || only.has(i.id)))
        .sort((a, b) => b.recordIn - a.recordIn);
      for (const clip of clips) {
        const clipIn = clip.recordIn;
        const clipOut = clip.recordOut;
        const cutStart = Math.max(clipIn, span.startFrame);
        const cutEnd = Math.min(clipOut, span.endFrame);
        if (cutEnd <= cutStart) continue;

        let removedId = clip.id;
        // Blade off the tail that survives (right of the cut), then the head (left of the cut); the
        // piece to extract is whichever id ends up covering [cutStart, cutEnd).
        if (cutEnd < clipOut) {
          run({ type: 'blade', itemId: removedId, atFrame: cutEnd, newItemId: options.newItemId() });
        }
        if (cutStart > clipIn) {
          const rightId = options.newItemId();
          run({ type: 'blade', itemId: removedId, atFrame: cutStart, newItemId: rightId });
          removedId = rightId;
        }
        const found = current.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.id === removedId && i.type === 'mediaClip');
        const piece = found && options.setAside ? structuredClone(found) : undefined;
        run({ type: 'extract', itemId: removedId });
        if (piece && options.setAside) parkPiece(live.kind, piece);
        trackCuts++;
        trackRemoved += cutEnd - cutStart;
      }
    }
    return [trackCuts, trackRemoved];
  };

  // R09.2: the clips on other tracks linked to this track's clips, found BEFORE the cut (blades
  // copy the link id, so the pieces stay linked to each other).
  const linkIds = new Set<string>();
  for (const item of track.items) if (item.type === 'mediaClip' && typeof item.linkId === 'string') linkIds.add(item.linkId);
  if (options.includeLinked === false) linkIds.clear();
  const partnerTracks: Array<{ trackId: string; ids: Set<string> }> = [];
  if (linkIds.size > 0) {
    for (const other of doc.tracks) {
      if (other.id === options.trackId) continue;
      const ids = new Set(other.items.filter((i): i is MediaClip => i.type === 'mediaClip' && typeof i.linkId === 'string' && linkIds.has(i.linkId)).map((i) => i.id));
      if (ids.size > 0) partnerTracks.push({ trackId: other.id, ids });
    }
  }

  // A picture and its sound that were never linked (a timeline made from a render has them side by side, same source, same
  // span) are a pair all the same: cutting only the picture would leave the sound out of step. Only exact twins qualify.
  if (options.includeLinked !== false) {
    const unlinked = (c: MediaClip): boolean => typeof c.linkId !== 'string' || c.linkId.length === 0;
    const twinKey = (c: MediaClip): string =>
      `${c.assetRef.kind}|${c.assetRef.projectFileId ?? ''}|${c.assetRef.stepResultId ?? ''}|${c.sourceIn ?? 0}|${c.recordIn}|${c.recordOut}|${c.playbackRate ?? 1}|${c.reverse === true}|${c.hold === true}`;
    const wanted = new Set(track.items.filter((i): i is MediaClip => i.type === 'mediaClip' && unlinked(i)).map(twinKey));
    if (wanted.size > 0) {
      for (const other of doc.tracks) {
        if (other.id === options.trackId || other.kind === track.kind) continue;
        const twins = other.items.filter((i): i is MediaClip => i.type === 'mediaClip' && unlinked(i) && wanted.has(twinKey(i))).map((i) => i.id);
        if (twins.length === 0) continue;
        const existing = partnerTracks.find((p) => p.trackId === other.id);
        if (existing) for (const id of twins) existing.ids.add(id);
        else partnerTracks.push({ trackId: other.id, ids: new Set(twins) });
      }
    }
  }

  const everLinked = new Set<string>();
  for (const item of track.items) if (item.type === 'mediaClip' && typeof item.linkId === 'string') everLinked.add(item.linkId);
  [cuts, removed] = cutTrack(options.trackId, spans, null);
  let linkedCuts = 0;
  for (const partner of partnerTracks) linkedCuts += cutTrack(partner.trackId, spans, partner.ids)[0];
  // A blade copies the link id to both halves, which would leave two clips of one group on one
  // track; re-pair the pieces that still line up and unlink the rest (one op each).
  for (const op of relinkOps(current, everLinked, options.newItemId)) run(op);

  return { ops, cuts, removedFrames: removed, linkedCuts, preview: current };
}

/**
 * After a program that cut linked clips: clips of the affected link groups that still show the same
 * source over the same record span on different tracks are one pair again (a fresh shared link id);
 * a clip left alone is unlinked. Returns only the `setProperty` ops that change something.
 */
export function relinkOps(doc: EditTimeline, affected: ReadonlySet<string>, newLinkId: () => string): EditOp[] {
  if (affected.size === 0) return [];
  const clusters = new Map<string, MediaClip[]>();
  const perLink = new Map<string, number>();
  for (const t of doc.tracks) for (const item of t.items) if (item.type === 'mediaClip' && typeof item.linkId === 'string') perLink.set(item.linkId, (perLink.get(item.linkId) ?? 0) + 1);
  for (const t of doc.tracks) {
    for (const item of t.items) {
      if (item.type !== 'mediaClip' || typeof item.linkId !== 'string' || !affected.has(item.linkId)) continue;
      const ref = item.assetRef;
      const key = `${item.linkId}|${item.recordIn}|${item.recordOut}|${ref.kind}|${ref.projectFileId ?? ''}|${ref.stepResultId ?? ''}`;
      const list = clusters.get(key) ?? [];
      list.push(item);
      clusters.set(key, list);
    }
  }
  const trackOf = new Map<string, string>();
  for (const t of doc.tracks) for (const item of t.items) trackOf.set(item.id, t.id);
  const ops: EditOp[] = [];
  for (const members of clusters.values()) {
    const tracks = new Set(members.map((m) => trackOf.get(m.id)));
    const paired = members.length >= 2 && tracks.size === members.length;
    // An untouched pair keeps its link as it is.
    if (paired && perLink.get(members[0].linkId as string) === members.length) continue;
    const fresh = paired ? newLinkId() : null;
    for (const m of members) ops.push({ type: 'setProperty', itemId: m.id, key: 'linkId', value: fresh });
  }
  return ops;
}
