/**
 * The main-thread side of a decode worker: a `FrameSource` whose demux and decode live in
 * `decode-worker.ts`. Frames arrive as transferred `VideoFrame`s (no copy); each `DecodedFrame` owns
 * its frame and closes it. Returns null when the source cannot be opened, and `undefined` when a worker
 * cannot be created at all (the caller then decodes on the main thread).
 *
 * Booting a worker (its script and the mediabunny chunks) costs the main thread 70-180 ms, which at a
 * cut in the middle of playback was a visible stall (profiled). Workers are therefore kept in a small
 * pool: a few are booted ahead (`warmDecodeWorkers`, called when a preview session opens, while paused),
 * a finished source's worker goes back to the pool instead of being terminated, and the pool is topped up
 * whenever the session pauses (never in the middle of a play).
 */

import type { DecodedFrame, FrameSource } from './decoder-pool';
import type { DecodeWorkerRequest, DecodeWorkerResponse } from './decode-worker';
import type { FrameImage } from './mediabunny-reader';

/** Idle, booted workers kept ready. */
const SPARE_WORKERS = 4;
/** Request ids are unique across every source, so a late reply to a recycled worker can never be taken for the next owner's. */
let nextRequestId = 1;
const idle: Worker[] = [];
let warming: ReturnType<typeof setTimeout> | null = null;

function spawn(): Worker | undefined {
  if (typeof Worker === 'undefined') return undefined;
  try {
    return new Worker(new URL('./decode-worker.ts', import.meta.url), { type: 'module' });
  } catch {
    return undefined;
  }
}

/** Boots spare workers in the background, one per task, so no single task carries the whole boot cost. */
export function warmDecodeWorkers(): void {
  if (warming !== null || idle.length >= SPARE_WORKERS) return;
  warming = setTimeout(() => {
    warming = null;
    const worker = spawn();
    if (!worker) return;
    idle.push(worker);
    warmDecodeWorkers();
  }, 0);
}

/** Takes a booted worker if one is idle. It does NOT boot a replacement: that is a main-thread stall, so it waits for `warmDecodeWorkers`, which the session calls while paused. */
function acquire(): Worker | undefined {
  return idle.pop() ?? spawn();
}

function release(worker: Worker): void {
  worker.onmessage = null;
  worker.onerror = null;
  if (idle.length < SPARE_WORKERS) idle.push(worker);
  else worker.terminate();
}

export async function openWorkerSource(url: string, fps: number, scaleToWidth?: number, rotation?: number): Promise<FrameSource | null | undefined> {
  const worker = acquire();
  if (!worker) return undefined;
  const post = (message: DecodeWorkerRequest) => worker.postMessage(message);
  const pending = new Map<number, (frame: FrameImage | null) => void>();
  let disposed = false;

  const opened = await new Promise<boolean | undefined>((resolve) => {
    worker.onmessage = (event: MessageEvent<DecodeWorkerResponse>) => {
      const message = event.data;
      if (message.type === 'opened') resolve(message.ok);
      else {
        const done = pending.get(message.id);
        pending.delete(message.id);
        if (done) done(message.frame);
        else message.frame?.close();
      }
    };
    // A worker that fails to load (blocked, unsupported module workers) reports through `error`.
    worker.onerror = () => {
      resolve(undefined);
      for (const done of pending.values()) done(null);
      pending.clear();
    };
    post({ type: 'open', url: new URL(url, globalThis.location?.href).href, scaleToWidth, ...(rotation ? { rotation } : {}) });
  });
  if (opened !== true) {
    if (opened === false) {
      post({ type: 'dispose' });
      release(worker);
      return null;
    }
    worker.terminate();
    return undefined;
  }

  return {
    getFrame(sourceFrame: number): Promise<DecodedFrame | null> {
      if (disposed) return Promise.resolve(null);
      // Sample at the frame's midpoint so a tiny PTS jitter cannot flip to the neighbour.
      const timestamp = (sourceFrame + 0.5) / (fps > 0 ? fps : 30);
      return new Promise((resolve) => {
        const id = nextRequestId++;
        pending.set(id, (frame) => {
          if (!frame) {
            resolve(null);
            return;
          }
          let closed = false;
          resolve({
            sourceFrame,
            image: frame,
            close() {
              if (closed) return;
              closed = true;
              frame.close();
            },
          });
        });
        post({ type: 'frame', id, timestamp });
      });
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      post({ type: 'dispose' });
      for (const done of pending.values()) done(null);
      pending.clear();
      // Frames the worker still answers are closed by the handler of the NEXT owner not existing: drop them here.
      worker.onmessage = (event: MessageEvent<DecodeWorkerResponse>) => {
        if (event.data.type === 'frame') event.data.frame?.close();
      };
      // Give in-flight replies a moment to be closed, then recycle.
      setTimeout(() => release(worker), 250);
    },
  };
}
