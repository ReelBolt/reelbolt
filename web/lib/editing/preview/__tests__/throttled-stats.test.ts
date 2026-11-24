import { describe, expect, it } from 'vitest';
import { createStatsThrottle } from '../throttled-stats';

function fakeClock() {
  let t = 0;
  const timers: Array<{ at: number; cb: () => void; id: number }> = [];
  let next = 1;
  return {
    now: () => t,
    setTimeout: (cb: () => void, ms: number) => {
      const id = next++;
      timers.push({ at: t + ms, cb, id });
      return id;
    },
    clearTimeout: (h: unknown) => {
      const i = timers.findIndex((x) => x.id === h);
      if (i >= 0) timers.splice(i, 1);
    },
    advance(ms: number) {
      t += ms;
      for (const x of [...timers].sort((a, b) => a.at - b.at)) {
        if (x.at <= t) {
          timers.splice(timers.indexOf(x), 1);
          x.cb();
        }
      }
    },
  };
}

describe('createStatsThrottle', () => {
  it('delivers the first value at once and at most one per interval after, ending on the latest', () => {
    const clock = fakeClock();
    const got: number[] = [];
    const throttle = createStatsThrottle<number>((v) => got.push(v), 250, clock);
    for (let i = 0; i < 30; i++) {
      throttle.push(i);
      clock.advance(10);
    }
    expect(got).toEqual([0, 24]);
    clock.advance(300);
    expect(got).toEqual([0, 24, 29]);
  });

  it('flush delivers the pending value immediately and cancels the timer', () => {
    const clock = fakeClock();
    const got: number[] = [];
    const throttle = createStatsThrottle<number>((v) => got.push(v), 250, clock);
    throttle.push(1);
    throttle.push(2);
    throttle.flush();
    expect(got).toEqual([1, 2]);
    clock.advance(1000);
    expect(got).toEqual([1, 2]);
  });

  it('cancel drops the pending value', () => {
    const clock = fakeClock();
    const got: number[] = [];
    const throttle = createStatsThrottle<number>((v) => got.push(v), 250, clock);
    throttle.push(1);
    throttle.push(2);
    throttle.cancel();
    clock.advance(1000);
    expect(got).toEqual([1]);
  });
});
