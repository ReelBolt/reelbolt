// @vitest-environment jsdom
/**
 * R10.3: the on-canvas mask / reframe / power-window handles against a real store: a drag drafts (the
 * stored document is untouched until release), release writes ONE batch (one undo step), a click without
 * movement writes nothing, the arrow keys nudge, and a tracked window is read-only.
 */

import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import React, { Profiler } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { FrameHandlesOverlay, FrameTools } from '../FrameHandlesOverlay';
import { EditorWorkspace } from '../../EditorWorkspace';
import { createEditorStore } from '@/lib/editing/store';
import type { MediaClip, ShapeMask } from '@/lib/editing/types';
import type { SaveState, SyncClient } from '@/lib/editing/sync';
import { frameDraftFor, enterViewerTool, windowTargetFor } from '@/lib/editing/viewer-transform';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { buildKeymap, eventToChord, shortcutBlocked } from '@/lib/editing/keymap';
import { runCommand, type CommandContext } from '@/lib/editing/commands';

// §7.3's Escape wiring lives in the workspace's global keydown switch; the viewer itself is stubbed so the
// test reads the store, not a render session.
vi.mock('@/lib/hooks/use-media', async (importOriginal) => ({ ...(await importOriginal<object>()), useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-media-derivatives', async (importOriginal) => ({ ...(await importOriginal<object>()), useRequestTimelineDerivatives: () => undefined }));
vi.mock('../../timeline/TimelineCanvas', () => ({ TimelineCanvas: () => null }));
vi.mock('../../viewer', async (importOriginal) => ({ ...(await importOriginal<object>()), ProgramViewer: () => null }));
Element.prototype.scrollIntoView = vi.fn();
globalThis.fetch = vi.fn(() => new Promise<Response>(() => {})) as unknown as typeof fetch;

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
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

// The viewer is 960 x 540 CSS px for a 1920 x 1080 timeline (k = 0.5).
beforeAll(() => {
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 960 });
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 540 });
  HTMLElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, right: 960, bottom: 540, width: 960, height: 540, x: 0, y: 0, toJSON: () => ({}) });
});
// These tests are about the full editor: say so, or a desktop with no stored choice waits for the saved preferences.
window.localStorage.setItem('reelbolt:editor:shell', 'full');
afterEach(() => {
  cleanup();
  setCoarse(false);
});

const mask = { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.2, h: 0.2, feather: 0.05, invert: false };
const sourceSizeFor = () => ({ width: 1920, height: 1080 });

function setup(extra: Partial<MediaClip> = {}) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0, extra)])]), revision: 1 });
  store.setUi({ selection: ['c1'], playheadFrame: 10, playbackSpeed: 0 });
  const view = render(
    <MantineProvider>
      <FrameHandlesOverlay store={store} sourceSizeFor={sourceSizeFor} readOnly={false} />
      <FrameTools store={store} readOnly={false} />
    </MantineProvider>,
  );
  return { store, view };
}
const clipOf = (store: ReturnType<typeof setup>['store']) => store.getState().doc.tracks[0].items[0] as MediaClip;
const ptr = (type: string, x: number, y: number, extra: PointerEventInit = {}) => new PointerEvent(type, { bubbles: true, clientX: x, clientY: y, pointerId: 1, ...extra });
const drag = (el: Element, from: [number, number], to: [number, number], extra: PointerEventInit = {}) => {
  act(() => {
    el.dispatchEvent(ptr('pointerdown', ...from));
    el.dispatchEvent(ptr('pointermove', ...to, extra));
  });
};
const release = (el: Element, at: [number, number], extra: PointerEventInit = {}) => act(() => void el.dispatchEvent(ptr('pointerup', ...at, extra)));

/** A coarse pointer (the phone): Mantine's useMediaQuery('(pointer: coarse)') answers true. */
function setCoarse(coarse: boolean) {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: coarse && query.includes('(pointer: coarse)'), media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
}

