import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { IntentGate, needsTouchIntent, TOUCH_INTENT_MS } from './touch-intent';

describe('touch intent (T7)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('only one-finger tools on a touch screen wait', () => {
    expect(needsTouchIntent('text', 'touch')).toBe(true);
    expect(needsTouchIntent('brush', 'touch')).toBe(true);
    expect(needsTouchIntent('subject', 'touch')).toBe(true);
    expect(needsTouchIntent('select', 'touch')).toBe(false);
    expect(needsTouchIntent('text', 'mouse')).toBe(false);
  });

  it('fires after the wait when the finger stays down alone', () => {
    const run = vi.fn();
    const gate = new IntentGate();
    gate.begin(1, run);
    vi.advanceTimersByTime(TOUCH_INTENT_MS - 1);
    expect(run).not.toHaveBeenCalled();
    vi.advanceTimersByTime(2);
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('a second finger (pinch) cancels it: nothing is painted or placed', () => {
    const run = vi.fn();
    const gate = new IntentGate();
    gate.begin(1, run);
    vi.advanceTimersByTime(30);
    gate.cancel();
    vi.advanceTimersByTime(500);
    expect(run).not.toHaveBeenCalled();
    expect(gate.isPending).toBe(false);
  });

  it('a quick tap fires on release, once', () => {
    const run = vi.fn();
    const gate = new IntentGate();
    gate.begin(7, run);
    expect(gate.release(8)).toBe(false);
    expect(gate.release(7)).toBe(true);
    vi.advanceTimersByTime(500);
    expect(run).toHaveBeenCalledTimes(1);
  });
});
