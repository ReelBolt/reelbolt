import { describe, expect, it } from 'vitest';

import { buildBeatGrid, gridLines, NO_CORRECTION, readBeatAnalysis } from '../beat-grid';
import { analysis } from './fixtures';

describe('readBeatAnalysis', () => {
  it('reads a valid payload and sorts/filters beats', () => {
    const r = readBeatAnalysis({ ...analysis(), beatTimesSec: [2, 1, Number.NaN, 'x', 3] });
    expect(r?.beatTimesSec).toEqual([1, 2, 3]);
    expect(r?.bpm).toBe(120);
  });
  it('rejects non-objects and missing tempo', () => {
    expect(readBeatAnalysis(null)).toBeNull();
    expect(readBeatAnalysis('x')).toBeNull();
    expect(readBeatAnalysis({ ...analysis(), bpm: Number.NaN })).toBeNull();
    expect(readBeatAnalysis({ ...analysis(), beatSec: 0 })).toBeNull();
  });
  it('caps beats at 20000 and defaults odd fields', () => {
    const r = readBeatAnalysis({ ...analysis(), beatTimesSec: Array.from({ length: 30000 }, (_, i) => i), beatsPerBar: 0, dropSec: 'a' });
    expect(r?.beatTimesSec).toHaveLength(20000);
    expect(r?.beatsPerBar).toBe(4);
    expect(r?.dropSec).toBeNull();
  });
});

describe('buildBeatGrid', () => {
  it('keeps the tracked beats with no correction', () => {
    const g = buildBeatGrid(analysis(), NO_CORRECTION);
    expect(g.beats).toHaveLength(33);
    expect(g.downbeatIndex).toBe(0);
    expect(g.bpm).toBe(120);
  });
  it('half keeps the downbeat parity and halves bpm', () => {
    const g = buildBeatGrid(analysis({ firstDownbeatIndex: 1 }), { ...NO_CORRECTION, tempo: 'half' });
    expect(g.beats[g.downbeatIndex]).toBe(0.5);
    expect(g.beats.slice(0, 3)).toEqual([0.5, 1.5, 2.5]);
    expect(g.bpm).toBe(60);
  });
  it('double inserts midpoints, reuses the last interval, doubles bpm', () => {
    const g = buildBeatGrid(analysis({ beatTimesSec: [0, 1, 2], firstDownbeatIndex: 1 }), { ...NO_CORRECTION, tempo: 'double' });
    expect(g.beats).toEqual([0, 0.5, 1, 1.5, 2, 2.5]);
    expect(g.downbeatIndex).toBe(2);
    expect(g.bpm).toBe(240);
  });
  it('nudge shifts every beat and clamps to 500ms', () => {
    const g = buildBeatGrid(analysis({ beatTimesSec: [1, 2] }), { ...NO_CORRECTION, nudgeMs: 900 });
    expect(g.beats).toEqual([1.5, 2.5]);
  });
  it('barShift moves the downbeat', () => {
    const g = buildBeatGrid(analysis(), { ...NO_CORRECTION, barShift: 2 });
    expect(g.downbeatIndex).toBe(2);
    const bars = gridLines(g, 'bar');
    expect(bars[0].sec).toBe(1);
  });
  it('synthesises a grid from phase when beats are empty', () => {
    const g = buildBeatGrid(analysis({ beatTimesSec: [], beatPhaseSec: 0.25, barPhaseSec: 0.75, durationSec: 3 }), NO_CORRECTION);
    expect(g.beats.slice(0, 3)).toEqual([0.25, 0.75, 1.25]);
    expect(g.downbeatIndex).toBe(1);
    expect(g.beats[g.beats.length - 1]).toBeLessThanOrEqual(3);
  });
});

describe('gridLines', () => {
  const grid = buildBeatGrid(analysis(), NO_CORRECTION);
  it('beat gives every beat, flagging bars and numbering them', () => {
    const l = gridLines(grid, 'beat');
    expect(l).toHaveLength(33);
    expect(l[0]).toEqual({ sec: 0, bar: true, barNumber: 1 });
    expect(l[1].bar).toBe(false);
    expect(l[4]).toEqual({ sec: 2, bar: true, barNumber: 2 });
  });
  it('twoBeats, bar, twoBars, fourBars align to the downbeat', () => {
    expect(gridLines(grid, 'twoBeats')[1].sec).toBe(1);
    expect(gridLines(grid, 'bar').map((x) => x.sec).slice(0, 3)).toEqual([0, 2, 4]);
    expect(gridLines(grid, 'twoBars').map((x) => x.sec).slice(0, 2)).toEqual([0, 4]);
    expect(gridLines(grid, 'fourBars').map((x) => x.sec)).toEqual([0, 8, 16]);
    expect(gridLines(grid, 'fourBars').every((x) => x.bar)).toBe(true);
  });
});