/** The overlay under a Profiler, so a test can count the commits a playhead tick costs. */
function setupProfiled(extra: Partial<MediaClip> = {}) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0, extra)])]), revision: 1 });
  store.setUi({ selection: ['c1'], playheadFrame: 10, playbackSpeed: 0 });
  const onRender = vi.fn();
  render(
    <MantineProvider>
      <Profiler id="handles" onRender={onRender}>
        <FrameHandlesOverlay store={store} sourceSizeFor={sourceSizeFor} readOnly={false} />
      </Profiler>
    </MantineProvider>,
  );
  return { store, onRender };
}

const saved: SaveState = { status: 'saved', revision: 1, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };
const fakeSync = () => ({ getState: () => saved, subscribe: () => () => {}, attachOps: vi.fn(), flush: vi.fn(), getQueuedEntry: () => null, discardQueue: vi.fn() }) as unknown as SyncClient;

/** The real workspace, with the viewer stubbed: the Escape keydown is the thing under test. */
function mountWorkspace() {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0, { mask })])]), revision: 1 });
  store.setUi({ workspace: 'edit', selection: ['c1'], playheadFrame: 10, playbackSpeed: 0, viewerTool: 'mask' });
  render(
    <MantineProvider>
      <EditorWorkspace store={store} sync={fakeSync()} onReload={() => {}} />
    </MantineProvider>,
  );
  return store;
}

