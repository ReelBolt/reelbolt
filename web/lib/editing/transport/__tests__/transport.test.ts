/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import { dropFrameToFrames, isDropFrameRate, parseTimecodeEntry } from '../timecode-entry';
import { advancePlayhead, shuttle } from '../shuttle';
import { editPoints, markerFrames, nextEdit, nextMarker, previousEdit, previousMarker, stepFrame, timelineEndFrame } from '../navigation';
import { effectivePlayRange, rangeEnded, rangeStep } from '../play-range';
import { readPlayhead, rememberReported } from '../reported-frames';
import { framesToTimecode } from '../../time';
import { standardDoc } from '../../__tests__/fixtures';

const R30 = { num: 30, den: 1 };
const R2997 = { num: 30000, den: 1001 };

describe('parseTimecodeEntry', () => {
  const ctx = { rate: R30, currentFrame: 300 };

  it('parses absolute HH:MM:SS:FF and shorter right-aligned forms', () => {
    expect(parseTimecodeEntry('00:00:10:05', ctx)).toBe(305);
    expect(parseTimecodeEntry('01:00:12:05', ctx)).toBe(((60 + 0) * 60 + 12) * 30 + 5);
    expect(parseTimecodeEntry('10:05', ctx)).toBe(305);
    expect(parseTimecodeEntry('1:10:05', ctx)).toBe(60 * 30 + 305);
    expect(parseTimecodeEntry('07', ctx)).toBe(7);
  });

  it('parses relative frames and seconds', () => {
    expect(parseTimecodeEntry('+15', ctx)).toBe(315);
    expect(parseTimecodeEntry('-15', ctx)).toBe(285);
    expect(parseTimecodeEntry('+15f', ctx)).toBe(315);
    expect(parseTimecodeEntry('-2s', ctx)).toBe(240);
    expect(parseTimecodeEntry('+0.5s', ctx)).toBe(315);
    expect(parseTimecodeEntry('+1:00', ctx)).toBe(330); // right-aligned SS:FF — one second
    expect(parseTimecodeEntry('+1:00:00', ctx)).toBe(300 + 60 * 30); // one minute
  });

  it('clamps to [0, maxFrame] and never jumps to zero on a refusal', () => {
    expect(parseTimecodeEntry('-20s', ctx)).toBe(0);
    expect(parseTimecodeEntry('+999999', { ...ctx, maxFrame: 1000 })).toBe(1000);
    expect(parseTimecodeEntry('banana', ctx)).toBeNull();
    expect(parseTimecodeEntry('00:00:10:35', ctx)).toBeNull(); // frame field >= base
    expect(parseTimecodeEntry('00:61:00:00', ctx)).toBeNull();
    expect(parseTimecodeEntry('', ctx)).toBeNull();
  });

  it('round-trips the display format for any frame count', () => {
    fc.assert(
      fc.property(fc.integer({ min: 0, max: 10_000_000 }), (frames) => {
        expect(parseTimecodeEntry(framesToTimecode(frames, R30), { rate: R30, currentFrame: 0 })).toBe(frames);
      }),
    );
  });

  it('reads drop-frame notation at 29.97 as the frame count it names', () => {
    expect(isDropFrameRate(R2997)).toBe(true);
    expect(isDropFrameRate(R30)).toBe(false);
    // 00:01:00;02 is the first frame after the minute drop: 1800 frames (NDF would say 1802).
    expect(dropFrameToFrames(0, 1, 0, 2, 30)).toBe(1800);
    expect(parseTimecodeEntry('00:01:00;02', { rate: R2997, currentFrame: 0 })).toBe(1800);
    // Every tenth minute is not dropped.
    expect(dropFrameToFrames(0, 10, 0, 0, 30)).toBe(10 * 60 * 30 - 2 * 9);
    // At an integer rate the `;` is read as `:`.
    expect(parseTimecodeEntry('00:01:00;02', ctx)).toBe(1802);
  });
});

