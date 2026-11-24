// @vitest-environment jsdom
/** T-013: click-to-select on the picture, the Transform tool toggle, drag-while-playing, title pad snapping and the once-per-session hint. */

import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import React from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TransformOverlay, TransformToolToggle } from '../TransformOverlay';
import { resetTransformHint } from '@/lib/editing/viewer-transform';
import { createEditorStore } from '@/lib/editing/store';
import { caption, doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { KEY_ACTIONS, buildKeymap } from '@/lib/editing/keymap';
import { TOUCH_PARITY } from '@/lib/editing/keymap-touch';
import { runCommand, type CommandContext } from '@/lib/editing/commands';
import type { Item, Track } from '@/lib/editing/types';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
global.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;
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
beforeAll(() => {
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 960 });
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 540 });
  HTMLElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, right: 960, bottom: 540, width: 960, height: 540, x: 0, y: 0, toJSON: () => ({}) });
});
beforeEach(() => resetTransformHint());
afterEach(() => cleanup());

const ptr = (type: string, x: number, y: number, extra: PointerEventInit = {}) => new PointerEvent(type, { bubbles: true, clientX: x, clientY: y, pointerId: 1, button: 0, ...extra });

function setup(tracks: Track[], ui: Parameters<ReturnType<typeof createEditorStore>['setUi']>[0] = {}) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc(tracks), revision: 1 });
  store.setUi({ selection: [], playheadFrame: 10, playbackSpeed: 0, ...ui });
  const view = render(
    <MantineProvider>
      <TransformOverlay store={store} sourceSizeFor={() => null} readOnly={false} />
      <TransformToolToggle store={store} />
    </MantineProvider>,
  );
  return { store, view };
}

const items = (store: ReturnType<typeof createEditorStore>): Item[] => store.getState().doc.tracks.flatMap((t) => t.items);
const click = (x: number, y: number, extra: PointerEventInit = {}) => act(() => void screen.getByTestId('viewer-pick-layer').dispatchEvent(ptr('pointerdown', x, y, extra)));

describe('click to select', () => {
  const two = () => [track(TRACKS.v1, 'video', [media('low', 0, 100, 0)]), track(TRACKS.v2, 'video', [media('high', 0, 100, 0)])];

  it('selects the topmost item under the pointer, and Alt-click cycles to the one below', () => {
    const { store } = setup(two());
    click(480, 270);
    expect(store.getState().ui.selection).toEqual(['high']);
    click(480, 270, { altKey: true });
    expect(store.getState().ui.selection).toEqual(['low']);
    click(480, 270, { altKey: true });
    expect(store.getState().ui.selection).toEqual(['high']);
  });

  it('clicking empty canvas clears the selection', () => {
    const small = media('small', 0, 100, 0, { transform: { scale: { value: 0.2, keys: null } } });
    const { store } = setup([track(TRACKS.v1, 'video', [small])], { selection: ['small'] });
    click(20, 20);
    expect(store.getState().ui.selection).toEqual([]);
  });

  it('does nothing while another viewer tool is armed', () => {
    setup(two(), { viewerTool: 'mask' });
    expect(screen.queryByTestId('viewer-pick-layer')).toBeNull();
  });
});

describe('Transform tool', () => {
  it('is on by default, the dock button turns the handles off and on, and Shift+T does the same', () => {
    const { store } = setup([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0)])], { selection: ['c1'] });
    const button = screen.getByTestId('viewer-transform-tool');
    expect(button.getAttribute('aria-pressed')).toBe('true');
    expect(screen.queryByTestId('viewer-xform-body')).not.toBeNull();
    fireEvent.click(button);
    expect(store.getState().ui.transformTool).toBe(false);
    expect(screen.queryByTestId('viewer-xform-body')).toBeNull();
    expect(screen.getByTestId('viewer-pick-layer')).toBeTruthy();
    const keymap = buildKeymap({});
    expect(keymap.get('Shift+T')).toBe('toolTransform');
    act(() => runCommand('toolTransform', { store, event: new KeyboardEvent('keydown') } as unknown as CommandContext));
    expect(store.getState().ui.transformTool).toBe(true);
  });

  it('has a keymap entry and a touch parity entry', () => {
    expect(KEY_ACTIONS.some((a) => a.id === 'toolTransform')).toBe(true);
    expect(TOUCH_PARITY.toolTransform.kind).toBe('keyboard-only');
  });

  it('while playing the outline shows without handles, and a drag pauses first then commits ONE batch', () => {
    const { store } = setup([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0)])], { selection: ['c1'], playbackSpeed: 1 });
    expect(screen.queryByTestId('viewer-xform-handle-tl')).toBeNull();
    const body = screen.getByTestId('viewer-xform-body');
    act(() => void body.dispatchEvent(ptr('pointerdown', 480, 270)));
    expect(store.getState().ui.playbackSpeed).toBe(0);
    act(() => void body.dispatchEvent(ptr('pointermove', 560, 300)));
    act(() => void body.dispatchEvent(ptr('pointerup', 560, 300)));
    expect(store.getState().history).toHaveLength(1);
  });
});