describe('FrameHandlesOverlay', () => {
  it('draws nothing without a mask, a reframe or a window, and nothing while playing', () => {
    const { view, store } = setup();
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
    view.unmount();
    const s = setup({ mask });
    act(() => s.store.setUi({ playbackSpeed: 1 }));
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
    expect(store).toBeTruthy();
  });

  it('dragging the mask body drafts, then ONE batch on release; one undo reverts it', () => {
    const { store } = setup({ mask });
    const body = screen.getByTestId('viewer-mask-body');
    const before = clipOf(store).mask;
    const revisionOps = vi.spyOn(store, 'dispatchBatch');
    drag(body, [480, 270], [576, 324]);
    expect(frameDraftFor(store).get()).toMatchObject({ kind: 'mask', itemId: 'c1' });
    expect(clipOf(store).mask).toEqual(before); // nothing written while dragging
    release(body, [576, 324]);
    expect(revisionOps).toHaveBeenCalledTimes(1);
    expect(frameDraftFor(store).get()).toBeNull();
    // +96 px / 960 = +0.1 of the width, +54 / 540 = +0.1 of the height
    expect(clipOf(store).mask).toMatchObject({ x: 0.6, y: 0.6, w: 0.2, h: 0.2, feather: 0.05 });
    store.undo();
    expect(clipOf(store).mask).toEqual(before);
  });

  it('a click without movement writes nothing', () => {
    const { store } = setup({ mask });
    const spy = vi.spyOn(store, 'dispatchBatch');
    const body = screen.getByTestId('viewer-mask-body');
    act(() => void body.dispatchEvent(ptr('pointerdown', 480, 270)));
    release(body, [480, 270]);
    expect(spy).not.toHaveBeenCalled();
    expect(frameDraftFor(store).get()).toBeNull();
  });

  it('a handle drag that outlives its layer leaves no draft behind', () => {
    const { store } = setup({ mask });
    const h = screen.getByTestId('viewer-mask-handle-br');
    drag(h, [576, 324], [672, 380]);
    expect(frameDraftFor(store).get()?.kind).toBe('mask');
    act(() => store.setUi({ playheadFrame: 150 })); // the clip is no longer under the playhead
    expect(screen.queryByTestId('viewer-mask-layer')).toBeNull();
    expect(frameDraftFor(store).get()).toBeNull();
    expect(store.getState().history).toHaveLength(0);
  });

  it('Escape cancels a handle drag with one press through the real keymap; the release writes nothing and nothing is deselected', () => {
    const { store } = setup({ mask });
    const keymap = buildKeymap({});
    const onKey = (event: KeyboardEvent) => {
      const chord = eventToChord(event);
      if (shortcutBlocked(event.target, chord)) return;
      const action = keymap.get(chord);
      if (action) runCommand(action, { store, event } as unknown as CommandContext);
    };
    window.addEventListener('keydown', onKey);
    const h = screen.getByTestId('viewer-mask-handle-br');
    drag(h, [576, 324], [672, 380]);
    expect(frameDraftFor(store).get()?.kind).toBe('mask');
    act(() => void fireEvent.keyDown(document.body, { key: 'Escape', code: 'Escape' }));
    expect(frameDraftFor(store).get()).toBeNull();
    expect(store.getState().ui.selection).toEqual(['c1']);
    release(h, [672, 380]);
    expect(store.getState().history).toHaveLength(0);
    expect(clipOf(store).mask).toEqual(mask);
    window.removeEventListener('keydown', onKey);
  });

  it('a bare arrow during a pointer drag is ignored, so one drag writes once', () => {
    const { store } = setup({ mask });
    const h = screen.getByTestId('viewer-mask-handle-br');
    drag(h, [576, 324], [672, 380]);
    act(() => void fireEvent.keyDown(screen.getByTestId('viewer-mask-layer'), { key: 'ArrowRight' }));
    act(() => void fireEvent.keyDown(h, { key: 'ArrowRight' }));
    expect(store.getState().history).toHaveLength(0);
    release(h, [672, 380]);
    expect(store.getState().history).toHaveLength(1);
  });

  it('a corner handle resizes; Shift keeps the aspect', () => {
    const { store } = setup({ mask });
    const h = screen.getByTestId('viewer-mask-handle-br');
    // mask box on screen: 384..576 x 216..324 (w 192, h 108). Drag br to (672, 324)
    drag(h, [576, 324], [672, 380], { shiftKey: true });
    release(h, [672, 380], { shiftKey: true });
    const m = clipOf(store).mask as ShapeMask;
    expect((m.w! * 1920) / (m.h! * 1080)).toBeCloseTo((0.2 * 1920) / (0.2 * 1080), 3); // the start aspect, in pixels
    expect(m.w).toBeGreaterThan(0.2);
  });

  it('the feather handle changes only the feather', () => {
    const { store } = setup({ mask });
    const h = screen.getByTestId('viewer-mask-handle-feather');
    drag(h, [0, 0], [500, 300]);
    release(h, [500, 300]);
    const m = clipOf(store).mask as ShapeMask;
    expect(m).toMatchObject({ x: 0.5, y: 0.5, w: 0.2, h: 0.2 });
    expect(m.feather).not.toBe(0.05);
  });

  it('arrow keys nudge 1 percent, Shift 10 percent, each one undo step', () => {
    const { store } = setup({ mask });
    const layer = screen.getByTestId('viewer-mask-layer');
    fireEvent.keyDown(layer, { key: 'ArrowRight' });
    expect((clipOf(store).mask as ShapeMask).x).toBeCloseTo(0.51, 4);
    fireEvent.keyDown(layer, { key: 'ArrowDown', shiftKey: true });
    expect((clipOf(store).mask as ShapeMask).y).toBeCloseTo(0.6, 4);
    store.undo();
    expect((clipOf(store).mask as ShapeMask).y).toBeCloseTo(0.5, 4);
  });

  it('handles are focusable buttons with names; the layer has a group name', () => {
    setup({ mask });
    expect(screen.getByLabelText('Resize mask from the top left corner').getAttribute('tabindex')).toBe('0');
    expect(screen.getAllByRole('button', { name: /Resize mask/ })).toHaveLength(8);
    expect(screen.getByRole('group', { name: /Mask handles/ })).toBeTruthy();
    expect(screen.getByTestId('viewer-mask-body').style.touchAction).toBe('none');
  });

  it('the reframe point drag stores x,y and dims outside the kept window while active', () => {
    const { store } = setup({ reframe: { x: 0.5, y: 0.5 } });
    const h = screen.getByTestId('viewer-reframe-handle-point');
    drag(h, [480, 270], [240, 135]);
    expect(screen.getByTestId('viewer-reframe-dim')).toBeTruthy();
    release(h, [240, 135]);
    expect(clipOf(store).reframe).toEqual({ x: 0.25, y: 0.25 });
    expect(screen.queryByTestId('viewer-reframe-dim')).toBeNull();
  });

  it('the invert tool toggles the mask in one op', () => {
    const { store } = setup({ mask });
    fireEvent.click(screen.getByTestId('viewer-mask-invert'));
    expect((clipOf(store).mask as ShapeMask).invert).toBe(true);
    store.undo();
    expect((clipOf(store).mask as ShapeMask).invert).toBe(false);
  });

  it('a power window shows only when the Colour panel publishes it; tracked windows are read-only with the plain line', () => {
    const win = { shape: { kind: 'ellipse' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05 }, offsetX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' as const }, { frame: 50, value: 0.1, interp: 'linear' as const }] } };
    const { store } = setup({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: win }] } });
    expect(screen.queryByTestId('viewer-window-layer')).toBeNull();
    act(() => windowTargetFor(store).set({ itemId: 'c1', nodeId: 'n1' }));
    expect(screen.getByTestId('viewer-window-layer')).toBeTruthy();
    expect(screen.queryByTestId('viewer-window-handle-br')).toBeNull();
    expect(screen.getByTestId('viewer-window-tracked-note').textContent).toBe('Tracked windows follow the picture. Turn off tracking to move this by hand.');
  });

  it('an untracked window drags by hand and keeps its offsets', () => {
    const win = { shape: { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05 }, offsetX: { value: 0.1, keys: null } };
    const { store } = setup({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: win }] } });
    act(() => windowTargetFor(store).set({ itemId: 'c1', nodeId: 'n1' }));
    const body = screen.getByTestId('viewer-window-body');
    drag(body, [500, 270], [596, 270]);
    release(body, [596, 270]);
    const node = clipOf(store).grade!.nodes![0];
    expect(node.window?.offsetX).toEqual({ value: 0.1, keys: null });
    expect(node.window?.shape.x).toBeCloseTo(0.6, 3);
  });
});