describe('shuttle', () => {
  it('steps the ladder forward, reverse, and pauses', () => {
    expect(shuttle(0, 'L')).toBe(1);
    expect(shuttle(1, 'L')).toBe(2);
    expect(shuttle(2, 'L')).toBe(4);
    expect(shuttle(4, 'L')).toBe(8);
    expect(shuttle(8, 'L')).toBe(8);
    expect(shuttle(0, 'J')).toBe(-1);
    expect(shuttle(-1, 'J')).toBe(-2);
    expect(shuttle(-8, 'J')).toBe(-8);
    expect(shuttle(4, 'K')).toBe(0);
    expect(shuttle(4, 'J')).toBe(-1);
    expect(shuttle(-4, 'L')).toBe(1);
    expect(shuttle(0, 'Space')).toBe(1);
    expect(shuttle(2, 'Space')).toBe(0);
  });

  it('advancePlayhead accumulates fractional frames and reports the end', () => {
    let state = { fractional: 0, frame: 0, hitEnd: false };
    for (let i = 0; i < 60; i++) state = advancePlayhead(state.fractional, 0.5, 1 / 60, 30, 1000);
    expect(state.frame).toBe(15);
    expect(advancePlayhead(999.5, 1, 1, 30, 1000)).toEqual({ fractional: 1000, frame: 1000, hitEnd: true });
    expect(advancePlayhead(0.2, -1, 1, 30, 1000)).toEqual({ fractional: 0, frame: 0, hitEnd: true });
  });
});

describe('navigation', () => {
  const d = standardDoc();

  it('collects edit points and marker frames, ascending and unique', () => {
    expect(editPoints(d)).toEqual([0, 30, 60, 90, 120, 150, 240]);
    expect(markerFrames(d)).toEqual([45, 150]);
    expect(timelineEndFrame(d)).toBe(240);
  });

  it('navigates to the next/previous edit and marker strictly past the playhead', () => {
    expect(nextEdit(d, 60)).toBe(90);
    expect(previousEdit(d, 60)).toBe(30);
    expect(nextEdit(d, 240)).toBeNull();
    expect(previousEdit(d, 0)).toBeNull();
    expect(nextMarker(d, 45)).toBe(150);
    expect(previousMarker(d, 150)).toBe(45);
    expect(nextEdit(d, 0, ['track-v2'])).toBe(30);
  });

  it('stepFrame clamps to the timeline', () => {
    expect(stepFrame(0, -1, 100)).toBe(0);
    expect(stepFrame(100, 1, 100)).toBe(100);
    expect(stepFrame(50, 10, 100)).toBe(60);
  });
});

describe('PA-22: bare digits fill the timecode fields from the right', () => {
  const ctx = { rate: R30, currentFrame: 300 };
  it('reads 1215 as 00:00:12:15, as documented', () => {
    expect(parseTimecodeEntry('1215', ctx)).toBe(12 * 30 + 15);
    expect(parseTimecodeEntry('10005', ctx)).toBe((60 + 0) * 30 + 5); // 1:00:05
    expect(parseTimecodeEntry('1000000', ctx)).toBe(3600 * 30); // 1:00:00:00
    expect(parseTimecodeEntry('07', ctx)).toBe(7);
    expect(parseTimecodeEntry('5', ctx)).toBe(5);
  });
  it('carries an overfull field into the next one instead of refusing it', () => {
    expect(parseTimecodeEntry('95', ctx)).toBe(95); // 3 s 05 f
    expect(parseTimecodeEntry('190', ctx)).toBe(1 * 30 + 90); // 00:00:01:90 → 4 s
  });
  it('round-trips a displayed timecode typed without separators', () => {
    fc.assert(
      fc.property(fc.integer({ min: 0, max: 10 * 3600 * 30 }), (frames) => {
        const digits = framesToTimecode(frames, R30).replace(/\D/g, '');
        expect(parseTimecodeEntry(digits, { rate: R30, currentFrame: 0 })).toBe(frames);
      }),
    );
  });
});

