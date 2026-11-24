/**
 * The timeline painter — pure functions from (document, viewport, ui) to draw calls on a minimal
 * 2D-context interface, so the "viewport only" claim is a counted assertion, not a hope.
 *
 * Why canvas: v1 positioned DOM divs by percentage, which does not survive frame-level zoom on a
 * 10-minute timeline (18 000 frames → 18 000 nodes). Here lanes are painted, only items that
 * intersect the visible frame window are visited per track (binary search for the first visible
 * item, then a forward scan that stops at the first item past the right edge), and DOM is used
 * only for the selected item's handles.
 *
 * Coordinates: `x = (frame - scrollFrame) * pixelsPerFrame`. Everything is in CSS pixels; the
 * component scales the backing store by devicePixelRatio and sets the transform once per frame.
 */

import { CLIP_FALLBACK, withClipColors } from './editorTheme';
import type { EditTimeline, Item, Track } from '@/lib/editing/types';
import type { ParityClass } from '@/lib/editing/preview';
import { framesToTimecode } from '@/lib/editing/time';
import type { ClipJobPaint } from '@/lib/editing/background/job-marks';
import { jobChipRect, type JobChipRect } from './clipJobPaint';
import { badgeAt, type BadgeRect, type ClipBadgePaint } from '@/lib/editing/display/clip-badges';
import { badgeRectsFor } from './clipBadgePaint';
import { paintClipContent, type ClipArt, type ClipView } from './clipPaint';
import { FALLBACK_PAINT_LABELS, type PaintLabels } from './paintLabels';
import { EVENT_COLORS, EVENTS_LANE_HEIGHT, paintEventsLane, type EventFamily, type EventsLaneUi } from './eventsLane';

export interface PaintContext {
  canvas: { width: number; height: number };
  save(): void;
  restore(): void;
  setTransform(a: number, b: number, c: number, d: number, e: number, f: number): void;
  clearRect(x: number, y: number, w: number, h: number): void;
  fillRect(x: number, y: number, w: number, h: number): void;
  strokeRect(x: number, y: number, w: number, h: number): void;
  fillText(text: string, x: number, y: number, maxWidth?: number): void;
  beginPath(): void;
  moveTo(x: number, y: number): void;
  lineTo(x: number, y: number): void;
  stroke(): void;
  rect(x: number, y: number, w: number, h: number): void;
  /** Rounded clip bodies where the browser has it; a plain rect otherwise (and in tests). */
  roundRect?(x: number, y: number, w: number, h: number, radii: number): void;
  fill?(): void;
  clip(): void;
  /** Thumbnails; absent in tests, where no picture is ever supplied. */
  drawImage?(image: CanvasImageSource, sx: number, sy: number, sw: number, sh: number, dx: number, dy: number, dw: number, dh: number): void;
  measureText?(text: string): { width: number };
  fillStyle: string | CanvasGradient | CanvasPattern;
  strokeStyle: string | CanvasGradient | CanvasPattern;
  lineWidth: number;
  font: string;
  textAlign: CanvasTextAlign;
  textBaseline: CanvasTextBaseline;
  globalAlpha: number;
}

export interface Viewport {
  /** CSS pixel width/height of the visible area. */
  width: number;
  height: number;
  scrollFrame: number;
  pixelsPerFrame: number;
  devicePixelRatio: number;
}

