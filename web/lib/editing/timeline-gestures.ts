/**
 * The timeline's touch gestures as pure functions (R03.8 / RM.3), so the rules are unit-testable
 * without a browser. The canvas feeds them pointer facts and does the side effects itself.
 *
 *   one finger:  tap selects, a drag pans, a still finger held 500 ms is a long press
 *   two fingers: pinch zooms time around the midpoint, moving the midpoint pans
 *   first 20 px of the screen edge: ignored (the iOS back-swipe lives there)
 */

/** The same limits the toolbar zoom, the wheel zoom and the overview use. */
export const ZOOM_MIN = 0.01;
export const ZOOM_MAX = 64;
/** A finger that moved less than this is still "a tap". */
export const TAP_SLOP_PX = 8;
export const LONG_PRESS_MS = 500;
export const EDGE_DEAD_ZONE_PX = 20;
/** Two taps this close in time and place are a double tap. */
export const DOUBLE_TAP_MS = 300;
export const DOUBLE_TAP_SLOP_PX = 24;
/** A tile scrub needs this much sideways travel, and more sideways than vertical, before it takes over from page scroll. */
export const SCRUB_INTENT_PX = 10;

export const clampZoom = (pixelsPerFrame: number): number => Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, pixelsPerFrame));

/** True when a touch starts in the system-gesture strip at either side of the screen. */
export function inEdgeDeadZone(clientX: number, viewportWidth: number): boolean {
  return clientX < EDGE_DEAD_ZONE_PX || clientX > viewportWidth - EDGE_DEAD_ZONE_PX;
}

export type OneFingerKind = 'pending' | 'tap' | 'pan' | 'scrub' | 'long-press';

export interface OneFingerFacts {
  downX: number;
  downY: number;
  x: number;
  y: number;
  elapsedMs: number;
  /** The finger started on the ruler: a drag there scrubs the playhead instead of panning. */
  onRuler: boolean;
  /** The finger started on a clip, so holding still can become a long press. */
  onClip: boolean;
  /** The finger is up: no further movement can change the answer. */
  released: boolean;
}

/** What one finger is doing right now. `pending` means "not decided yet" (still inside the tap slop, before 500 ms). */
export function classifyOneFinger(f: OneFingerFacts): OneFingerKind {
  const moved = Math.hypot(f.x - f.downX, f.y - f.downY) > TAP_SLOP_PX;
  if (moved) return f.onRuler ? 'scrub' : 'pan';
  if (f.released) return 'tap';
  if (f.onClip && f.elapsedMs >= LONG_PRESS_MS) return 'long-press';
  return 'pending';
}

export interface Point {
  x: number;
  y: number;
}

export const distanceBetween = (a: Point, b: Point): number => Math.hypot(a.x - b.x, a.y - b.y);
export const midpointOf = (a: Point, b: Point): Point => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

export interface PinchStart {
  distance: number;
  pixelsPerFrame: number;
  /** The frame under the midpoint when the second finger landed. */
  anchorFrame: number;
}

/**
 * The viewport that keeps `anchorFrame` under the (moving) midpoint at the pinched zoom. Fingers
 * closer than a few pixels at the start cannot give a stable ratio, so the zoom is left alone.
 */
export function pinchViewport(start: PinchStart, distance: number, midX: number): { pixelsPerFrame: number; scrollFrame: number } {
  const ratio = start.distance >= 8 ? distance / start.distance : 1;
  const pixelsPerFrame = clampZoom(start.pixelsPerFrame * ratio);
  return { pixelsPerFrame, scrollFrame: Math.max(0, start.anchorFrame - midX / pixelsPerFrame) };
}

/** Zoom and scroll that fit [startFrame, endFrame) into the width with a margin on each side. */
export function zoomToRange(startFrame: number, endFrame: number, widthPx: number, marginFraction = 0.1): { pixelsPerFrame: number; scrollFrame: number } {
  const span = Math.max(1, endFrame - startFrame);
  const usable = widthPx * (1 - 2 * marginFraction);
  const pixelsPerFrame = clampZoom(usable / span);
  return { pixelsPerFrame, scrollFrame: Math.max(0, startFrame - (widthPx * marginFraction) / pixelsPerFrame) };
}

export interface TapRecord {
  time: number;
  x: number;
  y: number;
  itemId: string | null;
}

export function isDoubleTap(previous: TapRecord | null, next: TapRecord): boolean {
  return (
    previous !== null &&
    next.itemId !== null &&
    previous.itemId === next.itemId &&
    next.time - previous.time <= DOUBLE_TAP_MS &&
    Math.hypot(next.x - previous.x, next.y - previous.y) <= DOUBLE_TAP_SLOP_PX
  );
}

export type TileScrubKind = 'pending' | 'scrub' | 'scroll';

/**
 * A finger pressed on a media tile: wait for a clear sideways intent before scrubbing, and give
 * vertical movement back to the page scroll.
 */
export function classifyTileScrub(dx: number, dy: number): TileScrubKind {
  const ax = Math.abs(dx);
  const ay = Math.abs(dy);
  if (ax >= SCRUB_INTENT_PX && ax > ay) return 'scrub';
  if (ay >= SCRUB_INTENT_PX && ay >= ax) return 'scroll';
  return 'pending';
}
