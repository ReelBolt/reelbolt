/** @vitest-environment node */
/**
 * The Events lane's layout and paint math: viewport-only visits, strength as height, shape as well as colour,
 * the 3:1 colour floor on both lane backgrounds, hit testing, the extra header height, and snapping to ticks.
 */

import { describe, expect, it } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { LaneEvent } from '@/lib/editing/events/events-data';
import { EVENT_TYPES } from '@/lib/editing/events/events-data';
import { snapCandidates, snapFrame } from '@/lib/editing/selection';
import {
  EVENT_COLORS, EVENTS_LANE_HEIGHT, EVENT_STYLE, eventStemHeight, eventStyle, hitTestEvent, LABEL_MIN_PIXELS_PER_FRAME, paintEventsLane, visibleEvents,
} from '../eventsLane';
import { DARK_THEME, headerHeight, LIGHT_THEME, MARKER_LANE_HEIGHT, RULER_HEIGHT, trackRows, hitTestItem, type PaintContext, type PaintUi, type Viewport } from '../paint';

const channel = (c: number): number => (c / 255 <= 0.03928 ? c / 255 / 12.92 : ((c / 255 + 0.055) / 1.055) ** 2.4);
const luminance = (hex: string): number => {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
};
const contrast = (a: string, b: string): number => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

const ev = (n: number, frame: number, type = 'hit', strength = 0.5): LaneEvent => ({ key: `f:e${n}:${frame}`, fileId: 'f', eventId: `e${n}`, clipId: 'c', trackId: 'v1', type, frame, sourceSec: frame / 30, strength, region: null });
const vp = (extra: Partial<Viewport> = {}): Viewport => ({ width: 800, height: 300, scrollFrame: 0, pixelsPerFrame: 2, devicePixelRatio: 1, ...extra });

function recorder() {
  const log: string[] = [];
  const texts: string[] = [];
  const fonts: string[] = [];
  const ctx = {
    canvas: { width: 800, height: 300 },
    save: () => undefined, restore: () => undefined, setTransform: () => undefined, clearRect: () => undefined,
    fillRect: () => log.push('fillRect'), strokeRect: () => log.push('strokeRect'),
    fillText: (t: string) => { texts.push(t); },
    beginPath: () => undefined, moveTo: () => undefined, lineTo: () => undefined, stroke: () => log.push('stroke'), rect: () => undefined, clip: () => undefined,
    fill: () => log.push('fill'),
    measureText: (t: string) => ({ width: t.length * 7 }),
    fillStyle: '', strokeStyle: '', lineWidth: 1, textAlign: 'left', textBaseline: 'top', globalAlpha: 1,
    set font(v: string) { fonts.push(v); }, get font() { return fonts[fonts.length - 1] ?? ''; },
  } as unknown as PaintContext;
  return { ctx, log, texts, fonts };
}
const ui = (items: LaneEvent[], extra: Partial<NonNullable<PaintUi['events']>> = {}): NonNullable<PaintUi['events']> => ({ items, labels: { hit: 'Hit', beat: 'Beat' }, activeKey: null, hoverKey: null, snap: true, ...extra });

describe('tick look', () => {
  it('strength is height: stronger is taller, never over the head room', () => {
    expect(eventStemHeight(0)).toBeLessThan(eventStemHeight(0.5));
    expect(eventStemHeight(0.5)).toBeLessThan(eventStemHeight(1));
    expect(eventStemHeight(5)).toBe(eventStemHeight(1));
    expect(eventStemHeight(1)).toBeLessThanOrEqual(EVENTS_LANE_HEIGHT - 11);
  });
  it('colour is never the only cue: every type has a shape, and no two types of one family share shape and fill', () => {
    for (const type of EVENT_TYPES) expect(EVENT_STYLE[type], type).toBeDefined();
    const seen = new Map<string, string>();
    for (const type of EVENT_TYPES) {
      const s = eventStyle(type);
      const id = `${s.family}/${s.shape}/${s.hollow}`;
      expect(seen.get(id), `${type} looks like ${seen.get(id)}`).toBeUndefined();
      seen.set(id, type);
    }
    expect(eventStyle('something_new')).toEqual({ family: 'other', shape: 'bar', hollow: false });
  });
  it('every family colour holds 3:1 on both lane backgrounds in both schemes', () => {
    for (const [scheme, theme] of [['dark', DARK_THEME], ['light', LIGHT_THEME]] as const) {
      for (const [family, color] of Object.entries(EVENT_COLORS[scheme])) {
        expect(contrast(color, theme.laneAlt), `${scheme} ${family} on laneAlt`).toBeGreaterThanOrEqual(3);
        expect(contrast(color, theme.background), `${scheme} ${family} on background`).toBeGreaterThanOrEqual(3);
      }
    }
  });
});

