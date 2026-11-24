/**
 * Putting a media-pool source on the timeline — pure, so every rule below is pinned by a test.
 *
 * ## The clip's length comes from the probe, and its range from the source viewer's marks
 *
 * A source with a probed duration goes in at its true length (a 12 s source at 30 fps is 360
 * frames). When the source viewer holds In/Out marks (source frames, at the probe's own rational
 * rate) the clip covers exactly that range: `sourceIn` is the In mark and the length is Out − In,
 * both converted to timeline frames. Only when nothing is known (the file has not been prepared,
 * `probe` is null) does the clip fall back to a fixed placeholder length, and the result says so
 * (`lengthKnown: false`) so the UI can tell the user instead of silently truncating.
 *
 * ## Footage sound goes on a paired audio track, in the same op batch
 *
 * A video track plays picture only; the footage's own sound reaches the mix through an audio-track
 * clip of the same source. So a pool insert of a video WITH an audio stream (and the "Extract
 * audio" clip action) also produces the companion clip — same source, same `sourceIn`, same span —
 * on a paired audio track named {@link PAIRED_AUDIO_TRACK_NAME}. Everything is plain existing ops
 * (`insert`/`append` for the picture, `insert` or `addTrack` for the sound), returned as ONE list
 * the store applies as one history entry, so one undo removes both. R08.1: the two clips share a
 * fresh `linkId`, so link-aware editing (`lib/editing/link`) moves, trims, blades and deletes them
 * together. A source without an audio stream gets no companion and no link.
 */

import { withDetectedProfile } from '../color/auto-profile';
import { compileSmartInsert } from '../cut/cut';
import type { EditOp } from '../engine';
import type { EditTimeline, Item, MediaClip, Rational, Track } from '../types';
import { assetRefFromMediaRef } from '../api/media';
import { framesFromSeconds, rationalOf, secondsFromFrames, type Marks } from './in-out';
import type { MediaPoolItem, MediaProbe } from './types';

/** The length a source of unknown duration (or a still) is given, in seconds. */
export const FALLBACK_CLIP_SECONDS = 5;

/** The name of the audio track that carries footage sound. Further ones are numbered. */
export const PAIRED_AUDIO_TRACK_NAME = 'Camera audio';

/** Shown when a source goes in at the placeholder length because nothing is known about it yet. */
export const LENGTH_UNKNOWN_NOTE =
  'Length unknown until the file is prepared, so it goes in at 5 seconds. Trim it once the file is ready.';

export interface PoolClipSpan {
  /** Offset into the source, in timeline frames. */
  sourceIn: number;
  /** The clip's length, in timeline frames (≥ 1). */
  durationFrames: number;
  /** False when the length is the placeholder because the source has not been probed. */
  lengthKnown: boolean;
  /** True when the span came from the source viewer's In/Out marks. */
  fromMarks: boolean;
}

const fpsOf = (rate: Rational): number => (rate.den > 0 && rate.num > 0 ? rate.num / rate.den : 30);

const toTimelineFrames = (seconds: number, rate: Rational): number => Math.round((seconds * rate.num) / rate.den);

/** The probe's duration in seconds, or null when it was not probed. */
function probedSeconds(probe: MediaProbe | null): number | null {
  const sec = probe?.durationSec;
  return typeof sec === 'number' && Number.isFinite(sec) && sec > 0 ? sec : null;
}

/**
 * Whether the source carries an audio stream: `true`/`false` when the probe says, `null` when it
 * does not know (no probe, or a probe that never looked at audio).
 */
export function hasAudioStream(item: Pick<MediaPoolItem, 'kind' | 'probe'>): boolean | null {
  if (item.kind === 'audio') return true;
  if (item.kind === 'image') return false;
  const probe = item.probe;
  if (!probe) return null;
  if (typeof probe.hasAudio === 'boolean') return probe.hasAudio;
  if (typeof probe.audioCodec === 'string' && probe.audioCodec.trim().length > 0) return true;
  if (typeof probe.audioChannels === 'number') return probe.audioChannels > 0;
  // A probe that read the video stream but reported nothing about audio: no audio stream.
  if (typeof probe.videoCodec === 'string' && probe.videoCodec.trim().length > 0) return false;
  return null;
}

