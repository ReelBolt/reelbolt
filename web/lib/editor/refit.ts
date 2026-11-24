/**
 * "Fit what's there": changing the canvas shape and carrying every layer along. The old frame is
 * scaled uniformly to fit inside the new one (so nothing is stretched or cut) and centred; each layer
 * keeps its place inside that scaled frame. Pure, one undo step.
 */

import type { Command } from './history';
import type { EditorDocState, EditorLayer } from './model';

export interface RefitPlacement {
  /** The uniform scale applied to the old frame. */
  factor: number;
  /** Where the old frame's top-left corner lands in the new frame. */
  offsetX: number;
  offsetY: number;
}

/** How the old frame sits inside the new one, scaled to fit and centred. Null when a size is unusable. */
export function refitPlacement(
  from: { width: number; height: number },
  to: { width: number; height: number },
): RefitPlacement | null {
  if (!(from.width > 0) || !(from.height > 0) || !(to.width > 0) || !(to.height > 0)) return null;
  const factor = Math.min(to.width / from.width, to.height / from.height);
  return {
    factor,
    offsetX: (to.width - from.width * factor) / 2,
    offsetY: (to.height - from.height * factor) / 2,
  };
}

/** True when two sizes have the same shape (within rounding), so a refit is a plain rescale. */
export function sameShape(a: { width: number; height: number }, b: { width: number; height: number }): boolean {
  if (!(a.width > 0) || !(a.height > 0) || !(b.width > 0) || !(b.height > 0)) return false;
  return Math.abs(a.width / a.height - b.width / b.height) / (a.width / a.height) <= 0.01;
}

const place = (layer: EditorLayer, p: RefitPlacement): EditorLayer => ({
  ...layer,
  transform: {
    ...layer.transform,
    x: layer.transform.x * p.factor + p.offsetX,
    y: layer.transform.y * p.factor + p.offsetY,
    scale: layer.transform.scale * p.factor,
  },
});

/**
 * The canvas becomes `size` and every layer is scaled and moved with the frame. One undo step that
 * puts the old size and every old transform back. Null when nothing would change.
 */
export function refitCanvas(state: EditorDocState, size: { width: number; height: number }, label: string): Command | null {
  if (size.width === state.canvasWidth && size.height === state.canvasHeight) return null;
  const placement = refitPlacement({ width: state.canvasWidth, height: state.canvasHeight }, size);
  if (!placement) return null;
  const before = new Map(state.layers.map((l) => [l.id, l.transform]));
  const beforeSize = { canvasWidth: state.canvasWidth, canvasHeight: state.canvasHeight };
  return {
    label,
    do: (s) => ({
      ...s,
      canvasWidth: size.width,
      canvasHeight: size.height,
      layers: s.layers.map((l) => (before.has(l.id) ? place(l, placement) : l)),
    }),
    undo: (s) => ({
      ...s,
      ...beforeSize,
      layers: s.layers.map((l) => {
        const t = before.get(l.id);
        return t ? { ...l, transform: t } : l;
      }),
    }),
  };
}