export interface PaintUi {
  playheadFrame: number;
  selection: ReadonlySet<string>;
  inFrame: number | null;
  outFrame: number | null;
  /** d7 N2: the frame a dragged edge or the scrub snapped to, drawn as a guide line (null/absent: none). */
  snapGuideFrame?: number | null;
  /** T-020: what the guide caught, in words ('Snapped to playhead'); drawn beside the guide, at least 12 px. */
  snapGuideLabel?: string | null;
  /** Per-track effective flags (document + UI overrides). */
  trackFlags: (track: Track) => { locked: boolean; enabled: boolean; mute: boolean; solo: boolean; collapsed: boolean };
  parityOf: (itemId: string) => ParityClass;
  /** R03.4: poster / filmstrip / waveform / offline answers for media clips; absent paints name and colour only. */
  clipArt?: ClipArt;
  /** R03.5: the view-only toggles (waveforms, thumbnails, names). Absent = all on. */
  view?: ClipView;
  /** R08.1: true for a clip that is linked to another (a stripe along its foot). */
  isLinked?: (item: Item) => boolean;
  /** Clips a background job is still working on (kept for callers that only know "busy"): each shows the running state. */
  busyClipIds?: ReadonlySet<string>;
  /** What a background job shows on a clip (running / ready to review / failed); wins over `busyClipIds`. */
  jobOf?: (itemId: string) => ClipJobPaint | undefined;
  /** The clock of the moving stripe; absent = static hatching (reduced motion, or the first paint). */
  nowMs?: number;
  /** The adjustment badges to draw on a clip (lib/editing/display/clip-badges.ts); absent = none. */
  badgesOf?: (item: Item) => readonly ClipBadgePaint[];
  /** Rationale marker frames (derived, read-only). */
  rationaleFrames: readonly number[];
  /**
   * T-008: the beat grid of the music clip, in record frames. Bar lines run through the lanes, every
   * beat gets a tick on the ruler foot. Derived from the analysis and never stored in the document.
   */
  beatGrid?: readonly { frame: number; bar: boolean }[];
  /** The Events lane under the marker lane (ticks by type, strength as height). Absent = no lane and no extra height. */
  events?: EventsLaneUi;
  /**
   * The last render's region picture, painted as a thin strip at the foot of the ruler: blue where
   * the region came from the render cache, red where it was rendered afresh. Absent (or empty) means
   * no render matches the current revision, and nothing is painted — the strip never guesses.
   */
  renderRegions?: readonly { startFrame: number; endFrame: number; cached: boolean }[];
  /**
   * P8: an AI-assist proposal's ghost diff — additions outlined (green), removals hatched (red),
   * changes outlined at the new span with the old span hatched (orange). A ghost on a clip the user
   * made gets a heavier outline and a "yours" tag, so it can never change silently.
   */
  /** Words painted on the canvas (offline, "yours", ...); absent = the English fallback. */
  labels?: PaintLabels;
  ghosts?: readonly { itemId: string; trackId: string; recordIn: number; recordOut: number; kind: 'add' | 'remove' | 'change'; before?: { recordIn: number; recordOut: number; trackId: string }; userOwned: boolean }[];
}

export const GHOST_COLORS = { add: '#2f9e44', remove: '#e03131', change: '#f08c00' } as const;

/** Diagonal hatching over a rect, built from line segments (the context has no pattern API in tests). */
function hatch(ctx: PaintContext, x: number, y: number, w: number, h: number, color: string): void {
  ctx.save();
  ctx.beginPath();
  ctx.rect(x, y, w, h);
  ctx.clip();
  ctx.strokeStyle = color;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let d = -h; d < w; d += 6) {
    ctx.moveTo(x + d, y + h);
    ctx.lineTo(x + d + h, y);
  }
  ctx.stroke();
  ctx.restore();
}

