// @vitest-environment jsdom
/** The picture (transform) overlay: Escape cancels a drag alone (no write, no deselect) and an unmount mid-drag clears the draft. */

import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import React from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TransformOverlay } from '../TransformOverlay';
import { draftFor } from '@/lib/editing/viewer-transform';
import { createEditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { buildKeymap, eventToChord, shortcutBlocked } from '@/lib/editing/keymap';
import { runCommand, type CommandContext } from '@/lib/editing/commands';

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
afterEach(() => cleanup());

const ptr = (type: string, x: number, y: number) => new PointerEvent(type, { bubbles: true, clientX: x, clientY: y, pointerId: 1 });

function setup() {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0)])]), revision: 1 });
  store.setUi({ selection: ['c1'], playheadFrame: 10, playbackSpeed: 0 });
  const view = render(
    <MantineProvider>
      <TransformOverlay store={store} sourceSizeFor={() => ({ width: 1920, height: 1080 })} readOnly={false} />
    </MantineProvider>,
  );
  const body = screen.getByTestId('viewer-xform-body');
  act(() => {
    body.dispatchEvent(ptr('pointerdown', 480, 270));
    body.dispatchEvent(ptr('pointermove', 560, 300));
  });
  return { store, view, body };
}

describe('TransformOverlay drag', () => {
  it('one Escape through the real keymap cancels the drag: no write, no deselect, and the release writes nothing', () => {
    const { store, body } = setup();
    const keymap = buildKeymap({});
    const onKey = (event: KeyboardEvent) => {
      const chord = eventToChord(event);
      if (shortcutBlocked(event.target, chord)) return;
      const action = keymap.get(chord);
      if (action) runCommand(action, { store, event } as unknown as CommandContext);
    };
    window.addEventListener('keydown', onKey);
    expect(draftFor(store).get()).not.toBeNull();
    act(() => void fireEvent.keyDown(document.body, { key: 'Escape', code: 'Escape' }));
    expect(draftFor(store).get()).toBeNull();
    expect(store.getState().ui.selection).toEqual(['c1']);
    act(() => void body.dispatchEvent(ptr('pointerup', 560, 300)));
    expect(store.getState().history).toHaveLength(0);
    window.removeEventListener('keydown', onKey);
  });

  it('Escape with no drag is left alone', () => {
    const { body } = setup();
    act(() => void body.dispatchEvent(ptr('pointerup', 560, 300)));
    const e = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    window.dispatchEvent(e);
    expect(e.defaultPrevented).toBe(false);
  });

  it('an unmount mid-drag clears the draft', () => {
    const { store, view } = setup();
    expect(draftFor(store).get()).not.toBeNull();
    view.unmount();
    expect(draftFor(store).get()).toBeNull();
  });
});
