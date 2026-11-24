/** @vitest-environment node */
/** The adaptive picture size: skipped frames make the picture smaller (never drop an effect), and a pause is always full size. */

import { describe, expect, it } from 'vitest';
import { CLEAN_WINDOWS_TO_RECOVER, fitRenderSize, MIN_LONG_SIDE, quantizeLongSide, ResolutionGovernor, SCALE_STEPS, STRIKES, WARMUP_SEC, WINDOW_SEC } from '../resolution-governor';

const FPS = 30;
const windowSlots = Math.ceil(FPS * WINDOW_SEC);
const warmupSlots = Math.ceil(FPS * WARMUP_SEC);

function governor(fullLongSide = 960) {
  const g = new ResolutionGovernor(() => FPS, () => fullLongSide);
  g.beginPlay();
  return g;
}
const warm = (g: ResolutionGovernor) => {
  for (let i = 0; i < warmupSlots; i++) g.record(0);
};
/** One window in which every third presented slot follows two skipped ones (well past the bad threshold). */
const badWindow = (g: ResolutionGovernor): boolean => {
  let changed = false;
  for (let slots = 0; slots < windowSlots; slots += 3) changed = g.record(2) || changed;
  return changed;
};
const cleanWindow = (g: ResolutionGovernor): boolean => {
  let changed = false;
  for (let i = 0; i < windowSlots; i++) changed = g.record(0) || changed;
  return changed;
};

describe('ResolutionGovernor', () => {
  it('starts at the full size and stays there while playback keeps up', () => {
    const g = governor();
    warm(g);
    for (let i = 0; i < 40; i++) expect(cleanWindow(g)).toBe(false);
    expect(g.scale).toBe(1);
  });

  it('does not judge the first second after Play', () => {
    const g = governor();
    for (let i = 0; i < warmupSlots / 3; i++) expect(g.record(2)).toBe(false);
    expect(g.scale).toBe(1);
  });

  it('steps down one level after sustained skips, and again if they continue', () => {
    const g = governor();
    warm(g);
    for (let i = 0; i < STRIKES - 1; i++) expect(badWindow(g)).toBe(false);
    expect(badWindow(g)).toBe(true);
    expect(g.scale).toBe(SCALE_STEPS[1]);
    for (let i = 0; i < STRIKES - 1; i++) badWindow(g);
    expect(badWindow(g)).toBe(true);
    expect(g.scale).toBe(SCALE_STEPS[2]);
  });

  it('one isolated stall, however long, is not a slow device', () => {
    const g = governor();
    warm(g);
    for (let w = 0; w < 6; w++) {
      g.record(20);
      for (let i = 0; i < windowSlots; i++) g.record(0);
    }
    expect(g.scale).toBe(1);
  });

  it('a good window between bad ones resets the strikes', () => {
    const g = governor();
    warm(g);
    for (let i = 0; i < 6; i++) {
      badWindow(g);
      cleanWindow(g);
    }
    expect(g.scale).toBe(1);
  });

  it('never steps back up within a play, and keeps what it learned for the next play', () => {
    const g = governor();
    warm(g);
    for (let i = 0; i < STRIKES; i++) badWindow(g);
    expect(g.scale).toBe(SCALE_STEPS[1]);
    for (let i = 0; i < CLEAN_WINDOWS_TO_RECOVER - 1; i++) cleanWindow(g);
    expect(g.scale).toBe(SCALE_STEPS[1]);
    g.beginPlay();
    expect(g.scale).toBe(SCALE_STEPS[1]);
  });

  it('a long clean run at a reduced size lets the NEXT play try one level higher', () => {
    const g = governor();
    warm(g);
    for (let i = 0; i < STRIKES; i++) badWindow(g);
    for (let i = 0; i < CLEAN_WINDOWS_TO_RECOVER; i++) cleanWindow(g);
    expect(g.scale).toBe(SCALE_STEPS[1]);
    g.beginPlay();
    expect(g.scale).toBe(1);
  });

  it('stops at the floor: the picture never gets a long side under the minimum', () => {
    const g = governor(MIN_LONG_SIDE * 1.5);
    warm(g);
    for (let i = 0; i < 12; i++) badWindow(g);
    expect(g.scale * MIN_LONG_SIDE * 1.5).toBeGreaterThanOrEqual(MIN_LONG_SIDE);
    expect(g.atFloor).toBe(true);
  });

  it('reaches the last level on a large picture and then reports the floor', () => {
    const g = governor(1920);
    expect(g.atFloor).toBe(false);
    warm(g);
    for (let i = 0; i < 20; i++) badWindow(g);
    expect(g.scale).toBe(SCALE_STEPS[SCALE_STEPS.length - 1]);
    expect(g.atFloor).toBe(true);
  });

  it('reset forgets the learned level', () => {
    const g = governor();
    warm(g);
    for (let i = 0; i < STRIKES; i++) badWindow(g);
    g.reset();
    expect(g.scale).toBe(1);
  });
});

describe('fitRenderSize', () => {
  it('draws no larger than the viewer shows the picture, in the timeline shape, with even sides', () => {
    expect(fitRenderSize(1920, 1080, 900, 1280)).toEqual({ width: 960, height: 540 });
    expect(fitRenderSize(1080, 1920, 700, 1280)).toEqual({ width: 450, height: 800 });
  });
  it('never exceeds the device ceiling or the timeline itself', () => {
    expect(fitRenderSize(1920, 1080, 3000, 1280)).toEqual({ width: 1280, height: 720 });
    expect(fitRenderSize(640, 360, 3000, 1280)).toEqual({ width: 640, height: 360 });
  });
  it('an unmeasured viewer uses the ceiling', () => {
    expect(fitRenderSize(1920, 1080, null, 854)).toEqual({ width: 854, height: 480 });
  });
  it('a tiny viewer still gets the minimum long side', () => {
    const size = fitRenderSize(1920, 1080, 40, 1280);
    expect(Math.max(size.width, size.height)).toBeGreaterThanOrEqual(160);
  });
  it('snaps the viewer size up to a step so a resize does not reallocate on every pixel', () => {
    expect(quantizeLongSide(801)).toBe(960);
    expect(quantizeLongSide(960)).toBe(960);
    expect(quantizeLongSide(10)).toBe(160);
  });
});
