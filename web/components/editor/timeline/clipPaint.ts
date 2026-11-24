/**
 * Clip content for the canvas timeline (R03.4 / VF11): the paint-model functions (pure, unit
 * tested) and the one draw routine `paint.ts` calls for each visible clip. Everything here is
 * Canvas-based and bounded by what is on screen: a clip wider than the viewport only gets columns
 * and thumbnails for the part you can see.
 *
 * Layout of a clip body (top to bottom): a label band (kind badge, name, duration) painted in the
 * kind colour so the text keeps its AA contrast, then the art area: a poster or filmstrip for
 * pictures, the waveform for sound, both for a video clip with sound (waveform on a dark scrim over
 * the lower part of the thumbnails). Trimmed clips get a hatched edge on each side that still has
 * unused source behind it. Offline clips are hatched and say so.
 */

import type { Item } from '@/lib/editing/types';
import type { PaintContext, PaintTheme } from './paint';
import type { ClipBadgePaint } from '@/lib/editing/display/clip-badges';
import type { ClipJobPaint } from '@/lib/editing/background/job-marks';
import { paintClipJob } from './clipJobPaint';
import { badgeRectsFor, paintClipBadges } from './clipBadgePaint';
import { FALLBACK_PAINT_LABELS, type PaintLabels } from './paintLabels';

/** A drawable picture (an <img> or a canvas) the painter hands straight to `drawImage`. */
export type ClipImage = CanvasImageSource;

/** One thumbnail cell: the sheet image and the rectangle of the cell inside it. */
export interface ThumbCell {
  image: ClipImage;
  sx: number;
  sy: number;
  sw: number;
  sh: number;
}

/** Waveform peaks: channel-major `[channel][bucket]{min, max}` as int16 pairs, `bucketMs` per bucket. */
export interface PeakData {
  data: Int16Array;
  channels: number;
  bucketMs: number;
}

/** What the painter may ask for. All of it is cached and answers synchronously; null means "not (yet) available". */
export interface ClipArt {
  /** Seconds in the item's source media, when known. */
  sourceDurationSec(item: Item): number | null;
  /** The thumbnail nearest `sourceSec`, or null. */
  thumb(item: Item, sourceSec: number): ThumbCell | null;
  /** The aspect ratio (width / height) of the source's thumbnails, or null. */
  thumbAspect(item: Item): number | null;
  /** Peaks for the item's source, or null. */
  peaks(item: Item): PeakData | null;
  /** True when the source is known to be unavailable. */
  offline(item: Item): boolean;
  /** T-023: true once the source was looked up and its length is still unknown (the trim limit cannot be checked). */
  lengthUnchecked?(item: Item): boolean;
}

export interface ClipView {
  waveforms: boolean;
  thumbnails: boolean;
  names: boolean;
}

export type ClipKind = 'video' | 'audio' | 'title' | 'colour' | 'compound' | 'other';

/** Kind of a clip for colour and badge. A media clip on an audio track is audio. */
export function clipKindOf(item: Pick<Item, 'type'>, trackKind: 'video' | 'audio', labels: Pick<PaintLabels, 'compound'> = FALLBACK_PAINT_LABELS): { kind: ClipKind; badge: string } {
  switch (item.type) {
    case 'mediaClip':
      return trackKind === 'audio' ? { kind: 'audio', badge: 'A' } : { kind: 'video', badge: 'V' };
    case 'titleClip':
      return { kind: 'title', badge: 'T' };
    case 'generatorClip':
      return { kind: 'colour', badge: 'C' };
    case 'compoundClip':
    case 'multicamClip':
      return { kind: 'compound', badge: labels.compound };
    default:
      return { kind: 'other', badge: '' };
  }
}