/** Paints the ghost diff over the lanes (see {@link PaintUi.ghosts}). Returns how many ghosts were drawn. */
export function paintGhosts(ctx: PaintContext, rows: { track: Track; y: number; height: number }[], viewport: Viewport, ghosts: NonNullable<PaintUi['ghosts']>, yoursLabel: string = FALLBACK_PAINT_LABELS.yours): number {
  const rowOf = new Map(rows.map((r) => [r.track.id, r]));
  let painted = 0;
  const box = (trackId: string, recordIn: number, recordOut: number) => {
    const row = rowOf.get(trackId);
    if (!row) return null;
    const x0 = frameToX(recordIn, viewport);
    const x1 = frameToX(recordOut, viewport);
    if (x1 < 0 || x0 > viewport.width) return null;
    return { x: x0, y: row.y + 3, w: Math.max(2, x1 - x0), h: row.height - 6 };
  };
  for (const ghost of ghosts) {
    const color = GHOST_COLORS[ghost.kind];
    if (ghost.kind === 'remove') {
      const b = box(ghost.trackId, ghost.recordIn, ghost.recordOut);
      if (!b) continue;
      hatch(ctx, b.x, b.y, b.w, b.h, color);
    } else {
      if (ghost.kind === 'change' && ghost.before && (ghost.before.recordIn !== ghost.recordIn || ghost.before.recordOut !== ghost.recordOut || ghost.before.trackId !== ghost.trackId)) {
        const old = box(ghost.before.trackId, ghost.before.recordIn, ghost.before.recordOut);
        if (old) hatch(ctx, old.x, old.y, old.w, old.h, color);
      }
      const b = box(ghost.trackId, ghost.recordIn, ghost.recordOut);
      if (!b) continue;
      ctx.globalAlpha = 0.18;
      ctx.fillStyle = color;
      ctx.fillRect(b.x, b.y, b.w, b.h);
      ctx.globalAlpha = 1;
    }
    const b = box(ghost.trackId, ghost.recordIn, ghost.recordOut);
    if (!b) continue;
    ctx.strokeStyle = color;
    ctx.lineWidth = ghost.userOwned ? 3 : 1.5;
    ctx.strokeRect(b.x + 0.5, b.y + 0.5, b.w - 1, b.h - 1);
    ctx.lineWidth = 1;
    if (ghost.userOwned && b.w > 30) {
      ctx.fillStyle = color;
      ctx.font = 'bold 12px system-ui, sans-serif';
      ctx.textAlign = 'left';
      ctx.textBaseline = 'bottom';
      ctx.fillText(yoursLabel, b.x + 4, b.y + b.h - 2, b.w - 8);
    }
    painted++;
  }
  return painted;
}

export interface PaintTheme {
  background: string;
  laneAlt: string;
  laneDisabled: string;
  ruler: string;
  rulerText: string;
  tick: string;
  item: Record<Item['type'], string>;
  /** A media clip on an audio track. */
  audioClip: string;
  /** A disabled clip, or one on a muted or disabled track: the same text colour, a quieter fill. */
  mutedClip: string;
  itemText: string;
  selected: string;
  playhead: string;
  marker: string;
  rationale: string;
  inOut: string;
  /** The edges of the marked range. */
  inOutEdge: string;
  snapGuide: string;
  /** The snap label's text: on the canvas background it needs 4.5:1, which the guide line's colour need not reach. */
  snapLabelText: string;
  approximate: string;
  renderRequired: string;
  locked: string;
  /** The stripe along the foot of a linked clip. */
  linked: string;
  /** T-008: the beat-grid overlay's bar lines and beat ticks. */
  beat: string;
  /** The Events lane's family colours (3:1 on this theme's lanes). */
  eventColors: Readonly<Record<EventFamily, string>>;
  /** The dark chip behind badges and job labels (the `--rb-clip-chip` token). */
  chip: string;
}

