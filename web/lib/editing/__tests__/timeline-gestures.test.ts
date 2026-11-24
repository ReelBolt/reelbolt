// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import {
  EDGE_DEAD_ZONE_PX,
  LONG_PRESS_MS,
  ZOOM_MAX,
  ZOOM_MIN,
  classifyOneFinger,
  classifyTileScrub,
  clampZoom,
  inEdgeDeadZone,
  isDoubleTap,
  pinchViewport,
  zoomToRange,
} from '../timeline-gestures';
import { TOUCH_DRAG_KEY, readTouchDragEnabled, setTouchDragEnabled } from '../touch-drag';

const base = { downX: 100, downY: 100, x: 100, y: 100, elapsedMs: 0, onRuler: false, onClip: true, released: false };

describe('classifyOneFinger', () => {
  it('is undecided while still and young', () => expect(classifyOneFinger(base)).toBe('pending'));
  it('a finger lifted without moving is a tap', () => expect(classifyOneFinger({ ...base, released: true, elapsedMs: 120 })).toBe('tap'));
  it('a drag beyond the slop pans', () => expect(classifyOneFinger({ ...base, x: 140 })).toBe('pan'));
  it('a drag that started on the ruler scrubs', () => expect(classifyOneFinger({ ...base, x: 140, onRuler: true })).toBe('scrub'));
  it('holding still on a clip for 500 ms is a long press', () => {
    expect(classifyOneFinger({ ...base, elapsedMs: LONG_PRESS_MS - 1 })).toBe('pending');
    expect(classifyOneFinger({ ...base, elapsedMs: LONG_PRESS_MS })).toBe('long-press');
  });
  it('holding still on empty space is never a long press', () => expect(classifyOneFinger({ ...base, onClip: false, elapsedMs: 900 })).toBe('pending'));
  it('moving cancels the long press', () => expect(classifyOneFinger({ ...base, x: 130, elapsedMs: 900 })).toBe('pan'));
});

describe('edge dead zone', () => {
  it('ignores the outer 20 px of the screen on both sides', () => {
    expect(inEdgeDeadZone(EDGE_DEAD_ZONE_PX - 1, 400)).toBe(true);
    expect(inEdgeDeadZone(EDGE_DEAD_ZONE_PX, 400)).toBe(false);
    expect(inEdgeDeadZone(380, 400)).toBe(false);
    expect(inEdgeDeadZone(381, 400)).toBe(true);
  });
});

describe('pinch', () => {
  const start = { distance: 100, pixelsPerFrame: 2, anchorFrame: 175 };
  it('doubling the finger distance doubles the zoom and keeps the anchor under the midpoint', () => {
    expect(pinchViewport(start, 200, 400)).toEqual({ pixelsPerFrame: 4, scrollFrame: 75 });
  });
  it('is clamped to the zoom limits', () => {
    expect(pinchViewport(start, 1e6, 400).pixelsPerFrame).toBe(ZOOM_MAX);
    expect(pinchViewport(start, 0.001, 400).pixelsPerFrame).toBe(ZOOM_MIN);
    expect(clampZoom(0)).toBe(ZOOM_MIN);
  });
  it('moving the midpoint with the same distance pans', () => {
    expect(pinchViewport(start, 100, 300).scrollFrame).toBe(175 - 300 / 2);
  });
  it('never scrolls before the start of the timeline', () => expect(pinchViewport(start, 100, 9999).scrollFrame).toBe(0));
  it('leaves the zoom alone when the fingers started on top of each other', () => {
    expect(pinchViewport({ ...start, distance: 1 }, 300, 400).pixelsPerFrame).toBe(2);
  });
});

describe('zoomToRange and double tap', () => {
  it('fits a clip with a margin', () => {
    expect(zoomToRange(100, 200, 500)).toEqual({ pixelsPerFrame: 4, scrollFrame: 87.5 });
  });
  it('two taps on the same clip, close in time and place, are a double tap', () => {
    const a = { time: 1000, x: 50, y: 50, itemId: 'c1' };
    expect(isDoubleTap(a, { ...a, time: 1250, x: 60 })).toBe(true);
    expect(isDoubleTap(a, { ...a, time: 1400 })).toBe(false);
    expect(isDoubleTap(a, { ...a, time: 1100, x: 200 })).toBe(false);
    expect(isDoubleTap(a, { ...a, time: 1100, itemId: 'c2' })).toBe(false);
    expect(isDoubleTap(null, a)).toBe(false);
    expect(isDoubleTap({ ...a, itemId: null }, { ...a, itemId: null })).toBe(false);
  });
});

describe('classifyTileScrub', () => {
  it('waits, then scrubs on sideways intent, and hands vertical intent to the page scroll', () => {
    expect(classifyTileScrub(4, 3)).toBe('pending');
    expect(classifyTileScrub(14, 3)).toBe('scrub');
    expect(classifyTileScrub(-14, 6)).toBe('scrub');
    expect(classifyTileScrub(3, 14)).toBe('scroll');
    expect(classifyTileScrub(12, 12)).toBe('scroll');
  });
});

describe('the touch-drag flag', () => {
  afterEach(() => localStorage.clear());
  it('is off by default and per device (localStorage)', () => {
    expect(readTouchDragEnabled()).toBe(false);
    setTouchDragEnabled(true);
    expect(localStorage.getItem(TOUCH_DRAG_KEY)).toBe('on');
    expect(readTouchDragEnabled()).toBe(true);
    setTouchDragEnabled(false);
    expect(readTouchDragEnabled()).toBe(false);
  });
  it('uses the documented key', () => expect(TOUCH_DRAG_KEY).toBe('reelbolt:editor:touch-drag'));
});
