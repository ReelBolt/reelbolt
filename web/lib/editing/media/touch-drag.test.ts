import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  LONG_PRESS_MS,
  MOVE_TOLERANCE_PX,
  TouchLiftGesture,
  emitPoolTouchDrag,
  subscribePoolTouchDrag,
  type PoolTouchDragDetail,
} from './touch-drag';

describe('R05.4 touch lift gesture', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const make = () => {
    const cb = { onLift: vi.fn(), onMove: vi.fn(), onDrop: vi.fn(), onCancel: vi.fn() };
    return { cb, g: new TouchLiftGesture(cb) };
  };

  it('lifts after a long press, follows the finger and drops on release', () => {
    const { cb, g } = make();
    g.down(10, 10);
    vi.advanceTimersByTime(LONG_PRESS_MS + 1);
    expect(cb.onLift).toHaveBeenCalledWith(10, 10);
    g.move(50, 60);
    expect(cb.onMove).toHaveBeenCalledWith(50, 60);
    g.up(55, 65);
    expect(cb.onDrop).toHaveBeenCalledWith(55, 65);
    expect(g.state).toBe('idle');
  });

  it('treats a swipe before the press matures as a scroll: no lift, no drop', () => {
    const { cb, g } = make();
    g.down(10, 10);
    g.move(10, 10 + MOVE_TOLERANCE_PX + 5);
    vi.advanceTimersByTime(LONG_PRESS_MS * 2);
    g.up(10, 40);
    expect(cb.onLift).not.toHaveBeenCalled();
    expect(cb.onDrop).not.toHaveBeenCalled();
  });

  it('a quick tap neither lifts nor drops', () => {
    const { cb, g } = make();
    g.down(5, 5);
    vi.advanceTimersByTime(LONG_PRESS_MS - 50);
    g.up(5, 5);
    vi.advanceTimersByTime(LONG_PRESS_MS);
    expect(cb.onLift).not.toHaveBeenCalled();
    expect(cb.onDrop).not.toHaveBeenCalled();
  });

  it('small finger jitter does not cancel the press, and a cancel after lift reports it', () => {
    const { cb, g } = make();
    g.down(0, 0);
    g.move(3, 3);
    vi.advanceTimersByTime(LONG_PRESS_MS + 1);
    expect(cb.onLift).toHaveBeenCalledTimes(1);
    g.cancel();
    expect(cb.onCancel).toHaveBeenCalledTimes(1);
    expect(cb.onDrop).not.toHaveBeenCalled();
  });

  it('delivers drag events to subscribers until they unsubscribe', () => {
    const seen: PoolTouchDragDetail[] = [];
    const off = subscribePoolTouchDrag((d) => seen.push(d));
    emitPoolTouchDrag({ phase: 'drop', mediaRef: 'file:a', kind: 'video', clientX: 1, clientY: 2 });
    off();
    emitPoolTouchDrag({ phase: 'drop', mediaRef: 'file:b', kind: 'video', clientX: 1, clientY: 2 });
    expect(seen.map((d) => d.mediaRef)).toEqual(['file:a']);
  });
});