/** Source seconds the clip plays, from its `sourceIn` and length, at the clip's speed. */
export function sourceSpanSec(item: { recordIn: number; recordOut: number; sourceIn?: number; playbackRate?: number }, fps: number): { startSec: number; endSec: number } {
  const rate = item.playbackRate && item.playbackRate > 0 ? item.playbackRate : 1;
  const start = (item.sourceIn ?? 0) / fps;
  return { startSec: start, endSec: start + ((item.recordOut - item.recordIn) * rate) / fps };
}

/**
 * The unused source behind each edge of a trimmed clip, as pixels of timeline at the current zoom
 * (so a clip trimmed by 2 s shows twice the shading at twice the zoom). Zero when the clip reaches
 * that end of its source, or when the source length is unknown.
 */
export function trimHandles(
  item: { recordIn: number; recordOut: number; sourceIn?: number; playbackRate?: number },
  fps: number,
  sourceDurationSec: number | null,
  pixelsPerFrame: number,
): { left: number; right: number } {
  if (sourceDurationSec === null || !(sourceDurationSec > 0)) return { left: 0, right: 0 };
  const { startSec, endSec } = sourceSpanSec(item, fps);
  const rate = item.playbackRate && item.playbackRate > 0 ? item.playbackRate : 1;
  const leftFrames = Math.max(0, startSec * fps) / rate;
  // Half a frame of slack: probes round the duration.
  const rightFrames = Math.max(0, (sourceDurationSec - endSec) * fps - 0.5) / rate;
  return { left: leftFrames * pixelsPerFrame, right: rightFrames * pixelsPerFrame };
}

/** Pixel width of a trim-shade band for `handlePx` of unused source: none at 0, otherwise 4..14 px. */
export function shadeWidth(handlePx: number): number {
  if (!(handlePx > 0.5)) return 0;
  return Math.min(14, Math.max(4, Math.round(handlePx)));
}

/** "3.2s", "12s", "1:05" for a clip length. */
export function formatClipDuration(frames: number, fps: number): string {
  const sec = Math.max(0, frames) / fps;
  if (sec < 10) return `${(Math.round(sec * 10) / 10).toFixed(1)}s`;
  if (sec < 60) return `${Math.round(sec)}s`;
  const m = Math.floor(sec / 60);
  const s = Math.round(sec - m * 60);
  return s === 60 ? `${m + 1}:00` : `${m}:${String(s).padStart(2, '0')}`;
}

/** The text width of `text` at 12 px when no real measurement is available. */
export const approxTextWidth = (text: string): number => text.length * 6.6;

/** Truncates `text` with an ellipsis so it fits `maxPx`; '' when not even a character plus the ellipsis fits. */
export function fitText(text: string, maxPx: number, measure: (s: string) => number = approxTextWidth): string {
  if (maxPx <= 0) return '';
  if (measure(text) <= maxPx) return text;
  let lo = 0;
  let hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (measure(`${text.slice(0, mid)}…`) <= maxPx) lo = mid;
    else hi = mid - 1;
  }
  return lo === 0 ? '' : `${text.slice(0, lo)}…`;
}

export interface ClipLabel {
  badge: string;
  name: string;
  duration: string;
}

/**
 * What the label band shows for a clip `widthPx` wide. The font is always 12 px, so a narrow clip
 * drops things instead of shrinking them: duration first, then the name, then the badge.
 */
export function clipLabel(
  input: { name: string; badge: string; durationText: string; widthPx: number; showName: boolean },
  measure: (s: string) => number = approxTextWidth,
): ClipLabel {
  const pad = 10;
  let room = input.widthPx - pad;
  const out: ClipLabel = { badge: '', name: '', duration: '' };
  if (room < 12) return out;
  const badgeW = input.badge ? measure(input.badge) + 6 : 0;
  if (input.badge && room >= badgeW + (input.showName ? 30 : 0)) {
    out.badge = input.badge;
    room -= badgeW;
  }
  const durW = measure(input.durationText) + 8;
  const nameFull = input.showName ? measure(input.name) : 0;
  if (input.durationText && room >= nameFull + durW + (input.showName ? 0 : 0) && room >= durW + 20) {
    out.duration = input.durationText;
    room -= durW;
  }
  if (input.showName) out.name = fitText(input.name, room, measure);
  return out;
}

