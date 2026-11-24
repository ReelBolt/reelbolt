// @vitest-environment jsdom
/**
 * T-006: the round colour wheel. A drag is a local draft (live puck + readout) and ONE onChange on
 * release; arrows nudge (Shift = 5x); Home/Delete and the reset button clear the colour but keep the
 * master; read-only blocks every write; the readout is built from describeWheel in English.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { nudgeWheel, wheelFromPuck, wheelPuck } from '@/lib/editing/color/wheel';
import type { GradeWheel } from '@/lib/editing/types';
import { ColorWheel, type ColorWheelProps } from '../ColorWheel';

// jsdom has no PointerEvent: a MouseEvent carrying pointerId/pointerType stands in, so clientX survives.
if (typeof window.PointerEvent === 'undefined') {
  class PointerEventStub extends MouseEvent {
    pointerId: number;
    pointerType: string;
    constructor(type: string, init: PointerEventInit = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
      this.pointerType = init.pointerType ?? 'mouse';
    }
  }
  (window as unknown as { PointerEvent: unknown }).PointerEvent = PointerEventStub;
}

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

afterEach(cleanup);
afterEach(() => vi.unstubAllGlobals());

/** Queues animation frames so a test flushes one "frame" at a time (T-008 preview coalescing). */
function frameQueue() {
  const queued: FrameRequestCallback[] = [];
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
    queued.push(cb);
    return queued.length;
  });
  vi.stubGlobal('cancelAnimationFrame', () => undefined);
  return () => {
    for (const cb of queued.splice(0)) cb(0);
  };
}

