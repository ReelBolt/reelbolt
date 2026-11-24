import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { MediaClip, QuadCorners } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { frameDraftFor } from '@/lib/editing/viewer-transform';
import { buildKeymap, eventToChord, shortcutBlocked } from '@/lib/editing/keymap';
import { runCommand, type CommandContext } from '@/lib/editing/commands';
import { FrameHandlesOverlay } from '../FrameHandlesOverlay';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
(globalThis as unknown as { PointerEvent: unknown }).PointerEvent = class extends MouseEvent {};
afterEach(cleanup);

const PIN: QuadCorners = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };
const RECT = { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false };

function setup(clipExtra: Record<string, unknown>, tool: 'pin' | 'mask') {
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), ...clipExtra } as unknown as MediaClip])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  store.setUi({ selection: ['c1'], viewerTool: tool, playheadFrame: 20, playbackSpeed: 0 });
  render(
    <MantineProvider>
      <FrameHandlesOverlay store={store} sourceSizeFor={() => null} readOnly={false} />
    </MantineProvider>,
  );
  const draft = () => frameDraftFor(store).get();
  const box = (el: HTMLElement) => {
    el.getBoundingClientRect = () => ({ left: 0, top: 0, right: 200, bottom: 100, width: 200, height: 100, x: 0, y: 0, toJSON: () => ({}) });
  };
  return { store, draft, box };
}

/** The workspace's keydown listener, over the same keymap, guard and command catalogue. */
function installGlobalKeys(store: ReturnType<typeof createEditorStore>) {
  const keymap = buildKeymap({});
  const onKey = (event: KeyboardEvent) => {
    const chord = eventToChord(event);
    if (shortcutBlocked(event.target, chord)) return;
    const action = keymap.get(chord);
    if (action) runCommand(action, { store, event } as unknown as CommandContext);
  };
  window.addEventListener('keydown', onKey);
  return () => window.removeEventListener('keydown', onKey);
}
const escape = () => fireEvent.keyDown(document.body, { key: 'Escape', code: 'Escape' });

describe('a drag that outlives its layer', () => {
  it('clears the pin draft when the tool is left mid-drag', () => {
    const { store, draft, box } = setup({ cornerPin: PIN }, 'pin');
    box(screen.getByTestId('viewer-pin'));
    const handle = screen.getByTestId('viewer-pin-bottomRight');
    fireEvent.pointerDown(handle, { button: 0, clientX: 200, clientY: 100 });
    fireEvent.pointerMove(handle, { clientX: 100, clientY: 50 });
    expect(draft()?.kind).toBe('pin');
    act(() => store.setUi({ viewerTool: 'select' }));
    expect(draft()).toBeNull();
    expect(store.getState().history).toHaveLength(0);
  });

  it('clears the mask draft when another chord or the playhead unmounts the draw layer', () => {
    const { store, draft, box } = setup({ mask: null }, 'mask');
    const layer = screen.getByTestId('viewer-mask-draw');
    box(layer);
    fireEvent.pointerDown(layer, { button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(layer, { clientX: 120, clientY: 70 });
    expect(draft()?.kind).toBe('mask');
    act(() => store.setUi({ playheadFrame: 5 })); // the clip is no longer under the playhead
    expect(screen.queryByTestId('viewer-mask-draw')).toBeNull();
    expect(draft()).toBeNull();
    expect(store.getState().history).toHaveLength(0);
  });

  it('leaves another tool\'s draft alone when no drag was running', () => {
    const { store, draft } = setup({ cornerPin: PIN }, 'pin');
    act(() => frameDraftFor(store).set({ kind: 'pin', itemId: 'c1', pin: PIN }));
    act(() => store.setUi({ viewerTool: 'select' }));
    expect(draft()).not.toBeNull();
  });
});

describe('Escape through the global handler', () => {
  it('pin: one press cancels the drag only, the next leaves the tool, the next deselects', () => {
    const { store, draft, box } = setup({ cornerPin: PIN }, 'pin');
    const stop = installGlobalKeys(store);
    box(screen.getByTestId('viewer-pin'));
    const handle = screen.getByTestId('viewer-pin-topLeft');
    fireEvent.pointerDown(handle, { button: 0, clientX: 0, clientY: 0 });
    fireEvent.pointerMove(handle, { clientX: 60, clientY: 30 });
    expect(draft()?.kind).toBe('pin');

    escape();
    expect(draft()).toBeNull();
    expect(store.getState().ui.viewerTool).toBe('pin');
    expect(store.getState().ui.selection).toEqual(['c1']);
    expect(store.getState().history).toHaveLength(0);

    act(() => escape());
    expect(store.getState().ui.viewerTool).toBe('select');
    expect(store.getState().ui.selection).toEqual(['c1']);

    act(() => escape());
    expect(store.getState().ui.selection).toEqual([]);
    stop();
  });

  it('mask: one press cancels the draw only, the next leaves the tool, the next deselects', () => {
    const { store, draft, box } = setup({ mask: null }, 'mask');
    const stop = installGlobalKeys(store);
    const layer = screen.getByTestId('viewer-mask-draw');
    box(layer);
    fireEvent.pointerDown(layer, { button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(layer, { clientX: 120, clientY: 70 });
    expect(draft()?.kind).toBe('mask');

    escape();
    expect(draft()).toBeNull();
    expect(screen.queryByTestId('viewer-mask-draw-box')).toBeNull();
    expect(store.getState().ui.viewerTool).toBe('mask');
    expect(store.getState().ui.selection).toEqual(['c1']);

    act(() => escape());
    expect(store.getState().ui.viewerTool).toBe('select');
    expect(store.getState().ui.selection).toEqual(['c1']);

    act(() => escape());
    expect(store.getState().ui.selection).toEqual([]);
    stop();
  });

  it('with no drag running, Escape reaches the global handler at once', () => {
    const { store } = setup({ mask: RECT }, 'mask');
    const stop = installGlobalKeys(store);
    act(() => escape());
    expect(store.getState().ui.viewerTool).toBe('select');
    stop();
  });
});
