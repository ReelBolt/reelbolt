import { useRef, useCallback } from 'react';
import type { PoolPlacementCtx } from './usePoolPlacement';

/** Zooming the timeline. */
export function useTimelineZoom(ctx: PoolPlacementCtx) {
  const { store, rate, endFrame } = ctx;
  // ── zoom ──────────────────────────────────────────────────────────────────
  const timelineBodyRef = useRef<HTMLDivElement | null>(null);
  const zoomTo = useCallback(
    (pixelsPerFrame: number) => {
      const s = store.getState().ui;
      const next = Math.max(0.01, Math.min(64, pixelsPerFrame));
      // Keep the playhead where it is on screen.
      const anchorX = (s.playheadFrame - s.scrollFrame) * s.pixelsPerFrame;
      store.setUi({ pixelsPerFrame: next, scrollFrame: Math.max(0, s.playheadFrame - anchorX / next) });
    },
    [store],
  );
  const zoomToFit = useCallback(() => {
    const width = (timelineBodyRef.current?.clientWidth ?? 900) - 180 - 24;
    const fps = rate.den > 0 ? rate.num / rate.den : 30;
    const span = Math.max(endFrame, Math.round(fps * 10));
    store.setUi({ pixelsPerFrame: Math.max(0.01, Math.min(64, width / span)), scrollFrame: 0 });
  }, [store, endFrame, rate]);

  return { timelineBodyRef, zoomTo, zoomToFit };
}

export type TimelineZoomCtx = PoolPlacementCtx & ReturnType<typeof useTimelineZoom>;
