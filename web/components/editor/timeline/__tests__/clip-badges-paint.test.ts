/** @vitest-environment node */
/** The adjustment badge row: painted under the label band, parity dots, hit testing through the same layout. */

import { describe, expect, it } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { ClipBadgePaint } from '@/lib/editing/display/clip-badges';
import { DARK_THEME, hitTestBadge, paintTimeline, type PaintContext, type PaintUi, type Viewport } from '../paint';
import { CLIP_FALLBACK, withClipColors } from '../editorTheme';

const theme = withClipColors(DARK_THEME, CLIP_FALLBACK.dark);

function paint(badgesOf: PaintUi['badgesOf'], height?: number) {
  const fills: Array<{ x: number; y: number; w: number; h: number; fill: string }> = [];
  const texts: string[] = [];
  const ctx: Record<string, unknown> & { fillStyle: string } = {
    canvas: { width: 800, height: 300 },
    save: () => {}, restore: () => {}, setTransform: () => {}, clearRect: () => {}, strokeRect: () => {},
    fillText: (s: string) => { texts.push(s); },
    beginPath: () => {}, moveTo: () => {}, lineTo: () => {}, stroke: () => {}, rect: () => {}, clip: () => {},
    fillStyle: '', strokeStyle: '', lineWidth: 1, font: '', textAlign: 'left', textBaseline: 'top', globalAlpha: 1,
    fillRect: (x: number, y: number, w: number, h: number) => { fills.push({ x, y, w, h, fill: ctx.fillStyle }); },
  };
  const ui: PaintUi = {
    playheadFrame: 0, selection: new Set(), inFrame: null, outFrame: null, rationaleFrames: [], parityOf: () => 'faithful', badgesOf,
    trackFlags: (t) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo, collapsed: false }),
  };
  const viewport: Viewport = { width: 800, height: height ?? 300, scrollFrame: 0, pixelsPerFrame: 4, devicePixelRatio: 1 };
  const d = doc([track('v', 'video', [media('wide', 0, 100, 0), media('narrow', 120, 130, 0)])]);
  paintTimeline(ctx as unknown as PaintContext, d, viewport, ui, theme);
  return { fills, texts, d, ui, viewport };
}

const badge = (kind: ClipBadgePaint['kind'], parity: ClipBadgePaint['parity'] = 'faithful', enabled = true): ClipBadgePaint => ({ kind, parity, enabled });
const dots = (fills: ReturnType<typeof paint>['fills']) => fills.filter((f) => f.w === 4 && f.h === 4);

describe('clip badge row', () => {
  it('paints nothing without badges', () => {
    const { fills } = paint(undefined);
    expect(dots(fills)).toHaveLength(0);
  });
  it('a render-required badge gets the render-required dot, an approximate one the amber dot, a faithful one none', () => {
    const { fills } = paint((item) => (item.id === 'wide' ? [badge('keyer', 'renderRequired'), badge('speed', 'approximate'), badge('crop')] : []));
    const colours = dots(fills).map((f) => f.fill);
    expect(colours).toContain(theme.renderRequired);
    expect(colours).toContain(theme.approximate);
    expect(colours).toHaveLength(2);
  });
  it('a clip too narrow for the row paints a single +N badge', () => {
    const { texts } = paint((item) => (item.id === 'narrow' ? [badge('keyer'), badge('speed'), badge('crop'), badge('mask')] : []));
    // narrow spans 40 px at 4 px per frame: room for "+N" only
    expect(texts).toContain('+4');
  });
  it('the hit test finds a badge through the same layout, and misses elsewhere', () => {
    const { d, ui, viewport } = paint((item) => (item.id === 'wide' ? [badge('keyer')] : []));
    // The chip sits under the label band, right-aligned: scan the rows for it.
    let hit = null as ReturnType<typeof hitTestBadge>;
    for (let y = 0; y < 300 && !hit; y += 1) hit = hitTestBadge(d, viewport, ui, 400 - 3 - 7, y);
    expect(hit?.item.id).toBe('wide');
    expect(hit?.rect.kind).toBe('keyer');
    expect(hitTestBadge(d, viewport, ui, 10, 5)).toBeNull();
  });
});

describe('fade ramps', () => {
  function strokes(fadeIn: number, fadeOut: number) {
    const lines: Array<[string, number, number]> = [];
    const ctx: Record<string, unknown> = {
      canvas: { width: 800, height: 300 },
      save: () => {}, restore: () => {}, setTransform: () => {}, clearRect: () => {}, strokeRect: () => {}, fillText: () => {}, fillRect: () => {},
      beginPath: () => {}, moveTo: (x: number, y: number) => { lines.push(['m', x, y]); }, lineTo: (x: number, y: number) => { lines.push(['l', x, y]); }, stroke: () => {}, rect: () => {}, clip: () => {},
      fillStyle: '', strokeStyle: '', lineWidth: 1, font: '', textAlign: 'left', textBaseline: 'top', globalAlpha: 1,
    };
    const ui: PaintUi = { playheadFrame: 0, selection: new Set(), inFrame: null, outFrame: null, rationaleFrames: [], parityOf: () => 'faithful', trackFlags: (t) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo, collapsed: false }) };
    const viewport: Viewport = { width: 800, height: 300, scrollFrame: 0, pixelsPerFrame: 4, devicePixelRatio: 1 };
    paintTimeline(ctx as unknown as PaintContext, doc([track('v', 'video', [media('c', 0, 100, 0, { fadeInFrames: fadeIn || undefined, fadeOutFrames: fadeOut || undefined })])]), viewport, ui, theme);
    return lines;
  }
  it('no fades draw no diagonal; a fade in rises from the bottom-left corner, a fade out falls to the bottom-right', () => {
    const none = strokes(0, 0);
    const inOnly = strokes(10, 0);
    const both = strokes(10, 10);
    // a ramp is a move-to/line-to pair 40 px wide (10 frames at 4 px per frame): rising for a fade in, falling for a fade out
    const ramps = (lines: ReturnType<typeof strokes>, dir: 'up' | 'down') =>
      lines.filter((l, i) => l[0] === 'm' && lines[i + 1]?.[0] === 'l' && lines[i + 1]![1] - l[1] === 40 && (dir === 'up' ? lines[i + 1]![2] < l[2] : lines[i + 1]![2] > l[2])).length;
    expect(ramps(none, 'up') + ramps(none, 'down')).toBe(0);
    expect(ramps(inOnly, 'up')).toBe(1);
    expect(ramps(inOnly, 'down')).toBe(0);
    expect(ramps(both, 'up')).toBe(1);
    expect(ramps(both, 'down')).toBe(1);
  });
});