describe('titles', () => {
  it('a drag moves the title freely and writes one transform batch (no pad position)', () => {
    const { store } = setup([track(TRACKS.v1, 'video', [title('t1', 0, 100, 'Hello')])], { selection: ['t1'] });
    const body = screen.getByTestId('viewer-xform-body');
    act(() => void body.dispatchEvent(ptr('pointerdown', 480, 270)));
    act(() => void body.dispatchEvent(ptr('pointermove', 560, 300)));
    act(() => void body.dispatchEvent(ptr('pointerup', 560, 300)));
    expect(store.getState().history).toHaveLength(1);
    const t1 = items(store).find((i) => i.id === 't1') as { transform?: { positionX?: { value: number }; positionY?: { value: number } }; position?: string };
    expect(t1.transform?.positionX?.value).toBeGreaterThan(0);
    expect(t1.transform?.positionY?.value).toBeGreaterThan(0);
    expect(t1.position).toBeUndefined();
  });

  it('a corner drag scales the title through its transform as one step', () => {
    const { store } = setup([track(TRACKS.v1, 'video', [title('t1', 0, 100, 'Hello')])], { selection: ['t1'] });
    const handle = screen.getByTestId('viewer-xform-handle-br');
    act(() => void handle.dispatchEvent(ptr('pointerdown', 520, 285)));
    act(() => void handle.dispatchEvent(ptr('pointermove', 900, 500)));
    act(() => void handle.dispatchEvent(ptr('pointerup', 900, 500)));
    expect(store.getState().history).toHaveLength(1);
    const t1 = items(store).find((i) => i.id === 't1') as { transform?: { scale?: { value: number } } };
    expect(t1.transform?.scale?.value).toBeGreaterThan(1);
  });
});

describe('captions', () => {
  it('a drag snaps to the nearest pad cell with a ghost and writes one position op', () => {
    const { store } = setup([track(TRACKS.v1, 'video', [caption('c1', 0, 100, 'Hello')])], { selection: ['c1'] });
    const body = screen.getByTestId('viewer-text-body');
    act(() => void body.dispatchEvent(ptr('pointerdown', 480, 500)));
    act(() => void body.dispatchEvent(ptr('pointermove', 900, 40)));
    expect(screen.getByTestId('viewer-text-ghost')).toBeTruthy();
    act(() => void body.dispatchEvent(ptr('pointerup', 900, 40)));
    expect(store.getState().history).toHaveLength(1);
    expect(items(store).find((i) => i.id === 'c1')).toMatchObject({ position: 'topRight' });
  });
});

describe('hint', () => {
  it('shows once per session: dismissed or not, a second mount does not show it again', () => {
    const tracks = [track(TRACKS.v1, 'video', [media('c1', 0, 100, 0)])];
    const first = setup(tracks, { selection: ['c1'] });
    expect(screen.getByTestId('viewer-xform-hint')).toBeTruthy();
    fireEvent.click(screen.getByTestId('viewer-xform-hint-dismiss'));
    expect(screen.queryByTestId('viewer-xform-hint')).toBeNull();
    first.view.unmount();
    cleanup();
    setup(tracks, { selection: ['c1'] });
    expect(screen.queryByTestId('viewer-xform-hint')).toBeNull();
  });
});
