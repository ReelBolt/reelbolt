/**
 * The editor's infinite workspace: where the picture's frame sits on screen, and how pan and zoom
 * move it.
 *
 * A view is `{ zoom, x, y }`: a document point `p` is drawn at screen point `p * zoom + (x, y)`, in
 * CSS pixels of the workspace element. The frame (the picture the export crops to) is the document
 * rectangle `0..canvasWidth × 0..canvasHeight`; nothing about the view is limited to it, so a layer
 * dragged past the frame stays visible beside it rather than being cut off.
 *
 * Pure: no React, no DOM. The view is editor state, never part of the document, and never saved.
 */

import type { Point } from './compose';

export interface View {
  zoom: number;
  /** Screen position of the document origin (the frame's top-left corner). */
  x: number;
  y: number;
}

export interface Size2 {
  width: number;
  height: number;
}

export const MIN_ZOOM = 0.02;
export const MAX_ZOOM = 32;

/** The empty space kept around the frame by "fit to screen", in CSS pixels. */
export const FIT_MARGIN = 32;

export function clampZoom(zoom: number): number {
  if (!Number.isFinite(zoom) || zoom <= 0) return 1;
  return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom));
}

/** Empty space kept on each side of the frame by "fit to screen", in CSS pixels. */
export interface FitInsets {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

/** How far above the top edge of a selected layer its rotate handle is drawn, in CSS pixels. */
export const ROTATE_HANDLE_OFFSET_PX = 34;

/** The zoom bar's distance from the workspace's bottom edge, in CSS pixels. */
export const ZOOM_BAR_INSET_PX = 8;

/** The zoom bar's height: 28 px buttons with a mouse, 44 px on a touch screen, plus padding and border. */
export function zoomBarHeight(coarse: boolean): number {
  return (coarse ? 44 : 28) + 10;
}

/**
 * The insets "fit to screen" keeps so a layer that fills the frame can still be transformed: room
 * above for its rotate handle, and room below for its corner handles clear of the zoom bar (on a
 * phone they used to sit under it). A touch screen gets more, for a finger.
 */
export function fitInsetsFor(coarse: boolean): FitInsets {
  const handle = coarse ? 12 : 8;
  return {
    top: ROTATE_HANDLE_OFFSET_PX + handle + 4,
    right: FIT_MARGIN,
    bottom: ZOOM_BAR_INSET_PX + zoomBarHeight(coarse) + handle + 4,
    left: FIT_MARGIN,
  };
}

/**
 * The view that shows the whole frame, as large as fits inside the workspace less `margin` (one
 * number for every side, or {@link FitInsets}), centred in what is left.
 */
export function fitView(frame: Size2, workspace: Size2, margin: number | FitInsets = FIT_MARGIN): View {
  if (!(frame.width > 0) || !(frame.height > 0) || !(workspace.width > 0) || !(workspace.height > 0)) {
    return { zoom: 1, x: 0, y: 0 };
  }
  const m = typeof margin === 'number' ? { top: margin, right: margin, bottom: margin, left: margin } : margin;
  const usableW = Math.max(1, workspace.width - m.left - m.right);
  const usableH = Math.max(1, workspace.height - m.top - m.bottom);
  const zoom = clampZoom(Math.min(usableW / frame.width, usableH / frame.height));
  return {
    zoom,
    x: m.left + (usableW - frame.width * zoom) / 2,
    y: m.top + (usableH - frame.height * zoom) / 2,
  };
}

/** The frame at `zoom`, centred in the workspace. */
export function centred(frame: Size2, workspace: Size2, zoom: number): View {
  const z = clampZoom(zoom);
  return {
    zoom: z,
    x: (workspace.width - frame.width * z) / 2,
    y: (workspace.height - frame.height * z) / 2,
  };
}

export function screenToDoc(view: View, screen: Point): Point {
  return { x: (screen.x - view.x) / view.zoom, y: (screen.y - view.y) / view.zoom };
}

export function docToScreen(view: View, doc: Point): Point {
  return { x: doc.x * view.zoom + view.x, y: doc.y * view.zoom + view.y };
}

/**
 * Zooms by `factor` keeping the document point under `anchor` (a screen point) exactly where it is —
 * the cursor for the wheel, the midpoint of the fingers for a pinch, the workspace centre for a button.
 */
export function zoomAt(view: View, factor: number, anchor: Point): View {
  const zoom = clampZoom(view.zoom * factor);
  const docPoint = screenToDoc(view, anchor);
  return { zoom, x: anchor.x - docPoint.x * zoom, y: anchor.y - docPoint.y * zoom };
}

/** The same view moved by a screen-space delta. */
export function panBy(view: View, dx: number, dy: number): View {
  return { ...view, x: view.x + dx, y: view.y + dy };
}

/** How much one wheel unit of `deltaY` zooms. Tuned so a mouse notch (~100) is about 18%. */
const WHEEL_ZOOM_RATE = 0.0018;

/** The zoom factor for a wheel or pinch delta (negative delta = zoom in, like every map and editor). */
export function wheelZoomFactor(deltaY: number, deltaMode = 0): number {
  const pixels = deltaMode === 1 ? deltaY * 33 : deltaMode === 2 ? deltaY * 400 : deltaY;
  const clamped = Math.max(-600, Math.min(600, pixels));
  return Math.exp(-clamped * WHEEL_ZOOM_RATE);
}

export interface WheelLike {
  deltaX: number;
  deltaY: number;
  deltaMode: number;
  ctrlKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
}

/**
 * What a wheel event means in the workspace.
 *
 * - A pinch on a trackpad arrives as a wheel event with `ctrlKey` set (every desktop browser), and
 *   Ctrl/Cmd + wheel is the zoom everybody already knows: both zoom.
 * - A mouse wheel moves in whole lines, or in large even steps: that zooms too, because a mouse has
 *   no other way to zoom and the editor has nothing else to scroll.
 * - A two-finger trackpad scroll arrives as small pixel deltas, often with a sideways part: that
 *   pans, which is what the fingers are asking for.
 * - Shift + wheel pans sideways, the usual convention for a mouse.
 */
export function classifyWheel(e: WheelLike): 'zoom' | 'pan' {
  if (e.ctrlKey || e.metaKey) return 'zoom';
  if (e.shiftKey) return 'pan';
  if (e.deltaMode !== 0) return 'zoom';
  if (e.deltaX !== 0) return 'pan';
  const step = Math.abs(e.deltaY);
  return step >= 40 && Number.isInteger(step) ? 'zoom' : 'pan';
}

/** The pan for a wheel event classified as `pan` (Shift turns a vertical wheel sideways). */
export function wheelPan(e: WheelLike): { dx: number; dy: number } {
  const scale = e.deltaMode === 1 ? 33 : e.deltaMode === 2 ? 400 : 1;
  if (e.shiftKey && e.deltaX === 0) return { dx: -e.deltaY * scale, dy: 0 };
  return { dx: -e.deltaX * scale, dy: -e.deltaY * scale };
}

/** The zoom steps the − / + buttons move between. */
export const ZOOM_STEPS: readonly number[] = [0.05, 0.1, 0.125, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32];

/** The next zoom step above (`direction` 1) or below (-1) the current zoom. */
export function nextZoomStep(zoom: number, direction: 1 | -1): number {
  const epsilon = 1e-6;
  if (direction > 0) return ZOOM_STEPS.find((s) => s > zoom + epsilon) ?? MAX_ZOOM;
  for (let i = ZOOM_STEPS.length - 1; i >= 0; i--) if (ZOOM_STEPS[i] < zoom - epsilon) return ZOOM_STEPS[i];
  return MIN_ZOOM;
}

/** "75%" — the zoom the way the toolbar shows it. */
export function formatZoom(zoom: number): string {
  const pct = zoom * 100;
  return `${pct < 10 ? pct.toFixed(1).replace(/\.0$/, '') : Math.round(pct)}%`;
}

/**
 * The document rectangle visible in the workspace (used to bound what an off-frame render has to
 * cover). Always contains the frame-relative region the screen shows, whatever the zoom.
 */
export function visibleDocRect(view: View, workspace: Size2): { x: number; y: number; w: number; h: number } {
  const tl = screenToDoc(view, { x: 0, y: 0 });
  return { x: tl.x, y: tl.y, w: workspace.width / view.zoom, h: workspace.height / view.zoom };
}

/** Two-finger gesture: the midpoint and spread of two touches. */
export function pinchState(a: Point, b: Point): { mid: Point; distance: number } {
  return { mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }, distance: Math.hypot(a.x - b.x, a.y - b.y) };
}

/**
 * The view after the two fingers moved from `before` to `after`: zoomed by the change in spread about
 * the old midpoint, then panned by how far the midpoint travelled — the "map" gesture.
 */
export function applyPinch(
  view: View,
  before: { mid: Point; distance: number },
  after: { mid: Point; distance: number },
): View {
  const factor = before.distance > 0 && after.distance > 0 ? after.distance / before.distance : 1;
  const zoomed = zoomAt(view, factor, before.mid);
  return panBy(zoomed, after.mid.x - before.mid.x, after.mid.y - before.mid.y);
}
