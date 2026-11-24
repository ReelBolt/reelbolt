/**
 * The Events lane under the ruler: layout and paint math, pure and counted like the rest of the canvas.
 * A tick is a stem whose height is the event's strength, capped by a head whose SHAPE says what kind it
 * is (so colour is never the only cue), in a family colour that holds 3:1 on both lane backgrounds.
 * Labels show only when the zoom leaves room for them. The data is derived, read-only and not in the document.
 */

import type { LaneEvent } from '@/lib/editing/events/events-data';
import type { PaintContext, PaintTheme, Viewport } from './paint';

export const EVENTS_LANE_HEIGHT = 26;
export type EventFamily = 'motion' | 'appearance' | 'text' | 'camera' | 'audio' | 'cut' | 'other';
export type EventShape = 'bar' | 'diamond' | 'triangle' | 'square' | 'circle' | 'cross';

export interface EventStyle {
  family: EventFamily;
  shape: EventShape;
  /** The head is drawn as an outline for the "end" half of a pair (disappear, text out, speech end). */
  hollow: boolean;
}

const OTHER: EventStyle = { family: 'other', shape: 'bar', hollow: false };

/** Type word -> look. A word the detector learns later falls back to `OTHER`. */
export const EVENT_STYLE: Readonly<Record<string, EventStyle>> = Object.freeze({
  appear: { family: 'appearance', shape: 'diamond', hollow: false },
  disappear: { family: 'appearance', shape: 'diamond', hollow: true },
  move_start: { family: 'motion', shape: 'triangle', hollow: false },
  move_stop: { family: 'motion', shape: 'square', hollow: false },
  hit: { family: 'cut', shape: 'bar', hollow: false },
  text_in: { family: 'text', shape: 'square', hollow: false },
  text_out: { family: 'text', shape: 'square', hollow: true },
  camera_move_start: { family: 'camera', shape: 'triangle', hollow: false },
  camera_move_stop: { family: 'camera', shape: 'triangle', hollow: true },
  transition: { family: 'cut', shape: 'cross', hollow: false },
  change: { family: 'other', shape: 'bar', hollow: false },
  beat: { family: 'audio', shape: 'circle', hollow: false },
  speech_start: { family: 'audio', shape: 'square', hollow: false },
  speech_end: { family: 'audio', shape: 'square', hollow: true },
  flash: { family: 'appearance', shape: 'circle', hollow: false },
  caption_tick: { family: 'text', shape: 'bar', hollow: false },
});

export const eventStyle = (type: string): EventStyle => EVENT_STYLE[type] ?? OTHER;

/** Family colours per scheme (`--rb-clip-*` style table; the lane test holds each to 3:1 on the lane backgrounds). */
export const EVENT_COLORS: Readonly<Record<'dark' | 'light', Readonly<Record<EventFamily, string>>>> = {
  dark: { motion: '#4dabf7', appearance: '#69db7c', text: '#ffd43b', camera: '#da77f2', audio: '#ff922b', cut: '#ff6b6b', other: '#adb5bd' },
  light: { motion: '#1971c2', appearance: '#2b8a3e', text: '#8f6a00', camera: '#9c36b5', audio: '#c2410c', cut: '#c92a2a', other: '#5c6370' },
};

/** What the painter needs besides the document. */
export interface EventsLaneUi {
  items: readonly LaneEvent[];
  /** Type word -> the words painted for it at zoom. */
  labels: Readonly<Record<string, string>>;
  /** The focused or last-chosen event (drawn with a ring). */
  activeKey: string | null;
  /** The event under the pointer. */
  hoverKey: string | null;
  /** Snapping to ticks is on: each tick gets a snap foot, so the indicator is visible on the lane itself. */
  snap: boolean;
}

const HEAD = 7;
const STEM_MIN = 6;
const HIT_RADIUS_PX = 6;
/** Ticks closer than this (CSS px) are drawn as one cluster at coarse zoom: the strongest wins. */
const MIN_GAP_PX = 3;
/** Zoom (px per frame) from which type labels are painted when they fit. */
export const LABEL_MIN_PIXELS_PER_FRAME = 2;

