/**
 * The thumbnail editor's compositor: layers in, pixels out, on any `CanvasRenderingContext2D`.
 *
 * Pure in the sense that matters here: no React, no `document`, no `window`, no clock. Everything
 * the browser must provide arrives as an argument — the decoded bitmaps, a text measurer, a scratch
 * canvas factory — so the same function draws the on-screen view and every export, and a test
 * can drive it with a recording context.
 *
 * ## Conventions (docs/design/thumbnail-editor.md section 6)
 *
 * - Layers are drawn back to front in `zOrder` order; a hidden layer is skipped entirely.
 * - A layer's `transform.x`/`y` are canvas pixels of its top-left corner, `scale` is uniform and
 *   `rotation` is degrees clockwise about the layer's *centre*. Flips mirror about that same
 *   centre. The context is moved to the centre, rotated, then scaled, and the layer is drawn
 *   around the origin — so rotation and flip never change where the layer sits.
 * - Opacity is `globalAlpha`; the blend mode is `globalCompositeOperation` (`normal` is
 *   `source-over`; the other eight are the canvas names verbatim).
 * - A bitmap layer of ANY kind with a matte (a cut-out's, or a mask the user painted) is drawn bitmap-first, then `destination-in` the matte, **on a scratch
 *   canvas**: doing that on the main canvas would erase every layer underneath it.
 * - A `text` layer has no bitmap; it is laid out by {@link layoutText} and drawn by `fillText` /
 *   `strokeText`.
 *
 * ## Known limits
 *
 * - A text layer with a stroke at opacity below 1 shows the stroke through the fill where they
 *   overlap, because opacity is applied per draw call rather than to a flattened text group.
 * - A matte is applied through its alpha channel (that is what `destination-in` reads). The server
 *   serves mattes as opaque grayscale PNGs, so the loader turns luminance into alpha first.
 */

import type { BlendMode, TextContent } from '@/lib/types/image-document';
import type { EditorLayer } from './model';

export interface Point {
  x: number;
  y: number;
}

export interface Size {
  w: number;
  h: number;
}

/** Anything the context can `drawImage`, with a pixel size. */
export type Bitmap = (HTMLImageElement | ImageBitmap | HTMLCanvasElement | OffscreenCanvas) & {
  readonly width: number;
  readonly height: number;
};

/** The decoded pixels of one layer, once loaded. A text layer never has any. */
export interface LayerBitmaps {
  image?: Bitmap;
  matte?: Bitmap;
}

export type BitmapMap = ReadonlyMap<string, LayerBitmaps>;

/** An offscreen drawing surface the compositor can mask on. `null` = none available. */
export type ScratchFactory = (
  width: number,
  height: number,
) => { canvas: Bitmap; ctx: CanvasRenderingContext2D } | null;

/** Width of `text` in CSS pixels when set in `font` (a full CSS font shorthand). */
export type TextMeasurer = (font: string, text: string) => number;

/** `normal` is `source-over`; the rest are the canvas operation of the same name. */
export const BLEND_TO_COMPOSITE: Record<BlendMode, GlobalCompositeOperation> = {
  normal: 'source-over',
  multiply: 'multiply',
  screen: 'screen',
  overlay: 'overlay',
  darken: 'darken',
  lighten: 'lighten',
  'soft-light': 'soft-light',
  'hard-light': 'hard-light',
  difference: 'difference',
};

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Text
// ─────────────────────────────────────────────────────────────────────────────────────────────

export interface TextLine {
  text: string;
  /** Measured width including letter spacing. */
  width: number;
}

export interface TextLayout {
  font: string;
  lines: TextLine[];
  /** The layer's natural box: `maxWidth` when set, otherwise the widest line. */
  width: number;
  height: number;
  lineHeightPx: number;
}