export const DARK_THEME: PaintTheme = withClipColors({
  background: '#16171b',
  laneAlt: '#1a1b20',
  laneDisabled: '#121316',
  ruler: '#1c1d22',
  rulerText: '#9a9ca5',
  tick: '#2d2f36',
  playhead: '#ff5c5c',
  marker: '#5aa9f0',
  rationale: '#5fc77a',
  inOut: 'rgba(255, 212, 59, 0.16)',
  inOutEdge: 'rgba(255, 212, 59, 0.85)',
  snapGuide: '#4dd0e1',
  snapLabelText: '#4dd0e1',
  approximate: '#f2b30a',
  renderRequired: '#ff7a7a',
  locked: 'rgba(255,255,255,0.06)',
  linked: '#ffd43b',
  beat: '#a78bfa',
  eventColors: EVENT_COLORS.dark,
}, CLIP_FALLBACK.dark);
/** The same roles on a light surface: the app's light theme follows through to the canvas. */
export const LIGHT_THEME: PaintTheme = withClipColors({
  background: '#fbfbfc',
  laneAlt: '#f4f5f7',
  laneDisabled: '#eceef1',
  ruler: '#ffffff',
  rulerText: '#6b6f78',
  tick: '#e1e3e8',
  playhead: '#e5484d',
  marker: '#3b8fe0',
  rationale: '#3fae5c',
  inOut: 'rgba(250, 176, 5, 0.20)',
  inOutEdge: 'rgba(230, 150, 0, 0.9)',
  snapGuide: '#0b9fb5',
  snapLabelText: '#06707f',
  approximate: '#e9a700',
  renderRequired: '#e5484d',
  locked: 'rgba(0,0,0,0.04)',
  linked: '#e8a900',
  beat: '#6d4ad8',
  eventColors: EVENT_COLORS.light,
}, CLIP_FALLBACK.light);
/** Fill a clip body: rounded where the context can, a plain rect where it cannot. */
function fillBody(ctx: PaintContext, x: number, y: number, w: number, h: number): void {
  if (ctx.roundRect && ctx.fill && w > 6) {
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, 4);
    ctx.fill();
    return;
  }
  ctx.fillRect(x, y, w, h);
}

export const RULER_HEIGHT = 28;
export const MARKER_LANE_HEIGHT = 14;

/** Height of everything above the first track: ruler, marker lane and, when shown, the Events lane. */
export function headerHeight(ui: Pick<PaintUi, 'events'>): number {
  return RULER_HEIGHT + MARKER_LANE_HEIGHT + (ui.events ? EVENTS_LANE_HEIGHT : 0);
}
/** The render-cache strip at the foot of the ruler (P4): its height and its two colours. */
export const RENDER_STRIP_HEIGHT = 3;
export const RENDER_CACHE_HIT = '#1c7ed6';
export const RENDER_CACHE_MISS = '#e03131';
export const DEFAULT_TRACK_HEIGHT = 56;
export const COLLAPSED_TRACK_HEIGHT = 20;

export interface PaintStats {
  /** Items whose rectangles were actually painted. */
  paintedItems: number;
  /** Items visited (binary search + scan) — bounded by the viewport, never the document. */
  visitedItems: number;
  tracks: number;
}

export function frameToX(frame: number, viewport: Viewport): number {
  return (frame - viewport.scrollFrame) * viewport.pixelsPerFrame;
}

export function xToFrame(x: number, viewport: Viewport): number {
  return Math.round(x / viewport.pixelsPerFrame + viewport.scrollFrame);
}

export function visibleFrameRange(viewport: Viewport): { from: number; to: number } {
  const from = Math.floor(viewport.scrollFrame);
  const to = Math.ceil(viewport.scrollFrame + viewport.width / viewport.pixelsPerFrame);
  return { from, to };
}

/** The track row geometry: `[y, height]` per track, after the ruler and marker lane. */
export function trackRows(doc: EditTimeline, ui: PaintUi): { track: Track; y: number; height: number }[] {
  let y = headerHeight(ui);
  const rows: { track: Track; y: number; height: number }[] = [];
  for (const track of doc.tracks) {
    const flags = ui.trackFlags(track);
    const height = flags.collapsed ? COLLAPSED_TRACK_HEIGHT : Math.max(COLLAPSED_TRACK_HEIGHT, Math.round(track.height) || DEFAULT_TRACK_HEIGHT);
    rows.push({ track, y, height });
    y += height;
  }
  return rows;
}

export function totalTimelineHeight(doc: EditTimeline, ui: PaintUi): number {
  const rows = trackRows(doc, ui);
  const last = rows[rows.length - 1];
  return last ? last.y + last.height : headerHeight(ui);
}

