/**
 * In/out marks — the pure reducer behind the source viewer's marking controls, and the *frame
 * domain* those marks live in.
 *
 * ## Marks are VIEW state, never document state
 *
 * Nothing in this module performs I/O, and nothing that calls it may: changing an in/out mark is a
 * local UI gesture. It produces **no request** and **no timeline revision**, which is this
 * initiative's standing rule — the marks exist so a human can look at a range, and only an explicit
 * "create subclip" turns them into a `SubclipDescriptor` (see `subclip.ts`), which in this phase is
 * not persisted either.
 *
 * ## Two frame domains, because a probe may carry no frame rate
 *
 * A `MediaProbe` is a projection of a `VideoAnalyze` artifact, and `fps` is nullable — a source can
 * be probed for a duration and never for a rate (or not probed at all). A mark therefore lives in
 * one of three domains, decided by {@link resolveMarkDomain}:
 *
 *   - `timebase` — the probe carries a usable RATIONAL rate, so a mark is a frame index in the
 *     source's own timebase and the upper bound is `round(durationSec × fpsNum / fpsDen)`.
 *   - `decoded` — no usable rate, but a known duration. The marks fall back to **decoded-frame
 *     terms**: the count the decoder itself reports for this media element, which a caller supplies
 *     because it is the only thing that can observe it. It is deliberately NOT estimated from
 *     `durationSec` and a nominal rate — that guess is exactly what the plan forbids.
 *   - `unavailable` — no duration AND no rate, so no frame can honestly be named. Marking is
 *     disabled with a stated reason rather than offering a control that would produce `NaN`.
 *
 * ## The arithmetic is rational, never a float rate
 *
 * `framesFromSeconds` multiplies by the numerator and only then divides by the denominator, so
 * 30000/1001 never becomes a 29.97 double on the way in. That distinction is load-bearing on real
 * media: at 17 s of 30000/1001, the exact frame count is 509, while rounding the rate to 30 first
 * gives 510 — a one-frame drift, pinned by `__tests__/in-out.test.ts`.
 */

import type { Rational } from '../types';
import type { MediaProbe } from './types';

// ─────────────────────────────────────────────────────────────────────────────
// Marks
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An in/out range in whole frames.
 *
 * `outFrame` is **exclusive**, matching `ItemBase.recordOut` in the document model, so the shortest
 * legal range spans exactly one frame (`outFrame === inFrame + 1`). Either end may be unset
 * independently: a user sets one mark, looks at it, and sets the other.
 */
export interface Marks {
  inFrame: number | null;
  outFrame: number | null;
}

/**
 * The empty marking, as a single frozen constant.
 *
 * Sharing one object is what makes "no change" observable by IDENTITY: a no-op call returns the
 * very same reference, so a caller (and `React.useState`) can tell "nothing happened" without
 * deep-comparing, and a repeated identical mark cannot cause a re-render.
 */
export const NO_MARKS: Marks = Object.freeze({ inFrame: null, outFrame: null });

/** Stated reason shown when the source was never analysed at all. */
export const NOT_ANALYSED_MARK_REASON =
  'This source has not been analysed, so its duration and frame rate are unknown. Setting marks is disabled.';

/** Stated reason shown when the source exists but carries neither a duration nor a frame rate. */
export const NO_TIMEBASE_MARK_REASON =
  'This source has no probed duration and no probed frame rate, so its frames cannot be counted. Setting marks is disabled.';

/**
 * The domain an in/out mark is expressed in.
 *
 * `totalFrames` is the exclusive upper bound in the SAME unit as the marks, or `null` when it is
 * not known yet — never `Infinity`, never a guess. A `null` bound leaves marking enabled and clamps
 * only the lower end; it does not silently invent an end-of-source.
 */
export type MarkDomain =
  | {
      kind: 'timebase';
      /** The probe's exact rational rate. Never pre-divided. */
      fps: Rational;
      /** `round(durationSec × num / den)`, or `null` when the duration is unknown. */
      totalFrames: number | null;
    }
  | {
      kind: 'decoded';
      /** Frames the decoder reports for this element, or `null` until it reports them. */
      totalFrames: number | null;
    }
  | { kind: 'unavailable'; reason: string };

// ─────────────────────────────────────────────────────────────────────────────
// Rate arithmetic
// ─────────────────────────────────────────────────────────────────────────────

const isUsableNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);

/**
 * The probe's frame rate as an exact rational, or `null` when it cannot be used as one.
 *
 * A non-positive or non-finite numerator or denominator is rejected rather than divided by: those
 * are "unknown", and returning `Infinity`/`NaN` from here is what would eventually put `NaN` in the
 * DOM.
 */
export function rationalOf(probe: MediaProbe | null | undefined): Rational | null {
  if (!probe || typeof probe !== 'object') return null;
  const { fpsNum, fpsDen } = probe;
  if (!isUsableNumber(fpsNum) || !isUsableNumber(fpsDen)) return null;
  if (fpsNum <= 0 || fpsDen <= 0) return null;
  return { num: fpsNum, den: fpsDen };
}

/**
 * Seconds → frames at a rational rate, exactly, or `null` when either input is unusable.
 *
 * The numerator is multiplied BEFORE the division, so the rate never becomes a rounded double.
 * `null` — not `0`, not `NaN` — is the answer for an unknown duration, an unknown rate, a negative
 * duration or a non-finite one: "we do not know" is a different fact from "zero frames".
 */
