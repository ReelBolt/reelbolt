/**
 * A `FrameSource` over mediabunny — the demuxer spike (a) chose (random seek 9.21× faster than
 * mp4box.js). Browser-only and loaded lazily, so no test and no server render ever imports it.
 *
 * The demux and decode run in a Web Worker (`worker-source.ts` / `decode-worker.ts`): one worker per
 * source, each reading its clip forward-sequentially (`sequential-reader.ts`, every packet decoded
 * once) and handing back `VideoFrame`s that the GL compositor uploads straight to a texture (no 2D-canvas
 * `drawImage` readback). Without Worker support the same reader runs on the main thread. The pool owns
 * frame lifetime through `DecodedFrame.close`. The playback-resolution toggle sizes the
 * compositor canvas, so the GPU does the scaling.
 */

import type { DecodedFrame, FrameSource } from './decoder-pool';
import { openMediabunnyReader } from './mediabunny-reader';
import { openWorkerSource, warmDecodeWorkers } from './worker-source';

export { warmDecodeWorkers };

export interface MediabunnySourceOptions {
  url: string;
  /** Source frame rate (frames → seconds). */
  fps: number;
  /** The playback width: the compositor canvas is sized to it and the GPU scales, so frames are only resized when `scaleDown`. */
  targetWidth: number;
  /** Scale frames wider than `targetWidth` down in the decoder (software graphics, where uploading 1080p/4K layers is the bottleneck). */
  scaleDown?: boolean;
  /** The clip's `sourceRotation` (clockwise degrees), applied after the container's. */
  rotation?: 0 | 90 | 180 | 270 | number;
}

export async function openMediabunnySource(options: MediabunnySourceOptions): Promise<FrameSource | null> {
  if (typeof globalThis.VideoDecoder === 'undefined') return null;
  const viaWorker = await openWorkerSource(options.url, options.fps, options.scaleDown ? options.targetWidth : undefined, options.rotation);
  if (viaWorker !== undefined) return viaWorker;
  return openMainThreadSource(options);
}

async function openMainThreadSource(options: MediabunnySourceOptions): Promise<FrameSource | null> {
  const reader = await openMediabunnyReader(options.url);
  if (!reader) return null;
  const fps = options.fps > 0 ? options.fps : 30;
  const extra = options.rotation === 90 || options.rotation === 180 || options.rotation === 270 ? options.rotation : 0;
  return {
    async getFrame(sourceFrame: number): Promise<DecodedFrame | null> {
      // Sample at the frame's midpoint so a tiny PTS jitter cannot flip to the neighbour.
      const video = await reader.read((sourceFrame + 0.5) / fps, options.scaleDown ? options.targetWidth : undefined, extra);
      if (!video) return null;
      let closed = false;
      return {
        sourceFrame,
        image: video,
        close() {
          if (closed) return;
          closed = true;
          video.close();
        },
      };
    },
    dispose() {
      reader.dispose();
    },
  };
}