/** First item index whose `recordOut` is after `from` (items are sorted by recordIn and non-overlapping). */
export function firstVisibleIndex(items: readonly Item[], from: number): number {
  let lo = 0;
  let hi = items.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (items[mid].recordOut <= from) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

/** A tick spacing (in frames) that keeps labels ≥ ~90 px apart at the current zoom. */
export function rulerStepFrames(viewport: Viewport, fps: number): number {
  const minPx = 90;
  const candidates = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600].map((s) => Math.max(1, Math.round((s / 30) * fps)));
  for (const step of candidates) {
    if (step * viewport.pixelsPerFrame >= minPx) return step;
  }
  return candidates[candidates.length - 1] * 10;
}

export function paintTimeline(
  ctx: PaintContext,
  doc: EditTimeline,
  viewport: Viewport,
  ui: PaintUi,
  theme: PaintTheme = DARK_THEME,
): PaintStats {
  const dpr = viewport.devicePixelRatio || 1;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, viewport.width, viewport.height);
  ctx.fillStyle = theme.background;
  ctx.fillRect(0, 0, viewport.width, viewport.height);

  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const { from, to } = visibleFrameRange(viewport);
  const stats: PaintStats = { paintedItems: 0, visitedItems: 0, tracks: 0 };

  // ── ruler ─────────────────────────────────────────────────────────────────
  ctx.fillStyle = theme.ruler;
  ctx.fillRect(0, 0, viewport.width, RULER_HEIGHT);
  const step = rulerStepFrames(viewport, fps);
  const firstTick = Math.floor(from / step) * step;
  ctx.font = '12px ui-monospace, SFMono-Regular, Menlo, monospace';
  ctx.textAlign = 'left';
  ctx.textBaseline = 'top';
  ctx.strokeStyle = theme.tick;
  ctx.lineWidth = 1;
  for (let f = firstTick; f <= to; f += step) {
    // The phone strip centres the playhead, so the view can start before frame 0: no ticks there.
    if (f < 0) continue;
    const x = Math.round(frameToX(f, viewport)) + 0.5;
    ctx.beginPath();
    ctx.moveTo(x, RULER_HEIGHT - 8);
    ctx.lineTo(x, RULER_HEIGHT);
    ctx.stroke();
    ctx.fillStyle = theme.rulerText;
    ctx.fillText(framesToTimecode(f, doc.settings.rate), x + 3, 4);
    // Vertical guide through the lanes.
    ctx.globalAlpha = 0.35;
    ctx.beginPath();
    ctx.moveTo(x, RULER_HEIGHT);
    ctx.lineTo(x, viewport.height);
    ctx.stroke();
    ctx.globalAlpha = 1;
  }

  // ── beat grid (T-008): bar lines through the lanes, every beat ticked on the ruler foot ────────
  if (ui.beatGrid && ui.beatGrid.length > 0) {
    ctx.strokeStyle = theme.beat;
    ctx.lineWidth = 1;
    for (const beat of ui.beatGrid) {
      if (beat.frame < from - 1 || beat.frame > to + 1) continue;
      const x = Math.round(frameToX(beat.frame, viewport));
      if (x < -1 || x > viewport.width + 1) continue;
      // Every beat: a short tick at the foot of the ruler.
      ctx.globalAlpha = beat.bar ? 0.9 : 0.6;
      ctx.beginPath();
      ctx.moveTo(x, RULER_HEIGHT - (beat.bar ? 10 : 6));
      ctx.lineTo(x, RULER_HEIGHT);
      ctx.stroke();
      // Bars only: the line is stronger than the beats and runs through every lane.
      if (beat.bar) {
        ctx.globalAlpha = 0.5;
        ctx.beginPath();
        ctx.moveTo(x, RULER_HEIGHT);
        ctx.lineTo(x, viewport.height);
        ctx.stroke();
      }
    }
    ctx.globalAlpha = 1;
  }

  // ── render cache strip (P4): the foot of the ruler ─────────────────────────
  if (ui.renderRegions && ui.renderRegions.length > 0) {
    for (const region of ui.renderRegions) {
      if (region.endFrame < from || region.startFrame > to) continue;
      const x0 = Math.max(0, frameToX(region.startFrame, viewport));
      const x1 = Math.min(viewport.width, frameToX(region.endFrame, viewport));
      if (x1 <= x0) continue;
      ctx.fillStyle = region.cached ? RENDER_CACHE_HIT : RENDER_CACHE_MISS;
      ctx.fillRect(x0, RULER_HEIGHT - RENDER_STRIP_HEIGHT, x1 - x0, RENDER_STRIP_HEIGHT);
    }
  }

  // ── marker lane ───────────────────────────────────────────────────────────
  ctx.fillStyle = theme.laneAlt;
  ctx.fillRect(0, RULER_HEIGHT, viewport.width, MARKER_LANE_HEIGHT);
  for (const marker of doc.markers) {
    if (marker.frame < from - 1 || marker.frame > to + 1) continue;
    const x = frameToX(marker.frame, viewport);
    ctx.fillStyle = marker.color && /^#[0-9a-fA-F]{6}$/.test(marker.color) ? marker.color : theme.marker;
    const width = marker.durationFrames ? Math.max(6, marker.durationFrames * viewport.pixelsPerFrame) : 6;
    ctx.fillRect(x - 3, RULER_HEIGHT + 2, width, MARKER_LANE_HEIGHT - 4);
  }
  ctx.fillStyle = theme.rationale;
  for (const frame of ui.rationaleFrames) {
    if (frame < from - 1 || frame > to + 1) continue;
    const x = frameToX(frame, viewport);
    ctx.fillRect(x - 2, RULER_HEIGHT + MARKER_LANE_HEIGHT - 5, 4, 4);
  }

  // ── events lane: under the marker lane, derived and read-only ──────────────────────────────
  if (ui.events) paintEventsLane(ctx, viewport, RULER_HEIGHT + MARKER_LANE_HEIGHT, ui.events, theme, from, to);

  // ── tracks ────────────────────────────────────────────────────────────────
  const rows = trackRows(doc, ui);
  rows.forEach(({ track, y, height }, trackIndex) => {
    stats.tracks += 1;
    const flags = ui.trackFlags(track);
    ctx.fillStyle = !flags.enabled || (track.kind === 'audio' && flags.mute) ? theme.laneDisabled : trackIndex % 2 === 0 ? theme.background : theme.laneAlt;
    ctx.fillRect(0, y, viewport.width, height);
    if (flags.locked) {
      ctx.fillStyle = theme.locked;
      ctx.fillRect(0, y, viewport.width, height);
    }
    ctx.strokeStyle = theme.tick;
    ctx.beginPath();
    ctx.moveTo(0, y + height - 0.5);
    ctx.lineTo(viewport.width, y + height - 0.5);
    ctx.stroke();

    if (flags.collapsed) return;

    const items = track.items;
    const start = firstVisibleIndex(items, from);
    for (let i = start; i < items.length; i++) {
      const item = items[i];
      stats.visitedItems += 1;
      if (item.recordIn > to) break;
      const x0 = frameToX(item.recordIn, viewport);
      const x1 = frameToX(item.recordOut, viewport);
      const w = Math.max(1, x1 - x0);
      const enabled = (item as { enabled?: boolean }).enabled !== false;

      // A disabled clip, or one on a muted or disabled track, is painted in the muted fill rather than faded:
      // fading dropped its name below AA against the lane.
      const muted = !enabled || !flags.enabled || (track.kind === 'audio' && flags.mute);
      ctx.fillStyle = muted ? theme.mutedClip : item.type === 'mediaClip' && track.kind === 'audio' ? theme.audioClip : (theme.item[item.type] ?? theme.item.mediaClip);
      fillBody(ctx, x0 + 0.5, y + 4, Math.max(1, w - 1), height - 8);
      stats.paintedItems += 1;
      paintClipContent(ctx, {
        item, trackKind: track.kind, x0, y, w, height, fps, pixelsPerFrame: viewport.pixelsPerFrame, viewportWidth: viewport.width,
        muted, linked: ui.isLinked ? ui.isLinked(item) : false, job: ui.jobOf?.(item.id) ?? (ui.busyClipIds?.has(item.id) === true ? RUNNING_JOB : undefined), nowMs: ui.nowMs, badges: ui.badgesOf?.(item), art: ui.clipArt, view: ui.view ?? { waveforms: true, thumbnails: true, names: true }, theme, labels: ui.labels,
      });

      const parity = ui.parityOf(item.id);
      if (parity !== 'faithful') {
        ctx.fillStyle = parity === 'renderRequired' ? theme.renderRequired : theme.approximate;
        ctx.fillRect(x0, y + 4, w, 3);
      }

      if (ui.selection.has(item.id)) {
        ctx.strokeStyle = theme.selected;
        ctx.lineWidth = 2;
        ctx.strokeRect(x0 + 1, y + 5, Math.max(1, w - 2), height - 10);
        ctx.lineWidth = 1;
      }

      ctx.globalAlpha = 1;
    }
  });

  // ── ghost diff (P8) ───────────────────────────────────────────────────────
  if (ui.ghosts && ui.ghosts.length > 0) paintGhosts(ctx, rows, viewport, ui.ghosts, ui.labels?.yours);

  // ── in/out shading ────────────────────────────────────────────────────────
  // A translucent tint over the ruler and every lane, painted AFTER them: painted first, the
  // opaque ruler, marker lane and track rows covered it completely (d7 N1). Marks set in the wrong
  // order shade the range they span.
  if (ui.inFrame !== null || ui.outFrame !== null) {
    const a = ui.inFrame === null ? null : frameToX(ui.inFrame, viewport);
    const b = ui.outFrame === null ? null : frameToX(ui.outFrame, viewport);
    const lo = a !== null && b !== null ? Math.min(a, b) : a ?? 0;
    const hi = a !== null && b !== null ? Math.max(a, b) : b ?? viewport.width;
    const x0 = Math.max(0, lo);
    const x1 = Math.min(viewport.width, hi);
    if (x1 > x0) {
      ctx.fillStyle = theme.inOut;
      ctx.fillRect(x0, 0, x1 - x0, viewport.height);
    }
    ctx.strokeStyle = theme.inOutEdge;
    ctx.lineWidth = 1;
    for (const edge of [a, b]) {
      if (edge === null || edge < 0 || edge > viewport.width) continue;
      const x = Math.round(edge) + 0.5;
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, viewport.height);
      ctx.stroke();
    }
  }

  // ── snap guide ────────────────────────────────────────────────────────────
  if (ui.snapGuideFrame !== null && ui.snapGuideFrame !== undefined) {
    const gx = Math.round(frameToX(ui.snapGuideFrame, viewport)) + 0.5;
    if (gx >= 0 && gx <= viewport.width) {
      ctx.strokeStyle = theme.snapGuide;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(gx, 0);
      ctx.lineTo(gx, viewport.height);
      ctx.stroke();
      if (ui.snapGuideLabel) {
        ctx.font = '12px system-ui, sans-serif';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        const measured = ctx.measureText ? ctx.measureText(ui.snapGuideLabel).width : ui.snapGuideLabel.length * 7;
        const width = Math.min(viewport.width - 8, measured + 10);
        const left = gx + 4 + width <= viewport.width ? gx + 4 : Math.max(0, gx - 4 - width);
        const top = viewport.height > 60 ? 28 : 2;
        ctx.fillStyle = theme.background;
        ctx.fillRect(left, top, width, 18);
        ctx.strokeStyle = theme.snapGuide;
        ctx.strokeRect(left + 0.5, top + 0.5, width - 1, 17);
        ctx.fillStyle = theme.snapLabelText;
        ctx.fillText(ui.snapGuideLabel, left + 5, top + 3, width - 10);
      }
    }
  }

  // ── playhead ──────────────────────────────────────────────────────────────
  const px = Math.round(frameToX(ui.playheadFrame, viewport)) + 0.5;
  if (px >= -1 && px <= viewport.width + 1) {
    ctx.strokeStyle = theme.playhead;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(px, 0);
    ctx.lineTo(px, viewport.height);
    ctx.stroke();
    ctx.fillStyle = theme.playhead;
    ctx.fillRect(px - 5, 0, 10, 8);
  }

  return stats;
}

