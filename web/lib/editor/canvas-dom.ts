/**
 * The browser-only half of the thumbnail editor: the pieces `compose.ts` and `mask.ts` take as
 * arguments, built from the real DOM. Everything here touches `document` / `window`, which is why
 * it is a separate file — the pure modules never import it, and a test that needs a recording
 * canvas never has to stub the DOM around it.
 */

import {
  composeLayers,
  composeScene,
  drawLayer,
  type PlacedLayer,
  makeTextMeasurer,
  placeLayers,
  type Bitmap,
  type BitmapMap,
  type ScratchFactory,
} from './compose';
import { LayerMaskBuffer, lumaToAlphaInPlace } from './layer-mask';
import type { MaskCanvas, MaskCanvasFactory } from './mask';
import type { EditorDocState } from './model';
import { aiLabelLayout, aiLabelStyle, type AiLabelLayout } from './ai-label';
import { exportFormat, OPAQUE_FALLBACK_BACKGROUND, type ExportFormatId } from './export-formats';
import {
  buildOra,
  encodePsd,
  oraThumbnailSize,
  rasterBounds,
  type LayerRaster,
} from './layered-export';
import { findFont, googleFontsHref, type EditorFont } from './text-content';

/**
 * Decodes an image URL (same-origin, so the canvas stays untainted and can be exported). A `matte` is
 * served as a grayscale PNG (luminance = visible amount): it comes back as a canvas whose ALPHA is
 * that amount, which is what `destination-in` reads.
 */
export async function loadBitmap(url: string, options?: { matte?: boolean }): Promise<Bitmap> {
  const image = await new Promise<HTMLImageElement>((resolve, reject) => {
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('A layer image could not be loaded.'));
    img.src = url;
  });
  if (!options?.matte) return image;
  const canvas = createCanvas(image.naturalWidth, image.naturalHeight);
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('A layer image could not be loaded.');
  ctx.drawImage(image, 0, 0);
  const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
  lumaToAlphaInPlace(pixels.data);
  ctx.putImageData(pixels, 0, 0);
  return canvas;
}

/** A matte bitmap (alpha = visible amount) drawn as black-and-white: white is shown, black is hidden. */
export function matteAsGray(matte: Bitmap): Bitmap {
  const canvas = createCanvas(matte.width, matte.height);
  const ctx = canvas.getContext('2d');
  if (ctx) {
    ctx.fillStyle = '#000000';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(matte, 0, 0);
  }
  return canvas;
}

/** Reads a matte bitmap (alpha = visible amount) back into an editable mask. */
export function maskFromMatte(matte: Bitmap): LayerMaskBuffer | null {
  const canvas = createCanvas(matte.width, matte.height);
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(matte, 0, 0);
  const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
  return LayerMaskBuffer.fromAlphaRgba(canvas.width, canvas.height, pixels.data);
}

/** A mask's grayscale PNG, ready to upload. */
export function maskToPng(mask: LayerMaskBuffer): Promise<Blob> {
  const canvas = createCanvas(mask.width, mask.height);
  const ctx = canvas.getContext('2d');
  if (!ctx) return Promise.reject(new Error('This browser cannot draw a mask.'));
  ctx.putImageData(new ImageData(mask.toGrayRgba() as unknown as Uint8ClampedArray<ArrayBuffer>, mask.width, mask.height), 0, 0);
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('The mask could not be turned into an image.'))), 'image/png');
  });
}

/**
 * What the editor shows while a mask is being painted: a canvas with the visible amount as alpha (the
 * live matte) and a black-and-white one (for "Show mask only"), both redrawn only where paint landed.
 */
export class MaskSurface {
  readonly alpha: HTMLCanvasElement;
  readonly gray: HTMLCanvasElement;

  constructor(private readonly mask: LayerMaskBuffer) {
    this.alpha = createCanvas(mask.width, mask.height);
    this.gray = createCanvas(mask.width, mask.height);
    mask.takeDirty();
    this.draw({ x: 0, y: 0, w: mask.width, h: mask.height });
  }

  /** Brings the canvases up to date with the mask. Cheap when little changed. */
  sync(): void {
    const rect = this.mask.takeDirty();
    if (rect) this.draw(rect);
  }

  private draw(rect: { x: number; y: number; w: number; h: number }): void {
    const ctx = this.alpha.getContext('2d');
    const grayCtx = this.gray.getContext('2d');
    if (!ctx || !grayCtx) return;
    const pixels = new ImageData(this.mask.toAlphaRgba(rect) as unknown as Uint8ClampedArray<ArrayBuffer>, rect.w, rect.h);
    ctx.clearRect(rect.x, rect.y, rect.w, rect.h);
    ctx.putImageData(pixels, rect.x, rect.y);
    grayCtx.fillStyle = '#000000';
    grayCtx.fillRect(rect.x, rect.y, rect.w, rect.h);
    grayCtx.drawImage(this.alpha, rect.x, rect.y, rect.w, rect.h, rect.x, rect.y, rect.w, rect.h);
  }
}