/**
 * The span a pool source takes on a timeline at `rate`, honouring the source viewer's marks.
 *
 * Marks count only when they are in the source's own timebase (a probe with a frame rate); a mark
 * in decoded frames has no exact time and is ignored rather than guessed at.
 */
export function poolClipSpan(item: Pick<MediaPoolItem, 'kind' | 'probe'>, rate: Rational, marks?: Marks | null): PoolClipSpan {
  const fallback = Math.max(1, Math.round(FALLBACK_CLIP_SECONDS * fpsOf(rate)));
  if (item.kind === 'image') return { sourceIn: 0, durationFrames: fallback, lengthKnown: true, fromMarks: false };

  const seconds = probedSeconds(item.probe);
  const sourceRate = rationalOf(item.probe);
  const inMark = marks?.inFrame ?? null;
  const outMark = marks?.outFrame ?? null;
  if (sourceRate && (inMark !== null || outMark !== null)) {
    const inSec = inMark !== null ? secondsFromFrames(inMark, sourceRate) : 0;
    const outSec = outMark !== null ? secondsFromFrames(outMark, sourceRate) : seconds;
    if (inSec !== null && outSec !== null && outSec > inSec) {
      const sourceIn = toTimelineFrames(inSec, rate);
      const durationFrames = Math.max(1, toTimelineFrames(outSec, rate) - sourceIn);
      return { sourceIn, durationFrames, lengthKnown: true, fromMarks: true };
    }
  }

  if (seconds !== null) {
    return { sourceIn: 0, durationFrames: Math.max(1, toTimelineFrames(seconds, rate)), lengthKnown: true, fromMarks: false };
  }
  return { sourceIn: 0, durationFrames: fallback, lengthKnown: false, fromMarks: false };
}

/** The source frame count a set of marks covers, for naming (`clip [in-out]`). */
function rangeName(fileName: string, marks: Marks | null | undefined, probe: MediaProbe | null): string {
  if (!marks || (marks.inFrame === null && marks.outFrame === null)) return fileName;
  const total = framesFromSeconds(probedSeconds(probe), rationalOf(probe));
  const inFrame = marks.inFrame ?? 0;
  const outFrame = marks.outFrame ?? total;
  if (outFrame === null) return fileName;
  const dot = fileName.lastIndexOf('.');
  const stem = dot > 0 ? fileName.slice(0, dot) : fileName;
  return `${stem} [${inFrame}-${outFrame}]`;
}

/** A media clip of a pool source, spanning [0, durationFrames) — placement sets the real span. */
export function clipFromPoolItem(
  item: MediaPoolItem,
  rate: Rational,
  newId: () => string,
  marks?: Marks | null,
): { clip: MediaClip; span: PoolClipSpan } | null {
  const assetRef = assetRefFromMediaRef(item.mediaRef);
  if (!assetRef) return null;
  const span = poolClipSpan(item, rate, marks);
  const clip: MediaClip = {
    id: newId(),
    type: 'mediaClip',
    name: span.fromMarks ? rangeName(item.fileName, marks, item.probe) : item.fileName,
    recordIn: 0,
    recordOut: span.durationFrames,
    assetRef,
    sourceIn: span.sourceIn,
    playbackRate: 1,
    enabled: true,
    provenance: { origin: 'manual', editedByUser: true },
  };
  return { clip, span };
}

// ─────────────────────────────────────────────────────────────────────────────
// The paired audio track
// ─────────────────────────────────────────────────────────────────────────────

export type IsLocked = (track: Track) => boolean;