/** Hit test: the item under (x, y), or null. Uses the same geometry the painter does. */
export function hitTestItem(
  doc: EditTimeline,
  viewport: Viewport,
  ui: PaintUi,
  x: number,
  y: number,
): { item: Item; track: Track } | null {
  if (y < headerHeight(ui)) return null;
  const frame = x / viewport.pixelsPerFrame + viewport.scrollFrame;
  for (const row of trackRows(doc, ui)) {
    if (y < row.y || y >= row.y + row.height) continue;
    for (const item of row.track.items) {
      if (item.recordIn <= frame && frame < item.recordOut) return { item, track: row.track };
    }
    return null;
  }
  return null;
}

const RUNNING_JOB: ClipJobPaint = { state: 'running', label: '', jobKind: '', markId: '' };

/** The job chip under a point ("Ready to review", "Did not finish", the running label), or null. */
export function hitTestJobChip(
  doc: EditTimeline,
  viewport: Viewport,
  ui: PaintUi,
  x: number,
  y: number,
): { item: Item; track: Track; job: ClipJobPaint; rect: JobChipRect } | null {
  if (!ui.jobOf) return null;
  const hit = hitTestItem(doc, viewport, ui, x, y);
  if (!hit) return null;
  const job = ui.jobOf(hit.item.id);
  if (!job) return null;
  const row = trackRows(doc, ui).find((r) => r.track.id === hit.track.id);
  if (!row) return null;
  const x0 = frameToX(hit.item.recordIn, viewport);
  const w = Math.max(1, frameToX(hit.item.recordOut, viewport) - x0);
  const rect = jobChipRect(job, ui.labels ?? FALLBACK_PAINT_LABELS, x0, w, row.y + 4, row.height - 8);
  return rect && x >= rect.x && x < rect.x + rect.w && y >= rect.y && y < rect.y + rect.h ? { item: hit.item, track: hit.track, job, rect } : null;
}

/** The adjustment badge under a point (clip, track and the badge's rect), or null. */
export function hitTestBadge(
  doc: EditTimeline,
  viewport: Viewport,
  ui: PaintUi,
  x: number,
  y: number,
): { item: Item; track: Track; rect: BadgeRect } | null {
  if (!ui.badgesOf) return null;
  const hit = hitTestItem(doc, viewport, ui, x, y);
  if (!hit) return null;
  const badges = ui.badgesOf(hit.item);
  if (badges.length === 0) return null;
  const row = trackRows(doc, ui).find((r) => r.track.id === hit.track.id);
  if (!row) return null;
  const x0 = frameToX(hit.item.recordIn, viewport);
  const w = Math.max(1, frameToX(hit.item.recordOut, viewport) - x0);
  const rect = badgeAt(badgeRectsFor(badges, x0, w, row.y + 4, row.height - 8), x, y);
  return rect ? { item: hit.item, track: hit.track, rect } : null;
}

/** Which track row a y coordinate is in, or null for the ruler/marker lane. */
export function trackAtY(doc: EditTimeline, ui: PaintUi, y: number): Track | null {
  for (const row of trackRows(doc, ui)) {
    if (y >= row.y && y < row.y + row.height) return row.track;
  }
  return null;
}
