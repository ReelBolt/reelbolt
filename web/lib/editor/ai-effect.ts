/**
 * What the canvas shows while an AI tool works, and when its result lands: the part of the picture
 * being reworked goes soft and shimmers in place, and when the new layer arrives the changed pixels
 * come out of that blur into focus — on the editor's own canvas, where the work is happening, not
 * in a separate preview.
 *
 * Pure: no React, no DOM, no clock. The canvas passes `now` in; this module says how blurred the
 * region is at that instant and where the shimmer is, and the canvas draws it (see
 * `drawEffectOverlay` in `workspace.ts`). The effect is screen-only: it is never part of the
 * document, the saved picture or an export.
 */

import { layerBounds, layerLocalToCanvas, type PlacedLayer, type Point } from './compose';

/** A rectangle in DOCUMENT pixels (the frame's own space). */
export interface Region {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Which part of the picture an AI run is about, resolved to a rectangle by the canvas once it knows
 * where the layers are (a layer still loading has no bounds yet).
 *
 * - `frame`: the whole picture (creating a new picture).
 * - `layers`: the bounds of these layers together (the selected layer while working; the new layers
 *   once they arrive).
 * - `mask`: the painted part of one layer (erase and fill change only what was painted), in that
 *   layer's own pixels.
 * - `region`: a rectangle already in document pixels — where a designed graphic is expected to land
 *   while the designer is still working on it (there is no layer yet to take the bounds of).
 */
export type EffectTarget =
  | { kind: 'frame' }
  | { kind: 'layers'; ids: readonly string[] }
  | { kind: 'mask'; layerId: string; bounds: Region }
  | { kind: 'region'; bounds: Region };

export type AiEffect =
  /** The AI is working: the target region is blurred and shimmering. */
  | { phase: 'working'; target: EffectTarget; startedAt: number }
  /**
   * The result has arrived: the region resolves from blur to sharp from `startedAt`. The canvas
   * holds the working look until every layer in `waitFor` can be drawn (its pixels have loaded) and
   * starts the reveal then — otherwise the focus would come up on a picture that is not there yet.
   */
  | { phase: 'revealing'; target: EffectTarget; startedAt: number; waitFor?: readonly string[] };

/** The longest the canvas waits for a result's pixels before revealing anyway. */
export const REVEAL_WAIT_LIMIT_MS = 15_000;

/** How long the region takes to go soft when work starts. */
export const WORKING_BLUR_IN_MS = 320;
/** How long the result takes to come into focus. */
export const REVEAL_MS = 800;
/** One sweep of the shimmer across the region. */
export const SHIMMER_PERIOD_MS = 1600;
/** Blur radius while working, in SCREEN pixels (so it reads the same at any zoom). */
export const WORKING_BLUR_PX = 14;
/** Opacity of the faint blue "working" tint over the region. */
export const WORKING_TINT = 0.08;
/** Blur radius the result starts from: a touch stronger than the working blur, so the swap is hidden. */
export const REVEAL_BLUR_PX = 22;

export interface EffectFrame {
  /** Blur radius in screen pixels; 0 = sharp. */
  blurPx: number;
  /** Opacity of the blurred copy over the sharp picture (1 = only the blur shows). */
  alpha: number;
  /** Where the shimmer band is across the region, 0..1, or null for no shimmer. */
  shimmer: number | null;
  /** Opacity of a faint "working" tint over the region (0 once the result is in). */
  tint: number;
  /** True once a reveal has finished and the overlay can be dropped. */
  done: boolean;
}

const easeOutCubic = (t: number) => 1 - Math.pow(1 - t, 3);
const clamp01 = (t: number) => (Number.isFinite(t) ? Math.min(1, Math.max(0, t)) : 0);

/**
 * The effect at instant `now` (same clock as `startedAt`). With `reducedMotion` nothing moves: the
 * working region is simply shown blurred (no shimmer, no fade-in), and a reveal is over the moment it
 * starts — the result appears sharp straight away.
 */
export function sampleEffect(effect: AiEffect, now: number, reducedMotion = false): EffectFrame {
  const elapsed = Math.max(0, now - effect.startedAt);
  if (effect.phase === 'working') {
    if (reducedMotion) return { blurPx: WORKING_BLUR_PX, alpha: 1, shimmer: null, tint: WORKING_TINT, done: false };
    const fadeIn = easeOutCubic(clamp01(elapsed / WORKING_BLUR_IN_MS));
    return {
      blurPx: WORKING_BLUR_PX * fadeIn,
      alpha: fadeIn,
      shimmer: (elapsed % SHIMMER_PERIOD_MS) / SHIMMER_PERIOD_MS,
      tint: WORKING_TINT * fadeIn,
      done: false,
    };
  }
  if (reducedMotion) return { blurPx: 0, alpha: 0, shimmer: null, tint: 0, done: true };
  const t = clamp01(elapsed / REVEAL_MS);
  const eased = easeOutCubic(t);
  return {
    blurPx: REVEAL_BLUR_PX * (1 - eased),
    // The blurred copy fades a little behind the radius, so the last moments are a crisp settle
    // rather than a cross-fade of two almost identical pictures.
    alpha: 1 - Math.pow(t, 1.6),
    shimmer: null,
    tint: 0,
    done: t >= 1,
  };
}

/** The smallest rectangle around a set of points. */
function boxAround(points: readonly Point[]): Region {
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  const x = Math.min(...xs);
  const y = Math.min(...ys);
  return { x, y, w: Math.max(...xs) - x, h: Math.max(...ys) - y };
}

/** The union of rectangles, or null for none. */
export function unionRegions(regions: readonly Region[]): Region | null {
  if (regions.length === 0) return null;
  const x = Math.min(...regions.map((r) => r.x));
  const y = Math.min(...regions.map((r) => r.y));
  const right = Math.max(...regions.map((r) => r.x + r.w));
  const bottom = Math.max(...regions.map((r) => r.y + r.h));
  return { x, y, w: right - x, h: bottom - y };
}

/**
 * The document rectangle a target covers, or null when it cannot be placed yet (its layers have not
 * loaded, or were removed). A mask's bounds are carried through its layer's transform, so a stroke
 * on a moved, scaled or rotated layer still lands where it was painted.
 */
export function resolveTarget(
  target: EffectTarget,
  layers: readonly PlacedLayer[],
  frame: { width: number; height: number },
): Region | null {
  switch (target.kind) {
    case 'frame':
      return { x: 0, y: 0, w: frame.width, h: frame.height };
    case 'layers': {
      const boxes = target.ids
        .map((id) => layers.find((l) => l.id === id))
        .filter((l): l is PlacedLayer => !!l)
        .map((l) => layerBounds(l).aabb);
      return unionRegions(boxes);
    }
    case 'mask': {
      const layer = layers.find((l) => l.id === target.layerId);
      if (!layer) return null;
      const b = target.bounds;
      return boxAround(
        [
          { x: b.x, y: b.y },
          { x: b.x + b.w, y: b.y },
          { x: b.x + b.w, y: b.y + b.h },
          { x: b.x, y: b.y + b.h },
        ].map((p) => layerLocalToCanvas(layer, p)),
      );
    }
    case 'region': {
      const b = target.bounds;
      if (![b.x, b.y, b.w, b.h].every(Number.isFinite) || b.w <= 0 || b.h <= 0) return null;
      return { x: b.x, y: b.y, w: b.w, h: b.h };
    }
  }
}

/** True when every one of these layers can be drawn — the moment a reveal can start. */
export function layersReady(
  ids: readonly string[],
  layers: readonly Pick<PlacedLayer, 'id' | 'kind'>[],
  hasPixels: (id: string) => boolean,
): boolean {
  return ids.every((id) => {
    const layer = layers.find((l) => l.id === id);
    if (!layer) return false;
    return layer.kind === 'text' || hasPixels(id);
  });
}