function createCanvas(width: number, height: number): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(width));
  canvas.height = Math.max(1, Math.round(height));
  return canvas;
}

export const domScratch: ScratchFactory = (width, height) => {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext('2d');
  return ctx ? { canvas, ctx } : null;
};

export const domMaskCanvas: MaskCanvasFactory = (width, height) => createCanvas(width, height) as MaskCanvas;

let measureCtx: CanvasRenderingContext2D | null = null;

/** A text measurer over one shared scratch context (created on first use). */
export function domTextMeasurer() {
  if (!measureCtx) measureCtx = createCanvas(1, 1).getContext('2d');
  if (!measureCtx) return () => 0;
  return makeTextMeasurer(measureCtx);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Fonts
// ─────────────────────────────────────────────────────────────────────────────────────────────

const requestedFamilies = new Set<string>();

/**
 * Loads a curated font from Google Fonts on demand: one `<link rel=stylesheet>` per family (added
 * once), then `document.fonts.load` so the canvas can use it. Resolves when the face is ready, and
 * also when loading fails — the text then simply draws in the fallback, and the editor must not be
 * stuck behind a stylesheet that never arrived.
 */
export async function ensureFont(font: EditorFont, weight: number): Promise<void> {
  if (typeof document === 'undefined') return;
  if (!requestedFamilies.has(font.family)) {
    requestedFamilies.add(font.family);
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = googleFontsHref(font);
    link.dataset.reelboltEditorFont = font.family;
    document.head.appendChild(link);
  }
  try {
    await document.fonts.load(`${weight} 32px "${font.family}"`);
  } catch {
    /* fall back silently */
  }
}

/** Loads whichever curated fonts a set of text layers use. Unknown families are left to the system. */
export async function ensureFontsFor(contents: readonly { fontFamily: string; fontWeight: number }[]): Promise<void> {
  const jobs: Promise<void>[] = [];
  for (const c of contents) {
    const font = findFont(c.fontFamily);
    if (font) jobs.push(ensureFont(font, c.fontWeight));
  }
  await Promise.all(jobs);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Export
// ─────────────────────────────────────────────────────────────────────────────────────────────

/** The flattened picture at frame size on a fresh canvas. `background` overrides the document's. */
function renderComposition(
  state: EditorDocState,
  bitmaps: BitmapMap,
  background?: string | null,
): HTMLCanvasElement {
  const canvas = createCanvas(state.canvasWidth, state.canvasHeight);
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('This browser cannot draw the picture.');
  composeScene(
    ctx,
    {
      width: state.canvasWidth,
      height: state.canvasHeight,
      backgroundColor: background === undefined ? state.backgroundColor : background,
      layers: placeLayers(state.layers, bitmaps, domTextMeasurer()),
      bitmaps,
    },
    { scratch: domScratch },
  );
  return canvas;
}

/** What an export adds on top of the document's own layers. */
export interface RenderExportOptions {
  /**
   * The words of the "AI-generated" label, drawn small in the bottom-right corner (white on a dark rounded box).
   * The caller decides whether one is needed (`needsAiLabel`); null or absent draws nothing.
   */
  aiLabel?: string | null;
}

const AI_LABEL_FONT_FAMILY = 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif';

/**
 * Draws the label onto a finished picture, at the size and place `aiLabelLayout` gives for it. Answers the box
 * it drew (canvas pixels), or null when this browser cannot draw.
 */
export function drawAiLabel(canvas: HTMLCanvasElement, text: string): AiLabelLayout | null {
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  const style = aiLabelStyle(canvas.width, canvas.height);
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalAlpha = 1;
  ctx.globalCompositeOperation = 'source-over';
  ctx.font = `600 ${style.fontSize}px ${AI_LABEL_FONT_FAMILY}`;
  const box = aiLabelLayout(canvas.width, canvas.height, ctx.measureText(text).width, style);
  ctx.fillStyle = 'rgba(0, 0, 0, 0.62)';
  ctx.beginPath();
  if (typeof ctx.roundRect === 'function') ctx.roundRect(box.x, box.y, box.width, box.height, box.radius);
  else ctx.rect(box.x, box.y, box.width, box.height);
  ctx.fill();
  ctx.font = `600 ${box.fontSize}px ${AI_LABEL_FONT_FAMILY}`;
  ctx.fillStyle = '#ffffff';
  ctx.textAlign = 'left';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, box.textX, box.textY);
  ctx.restore();
  return box;
}

/**
 * A copy of one layer's own picture at `width` x `height`, as a PNG - what an AI service gets instead of
 * the original when the original is larger than it accepts (the server names the size: the size the layer
 * appears in the design). Only the bitmap is drawn: no matte, no transform, no other layer, because the
 * server sends exactly this layer's pixels in the original's place. The browser draws a JPEG turned by its
 * EXIF orientation, which is also how the server measured it.
 */
export function renderScaledCopy(image: Bitmap, width: number, height: number): Promise<Blob> {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext('2d');
  if (!ctx) return Promise.reject(new Error('This browser cannot draw the picture.'));
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(image as CanvasImageSource, 0, 0, canvas.width, canvas.height);
  return encodeCanvas(canvas, 'image/png');
}

/** The picked layers as they look together (opacity, blend, order) inside `region`, as a transparent PNG. */
export function renderMergedLayers(
  layers: readonly PlacedLayer[],
  bitmaps: BitmapMap,
  region: { x: number; y: number; w: number; h: number },
  factor = 1,
): Promise<Blob> {
  const canvas = createCanvas(Math.max(1, Math.round(region.w * factor)), Math.max(1, Math.round(region.h * factor)));
  const ctx = canvas.getContext('2d');
  if (!ctx) return Promise.reject(new Error('This browser cannot draw the picture.'));
  ctx.scale(factor, factor);
  ctx.translate(-region.x, -region.y);
  composeLayers(ctx, layers, bitmaps, { scratch: domScratch });
  return encodeCanvas(canvas, 'image/png');
}

/**
 * Encodes a canvas as `mime`. A browser that cannot make that type silently answers a PNG instead;
 * that is caught here and said plainly rather than saved under the wrong extension.
 */
function encodeCanvas(canvas: HTMLCanvasElement, mime: string, quality?: number): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => {
        if (!blob) reject(new Error('The picture could not be turned into a file.'));
        else if (blob.type !== mime) reject(new Error('This browser cannot make this kind of file. Choose another format.'));
        else resolve(blob);
      },
      mime,
      quality,
    );
  });
}

