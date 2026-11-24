/**
 * Titles, captions and generators in TRACK ORDER. The render composites every track's item at that
 * track's place in the stack, so a title on V1 sits under a video on V2. The viewer used to rasterise all
 * of them onto one canvas drawn above every video; instead, each run of consecutive overlay layers (in
 * draw order) becomes ONE picture layer at the run's own position in the stack.
 *
 * Pure: it only groups; rasterising and drawing stay in `live-session.ts` / `overlay-raster.ts`.
 */

import { hasKeyedTransform, type OverlayItem } from './overlay-raster';
import type { ActiveLayer } from './scheduler';

export interface OverlayRun {
  /** 0-based position of the run among the frame's runs; one raster canvas per slot. */
  slot: number;
  /** The run's items, bottom to top. */
  items: OverlayItem[];
  /** Identifies the run's content: equal signatures raster to the same picture. */
  signature: string;
}

/** True for every layer the 2D overlay raster draws (everything but a media clip and an adjustment clip, which the compositor applies to the picture below). */
export function isOverlayLayer(layer: ActiveLayer): boolean {
  return layer.item.type !== 'mediaClip' && layer.item.type !== 'adjustmentClip' && !(layer.item.type === 'insertClip' && layer.item.assetRef);
}

/**
 * Groups consecutive overlay layers (in `layers` order, bottom first) into runs. The result is keyed by the
 * FIRST layer of each run: the compositor draws the run's picture where that layer sits, and the run's other
 * layers draw nothing of their own.
 */
export function overlayRunsOf(layers: readonly ActiveLayer[], frame?: number): Map<ActiveLayer, OverlayRun> {
  const runs = new Map<ActiveLayer, OverlayRun>();
  let open: { first: ActiveLayer; items: OverlayItem[] } | null = null;
  const close = () => {
    if (!open) return;
    runs.set(open.first, { slot: runs.size, items: open.items, signature: JSON.stringify(open.items) });
    open = null;
  };
  for (const layer of layers) {
    if (!isOverlayLayer(layer)) {
      close();
      continue;
    }
    if (!open) open = { first: layer, items: [] };
    // A title with a keyframed transform rasters differently on every frame, so its run carries the frame (and so does its signature).
    open.items.push(layer.item.type === 'titleClip' && frame !== undefined && hasKeyedTransform(layer.item.transform) ? { item: layer.item, layerIndex: layer.layerIndex, frame } : { item: layer.item, layerIndex: layer.layerIndex });
  }
  close();
  return runs;
}
