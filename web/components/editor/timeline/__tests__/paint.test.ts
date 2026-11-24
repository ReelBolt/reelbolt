/** @vitest-environment node */
/**
 * The painter's "viewport only" claim as a counted assertion: for a document whose off-screen items
 * outnumber the on-screen ones 10:1, the paint path visits a number of items bounded by the
 * viewport (plus one per track for the stop condition), not the document. Plus dpr-aware
 * transform, hit testing and ruler step selection.
 */

import { describe, expect, it } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import { DARK_THEME, firstVisibleIndex, frameToX, hitTestItem, paintTimeline, rulerStepFrames, RULER_HEIGHT, MARKER_LANE_HEIGHT, trackAtY, xToFrame, type PaintContext, type PaintUi, type Viewport } from '../paint';

function recordingContext(width: number, height: number) {
  const calls: Record<string, number> = {};
  const transforms: number[][] = [];
  const bump = (k: string) => {
    calls[k] = (calls[k] ?? 0) + 1;
  };
  const ctx: PaintContext = {
    canvas: { width, height },
    save: () => bump('save'),
    restore: () => bump('restore'),
    setTransform: (...args) => {
      transforms.push(args as number[]);
      bump('setTransform');
    },
    clearRect: () => bump('clearRect'),
    fillRect: () => bump('fillRect'),
    strokeRect: () => bump('strokeRect'),
    fillText: () => bump('fillText'),
    beginPath: () => bump('beginPath'),
    moveTo: () => bump('moveTo'),
    lineTo: () => bump('lineTo'),
    stroke: () => bump('stroke'),
    rect: () => bump('rect'),
    clip: () => bump('clip'),
    fillStyle: '',
    strokeStyle: '',
    lineWidth: 1,
    font: '',
    textAlign: 'left',
    textBaseline: 'top',
    globalAlpha: 1,
  };
  return { ctx, calls, transforms };
}

const ui: PaintUi = {
  playheadFrame: 0,
  selection: new Set(),
  inFrame: null,
  outFrame: null,
  trackFlags: (t) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo, collapsed: false }),
  parityOf: () => 'faithful',
  rationaleFrames: [],
};

/** 10-minute 30 fps timeline: 600 clips of 30 frames on one track. */
function longDoc() {
  const items = Array.from({ length: 600 }, (_, i) => media(`c${i}`, i * 30, i * 30 + 30));
  return doc([track('v', 'video', items), track('a', 'audio', items.map((it) => ({ ...it, id: `${it.id}a` })))]);
}

describe('paintTimeline beat grid', () => {
  it('draws a ruler tick per beat and a lane-spanning line only on bars, at the frame x', () => {
    const viewport: Viewport = { width: 1000, height: 300, scrollFrame: 0, pixelsPerFrame: 2, devicePixelRatio: 1 };
    const moves: Array<[number, number]> = [];
    const lines: Array<[number, number]> = [];
    const { ctx } = recordingContext(1000, 300);
    ctx.strokeStyle = '';
    ctx.moveTo = (x, y) => void moves.push([x, y]);
    ctx.lineTo = (x, y) => void lines.push([x, y]);
    const beatUi: PaintUi = { ...ui, beatGrid: [{ frame: 100, bar: true }, { frame: 115, bar: false }] };
    paintTimeline(ctx, longDoc(), viewport, beatUi);
    const through = (x: number) => lines.some(([lx, ly]) => lx === x && ly === 300);
    expect(moves.some(([x, y]) => x === 200 && y === RULER_HEIGHT - 10)).toBe(true);
    expect(moves.some(([x, y]) => x === 230 && y === RULER_HEIGHT - 6)).toBe(true);
    expect(through(200)).toBe(true);
    expect(through(230)).toBe(false);
    // Nothing is drawn without a grid.
    const none: Array<[number, number]> = [];
    const second = recordingContext(1000, 300);
    second.ctx.moveTo = (x, y) => void none.push([x, y]);
    paintTimeline(second.ctx, longDoc(), viewport, ui);
    expect(none.some(([x, y]) => x === 200 && y === RULER_HEIGHT - 10)).toBe(false);
  });
});

