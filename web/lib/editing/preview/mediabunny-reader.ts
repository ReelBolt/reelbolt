/**
 * The demux + decode half of a source: mediabunny over a URL, read forward-sequentially
 * (`sequential-reader.ts`) so each packet is decoded once. It yields `VideoFrame`s.
 *
 * It runs in a Web Worker in the editor (`decode-worker.ts`): `VideoDecoder.configure`, the demuxer's
 * parsing and every decode callback then never wait for the React/UI thread (profiled: opening a decoder
 * at a cut blocked the main thread for 70-150 ms, which was a visible stall at every cut and dissolve),
 * and a busy UI thread cannot starve the decoders. It also runs on the main thread when workers are
 * unavailable (`mediabunny-source.ts` falls back to that). Browser-only, loaded lazily.
 */

import { guardVideoColorSpace } from './colorspace-guard';
import { SequentialReader } from './sequential-reader';

/** What a read yields: the decoded `VideoFrame`, or an `ImageBitmap` of it when it was scaled down. The caller closes it. */
export type FrameImage = VideoFrame | ImageBitmap;

export interface MediabunnyReader {
  /**
   * The frame covering `timestamp` (seconds); null when nothing is readable there. With `scaleToWidth`, a frame wider
   * than that is scaled down to it where the read happens (the worker), so a slow graphics stack uploads fewer pixels.
   * A container rotation/flip (phone clips) is honoured: such a frame comes back as a display-oriented `ImageBitmap`.
   * `extraRotation` (clockwise degrees) is applied after the container's, and `scaleToWidth` targets the rotated width.
   */
  read(timestamp: number, scaleToWidth?: number, extraRotation?: Rotation): Promise<FrameImage | null>;
  dispose(): void;
}

export type Rotation = 0 | 90 | 180 | 270;

/** The slice of mediabunny's `VideoSample` the reader needs. */
interface VideoSampleLike {
  timestamp: number;
  rotation: Rotation;
  flip: boolean;
  displayWidth: number;
  displayHeight: number;
  toVideoFrame(): VideoFrame;
  draw(ctx: OffscreenCanvasRenderingContext2D, dx: number, dy: number, dw: number, dh: number): void;
  close(): void;
}

export interface OrientationPlan {
  /** Output bitmap size. */
  width: number;
  height: number;
  /** Size the sample (already container-oriented) is drawn at, before the extra rotation. */
  drawWidth: number;
  drawHeight: number;
}

/**
 * Whether a read needs an oriented redraw, and at what size. null = the raw VideoFrame path (no container
 * rotation/flip, and the extra rotation is a multiple of 360). `displayWidth/Height` are after container rotation.
 */
export function planOrientation(
  rotation: Rotation,
  flip: boolean,
  extraRotation: Rotation,
  displayWidth: number,
  displayHeight: number,
  scaleToWidth?: number,
): OrientationPlan | null {
  if (rotation === 0 && !flip && extraRotation === 0) return null;
  const swap = extraRotation === 90 || extraRotation === 270;
  const naturalW = swap ? displayHeight : displayWidth;
  const naturalH = swap ? displayWidth : displayHeight;
  const factor = scaleToWidth && scaleToWidth > 0 && naturalW > scaleToWidth * 1.1 ? Math.round(scaleToWidth) / naturalW : 1;
  const width = Math.max(1, Math.round(naturalW * factor));
  const height = Math.max(1, Math.round(naturalH * factor));
  return { width, height, drawWidth: swap ? height : width, drawHeight: swap ? width : height };
}

let warnedNoCanvas = false;

/** Draw the sample (container rotation/flip via mediabunny) onto a canvas, then rotate by `extra`; null when no canvas. */
function drawOriented(sample: VideoSampleLike, plan: OrientationPlan, extra: Rotation): ImageBitmap | null {
  if (typeof OffscreenCanvas === 'undefined') {
    if (!warnedNoCanvas) {
      warnedNoCanvas = true;
      console.warn('mediabunny-reader: OffscreenCanvas unavailable; frames are not rotated');
    }
    return null;
  }
  const canvas = new OffscreenCanvas(plan.width, plan.height);
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  if (extra === 90) {
    ctx.translate(plan.width, 0);
    ctx.rotate(Math.PI / 2);
  } else if (extra === 180) {
    ctx.translate(plan.width, plan.height);
    ctx.rotate(Math.PI);
  } else if (extra === 270) {
    ctx.translate(0, plan.height);
    ctx.rotate(-Math.PI / 2);
  }
  sample.draw(ctx, 0, 0, plan.drawWidth, plan.drawHeight);
  return canvas.transferToImageBitmap();
}

export async function openMediabunnyReader(url: string): Promise<MediabunnyReader | null> {
  if (typeof globalThis.VideoDecoder === 'undefined') return null;
  // WebKit reports unsignalled primaries as "unspecified", which mediabunny rejects frame by frame.
  guardVideoColorSpace();
  const mediabunny = await import('mediabunny');
  const input = new mediabunny.Input({
    source: new mediabunny.UrlSource(url),
    formats: mediabunny.ALL_FORMATS,
  });

  let track: Awaited<ReturnType<typeof input.getPrimaryVideoTrack>>;
  try {
    track = await input.getPrimaryVideoTrack();
  } catch {
    input.dispose();
    return null;
  }
  if (!track || !(await track.canDecode())) {
    input.dispose();
    return null;
  }

  const sink = new mediabunny.VideoSampleSink(track, { optimizeForLatency: true });
  const reader = new SequentialReader<VideoSampleLike>((start) => sink.samples(start));
  return {
    async read(timestamp, scaleToWidth, extraRotation = 0) {
      try {
        let oriented = false;
        // Each caller owns its VideoFrame (a cheap reference to the decoded planes) and closes it.
        const frame = await reader.read<FrameImage>(timestamp, (sample) => {
          const plan = planOrientation(sample.rotation, sample.flip, extraRotation, sample.displayWidth, sample.displayHeight, scaleToWidth);
          if (plan) {
            const bitmap = drawOriented(sample, plan, extraRotation);
            if (bitmap) {
              oriented = true;
              return bitmap;
            }
          }
          return sample.toVideoFrame();
        });
        return frame && scaleToWidth && !oriented ? await scaleDown(frame as VideoFrame, scaleToWidth) : frame;
      } catch {
        return null;
      }
    },
    dispose() {
      reader.dispose();
      input.dispose();
    },
  };
}

/** A frame wider than ~1.1 x `width` as an ImageBitmap of `width`; otherwise (or when the browser cannot) the frame itself. */
async function scaleDown(frame: VideoFrame, width: number): Promise<FrameImage> {
  const sourceWidth = frame.displayWidth;
  if (width <= 0 || sourceWidth <= width * 1.1 || typeof createImageBitmap !== 'function') return frame;
  try {
    const bitmap = await createImageBitmap(frame, { resizeWidth: Math.round(width), resizeHeight: Math.max(1, Math.round((frame.displayHeight * width) / sourceWidth)), resizeQuality: 'low' });
    frame.close();
    return bitmap;
  } catch {
    return frame;
  }
}
