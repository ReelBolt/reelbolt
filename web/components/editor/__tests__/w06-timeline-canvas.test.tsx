// @vitest-environment jsdom
/**
 * W0.6 (ui1 D9, D12): the timeline's wheel/pinch handler is registered non-passive, so Ctrl+wheel
 * (and trackpad pinch, which arrives as Ctrl+wheel) zooms the timeline instead of logging "Unable to
 * preventDefault inside passive event listener"; dragging a clip shows a ghost where it will land.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TimelineCanvas, type TimelineCanvasProps } from '../timeline/TimelineCanvas';
import { trackRows } from '../timeline/paint';
import { standardDoc, TRACKS } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
// jsdom paints nothing and has no pointer capture; the canvas only needs to exist.
HTMLCanvasElement.prototype.getContext = (() => null) as unknown as HTMLCanvasElement['getContext'];
HTMLElement.prototype.setPointerCapture = () => {};
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

afterEach(() => cleanup());

const flags = () => ({ locked: false, enabled: true, mute: false, solo: false, collapsed: false });

function renderCanvas(overrides: Partial<TimelineCanvasProps> = {}) {
  const props: TimelineCanvasProps = {
    doc: standardDoc(),
    playheadFrame: 0,
    selection: [],
    inFrame: null,
    outFrame: null,
    pixelsPerFrame: 2,
    scrollFrame: 0,
    tool: 'select',
    trackFlags: flags,
    parityOf: () => 'faithful',
    rationaleFrames: [],
    snap: (frame) => frame,
    onSeek: vi.fn(),
    onSelect: vi.fn(),
    onMarquee: vi.fn(),
    onBlade: vi.fn(),
    onMoveItem: vi.fn(),
    onTrimItem: vi.fn(),
    onZoom: vi.fn(),
    onScroll: vi.fn(),
    ...overrides,
  };
  render(
    <MantineProvider>
      <TimelineCanvas {...props} />
    </MantineProvider>,
  );
  return props;
}

describe('W0.6 timeline canvas', () => {
  it('Ctrl+wheel is handled by a non-passive listener: the page default is prevented and the timeline zooms (ui1 D9)', () => {
    const props = renderCanvas();
    const canvas = screen.getByTestId('timeline-canvas');
    const event = new WheelEvent('wheel', { deltaY: -100, ctrlKey: true, bubbles: true, cancelable: true });
    canvas.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(props.onZoom).toHaveBeenCalledWith(2.5, expect.any(Number));
  });

  it('dragging a clip shows a ghost at the frame and lane it will land on, and it goes on release (ui1 D12)', () => {
    const doc = standardDoc();
    const props = renderCanvas({ doc });
    const rows = trackRows(doc, { trackFlags: flags } as unknown as Parameters<typeof trackRows>[1]);
    const v1 = rows.find((r) => r.track.id === TRACKS.v1)!;
    const v2 = rows.find((r) => r.track.id === TRACKS.v2)!;
    const canvas = screen.getByTestId('timeline-canvas');
    // c1 spans frames 0..60 on V1 = x 0..120 at 2 px/frame; grab it 10 frames in.
    fireEvent.pointerDown(canvas, { clientX: 20, clientY: v1.y + 5, pointerId: 1 });
    expect(screen.queryByTestId('timeline-drag-ghost')).toBeNull();
    fireEvent.pointerMove(canvas, { clientX: 220, clientY: v2.y + 5, pointerId: 1 });
    const ghost = screen.getByTestId('timeline-drag-ghost');
    // x 220 = frame 110; lands at 110 − 10 = 100 on V2: x 200, 120 px wide.
    expect(ghost.style.left).toBe('200px');
    expect(ghost.style.width).toBe('120px');
    expect(ghost.style.top).toBe(`${v2.y + 2}px`);
    fireEvent.pointerUp(canvas, { clientX: 220, clientY: v2.y + 5, pointerId: 1 });
    expect(props.onMoveItem).toHaveBeenCalledWith('c1', TRACKS.v2, 100);
    expect(screen.queryByTestId('timeline-drag-ghost')).toBeNull();
  });
});
