import type { VideoInsertQuadKeyframe } from '../types/timeline';

export interface InterpolatedQuad {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  x3: number;
  y3: number;
}

/**
 * Linearly interpolates the 4 corner points of a tracked screen insert
 * at the given timestamp in seconds.
 * 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.
 */
export function interpolateQuadAtTime(
  keyframes: VideoInsertQuadKeyframe[] | undefined,
  timeSec: number
): InterpolatedQuad | null {
  if (!keyframes || keyframes.length === 0) {
    return null;
  }

  // Before first keyframe
  if (timeSec <= keyframes[0].timeSec) {
    const k = keyframes[0];
    return { x0: k.x0, y0: k.y0, x1: k.x1, y1: k.y1, x2: k.x2, y2: k.y2, x3: k.x3, y3: k.y3 };
  }

  // After last keyframe
  const lastIndex = keyframes.length - 1;
  if (timeSec >= keyframes[lastIndex].timeSec) {
    const k = keyframes[lastIndex];
    return { x0: k.x0, y0: k.y0, x1: k.x1, y1: k.y1, x2: k.x2, y2: k.y2, x3: k.x3, y3: k.y3 };
  }

  // Find surrounding keyframes
  for (let i = 0; i < lastIndex; i++) {
    const k1 = keyframes[i];
    const k2 = keyframes[i + 1];

    if (timeSec >= k1.timeSec && timeSec <= k2.timeSec) {
      const span = k2.timeSec - k1.timeSec;
      const alpha = span > 0 ? (timeSec - k1.timeSec) / span : 0;

      const lerp = (a: number, b: number) => a + (b - a) * alpha;

      return {
        x0: lerp(k1.x0, k2.x0),
        y0: lerp(k1.y0, k2.y0),
        x1: lerp(k1.x1, k2.x1),
        y1: lerp(k1.y1, k2.y1),
        x2: lerp(k1.x2, k2.x2),
        y2: lerp(k1.y2, k2.y2),
        x3: lerp(k1.x3, k2.x3),
        y3: lerp(k1.y3, k2.y3),
      };
    }
  }

  return null;
}
