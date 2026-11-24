/**
 * R12.7: saved layouts keep panel sizes as fractions of the window (left and right of its width, the
 * timeline of its height), so a layout saved on a big monitor still fits a laptop or a tablet.
 */

export interface PanelSizes {
  left: number;
  right: number;
  timeline: number;
}
export interface Bounds {
  width: number;
  height: number;
}

const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));
const round = (v: number): number => Math.round(v * 10000) / 10000;

export function toFractions(sizes: PanelSizes, bounds: Bounds): PanelSizes {
  const w = Math.max(1, bounds.width);
  const h = Math.max(1, bounds.height);
  return { left: round(sizes.left / w), right: round(sizes.right / w), timeline: round(sizes.timeline / h) };
}

/** Pixels for a saved layout in a window of `bounds`; the side panels never squeeze the viewer under 400 px, nor a panel under `min`. */
export function fromFractions(fractions: PanelSizes, bounds: Bounds, min = 160): PanelSizes {
  const sideMax = Math.max(min, bounds.width - 400);
  const left = clamp(fractions.left * bounds.width, min, sideMax);
  const right = clamp(fractions.right * bounds.width, min, Math.max(min, sideMax - left + min));
  return { left: Math.round(left), right: Math.round(right), timeline: Math.round(clamp(fractions.timeline * bounds.height, min, Math.max(min, bounds.height - 160))) };
}

/** True when `value` looks like a fractions triple (each in 0..1). */
export function isFractions(value: unknown): value is PanelSizes {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  return (['left', 'right', 'timeline'] as const).every((k) => typeof v[k] === 'number' && (v[k] as number) >= 0 && (v[k] as number) <= 1);
}