const encodable = new Map<string, Promise<boolean>>();

/** Whether this browser can encode `mime` from a canvas (AVIF is the one that varies). Cached. */
export function canEncode(mime: string): Promise<boolean> {
  if (typeof document === 'undefined') return Promise.resolve(false);
  let known = encodable.get(mime);
  if (!known) {
    known = new Promise<boolean>((resolve) => {
      try {
        const canvas = createCanvas(2, 2);
        if (!canvas.toBlob) {
          resolve(false);
          return;
        }
        canvas.toBlob((blob) => resolve(!!blob && blob.type === mime), mime, 0.8);
      } catch {
        resolve(false);
      }
    });
    encodable.set(mime, known);
  }
  return known;
}

/**
 * Every layer rasterised on its own: its pixels through its transform (but at full opacity and the
 * normal blend, which the layered file stores as layer settings), cut to {@link rasterBounds}.
 * Hidden layers are included and marked hidden. Back to front.
 */
export function rasterizeForExport(state: EditorDocState, bitmaps: BitmapMap): LayerRaster[] {
  const frame = { width: state.canvasWidth, height: state.canvasHeight };
  const placed = placeLayers(state.layers, bitmaps, domTextMeasurer()).sort((a, b) => a.zOrder - b.zOrder);
  const rasters: LayerRaster[] = [];
  for (const layer of placed) {
    if (layer.kind !== 'text' && !bitmaps.get(layer.id)?.image) continue;
    const bounds = rasterBounds(layer, frame);
    if (!bounds) continue;
    const canvas = createCanvas(bounds.width, bounds.height);
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('This browser cannot draw the picture.');
    ctx.translate(-bounds.left, -bounds.top);
    drawLayer(ctx, layer, bitmaps, { scratch: domScratch, pixelsOnly: true });
    rasters.push({
      id: layer.id,
      name: layer.label,
      ...bounds,
      pixels: ctx.getImageData(0, 0, bounds.width, bounds.height).data,
      opacity: layer.opacity,
      blendMode: layer.blendMode,
      hidden: !layer.visible,
    });
  }
  // A background colour is a property of the editor's canvas, which neither layered format has:
  // it becomes the bottom layer, so the picture opens looking the same.
  if (state.backgroundColor) {
    const canvas = createCanvas(frame.width, frame.height);
    const ctx = canvas.getContext('2d');
    if (ctx) {
      ctx.fillStyle = state.backgroundColor;
      ctx.fillRect(0, 0, frame.width, frame.height);
      rasters.unshift({
        id: 'background',
        name: 'Background colour',
        left: 0,
        top: 0,
        width: frame.width,
        height: frame.height,
        pixels: ctx.getImageData(0, 0, frame.width, frame.height).data,
        opacity: 1,
        blendMode: 'normal',
        hidden: false,
      });
    }
  }
  return rasters;
}