describe('visibility, clustering and hit testing', () => {
  const many = Array.from({ length: 5000 }, (_, i) => ev(i, i * 10));
  it('visits only the window, found by binary search', () => {
    const v = visibleEvents(many, 1000, 1400, 2);
    expect(v.length).toBe(41);
    expect(v[0].frame).toBe(1000);
  });
  it('merges ticks closer than 3 px at coarse zoom and keeps the strongest', () => {
    const v = visibleEvents([ev(0, 100, 'hit', 0.2), ev(1, 101, 'hit', 0.9), ev(2, 400)], 0, 1000, 0.5);
    expect(v.map((e) => e.eventId)).toEqual(['e1', 'e2']);
  });
  it('hit tests the nearest tick within 6 px, only inside the lane', () => {
    const items = [ev(0, 100), ev(1, 103)];
    const top = RULER_HEIGHT + MARKER_LANE_HEIGHT;
    expect(hitTestEvent(items, vp(), top, 100 * 2 + 1, top + 5)?.eventId).toBe('e0');
    expect(hitTestEvent(items, vp(), top, 103 * 2 - 1, top + 5)?.eventId).toBe('e1');
    expect(hitTestEvent(items, vp(), top, 100 * 2 + 20, top + 5)).toBeNull();
    expect(hitTestEvent(items, vp(), top, 200, top - 1)).toBeNull();
    expect(hitTestEvent(items, vp(), top, 200, top + EVENTS_LANE_HEIGHT)).toBeNull();
  });
});

describe('painting', () => {
  it('paints only the ticks in view, with a snap foot per tick while snapping is on', () => {
    const items = Array.from({ length: 3000 }, (_, i) => ev(i, i * 10));
    const on = recorder();
    const stats = paintEventsLane(on.ctx, vp({ scrollFrame: 1000 }), 42, ui(items), DARK_THEME, 1000, 1400);
    expect(stats.painted).toBe(41);
    const off = recorder();
    paintEventsLane(off.ctx, vp({ scrollFrame: 1000 }), 42, ui(items, { snap: false }), DARK_THEME, 1000, 1400);
    expect(on.log.filter((c) => c === 'fillRect').length - off.log.filter((c) => c === 'fillRect').length).toBe(41);
  });
  it('shows labels only when the zoom leaves room, without overlapping and at 12 px or more', () => {
    const items = [ev(0, 100), ev(1, 102), ev(2, 200, 'beat')];
    const far = recorder();
    paintEventsLane(far.ctx, vp({ pixelsPerFrame: LABEL_MIN_PIXELS_PER_FRAME / 4 }), 42, ui(items), DARK_THEME, 0, 400);
    expect(far.texts).toEqual([]);
    const near = recorder();
    const stats = paintEventsLane(near.ctx, vp({ pixelsPerFrame: 3 }), 42, ui(items), DARK_THEME, 0, 400);
    expect(near.texts).toEqual(['Hit', 'Beat']);
    expect(stats.labels).toBe(2);
    for (const f of near.fonts) expect(Number(/(\d+)px/.exec(f)?.[1])).toBeGreaterThanOrEqual(12);
  });
});

describe('the extra header height', () => {
  const d = doc([track('v1', 'video', [media('c1', 0, 300)])]);
  const base = { playheadFrame: 0, selection: new Set<string>(), inFrame: null, outFrame: null, trackFlags: () => ({ locked: false, enabled: true, mute: false, solo: false, collapsed: false }), parityOf: () => 'faithful' as const, rationaleFrames: [] };
  it('pushes the tracks down by exactly the lane height, and a click in the lane is not a clip', () => {
    const without = base as unknown as PaintUi;
    const withLane = { ...base, events: ui([]) } as unknown as PaintUi;
    expect(headerHeight(without)).toBe(RULER_HEIGHT + MARKER_LANE_HEIGHT);
    expect(headerHeight(withLane)).toBe(RULER_HEIGHT + MARKER_LANE_HEIGHT + EVENTS_LANE_HEIGHT);
    expect(trackRows(d, withLane)[0].y - trackRows(d, without)[0].y).toBe(EVENTS_LANE_HEIGHT);
    const y = RULER_HEIGHT + MARKER_LANE_HEIGHT + 4;
    expect(hitTestItem(d, vp(), without, 50, y)?.item.id).toBe('c1');
    expect(hitTestItem(d, vp(), withLane, 50, y)).toBeNull();
  });
});

describe('snapping to event ticks', () => {
  const d = doc([track('v1', 'video', [media('c1', 0, 300)])]);
  const ticks = [90, 150];
  const candidates = snapCandidates(d, { playheadFrame: 0, inFrame: null, outFrame: null, extraFrames: ticks });
  it('the playhead and a clip edge land on a tick within the threshold, and not beyond it', () => {
    expect(snapFrame(92, candidates, 4, true)).toEqual({ frame: 90, snapped: true, candidate: 90 });
    expect(snapFrame(147, candidates, 4, true).frame).toBe(150);
    expect(snapFrame(120, candidates, 4, true).snapped).toBe(false);
  });
  it('is the identity with snapping off, and ticks are only candidates when passed', () => {
    expect(snapFrame(92, candidates, 4, false).frame).toBe(92);
    expect(snapCandidates(d, { playheadFrame: 0, inFrame: null, outFrame: null })).not.toContain(90);
  });
});
