// @vitest-environment jsdom
/**
 * T-006 integration: the grade editor shows four round wheels, the exact R/G/B/master fields stay
 * reachable (open by default, so the colour e2e can type straight into them), typing a number moves
 * the puck, and one drag is one whole-stack write with the wheel's undo label.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { GradeStack } from '@/lib/editing/types';
import { wheelFromPuck, wheelPuck } from '@/lib/editing/color/wheel';
import { GradeStackEditor } from '../GradeStackEditor';

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

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
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

const stack = (): GradeStack => ({ enabled: true, nodes: [{ id: 'n1', name: 'Correction 1', primaries: {} }] });

function renderEditor(value: GradeStack = stack()) {
  const writes: Array<[GradeStack | null, string]> = [];
  const view = render(
    <MantineProvider>
      <GradeStackEditor projectId="p1" stack={value} readOnly={false} onChange={(s, l) => writes.push([s, l])} newId={() => 'n2'} lutFiles={[]} subject="Clip" />
    </MantineProvider>,
  );
  return { ...view, writes };
}

describe('GradeStackEditor round wheels', () => {
  it('shows one round wheel per key with the old container test id', () => {
    renderEditor();
    for (const key of ['lift', 'gamma', 'gain', 'offset']) {
      expect(screen.getByTestId(`grade-wheel-${key}`)).toBeTruthy();
      expect(screen.getByTestId(`grade-wheel-${key}-disc`)).toBeTruthy();
    }
  });

  it('types into the exact-value fields and writes the wheel with its undo label', () => {
    const { writes } = renderEditor();
    fireEvent.change(screen.getByTestId('grade-wheel-gain-r'), { target: { value: '0.5' } });
    expect(writes).toHaveLength(1);
    expect(writes[0][0]?.nodes?.[0].primaries?.gain?.r).toBe(0.5);
    expect(writes[0][1]).toBe('Clip: Gain (highlights)');
  });

  it('typing a number moves the puck', () => {
    const { writes, rerender } = renderEditor();
    const before = Number(screen.getByTestId('grade-wheel-gain-puck').getAttribute('cx'));
    fireEvent.change(screen.getByTestId('grade-wheel-gain-r'), { target: { value: '0.5' } });
    const next = writes[0][0] as GradeStack;
    rerender(
      <MantineProvider>
        <GradeStackEditor projectId="p1" stack={next} readOnly={false} onChange={() => undefined} newId={() => 'n2'} lutFiles={[]} subject="Clip" />
      </MantineProvider>,
    );
    const after = Number(screen.getByTestId('grade-wheel-gain-puck').getAttribute('cx'));
    expect(Math.abs(after - before)).toBeGreaterThan(1);
    expect(after).toBeCloseTo(50 + wheelPuck({ r: 0.5 }).x * 50, 1);
  });

  it('one drag writes the whole stack once with the wheel label', () => {
    const { writes } = renderEditor();
    const disc = screen.getByTestId('grade-wheel-gain-disc');
    Object.defineProperty(disc, 'getBoundingClientRect', { value: () => RECT, configurable: true });
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    fireEvent.pointerUp(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    expect(writes).toHaveLength(1);
    expect(writes[0][0]?.nodes?.[0].primaries?.gain).toEqual(wheelFromPuck(0.9, 0, {}));
    expect(writes[0][1]).toBe('Clip: Gain (highlights)');
  });

  it('clears a scalar slider preview when the panel unmounts mid-drag', async () => {
    const previews: Array<GradeStack | null> = [];
    const spy = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, width: 100, height: 10, right: 100, bottom: 10, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    try {
      const view = render(
        <MantineProvider>
          <GradeStackEditor projectId="p1" stack={stack()} readOnly={false} onChange={() => undefined} onPreview={(s) => previews.push(s)} newId={() => 'n2'} lutFiles={[]} subject="Clip" />
        </MantineProvider>,
      );
      const thumb = within(screen.getByTestId('grade-scalar-exposure')).getByRole('slider');
      const track = thumb.parentElement!.parentElement!;
      fireEvent.mouseDown(track, { clientX: 40, clientY: 5 });
      fireEvent.mouseMove(document, { clientX: 70, clientY: 5 });
      await waitFor(() => expect(previews.some((p) => p !== null)).toBe(true));
      // No release: the panel goes away with the drag still live.
      view.unmount();
      expect(previews.at(-1)).toBeNull();
    } finally {
      spy.mockRestore();
    }
  });

  it('previews the would-be stack during a drag and clears it on release, with one write on release', () => {
    const flushFrame = frameQueue();
    const writes: Array<[GradeStack | null, string]> = [];
    const previews: Array<GradeStack | null> = [];
    render(
      <MantineProvider>
        <GradeStackEditor projectId="p1" stack={stack()} readOnly={false} onChange={(s, l) => writes.push([s, l])} onPreview={(s) => previews.push(s)} newId={() => 'n2'} lutFiles={[]} subject="Clip" />
      </MantineProvider>,
    );
    const disc = screen.getByTestId('grade-wheel-gain-disc');
    Object.defineProperty(disc, 'getBoundingClientRect', { value: () => RECT, configurable: true });
    fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 70, clientY: 50 });
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
    expect(previews).toHaveLength(0);
    flushFrame();
    expect(previews).toHaveLength(1);
    expect(previews[0]?.nodes?.[0].primaries?.gain).toEqual(wheelFromPuck(0.9, 0, {}));
    fireEvent.pointerMove(disc, { pointerId: 1, clientX: 60, clientY: 50 });
    flushFrame();
    expect(previews).toHaveLength(2);
    fireEvent.pointerUp(disc, { pointerId: 1, clientX: 60, clientY: 50 });
    expect(writes).toHaveLength(1);
    expect(previews.at(-1)).toBeNull();
  });
});