describe('paintTimeline', () => {
  it('visits a viewport-bounded number of items, not the whole document', () => {
    const d = longDoc();
    const viewport: Viewport = { width: 1200, height: 300, scrollFrame: 9000, pixelsPerFrame: 1, devicePixelRatio: 1 };
    const { ctx } = recordingContext(1200, 300);
    const stats = paintTimeline(ctx, d, viewport, ui);
    // 1200 px at 1 px/frame = 1200 frames = 40 items per track (+1 stop item each).
    expect(stats.tracks).toBe(2);
    expect(stats.paintedItems).toBeLessThanOrEqual(2 * 41);
    expect(stats.paintedItems).toBeGreaterThanOrEqual(2 * 39);
    expect(stats.visitedItems).toBeLessThanOrEqual(2 * 42);
    // The document holds 1200 items; the painter looked at well under a tenth of them.
    expect(stats.visitedItems).toBeLessThan(1200 / 10);
  });

  it('sets the dpr transform exactly once per paint', () => {
    const d = longDoc();
    const viewport: Viewport = { width: 800, height: 200, scrollFrame: 0, pixelsPerFrame: 2, devicePixelRatio: 2 };
    const { ctx, transforms } = recordingContext(1600, 400);
    paintTimeline(ctx, d, viewport, ui);
    expect(transforms).toEqual([[2, 0, 0, 2, 0, 0]]);
  });

  it('firstVisibleIndex binary-searches the first item ending after the window start', () => {
    const items = longDoc().tracks[0].items;
    expect(firstVisibleIndex(items, 0)).toBe(0);
    expect(firstVisibleIndex(items, 30)).toBe(1);
    expect(firstVisibleIndex(items, 45)).toBe(1);
    expect(firstVisibleIndex(items, 9000)).toBe(300);
    expect(firstVisibleIndex(items, 99999)).toBe(600);
  });

  it('frame↔x are inverses and the ruler step keeps labels apart', () => {
    const viewport: Viewport = { width: 1000, height: 200, scrollFrame: 120, pixelsPerFrame: 2, devicePixelRatio: 1 };
    expect(frameToX(130, viewport)).toBe(20);
    expect(xToFrame(20, viewport)).toBe(130);
    expect(rulerStepFrames(viewport, 30) * 2).toBeGreaterThanOrEqual(90);
    expect(rulerStepFrames({ ...viewport, pixelsPerFrame: 0.05 }, 30) * 0.05).toBeGreaterThanOrEqual(90);
  });

  it('hit-tests items and tracks with the same geometry the painter uses', () => {
    const d = longDoc();
    const viewport: Viewport = { width: 1000, height: 300, scrollFrame: 0, pixelsPerFrame: 2, devicePixelRatio: 1 };
    const y = RULER_HEIGHT + MARKER_LANE_HEIGHT + 10;
    expect(hitTestItem(d, viewport, ui, 10, y)?.item.id).toBe('c0');
    expect(hitTestItem(d, viewport, ui, 70, y)?.item.id).toBe('c1');
    expect(hitTestItem(d, viewport, ui, 10, 5)).toBeNull();
    expect(trackAtY(d, ui, y)?.id).toBe('v');
    expect(trackAtY(d, ui, y + 64)?.id).toBe('a');
    expect(trackAtY(d, ui, 5)).toBeNull();
  });
});

/** A context that logs every fill/stroke with the style in effect, in order. */
function loggingContext(width: number, height: number) {
  const log: { op: 'fill' | 'line'; style: string; x: number; y: number; w: number; h: number }[] = [];
  let path: { x: number; y: number }[] = [];
  const ctx = {
    canvas: { width, height },
    save: () => undefined,
    restore: () => undefined,
    setTransform: () => undefined,
    clearRect: () => undefined,
    fillRect(x: number, y: number, w: number, h: number) {
      log.push({ op: 'fill', style: String(ctx.fillStyle), x, y, w, h });
    },
    strokeRect: () => undefined,
    fillText: () => undefined,
    beginPath: () => {
      path = [];
    },
    moveTo: (x: number, y: number) => path.push({ x, y }),
    lineTo: (x: number, y: number) => path.push({ x, y }),
    stroke() {
      if (path.length >= 2) log.push({ op: 'line', style: String(ctx.strokeStyle), x: path[0].x, y: path[0].y, w: path[1].x - path[0].x, h: path[1].y - path[0].y });
    },
    rect: () => undefined,
    clip: () => undefined,
    fillStyle: '' as PaintContext['fillStyle'],
    strokeStyle: '' as PaintContext['strokeStyle'],
    lineWidth: 1,
    font: '',
    textAlign: 'left' as CanvasTextAlign,
    textBaseline: 'top' as CanvasTextBaseline,
    globalAlpha: 1,
  };
  return { ctx: ctx as PaintContext, log };
}