const defaultIsLocked: IsLocked = (track) => track.locked;

const isPairedName = (name: string): boolean =>
  name === PAIRED_AUDIO_TRACK_NAME || new RegExp(`^${PAIRED_AUDIO_TRACK_NAME} \\d+$`).test(name);

/**
 * The `insert` that puts `item` at its own `recordIn` on `track` without touching anything, or with
 * a whole-length ripple when the following item starts exactly there — the two positions the engine
 * accepts. Null when the span would partly overlap an item.
 */
function insertAt(track: Track, item: Item): EditOp | null {
  const index = track.items.filter((i) => i.recordIn < item.recordIn).length;
  const previous = index > 0 ? track.items[index - 1] : undefined;
  if (previous && previous.recordOut > item.recordIn) return null;
  const following = track.items[index];
  if (following && following.recordIn < item.recordOut && following.recordIn !== item.recordIn) return null;
  return { type: 'insert', trackId: track.id, index, item };
}

/** Where a new audio track goes: above the first existing audio track (A1), else at the bottom. */
function newAudioTrackIndex(doc: EditTimeline): number {
  const firstAudio = doc.tracks.findIndex((t) => t.kind === 'audio');
  return firstAudio >= 0 ? firstAudio : doc.tracks.length;
}

function nextPairedName(doc: EditTimeline): string {
  const taken = new Set(doc.tracks.map((t) => t.name));
  if (!taken.has(PAIRED_AUDIO_TRACK_NAME)) return PAIRED_AUDIO_TRACK_NAME;
  for (let n = 2; ; n++) if (!taken.has(`${PAIRED_AUDIO_TRACK_NAME} ${n}`)) return `${PAIRED_AUDIO_TRACK_NAME} ${n}`;
}

/**
 * The op that puts `companion` (already spanned) on a paired audio track: an `insert` on the first
 * unlocked paired track that can take it, otherwise an `addTrack` that creates one holding it.
 * `ripple` allows the whole-length ripple a smart insert performs on the picture track.
 */
export function companionAudioOp(doc: EditTimeline, companion: MediaClip, newId: () => string, options: { ripple: boolean; isLocked?: IsLocked }): EditOp {
  const isLocked = options.isLocked ?? defaultIsLocked;
  for (const track of doc.tracks) {
    if (track.kind !== 'audio' || !isPairedName(track.name) || isLocked(track)) continue;
    const op = insertAt(track, companion);
    if (!op || op.type !== 'insert') continue;
    const following = track.items[op.index];
    if (!options.ripple && following && following.recordIn < companion.recordOut) continue;
    return op;
  }
  const track: Track = {
    id: newId(),
    kind: 'audio',
    name: nextPairedName(doc),
    color: null,
    height: 56,
    locked: false,
    enabled: true,
    mute: false,
    solo: false,
    busId: null,
    items: [companion],
  };
  return { type: 'addTrack', index: newAudioTrackIndex(doc), track };
}

// ─────────────────────────────────────────────────────────────────────────────
// Pool placement
// ─────────────────────────────────────────────────────────────────────────────

export interface PoolPlacementInput {
  item: MediaPoolItem;
  /** The track the picture (or, for an audio source, the sound) goes on. */
  track: Track;
  where: 'playhead' | 'end';
  playheadFrame: number;
  rate: Rational;
  marks?: Marks | null;
  newId: () => string;
  isLocked?: IsLocked;
  /** Override the clip's name (Create subclip names it after the marked range). */
  name?: string;
}

export interface PoolPlacement {
  ops: EditOp[];
  clipId: string;
  /** The companion audio clip's id, when the source's sound was placed too. */
  companionId: string | null;
  /** The link id shared by the picture and its companion sound; null when there is no companion. */
  linkId: string | null;
  atFrame: number;
  span: PoolClipSpan;
  label: string;
}