const clampStrength = (s: number): number => (Number.isFinite(s) ? Math.min(1, Math.max(0, s)) : 0);

/** The stem height of a tick: the strength spread over the lane (at least a short stub, never over the head room). */
export function eventStemHeight(strength: number, laneHeight = EVENTS_LANE_HEIGHT): number {
  const room = laneHeight - HEAD - 4;
  return Math.round(STEM_MIN + clampStrength(strength) * (room - STEM_MIN));
}

/** First index whose frame is >= `frame` (the items are ascending). */
export function firstEventAtOrAfter(items: readonly LaneEvent[], frame: number): number {
  let lo = 0;
  let hi = items.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (items[mid].frame < frame) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

/** The events inside `[from, to]` frames, found by binary search, with the cluster rule applied at coarse zoom. */
export function visibleEvents(items: readonly LaneEvent[], from: number, to: number, pixelsPerFrame: number): LaneEvent[] {
  const out: LaneEvent[] = [];
  const minFrames = pixelsPerFrame > 0 ? MIN_GAP_PX / pixelsPerFrame : 0;
  for (let i = firstEventAtOrAfter(items, from - 1); i < items.length; i++) {
    const e = items[i];
    if (e.frame > to + 1) break;
    const last = out[out.length - 1];
    if (last && e.frame - last.frame < minFrames) {
      if (e.strength > last.strength) out[out.length - 1] = e;
      continue;
    }
    out.push(e);
  }
  return out;
}

const xOf = (frame: number, viewport: Viewport): number => (frame - viewport.scrollFrame) * viewport.pixelsPerFrame;

/** The tick under (x, y), or null. `top` is the lane's y. The nearest tick within 6 px wins; clustered ticks are reachable by zooming. */
export function hitTestEvent(items: readonly LaneEvent[], viewport: Viewport, top: number, x: number, y: number, radiusPx = HIT_RADIUS_PX): LaneEvent | null {
  if (y < top || y >= top + EVENTS_LANE_HEIGHT) return null;
  const slack = radiusPx / viewport.pixelsPerFrame;
  const centre = viewport.scrollFrame + x / viewport.pixelsPerFrame;
  let best: LaneEvent | null = null;
  let bestDistance = Infinity;
  for (let i = firstEventAtOrAfter(items, centre - slack); i < items.length; i++) {
    const e = items[i];
    if (e.frame > centre + slack) break;
    const d = Math.abs(e.frame - centre);
    if (d < bestDistance) {
      best = e;
      bestDistance = d;
    }
  }
  return best;
}

type Pt = readonly [number, number];

function pathOf(ctx: PaintContext, pts: readonly Pt[], hollow: boolean): void {
  ctx.beginPath();
  pts.forEach(([px, py], i) => (i === 0 ? ctx.moveTo(px, py) : ctx.lineTo(px, py)));
  ctx.lineTo(pts[0][0], pts[0][1]);
  if (hollow || !ctx.fill) ctx.stroke();
  else ctx.fill();
}

/** Draws one head centred on (cx, cy). */
function paintHead(ctx: PaintContext, shape: EventShape, hollow: boolean, cx: number, cy: number): void {
  const r = HEAD / 2;
  switch (shape) {
    case 'diamond':
      pathOf(ctx, [[cx, cy - r - 1], [cx + r + 1, cy], [cx, cy + r + 1], [cx - r - 1, cy]], hollow);
      return;
    case 'triangle':
      pathOf(ctx, [[cx - r, cy - r], [cx + r + 1, cy], [cx - r, cy + r]], hollow);
      return;
    case 'square':
      if (hollow) ctx.strokeRect(cx - r + 0.5, cy - r + 0.5, HEAD - 1, HEAD - 1);
      else ctx.fillRect(cx - r, cy - r, HEAD, HEAD);
      return;
    case 'circle':
      // An octagon: reads as a dot at this size and needs only path calls.
      pathOf(ctx, [[cx - 1, cy - r], [cx + 1, cy - r], [cx + r, cy - 1], [cx + r, cy + 1], [cx + 1, cy + r], [cx - 1, cy + r], [cx - r, cy + 1], [cx - r, cy - 1]], hollow);
      return;
    case 'cross':
      ctx.beginPath();
      ctx.moveTo(cx - r, cy - r);
      ctx.lineTo(cx + r, cy + r);
      ctx.moveTo(cx + r, cy - r);
      ctx.lineTo(cx - r, cy + r);
      ctx.stroke();
      return;
    default:
      ctx.fillRect(cx - 2, cy - r, 4, HEAD);
  }
}

/** Counts what a paint touched, so the "viewport only" claim is assertable. */
export interface EventsLanePaintStats { painted: number; labels: number }

/** Paints the lane at `top`: background, ticks (stem + head), snap feet, the active ring and the labels that fit. */
export function paintEventsLane(ctx: PaintContext, viewport: Viewport, top: number, ui: EventsLaneUi, theme: PaintTheme, from: number, to: number): EventsLanePaintStats {
  const stats: EventsLanePaintStats = { painted: 0, labels: 0 };
  ctx.fillStyle = theme.laneAlt;
  ctx.fillRect(0, top, viewport.width, EVENTS_LANE_HEIGHT);
  ctx.strokeStyle = theme.tick;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, top + EVENTS_LANE_HEIGHT - 0.5);
  ctx.lineTo(viewport.width, top + EVENTS_LANE_HEIGHT - 0.5);
  ctx.stroke();

  const colors = theme.eventColors;
  const baseline = top + EVENTS_LANE_HEIGHT - 3;
  const showLabels = viewport.pixelsPerFrame >= LABEL_MIN_PIXELS_PER_FRAME;
  const visible = visibleEvents(ui.items, from, to, viewport.pixelsPerFrame);
  let labelRight = -Infinity;
  ctx.lineWidth = 2;
  for (const e of visible) {
    const x = Math.round(xOf(e.frame, viewport));
    if (x < -HEAD || x > viewport.width + HEAD) continue;
    const style = eventStyle(e.type);
    const color = colors[style.family];
    const stem = eventStemHeight(e.strength);
    const headY = baseline - stem;
    ctx.strokeStyle = color;
    ctx.fillStyle = color;
    ctx.globalAlpha = 1;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x + 0.5, baseline);
    ctx.lineTo(x + 0.5, headY);
    ctx.stroke();
    ctx.lineWidth = 1.5;
    paintHead(ctx, style.shape, style.hollow, x + 0.5, headY);
    if (ui.snap) {
      // The snap indicator on the lane itself: a small foot under each tick that is a snap target.
      ctx.fillRect(x - 1, baseline + 1, 3, 2);
    }
    if (e.key === ui.activeKey || e.key === ui.hoverKey) {
      ctx.strokeStyle = theme.playhead;
      ctx.lineWidth = 1;
      ctx.strokeRect(x - HEAD / 2 - 2.5, headY - HEAD / 2 - 2.5, HEAD + 5, HEAD + 5);
    }
    stats.painted += 1;
    if (showLabels) {
      const word = ui.labels[e.type] ?? e.type.replace(/_/g, ' ');
      const width = ctx.measureText ? ctx.measureText(word).width : word.length * 7;
      const lx = x + HEAD / 2 + 4;
      if (lx > labelRight && lx + width < viewport.width) {
        ctx.fillStyle = theme.rulerText;
        ctx.font = '12px system-ui, sans-serif';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';
        ctx.fillText(word, lx, top + EVENTS_LANE_HEIGHT / 2);
        labelRight = lx + width + 6;
        stats.labels += 1;
      }
    }
  }
  ctx.globalAlpha = 1;
  return stats;
}
