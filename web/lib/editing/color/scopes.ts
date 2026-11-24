/**
 * Scopes (P7): waveform, RGB parade, vectorscope and histogram computed from the COMPOSITED preview
 * frame's pixels — what the viewer shows, not a source — plus the frame statistics shot match uses.
 * Pure over an `ImageData`-like buffer, so the maths is tested without a canvas; the panel samples
 * the program canvas at ~15 Hz and draws these bins.
 */

import type { FrameStats } from './grade';
import { readCleanPicture } from '../preview/gl-compositor';

export interface ScopeData {
  /** Luma waveform: `columns` × `levels` counts (column-major), levels 0 = black at index 0. */
  waveform: Uint32Array;
  /** RGB parade: three `columns` × `levels` planes. */
  parade: [Uint32Array, Uint32Array, Uint32Array];
  /** Vectorscope: `vectorSize` × `vectorSize` counts over (Cb, Cr) centred at the middle. */
  vectorscope: Uint32Array;
  /** Histograms over `levels` bins: Y, R, G, B. */
  histogram: { y: Uint32Array; r: Uint32Array; g: Uint32Array; b: Uint32Array };
  columns: number;
  levels: number;
  vectorSize: number;
  stats: FrameStats;
  /** Fraction of pixels clipped at black and at white (luma). */
  clipped: { black: number; white: number };
}

export interface ScopeOptions {
  columns?: number;
  levels?: number;
  vectorSize?: number;
  /** Sample every n-th pixel for speed. Default 1. */
  stride?: number;
}

export interface PixelBuffer {
  data: Uint8ClampedArray | Uint8Array;
  width: number;
  height: number;
}

/** BT.709 luma of 0..255 channels, 0..255. */
export const luma709 = (r: number, g: number, b: number): number => 0.2126 * r + 0.7152 * g + 0.0722 * b;

export function computeScopes(frame: PixelBuffer, options: ScopeOptions = {}): ScopeData {
  const columns = Math.max(8, options.columns ?? 128);
  const levels = Math.max(16, options.levels ?? 128);
  const vectorSize = Math.max(16, options.vectorSize ?? 96);
  const stride = Math.max(1, Math.floor(options.stride ?? 1));
  const { data, width, height } = frame;

  const waveform = new Uint32Array(columns * levels);
  const parade: [Uint32Array, Uint32Array, Uint32Array] = [new Uint32Array(columns * levels), new Uint32Array(columns * levels), new Uint32Array(columns * levels)];
  const vectorscope = new Uint32Array(vectorSize * vectorSize);
  const hy = new Uint32Array(levels);
  const hr = new Uint32Array(levels);
  const hg = new Uint32Array(levels);
  const hb = new Uint32Array(levels);

  let sumR = 0;
  let sumG = 0;
  let sumB = 0;
  let sumY = 0;
  let sumY2 = 0;
  let black = 0;
  let white = 0;
  let count = 0;
  const level = (v: number) => Math.min(levels - 1, Math.floor((v / 256) * levels));

  for (let y = 0; y < height; y += stride) {
    for (let x = 0; x < width; x += stride) {
      const i = (y * width + x) * 4;
      const r = data[i];
      const g = data[i + 1];
      const b = data[i + 2];
      const yl = luma709(r, g, b);
      const column = Math.min(columns - 1, Math.floor((x / Math.max(1, width)) * columns));
      waveform[column * levels + level(yl)]++;
      parade[0][column * levels + level(r)]++;
      parade[1][column * levels + level(g)]++;
      parade[2][column * levels + level(b)]++;
      hy[level(yl)]++;
      hr[level(r)]++;
      hg[level(g)]++;
      hb[level(b)]++;
      // BT.709 chroma, centred: Cb = (B − Y)/1.8556, Cr = (R − Y)/1.5748, each in ≈[−128, 128].
      // Rounded to a micro-level so a neutral pixel whose luma is 254.9999… lands on the centre cell.
      const cb = Math.round(((b - yl) / 1.8556) * 1e6) / 1e6;
      const cr = Math.round(((r - yl) / 1.5748) * 1e6) / 1e6;
      const vx = Math.min(vectorSize - 1, Math.max(0, Math.floor(((cb + 128) / 256) * vectorSize)));
      const vy = Math.min(vectorSize - 1, Math.max(0, Math.floor(((128 - cr) / 256) * vectorSize)));
      vectorscope[vy * vectorSize + vx]++;
      sumR += r;
      sumG += g;
      sumB += b;
      sumY += yl;
      sumY2 += yl * yl;
      if (yl <= 1) black++;
      if (yl >= 254) white++;
      count++;
    }
  }

  const n = Math.max(1, count);
  const meanY = sumY / n;
  const variance = Math.max(0, sumY2 / n - meanY * meanY);
  return {
    waveform,
    parade,
    vectorscope,
    histogram: { y: hy, r: hr, g: hg, b: hb },
    columns,
    levels,
    vectorSize,
    stats: { meanR: sumR / n / 255, meanG: sumG / n / 255, meanB: sumB / n / 255, meanLuma: meanY / 255, lumaSpread: Math.sqrt(variance) / 255 },
    clipped: { black: black / n, white: white / n },
  };
}

/** Reads the program canvas into a small pixel buffer (downsampled by drawing into a 2D canvas). */
export function sampleCanvas(source: HTMLCanvasElement, targetWidth = 256, scratch?: HTMLCanvasElement): PixelBuffer | null {
  if (source.width === 0 || source.height === 0) return null;
  const canvas = scratch ?? document.createElement('canvas');
  const scale = targetWidth / source.width;
  canvas.width = Math.max(1, Math.round(source.width * scale));
  canvas.height = Math.max(1, Math.round(source.height * scale));
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  if (!ctx) return null;
  try {
    // The scopes read the converted picture WITHOUT the viewing LUT (a view is never measured).
    readCleanPicture(source, () => ctx.drawImage(source, 0, 0, canvas.width, canvas.height));
    const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
    return { data: image.data, width: image.width, height: image.height };
  } catch {
    return null;
  }
}