describe('PA-7: play range (Loop, Play in to out)', () => {
  it('loops the whole timeline when Loop is on with no marks', () => {
    expect(effectivePlayRange({ inFrame: null, outFrame: null }, { loop: true, inToOut: false, endFrame: 300 })).toEqual({ inFrame: 0, outFrame: 300, loop: true });
  });
  it('loops between the marks, in either order', () => {
    expect(effectivePlayRange({ inFrame: 60, outFrame: 120 }, { loop: true, inToOut: false, endFrame: 300 })).toEqual({ inFrame: 60, outFrame: 120, loop: true });
    expect(effectivePlayRange({ inFrame: 120, outFrame: 60 }, { loop: true, inToOut: false, endFrame: 300 })).toEqual({ inFrame: 60, outFrame: 120, loop: true });
    expect(effectivePlayRange({ inFrame: 60, outFrame: null }, { loop: true, inToOut: false, endFrame: 300 })).toEqual({ inFrame: 60, outFrame: 300, loop: true });
  });
  it('Play in to out stops at Out; plain play ignores the marks', () => {
    expect(effectivePlayRange({ inFrame: 60, outFrame: 120 }, { loop: false, inToOut: true, endFrame: 300 })).toEqual({ inFrame: 60, outFrame: 120, loop: false });
    expect(effectivePlayRange({ inFrame: 60, outFrame: 120 }, { loop: false, inToOut: false, endFrame: 300 })).toBeNull();
  });
  it('steps: continue inside, wrap to In at Out when looping, stop on the last frame otherwise', () => {
    const loop = { inFrame: 60, outFrame: 120, loop: true };
    expect(rangeStep(90, 1, loop)).toEqual({ kind: 'continue' });
    expect(rangeStep(120, 1, loop)).toEqual({ kind: 'loop', frame: 60 });
    expect(rangeStep(119, 1, { ...loop, loop: false })).toEqual({ kind: 'continue' });
    expect(rangeStep(121, 1, { ...loop, loop: false })).toEqual({ kind: 'stop', frame: 119 });
    // Reverse shuttle wraps the other way.
    expect(rangeStep(59, -1, loop)).toEqual({ kind: 'loop', frame: 119 });
    expect(rangeStep(59, -2, { ...loop, loop: false })).toEqual({ kind: 'stop', frame: 60 });
    expect(rangeStep(500, 1, null)).toEqual({ kind: 'continue' });
  });
  it('a looped play reaching the end of the timeline restarts at In', () => {
    expect(rangeEnded({ inFrame: 0, outFrame: 300, loop: true }, 1)).toEqual({ kind: 'loop', frame: 0 });
    expect(rangeEnded({ inFrame: 60, outFrame: 120, loop: false }, 1)).toEqual({ kind: 'stop', frame: 119 });
    expect(rangeEnded(null, 1)).toEqual({ kind: 'stop', frame: null });
  });
});

describe('reported frames vs user seeks while playing (PA-8)', () => {
  it('an echo of a reported frame is ignored, a seek back to a frame already echoed is not', () => {
    let recent: number[] = [];
    for (const f of [10, 11, 12]) recent = rememberReported(recent, f);
    let read = readPlayhead(recent, 11);
    expect(read.echo).toBe(true);
    recent = read.rest;
    read = readPlayhead(recent, 12);
    expect(read.echo).toBe(true);
    recent = rememberReported(read.rest, 13);
    // The user seeks back to 11, a frame among the last 8 reported: it is a seek.
    read = readPlayhead(recent, 11);
    expect(read.echo).toBe(false);
    expect(read.rest).toEqual([]);
  });

  it('remembers a bounded window of reports and skips echoes the store coalesced', () => {
    let recent: number[] = [];
    for (let f = 0; f < 20; f++) recent = rememberReported(recent, f);
    expect(recent).toEqual([12, 13, 14, 15, 16, 17, 18, 19]);
    const read = readPlayhead(recent, 15);
    expect(read).toEqual({ echo: true, rest: [16, 17, 18, 19] });
    expect(readPlayhead(read.rest, 14).echo).toBe(false);
  });
});