/** The CSS font shorthand for a text layer. The family is quoted so "Chakra Petch" parses. */
export function fontString(content: Pick<TextContent, 'fontWeight' | 'fontSize' | 'fontFamily'>): string {
  const family = content.fontFamily.replace(/"/g, '');
  return `${content.fontWeight} ${content.fontSize}px "${family}", sans-serif`;
}

/** A measurer over a real 2D context (it mutates `ctx.font`, so give it a scratch one). */
export function makeTextMeasurer(ctx: CanvasRenderingContext2D): TextMeasurer {
  return (font, text) => {
    ctx.font = font;
    return ctx.measureText(text).width;
  };
}

/**
 * Lays text out: uppercase applied, hard line breaks kept, words greedily wrapped to `maxWidth`
 * when one is set. A single word wider than `maxWidth` keeps its own line rather than being split.
 */
export function layoutText(content: TextContent, measure: TextMeasurer): TextLayout {
  const font = fontString(content);
  const spacing = content.letterSpacing || 0;
  const widthOf = (s: string) => (s.length === 0 ? 0 : measure(font, s) + spacing * s.length);
  const source = content.uppercase ? content.text.toUpperCase() : content.text;
  const limit = content.maxWidth && content.maxWidth > 0 ? content.maxWidth : null;

  const lines: TextLine[] = [];
  for (const paragraph of source.split('\n')) {
    if (limit === null || paragraph.length === 0) {
      lines.push({ text: paragraph, width: widthOf(paragraph) });
      continue;
    }
    let current = '';
    for (const word of paragraph.split(' ')) {
      const candidate = current.length === 0 ? word : `${current} ${word}`;
      if (current.length > 0 && widthOf(candidate) > limit) {
        lines.push({ text: current, width: widthOf(current) });
        current = word;
      } else {
        current = candidate;
      }
    }
    lines.push({ text: current, width: current.length === 0 ? 0 : widthOf(current) });
  }

  const lineHeightPx = content.fontSize * content.lineHeight;
  const widest = lines.reduce((m, l) => Math.max(m, l.width), 0);
  return {
    font,
    lines,
    width: Math.max(1, limit ?? widest),
    height: Math.max(1, lines.length * lineHeightPx),
    lineHeightPx,
  };
}

/** Draws a laid-out text layer with its top-left at (`left`, `top`) in the current space. */
function drawText(
  ctx: CanvasRenderingContext2D,
  layout: TextLayout,
  content: TextContent,
  left: number,
  top: number,
): void {
  ctx.font = layout.font;
  ctx.textBaseline = 'top';
  ctx.textAlign = content.align;
  ctx.lineJoin = 'round';
  if ('letterSpacing' in ctx) {
    (ctx as unknown as { letterSpacing: string }).letterSpacing = `${content.letterSpacing || 0}px`;
  }

  const stroked = !!content.stroke && content.stroke.width > 0;
  const x =
    content.align === 'left' ? left : content.align === 'center' ? left + layout.width / 2 : left + layout.width;
  const inset = (layout.lineHeightPx - content.fontSize) / 2;

  const paint = (kind: 'stroke' | 'fill', withShadow: boolean) => {
    if (withShadow && content.shadow) {
      ctx.shadowColor = content.shadow.color;
      ctx.shadowBlur = content.shadow.blur;
      ctx.shadowOffsetX = content.shadow.offsetX;
      ctx.shadowOffsetY = content.shadow.offsetY;
    } else {
      ctx.shadowColor = 'rgba(0,0,0,0)';
      ctx.shadowBlur = 0;
      ctx.shadowOffsetX = 0;
      ctx.shadowOffsetY = 0;
    }
    layout.lines.forEach((line, i) => {
      if (line.text.length === 0) return;
      const y = top + i * layout.lineHeightPx + inset;
      if (kind === 'stroke') ctx.strokeText(line.text, x, y);
      else ctx.fillText(line.text, x, y);
    });
  };

  if (stroked && content.stroke) {
    // An OUTER stroke: draw twice the width underneath, then the fill over the inner half. The
    // shadow rides on the first pass so it is cast once, not once per pass.
    ctx.strokeStyle = content.stroke.color;
    ctx.lineWidth = content.stroke.width * 2;
    paint('stroke', true);
    ctx.fillStyle = content.color;
    paint('fill', false);
  } else {
    ctx.fillStyle = content.color;
    paint('fill', true);
  }
  ctx.shadowColor = 'rgba(0,0,0,0)';
  ctx.shadowBlur = 0;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Placement: a layer plus the size it occupies at scale 1
// ─────────────────────────────────────────────────────────────────────────────────────────────

/** An {@link EditorLayer} whose natural size is known, which is what drawing and hit-testing need. */
export interface PlacedLayer extends EditorLayer {
  /** Pixel size at scale 1: the decoded bitmap, or the laid-out text box. */
  size: Size;
}

/**
 * Resolves each layer's natural size and drops the ones that have none yet — a bitmap that is still
 * loading (and whose DTO states no size) is simply not drawn, and appears when its image arrives.
 */
export function placeLayers(
  layers: readonly EditorLayer[],
  bitmaps: BitmapMap,
  measure: TextMeasurer,
): PlacedLayer[] {
  const placed: PlacedLayer[] = [];
  for (const layer of layers) {
    let size: Size | null = null;
    if (layer.kind === 'text') {
      if (layer.content) {
        const layout = layoutText(layer.content, measure);
        size = { w: layout.width, h: layout.height };
      }
    } else {
      const image = bitmaps.get(layer.id)?.image;
      if (image && image.width > 0 && image.height > 0) size = { w: image.width, h: image.height };
      else if (layer.width && layer.height) size = { w: layer.width, h: layer.height };
      // A cropped layer occupies only its kept part; the stored bitmap stays whole.
      if (size && layer.crop) size = { w: size.w * layer.crop.w, h: size.h * layer.crop.h };
    }
    if (size) placed.push({ ...layer, size });
  }
  return placed;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Geometry
// ─────────────────────────────────────────────────────────────────────────────────────────────

const RAD = Math.PI / 180;

/** The point the layer rotates and flips about. */
export function layerCenter(layer: Pick<PlacedLayer, 'transform' | 'size'>): Point {
  const { transform: t, size } = layer;
  return { x: t.x + (size.w * t.scale) / 2, y: t.y + (size.h * t.scale) / 2 };
}

export interface LayerBounds {
  center: Point;
  /** Width and height on the canvas (size × scale), before rotation. */
  width: number;
  height: number;
  /** tl, tr, br, bl — after rotation, in canvas pixels. */
  corners: [Point, Point, Point, Point];
  /** The axis-aligned box around `corners`. */
  aabb: { x: number; y: number; w: number; h: number };
}

function rotateAbout(p: Point, c: Point, degrees: number): Point {
  const cos = Math.cos(degrees * RAD);
  const sin = Math.sin(degrees * RAD);
  const dx = p.x - c.x;
  const dy = p.y - c.y;
  return { x: c.x + dx * cos - dy * sin, y: c.y + dx * sin + dy * cos };
}

/** Where a placed layer actually is on the canvas: its centre, its rotated corners and their box. */
export function layerBounds(layer: Pick<PlacedLayer, 'transform' | 'size'>): LayerBounds {
  const { transform: t, size } = layer;
  const width = size.w * t.scale;
  const height = size.h * t.scale;
  const center = layerCenter(layer);
  const corners = [
    { x: t.x, y: t.y },
    { x: t.x + width, y: t.y },
    { x: t.x + width, y: t.y + height },
    { x: t.x, y: t.y + height },
  ].map((p) => rotateAbout(p, center, t.rotation)) as LayerBounds['corners'];
  const xs = corners.map((c) => c.x);
  const ys = corners.map((c) => c.y);
  const minX = Math.min(...xs);
  const minY = Math.min(...ys);
  return {
    center,
    width,
    height,
    corners,
    aabb: { x: minX, y: minY, w: Math.max(...xs) - minX, h: Math.max(...ys) - minY },
  };
}

/**
 * A canvas point in the layer's own pixel space (0..size.w, 0..size.h from its top-left, in the
 * layer's UNFLIPPED, UNSCALED frame) — the inverse of the draw transform. This is what a brush
 * stroke or a subject click must be converted through to land on the right bitmap pixel.
 */
export function canvasToLayerLocal(layer: Pick<PlacedLayer, 'transform' | 'size'>, point: Point): Point {
  const t = layer.transform;
  const c = layerCenter(layer);
  const r = rotateAbout(point, c, -t.rotation);
  const dx = (r.x - c.x) / t.scale;
  const dy = (r.y - c.y) / t.scale;
  return {
    x: (t.flipX ? -dx : dx) + layer.size.w / 2,
    y: (t.flipY ? -dy : dy) + layer.size.h / 2,
  };
}

/** The forward transform of {@link canvasToLayerLocal}. */
export function layerLocalToCanvas(layer: Pick<PlacedLayer, 'transform' | 'size'>, local: Point): Point {
  const t = layer.transform;
  const c = layerCenter(layer);
  const dx = (local.x - layer.size.w / 2) * (t.flipX ? -1 : 1) * t.scale;
  const dy = (local.y - layer.size.h / 2) * (t.flipY ? -1 : 1) * t.scale;
  return rotateAbout({ x: c.x + dx, y: c.y + dy }, c, t.rotation);
}

export interface HitTestOptions {
  /** Count hidden layers as hit (default false). */
  includeHidden?: boolean;
}

/**
 * The top-most layer under `point`, or null. A layer is a hit when the point is inside its rotated
 * bounds — a bounding-box test, not a pixel test, so clicking a transparent corner of a cut-out
 * still selects it. Locked layers are hit (they can be selected, just not moved).
 */
export function hitTest(
  layers: readonly PlacedLayer[],
  point: Point,
  options: HitTestOptions = {},
): PlacedLayer | null {
  const ordered = [...layers].sort((a, b) => b.zOrder - a.zOrder);
  for (const layer of ordered) {
    if (!layer.visible && !options.includeHidden) continue;
    const local = canvasToLayerLocal(layer, point);
    if (local.x >= 0 && local.x <= layer.size.w && local.y >= 0 && local.y <= layer.size.h) {
      return layer;
    }
  }
  return null;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Drawing
// ─────────────────────────────────────────────────────────────────────────────────────────────

/** Moves the context to the layer's centre, rotates, then scales with flips: draw around (0,0). */
export function applyLayerTransform(
  ctx: CanvasRenderingContext2D,
  layer: Pick<PlacedLayer, 'transform' | 'size'>,
): void {
  const t = layer.transform;
  const c = layerCenter(layer);
  ctx.translate(c.x, c.y);
  ctx.rotate(t.rotation * RAD);
  ctx.scale(t.scale * (t.flipX ? -1 : 1), t.scale * (t.flipY ? -1 : 1));
}

const clamp01 = (n: number) => (Number.isFinite(n) ? Math.min(1, Math.max(0, n)) : 1);

export interface ComposeScene {
  /** The composition size in pixels. */
  width: number;
  height: number;
  /** CSS colour under the lowest layer, or null for transparency. */
  backgroundColor: string | null;
  layers: readonly PlacedLayer[];
  bitmaps: BitmapMap;
}

export interface ComposeOptions {
  /** Needed to mask a layer by its matte; without it the matte is skipped. */
  scratch?: ScratchFactory;
  /** Layer ids to leave out (the layer under an overlay editor, for instance). */
  skip?: ReadonlySet<string>;
}

export interface DrawLayerOptions extends ComposeOptions {
  /**
   * Draw the layer's pixels only, at full opacity and with the normal blend: what a layered export
   * (Photoshop, OpenRaster) stores, since those files keep opacity and blend as layer settings.
   */
  pixelsOnly?: boolean;
}

/** The pixels of `image` a crop keeps (all of it when uncropped). */
export function cropSourceRect(
  crop: { x: number; y: number; w: number; h: number } | null | undefined,
  image: { width: number; height: number },
): { x: number; y: number; w: number; h: number } {
  if (!crop) return { x: 0, y: 0, w: image.width, h: image.height };
  return { x: crop.x * image.width, y: crop.y * image.height, w: crop.w * image.width, h: crop.h * image.height };
}

/**
 * Draws one layer through its transform. Exported so a layered export can rasterise each layer on
 * its own surface with exactly the drawing the screen and the flat export use.
 */
export function drawLayer(
  ctx: CanvasRenderingContext2D,
  layer: PlacedLayer,
  bitmaps: BitmapMap,
  options: DrawLayerOptions = {},
): void {
  const { w, h } = layer.size;
  const pixels = bitmaps.get(layer.id);
  if (layer.kind !== 'text' && !pixels?.image) return; // still loading: draw nothing, not a placeholder

  ctx.save();
  ctx.globalAlpha = options.pixelsOnly ? 1 : clamp01(layer.opacity);
  ctx.globalCompositeOperation = options.pixelsOnly
    ? 'source-over'
    : (BLEND_TO_COMPOSITE[layer.blendMode] ?? 'source-over');
  applyLayerTransform(ctx, layer);

  if (layer.kind === 'text') {
    if (layer.content) {
      const measure: TextMeasurer = (font, text) => {
        ctx.font = font;
        return ctx.measureText(text).width;
      };
      drawText(ctx, layoutText(layer.content, measure), layer.content, -w / 2, -h / 2);
    }
  } else if (pixels?.image) {
    const matte = pixels.matte;
    const scratch = matte ? options.scratch?.(Math.ceil(w), Math.ceil(h)) : null;
    // The kept window of the bitmap (the whole bitmap when uncropped); the matte shares its frame.
    const src = layer.crop ? cropSourceRect(layer.crop, pixels.image) : null;
    if (matte && scratch) {
      if (src) scratch.ctx.drawImage(pixels.image, src.x, src.y, src.w, src.h, 0, 0, w, h);
      else scratch.ctx.drawImage(pixels.image, 0, 0, w, h);
      scratch.ctx.globalCompositeOperation = 'destination-in';
      const m = layer.crop ? cropSourceRect(layer.crop, matte) : null;
      if (m) scratch.ctx.drawImage(matte, m.x, m.y, m.w, m.h, 0, 0, w, h);
      else scratch.ctx.drawImage(matte, 0, 0, w, h);
      scratch.ctx.globalCompositeOperation = 'source-over';
      ctx.drawImage(scratch.canvas, -w / 2, -h / 2, w, h);
    } else if (src) {
      ctx.drawImage(pixels.image, src.x, src.y, src.w, src.h, -w / 2, -h / 2, w, h);
    } else {
      ctx.drawImage(pixels.image, -w / 2, -h / 2, w, h);
    }
  }
  ctx.restore();
}

/**
 * Draws an editor-only overlay (the brush mask's tint) over one layer, through that layer's own
 * transform, so a stroke painted in the layer's pixels lands on the layer's pixels wherever the
 * layer has been moved, scaled, rotated or flipped. Never part of the export.
 */
export function drawOverlayOnLayer(
  ctx: CanvasRenderingContext2D,
  layer: PlacedLayer,
  overlay: Bitmap,
  alpha: number,
): void {
  ctx.save();
  ctx.globalAlpha = clamp01(alpha);
  applyLayerTransform(ctx, layer);
  ctx.drawImage(overlay, -layer.size.w / 2, -layer.size.h / 2, layer.size.w, layer.size.h);
  ctx.restore();
}

/**
 * Draws the whole composition: background, then every visible layer back to front. Leaves the
 * context's state as it found it.
 */
export function composeScene(
  ctx: CanvasRenderingContext2D,
  scene: ComposeScene,
  options: ComposeOptions = {},
): void {
  ctx.save();
  ctx.globalAlpha = 1;
  ctx.globalCompositeOperation = 'source-over';
  ctx.clearRect(0, 0, scene.width, scene.height);
  if (scene.backgroundColor) {
    ctx.fillStyle = scene.backgroundColor;
    ctx.fillRect(0, 0, scene.width, scene.height);
  }
  composeLayers(ctx, scene.layers, scene.bitmaps, options);
  ctx.restore();
}

/**
 * Draws every visible layer back to front in the context's CURRENT space, with no clearing and no
 * background and no clipping: the editor's workspace uses this to draw layers that reach past the
 * frame, beside it, exactly as the export would draw the part inside it.
 */
export function composeLayers(
  ctx: CanvasRenderingContext2D,
  layers: readonly PlacedLayer[],
  bitmaps: BitmapMap,
  options: ComposeOptions = {},
): void {
  const ordered = [...layers].sort((a, b) => a.zOrder - b.zOrder);
  for (const layer of ordered) {
    if (!layer.visible) continue;
    if (options.skip?.has(layer.id)) continue;
    drawLayer(ctx, layer, bitmaps, options);
  }
}
