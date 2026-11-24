import fc from 'fast-check';
import { describe, expect, it } from 'vitest';

import { beatSecondsToRecordFrames, recordFrameToSourceSec } from '../map-to-record';
import { buildBeatGrid, gridLines, NO_CORRECTION } from '../beat-grid';
import { analysis, clip } from './fixtures';

const lines = gridLines(buildBeatGrid(analysis(), NO_CORRECTION), 'beat');

describe('beatSecondsToRecordFrames', () => {
  it('maps forward with sourceIn and keeps only frames inside the clip', () => {
    const c = clip('m', 100, 160, { sourceIn: 30 });
    const r = beatSecondsToRecordFrames(c, lines, 30);
    expect(Array.isArray(r)).toBe(true);
    const frames = (r as { frame: number }[]).map((x) => x.frame);
    expect(frames.slice(0, 4)).toEqual([100, 115, 130, 145]);
    expect(frames.every((f) => f >= 100 && f < 160)).toBe(true);
  });
  it('playbackRate 2 halves the spacing, 0.5 doubles it', () => {
    const fast = beatSecondsToRecordFrames(clip('m', 0, 60, { playbackRate: 2 }), lines, 30) as { frame: number }[];
    expect(fast.slice(0, 3).map((x) => x.frame)).toEqual([0, 8, 15]); // 7.5 rounds half up to 8
    const slow = beatSecondsToRecordFrames(clip('m', 0, 90, { playbackRate: 0.5 }), lines, 30) as { frame: number }[];
    expect(slow.slice(0, 3).map((x) => x.frame)).toEqual([0, 30, 60]);
  });
  it('reverse re-sorts ascending', () => {
    const c = clip('m', 0, 60, { sourceIn: 0, reverse: true });
    const r = beatSecondsToRecordFrames(c, lines, 30) as { frame: number }[];
    const frames = r.map((x) => x.frame);
    expect(frames).toEqual([...frames].sort((a, b) => a - b));
    expect(frames).toEqual([0, 15, 30, 45]);
  });
  it('refuses a hold', () => {
    expect(beatSecondsToRecordFrames(clip('m', 0, 60, { hold: true }), lines, 30)).toEqual({ refused: 'hold' });
  });
  it('de-duplicates frames', () => {
    const dense = [{ sec: 1, bar: false, barNumber: 1 }, { sec: 1.001, bar: true, barNumber: 2 }];
    const r = beatSecondsToRecordFrames(clip('m', 0, 90, { sourceIn: 0 }), dense, 30) as { frame: number; bar: boolean }[];
    expect(r).toHaveLength(1);
    expect(r[0].bar).toBe(true);
  });
});

describe('round trip (property)', () => {
  it('record -> source -> record is the identity, and source -> record -> source is within half a record frame', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 0, max: 5000 }),
        fc.integer({ min: 1, max: 3000 }),
        fc.integer({ min: 0, max: 9000 }),
        fc.constantFrom(0.5, 1, 2),
        fc.boolean(),
        fc.double({ min: 0, max: 1, noNaN: true }),
        (recordIn, duration, sourceIn, rate, reverse, pos) => {
          const c = clip('m', recordIn, recordIn + duration, { sourceIn, playbackRate: rate, reverse });
          const fps = 30;
          const frame = recordIn + Math.floor(pos * (duration - 1));
          const sec = recordFrameToSourceSec(c, frame, fps);
          const back = beatSecondsToRecordFrames(c, [{ sec, bar: false, barNumber: 1 }], fps);
          expect(Array.isArray(back) ? back.map((b) => b.frame) : []).toEqual([frame]);
          // sub-frame source position: stays within half a record frame
          const sec2 = sec + (pos - 0.5) * (rate / fps) * 0.9;
          const mapped = beatSecondsToRecordFrames(c, [{ sec: sec2, bar: false, barNumber: 1 }], fps);
          if (Array.isArray(mapped) && mapped.length === 1) {
            expect(Math.abs(recordFrameToSourceSec(c, mapped[0].frame, fps) - sec2)).toBeLessThanOrEqual((0.5 * rate) / fps + 1e-9);
          }
        },
      ),
      { numRuns: 300 },
    );
  });
});
