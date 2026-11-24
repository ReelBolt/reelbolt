/** @vitest-environment node */
/** R04.5: every device starts on the live preview, sized for what it can carry; sustained drops are detected. */

import { describe, expect, it } from 'vitest';
import { AUTO_MAX_LONG_SIDE, AUTO_MAX_LONG_SIDE_LOW, AUTO_MAX_LONG_SIDE_PHONE, AUTO_MAX_LONG_SIDE_SOFTWARE, budgetOf, createDropWatch, planPlayback, type DeviceFacts } from '../device-budget';

const desktop: DeviceFacts = { deviceClass: 'desktop', cores: 16, memoryGb: 8, softwareGraphics: false };

describe('budgetOf / planPlayback', () => {
  it('a capable desktop is not low-budget; Auto is live, fitted to the viewer under the normal ceiling', () => {
    expect(budgetOf(desktop).low).toBe(false);
    expect(planPlayback('auto', desktop)).toEqual({ resolution: 'half', simple: false, fitMaxLongSide: AUTO_MAX_LONG_SIDE, autoReason: null });
  });
  it('a phone starts on the LIVE preview (every effect on screen), sized for a phone, with no sentence about being a phone', () => {
    const plan = planPlayback('auto', { ...desktop, deviceClass: 'phone' });
    expect(plan).toEqual({ resolution: 'quarter', simple: false, fitMaxLongSide: AUTO_MAX_LONG_SIDE_PHONE, autoReason: null });
  });
  it('a weak phone says the other reasons, never "this is a phone"', () => {
    const plan = planPlayback('auto', { ...desktop, deviceClass: 'phone', cores: 4 });
    expect(plan).toMatchObject({ simple: false, fitMaxLongSide: AUTO_MAX_LONG_SIDE_PHONE });
    expect(plan.autoReason).toBe('The preview is drawn at a smaller size because it has 4 processor cores.');
    expect(plan.autoWhy).toEqual({ mode: 'reduced', codes: [{ code: 'cores', n: 4 }] });
  });
  it.each([
    ['software graphics', { ...desktop, softwareGraphics: true }, /software graphics/, AUTO_MAX_LONG_SIDE_SOFTWARE],
    ['4 cores', { ...desktop, cores: 4 }, /4 processor cores/, AUTO_MAX_LONG_SIDE_LOW],
    ['4 GB of memory', { ...desktop, memoryGb: 4 }, /4 GB/, AUTO_MAX_LONG_SIDE_LOW],
    ['a tablet with 4 cores', { ...desktop, deviceClass: 'tablet' as const, cores: 4 }, /4 processor cores/, AUTO_MAX_LONG_SIDE_LOW],
  ])('%s on a desktop or tablet starts LIVE at a smaller size (never the simple preview), and says why', (_name, facts, why, cap) => {
    expect(budgetOf(facts).low).toBe(true);
    const plan = planPlayback('auto', facts);
    expect(plan).toMatchObject({ resolution: 'quarter', simple: false, fitMaxLongSide: cap });
    expect(plan.autoReason).toMatch(why);
    expect(plan.autoReason).toMatch(/^The preview is drawn at a smaller size because /);
  });
  it('Auto never starts on the simple preview, on any device', () => {
    for (const deviceClass of ['phone', 'tablet', 'desktop'] as const) {
      for (const softwareGraphics of [false, true]) {
        expect(planPlayback('auto', { deviceClass, cores: 2, memoryGb: 2, softwareGraphics }).simple).toBe(false);
      }
    }
  });
  it('unknown cores and memory do not make a device low-budget', () => {
    expect(budgetOf({ ...desktop, cores: null, memoryGb: null }).low).toBe(false);
    expect(budgetOf({ ...desktop, cores: 5, memoryGb: 6 }).low).toBe(false);
  });
  it('an explicit pick is an exact fraction of the timeline on any device', () => {
    expect(planPlayback('full', { ...desktop, deviceClass: 'phone' })).toEqual({ resolution: 'full', simple: false, fitMaxLongSide: null, autoReason: null });
    expect(planPlayback('quarter', desktop)).toMatchObject({ resolution: 'quarter', fitMaxLongSide: null });
  });
});

describe('createDropWatch', () => {
  const at = (t: number, deadlines: number, dropped: number) => ({ at: t, deadlines, dropped });
  it('stays quiet while few frames have been seen or the window is short', () => {
    const w = createDropWatch();
    expect(w.push(at(0, 0, 0))).toBe(false);
    expect(w.push(at(500, 15, 15))).toBe(false);
  });
  it('is sustained when more than a quarter of the last seconds dropped', () => {
    const w = createDropWatch();
    w.push(at(0, 0, 0));
    expect(w.push(at(2500, 75, 40))).toBe(true);
  });
  it('is not sustained at a healthy rate, and forgets old drops', () => {
    const w = createDropWatch();
    w.push(at(0, 0, 0));
    expect(w.push(at(2500, 75, 5))).toBe(false);
    // a bad start that then recovers: the window slides past it
    const v = createDropWatch();
    v.push(at(0, 0, 0));
    v.push(at(1000, 30, 30));
    for (let t = 2000; t <= 8000; t += 1000) v.push(at(t, 30 + (t - 1000) * 0.03, 30));
    expect(v.push(at(9000, 30 + 8000 * 0.03, 30))).toBe(false);
  });
  it('a counter that goes backwards (a new play) starts over', () => {
    const w = createDropWatch();
    w.push(at(0, 0, 0));
    w.push(at(2500, 75, 40));
    expect(w.push(at(2600, 3, 0))).toBe(false);
  });
});