/** Number of filmstrip cells for a body `widthPx` wide when each cell is `cellW` wide. 0 means "poster only". */
export function filmstripCount(widthPx: number, cellW: number): number {
  if (!(cellW > 0) || widthPx < cellW * 2) return 0;
  return Math.ceil(widthPx / cellW);
}

/** The source time cell `i` of `count` shows, spread evenly over the clip's source span. */
export function filmstripCellSec(i: number, count: number, startSec: number, endSec: number): number {
  if (count <= 1) return startSec;
  return startSec + ((endSec - startSec) * (i + 0.5)) / count;
}

/**
 * Min/max (normalised to -1..1, loudest channel) for each of `columns` columns covering source
 * time [startSec, endSec). Returns `2 * columns` numbers `[min0, max0, min1, max1 ...]`; a column
 * outside the data is 0,0.
 */
export function peakColumns(peaks: PeakData, startSec: number, endSec: number, columns: number): Float32Array {
  const out = new Float32Array(Math.max(0, columns) * 2);
  if (columns <= 0 || !(endSec > startSec) || peaks.channels <= 0 || peaks.bucketMs <= 0) return out;
  const perChannel = Math.floor(peaks.data.length / 2 / peaks.channels);
  const bucketSec = peaks.bucketMs / 1000;
  const span = (endSec - startSec) / columns;
  for (let c = 0; c < columns; c++) {
    const b0 = Math.max(0, Math.floor((startSec + c * span) / bucketSec));
    const b1 = Math.min(perChannel - 1, Math.max(b0, Math.ceil((startSec + (c + 1) * span) / bucketSec) - 1));
    if (b0 > perChannel - 1) continue;
    let lo = 0;
    let hi = 0;
    for (let ch = 0; ch < peaks.channels; ch++) {
      const base = ch * perChannel;
      for (let b = b0; b <= b1; b++) {
        const mn = peaks.data[(base + b) * 2];
        const mx = peaks.data[(base + b) * 2 + 1];
        if (mn < lo) lo = mn;
        if (mx > hi) hi = mx;
      }
    }
    out[c * 2] = Math.max(-1, lo / 32768);
    out[c * 2 + 1] = Math.min(1, hi / 32767);
  }
  return out;
}

/** Peaks of a row from the derivatives list: which LOD (20 / 100 / 1000 ms per bucket) a raw object is, from its size. */
export const PEAK_LODS_MS = [20, 100, 1000] as const;

/** The bucket length of a peaks object of `sizeBytes` for a source of `durationSec`, or null when it fits no LOD. */
export function peaksBucketMs(sizeBytes: number, channels: number, durationSec: number | null): number | null {
  const buckets = sizeBytes / (4 * channels);
  if (!(buckets > 0)) return null;
  if (durationSec === null || !(durationSec > 0)) return null;
  let best: number | null = null;
  let bestErr = Infinity;
  for (const ms of PEAK_LODS_MS) {
    const err = Math.abs((buckets * ms) / 1000 - durationSec);
    if (err < bestErr) {
      best = ms;
      bestErr = err;
    }
  }
  return best;
}

/**
 * The LOD (ms per bucket) of each peaks row when the source's duration is unknown (an audio-only file
 * has no filmstrip): the three LODs hold 20 : 100 : 1000 ms buckets, so their sizes are about 50 : 10 : 1.
 * Three rows rank directly; two rows are told apart by their size ratio; one row cannot be told (null).
 */
