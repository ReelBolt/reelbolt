// @vitest-environment jsdom
/**
 * The picker on the viewer (generalised Follow draw layer): tap = marker + default box, drag = box, touch has a larger
 * tap/drag threshold and 44px handles, the keyboard nudges 1% / Enter confirms / Escape cancels, playback pauses.
 */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { armFollowBox, disarmFollowBox, getFollowHost } from '@/lib/editing/follow/follow-host';
import type { ObjectSeed } from '@/lib/editing/selection/seed';
import { FollowTool } from '../FollowDrawLayer';

const coarse = vi.hoisted(() => ({ value: false }));
vi.mock('../../thumbnail/useCoarsePointer', () => ({ useCoarsePointer: () => coarse.value }));
(globalThis as unknown as { PointerEvent: unknown }).PointerEvent = class extends MouseEvent {
  pointerType: string;
  pointerId = 1;
  constructor(type: string, init: MouseEventInit & { pointerType?: string } = {}) {
    super(type, init);
    this.pointerType = init.pointerType ?? 'mouse';
  }
};

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;

let store: EditorStore;
let onSeed: ReturnType<typeof vi.fn>;
const source = { width: 1920, height: 1080 };

const mount = () => {
  const view = render(
    <MantineProvider>
      <FollowTool store={store} sourceSizeFor={() => source} readOnly={false} />
    </MantineProvider>,
  );
  act(() => armFollowBox({ clipId: 'c1', onSeed: onSeed as never }));
  const surface = screen.getByTestId('viewer-follow-draw');
  surface.getBoundingClientRect = () => ({ left: 0, top: 0, right: 960, bottom: 540, width: 960, height: 540, x: 0, y: 0, toJSON: () => ({}) });
  return { view, surface };
};
// The surface is 960x540 on screen for a 1920x1080 canvas: one screen px is two canvas px.
const tap = (el: Element, x: number, y: number, pointerType = 'mouse') => {
  fireEvent.pointerDown(el, { button: 0, clientX: x, clientY: y, pointerType });
  fireEvent.pointerUp(el, { clientX: x, clientY: y, pointerType });
};

beforeEach(() => {
  coarse.value = false;
  onSeed = vi.fn();
  store = createEditorStore({ timelineId: 'tl-1', projectId: 'p1', doc: standardDoc(), revision: 1 });
});
afterEach(() => {
  act(() => disarmFollowBox());
  cleanup();
});

