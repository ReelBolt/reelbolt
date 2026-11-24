// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import { ClipStrip, type ClipStripProps } from '../ClipStrip';
import { STRIP_HEADER_PX } from '../strip-geometry';

// jsdom has no PointerEvent; a MouseEvent carries the coordinates the strip reads.
if (typeof window !== 'undefined' && !('PointerEvent' in window)) {
  class PointerEventShim extends MouseEvent {
    pointerId: number;
    constructor(type: string, init: MouseEventInit & { pointerId?: number } = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
    }
  }
  Object.defineProperty(window, 'PointerEvent', { value: PointerEventShim, configurable: true });
}

const WIDTH = 400;
const PPF = 2;
const d = doc([
  track('v1', 'video', [media('c1', 0, 120, 0, { name: 'Intro.mp4' }), media('c2', 150, 270, 0, { name: 'Beach.mp4' })]),
  track('a1', 'audio', []),
]);

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ x: 0, y: 0, left: 0, top: 0, right: WIDTH, bottom: 300, width: WIDTH, height: 300, toJSON: () => ({}) });
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
  window.localStorage.clear();
});
afterEach(() => {
  vi.restoreAllMocks();
  cleanup();
});

function setup(over: Partial<ClipStripProps> = {}) {
  const calls = { seek: vi.fn(), select: vi.fn(), zoom: vi.fn(), trim: vi.fn() };
  const props: ClipStripProps = {
    doc: d,
    playheadFrame: 100,
    selection: [],
    inFrame: null,
    outFrame: null,
    pixelsPerFrame: PPF,
    trackFlags: () => ({ locked: false, enabled: true, mute: false, solo: false, collapsed: false }),
    parityOf: () => 'exact' as never,
    rationaleFrames: [],
    onSeek: calls.seek,
    onSelect: calls.select,
    onZoom: calls.zoom,
    onTrimItem: calls.trim,
    ...over,
  };
  render(
    <MantineProvider>
      <ClipStrip {...props} />
    </MantineProvider>,
  );
  return { calls, surface: screen.getByTestId('timeline-canvas-wrapper') };
}

const LANE_Y = STRIP_HEADER_PX + 20;
// The playhead (frame 100) is at x = 200; frame f is at 200 + (f - 100) * 2.
const xOf = (frame: number) => 200 + (frame - 100) * PPF;
const down = (el: Element, id: number, x: number, y: number) => fireEvent.pointerDown(el, { pointerId: id, clientX: x, clientY: y });
const move = (el: Element, id: number, x: number, y: number) => fireEvent.pointerMove(el, { pointerId: id, clientX: x, clientY: y });
const up = (el: Element, id: number, x: number, y: number) => fireEvent.pointerUp(el, { pointerId: id, clientX: x, clientY: y });

describe('ClipStrip gestures', () => {
  it('a tap on a clip selects it and does not seek', () => {
    const { calls, surface } = setup();
    down(surface, 1, xOf(50), LANE_Y);
    up(surface, 1, xOf(50), LANE_Y);
    expect(calls.select).toHaveBeenCalledWith('c1', { shift: false, toggle: false });
    expect(calls.seek).not.toHaveBeenCalled();
  });

  it('a tap on empty strip deselects', () => {
    const { calls, surface } = setup();
    down(surface, 1, xOf(130), LANE_Y);
    up(surface, 1, xOf(130), LANE_Y);
    expect(calls.select).toHaveBeenCalledWith(null, { shift: false, toggle: false });
  });

  it('a one-finger drag pans (seeks against the finger) and never selects', () => {
    const { calls, surface } = setup();
    down(surface, 1, 300, LANE_Y);
    move(surface, 1, 260, LANE_Y);
    up(surface, 1, 260, LANE_Y);
    // 40 px left at 2 px per frame moves the playhead 20 frames later.
    expect(calls.seek).toHaveBeenLastCalledWith(120);
    expect(calls.select).not.toHaveBeenCalled();
  });

  it('pinching out zooms in and leaves the selection alone', () => {
    const { calls, surface } = setup({ selection: ['c1'] });
    down(surface, 1, 180, LANE_Y);
    down(surface, 2, 220, LANE_Y);
    move(surface, 1, 140, LANE_Y);
    move(surface, 2, 260, LANE_Y);
    up(surface, 1, 140, LANE_Y);
    up(surface, 2, 260, LANE_Y);
    const zoomed = calls.zoom.mock.calls.map((c) => c[0] as number);
    expect(Math.max(...zoomed)).toBeGreaterThan(PPF);
    expect(calls.select).not.toHaveBeenCalled();
  });

  it('two fingers moving together pan without changing the selection', () => {
    const { calls, surface } = setup({ selection: ['c1'] });
    down(surface, 1, 180, LANE_Y);
    down(surface, 2, 220, LANE_Y);
    move(surface, 1, 140, LANE_Y);
    move(surface, 2, 180, LANE_Y);
    up(surface, 1, 140, LANE_Y);
    up(surface, 2, 180, LANE_Y);
    expect(calls.seek).toHaveBeenLastCalledWith(120);
    expect(calls.select).not.toHaveBeenCalled();
  });

  it('a double tap on a clip zooms to it', () => {
    const { calls, surface } = setup();
    for (let i = 0; i < 2; i++) {
      down(surface, 1, xOf(50), LANE_Y);
      up(surface, 1, xOf(50), LANE_Y);
    }
    expect(calls.zoom).toHaveBeenCalled();
    expect(calls.seek).toHaveBeenLastCalledWith(60);
  });
});

describe('ClipStrip accessibility', () => {
  it('is a labelled region with the selected clip described in words', () => {
    setup({ selection: ['c2'] });
    expect(screen.getByRole('region', { name: 'Clip strip' })).toBeTruthy();
    expect(screen.getByTestId('strip-description').textContent).toBe('Clip 2 of 2, Beach.mp4, 0:05 to 0:09');
    expect(screen.getByTestId('status-timecode').textContent).toBe('00:00:03:10');
  });

  it('previous and next buttons move the selection and the playhead without any drag', () => {
    const { calls } = setup({ selection: ['c1'] });
    expect((screen.getByTestId('strip-prev') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('strip-next'));
    expect(calls.select).toHaveBeenCalledWith('c2', { shift: false, toggle: false });
    expect(calls.seek).toHaveBeenCalledWith(150);
  });

  it('shows trim handles for the selected clip only, each with a 44 px finger area', () => {
    setup({ selection: ['c1'] });
    expect(screen.getAllByTestId(/^trim-handle-/)).toHaveLength(2);
    expect(parseFloat((screen.getByTestId('trim-handle-out') as HTMLElement).style.width)).toBeGreaterThanOrEqual(44);
  });

  it('dragging the end handle issues one trim for the dragged distance', () => {
    const { calls } = setup({ selection: ['c1'] });
    const handle = screen.getByTestId('trim-handle-out');
    fireEvent.pointerDown(handle, { pointerId: 1, clientX: 300 });
    fireEvent.pointerMove(handle, { pointerId: 1, clientX: 260 });
    fireEvent.pointerUp(handle, { pointerId: 1, clientX: 260 });
    expect(calls.trim).toHaveBeenCalledWith('c1', 'out', -20);
    expect(calls.seek).not.toHaveBeenCalled();
  });
});