export function peakLodsBySize(sizes: readonly number[]): (number | null)[] {
  const out: (number | null)[] = sizes.map(() => null);
  const order = sizes.map((size, i) => ({ size, i })).filter((e) => e.size > 0).sort((a, b) => b.size - a.size);
  if (order.length === 3) {
    order.forEach((e, rank) => (out[e.i] = PEAK_LODS_MS[rank]));
  } else if (order.length === 2) {
    const ratio = order[0].size / order[1].size;
    const pair: readonly [number, number] | null = ratio < 7 ? [20, 100] : ratio < 22 ? [100, 1000] : ratio < 90 ? [20, 1000] : null;
    if (pair) {
      out[order[0].i] = pair[0];
      out[order[1].i] = pair[1];
    }
  }
  return out;
}

/** The label band's height, and the least body height that gets an art area under it. */
export const LABEL_BAND = 18;
export const MIN_ART_HEIGHT = 10;

function hatchBand(ctx: PaintContext, x: number, y: number, w: number, h: number, color: string): void {
  ctx.save();
  ctx.beginPath();
  ctx.rect(x, y, w, h);
  ctx.clip();
  ctx.strokeStyle = color;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let d = -h; d < w; d += 5) {
    ctx.moveTo(x + d, y + h);
    ctx.lineTo(x + d + h, y);
  }
  ctx.stroke();
  ctx.restore();
}

export interface ClipPaintArgs {
  item: Item;
  trackKind: 'video' | 'audio';
  x0: number;
  y: number;
  w: number;
  height: number;
  fps: number;
  pixelsPerFrame: number;
  viewportWidth: number;
  muted: boolean;
  linked: boolean;
  /** A background job is working on this clip, or has a result waiting: dim body, foot strip, state chip. */
  job?: ClipJobPaint;
  /** The clock for the moving stripe of a job with no progress; undefined = static hatching (reduced motion). */
  nowMs?: number;
  /** The adjustment badges to draw under the label band (absent or empty: none). */
  badges?: readonly ClipBadgePaint[];
  art?: ClipArt;
  view: ClipView;
  theme: PaintTheme;
  /** Words painted on the canvas; absent = the English fallback. */
  labels?: PaintLabels;
}

