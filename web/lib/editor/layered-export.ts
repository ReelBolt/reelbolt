/**
 * Layered exports: a Photoshop document (`.psd`) and an OpenRaster archive (`.ora`, which GIMP and
 * Krita open), each keeping the editor's layers as layers — name, order, position, opacity, blend
 * mode and visibility — instead of one flattened picture.
 *
 * Pure: the browser rasterises every layer into straight-alpha RGBA (see `rasterizeForExport` in
 * `canvas-dom.ts`) and this module only arranges those pixels into a file. Photoshop's format is
 * written by `ag-psd` (MIT), loaded on demand so the editor does not pay for it until someone
 * exports a PSD; OpenRaster is a ZIP of PNGs plus an XML stack and needs nothing but `zip.ts`.
 *
 * What a layered file keeps beyond the frame: a layer that reaches past the frame keeps its pixels
 * out to half a frame on every side (Photoshop and Krita both keep pixels outside the canvas, so
 * the user can move them back in). Further than that is cut, which bounds the file's size. Text is
 * exported as pixels (named after the text layer), not as editable type.
 */

import type { BlendMode as PsdBlendMode, Layer as PsdLayer, Psd } from 'ag-psd';
import { layerBounds, type PlacedLayer } from './compose';
import { buildStoredZip, type ZipEntry } from './zip';
import type { BlendMode } from '@/lib/types/image-document';

/** One layer's pixels, ready for a layered file. `left`/`top` are frame pixels and may be negative. */
export interface LayerRaster {
  id: string;
  name: string;
  left: number;
  top: number;
  width: number;
  height: number;
  /** RGBA, straight (not premultiplied) alpha, `width × height × 4` bytes. */
  pixels: Uint8ClampedArray;
  opacity: number;
  blendMode: BlendMode;
  hidden: boolean;
}

export interface LayeredPicture {
  width: number;
  height: number;
  /** Back to front. */
  layers: readonly LayerRaster[];
  /** The flattened picture at frame size, what a viewer without layers shows. */
  composite: Uint8ClampedArray;
}

/**
 * The integer rectangle a layer's pixels are rasterised into: its rotated bounds, kept to the frame
 * plus half a frame on each side. Null when nothing of the layer is inside that area.
 */
export function rasterBounds(
  layer: Pick<PlacedLayer, 'transform' | 'size'>,
  frame: { width: number; height: number },
): { left: number; top: number; width: number; height: number } | null {
  const box = layerBounds(layer).aabb;
  const marginX = Math.ceil(frame.width / 2);
  const marginY = Math.ceil(frame.height / 2);
  const left = Math.max(-marginX, Math.floor(box.x));
  const top = Math.max(-marginY, Math.floor(box.y));
  const right = Math.min(frame.width + marginX, Math.ceil(box.x + box.w));
  const bottom = Math.min(frame.height + marginY, Math.ceil(box.y + box.h));
  if (right - left < 1 || bottom - top < 1) return null;
  return { left, top, width: right - left, height: bottom - top };
}

/** The editor's blend modes in Photoshop's words (ag-psd's `BlendMode`). */
export const PSD_BLEND: Record<BlendMode, PsdBlendMode> = {
  normal: 'normal',
  multiply: 'multiply',
  screen: 'screen',
  overlay: 'overlay',
  darken: 'darken',
  lighten: 'lighten',
  'soft-light': 'soft light',
  'hard-light': 'hard light',
  difference: 'difference',
};

/** The editor's blend modes as OpenRaster `composite-op` values (the spec's `svg:` names). */
export const ORA_COMPOSITE: Record<BlendMode, string> = {
  normal: 'svg:src-over',
  multiply: 'svg:multiply',
  screen: 'svg:screen',
  overlay: 'svg:overlay',
  darken: 'svg:darken',
  lighten: 'svg:lighten',
  'soft-light': 'svg:soft-light',
  'hard-light': 'svg:hard-light',
  difference: 'svg:difference',
};