function pixelsToCanvas(pixels: Uint8ClampedArray, width: number, height: number): HTMLCanvasElement {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('This browser cannot draw the picture.');
  ctx.putImageData(new ImageData(new Uint8ClampedArray(pixels), width, height), 0, 0);
  return canvas;
}

async function pngBytes(canvas: HTMLCanvasElement): Promise<Uint8Array> {
  const blob = await encodeCanvas(canvas, 'image/png');
  return new Uint8Array(await blob.arrayBuffer());
}

/** Copies bytes into a fresh ArrayBuffer-backed array, which is what `Blob` accepts everywhere. */
function asBlobPart(bytes: Uint8Array): ArrayBuffer {
  const copy = new Uint8Array(bytes.length);
  copy.set(bytes);
  return copy.buffer;
}

/**
 * Makes the export file for `format`. Flat formats are the frame, flattened; JPEG, which has no
 * transparency, is flattened onto the background colour or white. `quality` (0..1) applies to the
 * lossy formats only.
 */
export async function renderExport(
  format: ExportFormatId,
  state: EditorDocState,
  bitmaps: BitmapMap,
  quality?: number,
  options?: RenderExportOptions,
): Promise<Blob> {
  const info = exportFormat(format);
  const aiLabel = options?.aiLabel?.trim() || null;
  if (!info.layered) {
    const background = info.transparency ? undefined : (state.backgroundColor ?? OPAQUE_FALLBACK_BACKGROUND);
    const canvas = renderComposition(state, bitmaps, background);
    if (aiLabel) drawAiLabel(canvas, aiLabel);
    return encodeCanvas(canvas, info.mime, info.lossy ? quality : undefined);
  }

  const composite = renderComposition(state, bitmaps);
  if (aiLabel) drawAiLabel(composite, aiLabel);
  const compositeCtx = composite.getContext('2d');
  if (!compositeCtx) throw new Error('This browser cannot draw the picture.');
  const rasters = rasterizeForExport(state, bitmaps);
  // A layered file keeps the label as its own top layer, so the picture opens looking like the flat export.
  if (aiLabel) {
    const label = createCanvas(state.canvasWidth, state.canvasHeight);
    const box = drawAiLabel(label, aiLabel);
    const labelCtx = label.getContext('2d');
    if (box && labelCtx) {
      const left = Math.max(0, Math.floor(box.x));
      const top = Math.max(0, Math.floor(box.y));
      const width = Math.max(1, Math.min(state.canvasWidth - left, Math.ceil(box.x + box.width) - left));
      const height = Math.max(1, Math.min(state.canvasHeight - top, Math.ceil(box.y + box.height) - top));
      rasters.push({ id: 'ai-label', name: aiLabel, left, top, width, height, pixels: labelCtx.getImageData(left, top, width, height).data, opacity: 1, blendMode: 'normal', hidden: false });
    }
  }

  if (format === 'psd') {
    const bytes = await encodePsd({
      width: state.canvasWidth,
      height: state.canvasHeight,
      layers: rasters,
      composite: compositeCtx.getImageData(0, 0, state.canvasWidth, state.canvasHeight).data,
    });
    return new Blob([asBlobPart(bytes)], { type: info.mime });
  }

  const layers = [];
  for (const raster of rasters) {
    layers.push({
      name: raster.name,
      left: raster.left,
      top: raster.top,
      opacity: raster.opacity,
      blendMode: raster.blendMode,
      hidden: raster.hidden,
      png: await pngBytes(pixelsToCanvas(raster.pixels, raster.width, raster.height)),
    });
  }
  const thumbSize = oraThumbnailSize(state.canvasWidth, state.canvasHeight);
  const thumb = createCanvas(thumbSize.width, thumbSize.height);
  const thumbCtx = thumb.getContext('2d');
  if (!thumbCtx) throw new Error('This browser cannot draw the picture.');
  thumbCtx.drawImage(composite, 0, 0, thumbSize.width, thumbSize.height);
  const bytes = buildOra({
    width: state.canvasWidth,
    height: state.canvasHeight,
    layers,
    mergedPng: await pngBytes(composite),
    thumbnailPng: await pngBytes(thumb),
  });
  return new Blob([asBlobPart(bytes)], { type: info.mime });
}

/** Hands a file to the browser's downloads. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Revoked a moment later: some browsers start the download after the click handler returns.
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
