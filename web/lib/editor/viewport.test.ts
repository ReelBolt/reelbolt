import { describe, expect, it } from 'vitest';
import {
  FIT_MARGIN,
  MAX_ZOOM,
  MIN_ZOOM,
  applyPinch,
  classifyWheel,
  clampZoom,
  docToScreen,
  fitInsetsFor,
  fitView,
  formatZoom,
  ROTATE_HANDLE_OFFSET_PX,
  zoomBarHeight,
  ZOOM_BAR_INSET_PX,
  nextZoomStep,
  panBy,
  pinchState,
  screenToDoc,
  wheelPan,
  wheelZoomFactor,
  zoomAt,
} from './viewport';

const wheel = (over: Partial<Parameters<typeof classifyWheel>[0]>) => ({
  deltaX: 0,
  deltaY: 0,
  deltaMode: 0,
  ctrlKey: false,
  metaKey: false,
  shiftKey: false,
  ...over,
});

describe('fitView', () => {
  it('shows the whole frame, centred, inside the margin', () => {
    const view = fitView({ width: 1280, height: 720 }, { width: 1000, height: 800 });
    expect(view.zoom).toBeCloseTo((1000 - FIT_MARGIN * 2) / 1280);
    const tl = docToScreen(view, { x: 0, y: 0 });
    const br = docToScreen(view, { x: 1280, y: 720 });
    expect(tl.x).toBeCloseTo(FIT_MARGIN);
    expect(1000 - br.x).toBeCloseTo(FIT_MARGIN);
    expect(tl.y).toBeCloseTo(800 - br.y); // centred vertically
  });

  it('falls back to 1:1 with nothing to fit', () => {
    expect(fitView({ width: 0, height: 0 }, { width: 100, height: 100 })).toEqual({ zoom: 1, x: 0, y: 0 });
  });
});

describe('zooming and panning', () => {
  it('keeps the point under the cursor still while zooming', () => {
    const view = { zoom: 0.5, x: 40, y: 30 };
    const anchor = { x: 300, y: 200 };
    const before = screenToDoc(view, anchor);
    const after = zoomAt(view, 2.5, anchor);
    expect(screenToDoc(after, anchor).x).toBeCloseTo(before.x);
    expect(screenToDoc(after, anchor).y).toBeCloseTo(before.y);
    expect(after.zoom).toBeCloseTo(1.25);
  });

  it('never zooms past its limits', () => {
    expect(clampZoom(1000)).toBe(MAX_ZOOM);
    expect(clampZoom(0.00001)).toBe(MIN_ZOOM);
    expect(clampZoom(Number.NaN)).toBe(1);
    expect(zoomAt({ zoom: MAX_ZOOM, x: 0, y: 0 }, 4, { x: 0, y: 0 }).zoom).toBe(MAX_ZOOM);
  });

  it('round-trips screen and document points', () => {
    const view = { zoom: 1.7, x: -220, y: 15 };
    const p = { x: 1234.5, y: -50 };
    const back = screenToDoc(view, docToScreen(view, p));
    expect(back.x).toBeCloseTo(p.x);
    expect(back.y).toBeCloseTo(p.y);
  });

  it('pans by a screen delta', () => {
    expect(panBy({ zoom: 2, x: 1, y: 2 }, 10, -5)).toEqual({ zoom: 2, x: 11, y: -3 });
  });

  it('steps the zoom buttons through round numbers', () => {
    expect(nextZoomStep(1, 1)).toBe(1.5);
    expect(nextZoomStep(1, -1)).toBe(0.75);
    expect(nextZoomStep(0.8, -1)).toBe(0.75);
    expect(formatZoom(0.75)).toBe('75%');
    expect(formatZoom(0.05)).toBe('5%');
  });
});

describe('the wheel and the fingers', () => {
  it('zooms for a pinch, Ctrl/Cmd + wheel and a mouse wheel; pans for a trackpad scroll and Shift', () => {
    expect(classifyWheel(wheel({ deltaY: -3.2, ctrlKey: true }))).toBe('zoom');
    expect(classifyWheel(wheel({ deltaY: 100, metaKey: true }))).toBe('zoom');
    expect(classifyWheel(wheel({ deltaY: 100 }))).toBe('zoom');
    expect(classifyWheel(wheel({ deltaY: 3, deltaMode: 1 }))).toBe('zoom');
    expect(classifyWheel(wheel({ deltaY: 7.5 }))).toBe('pan');
    expect(classifyWheel(wheel({ deltaX: 12, deltaY: 40 }))).toBe('pan');
    expect(classifyWheel(wheel({ deltaY: 100, shiftKey: true }))).toBe('pan');
  });

  it('zooms in for a wheel turned up, and out for one turned down', () => {
    expect(wheelZoomFactor(-100)).toBeGreaterThan(1);
    expect(wheelZoomFactor(100)).toBeLessThan(1);
    expect(wheelZoomFactor(100) * wheelZoomFactor(-100)).toBeCloseTo(1);
  });

  it('turns Shift + wheel into a sideways pan', () => {
    expect(wheelPan(wheel({ deltaY: 30, shiftKey: true }))).toEqual({ dx: -30, dy: 0 });
    expect(wheelPan(wheel({ deltaX: 5, deltaY: 6 }))).toEqual({ dx: -5, dy: -6 });
  });

  it('pinches about the fingers and pans with them', () => {
    const view = { zoom: 1, x: 0, y: 0 };
    const before = pinchState({ x: 100, y: 100 }, { x: 200, y: 100 });
    const after = pinchState({ x: 60, y: 110 }, { x: 260, y: 110 });
    const next = applyPinch(view, before, after);
    expect(next.zoom).toBeCloseTo(2);
    // the document point that was under the old midpoint is now under the new one
    const docMid = screenToDoc(view, before.mid);
    const screenNow = docToScreen(next, docMid);
    expect(screenNow.x).toBeCloseTo(after.mid.x);
    expect(screenNow.y).toBeCloseTo(after.mid.y);
  });
});

describe('fit leaves the handles reachable (m4 N2)', () => {
  // The Pixel 7 and iPhone stages measured by the audit, with a portrait photo filling the frame.
  for (const [name, workspace] of [
    ['pixel', { width: 346, height: 468 }],
    ['iphone', { width: 324, height: 370 }],
    ['desktop', { width: 900, height: 640 }],
  ] as const) {
    for (const coarse of [true, false]) {
      it(`${name}, ${coarse ? 'touch' : 'mouse'}: the rotate handle is inside the top and the corners clear the zoom bar`, () => {
        const frame = { width: 1080, height: 1350 };
        const view = fitView(frame, workspace, fitInsetsFor(coarse));
        const top = view.y;
        const bottom = view.y + frame.height * view.zoom;
        const handleRadius = coarse ? 12 : 8;
        expect(top - ROTATE_HANDLE_OFFSET_PX - handleRadius).toBeGreaterThanOrEqual(0);
        const barTop = workspace.height - ZOOM_BAR_INSET_PX - zoomBarHeight(coarse);
        expect(bottom + handleRadius).toBeLessThanOrEqual(barTop);
        // still centred left to right
        expect(view.x).toBeCloseTo((workspace.width - frame.width * view.zoom) / 2);
      });
    }
  }

  it('a plain number is still a margin on every side', () => {
    expect(fitView({ width: 100, height: 100 }, { width: 164, height: 164 }, FIT_MARGIN)).toEqual({ zoom: 1, x: 32, y: 32 });
  });
});
