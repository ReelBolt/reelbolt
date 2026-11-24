/**
 * The picture's size as the Canvas tab offers it: a SHAPE (an aspect ratio) and a SIZE (a resolution
 * tier named by its short side, the way video people say "1080p"), or a custom width and height.
 *
 * Pure: no React, no DOM. The server stores only the two numbers (`canvasWidth`/`canvasHeight`, each
 * {@link CANVAS_MIN}..{@link CANVAS_MAX}); the shape and the tier are re-derived from them on every
 * render, so nothing here is persisted and an undo of a size change needs no extra bookkeeping.
 */

import { CANVAS_MAX, CANVAS_MIN } from './model';

export interface AspectPreset {
  id: string;
  /** The ratio as people write it, e.g. "16:9". */
  label: string;
  /** Where that shape is usually seen, in a word or two. */
  hint: string;
  /** Width over height, as the two whole numbers of the label. */
  w: number;
  h: number;
}

/** The shapes the Canvas tab offers, most common first. */
export const ASPECT_PRESETS: readonly AspectPreset[] = [
  { id: '16:9', label: '16:9', hint: 'YouTube', w: 16, h: 9 },
  { id: '9:16', label: '9:16', hint: 'Shorts, Reels', w: 9, h: 16 },
  { id: '1:1', label: '1:1', hint: 'Square', w: 1, h: 1 },
  { id: '4:5', label: '4:5', hint: 'Instagram', w: 4, h: 5 },
  { id: '4:3', label: '4:3', hint: 'Classic', w: 4, h: 3 },
  { id: '3:2', label: '3:2', hint: 'Photo', w: 3, h: 2 },
  { id: '2:3', label: '2:3', hint: 'Poster', w: 2, h: 3 },
  { id: '21:9', label: '21:9', hint: 'Cinema', w: 21, h: 9 },
];

export interface ResolutionTier {
  id: string;
  label: string;
  /** The SHORT side in pixels: 1080 is 1920 × 1080 for 16:9 and 1080 × 1920 for 9:16. */
  shortSide: number;
}

export const RESOLUTION_TIERS: readonly ResolutionTier[] = [
  { id: '720', label: '720p', shortSide: 720 },
  { id: '1080', label: '1080p', shortSide: 1080 },
  { id: '1440', label: '1440p', shortSide: 1440 },
  { id: '2160', label: '4K', shortSide: 2160 },
];

/** The tier a new shape starts at when the current size matches no tier. */
export const DEFAULT_TIER = RESOLUTION_TIERS[0];

/** Rounds to an even number of pixels (video encoders and most social sites want even sides). */
const even = (n: number) => Math.max(2, Math.round(n / 2) * 2);

/**
 * The pixel size of a shape at a tier. The short side is the tier; the long side follows the ratio,
 * rounded to an even number. Never outside the server's limits: a side the ratio would push past
 * {@link CANVAS_MAX} is capped and the other side shrunk with it, so the shape is kept.
 */
export function sizeFor(aspect: Pick<AspectPreset, 'w' | 'h'>, tier: Pick<ResolutionTier, 'shortSide'>): {
  width: number;
  height: number;
} {
  const portrait = aspect.h > aspect.w;
  const ratio = Math.max(aspect.w, aspect.h) / Math.min(aspect.w, aspect.h);
  let short = tier.shortSide;
  let long = short * ratio;
  if (long > CANVAS_MAX) {
    long = CANVAS_MAX;
    short = long / ratio;
  }
  const shortPx = Math.max(CANVAS_MIN, even(short));
  const longPx = Math.min(CANVAS_MAX, Math.max(CANVAS_MIN, even(long)));
  return portrait ? { width: shortPx, height: longPx } : { width: longPx, height: shortPx };
}

/** How far a size may be from a shape's exact ratio and still count as that shape (rounding). */
const RATIO_TOLERANCE = 0.01;

/** The shape a size has, or null when it matches none of the presets. */
export function matchAspect(width: number, height: number): AspectPreset | null {
  if (!(width > 0) || !(height > 0)) return null;
  const ratio = width / height;
  for (const preset of ASPECT_PRESETS) {
    const target = preset.w / preset.h;
    if (Math.abs(ratio - target) / target <= RATIO_TOLERANCE) return preset;
  }
  return null;
}

/** The tier a size is at (its short side equals the tier's), or null. */
export function matchTier(width: number, height: number): ResolutionTier | null {
  const short = Math.min(width, height);
  const aspect = matchAspect(width, height);
  for (const tier of RESOLUTION_TIERS) {
    if (aspect) {
      const size = sizeFor(aspect, tier);
      if (size.width === width && size.height === height) return tier;
    } else if (short === tier.shortSide) {
      return tier;
    }
  }
  return null;
}

/** The most pixels a picture can have and still open on a phone (iOS Safari's canvas area limit). */
export const PHONE_SAFE_PIXELS = 16_777_216;

/** True when a size is larger than some phones can draw. The editor says so; it does not refuse. */
export function exceedsPhoneLimit(width: number, height: number): boolean {
  return width * height > PHONE_SAFE_PIXELS;
}

/** "1920 × 1080" — the same spelling the rest of the editor uses. */
export function formatSize(width: number, height: number): string {
  return `${width} × ${height}`;
}

/**
 * The longest side a picture opened on a photo gets: a larger photo is scaled down to it (both sides,
 * so the shape is kept). The server applies the same cap when it creates the picture.
 */
export const ORIGINAL_LONG_SIDE_MAX = 4096;

/**
 * "Original size": the photo's own pixels, the long side capped at {@link ORIGINAL_LONG_SIDE_MAX}.
 * Null when the photo's size is not known.
 */
export function originalCanvasSize(photo: { width: number; height: number }): { width: number; height: number } | null {
  if (!(photo.width > 0) || !(photo.height > 0)) return null;
  const f = Math.min(1, ORIGINAL_LONG_SIDE_MAX / Math.max(photo.width, photo.height));
  const edge = (n: number) => Math.min(CANVAS_MAX, Math.max(CANVAS_MIN, Math.round(n * f)));
  return { width: edge(photo.width), height: edge(photo.height) };
}
