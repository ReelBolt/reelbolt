// @vitest-environment jsdom
/**
 * Touch hardening of the timeline canvas (remediation W0.7 / RM.3): the browser must not take the
 * gesture (`touch-action: none`), a cancelled pointer leaves nothing half-applied, the pointer is
 * captured on drag start, and the selected clip's trim handles have a 44 px hit area on touch.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TOUCH_SURFACE_STYLE, TimelineCanvas, type TimelineCanvasProps } from '../TimelineCanvas';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';

let matching = new Set<string>();
function installMatchMedia() {
  global.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: matching.has(query),
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => true,
    }) as MediaQueryList;
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
// jsdom has no PointerEvent: without this, React's synthetic event loses clientX and pointerId.
class PointerEventPolyfill extends MouseEvent {
  pointerId: number;
  pointerType: string;
  constructor(type: string, init: PointerEventInit = {}) {
    super(type, init);
    this.pointerId = init.pointerId ?? 0;
    this.pointerType = init.pointerType ?? 'mouse';
  }
}
(globalThis as unknown as { PointerEvent: unknown }).PointerEvent = PointerEventPolyfill;
// jsdom has no 2D context; the paint effect simply returns.
HTMLCanvasElement.prototype.getContext = (() => null) as unknown as HTMLCanvasElement['getContext'];

const captured: number[] = [];
beforeEach(() => {
  resetTestViewport();
  matching = new Set();
  installMatchMedia();
  captured.length = 0;
  Element.prototype.setPointerCapture = function setPointerCapture(this: Element, id: number) {
    captured.push(id);
  };
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.releasePointerCapture = () => {};
});
afterEach(cleanup);

// Geometry: pixelsPerFrame 2, scroll 0 → clip c2 (frames 60-120 on the first video track) spans
// x 120-240; the first track row starts at y 42 (ruler 28 + marker lane 14).
const CLIP_C2 = { x: 180, y: 60 };
const RULER = { y: 10 };

function setup(overrides: Partial<TimelineCanvasProps> = {}) {
  const props: TimelineCanvasProps = {
    doc: standardDoc(),
    playheadFrame: 20,
    selection: [],
    inFrame: null,
    outFrame: null,
    pixelsPerFrame: 2,
    scrollFrame: 0,
    tool: 'select',
    trackFlags: () => ({ collapsed: false, locked: false, muted: false, solo: false, hidden: false }),
    parityOf: () => 'exact',
    rationaleFrames: [],
    snap: (f) => f,
    onSeek: vi.fn(),
    onSelect: vi.fn(),
    onMarquee: vi.fn(),
    onBlade: vi.fn(),
    onMoveItem: vi.fn(),
    onTrimItem: vi.fn(),
    onZoom: vi.fn(),
    onScroll: vi.fn(),
    ...overrides,
  } as TimelineCanvasProps;
  render(
    <MantineProvider>
      <TimelineCanvas {...props} />
    </MantineProvider>,
  );
  return { props, canvas: screen.getByTestId(overrides.overview ? 'timeline-overview-canvas' : 'timeline-canvas') };
}

const pointer = (x: number, y: number, extra: Record<string, unknown> = {}) => ({ clientX: x, clientY: y, pointerId: 7, button: 0, pointerType: 'touch', ...extra });

describe('TimelineCanvas on touch', () => {
  it('tells the browser not to take the gesture (touch-action: none)', () => {
    // jsdom's CSSStyleDeclaration drops `touch-action`, so the shared style object is asserted and
    // the canvas, its wrapper and both handles are checked to carry it (the e2e suite reads the
    // computed style in a real browser).
    expect(TOUCH_SURFACE_STYLE).toEqual({ touchAction: 'none', overscrollBehavior: 'contain' });
    setup({ selection: ['c2'] });
    for (const id of ['timeline-canvas', 'timeline-canvas-wrapper', 'trim-handle-in', 'trim-handle-out']) {
      expect(screen.getByTestId(id).getAttribute('data-touch-action')).toBe('none');
    }
  });

  it('the read-only overview strip renders no trim handles and keeps the default cursor', () => {
    const { canvas } = setup({ selection: ['c2'], overview: true });
    expect(screen.queryByTestId('trim-handle-in')).toBeNull();
    expect(screen.queryByTestId('trim-handle-out')).toBeNull();
    expect((canvas as HTMLElement).style.cursor).toBe('var(--rb-cursor-default)');
  });

  it('captures the pointer at drag start', () => {
    const { canvas } = setup();
    fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
    expect(captured).toEqual([7]);
  });

  it('a cancelled clip drag moves nothing, and a later pointerup does not apply it', () => {
    const { canvas, props } = setup({ selection: ['c2'] });
    fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
    fireEvent.pointerMove(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
    fireEvent.pointerCancel(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
    fireEvent.pointerUp(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
    expect(props.onMoveItem).not.toHaveBeenCalled();
  });

  it('an uncancelled mouse drag still moves the clip (the desktop path is unchanged)', () => {
    const { canvas, props } = setup({ selection: ['c2'] });
    const mouse = (x: number, y: number) => pointer(x, y, { pointerType: 'mouse' });
    fireEvent.pointerDown(canvas, mouse(CLIP_C2.x, CLIP_C2.y));
    fireEvent.pointerMove(canvas, mouse(CLIP_C2.x + 60, CLIP_C2.y));
    fireEvent.pointerUp(canvas, mouse(CLIP_C2.x + 60, CLIP_C2.y));
    expect(props.onMoveItem).toHaveBeenCalledTimes(1);
    expect(props.onMoveItem).toHaveBeenCalledWith('c2', expect.any(String), 90);
  });

  it('a cancelled ruler scrub puts the playhead back where it was', () => {
    const { canvas, props } = setup({ playheadFrame: 20 });
    fireEvent.pointerDown(canvas, pointer(200, RULER.y));
    fireEvent.pointerMove(canvas, pointer(260, RULER.y));
    fireEvent.pointerCancel(canvas, pointer(260, RULER.y));
    const seeks = vi.mocked(props.onSeek).mock.calls.map((c) => c[0]);
    expect(seeks.slice(0, 2)).toEqual([100, 130]);
    expect(seeks[seeks.length - 1]).toBe(20);
  });

  it('a cancelled pan scrolls back to where it started', () => {
    const { canvas, props } = setup({ tool: 'hand', scrollFrame: 40 });
    fireEvent.pointerDown(canvas, pointer(300, 80));
    fireEvent.pointerMove(canvas, pointer(200, 80));
    fireEvent.pointerCancel(canvas, pointer(200, 80));
    const scrolls = vi.mocked(props.onScroll).mock.calls.map((c) => c[0]);
    expect(scrolls[0]).toBe(90);
    expect(scrolls[scrolls.length - 1]).toBe(40);
  });

  it('a cancelled trim leaves the clip untouched', () => {
    const { canvas, props } = setup({ selection: ['c2'] });
    fireEvent.pointerDown(screen.getByTestId('trim-handle-out'), pointer(240, CLIP_C2.y));
    fireEvent.pointerMove(canvas, pointer(200, CLIP_C2.y));
    fireEvent.pointerCancel(canvas, pointer(200, CLIP_C2.y));
    fireEvent.pointerUp(canvas, pointer(200, CLIP_C2.y));
    expect(props.onTrimItem).not.toHaveBeenCalled();
  });

  it('a cancelled marquee selects nothing', () => {
    const { canvas, props } = setup();
    fireEvent.pointerDown(canvas, pointer(600, 60));
    fireEvent.pointerMove(canvas, pointer(700, 120));
    fireEvent.pointerCancel(canvas, pointer(700, 120));
    fireEvent.pointerUp(canvas, pointer(700, 120));
    expect(props.onMarquee).not.toHaveBeenCalled();
    expect(screen.queryByTestId('timeline-marquee')).toBeNull();
  });

  it('gives the selected clip’s trim handles a 44 px hit area on a coarse pointer', async () => {
    matching.add('(pointer: coarse)');
    setup({ selection: ['c2'] });
    await act(async () => {});
    for (const id of ['trim-handle-in', 'trim-handle-out']) {
      const handle = screen.getByTestId(id);
      expect(handle.style.width).toBe('44px');
    }
    // Centred on the edge: the out edge is at x 240.
    expect(screen.getByTestId('trim-handle-out').style.left).toBe('218px');
  });

  it('on a phone an empty-area drag pans the timeline instead of drawing a marquee', async () => {
    const { canvas, props } = setup({ scrollFrame: 40 });
    await act(async () => {});
    fireEvent.pointerDown(canvas, pointer(600, 60));
    fireEvent.pointerMove(canvas, pointer(500, 60));
    fireEvent.pointerUp(canvas, pointer(500, 60));
    expect(props.onMarquee).not.toHaveBeenCalled();
    expect(props.onScroll).toHaveBeenCalledWith(90);
    // A drag is a pan, never a selection change.
    expect(props.onSelect).not.toHaveBeenCalled();
  });
});

describe('TimelineCanvas touch gestures (R03.8)', () => {
  const two = (id: number, x: number, y: number) => pointer(x, y, { pointerId: id });

  it('a tap on a clip selects it; a tap on empty space clears the selection', () => {
    const { canvas, props } = setup();
    fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
    expect(props.onSelect).not.toHaveBeenCalled();
    fireEvent.pointerUp(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
    expect(props.onSelect).toHaveBeenCalledWith('c2', { shift: false, toggle: false });
    fireEvent.pointerDown(canvas, pointer(600, 60));
    fireEvent.pointerUp(canvas, pointer(600, 60));
    expect(props.onSelect).toHaveBeenLastCalledWith(null, { shift: false, toggle: false });
  });

  it('a one-finger drag on a clip pans and does not move or select it', () => {
    const { canvas, props } = setup({ scrollFrame: 40 });
    fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
    fireEvent.pointerMove(canvas, pointer(CLIP_C2.x - 100, CLIP_C2.y));
    fireEvent.pointerUp(canvas, pointer(CLIP_C2.x - 100, CLIP_C2.y));
    expect(props.onScroll).toHaveBeenCalledWith(90);
    expect(props.onMoveItem).not.toHaveBeenCalled();
    expect(props.onSelect).not.toHaveBeenCalled();
  });

  it('pinch zooms around the midpoint without changing the selection, clamped to the zoom limits', () => {
    const { canvas, props } = setup({ selection: ['c2'] });
    fireEvent.pointerDown(canvas, two(1, 300, 80));
    fireEvent.pointerDown(canvas, two(2, 400, 80));
    fireEvent.pointerMove(canvas, two(2, 500, 80));
    // Distance 100 -> 200 at 2 px per frame: 4 px per frame, midpoint frame 175 stays under x 400.
    expect(props.onZoom).toHaveBeenLastCalledWith(4, 175);
    expect(props.onScroll).toHaveBeenLastCalledWith(75);
    fireEvent.pointerMove(canvas, two(2, 100000, 80));
    expect(vi.mocked(props.onZoom).mock.calls.at(-1)?.[0]).toBe(64);
    fireEvent.pointerUp(canvas, two(2, 100000, 80));
    fireEvent.pointerUp(canvas, two(1, 300, 80));
    expect(props.onSelect).not.toHaveBeenCalled();
  });

  it('a second finger puts a started pan back, and the pinch is undone by a cancel', () => {
    const { canvas, props } = setup({ scrollFrame: 40 });
    fireEvent.pointerDown(canvas, two(1, 300, 80));
    fireEvent.pointerMove(canvas, two(1, 200, 80));
    fireEvent.pointerDown(canvas, two(2, 400, 80));
    expect(vi.mocked(props.onScroll).mock.calls.at(-1)?.[0]).toBe(40);
    fireEvent.pointerMove(canvas, two(2, 600, 80));
    fireEvent.pointerCancel(canvas, two(2, 600, 80));
    expect(props.onZoom).toHaveBeenLastCalledWith(2, expect.any(Number));
    expect(vi.mocked(props.onScroll).mock.calls.at(-1)?.[0]).toBe(40);
  });

  it('ignores a touch that starts in the 20 px edge dead zone (the iOS back-swipe)', () => {
    const { canvas, props } = setup({ scrollFrame: 40 });
    fireEvent.pointerDown(canvas, pointer(8, 80));
    fireEvent.pointerMove(canvas, pointer(120, 80));
    fireEvent.pointerUp(canvas, pointer(120, 80));
    expect(props.onScroll).not.toHaveBeenCalled();
    expect(props.onSelect).not.toHaveBeenCalled();
    expect(captured).toEqual([]);
  });

  it('a double tap on a clip zooms to it', () => {
    const { canvas, props } = setup();
    for (let i = 0; i < 2; i++) {
      fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
      fireEvent.pointerUp(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
    }
    expect(props.onZoom).toHaveBeenCalledTimes(1);
    // jsdom lays the canvas out 1 px wide, so the 60 frame clip fits at 0.8 / 60 px per frame.
    expect(vi.mocked(props.onZoom).mock.calls[0][0]).toBeCloseTo(0.8 / 60, 5);
  });

  describe('long press', () => {
    beforeEach(() => {
      vi.useFakeTimers();
      localStorage.removeItem('reelbolt:editor:touch-drag');
    });
    afterEach(() => {
      vi.useRealTimers();
      localStorage.removeItem('reelbolt:editor:touch-drag');
    });

    it('opens the clip action menu after 500 ms when the touch-drag flag is off', () => {
      const onClipContextMenu = vi.fn();
      const { canvas, props } = setup({ onClipContextMenu });
      fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
      act(() => {
        vi.advanceTimersByTime(499);
      });
      expect(onClipContextMenu).not.toHaveBeenCalled();
      act(() => {
        vi.advanceTimersByTime(2);
      });
      expect(onClipContextMenu).toHaveBeenCalledWith({ itemId: 'c2', clientX: CLIP_C2.x, clientY: CLIP_C2.y });
      expect(props.onSelect).toHaveBeenCalledWith('c2', { shift: false, toggle: false });
      fireEvent.pointerUp(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
      expect(props.onMoveItem).not.toHaveBeenCalled();
    });

    it('moving before 500 ms cancels the long press', () => {
      const onClipContextMenu = vi.fn();
      const { canvas } = setup({ onClipContextMenu });
      fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
      fireEvent.pointerMove(canvas, pointer(CLIP_C2.x + 30, CLIP_C2.y));
      act(() => {
        vi.advanceTimersByTime(600);
      });
      expect(onClipContextMenu).not.toHaveBeenCalled();
    });

    it('picks the clip up to move it when the touch-drag flag is on, and a cancel leaves it where it was', () => {
      localStorage.setItem('reelbolt:editor:touch-drag', 'on');
      const onClipContextMenu = vi.fn();
      const { canvas, props } = setup({ onClipContextMenu });
      fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
      act(() => {
        vi.advanceTimersByTime(600);
      });
      expect(onClipContextMenu).not.toHaveBeenCalled();
      fireEvent.pointerMove(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
      fireEvent.pointerUp(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
      expect(props.onMoveItem).toHaveBeenCalledWith('c2', expect.any(String), 90);
    });

    it('a cancelled pickup moves nothing and leaves no ghost', () => {
      localStorage.setItem('reelbolt:editor:touch-drag', 'on');
      const { canvas, props } = setup();
      fireEvent.pointerDown(canvas, pointer(CLIP_C2.x, CLIP_C2.y));
      act(() => {
        vi.advanceTimersByTime(600);
      });
      fireEvent.pointerMove(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
      fireEvent.pointerCancel(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
      fireEvent.pointerUp(canvas, pointer(CLIP_C2.x + 60, CLIP_C2.y));
      expect(props.onMoveItem).not.toHaveBeenCalled();
      expect(screen.queryByTestId('timeline-drag-ghost')).toBeNull();
    });
  });

  it('releases pointer capture on up and a lost capture without an up cancels the gesture', () => {
    const released: number[] = [];
    Element.prototype.hasPointerCapture = () => true;
    Element.prototype.releasePointerCapture = (id: number) => {
      released.push(id);
    };
    const { canvas, props } = setup({ scrollFrame: 40 });
    fireEvent.pointerDown(canvas, pointer(300, 80));
    fireEvent.pointerUp(canvas, pointer(300, 80));
    expect(released).toEqual([7]);
    fireEvent.pointerDown(canvas, pointer(300, 80));
    fireEvent.pointerMove(canvas, pointer(200, 80));
    fireEvent(canvas, new PointerEvent('lostpointercapture', { pointerId: 7, pointerType: 'touch', bubbles: true }));
    expect(vi.mocked(props.onScroll).mock.calls.at(-1)?.[0]).toBe(40);
  });
});
