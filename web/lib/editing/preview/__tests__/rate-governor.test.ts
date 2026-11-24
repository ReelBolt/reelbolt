/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { RateGovernor, STRIKES, WARMUP_SEC, WINDOW_SLOTS } from '../rate-governor';

/** Clean slots covering the warm-up grace of a governor at `fps`. */
const warm = (g: RateGovernor, fps: number) => {
  for (let i = 0; i < Math.ceil(fps * WARMUP_SEC); i++) g.record(0);
};

/** `windows` judged windows, each with `droppedPerWindow` drops spread as separate one-slot skips (a sustained drop). */
const feed = (g: RateGovernor, windows: number, droppedPerWindow: number) => {
  let changed = false;
  for (let w = 0; w < windows; w++) {
    let slots = 0;
    for (let d = 0; d < droppedPerWindow; d++) {
      changed = g.record(1) || changed;
      slots += 2;
    }
    for (; slots < WINDOW_SLOTS; slots++) changed = g.record(0) || changed;
  }
  return changed;
};

const governor = (fps: number, heavy?: () => boolean) => {
  const g = new RateGovernor(() => fps, heavy);
  warm(g, fps);
  return g;
};

describe('RateGovernor', () => {
  it('stays at full rate when drops are rare', () => {
    const g = governor(60);
    expect(feed(g, 10, 3)).toBe(false);
    expect(g.frameStep).toBe(1);
  });
  it('halves a 60 fps timeline after STRIKES consecutive bad windows, not before', () => {
    const g = governor(60);
    feed(g, STRIKES - 1, 20);
    expect(g.frameStep).toBe(1);
    feed(g, 1, 20);
    expect(g.frameStep).toBe(2);
  });
  it('a good window resets the strike count', () => {
    const g = governor(60);
    feed(g, STRIKES - 1, 20);
    feed(g, 1, 0);
    feed(g, STRIKES - 1, 20);
    expect(g.frameStep).toBe(1);
  });
  it('ignores drops in the warm-up after Play, however bad', () => {
    const g = new RateGovernor(() => 60);
    for (let i = 0; i < 60; i++) g.record(1);
    expect(g.frameStep).toBe(1);
    feed(g, STRIKES - 1, 20);
    expect(g.frameStep).toBe(1);
  });
  it('the warm-up restarts on reset (Play, or a seek while playing)', () => {
    const g = governor(60);
    g.reset();
    for (let i = 0; i < 45; i++) g.record(1);
    expect(g.frameStep).toBe(1);
  });
  it('one isolated stall per window, however long, is not a sustained drop', () => {
    const g = governor(60);
    for (let w = 0; w < STRIKES * 3; w++) {
      g.record(30);
      for (let i = 31; i < WINDOW_SLOTS; i++) g.record(0);
    }
    expect(g.frameStep).toBe(1);
  });
  it('never halves a 30 fps timeline, and reset restores full rate', () => {
    const slow = governor(30);
    feed(slow, 6, 40);
    expect(slow.frameStep).toBe(1);
    const g = governor(60);
    feed(g, STRIKES, 20);
    expect(g.frameStep).toBe(2);
    g.reset();
    expect(g.frameStep).toBe(1);
  });
  it('halves a 30 fps timeline when a heavy source is on screen, once more, and not below 15 fps', () => {
    let heavy = true;
    const g = governor(30, () => heavy);
    feed(g, STRIKES, 20);
    expect(g.frameStep).toBe(2);
    feed(g, STRIKES, 20);
    expect(g.frameStep).toBe(2);
    const sixty = governor(60, () => heavy);
    feed(sixty, STRIKES * 2, 20);
    expect(sixty.frameStep).toBe(4);
    feed(sixty, STRIKES * 2, 20);
    expect(sixty.frameStep).toBe(4);
    heavy = false;
    const light = governor(30, () => heavy);
    feed(light, 6, 40);
    expect(light.frameStep).toBe(1);
  });
  it('a heavy source halves at 4 % dropped, a light one does not', () => {
    const heavy = governor(60, () => true);
    feed(heavy, STRIKES, 4);
    expect(heavy.frameStep).toBe(2);
    const light = governor(60);
    feed(light, 4, 4);
    expect(light.frameStep).toBe(1);
  });
});
