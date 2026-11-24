/** Parses a `.cube` off the main thread. Replies with the table (transferred) or a stable error code. */

import { CubeLutError, parseCubeLut, type CubeLut, type CubeLutErrorCode, type CubeLutErrorDetails } from './cube-lut';

export type CubeLutWorkerResponse =
  | { ready: true }
  | { ok: true; lut: CubeLut }
  | { ok: false; code: CubeLutErrorCode | 'unknown'; details?: CubeLutErrorDetails };

self.onmessage = (event: MessageEvent<string>) => {
  try {
    const lut = parseCubeLut(event.data);
    (self as unknown as Worker).postMessage({ ok: true, lut } satisfies CubeLutWorkerResponse, [lut.data.buffer, ...(lut.oneD ? [lut.oneD.data.buffer] : [])]);
  } catch (err) {
    const code = err instanceof CubeLutError ? err.code : 'unknown';
    const details = err instanceof CubeLutError ? err.details : undefined;
    (self as unknown as Worker).postMessage({ ok: false, code, ...(details ? { details } : {}) } satisfies CubeLutWorkerResponse);
  }
};

// Handshake: the bundler's worker bootstrap may load this module asynchronously, and a message posted before
// `onmessage` exists is dropped (production build: every parse timed out as "could not be read"). The loader posts
// the text only after this arrives.
(self as unknown as Worker).postMessage({ ready: true } satisfies CubeLutWorkerResponse);
