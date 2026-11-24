/**
 * The brush mask tool's data: an offscreen canvas at the TARGET LAYER'S bitmap resolution, painted
 * white on black, exported as a PNG for `erase` / `fill` (white = the region to act on).
 *
 * Two canvases are kept in step. The *mask* is what gets exported: opaque, white where the user
 * painted and black everywhere else, because that is the polarity the vendors take (BFL fill: black
 * preserved, white inpainted). The *tint* is what the user sees: the same strokes in translucent red
 * on a transparent canvas, drawn over the layer in the editor. They are separate so the export never
 * contains the display colour and the display never has to read the export back.
 *
 * No DOM at module level: the canvas factory and the PNG encoder are arguments, so this runs under
 * a recording canvas in a test.
 */

import type { Point } from './compose';

/** The slice of an HTMLCanvasElement / OffscreenCanvas the painter touches. */
export interface MaskCanvas {
  width: number;
  height: number;
  getContext(type: '2d'): CanvasRenderingContext2D | null;
  toBlob?(callback: (blob: Blob | null) => void, type?: string): void;
}

export type MaskCanvasFactory = (width: number, height: number) => MaskCanvas;

/** Encodes a canvas as a PNG. The default uses `canvas.toBlob`; a test injects its own. */
export type PngEncoder = (canvas: MaskCanvas) => Promise<Blob>;

export const encodeCanvasAsPng: PngEncoder = (canvas) =>
  new Promise<Blob>((resolve, reject) => {
    if (!canvas.toBlob) {
      reject(new Error('This browser cannot export a canvas as an image.'));
      return;
    }
    canvas.toBlob((blob) => {
      if (blob) resolve(blob);
      else reject(new Error('The mask could not be turned into an image.'));
    }, 'image/png');
  });

export interface Brush {
  /** Diameter in LAYER pixels (the bitmap's own pixels, not canvas pixels). */
  size: number;
  /** True to paint black again (take area out of the mask). */
  erase: boolean;
}

export const BRUSH_MIN = 1;
export const BRUSH_MAX = 800;

/** A brush diameter chosen in canvas pixels, converted to the layer's pixels at its current scale. */
export function brushSizeInLayerPixels(canvasPixels: number, layerScale: number): number {
  const size = canvasPixels / (layerScale > 0 ? layerScale : 1);
  return Math.min(BRUSH_MAX, Math.max(BRUSH_MIN, Math.round(size * 10) / 10));
}

/** The red the tint is painted in; the editor draws the tint canvas at partial alpha. */
export const TINT_COLOR = 'rgb(255, 64, 64)';

export class MaskPainter {
  readonly width: number;
  readonly height: number;
  readonly mask: MaskCanvas;
  readonly tint: MaskCanvas;
  private readonly maskCtx: CanvasRenderingContext2D;
  private readonly tintCtx: CanvasRenderingContext2D;
  private painted = false;
  private box: { minX: number; minY: number; maxX: number; maxY: number } | null = null;

  constructor(factory: MaskCanvasFactory, width: number, height: number) {
    this.width = Math.max(1, Math.round(width));
    this.height = Math.max(1, Math.round(height));
    this.mask = factory(this.width, this.height);
    this.tint = factory(this.width, this.height);
    const maskCtx = this.mask.getContext('2d');
    const tintCtx = this.tint.getContext('2d');
    if (!maskCtx || !tintCtx) throw new Error('This browser cannot draw a mask.');
    this.maskCtx = maskCtx;
    this.tintCtx = tintCtx;
    this.reset();
  }

  /** True when nothing has been painted (an export of this would send an all-black mask). */
  get isEmpty(): boolean {
    return !this.painted;
  }

  private reset(): void {
    this.maskCtx.globalCompositeOperation = 'source-over';
    this.maskCtx.fillStyle = '#000000';
    this.maskCtx.fillRect(0, 0, this.width, this.height);
    this.tintCtx.clearRect(0, 0, this.width, this.height);
  }

  clear(): void {
    this.reset();
    this.painted = false;
    this.box = null;
  }

  /**
   * The rectangle around everything painted so far, in layer pixels and clamped to the layer, or null
   * when nothing is. Rubbing out does not shrink it: it is the area an operation MAY change, which is
   * what the canvas blurs while erase or fill runs.
   */
  get paintedBounds(): { x: number; y: number; w: number; h: number } | null {
    if (!this.box || !this.painted) return null;
    const x = Math.max(0, Math.floor(this.box.minX));
    const y = Math.max(0, Math.floor(this.box.minY));
    const right = Math.min(this.width, Math.ceil(this.box.maxX));
    const bottom = Math.min(this.height, Math.ceil(this.box.maxY));
    if (right <= x || bottom <= y) return null;
    return { x, y, w: right - x, h: bottom - y };
  }

  /** Paints a round-capped segment from `from` to `to`, in layer pixels. A zero-length one is a dot. */
  stroke(from: Point, to: Point, brush: Brush): void {
    const radius = Math.max(BRUSH_MIN, brush.size) / 2;
    const paint = (ctx: CanvasRenderingContext2D, color: string, operation: GlobalCompositeOperation) => {
      ctx.globalCompositeOperation = operation;
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = radius * 2;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.beginPath();
      ctx.arc(from.x, from.y, radius, 0, Math.PI * 2);
      ctx.fill();
      if (from.x !== to.x || from.y !== to.y) {
        ctx.beginPath();
        ctx.moveTo(from.x, from.y);
        ctx.lineTo(to.x, to.y);
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(to.x, to.y, radius, 0, Math.PI * 2);
        ctx.fill();
      }
    };
    paint(this.maskCtx, brush.erase ? '#000000' : '#ffffff', 'source-over');
    paint(this.tintCtx, TINT_COLOR, brush.erase ? 'destination-out' : 'source-over');
    if (!brush.erase) {
      this.painted = true;
      const minX = Math.min(from.x, to.x) - radius;
      const minY = Math.min(from.y, to.y) - radius;
      const maxX = Math.max(from.x, to.x) + radius;
      const maxY = Math.max(from.y, to.y) + radius;
      this.box = this.box
        ? {
            minX: Math.min(this.box.minX, minX),
            minY: Math.min(this.box.minY, minY),
            maxX: Math.max(this.box.maxX, maxX),
            maxY: Math.max(this.box.maxY, maxY),
          }
        : { minX, minY, maxX, maxY };
    }
  }

  /**
   * The white-on-black PNG. Throws a plain-language error when nothing is painted — an all-black mask
   * tells the vendor "change nothing", which would spend money on a no-op.
   */
  async toPng(encode: PngEncoder = encodeCanvasAsPng): Promise<Blob> {
    if (this.isEmpty) throw new MaskEmptyError();
    return encode(this.mask);
  }
}

export class MaskEmptyError extends Error {
  constructor() {
    super('Paint over the area you want to change first.');
    this.name = 'MaskEmptyError';
  }
}
