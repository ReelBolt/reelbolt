// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { assetRef, doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

// The guide is painted, not DOM: capture the `PaintUi` the component passes to paintTimeline.
const paint = vi.hoisted(() => ({ calls: [] as unknown[][] }));
vi.mock('../paint', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../paint')>();
  return {
    ...actual,
    paintTimeline: (...args: unknown[]) => {
      paint.calls.push(args);
      return (actual.paintTimeline as (...a: unknown[]) => unknown)(...args);
    },
  };
});

import { TimelineCanvas } from '../TimelineCanvas';

// jsdom has no PointerEvent and no 2d context; both are stubs, as in ThumbnailEditor.test.tsx.
vi.stubGlobal('PointerEvent', class extends MouseEvent {
  pointerId: number;
  constructor(type: string, init: MouseEventInit & { pointerId?: number } = {}) {
    super(type, init);
    this.pointerId = init.pointerId ?? 1;
  }
});
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
// Mantine's color-scheme manager and TimelineCanvas's useMediaQuery read window.matchMedia, which
// jsdom does not implement; every editor suite stubs it the same way.
global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.setPointerCapture = vi.fn();
Element.prototype.releasePointerCapture = vi.fn();
Element.prototype.getBoundingClientRect = () =>
  ({ left: 0, top: 0, width: 800, height: 400, right: 800, bottom: 400, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;

const noop = () => {};
const ctx = new Proxy({}, { get: () => noop, set: () => true });
HTMLCanvasElement.prototype.getContext = (() => ctx) as unknown as typeof HTMLCanvasElement.prototype.getContext;

// Snaps 401 -> 400 and 411 -> 410: any move off a multiple of 10 shows a guide.
const snap = (frame: number) => Math.round(frame / 10) * 10;

const lastGuide = () => (paint.calls.at(-1)?.[3] as { snapGuideFrame?: number | null }).snapGuideFrame ?? null;

beforeEach(() => { paint.calls.length = 0; });
afterEach(cleanup);

const renderCanvas = () => render(
  <MantineProvider>
    <TimelineCanvas
      doc={doc([track(TRACKS.v1, 'video', [media('a', 0, 300, 0, { assetRef: assetRef(1) })])])}
      playheadFrame={0}
      selection={[]}
      inFrame={null}
      outFrame={null}
      pixelsPerFrame={1}
      scrollFrame={0}
      tool="select"
      trackFlags={() => ({ locked: false, enabled: true, mute: false, solo: false, collapsed: false })}
      parityOf={() => 'faithful'}
      rationaleFrames={[]}
      snap={snap}
      onSeek={noop} onSelect={noop} onMarquee={noop} onBlade={noop}
      onMoveItem={noop} onTrimItem={noop} onZoom={noop} onScroll={noop}
    />
  </MantineProvider>,
);

const canvas = () => document.querySelector('[data-testid="timeline-canvas"]') as HTMLCanvasElement;

describe('a cancelled drag', () => {
  it('leaves no snap guide painted', () => {
    renderCanvas();
    fireEvent.pointerDown(canvas(), { clientX: 401, clientY: 4, button: 0, pointerId: 1 });
    fireEvent.pointerMove(canvas(), { clientX: 401, clientY: 4, pointerId: 1 });
    expect(lastGuide()).toBe(400); // the guide is up before the cancel

    fireEvent.pointerCancel(canvas(), { clientX: 401, clientY: 4, pointerId: 1 });
    expect(lastGuide()).toBeNull();
  });

  it('does not resume the drag when the pointer moves afterwards', () => {
    renderCanvas();
    fireEvent.pointerDown(canvas(), { clientX: 401, clientY: 4, button: 0, pointerId: 1 });
    fireEvent.pointerMove(canvas(), { clientX: 401, clientY: 4, pointerId: 1 });
    fireEvent.pointerCancel(canvas(), { clientX: 401, clientY: 4, pointerId: 1 });

    fireEvent.pointerMove(canvas(), { clientX: 431, clientY: 4, pointerId: 1 });
    expect(lastGuide()).toBeNull();
  });

  it('is a harmless no-op when no drag is in flight', () => {
    renderCanvas();
    expect(() => fireEvent.pointerCancel(canvas(), { clientX: 401, clientY: 4, pointerId: 1 })).not.toThrow();
    expect(lastGuide()).toBeNull();
  });
});
