import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { MediaClip, QuadCorners } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { PinTool } from '../OverlayQuadHandles';
import { FrameHandlesOverlay } from '../FrameHandlesOverlay';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
// jsdom has no PointerEvent: a MouseEvent subclass carries the client coordinates.
(globalThis as unknown as { PointerEvent: unknown }).PointerEvent = class extends MouseEvent {};
afterEach(cleanup);

const PIN: QuadCorners = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };

function setup(pin: QuadCorners | null, opts: { tool?: 'pin' | 'select'; readOnly?: boolean; overlay?: boolean; speed?: number } = {}) {
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), cornerPin: pin } as unknown as MediaClip])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  store.setUi({ selection: ['c1'], viewerTool: opts.tool ?? 'pin', playheadFrame: 20, playbackSpeed: opts.speed ?? 0 });
  render(
    <MantineProvider>
      {opts.overlay ? <FrameHandlesOverlay store={store} sourceSizeFor={() => null} readOnly={opts.readOnly ?? false} /> : <PinTool store={store} readOnly={opts.readOnly ?? false} />}
    </MantineProvider>,
  );
  const clip = () => store.getState().doc.tracks[0].items[0] as MediaClip;
  return { store, clip };
}

describe('the corner pin tool', () => {
  it('shows four named corner handles for a pinned clip', () => {
    setup(PIN);
    for (const c of ['topLeft', 'topRight', 'bottomRight', 'bottomLeft']) expect(screen.getByTestId(`viewer-pin-${c}`)).toBeDefined();
  });

  it('arrow nudges 1 percent and Shift 10, each ONE undo step, clamped to the field range', () => {
    const { store, clip } = setup(PIN);
    const h = screen.getByTestId('viewer-pin-topLeft');
    fireEvent.keyDown(h, { key: 'ArrowRight' });
    expect(clip().cornerPin?.topLeft.x).toBeCloseTo(0.01, 4);
    fireEvent.keyDown(h, { key: 'ArrowDown', shiftKey: true });
    expect(clip().cornerPin?.topLeft.y).toBeCloseTo(0.1, 4);
    expect(store.getState().history).toHaveLength(2);
    for (let i = 0; i < 40; i += 1) fireEvent.keyDown(h, { key: 'ArrowLeft', shiftKey: true });
    expect(clip().cornerPin?.topLeft.x).toBe(-1);
  });

  it('a drag writes ONE setProperty on release and nothing before', () => {
    const { store, clip } = setup(PIN);
    const root = screen.getByTestId('viewer-pin');
    root.getBoundingClientRect = () => ({ left: 0, top: 0, right: 200, bottom: 100, width: 200, height: 100, x: 0, y: 0, toJSON: () => ({}) });
    const h = screen.getByTestId('viewer-pin-bottomRight');
    fireEvent.pointerDown(h, { button: 0, clientX: 200, clientY: 100 });
    fireEvent.pointerMove(h, { clientX: 150, clientY: 50 });
    expect(store.getState().history).toHaveLength(0);
    fireEvent.pointerUp(h, { clientX: 100, clientY: 50 });
    expect(clip().cornerPin?.bottomRight).toEqual({ x: 0.5, y: 0.5 });
    expect(store.getState().history).toHaveLength(1);
  });

  it('read-only: handles are disabled and nudges write nothing', () => {
    const { store } = setup(PIN, { readOnly: true });
    const h = screen.getByTestId('viewer-pin-topLeft') as HTMLButtonElement;
    expect(h.disabled).toBe(true);
    fireEvent.keyDown(h, { key: 'ArrowRight' });
    expect(store.getState().history).toHaveLength(0);
  });

  it('a clip with no pin gets the plain-words note, no handles', () => {
    setup(null);
    expect(screen.queryByTestId('viewer-pin-topLeft')).toBeNull();
    expect(screen.getByTestId('viewer-pin-note')).toBeDefined();
  });
});

describe('mount matrix', () => {
  it('paused with another tool: the auto-show draws no pin handles', () => {
    setup(PIN, { tool: 'select', overlay: true });
    expect(screen.queryByTestId('viewer-pin-topLeft')).toBeNull();
  });
  it('pin tool, playing: the pin handles stay (static value, 4 Hz quantum)', () => {
    setup(PIN, { overlay: true, speed: 1 });
    expect(screen.getByTestId('viewer-pin-topLeft')).toBeDefined();
  });
  it('no tool, playing: nothing mounts', () => {
    setup(PIN, { tool: 'select', overlay: true, speed: 1 });
    expect(screen.queryByTestId('viewer-pin')).toBeNull();
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
  });
});