const RECT = { left: 0, top: 0, width: 100, height: 100, right: 100, bottom: 100, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;

function renderWheel(overrides: Partial<ColorWheelProps> = {}) {
  const onChange = overrides.onChange ?? vi.fn();
  const props: ColorWheelProps = { wheelKey: 'lift', label: 'Lift (shadows)', value: {}, readOnly: false, onChange, ...overrides };
  const view = renderWithI18n(<ColorWheel {...props} />, { locale: 'en' });
  const disc = screen.getByTestId('grade-wheel-lift-disc');
  Object.defineProperty(disc, 'getBoundingClientRect', { value: () => RECT, configurable: true });
  return { ...view, onChange, disc };
}

describe('ColorWheel', () => {
  it('writes the dragged position once on release, live during the drag', () => {
    const onChange = vi.fn();
    const { disc } = renderWheel({ onChange });
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    expect(onChange).not.toHaveBeenCalled();
    const puck = screen.getByTestId('grade-wheel-lift-puck');
    expect(Number(puck.getAttribute('cx'))).toBeGreaterThan(50);
    fireEvent.pointerUp(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0][0]).toEqual(wheelFromPuck(0.9, 0, {}));
  });

  it('nudges with the arrow keys and moves 5x further with Shift', () => {
    const onChange = vi.fn();
    const start = wheelFromPuck(0.5, 0, {});
    const { disc } = renderWheel({ onChange, value: start });
    fireEvent.keyDown(disc, { key: 'ArrowRight' });
    expect(onChange.mock.calls[0][0]).toEqual(nudgeWheel(start, 1, 0, 0.02));
    fireEvent.keyDown(disc, { key: 'ArrowRight', shiftKey: true });
    expect(onChange.mock.calls[1][0]).toEqual(nudgeWheel(start, 1, 0, 0.1, { snapAngle: true }));
    const normal = wheelPuck(onChange.mock.calls[0][0] as GradeWheel).x;
    const big = wheelPuck(onChange.mock.calls[1][0] as GradeWheel).x;
    expect(big - wheelPuck(start).x).toBeGreaterThan((normal - wheelPuck(start).x) * 3);
  });

  it('resets the colour with Home / Delete, the reset button and a double-click, keeping the master', () => {
    const value = { r: 0.3, g: -0.1, b: -0.2, master: 0.4 };
    const onChange = vi.fn();
    const { disc } = renderWheel({ onChange, value });
    fireEvent.keyDown(disc, { key: 'Home' });
    expect(onChange).toHaveBeenLastCalledWith({ r: 0, g: 0, b: 0, master: 0.4 });
    fireEvent.keyDown(disc, { key: 'Delete' });
    fireEvent.click(screen.getByTestId('grade-wheel-lift-reset'));
    fireEvent.doubleClick(disc);
    expect(onChange).toHaveBeenCalledTimes(4);
    for (const call of onChange.mock.calls) expect(call[0]).toEqual({ r: 0, g: 0, b: 0, master: 0.4 });
  });

  it('blocks every write when read-only', () => {
    const onChange = vi.fn();
    const { disc } = renderWheel({ onChange, readOnly: true, value: { r: 0.3, g: 0, b: 0, master: 0 } });
    expect(disc.getAttribute('aria-disabled')).toBe('true');
    expect(disc.getAttribute('tabindex')).toBe('-1');
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    fireEvent.pointerUp(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    fireEvent.keyDown(disc, { key: 'ArrowRight' });
    fireEvent.doubleClick(disc);
    fireEvent.click(screen.getByTestId('grade-wheel-lift-reset'));
    expect(onChange).not.toHaveBeenCalled();
    expect((screen.getByTestId('grade-wheel-lift-reset') as HTMLButtonElement).disabled).toBe(true);
  });

  it('reads out the hue and strength in English, with the brightness shift when the residual is non-zero', () => {
    renderWheel({ value: { r: 0.5, g: -0.25, b: -0.25, master: 0 } });
    expect(screen.getByTestId('grade-wheel-lift-readout').textContent).toBe('Red, 50%');
    cleanup();
    renderWheel({ value: { r: 0.1, g: 0.1, b: 0.1, master: 0 } });
    const text = screen.getByTestId('grade-wheel-lift-readout').textContent ?? '';
    expect(text).toContain('Neutral, 0%');
    expect(text).toContain('and a brightness shift of +0.10');
  });

  it('places the puck at the wheel maths position', () => {
    const value = { r: 0.5, g: -0.25, b: -0.25, master: 0 };
    renderWheel({ value });
    const puck = wheelPuck(value);
    expect(Number(screen.getByTestId('grade-wheel-lift-puck').getAttribute('cx'))).toBeCloseTo(50 + puck.x * 50, 1);
    expect(Number(screen.getByTestId('grade-wheel-lift-puck').getAttribute('cy'))).toBeCloseTo(50 - puck.y * 50, 1);
  });

  it('exposes the readout as the slider value text', () => {
    renderWheel({ value: { r: 0.5, g: -0.25, b: -0.25, master: 0 } });
    const disc = screen.getByTestId('grade-wheel-lift-disc');
    expect(disc.getAttribute('role')).toBe('slider');
    expect(disc.getAttribute('aria-valuetext')).toBe('Red, 50%');
    expect(disc.getAttribute('aria-valuenow')).toBe('50');
  });

  it('does not commit a drag when the wheel becomes read-only mid-drag', () => {
    const onChange = vi.fn();
    const { disc, rerender } = renderWheel({ onChange });
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    rerender(<ColorWheel wheelKey="lift" label="Lift (shadows)" value={{}} readOnly onChange={onChange} />);
    fireEvent.pointerUp(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    expect(onChange).not.toHaveBeenCalled();
  });

  it('previews the would-be wheel at most once per frame, then clears it after the release commit', () => {
    const flushFrame = frameQueue();
    const onChange = vi.fn();
    const onPreview = vi.fn();
    const { disc } = renderWheel({ onChange, onPreview });
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 70, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    expect(onPreview).not.toHaveBeenCalled();
    flushFrame();
    expect(onPreview).toHaveBeenCalledTimes(1);
    expect(onPreview.mock.calls[0][0]).toEqual(wheelFromPuck(0.9, 0, {}));
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 60, clientY: 50 });
    flushFrame();
    expect(onPreview).toHaveBeenCalledTimes(2);
    fireEvent.pointerUp(disc, { pointerId: 1, clientX: 60, clientY: 50 });
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onPreview).toHaveBeenLastCalledWith(null);
    expect(onChange.mock.invocationCallOrder[0]).toBeLessThan(onPreview.mock.invocationCallOrder.at(-1)!);
  });

  it('clears the preview on cancel and on unmount without writing', () => {
    const flushFrame = frameQueue();
    const onChange = vi.fn();
    const onPreview = vi.fn();
    const { disc, unmount } = renderWheel({ onChange, onPreview });
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 90, clientY: 50 });
    flushFrame();
    expect(onPreview).toHaveBeenCalledTimes(1);
    fireEvent.pointerCancel(disc, { pointerId: 1 });
    expect(onChange).not.toHaveBeenCalled();
    expect(onPreview).toHaveBeenLastCalledWith(null);
    unmount();
    expect(onPreview).toHaveBeenLastCalledWith(null);
  });
});
