/**
 * Paints the editor's infinite workspace: the frame (the picture the export crops to), every layer —
 * including the parts that reach past the frame, drawn beside it and dimmed — the brush mask's tint,
 * and the in-place blur an AI tool shows while it works and as its result comes into focus.
 *
 * Pure in the same sense as `compose.ts`: the context, the scratch surfaces and the pattern are
 * arguments, so a recording context can drive it in a test. The layers are drawn with the same
 * `composeLayers` the flat export uses, so what is inside the frame on screen is what is exported.
 */

import { composeLayers, drawOverlayOnLayer, type Bitmap, type BitmapMap, type PlacedLayer, type ScratchFactory } from './compose';
import type { EffectFrame, Region } from './ai-effect';
import type { Size2, View } from './viewport';

/** The colour of the workspace around the frame. */
export const WORKSPACE_BG = '#0b0d12';
/** How strongly what lies outside the frame is dimmed (it is not part of the export). */
export const OUTSIDE_DIM = 'rgba(11, 13, 18, 0.62)';
/** The frame's outline. */
export const FRAME_OUTLINE = 'rgba(255, 255, 255, 0.35)';

/** An offscreen surface the painter reuses between frames. */
export interface Surface {
  canvas: Bitmap & { width: number; height: number };
  ctx: CanvasRenderingContext2D;
}

export interface WorkspaceInput {
  view: View;
  /** Device pixels per CSS pixel. */
  dpr: number;
  /** The workspace's CSS size. */
  size: Size2;
  frame: Size2;
  backgroundColor: string | null;
  layers: readonly PlacedLayer[];
  bitmaps: BitmapMap;
  /** Fill for a transparent frame (a checkerboard pattern), or null for a flat grey. */
  checker: CanvasPattern | string | null;
  /** The brush mask's tint, drawn over its layer. */
  mask?: { layer: PlacedLayer; tint: Bitmap; alpha: number } | null;
  /** The AI effect for this instant: the region (document pixels) and how it looks now. */
  effect?: { region: Region; look: EffectFrame } | null;
}

export interface WorkspaceSurfaces {
  /** Where the layers are composited before they go on screen (so blend modes match the export). */
  layers: Surface | null;
  /** Where the blur is made. */
  blur: Surface | null;
  scratch?: ScratchFactory;
}

interface DeviceRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

function ensureSize(surface: Surface, width: number, height: number): void {
  const w = Math.max(1, Math.round(width));
  const h = Math.max(1, Math.round(height));
  if (surface.canvas.width !== w) surface.canvas.width = w;
  if (surface.canvas.height !== h) surface.canvas.height = h;
}

/** The frame (or any document rectangle) in device pixels on the main canvas. */
export function docRectToDevice(view: View, dpr: number, r: Region): DeviceRect {
  return {
    x: (r.x * view.zoom + view.x) * dpr,
    y: (r.y * view.zoom + view.y) * dpr,
    w: r.w * view.zoom * dpr,
    h: r.h * view.zoom * dpr,
  };
}

/**
 * Draws the whole workspace for one instant onto `ctx`, whose canvas is `size × dpr` device pixels.
 */
export function paintWorkspace(ctx: CanvasRenderingContext2D, input: WorkspaceInput, surfaces: WorkspaceSurfaces): void {
  const { view, dpr, size, frame } = input;
  const W = Math.max(1, Math.round(size.width * dpr));
  const H = Math.max(1, Math.round(size.height * dpr));
  const frameRect = docRectToDevice(view, dpr, { x: 0, y: 0, w: frame.width, h: frame.height });

  // 1. the workspace
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalAlpha = 1;
  ctx.globalCompositeOperation = 'source-over';
  ctx.clearRect(0, 0, W, H);
  ctx.fillStyle = WORKSPACE_BG;
  ctx.fillRect(0, 0, W, H);

  // 2. a transparent frame shows the checkerboard; a coloured one is painted with the layers below
  if (input.backgroundColor === null) {
    ctx.fillStyle = input.checker ?? '#2a2d36';
    ctx.fillRect(frameRect.x, frameRect.y, frameRect.w, frameRect.h);
  }

  // 3. the layers, composited on their own surface so a blend mode blends with the layers and the
  //    background colour exactly as in the export, never with the workspace or the checkerboard
  const target = surfaces.layers;
  const docTransform = (c: CanvasRenderingContext2D) =>
    c.setTransform(dpr * view.zoom, 0, 0, dpr * view.zoom, dpr * view.x, dpr * view.y);
  const drawLayers = (c: CanvasRenderingContext2D) => {
    docTransform(c);
    if (input.backgroundColor) {
      c.fillStyle = input.backgroundColor;
      c.fillRect(0, 0, frame.width, frame.height);
    }
    composeLayers(c, input.layers, input.bitmaps, { scratch: surfaces.scratch });
    if (input.mask) drawOverlayOnLayer(c, input.mask.layer, input.mask.tint, input.mask.alpha);
  };
  if (target) {
    ensureSize(target, W, H);
    target.ctx.save();
    target.ctx.setTransform(1, 0, 0, 1, 0, 0);
    target.ctx.clearRect(0, 0, W, H);
    drawLayers(target.ctx);
    target.ctx.restore();
    ctx.drawImage(target.canvas, 0, 0, W, H);
  } else {
    ctx.save();
    drawLayers(ctx);
    ctx.restore();
  }

  // 4. what lies outside the frame stays visible, dimmed: it is part of the picture's layers, not of
  //    the export
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.fillStyle = OUTSIDE_DIM;
  ctx.beginPath();
  ctx.rect(0, 0, W, H);
  ctx.rect(frameRect.x, frameRect.y, frameRect.w, frameRect.h);
  ctx.fill('evenodd');

  // 5. the frame
  ctx.strokeStyle = FRAME_OUTLINE;
  ctx.lineWidth = Math.max(1, dpr);
  ctx.beginPath();
  ctx.rect(frameRect.x - 0.5, frameRect.y - 0.5, frameRect.w + 1, frameRect.h + 1);
  ctx.stroke();

  // 6. the AI effect, in place
  if (input.effect) {
    const region = docRectToDevice(view, dpr, input.effect.region);
    drawEffectOverlay(ctx, region, input.effect.look, dpr, { width: W, height: H }, surfaces.blur);
  }
  ctx.restore();
}