const clamp01 = (n: number) => (Number.isFinite(n) ? Math.min(1, Math.max(0, n)) : 1);

/** The ag-psd document for a picture: one raster layer per editor layer, plus the composite. */
export function buildPsdDocument(picture: LayeredPicture): Psd {
  const children: PsdLayer[] = picture.layers.map((layer) => ({
    name: layer.name.slice(0, 255) || 'Layer',
    left: layer.left,
    top: layer.top,
    right: layer.left + layer.width,
    bottom: layer.top + layer.height,
    opacity: clamp01(layer.opacity),
    blendMode: PSD_BLEND[layer.blendMode] ?? 'normal',
    hidden: layer.hidden,
    imageData: { data: layer.pixels, width: layer.width, height: layer.height },
  }));
  return {
    width: picture.width,
    height: picture.height,
    children,
    imageData: { data: picture.composite, width: picture.width, height: picture.height },
  };
}

/** Writes the PSD bytes. `ag-psd` is imported here, on demand, and nowhere else. */
export async function encodePsd(picture: LayeredPicture): Promise<Uint8Array> {
  const { writePsdUint8Array } = await import('ag-psd');
  // `noBackground`: a fully opaque bottom layer stays an ordinary layer, never Photoshop's locked
  // "Background", so every layer the editor had can still be moved.
  return writePsdUint8Array(buildPsdDocument(picture), { noBackground: true });
}

const xmlEscape = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');

export interface OraLayer {
  name: string;
  left: number;
  top: number;
  opacity: number;
  blendMode: BlendMode;
  hidden: boolean;
  png: Uint8Array;
}

/** The OpenRaster `stack.xml`: the TOP layer first, which is the order the format lists them in. */
export function oraStackXml(width: number, height: number, layers: readonly OraLayer[]): string {
  const lines = [...layers]
    .map((layer, index) => ({ layer, index }))
    .reverse()
    .map(
      ({ layer, index }) =>
        `    <layer name="${xmlEscape(layer.name || 'Layer')}" src="data/layer${index}.png" x="${layer.left}" y="${layer.top}" ` +
        `opacity="${clamp01(layer.opacity).toFixed(3)}" visibility="${layer.hidden ? 'hidden' : 'visible'}" ` +
        `composite-op="${ORA_COMPOSITE[layer.blendMode] ?? 'svg:src-over'}"/>`,
    );
  return [
    "<?xml version='1.0' encoding='UTF-8'?>",
    `<image version="0.0.5" w="${width}" h="${height}">`,
    '  <stack>',
    ...lines,
    '  </stack>',
    '</image>',
    '',
  ].join('\n');
}

/**
 * The OpenRaster archive: `mimetype` first and stored (the format's own rule, so a file manager can
 * sniff it), then the stack, the layers (back to front, `data/layer0.png` is the back), the merged
 * picture and a thumbnail of at most 256 pixels.
 */
export function buildOra(input: {
  width: number;
  height: number;
  layers: readonly OraLayer[];
  mergedPng: Uint8Array;
  thumbnailPng: Uint8Array;
}): Uint8Array {
  const encoder = new TextEncoder();
  const entries: ZipEntry[] = [
    { name: 'mimetype', data: encoder.encode('image/openraster') },
    { name: 'stack.xml', data: encoder.encode(oraStackXml(input.width, input.height, input.layers)) },
    ...input.layers.map((layer, index) => ({ name: `data/layer${index}.png`, data: layer.png })),
    { name: 'mergedimage.png', data: input.mergedPng },
    { name: 'Thumbnails/thumbnail.png', data: input.thumbnailPng },
  ];
  return buildStoredZip(entries);
}

/** The thumbnail size the OpenRaster spec allows: the longest side at most 256. */
export function oraThumbnailSize(width: number, height: number): { width: number; height: number } {
  const scale = Math.min(1, 256 / Math.max(width, height));
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}
