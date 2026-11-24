// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import React, { useRef } from 'react';
import { render, screen, cleanup, createEvent, fireEvent, act } from '@testing-library/react';
import { TouchDragChip, useTouchLiftDrag } from '../useTouchLiftDrag';
import { LONG_PRESS_MS, subscribePoolTouchDrag, type PoolTouchDragDetail } from '@/lib/editing/media/touch-drag';

function Tile({ onClick }: { onClick: () => void }) {
  const ref = useRef<HTMLButtonElement | null>(null);
  useTouchLiftDrag(ref, { mediaRef: 'file:a', kind: 'video' });
  return (
    <>
      <button ref={ref} data-testid="tile" onClick={onClick}>clip</button>
      <TouchDragChip mediaRef="file:a" label="clip.mp4" />
    </>
  );
}

const pointer = (el: Element, type: 'pointerDown' | 'pointerMove' | 'pointerUp', x: number, y: number) => {
  const event = createEvent[type](el, { pointerId: 1 });
  Object.defineProperty(event, 'pointerType', { value: 'touch' });
  Object.defineProperty(event, 'clientX', { value: x });
  Object.defineProperty(event, 'clientY', { value: y });
  fireEvent(el, event);
};

describe('R05.4 useTouchLiftDrag', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('long-press lifts (chip appears), release drops at the finger, and the tap does not select', () => {
    const onClick = vi.fn();
    const seen: PoolTouchDragDetail[] = [];
    const off = subscribePoolTouchDrag((d) => seen.push(d));
    render(<Tile onClick={onClick} />);
    const tile = screen.getByTestId('tile');

    pointer(tile, 'pointerDown', 20, 20);
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS + 1));
    expect(screen.getByTestId('pool-touch-chip')).toBeTruthy();
    pointer(tile, 'pointerMove', 200, 300);
    pointer(tile, 'pointerUp', 200, 300);
    fireEvent.click(tile);

    expect(seen.map((d) => d.phase)).toEqual(['lift', 'move', 'drop']);
    expect(seen[2]).toMatchObject({ mediaRef: 'file:a', kind: 'video', clientX: 200, clientY: 300 });
    expect(screen.queryByTestId('pool-touch-chip')).toBeNull();
    expect(onClick).not.toHaveBeenCalled();
    off();
  });

  it('a scroll swipe emits nothing and a later tap still selects', () => {
    const onClick = vi.fn();
    const seen: PoolTouchDragDetail[] = [];
    const off = subscribePoolTouchDrag((d) => seen.push(d));
    render(<Tile onClick={onClick} />);
    const tile = screen.getByTestId('tile');
    pointer(tile, 'pointerDown', 20, 20);
    pointer(tile, 'pointerMove', 20, 80);
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS * 2));
    pointer(tile, 'pointerUp', 20, 80);
    fireEvent.click(tile);
    expect(seen).toEqual([]);
    expect(onClick).toHaveBeenCalledTimes(1);
    off();
  });
});