/** Draws a clip's content over its already-filled body. Returns the art kinds drawn (for tests / stats). */
export function paintClipContent(ctx: PaintContext, a: ClipPaintArgs): { thumbs: number; waveform: boolean; offline: boolean; unchecked: boolean } {
  const result = { thumbs: 0, waveform: false, offline: false, unchecked: false };
  const { item, x0, y, w, theme } = a;
  const bodyY = y + 4;
  const bodyH = a.height - 8;
  const words = a.labels ?? FALLBACK_PAINT_LABELS;
  const kind = clipKindOf(item, a.trackKind, words);
  if (w <= 6) return result;

  ctx.save();
  ctx.beginPath();
  ctx.rect(x0 + 0.5, bodyY, Math.max(1, w - 1), bodyH);
  ctx.clip();

  const durationFrames = item.recordOut - item.recordIn;
  const measure = (s: string): number => {
    const w = ctx.measureText?.(s)?.width;
    return typeof w === 'number' && Number.isFinite(w) ? w : approxTextWidth(s);
  };
  ctx.font = '12px system-ui, sans-serif';
  const label = clipLabel({ name: item.name || item.type, badge: kind.badge, durationText: formatClipDuration(durationFrames, a.fps), widthPx: w, showName: a.view.names }, measure);

  // Art area under the label band.
  const artY = bodyY + LABEL_BAND;
  const artH = bodyH - LABEL_BAND;
  const offline = a.art?.offline(item) === true;
  const art = a.art;
  const mediaLike = item.type === 'mediaClip';
  if (art && mediaLike && !offline && artH >= MIN_ART_HEIGHT) {
    const { startSec, endSec } = sourceSpanSec(item, a.fps);
    // Only the on-screen slice of a long clip gets drawn.
    const visL = Math.max(x0, 0);
    const visR = Math.min(x0 + w, a.viewportWidth);
    if (a.trackKind === 'video' && a.view.thumbnails && ctx.drawImage) {
      const aspect = art.thumbAspect(item) ?? 16 / 9;
      const cellW = Math.max(8, artH * aspect);
      const count = filmstripCount(w, cellW);
      if (count === 0) {
        const cell = art.thumb(item, startSec);
        if (cell) {
          ctx.drawImage(cell.image, cell.sx, cell.sy, cell.sw, cell.sh, x0 + 0.5, artY, Math.min(cellW, w - 1), artH);
          result.thumbs += 1;
        }
      } else {
        const cw = w / count;
        const first = Math.max(0, Math.floor((visL - x0) / cw));
        const last = Math.min(count - 1, Math.floor((visR - x0) / cw));
        for (let i = first; i <= last; i++) {
          const cell = art.thumb(item, filmstripCellSec(i, count, startSec, endSec));
          if (!cell) continue;
          ctx.drawImage(cell.image, cell.sx, cell.sy, cell.sw, cell.sh, x0 + i * cw, artY, Math.ceil(cw), artH);
          result.thumbs += 1;
        }
      }
    }
    const peaks = a.view.waveforms ? art.peaks(item) : null;
    if (peaks && visR > visL) {
      const hasPictureBehind = result.thumbs > 0;
      const waveH = hasPictureBehind ? Math.max(MIN_ART_HEIGHT, Math.round(artH * 0.45)) : artH;
      const waveY = artY + artH - waveH;
      if (hasPictureBehind) {
        ctx.globalAlpha = 0.6;
        ctx.fillStyle = '#000000';
        ctx.fillRect(x0 + 0.5, waveY, w - 1, waveH);
        ctx.globalAlpha = 1;
      }
      const cols = Math.max(1, Math.floor(visR - visL));
      const spanSec = ((endSec - startSec) * (visR - visL)) / w;
      const t0 = startSec + ((endSec - startSec) * (visL - x0)) / w;
      const mm = peakColumns(peaks, t0, t0 + spanSec, cols);
      const mid = waveY + waveH / 2;
      const half = waveH / 2 - 1;
      ctx.fillStyle = theme.itemText;
      ctx.globalAlpha = 0.85;
      for (let c = 0; c < cols; c++) {
        const top = mid - mm[c * 2 + 1] * half;
        const bottom = mid - mm[c * 2] * half;
        ctx.fillRect(Math.floor(visL) + c, top, 1, Math.max(1, bottom - top));
      }
      ctx.globalAlpha = 1;
      result.waveform = true;
    }
  }

  // Label band: solid kind colour so the text keeps its contrast whatever is under it.
  if (kind.kind !== 'other' || a.view.names) {
    ctx.fillStyle = a.muted ? theme.mutedClip : item.type === 'mediaClip' && a.trackKind === 'audio' ? theme.audioClip : (theme.item[item.type] ?? theme.item.mediaClip);
    ctx.fillRect(x0 + 0.5, bodyY, Math.max(1, w - 1), Math.min(LABEL_BAND, bodyH));
  }
  ctx.fillStyle = theme.itemText;
  ctx.textAlign = 'left';
  ctx.textBaseline = 'top';
  // 6 px more than the edge inset so the kind prefix ("V") never sits under the trim-handle dot.
  let tx = x0 + 11;
  if (label.badge) {
    ctx.font = 'bold 12px system-ui, sans-serif';
    ctx.fillText(label.badge, tx, bodyY + 3);
    tx += measure(label.badge) + 6;
    ctx.font = '12px system-ui, sans-serif';
  }
  if (label.name) ctx.fillText(label.name, tx, bodyY + 3);
  if (label.duration) {
    ctx.textAlign = 'right';
    ctx.fillText(label.duration, x0 + w - 5, bodyY + 3);
    ctx.textAlign = 'left';
  }

  // Trimmed regions: a hatched edge on each side that still has unused source behind it.
  if (art && mediaLike && !offline) {
    const handles = trimHandles(item, a.fps, art.sourceDurationSec(item), a.pixelsPerFrame);
    const l = shadeWidth(handles.left);
    const r = shadeWidth(handles.right);
    const shadeY = artH >= MIN_ART_HEIGHT ? artY : bodyY;
    const shadeH = artH >= MIN_ART_HEIGHT ? artH : bodyH;
    if (l > 0 && w > l * 2) hatchBand(ctx, x0 + 0.5, shadeY, l, shadeH, theme.itemText);
    if (r > 0 && w > r * 2) hatchBand(ctx, x0 + w - 0.5 - r, shadeY, r, shadeH, theme.itemText);
  }

  if (offline) {
    result.offline = true;
    hatchBand(ctx, x0 + 0.5, artY, Math.max(1, w - 1), Math.max(0, artH), theme.renderRequired);
    if (w > 70 && artH >= 12) {
      ctx.fillStyle = theme.itemText;
      ctx.font = '12px system-ui, sans-serif';
      ctx.fillText(words.offline, x0 + 5, artY + Math.max(0, (artH - 12) / 2));
    }
  }

  // T-023: a source with no known length is never refused a trim, but the clip says its end is not checked.
  if (art && mediaLike && !offline && w > 130 && bodyH >= 40 && art.lengthUnchecked?.(item) === true) {
    result.unchecked = true;
    const noteY = bodyY + bodyH - 18;
    ctx.globalAlpha = 0.6;
    ctx.fillStyle = '#000000';
    ctx.fillRect(x0 + 0.5, noteY - 1, Math.min(w - 1, 132), 16);
    ctx.globalAlpha = 1;
    ctx.fillStyle = theme.itemText;
    ctx.font = '12px system-ui, sans-serif';
    ctx.textAlign = 'left';
    ctx.fillText(words.lengthUnchecked, x0 + 5, noteY);
  }

  // Linked clips: a stripe along the foot.
  if (a.linked) {
    ctx.fillStyle = theme.linked;
    ctx.fillRect(x0 + 0.5, bodyY + bodyH - 3, Math.max(1, w - 1), 3);
  }

  // Fades are not badges: a thin diagonal in the clip's corner shows each ramp (in: rises from the bottom-left, out: falls to the bottom-right).
  if (item.type === 'mediaClip') {
    const fadeIn = Math.min(item.fadeInFrames ?? 0, durationFrames) * a.pixelsPerFrame;
    const fadeOut = Math.min(item.fadeOutFrames ?? 0, durationFrames) * a.pixelsPerFrame;
    if (fadeIn > 0 || fadeOut > 0) {
      const rampW = (px: number) => Math.min(px, (w - 1) / 2);
      ctx.save();
      ctx.strokeStyle = theme.itemText;
      ctx.globalAlpha = 0.85;
      ctx.lineWidth = 1;
      ctx.beginPath();
      if (fadeIn > 0) {
        ctx.moveTo(x0 + 0.5, bodyY + bodyH - 0.5);
        ctx.lineTo(x0 + 0.5 + rampW(fadeIn), bodyY + 0.5);
      }
      if (fadeOut > 0) {
        ctx.moveTo(x0 + w - 0.5 - rampW(fadeOut), bodyY + 0.5);
        ctx.lineTo(x0 + w - 0.5, bodyY + bodyH - 0.5);
      }
      ctx.stroke();
      ctx.restore();
    }
  }

  // Adjustment badges: a right-aligned row under the label band.
  if (a.badges && a.badges.length > 0) paintClipBadges(ctx, badgeRectsFor(a.badges, x0, w, bodyY, bodyH), a.badges, theme);

  // A background job: the body dims, a strip along the foot shows progress, a chip names the state.
  if (a.job) paintClipJob(ctx, a.job, words, x0, w, bodyY, bodyH, a.nowMs, theme);

  ctx.restore();
  return result;
}