/**
 * The op batch that places a pool source on `track`: `insert` at the edit point nearest the
 * playhead (rippling what follows) or `append` at the track's end, plus — for a video with an audio
 * stream placed on a video track — the companion clip on the paired audio track at the same frame.
 * Null when the source cannot be addressed or the placement is impossible.
 */
export function compilePoolPlacement(doc: EditTimeline, input: PoolPlacementInput): PoolPlacement | null {
  const built = clipFromPoolItem(input.item, input.rate, input.newId, input.marks);
  if (!built) return null;
  const { clip, span } = built;
  if (input.name) clip.name = input.name;

  const wantsCompanion = input.track.kind === 'video' && input.item.kind === 'video' && hasAudioStream(input.item) === true;
  const linkId = wantsCompanion ? input.newId() : null;
  if (linkId !== null) clip.linkId = linkId;

  const ops: EditOp[] = [];
  let atFrame: number;
  if (input.where === 'end') {
    const last = input.track.items[input.track.items.length - 1];
    atFrame = last ? last.recordOut : 0;
    ops.push({ type: 'append', trackId: input.track.id, item: clip });
  } else {
    const plan = compileSmartInsert(doc, input.track.id, clip, input.playheadFrame);
    if (!plan) return null;
    atFrame = plan.atFrame;
    ops.push(plan.op);
  }

  let companionId: string | null = null;
  if (wantsCompanion) {
    const companion: MediaClip = { ...clip, id: input.newId(), recordIn: atFrame, recordOut: atFrame + span.durationFrames };
    ops.push(companionAudioOp(doc, companion, input.newId, { ripple: input.where === 'playhead', isLocked: input.isLocked }));
    companionId = companion.id;
  }

  const verb = input.where === 'end' ? 'Append' : 'Insert';
  return { ops: withDetectedProfile(ops, input.item.probe, clip.id, input.item.kind), clipId: clip.id, companionId, linkId, atFrame, span, label: `${verb} ${clip.name}${companionId ? ' with its sound' : ''}` };
}

// ─────────────────────────────────────────────────────────────────────────────
// Extract audio
// ─────────────────────────────────────────────────────────────────────────────

export interface ExtractAudioPlan {
  ops: EditOp[];
  companionId: string;
}

/**
 * "Extract audio" on a media clip on a video track: the same source at the same span, `sourceIn`,
 * rate and direction, on the paired audio track (created when no paired track has room). Nothing
 * else moves. Returns a reason string when the clip cannot give up its sound.
 */
export function compileExtractAudio(doc: EditTimeline, itemId: string, newId: () => string, isLocked?: IsLocked): ExtractAudioPlan | string {
  const track = doc.tracks.find((t) => t.items.some((i) => i.id === itemId));
  const item = track?.items.find((i) => i.id === itemId);
  if (!track || !item) return 'That clip is no longer on the timeline.';
  if (item.type !== 'mediaClip') return 'Only footage clips have sound to extract.';
  if (track.kind !== 'video') return 'This clip is already on an audio track.';
  if (item.hold) return 'A freeze frame has no sound to extract.';
  const companion: MediaClip = {
    id: newId(),
    type: 'mediaClip',
    name: item.name,
    recordIn: item.recordIn,
    recordOut: item.recordOut,
    assetRef: item.assetRef,
    sourceIn: item.sourceIn ?? 0,
    playbackRate: item.playbackRate ?? 1,
    enabled: true,
    provenance: { origin: 'manual', editedByUser: true },
  };
  if (item.reverse) companion.reverse = true;
  // R08.1: the sound joins the picture's link group (a new one when the picture was not linked yet).
  const linkId = item.linkId ?? newId();
  companion.linkId = linkId;
  const ops: EditOp[] = [companionAudioOp(doc, companion, newId, { ripple: false, isLocked })];
  if (item.linkId == null) ops.push({ type: 'setProperty', itemId: item.id, key: 'linkId', value: linkId });
  return { ops, companionId: companion.id };
}