describe('in/out shading and snap guide (d2 N7, d7 N1, d7 N2)', () => {
  const d = doc([track('v', 'video', [media('c1', 0, 300)]), track('a', 'audio', [])]);
  const viewport: Viewport = { width: 600, height: 200, scrollFrame: 0, pixelsPerFrame: 1, devicePixelRatio: 1 };

  it('paints the marked range AFTER the ruler, marker lane and track rows, so it is visible', () => {
    const { ctx, log } = loggingContext(600, 200);
    paintTimeline(ctx, d, viewport, { ...ui, inFrame: 100, outFrame: 200 }, DARK_THEME);
    const shade = log.findIndex((e) => e.op === 'fill' && e.style === DARK_THEME.inOut);
    expect(shade).toBeGreaterThanOrEqual(0);
    const lastOpaqueRow = log.reduce((last, e, i) => (e.op === 'fill' && (e.style === DARK_THEME.background || e.style === DARK_THEME.laneAlt || e.style === DARK_THEME.ruler) ? i : last), -1);
    expect(shade).toBeGreaterThan(lastOpaqueRow);
    const fill = log[shade];
    expect(fill.x).toBe(100);
    expect(fill.w).toBe(100);
    // The ruler shows the range too.
    expect(fill.y).toBe(0);
  });

  it('shades the range the marks span even when Out was marked before In', () => {
    const { ctx, log } = loggingContext(600, 200);
    paintTimeline(ctx, d, viewport, { ...ui, inFrame: 300, outFrame: 100 }, DARK_THEME);
    const fill = log.find((e) => e.op === 'fill' && e.style === DARK_THEME.inOut)!;
    expect(fill.x).toBe(100);
    expect(fill.w).toBe(200);
  });

  it('writes the snap label at least 12 px beside the guide, and nothing without a guide', () => {
    const { ctx, log } = loggingContext(600, 200);
    const fonts: string[] = [];
    const orig = ctx.fillText.bind(ctx);
    ctx.fillText = (text, x, y, w) => {
      if (text === 'Snapped to playhead') fonts.push(ctx.font);
      orig(text, x, y, w);
    };
    paintTimeline(ctx, d, viewport, { ...ui, snapGuideFrame: 150, snapGuideLabel: 'Snapped to playhead' }, DARK_THEME);
    expect(fonts).toHaveLength(1);
    expect(Number(/(\d+)px/.exec(fonts[0])?.[1])).toBeGreaterThanOrEqual(12);
    expect(log.some((e) => e.op === 'line' && e.style === DARK_THEME.snapGuide)).toBe(true);
    const { ctx: off } = loggingContext(600, 200);
    const seen: string[] = [];
    const origOff = off.fillText.bind(off);
    off.fillText = (text, x, y, w) => {
      seen.push(text);
      origOff(text, x, y, w);
    };
    paintTimeline(off, d, viewport, { ...ui, snapGuideFrame: null, snapGuideLabel: 'Snapped to playhead' }, DARK_THEME);
    expect(seen).not.toContain('Snapped to playhead');
  });

  it('draws a snap guide line at the frame an edge snapped to', () => {
    const { ctx, log } = loggingContext(600, 200);
    paintTimeline(ctx, d, viewport, { ...ui, snapGuideFrame: 150 }, DARK_THEME);
    const guide = log.find((e) => e.op === 'line' && e.style === DARK_THEME.snapGuide);
    expect(guide).toBeDefined();
    expect(Math.floor(guide!.x)).toBe(150);
    const { ctx: none, log: noneLog } = loggingContext(600, 200);
    paintTimeline(none, d, viewport, ui, DARK_THEME);
    expect(noneLog.some((e) => e.op === 'line' && e.style === DARK_THEME.snapGuide)).toBe(false);
  });
});
