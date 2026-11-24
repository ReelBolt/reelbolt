/** @vitest-environment node */
/**
 * `createSubclip` — the marked range as a typed descriptor.
 *
 * What this file exists to pin:
 *
 *   1. **Frames come from the probe's RATIONAL rate.** At 29.97 (30000/1001) a naive
 *      `Math.round(sec × 30)` drifts by a frame; the descriptor must carry the exact count.
 *   2. **The token is passed through untouched.** A `step_result:` source is not rejected for its
 *      prefix and its mediaRef is never destructured.
 *   3. **Nothing is invented.** A source with no probed frame rate, a range shorter than one frame,
 *      a non-integer frame or a frame outside the source gets `null` — not a clamped guess.
 *   4. **The unit contract is explicit:** `durationFrames` is the SUBCLIP's length and
 *      `sourceDurationFrames` is the source's, and the two are asserted against each other.
 */

import { describe, expect, it } from 'vitest';

import { createSubclip, subclipName, type SubclipSource } from '../subclip';
import { AUDIO_FILE_ITEM, STEP_RESULT_ITEM, VIDEO_FILE_ITEM, preFixProbe } from '../fixtures';

/** The 29.97 fixture: 17 s of 30000/1001 is exactly 509 frames. */
const NTSC_17S: SubclipSource = {
  mediaRef: 'file:00000000-0000-4000-8000-000000000501',
  probe: preFixProbe({
    durationSec: 17,
    fpsNum: 30000,
    fpsDen: 1001,
    width: 1920,
    height: 1080,
  }),
};

