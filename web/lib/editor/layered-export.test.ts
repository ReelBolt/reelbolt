/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { readPsd } from 'ag-psd';
import {
  buildOra,
  buildPsdDocument,
  encodePsd,
  oraStackXml,
  oraThumbnailSize,
  rasterBounds,
  type LayerRaster,
  type LayeredPicture,
} from './layered-export';
import { buildStoredZip, crc32, readStoredZip } from './zip';

const solid = (width: number, height: number, rgba: [number, number, number, number]) => {
  const pixels = new Uint8ClampedArray(width * height * 4);
  for (let i = 0; i < pixels.length; i += 4) pixels.set(rgba, i);
  return pixels;
};

const raster = (over: Partial<LayerRaster> & { id: string }): LayerRaster => ({
  name: over.id,
  left: 0,
  top: 0,
  width: 4,
  height: 2,
  pixels: solid(4, 2, [255, 0, 0, 255]),
  opacity: 1,
  blendMode: 'normal',
  hidden: false,
  ...over,
});

const picture: LayeredPicture = {
  width: 8,
  height: 6,
  layers: [
    raster({ id: 'bg', name: 'Original', width: 8, height: 6, pixels: solid(8, 6, [10, 20, 30, 255]) }),
    raster({ id: 'cut', name: 'Person', left: -2, top: 3, opacity: 0.5, blendMode: 'multiply' }),
    raster({ id: 'txt', name: 'Title <big> & "bold"', left: 5, top: 1, hidden: true, blendMode: 'soft-light' }),
  ],
  composite: solid(8, 6, [1, 2, 3, 255]),
};

describe('the Photoshop file', () => {
  it('keeps every layer, in order, with its name, position, opacity, blend mode and visibility', async () => {
    const bytes = await encodePsd(picture);
    expect(String.fromCharCode(...bytes.slice(0, 4))).toBe('8BPS');
    const psd = readPsd(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer, {
      skipLayerImageData: true,
      skipCompositeImageData: true,
      skipThumbnail: true,
    });
    expect(psd.width).toBe(8);
    expect(psd.height).toBe(6);
    const layers = psd.children ?? [];
    expect(layers.map((l) => l.name)).toEqual(['Original', 'Person', 'Title <big> & "bold"']);
    expect(layers[1]).toMatchObject({ left: -2, top: 3, right: 2, bottom: 5, blendMode: 'multiply' });
    expect(layers[1].opacity).toBeCloseTo(0.5, 1);
    expect(layers[2]).toMatchObject({ hidden: true, blendMode: 'soft light' });
    expect(layers[0].hidden ?? false).toBe(false);
  });

  it('describes each layer to ag-psd as straight RGBA pixel data', () => {
    const doc = buildPsdDocument(picture);
    expect(doc.children?.[1].imageData).toMatchObject({ width: 4, height: 2 });
    expect(doc.imageData?.width).toBe(8);
  });
});

describe('the OpenRaster file', () => {
  const png = (n: number) => new Uint8Array([0x89, 0x50, 0x4e, 0x47, n]);
  const ora = buildOra({
    width: 8,
    height: 6,
    layers: picture.layers.map((l, i) => ({ ...l, png: png(i) })),
    mergedPng: png(9),
    thumbnailPng: png(8),
  });

  it('starts with an uncompressed "mimetype" member, as the format requires', () => {
    const text = new TextDecoder().decode(ora.slice(30, 30 + 8 + 16));
    expect(text).toBe('mimetypeimage/openraster');
    expect(new DataView(ora.buffer).getUint16(8, true)).toBe(0); // stored
  });

  it('holds the stack, every layer, the merged picture and a thumbnail', () => {
    const entries = readStoredZip(ora);
    expect(entries.map((e) => e.name)).toEqual([
      'mimetype',
      'stack.xml',
      'data/layer0.png',
      'data/layer1.png',
      'data/layer2.png',
      'mergedimage.png',
      'Thumbnails/thumbnail.png',
    ]);
    expect(entries[3].data).toEqual(png(1));
  });

  it('lists the top layer first, with escaped names and the format\'s blend names', () => {
    const xml = oraStackXml(8, 6, picture.layers.map((l) => ({ ...l, png: new Uint8Array() })));
    const order = [...xml.matchAll(/src="data\/(layer\d)\.png"/g)].map((m) => m[1]);
    expect(order).toEqual(['layer2', 'layer1', 'layer0']);
    expect(xml).toContain('name="Title &lt;big&gt; &amp; &quot;bold&quot;"');
    expect(xml).toContain('x="-2" y="3" opacity="0.500" visibility="visible" composite-op="svg:multiply"');
    expect(xml).toContain('visibility="hidden" composite-op="svg:soft-light"');
    expect(xml).toContain('<image version="0.0.5" w="8" h="6">');
  });

  it('keeps the thumbnail at most 256 pixels on its longest side', () => {
    expect(oraThumbnailSize(3840, 2160)).toEqual({ width: 256, height: 144 });
    expect(oraThumbnailSize(100, 50)).toEqual({ width: 100, height: 50 });
  });
});

describe('the ZIP writer', () => {
  it('computes the standard CRC-32', () => {
    expect(crc32(new TextEncoder().encode('123456789'))).toBe(0xcbf43926);
    expect(crc32(new Uint8Array())).toBe(0);
  });

  it('writes what it reads back, with a central directory that points at every entry', () => {
    const entries = [
      { name: 'a.txt', data: new TextEncoder().encode('hello') },
      { name: 'dir/ü.bin', data: new Uint8Array([0, 1, 2]) },
    ];
    const zip = buildStoredZip(entries);
    expect(readStoredZip(zip)).toEqual(entries);
    const view = new DataView(zip.buffer);
    const end = zip.length - 22;
    expect(view.getUint32(end, true)).toBe(0x06054b50);
    expect(view.getUint16(end + 10, true)).toBe(2);
  });
});

describe('rasterBounds', () => {
  const frame = { width: 1000, height: 500 };
  const at = (x: number, y: number, w = 200, h = 100, scale = 1) => ({
    transform: { x, y, scale, rotation: 0, flipX: false, flipY: false },
    size: { w, h },
  });

  it('is the layer\'s own box inside the frame', () => {
    expect(rasterBounds(at(10, 20), frame)).toEqual({ left: 10, top: 20, width: 200, height: 100 });
  });

  it('keeps pixels beside the frame, out to half a frame, and drops a layer further away', () => {
    expect(rasterBounds(at(900, -100), frame)).toEqual({ left: 900, top: -100, width: 200, height: 100 });
    expect(rasterBounds(at(-2000, 0, 5000, 100), frame)).toEqual({ left: -500, top: 0, width: 2000, height: 100 });
    expect(rasterBounds(at(5000, 0), frame)).toBeNull();
  });
});