const untrackedWindow = { shape: { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05 }, offsetX: { value: 0.1, keys: null } };
const trackedWindow = { shape: { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05 }, offsetX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' as const }, { frame: 50, value: 0.1, interp: 'linear' as const }] } };

describe('the mount matrix (§7.3)', () => {
  it('paused with no tool: the auto-show mounts (today\'s behaviour)', () => {
    setup({ mask });
    expect(screen.getByTestId('viewer-frame-handles')).toBeTruthy();
  });

  it('paused with a tool: the handles still mount whether or not the tool relates to them', () => {
    const { store } = setup({ mask });
    act(() => store.setUi({ viewerTool: 'mask' }));
    expect(screen.getByTestId('viewer-frame-handles')).toBeTruthy();
    act(() => store.setUi({ viewerTool: 'transform' }));
    expect(screen.getByTestId('viewer-frame-handles')).toBeTruthy();
  });

  it('playing with no tool: absent, and a playhead tick costs no commit', () => {
    const { store, onRender } = setupProfiled({ mask });
    act(() => store.setUi({ playbackSpeed: 1 }));
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
    onRender.mockClear();
    act(() => {
      for (let f = 11; f < 60; f += 1) store.setUi({ playheadFrame: f });
    });
    expect(onRender).not.toHaveBeenCalled();
  });

  it('the mask tool keeps the handles during playback at the 4 Hz leaf quantum', () => {
    const { store, onRender } = setupProfiled({ mask });
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'mask' }));
    expect(screen.getByTestId('viewer-frame-handles')).toBeTruthy();
    onRender.mockClear();
    act(() => store.setUi({ playheadFrame: 11 })); // same quantum bucket as 10 (30 fps -> 8 frames)
    expect(onRender).not.toHaveBeenCalled();
    act(() => store.setUi({ playheadFrame: 16 })); // the next bucket
    expect(onRender).toHaveBeenCalled();
  });

  it('the reframe tool persists during playback too', () => {
    const { store } = setup({ reframe: { x: 0.5, y: 0.5 } });
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'reframe' }));
    expect(screen.getByTestId('viewer-reframe-layer')).toBeTruthy();
  });

  it('an untracked window persists during playback', () => {
    const { store } = setup({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: untrackedWindow }] } });
    act(() => windowTargetFor(store).set({ itemId: 'c1', nodeId: 'n1' }));
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'window' }));
    expect(screen.getByTestId('viewer-window-layer')).toBeTruthy();
  });

  it('the transform tool stays pause-only', () => {
    const { store } = setup({ mask });
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'transform' }));
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
  });

  it('a tracked window stays pause-only during playback', () => {
    const { store } = setup({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: trackedWindow }] } });
    act(() => windowTargetFor(store).set({ itemId: 'c1', nodeId: 'n1' }));
    expect(screen.getByTestId('viewer-window-layer')).toBeTruthy(); // paused auto-show
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'window' }));
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
  });

  it('the sampled playhead leaving the clip hides the handles', () => {
    const { store } = setup({ mask });
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'mask', playheadFrame: 150 }));
    expect(screen.queryByTestId('viewer-frame-handles')).toBeNull();
  });

  it('a drag during playback pauses the transport and commits one setProperty mask', () => {
    const { store } = setup({ mask });
    const spy = vi.spyOn(store, 'dispatchBatch');
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'mask' }));
    const body = screen.getByTestId('viewer-mask-body');
    drag(body, [480, 270], [576, 324]);
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(screen.getByTestId('viewer-mask-body')).toBe(body); // the pause did not unmount the gesture
    release(body, [576, 324]);
    expect(spy).toHaveBeenCalledTimes(1);
    expect(clipOf(store).mask).toMatchObject({ x: 0.6, y: 0.6 });
  });

  it('the phone (coarse pointer) keeps the paused auto-show without a tool, with 44px handles', () => {
    setCoarse(true);
    setup({ mask });
    expect(screen.getByTestId('viewer-frame-handles')).toBeTruthy();
    expect(screen.getByTestId('viewer-mask-handle-br').style.width).toBe('44px');
  });
});

describe('the tool wiring (§7.3)', () => {
  it('entering a shape tool pauses playback', () => {
    const { store } = setup({ mask });
    act(() => store.setUi({ playbackSpeed: 1, viewerTool: 'select' }));
    act(() => enterViewerTool(store, 'mask'));
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(store.getState().ui.viewerTool).toBe('mask');
  });

  it('entering a tool focuses the picture handles', () => {
    const { store } = setup({ mask });
    act(() => enterViewerTool(store, 'reframe'));
    expect(document.activeElement).toBe(screen.getByTestId('viewer-frame-handles'));
  });

  it('Escape leaves the tool and keeps the selection; a second Escape deselects', () => {
    const store = mountWorkspace();
    fireEvent.keyDown(window, { key: 'Escape', code: 'Escape' });
    expect(store.getState().ui.viewerTool).toBe('select');
    expect(store.getState().ui.playbackSpeed).toBe(0); // the paused auto-show is still there
    expect(store.getState().ui.selection).toEqual(['c1']);
    fireEvent.keyDown(window, { key: 'Escape', code: 'Escape' });
    expect(store.getState().ui.selection).toEqual([]);
  });
});