describe('createSubclip — the descriptor', () => {
  it('describes a range of a probed source in source frames', () => {
    const descriptor = createSubclip(VIDEO_FILE_ITEM, 30, 120, VIDEO_FILE_ITEM.fileName);

    expect(descriptor).not.toBeNull();
    expect(descriptor).toEqual({
      mediaRef: VIDEO_FILE_ITEM.mediaRef,
      sourceInFrames: 30,
      sourceOutFrames: 120,
      durationFrames: 90,
      name: 'b-roll-city [30-120]',
      // 12.5 s of 30000/1001 is exactly 375 frames.
      sourceDurationFrames: 375,
    });
  });

  it('keeps the subclip length and the source length as distinct, non-negative facts', () => {
    const descriptor = createSubclip(VIDEO_FILE_ITEM, 0, 375, VIDEO_FILE_ITEM.fileName);

    expect(descriptor?.durationFrames).toBe(375);
    expect(descriptor?.sourceDurationFrames).toBe(375);
    expect(descriptor?.sourceOutFrames).toBe(descriptor?.sourceDurationFrames);
  });

  it('COMPUTES 29.97 exactly where a naive Math.round(sec × 30) drifts by a frame', () => {
    const descriptor = createSubclip(NTSC_17S, 0, 100, 'ntsc-take-1.mov');

    expect(descriptor).not.toBeNull();
    // The exact answer this module must produce…
    expect(descriptor?.sourceDurationFrames).toBe(509);
    // …and the wrong one, computed right here, so the difference is the assertion.
    expect(Math.round(17 * 30)).toBe(510);
    expect(descriptor?.sourceDurationFrames).not.toBe(Math.round(17 * 30));
  });

  it('accepts the full source range and refuses one frame past it', () => {
    // 509 frames means indices 0..508, with 509 the exclusive end — so [0, 509) is the whole source
    // and the shortest range at the tail is [508, 509).
    expect(createSubclip(NTSC_17S, 0, 509, 'x.mp4')).not.toBeNull();
    expect(createSubclip(NTSC_17S, 0, 510, 'x.mp4')).toBeNull();
    expect(createSubclip(NTSC_17S, 509, 510, 'x.mp4')).toBeNull();
    expect(createSubclip(NTSC_17S, 508, 509, 'x.mp4')?.durationFrames).toBe(1);
    expect(createSubclip(NTSC_17S, -1, 10, 'x.mp4')).toBeNull();
  });

  it('passes an opaque mediaRef through untouched — both prefixes alike', () => {
    const stepResult = createSubclip(
      { mediaRef: STEP_RESULT_ITEM.mediaRef, probe: NTSC_17S.probe },
      0,
      100,
      'render-pass-1.mp4',
    );

    // Refused for having no probe, not for its prefix:
    expect(createSubclip(STEP_RESULT_ITEM, 0, 100, 'render-pass-1.mp4')).toBeNull();
    // …and with a probe it is described as happily as a `file:` source is.
    expect(stepResult?.mediaRef).toBe(STEP_RESULT_ITEM.mediaRef);
    expect(stepResult?.mediaRef.startsWith('step_result:')).toBe(true);
  });

  it('returns null rather than inventing a frame when the probe has no usable rate', () => {
    expect(createSubclip(AUDIO_FILE_ITEM, 0, 100, AUDIO_FILE_ITEM.fileName)).toBeNull();
    expect(
      createSubclip({ mediaRef: 'file:x', probe: { ...NTSC_17S.probe!, fpsNum: 0 } }, 0, 10, 'a.mp4'),
    ).toBeNull();
    expect(
      createSubclip({ mediaRef: 'file:x', probe: { ...NTSC_17S.probe!, fpsDen: 0 } }, 0, 10, 'a.mp4'),
    ).toBeNull();
  });

  it('returns null for a source with nothing to point at, and for a sub-one-frame range', () => {
    expect(createSubclip(null, 0, 10, 'a.mp4')).toBeNull();
    expect(createSubclip(undefined, 0, 10, 'a.mp4')).toBeNull();
    expect(createSubclip({ mediaRef: '', probe: NTSC_17S.probe }, 0, 10, 'a.mp4')).toBeNull();

    expect(createSubclip(NTSC_17S, 40, 40, 'a.mp4')).toBeNull();
    expect(createSubclip(NTSC_17S, 40, 39, 'a.mp4')).toBeNull();
    // The minimum range is exactly one frame.
    expect(createSubclip(NTSC_17S, 40, 41, 'a.mp4')?.durationFrames).toBe(1);
  });

  it('refuses a non-integer or non-finite frame instead of rounding it', () => {
    expect(createSubclip(NTSC_17S, 10.5, 40, 'a.mp4')).toBeNull();
    expect(createSubclip(NTSC_17S, 10, 40.2, 'a.mp4')).toBeNull();
    expect(createSubclip(NTSC_17S, Number.NaN, 40, 'a.mp4')).toBeNull();
    expect(createSubclip(NTSC_17S, 10, Number.POSITIVE_INFINITY, 'a.mp4')).toBeNull();
  });

  it('states an unknown source duration as null when the probe has no duration', () => {
    const noDuration: SubclipSource = {
      mediaRef: 'file:00000000-0000-4000-8000-000000000502',
      probe: preFixProbe({ durationSec: null, fpsNum: 30, fpsDen: 1, width: null, height: null }),
    };

    const descriptor = createSubclip(noDuration, 10, 40, 'a.mp4');
    expect(descriptor?.sourceDurationFrames).toBeNull();
    expect(descriptor?.durationFrames).toBe(30);
  });

  it('is pure — the same inputs give an equal descriptor and never mutate the source', () => {
    const before = JSON.stringify(VIDEO_FILE_ITEM);
    const first = createSubclip(VIDEO_FILE_ITEM, 10, 20, 'b-roll-city.mp4');
    const second = createSubclip(VIDEO_FILE_ITEM, 10, 20, 'b-roll-city.mp4');

    expect(first).toEqual(second);
    expect(JSON.stringify(VIDEO_FILE_ITEM)).toBe(before);
  });
});

describe('subclipName', () => {
  it('strips one extension and appends the frame range', () => {
    expect(subclipName('b-roll-city.mp4', 30, 120)).toBe('b-roll-city [30-120]');
    expect(subclipName('take.01.mov', 0, 1)).toBe('take.01 [0-1]');
  });

  it('handles a name with no extension, a leading-dot name and an empty name', () => {
    expect(subclipName('rushes', 5, 9)).toBe('rushes [5-9]');
    expect(subclipName('.hidden', 5, 9)).toBe('.hidden [5-9]');
    expect(subclipName('', 5, 9)).toBe('Subclip [5-9]');
    expect(subclipName(null, 5, 9)).toBe('Subclip [5-9]');
    expect(subclipName('   ', 5, 9)).toBe('Subclip [5-9]');
  });

  it('never returns an empty or blank name', () => {
    for (const name of ['', ' ', '.', '..', null, undefined, ' .mp4 '] as const) {
      const result = subclipName(name, 0, 1);
      expect(result.trim().length).toBeGreaterThan(0);
      expect(result).not.toMatch(/NaN|undefined|null/);
    }
  });
});
