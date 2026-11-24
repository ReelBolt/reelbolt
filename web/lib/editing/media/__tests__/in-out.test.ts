/** @vitest-environment node */
/**
 * The in/out mark reducer, and the frame domain it runs in.
 *
 * What this file exists to pin:
 *
 *   1. **The in/out policy is a decision, not an emergent behaviour.** Setting `in` at or past the
 *      current `out` CLEARS the out mark (and symmetrically). A component is not allowed to decide
 *      this for itself, so it is asserted here.
 *   2. **A no-op is a no-op by IDENTITY.** A repeated mark returns the same object, which is what
 *      lets `useState` bail out of a re-render instead of trusting a deep comparison.
 *   3. **No `NaN` ever comes out.** A non-finite frame is ignored; an unmarkable domain ignores
 *      every frame; an unknown bound clamps only the lower end.
 *   4. **The 29.97 arithmetic is exact.** At 17 s of 30000/1001 the frame count is 509, while
 *      rounding the rate to 30 first gives 510 — a one-frame drift, asserted side by side so the
 *      difference is the test rather than a comment.
 */

import { describe, expect, it } from 'vitest';

import {
  NO_MARKS,
  NO_TIMEBASE_MARK_REASON,
  NOT_ANALYSED_MARK_REASON,
  clearMarks,
  framesFromSeconds,
  isMarkable,
  markSpanFrames,
  rationalOf,
  resolveMarkDomain,
  secondsFromFrames,
  setIn,
  setOut,
  type MarkDomain,
  type Marks,
} from '../in-out';
import type { MediaProbe } from '../types';

const RATE_2997 = { num: 30000, den: 1001 };
const RATE_30 = { num: 30, den: 1 };

const probeOf = (partial: Partial<MediaProbe>): MediaProbe => ({
  durationSec: null,
  fpsNum: null,
  fpsDen: null,
  width: null,
  height: null,
  // The six appended fields: null, never 0 — a pre-fix artifact has no value for them at all.
  videoCodec: null,
  audioCodec: null,
  container: null,
  audioChannels: null,
  audioSampleRateHz: null,
  bitrateBps: null,
  ...partial,
});

const timebaseDomain = (totalFrames: number | null): MarkDomain => ({
  kind: 'timebase',
  fps: RATE_2997,
  totalFrames,
});

const UNAVAILABLE: MarkDomain = { kind: 'unavailable', reason: NOT_ANALYSED_MARK_REASON };

// ─────────────────────────────────────────────────────────────────────────────
// The rational arithmetic
// ─────────────────────────────────────────────────────────────────────────────

describe('framesFromSeconds', () => {
  it('multiplies before dividing, so 30000/1001 never becomes a 29.97 double', () => {
    // The drift the ticket warns about, in frames:
    //   exact (rational)  17 × 30000 / 1001 = 509.49…  → 509
    //   round the rate 30000/1001 to 30   17 × 30      = 510     ← one frame too many
    expect(framesFromSeconds(17, RATE_2997)).toBe(509);
    expect(Math.round(17 * 30)).toBe(510);
    expect(Math.round(17 * 30) - (framesFromSeconds(17, RATE_2997) as number)).toBe(1);

    // The same drift from a TRUNCATED decimal rather than a rounded integer: at 16 684 s the
    // exact count is 500 020 while `Math.round(sec × 29.97)` gives 500 019.
    expect(framesFromSeconds(16_684, RATE_2997)).toBe(500_020);
    expect(Math.round(16_684 * 29.97)).toBe(500_019);
  });

  it('agrees with a whole-number rate and with zero', () => {
    expect(framesFromSeconds(12.5, RATE_30)).toBe(375);
    expect(framesFromSeconds(0, RATE_2997)).toBe(0);
  });

  it('answers null — never NaN, never 0 — for anything it cannot count', () => {
    expect(framesFromSeconds(null, RATE_2997)).toBeNull();
    expect(framesFromSeconds(undefined, RATE_2997)).toBeNull();
    expect(framesFromSeconds(Number.NaN, RATE_2997)).toBeNull();
    expect(framesFromSeconds(-1, RATE_2997)).toBeNull();
    expect(framesFromSeconds(Infinity, RATE_2997)).toBeNull();
    expect(framesFromSeconds(10, null)).toBeNull();
    expect(framesFromSeconds(10, { num: 0, den: 1 })).toBeNull();
    expect(framesFromSeconds(10, { num: 30, den: 0 })).toBeNull();
    expect(framesFromSeconds(10, { num: Number.NaN, den: 1 })).toBeNull();
  });

  it('round-trips through secondsFromFrames at a fractional rate', () => {
    const seconds = secondsFromFrames(509, RATE_2997);
    expect(seconds).not.toBeNull();
    expect(framesFromSeconds(seconds, RATE_2997)).toBe(509);
  });
});