/** Clamps a device rectangle to the canvas; null when nothing of it is on screen. */
function clampRect(r: DeviceRect, bounds: Size2): DeviceRect | null {
  const x = Math.max(0, Math.floor(r.x));
  const y = Math.max(0, Math.floor(r.y));
  const right = Math.min(bounds.width, Math.ceil(r.x + r.w));
  const bottom = Math.min(bounds.height, Math.ceil(r.y + r.h));
  if (right - x < 1 || bottom - y < 1) return null;
  return { x, y, w: right - x, h: bottom - y };
}

/**
 * The blur and shimmer over one region of what is already on the canvas: the region's pixels are
 * shrunk (in halving steps, so the result is soft rather than sparkly) and drawn back up, which is a
 * blur every browser can do; where the canvas `filter` is supported it smooths the result further.
 */
export function drawEffectOverlay(
  ctx: CanvasRenderingContext2D,
  region: DeviceRect,
  look: EffectFrame,
  dpr: number,
  bounds: Size2,
  blur: Surface | null,
): void {
  const on = clampRect(region, bounds);
  if (!on) return;
  const blurPx = look.blurPx * dpr;

  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.beginPath();
  ctx.rect(on.x, on.y, on.w, on.h);
  ctx.clip();

  if (blur && blurPx >= 0.5 && look.alpha > 0.01) {
    // read a margin around the region too, so its edges blur into their surroundings instead of
    // into transparency
    const margin = Math.ceil(blurPx * 2);
    const src = clampRect({ x: on.x - margin, y: on.y - margin, w: on.w + margin * 2, h: on.h + margin * 2 }, bounds);
    if (src) {
      const factor = Math.max(1, Math.min(32, blurPx / 2));
      ensureSize(blur, src.w, src.h);
      const b = blur.ctx;
      b.save();
      b.setTransform(1, 0, 0, 1, 0, 0);
      b.globalAlpha = 1;
      b.globalCompositeOperation = 'copy';
      b.imageSmoothingEnabled = true;
      let scale = Math.min(2, factor);
      let cw = Math.max(1, Math.round(src.w / scale));
      let ch = Math.max(1, Math.round(src.h / scale));
      b.drawImage(ctx.canvas as unknown as CanvasImageSource, src.x, src.y, src.w, src.h, 0, 0, cw, ch);
      while (scale * 2 <= factor && cw > 1 && ch > 1) {
        const nw = Math.max(1, Math.round(cw / 2));
        const nh = Math.max(1, Math.round(ch / 2));
        b.drawImage(blur.canvas, 0, 0, cw, ch, 0, 0, nw, nh);
        cw = nw;
        ch = nh;
        scale *= 2;
      }
      b.restore();

      ctx.globalAlpha = Math.min(1, Math.max(0, look.alpha));
      ctx.imageSmoothingEnabled = true;
      if ('filter' in ctx) ctx.filter = `blur(${Math.max(0, Math.round(blurPx / 3))}px)`;
      ctx.drawImage(blur.canvas, 0, 0, cw, ch, src.x, src.y, src.w, src.h);
      if ('filter' in ctx) ctx.filter = 'none';
    }
  }

  if (look.shimmer !== null) {
    // a soft light band sweeping across the region: "working on it", in place
    const band = Math.max(on.w, on.h) * 0.35;
    const cx = on.x - band + look.shimmer * (on.w + band * 2);
    const gradient = ctx.createLinearGradient(cx - band, on.y, cx + band, on.y + on.h * 0.4);
    gradient.addColorStop(0, 'rgba(255,255,255,0)');
    gradient.addColorStop(0.5, `rgba(255,255,255,${0.16 * look.alpha})`);
    gradient.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.globalAlpha = 1;
    ctx.fillStyle = gradient;
    ctx.fillRect(on.x, on.y, on.w, on.h);
  }
  if (look.tint > 0) {
    ctx.globalAlpha = 1;
    ctx.fillStyle = `rgba(77, 171, 247, ${look.tint})`;
    ctx.fillRect(on.x, on.y, on.w, on.h);
  }
  ctx.restore();
}
