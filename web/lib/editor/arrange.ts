/**
 * Placing layers relative to the canvas and to each other: align, distribute, and the numeric
 * position/size fields. Pure geometry over the layers' rotated bounding boxes (`layerBounds`), so a
 * turned layer lines up by what you SEE, not by its unrotated frame.
 */

import { layerBounds, type PlacedLayer } from './compose';
import type { EditorTransform } from './model';
import { SCALE_MAX, SCALE_MIN } from './transform-tools';

export type AlignMode = 'left' | 'center' | 'right' | 'top' | 'middle' | 'bottom';
export type DistributeAxis = 'horizontal' | 'vertical';

export interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface Move {
  id: string;
  transform: EditorTransform;
}

const round = (n: number) => Math.round(n * 100) / 100;

const boxOf = (layer: PlacedLayer): Box => layerBounds(layer).aabb;

/** The smallest box around all the layers' boxes, or null for none. */
export function unionBox(layers: readonly PlacedLayer[]): Box | null {
  if (layers.length === 0) return null;
  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  for (const layer of layers) {
    const b = boxOf(layer);
    x0 = Math.min(x0, b.x);
    y0 = Math.min(y0, b.y);
    x1 = Math.max(x1, b.x + b.w);
    y1 = Math.max(y1, b.y + b.h);
  }
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

const shifted = (layer: PlacedLayer, dx: number, dy: number): Move | null => {
  if (Math.abs(dx) < 0.005 && Math.abs(dy) < 0.005) return null;
  const t = layer.transform;
  return { id: layer.id, transform: { ...t, x: round(t.x + dx), y: round(t.y + dy) } };
};

/** The moves that line every (unlocked) layer up against `frame`: the canvas, or the selection's box. */
export function alignLayers(layers: readonly PlacedLayer[], mode: AlignMode, frame: Box): Move[] {
  const moves: Move[] = [];
  for (const layer of layers) {
    if (layer.locked) continue;
    const b = boxOf(layer);
    let dx = 0;
    let dy = 0;
    if (mode === 'left') dx = frame.x - b.x;
    else if (mode === 'center') dx = frame.x + frame.w / 2 - (b.x + b.w / 2);
    else if (mode === 'right') dx = frame.x + frame.w - (b.x + b.w);
    else if (mode === 'top') dy = frame.y - b.y;
    else if (mode === 'middle') dy = frame.y + frame.h / 2 - (b.y + b.h / 2);
    else dy = frame.y + frame.h - (b.y + b.h);
    const move = shifted(layer, dx, dy);
    if (move) moves.push(move);
  }
  return moves;
}

/**
 * Spreads three or more layers so the gaps between neighbours are equal, keeping the two outermost
 * where they are. Fewer than three: nothing to do.
 */
export function distributeLayers(layers: readonly PlacedLayer[], axis: DistributeAxis): Move[] {
  if (layers.length < 3) return [];
  const horizontal = axis === 'horizontal';
  const items = layers
    .map((layer) => ({ layer, box: boxOf(layer) }))
    .sort((a, b) => (horizontal ? a.box.x - b.box.x : a.box.y - b.box.y));
  const start = horizontal ? items[0].box.x : items[0].box.y;
  const last = items[items.length - 1].box;
  const end = horizontal ? last.x + last.w : last.y + last.h;
  const total = items.reduce((sum, i) => sum + (horizontal ? i.box.w : i.box.h), 0);
  const gap = (end - start - total) / (items.length - 1);
  const moves: Move[] = [];
  let cursor = start;
  for (const { layer, box } of items) {
    const size = horizontal ? box.w : box.h;
    if (!layer.locked) {
      const move = horizontal ? shifted(layer, cursor - box.x, 0) : shifted(layer, 0, cursor - box.y);
      if (move) moves.push(move);
    }
    cursor += size + gap;
  }
  return moves;
}

export type SizeField = 'x' | 'y' | 'width' | 'height';

/** What the numeric fields show for a layer, in canvas pixels (rounded for display). */
export function fieldValues(layer: PlacedLayer): Record<SizeField, number> {
  const t = layer.transform;
  return {
    x: Math.round(t.x),
    y: Math.round(t.y),
    width: Math.round(layer.size.w * t.scale),
    height: Math.round(layer.size.h * t.scale),
  };
}

/**
 * The transform after typing `value` into one field. Width and height change the (uniform) scale, so
 * the picture keeps its proportions and grows from its top-left corner. `null` for a value that is
 * not a number or changes nothing.
 */
export function setField(layer: PlacedLayer, field: SizeField, value: number): EditorTransform | null {
  if (!Number.isFinite(value)) return null;
  const t = layer.transform;
  let next: EditorTransform;
  if (field === 'x') next = { ...t, x: value };
  else if (field === 'y') next = { ...t, y: value };
  else {
    const base = field === 'width' ? layer.size.w : layer.size.h;
    if (base <= 0 || value <= 0) return null;
    next = { ...t, scale: Math.min(SCALE_MAX, Math.max(SCALE_MIN, value / base)) };
  }
  return next.x === t.x && next.y === t.y && next.scale === t.scale ? null : next;
}