describe('rationalOf', () => {
  it('accepts only a positive, finite pair', () => {
    expect(rationalOf(probeOf({ fpsNum: 30000, fpsDen: 1001 }))).toEqual(RATE_2997);
    expect(rationalOf(probeOf({ fpsNum: 30, fpsDen: 0 }))).toBeNull();
    expect(rationalOf(probeOf({ fpsNum: -30, fpsDen: 1 }))).toBeNull();
    expect(rationalOf(probeOf({ fpsNum: null, fpsDen: 1 }))).toBeNull();
    expect(rationalOf(null)).toBeNull();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// The domain — a probe with no frame rate degrades, it does not guess
// ─────────────────────────────────────────────────────────────────────────────

describe('resolveMarkDomain', () => {
  it('uses the source timebase when the probe carries a rate, bounding it by the duration', () => {
    const domain = resolveMarkDomain(probeOf({ durationSec: 12.5, fpsNum: 30000, fpsDen: 1001 }));
    expect(domain.kind).toBe('timebase');
    expect(domain).toMatchObject({ kind: 'timebase', fps: RATE_2997, totalFrames: 375 });
    expect(isMarkable(domain)).toBe(true);
  });

  it('leaves the bound unknown — not invented — when the rate is known but the duration is not', () => {
    const domain = resolveMarkDomain(probeOf({ fpsNum: 30, fpsDen: 1 }));
    expect(domain).toMatchObject({ kind: 'timebase', totalFrames: null });
    expect(isMarkable(domain)).toBe(true);
  });

  it('falls back to DECODED frames when the duration is known but the rate is not', () => {
    const domain = resolveMarkDomain(probeOf({ durationSec: 12.5 }), 375);
    expect(domain).toEqual({ kind: 'decoded', totalFrames: 375 });

    // The decoder has not reported a count yet: the bound is unknown, not guessed from the duration.
    expect(resolveMarkDomain(probeOf({ durationSec: 12.5 }))).toEqual({
      kind: 'decoded',
      totalFrames: null,
    });
  });

  it('disables marking, with a stated reason, when neither a duration nor a rate is known', () => {
    const neverAnalysed = resolveMarkDomain(null);
    expect(neverAnalysed).toEqual({ kind: 'unavailable', reason: NOT_ANALYSED_MARK_REASON });

    const emptyProbe = resolveMarkDomain(probeOf({}));
    expect(emptyProbe).toEqual({ kind: 'unavailable', reason: NO_TIMEBASE_MARK_REASON });

    expect(isMarkable(neverAnalysed)).toBe(false);
    expect(isMarkable(emptyProbe)).toBe(false);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// The reducer
// ─────────────────────────────────────────────────────────────────────────────

describe('setIn / setOut — the PINNED in/out policy', () => {
  const domain = timebaseDomain(100);

  it('sets each mark independently', () => {
    const withIn = setIn(NO_MARKS, 10, domain);
    expect(withIn).toEqual({ inFrame: 10, outFrame: null });

    const withBoth = setOut(withIn, 40, domain);
    expect(withBoth).toEqual({ inFrame: 10, outFrame: 40 });
    expect(markSpanFrames(withBoth)).toBe(30);
  });

  it('setting IN at or past OUT clears OUT — it never swaps the ends and never clamps IN back', () => {
    const marked: Marks = { inFrame: 10, outFrame: 40 };

    expect(setIn(marked, 40, domain)).toEqual({ inFrame: 40, outFrame: null });
    expect(setIn(marked, 55, domain)).toEqual({ inFrame: 55, outFrame: null });
    // Explicitly NOT a swap.
    expect(setIn(marked, 55, domain)).not.toEqual({ inFrame: 40, outFrame: 55 });
  });

  it('setting OUT at or before IN clears IN — the same policy in the other direction', () => {
    const marked: Marks = { inFrame: 10, outFrame: 40 };

    expect(setOut(marked, 10, domain)).toEqual({ inFrame: null, outFrame: 10 });
    expect(setOut(marked, 3, domain)).toEqual({ inFrame: null, outFrame: 3 });
    expect(setOut(marked, 3, domain)).not.toEqual({ inFrame: 3, outFrame: 10 });
  });

  it('keeps a one-frame range (out === in + 1) as the shortest legal mark', () => {
    const marked = setOut(setIn(NO_MARKS, 10, domain), 11, domain);
    expect(marked).toEqual({ inFrame: 10, outFrame: 11 });
    expect(markSpanFrames(marked)).toBe(1);

    // A zero-length range is not representable: setting out ON in drops the in mark entirely.
    expect(setOut(setIn(NO_MARKS, 10, domain), 10, domain)).toEqual({
      inFrame: null,
      outFrame: 10,
    });
  });

  it('clamps a mark outside [0, totalFrames] into the bound', () => {
    expect(setIn(NO_MARKS, -25, domain)).toEqual({ inFrame: 0, outFrame: null });
    expect(setIn(NO_MARKS, 500, domain)).toEqual({ inFrame: 100, outFrame: null });
    expect(setOut(NO_MARKS, -1, domain)).toEqual({ inFrame: null, outFrame: 0 });
    expect(setOut(NO_MARKS, 1_000, domain)).toEqual({ inFrame: null, outFrame: 100 });
  });

  it('treats a repeated identical mark as a NO-OP, by identity', () => {
    const marked = setIn(NO_MARKS, 10, domain);
    expect(setIn(marked, 10, domain)).toBe(marked);
    expect(setIn(marked, 10.2, domain)).toBe(marked); // rounds to the same frame
    expect(setIn(marked, 11, domain)).not.toBe(marked);

    const withOut = setOut(marked, 40, domain);
    expect(setOut(withOut, 40, domain)).toBe(withOut);
  });

  it('clears to no marks, as the shared frozen constant', () => {
    expect(clearMarks()).toBe(NO_MARKS);
    expect(clearMarks()).toEqual({ inFrame: null, outFrame: null });
    expect(markSpanFrames(clearMarks())).toBeNull();
    expect(setIn(NO_MARKS, 10, timebaseDomain(null))).not.toBe(NO_MARKS);
  });

  it('never produces a NaN frame, whatever it is handed', () => {
    const inputs = [Number.NaN, Infinity, -Infinity, 0.5, -0.5, Number.MAX_SAFE_INTEGER];

    for (const input of inputs) {
      for (const target of [setIn, setOut]) {
        const result = target(NO_MARKS, input, domain);
        for (const frame of [result.inFrame, result.outFrame]) {
          if (frame !== null) expect(Number.isFinite(frame)).toBe(true);
        }
      }
    }

    // A non-finite frame is REFUSED (the state is untouched), not rounded to something plausible.
    expect(setIn(NO_MARKS, Number.NaN, domain)).toBe(NO_MARKS);
    expect(setOut(NO_MARKS, Number.POSITIVE_INFINITY, domain)).toBe(NO_MARKS);
  });

  it('ignores every frame in an unmarkable domain — the reducer cannot be the back door', () => {
    expect(setIn(NO_MARKS, 10, UNAVAILABLE)).toBe(NO_MARKS);
    expect(setOut(NO_MARKS, 10, UNAVAILABLE)).toBe(NO_MARKS);
  });

  it('marks with an unbounded upper end without inventing one', () => {
    const unbounded = timebaseDomain(null);
    expect(setIn(NO_MARKS, 12_345, unbounded)).toEqual({ inFrame: 12_345, outFrame: null });
    expect(setIn(NO_MARKS, -3, unbounded)).toEqual({ inFrame: 0, outFrame: null });
  });

  it('reports no span until BOTH ends are set', () => {
    expect(markSpanFrames(NO_MARKS)).toBeNull();
    expect(markSpanFrames({ inFrame: 4, outFrame: null })).toBeNull();
    expect(markSpanFrames({ inFrame: null, outFrame: 4 })).toBeNull();
    expect(markSpanFrames({ inFrame: 4, outFrame: 9 })).toBe(5);
  });
});
