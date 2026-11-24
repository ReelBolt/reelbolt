/**
 * The decode worker: one worker per open source (`worker-source.ts` is the main-thread side).
 *
 *   main -> worker   { type: 'open', url }                      worker -> main  { type: 'opened', ok }
 *   main -> worker   { type: 'frame', id, timestamp }            worker -> main  { type: 'frame', id, frame }  (VideoFrame, transferred)
 *   main -> worker   { type: 'dispose' }                         (the worker closes the source and goes idle)
 *
 * Requests are answered in the order the reader serialises them. A frame that cannot be read is `null`.
 */

import { openMediabunnyReader, type FrameImage, type MediabunnyReader } from './mediabunny-reader';

export type DecodeWorkerRequest =
  | { type: 'open'; url: string; scaleToWidth?: number; rotation?: number }
  | { type: 'frame'; id: number; timestamp: number }
  | { type: 'dispose' };

export type DecodeWorkerResponse = { type: 'opened'; ok: boolean } | { type: 'frame'; id: number; frame: FrameImage | null };

interface WorkerScope {
  postMessage(message: DecodeWorkerResponse, transfer?: Transferable[]): void;
  onmessage: ((event: MessageEvent<DecodeWorkerRequest>) => void) | null;
}

const scope = self as unknown as WorkerScope;
// Load the demuxer now, while the worker is a spare, so its first `open` is not also a chunk load.
void import('mediabunny').catch(() => undefined);

let reader: MediabunnyReader | null = null;
let scaleToWidth: number | undefined;
let extraRotation: 0 | 90 | 180 | 270 = 0;
let opening: Promise<MediabunnyReader | null> | null = null;

scope.onmessage = (event) => {
  const message = event.data;
  if (message.type === 'open') {
    scaleToWidth = message.scaleToWidth;
    extraRotation = message.rotation === 90 || message.rotation === 180 || message.rotation === 270 ? message.rotation : 0;
    opening = openMediabunnyReader(message.url).catch(() => null);
    void opening.then((opened) => {
      reader = opened;
      scope.postMessage({ type: 'opened', ok: opened !== null });
    });
  } else if (message.type === 'frame') {
    const { id, timestamp } = message;
    void (opening ?? Promise.resolve(null)).then(async (opened) => {
      const frame = opened ? await opened.read(timestamp, scaleToWidth, extraRotation) : null;
      if (frame) scope.postMessage({ type: 'frame', id, frame }, [frame as unknown as Transferable]);
      else scope.postMessage({ type: 'frame', id, frame: null });
    });
  } else {
    reader?.dispose();
    reader = null;
    opening = null;
    scaleToWidth = undefined;
    extraRotation = 0;
  }
};
