/**
 * Picking an export quality that fits a size budget ("Under 2 MB"). The file is encoded in the
 * browser, so the search encodes at a few qualities and keeps the highest one that fits. Pure: the
 * encoder is passed in, so it is tested without a canvas.
 */

/** The size hint button on the export dialog. */
export const UNDER_2_MB = 2 * 1000 * 1000;

export interface QualityFit {
  /** The highest quality (0..1) whose file fits, or the lowest tried when none does. */
  quality: number;
  /** The size at that quality, in bytes. */
  bytes: number;
  /** False when even the lowest quality is over the limit. */
  fits: boolean;
}

/**
 * Binary search over quality for the largest file that stays within `limit` bytes. `encode` answers
 * the byte size at a quality. About six encodes at the default precision.
 */
export async function fitQuality(
  encode: (quality: number) => Promise<number>,
  limit: number,
  { min = 0.4, max = 1, precision = 0.02 }: { min?: number; max?: number; precision?: number } = {},
): Promise<QualityFit> {
  const top = await encode(max);
  if (top <= limit) return { quality: max, bytes: top, fits: true };
  const bottom = await encode(min);
  if (bottom > limit) return { quality: min, bytes: bottom, fits: false };
  let lo = min;
  let loBytes = bottom;
  let hi = max;
  while (hi - lo > precision) {
    const mid = (lo + hi) / 2;
    const bytes = await encode(mid);
    if (bytes <= limit) {
      lo = mid;
      loBytes = bytes;
    } else {
      hi = mid;
    }
  }
  return { quality: Math.round(lo * 100) / 100, bytes: loBytes, fits: true };
}

/** "about 1.4 MB", the way the dialog shows an estimate. */
export function estimateSizeText(bytes: number): string {
  if (bytes < 1000) return `about ${bytes} bytes`;
  if (bytes < 1_000_000) return `about ${Math.round(bytes / 1000)} KB`;
  return `about ${(bytes / 1_000_000).toFixed(1)} MB`;
}
