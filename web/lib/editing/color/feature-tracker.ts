/**
 * P9: the feature tracker behind tracked power windows.
 *
 * Unlike `ChromaQuadTracker` (uniform-colour plates, server side), this tracks ANY textured feature:
 * the user boxes it on the first frame and the tracker follows it by normalised cross-correlation
 * (NCC) of a grey template over a search window around the previous position, frame by frame. The
 * template is refreshed from the newest match only while the match is strong (so slow appearance
 * changes are followed but an occluder does not become the template), and a frame whose best score
 * falls under `minScore` is LOST: the position is held and the frame reported, never invented.
 *
 * The track becomes the window's `offsetX`/`offsetY` keyframes (`trackToWindowOffsets`): canvas
 * fractions relative to the window's own centre, thinned so a still feature writes two keys rather
 * than one per frame. Pure maths, tested on synthetic moving textures; the browser frame sampler
 * (`sampleVideoFrames`) is the only DOM-dependent part.
 */

import type { Keyframed } from '../types';
import { thinSamples } from '../audio/automation';

export interface GrayFrame {
  width: number;
  height: number;
  /** Row-major luma, 0..255. */
  data: Uint8ClampedArray | Uint8Array | Float32Array;
}

export interface TrackBox {
  /** Top-left corner and size, in frame pixels. */
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface TrackPoint {
  /** The box's top-left in frame pixels. */
  x: number;
  y: number;
  /** NCC of the best match, −1..1. */
  score: number;
  lost: boolean;
}

export interface TrackOptions {
  /** Search radius around the previous position, in pixels. */
  searchRadius?: number;
  /** Below this NCC a frame is lost (position held). */
  minScore?: number;
  /** Above this NCC the template is refreshed from the match. */
  refreshScore?: number;
}

function patch(frame: GrayFrame, x: number, y: number, w: number, h: number): Float64Array {
  const out = new Float64Array(w * h);
  for (let j = 0; j < h; j++) {
    const row = (y + j) * frame.width;
    for (let i = 0; i < w; i++) out[j * w + i] = frame.data[row + x + i];
  }
  return out;
}

function ncc(frame: GrayFrame, x: number, y: number, template: Float64Array, w: number, h: number, tMean: number, tNorm: number): number {
  let sum = 0;
  for (let j = 0; j < h; j++) {
    const row = (y + j) * frame.width;
    for (let i = 0; i < w; i++) sum += frame.data[row + x + i];
  }
  const mean = sum / (w * h);
  let dot = 0;
  let norm = 0;
  for (let j = 0; j < h; j++) {
    const row = (y + j) * frame.width;
    for (let i = 0; i < w; i++) {
      const v = frame.data[row + x + i] - mean;
      dot += v * (template[j * w + i] - tMean);
      norm += v * v;
    }
  }
  return norm > 0 && tNorm > 0 ? dot / Math.sqrt(norm * tNorm) : -1;
}

function stats(template: Float64Array): { mean: number; norm: number } {
  let mean = 0;
  for (const v of template) mean += v;
  mean /= template.length;
  let norm = 0;
  for (const v of template) norm += (v - mean) * (v - mean);
  return { mean, norm };
}

/** Tracks `box` (on `frames[0]`) through every frame. The first point is the box itself, score 1. */
export function trackFeature(frames: readonly GrayFrame[], box: TrackBox, options: TrackOptions = {}): TrackPoint[] {
  if (frames.length === 0) return [];
  const radius = Math.max(1, Math.round(options.searchRadius ?? 16));
  const minScore = options.minScore ?? 0.5;
  const refreshScore = options.refreshScore ?? 0.9;
  const w = Math.max(2, Math.round(box.w));
  const h = Math.max(2, Math.round(box.h));
  const first = frames[0];
  let x = Math.min(Math.max(0, Math.round(box.x)), first.width - w);
  let y = Math.min(Math.max(0, Math.round(box.y)), first.height - h);
  let template = patch(first, x, y, w, h);
  let { mean: tMean, norm: tNorm } = stats(template);
  const points: TrackPoint[] = [{ x, y, score: 1, lost: false }];

  for (let f = 1; f < frames.length; f++) {
    const frame = frames[f];
    let best = -2;
    let bx = x;
    let by = y;
    for (let dy = -radius; dy <= radius; dy++) {
      const cy = y + dy;
      if (cy < 0 || cy + h > frame.height) continue;
      for (let dx = -radius; dx <= radius; dx++) {
        const cx = x + dx;
        if (cx < 0 || cx + w > frame.width) continue;
        const score = ncc(frame, cx, cy, template, w, h, tMean, tNorm);
        if (score > best) {
          best = score;
          bx = cx;
          by = cy;
        }
      }
    }
    if (best < minScore) {
      points.push({ x, y, score: best, lost: true });
      continue;
    }
    x = bx;
    y = by;
    points.push({ x, y, score: best, lost: false });
    if (best >= refreshScore) {
      template = patch(frame, x, y, w, h);
      ({ mean: tMean, norm: tNorm } = stats(template));
    }
  }
  return points;
}

/**
 * The window offsets a track writes: per tracked frame, the box centre's displacement from the first
 * frame in canvas fractions, keyed at `startFrame + i` (timeline frames), thinned to within
 * `tolerance`. Lost frames are left out (the curve interpolates across them).
 */
export function trackToWindowOffsets(points: readonly TrackPoint[], frameWidth: number, frameHeight: number, startFrame: number, frameStep = 1, tolerance = 0.002): { offsetX: Keyframed<number>; offsetY: Keyframed<number>; lostFrames: number } {
  if (points.length === 0) return { offsetX: { value: 0, keys: null }, offsetY: { value: 0, keys: null }, lostFrames: 0 };
  const x0 = points[0].x;
  const y0 = points[0].y;
  const xs: { frame: number; value: number }[] = [];
  const ys: { frame: number; value: number }[] = [];
  let lost = 0;
  points.forEach((p, i) => {
    if (p.lost) {
      lost++;
      return;
    }
    const frame = Math.round(startFrame + i * frameStep);
    xs.push({ frame, value: clampOffset((p.x - x0) / frameWidth) });
    ys.push({ frame, value: clampOffset((p.y - y0) / frameHeight) });
  });
  const keys = (samples: { frame: number; value: number }[]) =>
    thinSamples(samples, tolerance).map((s) => ({ frame: s.frame, value: Math.round(s.value * 10000) / 10000, interp: 'linear' as const }));
  return { offsetX: { value: 0, keys: keys(xs) }, offsetY: { value: 0, keys: keys(ys) }, lostFrames: lost };
}

const clampOffset = (v: number) => Math.min(1, Math.max(-1, v));

/**
 * Samples `count` grey frames from a video URL starting at `startSec`, `1/fps` apart, scaled to
 * `width` pixels wide — by seeking an off-screen `<video>` and reading a canvas. Browser-only; slow
 * (one seek per frame) but needs no decoder plumbing. Throws a readable error when the media cannot
 * be loaded or read (e.g. a cross-origin URL without CORS).
 */
export async function sampleVideoFrames(url: string, startSec: number, count: number, fps: number, width = 320): Promise<GrayFrame[]> {
  if (typeof document === 'undefined') throw new Error('Frame sampling needs a browser.');
  const video = document.createElement('video');
  video.muted = true;
  video.preload = 'auto';
  video.crossOrigin = 'use-credentials';
  video.src = url;
  await new Promise<void>((resolve, reject) => {
    video.onloadeddata = () => resolve();
    video.onerror = () => reject(new Error('The clip could not be loaded for tracking.'));
  });
  const height = Math.max(2, Math.round((width * video.videoHeight) / Math.max(1, video.videoWidth)));
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  if (!ctx) throw new Error('No 2D canvas to read frames from.');
  const frames: GrayFrame[] = [];
  for (let i = 0; i < count; i++) {
    await new Promise<void>((resolve) => {
      video.onseeked = () => resolve();
      video.currentTime = startSec + i / fps;
    });
    ctx.drawImage(video, 0, 0, width, height);
    const rgba = ctx.getImageData(0, 0, width, height).data;
    const gray = new Uint8ClampedArray(width * height);
    for (let p = 0; p < gray.length; p++) gray[p] = 0.299 * rgba[p * 4] + 0.587 * rgba[p * 4 + 1] + 0.114 * rgba[p * 4 + 2];
    frames.push({ width, height, data: gray });
  }
  video.removeAttribute('src');
  return frames;
}
