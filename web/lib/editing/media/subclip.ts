/**
 * Subclip descriptors — a pure conversion from a marked range to a typed, still-unpersisted value.
 *
 * ## Nothing here is written anywhere
 *
 * `createSubclip` returns a value to its caller. It issues no request, touches no store and
 * produces no timeline op, and in this phase (P2) that is deliberate rather than unfinished:
 * persisting a subclip needs the P1 document/engine and the ops P4 adds, and a media-pool screen
 * that silently wrote a timeline is exactly the hidden write this initiative forbids. The viewer
 * that calls this shows the descriptor and says plainly that it is not saved.
 *
 * ## Frames are computed rationally, never from a float rate
 *
 * The probe carries `fpsNum`/`fpsDen` precisely so this stays exact: 30000/1001 is never reduced to
 * a 29.97 double. The drift is real — see `framesFromSeconds`'s note in `in-out.ts` and the pinned
 * case in `__tests__/subclip.test.ts`.
 *
 * ## A source with no probed frame rate produces NO descriptor
 *
 * Marks can fall back to decoded-frame terms when a source has a duration but no rate (see
 * `in-out.ts`). A `SubclipDescriptor` cannot: `sourceInFrames`/`sourceOutFrames` are positions in
 * the SOURCE's own timebase, and a decoded-frame index is not one. Rather than write a number that
 * means a different thing than its field name claims, `createSubclip` returns `null`.
 */

import type { MediaRef, MediaProbe } from './types';
import { framesFromSeconds, rationalOf } from './in-out';

/**
 * The minimum an item must expose to describe a subclip of it.
 *
 * Structurally a `MediaPoolItem` (from `./types`), which satisfies it — narrowed here so a test
 * (and P3, which mounts the viewer beside the timeline) can build one without the fields a
 * descriptor never reads.
 */
export interface SubclipSource {
  mediaRef: MediaRef;
  probe: MediaProbe | null;
}

/**
 * A marked range of one source, as a candidate clip.
 *
 * **Not persisted, and not yet a `MediaClip`.** It carries no `recordIn`/`recordOut` because there
 * is no timeline to place it on: the descriptor names a source and a range, and P4's `insert` op is
 * what will give it a position.
 */
export interface SubclipDescriptor {
  /** The source the range is cut from — the OPAQUE pool token, passed through untouched. */
  mediaRef: MediaRef;
  /** First frame of the range, inclusive, in the source's own timebase. */
  sourceInFrames: number;
  /** One past the last frame of the range, exclusive (matching `ItemBase.recordOut`). */
  sourceOutFrames: number;
  /** The subclip's own length in frames — always `sourceOutFrames - sourceInFrames`, ≥ 1. */
  durationFrames: number;
  /** Display name derived from the file name and the frame range. */
  name: string;
  /**
   * The source's total length in frames, from the probe's rational rate and duration, or `null`
   * when the probe carries no duration.
   *
   * Carried alongside the range so a consumer can tell a full-length clip from a trimmed one
   * without re-deriving the source's frame count from the probe itself.
   */
  sourceDurationFrames: number | null;
}

/** Shown when a file name carries nothing usable — never an empty string. */
const FALLBACK_STEM = 'Subclip';

/**
 * A display name for a subclip: the file's stem plus its frame range.
 *
 * `b-roll-city.mp4` + 30..120 → `b-roll-city [30-120]`. Frame numbers rather than timecodes, for
 * the same reason every other value here is in frames: a timecode needs a rate, and this name is
 * produced for sources that may have none.
 *
 * A name with no extension is used whole; a leading-dot name (`.gitignore`) is treated as a stem
 * rather than as extension-less garbage; anything empty degrades to {@link FALLBACK_STEM}.
 */
export function subclipName(
  fileName: string | null | undefined,
  inFrame: number,
  outFrame: number,
): string {
  const raw = typeof fileName === 'string' ? fileName.trim() : '';
  const dot = raw.lastIndexOf('.');
  const stem = dot > 0 ? raw.slice(0, dot) : raw;
  const safeStem = stem.trim().length > 0 ? stem.trim() : FALLBACK_STEM;
  return `${safeStem} [${inFrame}-${outFrame}]`;
}

/**
 * Builds a subclip descriptor from a marked range, or `null` when the range cannot be described
 * honestly.
 *
 * Returns `null` for each of these, and for no other reason:
 *
 *   - the source is missing, or its `mediaRef` is empty — there is nothing to point at;
 *   - the probe carries no usable rational frame rate, so source-frame indices have no meaning;
 *   - either frame is non-finite or not a whole number;
 *   - `outFrame <= inFrame` — the minimum range is one frame;
 *   - either frame falls outside `[0, sourceDurationFrames]` when the probe gives a duration.
 *
 * Out-of-range input is REJECTED rather than clamped. The viewer clamps interactively (through
 * `in-out.ts`'s reducer) against the same bounds, so a frame outside them is a caller error, and
 * silently moving it would hide that.
 */
export function createSubclip(
  source: SubclipSource | null | undefined,
  inFrame: number,
  outFrame: number,
  fileName: string | null | undefined,
): SubclipDescriptor | null {
  if (!source || typeof source.mediaRef !== 'string' || source.mediaRef.length === 0) return null;

  const fps = rationalOf(source.probe);
  if (!fps) return null;

  if (!isWholeFrame(inFrame) || !isWholeFrame(outFrame)) return null;
  if (outFrame <= inFrame) return null;

  const sourceDurationFrames = framesFromSeconds(source.probe?.durationSec ?? null, fps);
  if (sourceDurationFrames !== null && (inFrame < 0 || outFrame > sourceDurationFrames)) {
    return null;
  }

  return {
    mediaRef: source.mediaRef,
    sourceInFrames: inFrame,
    sourceOutFrames: outFrame,
    durationFrames: outFrame - inFrame,
    name: subclipName(fileName, inFrame, outFrame),
    sourceDurationFrames,
  };
}

const isWholeFrame = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value) && Number.isInteger(value);