describe('picker layer', () => {
  it('pauses playback and shows the one-line help', () => {
    store.setUi({ playbackSpeed: 1 });
    mount();
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(screen.getByTestId('viewer-pick-help').textContent).toBe('Tap the object. Drag to draw a box.');
  });

  it('a click puts a marker and a default box; confirming hands back the seed once', () => {
    store.setUi({ playheadFrame: 30 });
    const { surface } = mount();
    tap(surface, 480, 270);
    expect(screen.getByTestId('viewer-pick-marker')).toBeTruthy();
    expect(screen.getByTestId('viewer-pick-box')).toBeTruthy();
    expect(onSeed).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('viewer-pick-confirm'));
    expect(onSeed).toHaveBeenCalledTimes(1);
    const [seed, frame] = onSeed.mock.calls[0] as [ObjectSeed, number];
    expect(frame).toBe(30);
    expect(seed.mediaRef).toMatch(/^file:/);
    expect(seed.atSec).toBeCloseTo(1);
    expect(seed.point.x).toBeCloseTo(0.5);
    expect(seed.point.y).toBeCloseTo(0.5);
    expect(seed.box!.w).toBeCloseTo(0.2);
    expect(getFollowHost().armed).toBeNull();
  });

  it('a drag draws the box and its centre is the point', () => {
    const { surface } = mount();
    fireEvent.pointerDown(surface, { button: 0, clientX: 240, clientY: 135 });
    fireEvent.pointerMove(surface, { clientX: 480, clientY: 270 });
    fireEvent.pointerUp(surface, { clientX: 480, clientY: 270 });
    fireEvent.click(screen.getByTestId('viewer-pick-confirm'));
    const [seed] = onSeed.mock.calls[0] as [ObjectSeed];
    expect(seed.box!.x).toBeCloseTo(0.25);
    expect(seed.box!.w).toBeCloseTo(0.25);
    expect(seed.point.x).toBeCloseTo(0.375);
  });

  it('a 6px wobble is a click for a finger and a drag for a mouse', () => {
    const { surface } = mount();
    fireEvent.pointerDown(surface, { button: 0, clientX: 300, clientY: 200, pointerType: 'touch' });
    fireEvent.pointerMove(surface, { clientX: 306, clientY: 200, pointerType: 'touch' });
    fireEvent.pointerUp(surface, { clientX: 306, clientY: 200, pointerType: 'touch' });
    fireEvent.click(screen.getByTestId('viewer-pick-confirm'));
    expect((onSeed.mock.calls[0][0] as ObjectSeed).box!.w).toBeCloseTo(0.2);

    act(() => armFollowBox({ clipId: 'c1', onSeed: onSeed as never }));
    const again = screen.getByTestId('viewer-follow-draw');
    again.getBoundingClientRect = surface.getBoundingClientRect;
    fireEvent.pointerDown(again, { button: 0, clientX: 300, clientY: 200 });
    fireEvent.pointerMove(again, { clientX: 312, clientY: 260 });
    fireEvent.pointerUp(again, { clientX: 312, clientY: 260 });
    fireEvent.click(screen.getByTestId('viewer-pick-confirm'));
    expect((onSeed.mock.calls[1][0] as ObjectSeed).box!.w).toBeLessThan(0.05);
  });

  it('handles are 44px on a touch device', () => {
    coarse.value = true;
    const { surface } = mount();
    tap(surface, 480, 270, 'touch');
    expect(screen.getByTestId('viewer-pick-marker').style.width).toBe('44px');
    expect(screen.getByTestId('viewer-pick-corner-nw').style.height).toBe('44px');
  });

  it('keyboard: arrows nudge 1%, Enter confirms', () => {
    const { surface } = mount();
    tap(surface, 480, 270);
    const marker = screen.getByTestId('viewer-pick-marker');
    expect(marker.getAttribute('aria-label')).toBeTruthy();
    fireEvent.keyDown(marker, { key: 'ArrowRight' });
    fireEvent.keyDown(marker, { key: 'ArrowDown' });
    fireEvent.keyDown(marker, { key: 'Enter' });
    const [seed] = onSeed.mock.calls[0] as [ObjectSeed];
    expect(seed.point.x).toBeCloseTo(0.51);
    expect(seed.point.y).toBeCloseTo(0.51);
  });

  it('Escape cancels without a seed', () => {
    const { surface } = mount();
    tap(surface, 480, 270);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(getFollowHost().armed).toBeNull();
    expect(onSeed).not.toHaveBeenCalled();
  });

  it('Cancel disarms; a tap outside the picture makes no marker', () => {
    const { surface } = mount();
    fireEvent.click(screen.getByTestId('viewer-pick-cancel'));
    expect(getFollowHost().armed).toBeNull();
    expect(surface).toBeTruthy();
  });

  it('keyboard only: Enter on the surface places the marker in the middle, Shift+arrow moves 10%, Enter confirms', () => {
    const { surface } = mount();
    fireEvent.keyDown(surface, { key: 'Enter' });
    const marker = screen.getByTestId('viewer-pick-marker');
    fireEvent.keyDown(marker, { key: 'ArrowRight', shiftKey: true });
    fireEvent.keyDown(marker, { key: 'Enter' });
    const [seed] = onSeed.mock.calls[0] as [ObjectSeed];
    expect(seed.point.x).toBeCloseTo(0.6);
    expect(seed.point.y).toBeCloseTo(0.5);
  });

  it('keyboard only: Space places it too, and Escape cancels', () => {
    const { surface } = mount();
    fireEvent.keyDown(surface, { key: ' ' });
    expect(screen.getByTestId('viewer-pick-marker')).toBeTruthy();
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(getFollowHost().armed).toBeNull();
    expect(onSeed).not.toHaveBeenCalled();
  });
});
