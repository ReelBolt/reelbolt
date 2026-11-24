/**
 * Crop geometry in a picture layer's own pixels (its unrotated, unflipped frame). Pure.
 */

import { layerLocalToCanvas, type PlacedLayer, type Point } from './compose';
import { normalizeCrop, type EditorTransform } from './model';
import type { LayerCrop } from '@/lib/types/image-document';

export interface CropRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export type CropHandle = 'move' | 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';

export const MIN_CROP = 8;

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));

/** Pulls a rectangle inside the picture, whole pixels, never smaller than {@link MIN_CROP}. */
export function clampCrop(rect: CropRect, width: number, height: number): CropRect {
  const min = Math.min(MIN_CROP, width, height);
  const w = clamp(Math.round(rect.w), min, width);
  const h = clamp(Math.round(rect.h), min, height);
  return { x: clamp(Math.round(rect.x), 0, width - w), y: clamp(Math.round(rect.y), 0, height - h), w, h };
}

/** The rectangle after dragging `handle` by (dx, dy) picture pixels. */
export function dragCrop(start: CropRect, handle: CropHandle, dx: number, dy: number, width: number, height: number): CropRect {
  if (handle === 'move') return clampCrop({ ...start, x: start.x + dx, y: start.y + dy }, width, height);
  let x0 = start.x;
  let y0 = start.y;
  let x1 = start.x + start.w;
  let y1 = start.y + start.h;
  if (handle.includes('w')) x0 = clamp(x0 + dx, 0, x1 - MIN_CROP);
  if (handle.includes('e')) x1 = clamp(x1 + dx, x0 + MIN_CROP, width);
  if (handle.includes('n')) y0 = clamp(y0 + dy, 0, y1 - MIN_CROP);
  if (handle.includes('s')) y1 = clamp(y1 + dy, y0 + MIN_CROP, height);
  return clampCrop({ x: x0, y: y0, w: x1 - x0, h: y1 - y0 }, width, height);
}

/** The layer's whole stored picture in pixels, whatever crop it currently has. */
export function naturalSize(layer: Pick<PlacedLayer, 'size' | 'crop'>): { w: number; h: number } {
  return { w: layer.size.w / (layer.crop?.w ?? 1), h: layer.size.h / (layer.crop?.h ?? 1) };
}

/** A crop as a rectangle in the whole picture's pixels (the full picture when there is no crop). */
export function cropToRect(crop: LayerCrop | null | undefined, nat: { w: number; h: number }): CropRect {
  if (!crop) return { x: 0, y: 0, w: Math.round(nat.w), h: Math.round(nat.h) };
  return clampCrop(
    { x: crop.x * nat.w, y: crop.y * nat.h, w: crop.w * nat.w, h: crop.h * nat.h },
    Math.round(nat.w),
    Math.round(nat.h),
  );
}

const round6 = (n: number) => Math.round(n * 1e6) / 1e6;

/** A rectangle in the whole picture's pixels as stored fractions; null when it is the whole picture. */
export function rectToCrop(rect: CropRect, nat: { w: number; h: number }): LayerCrop | null {
  const W = Math.round(nat.w);
  const H = Math.round(nat.h);
  if (rect.x <= 0 && rect.y <= 0 && rect.w >= W && rect.h >= H) return null;
  return normalizeCrop({
    x: round6(rect.x / W),
    y: round6(rect.y / H),
    w: round6(Math.min(rect.w, W - rect.x) / W),
    h: round6(Math.min(rect.h, H - rect.y) / H),
  });
}

/**
 * Where the layer goes so the kept part stays exactly where it was on the canvas whatever the new
 * rectangle (narrower to crop, wider to reveal more): same scale, rotation and flips, with the new
 * window's centre where it stands now. `rect` is in the whole picture's pixels.
 */
export function croppedTransform(layer: PlacedLayer, rect: CropRect): EditorTransform {
  const t = layer.transform;
  const nat = naturalSize(layer);
  const ox = (layer.crop?.x ?? 0) * nat.w;
  const oy = (layer.crop?.y ?? 0) * nat.h;
  const centre: Point = layerLocalToCanvas(layer, { x: rect.x + rect.w / 2 - ox, y: rect.y + rect.h / 2 - oy });
  return {
    ...t,
    x: Math.round((centre.x - (rect.w * t.scale) / 2) * 100) / 100,
    y: Math.round((centre.y - (rect.h * t.scale) / 2) * 100) / 100,
  };
}