export function framesFromSeconds(
  seconds: number | null | undefined,
  rate: Rational | null | undefined,
): number | null {
  if (!isUsableNumber(seconds) || seconds < 0) return null;
  if (!rate || !isUsableNumber(rate.num) || !isUsableNumber(rate.den)) return null;
  if (rate.num <= 0 || rate.den <= 0) return null;
  return Math.round((seconds * rate.num) / rate.den);
}

/** Frames → seconds at a rational rate — the inverse used to seek a `<video>` element. */
export function secondsFromFrames(
  frames: number | null | undefined,
  rate: Rational | null | undefined,
): number | null {
  if (!isUsableNumber(frames) || frames < 0) return null;
  if (!rate || !isUsableNumber(rate.num) || !isUsableNumber(rate.den)) return null;
  if (rate.num <= 0 || rate.den <= 0) return null;
  return (frames * rate.den) / rate.num;
}

// ─────────────────────────────────────────────────────────────────────────────
// Domain resolution
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Which domain this source's marks live in.
 *
 * `decodedTotalFrames` is the decoder's own frame count when the caller can observe one (a
 * `<video>` element exposes it through `getVideoPlaybackQuality()`); it is used ONLY in the
 * `decoded` case, where it is the sole honest source of a bound.
 */
export function resolveMarkDomain(
  probe: MediaProbe | null | undefined,
  decodedTotalFrames: number | null | undefined = null,
): MarkDomain {
  if (!probe || typeof probe !== 'object') {
    return { kind: 'unavailable', reason: NOT_ANALYSED_MARK_REASON };
  }

  const fps = rationalOf(probe);
  if (fps) {
    return { kind: 'timebase', fps, totalFrames: framesFromSeconds(probe.durationSec, fps) };
  }

  if (isUsableNumber(probe.durationSec) && probe.durationSec >= 0) {
    const decoded = isUsableNumber(decodedTotalFrames) && decodedTotalFrames >= 0
      ? Math.round(decodedTotalFrames)
      : null;
    return { kind: 'decoded', totalFrames: decoded };
  }

  return { kind: 'unavailable', reason: NO_TIMEBASE_MARK_REASON };
}

/** Whether this domain can carry a mark at all. */
export function isMarkable(domain: MarkDomain): boolean {
  return domain.kind !== 'unavailable';
}

// ─────────────────────────────────────────────────────────────────────────────
// The reducer
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Clamps a frame into the domain's bounds, or `null` when the request must be ignored.
 *
 * `null` is returned for: an unusable (non-finite) frame, and for ANY frame in an `unavailable`
 * domain — marking is disabled there, and a reducer that quietly accepted one anyway would be the
 * back door around the disabled control.
 */
function clampFrame(frame: number, domain: MarkDomain): number | null {
  if (domain.kind === 'unavailable') return null;
  if (!isUsableNumber(frame)) return null;

  const rounded = Math.round(frame);
  if (rounded < 0) return 0;

  const upper = domain.totalFrames;
  if (isUsableNumber(upper) && rounded > upper) return Math.max(0, Math.round(upper));
  return rounded;
}

/**
 * Sets the in mark.
 *
 * **The in/out policy, pinned here and by `__tests__/in-out.test.ts`:** the range must span at
 * least one frame, so setting `in` at or past the current `out` CLEARS the out mark rather than
 * swapping the two ends or clamping `in` back. Nothing is ever invented on the user's behalf — the
 * alternative (silently swapping) rewrites a mark the user did not touch, and clamping back makes
 * the control feel broken.
 *
 * A frame outside `[0, totalFrames]` is clamped into it, a repeated identical mark returns the
 * SAME state object, and a call in an unmarkable domain is a no-op.
 */
export function setIn(marks: Marks, frame: number, domain: MarkDomain): Marks {
  const next = clampFrame(frame, domain);
  if (next === null || next === marks.inFrame) return marks;

  const outFrame =
    marks.outFrame !== null && marks.outFrame <= next ? null : marks.outFrame;

  return { inFrame: next, outFrame };
}

/**
 * Sets the out mark, under the same pinned policy in the other direction: setting `out` at or
 * before the current `in` clears the in mark.
 */
export function setOut(marks: Marks, frame: number, domain: MarkDomain): Marks {
  const next = clampFrame(frame, domain);
  if (next === null || next === marks.outFrame) return marks;

  const inFrame =
    marks.inFrame !== null && marks.inFrame >= next ? null : marks.inFrame;

  return { inFrame, outFrame: next };
}

/**
 * Clears both marks.
 *
 * Always returns the shared {@link NO_MARKS} constant, so clearing an already-empty marking is the
 * same object and cannot trigger a render.
 */
export function clearMarks(): Marks {
  return NO_MARKS;
}

/**
 * The length of the marked range in frames, or `null` when the range is not complete.
 *
 * Never negative: by construction a complete marking always has `outFrame > inFrame`.
 */
export function markSpanFrames(marks: Marks): number | null {
  if (marks.inFrame === null || marks.outFrame === null) return null;
  const span = marks.outFrame - marks.inFrame;
  return span > 0 ? span : null;
}
