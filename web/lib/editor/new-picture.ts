/** The shapes a blank picture starts in, in the words a marketer uses. */

import { CANVAS_MAX, CANVAS_MIN } from './model';

export interface NewPictureShape {
  id: 'youtube' | 'square' | 'post' | 'story' | 'landscape' | 'custom';
  label: string;
  hint: string;
  /** Null for Custom: the user types the size. */
  width: number | null;
  height: number | null;
}

export const DEFAULT_NEW_PICTURE_SHAPE_ID: NewPictureShape['id'] = 'youtube';

export const NEW_PICTURE_SHAPES: readonly NewPictureShape[] = [
  { id: 'youtube', label: 'YouTube thumbnail', hint: '16:9, 1280 × 720', width: 1280, height: 720 },
  { id: 'square', label: 'Square', hint: '1080 × 1080', width: 1080, height: 1080 },
  { id: 'post', label: 'Post', hint: '4:5, 1080 × 1350', width: 1080, height: 1350 },
  { id: 'story', label: 'Story', hint: '9:16, 1080 × 1920', width: 1080, height: 1920 },
  { id: 'landscape', label: 'Landscape', hint: '16:9, 1920 × 1080', width: 1920, height: 1080 },
  { id: 'custom', label: 'Custom', hint: 'Your own size', width: null, height: null },
];

/** A sensible name to start with, so an unnamed picture is still findable in the list. */
export function defaultPictureName(shape: Pick<NewPictureShape, 'id' | 'label'>): string {
  if (shape.id === 'youtube') return 'Untitled thumbnail';
  return shape.id === 'custom' ? 'Untitled picture' : `Untitled ${shape.label.toLowerCase()}`;
}

/** The size to create: the preset's, or the typed one clamped to what the server accepts. Null when missing. */
export function newPictureSize(
  shape: NewPictureShape,
  custom: { width: number | string; height: number | string },
): { width: number; height: number } | null {
  const clamp = (n: number | string) => {
    const v = Math.round(Number(n));
    return Number.isFinite(v) && v > 0 ? Math.min(CANVAS_MAX, Math.max(CANVAS_MIN, v)) : null;
  };
  const width = shape.width ?? clamp(custom.width);
  const height = shape.height ?? clamp(custom.height);
  return width !== null && height !== null ? { width, height } : null;
}
