import { describe, expect, it, vi } from 'vitest';
import {
  BRUSH_MAX,
  BRUSH_MIN,
  MaskEmptyError,
  MaskPainter,
  TINT_COLOR,
  brushSizeInLayerPixels,
  encodeCanvasAsPng,
  type MaskCanvas,
} from './mask';
import { RecordingContext } from './__tests__/recording-context';

function factory() {
  const made: { canvas: MaskCanvas; ctx: RecordingContext }[] = [];
  const make = (width: number, height: number): MaskCanvas => {
    const ctx = new RecordingContext();
    const canvas: MaskCanvas = { width, height, getContext: () => ctx.as() };
    made.push({ canvas, ctx });
    return canvas;
  };
  return { make, made };
}

describe('MaskPainter', () => {
  it('creates two canvases at the layer bitmap resolution and starts the mask black', () => {
    const { make, made } = factory();
    const painter = new MaskPainter(make, 640, 360);
    expect(made).toHaveLength(2);
    expect(made.map((m) => [m.canvas.width, m.canvas.height])).toEqual([
      [640, 360],
      [640, 360],
    ]);
    const maskFill = made[0].ctx.of('fillRect')[0];
    expect(maskFill.fillStyle).toBe('#000000');
    expect(maskFill.args).toEqual([0, 0, 640, 360]);
    expect(made[1].ctx.of('clearRect')).toHaveLength(1);
    expect(painter.isEmpty).toBe(true);
  });

  it('paints white into the mask and the tint colour into the display canvas', () => {
    const { make, made } = factory();
    const painter = new MaskPainter(make, 100, 100);
    painter.stroke({ x: 10, y: 10 }, { x: 40, y: 10 }, { size: 20, erase: false });
    const maskStroke = made[0].ctx.of('stroke')[0];
    expect(maskStroke.strokeStyle).toBe('#ffffff');
    expect(maskStroke.lineWidth).toBe(20);
    expect(made[1].ctx.of('stroke')[0].strokeStyle).toBe(TINT_COLOR);
    expect(painter.isEmpty).toBe(false);
  });

  it('paints a lone click as a dot and a drag as a capsule', () => {
    const { make, made } = factory();
    const painter = new MaskPainter(make, 100, 100);
    painter.stroke({ x: 5, y: 5 }, { x: 5, y: 5 }, { size: 10, erase: false });
    expect(made[0].ctx.of('arc')).toHaveLength(1);
    expect(made[0].ctx.of('lineTo')).toHaveLength(0);
    painter.stroke({ x: 5, y: 5 }, { x: 50, y: 5 }, { size: 10, erase: false });
    expect(made[0].ctx.of('lineTo')).toHaveLength(1);
  });

  it('the eraser paints black in the mask and removes the tint, and does not make it non-empty', () => {
    const { make, made } = factory();
    const painter = new MaskPainter(make, 100, 100);
    painter.stroke({ x: 5, y: 5 }, { x: 9, y: 9 }, { size: 10, erase: true });
    expect(made[0].ctx.of('stroke')[0].strokeStyle).toBe('#000000');
    expect(made[1].ctx.of('stroke')[0].composite).toBe('destination-out');
    expect(painter.isEmpty).toBe(true);
  });

  it('clear() resets both canvases and the empty flag', () => {
    const { make, made } = factory();
    const painter = new MaskPainter(make, 50, 50);
    painter.stroke({ x: 1, y: 1 }, { x: 2, y: 2 }, { size: 4, erase: false });
    painter.clear();
    expect(painter.isEmpty).toBe(true);
    expect(made[0].ctx.of('fillRect')).toHaveLength(2);
  });

  it('exports through the injected encoder and returns its blob', async () => {
    const { make, made } = factory();
    const painter = new MaskPainter(make, 20, 20);
    painter.stroke({ x: 1, y: 1 }, { x: 5, y: 5 }, { size: 4, erase: false });
    const png = new Blob(['png'], { type: 'image/png' });
    const encode = vi.fn(async () => png);
    await expect(painter.toPng(encode)).resolves.toBe(png);
    expect(encode).toHaveBeenCalledWith(made[0].canvas); // the MASK canvas, never the tint
  });

  it('refuses to export an unpainted mask, with a plain message and without encoding', async () => {
    const painter = new MaskPainter(factory().make, 20, 20);
    const encode = vi.fn();
    await expect(painter.toPng(encode)).rejects.toBeInstanceOf(MaskEmptyError);
    await expect(painter.toPng(encode)).rejects.toThrow('Paint over the area you want to change first.');
    expect(encode).not.toHaveBeenCalled();
  });

  it('rounds a fractional resolution and never makes a zero-sized canvas', () => {
    const { make } = factory();
    const painter = new MaskPainter(make, 0.2, 99.6);
    expect([painter.width, painter.height]).toEqual([1, 100]);
  });

  it('throws a readable error when the browser gives no 2d context', () => {
    const noContext = (): MaskCanvas => ({ width: 1, height: 1, getContext: () => null });
    expect(() => new MaskPainter(noContext, 10, 10)).toThrow('This browser cannot draw a mask.');
  });
});

describe('encodeCanvasAsPng', () => {
  it('asks the canvas for image/png and resolves with the blob', async () => {
    const blob = new Blob(['x'], { type: 'image/png' });
    const toBlob = vi.fn((cb: (b: Blob | null) => void, type?: string) => {
      expect(type).toBe('image/png');
      cb(blob);
    });
    const canvas: MaskCanvas = { width: 1, height: 1, getContext: () => null, toBlob };
    await expect(encodeCanvasAsPng(canvas)).resolves.toBe(blob);
  });

  it('rejects when the browser yields no blob, or cannot encode at all', async () => {
    const empty: MaskCanvas = { width: 1, height: 1, getContext: () => null, toBlob: (cb) => cb(null) };
    await expect(encodeCanvasAsPng(empty)).rejects.toThrow('could not be turned into an image');
    const none: MaskCanvas = { width: 1, height: 1, getContext: () => null };
    await expect(encodeCanvasAsPng(none)).rejects.toThrow('cannot export a canvas');
  });
});

describe('brushSizeInLayerPixels', () => {
  it('converts a canvas-pixel brush through the layer scale and clamps', () => {
    expect(brushSizeInLayerPixels(40, 2)).toBe(20);
    expect(brushSizeInLayerPixels(40, 0.5)).toBe(80);
    expect(brushSizeInLayerPixels(0.1, 1)).toBe(BRUSH_MIN);
    expect(brushSizeInLayerPixels(99999, 1)).toBe(BRUSH_MAX);
    expect(brushSizeInLayerPixels(40, 0)).toBe(40);
  });
});
