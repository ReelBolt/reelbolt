/**
 * Loads the viewing LUT: parse off the main thread when a Worker can be created (else inline), keep
 * the last few parsed tables in a small LRU keyed by project file id + size/etag, and pack a table as
 * RGBA half-float texels for a future `sampler3D` upload.
 */

import { CubeLutError, CUBE_LUT_MAX_BYTES, parseCubeLut, type CubeLut } from './cube-lut';
import type { CubeLutWorkerResponse } from './cube-lut-worker';

export const VIEWING_LUT_CACHE_MAX = 4;

/** A worker that does not answer in this time is treated as crashed; the view shows a plain error, never a frozen UI. */
export const WORKER_PARSE_TIMEOUT_MS = 10_000;

/** A worker that has not said it is listening by then is treated as one that never booted (parsed on the main thread). */
export const WORKER_BOOT_TIMEOUT_MS = 3_000;

/** A worker that crashed WITH the text falls back to a main-thread parse ONLY under this smaller cap: a huge one would freeze the UI. */
export const FALLBACK_PARSE_MAX_BYTES = 2 * 1024 * 1024;

export interface LoadViewingLutOptions {
  /** Cache key, e.g. `${fileId}:${size}:${etag}`. Without one nothing is cached. */
  key?: string;
  /** The body's declared length (Content-Length / the file listing's sizeBytes), checked BEFORE the body is read. */
  sizeHint?: number;
  /** Byte cap; tests pass a small one. Defaults to the parser's 16 MiB. */
  maxBytes?: number;
  /** How long one worker parse may run. */
  parseTimeoutMs?: number;
  /** How long the worker may take to say it is listening. */
  bootTimeoutMs?: number;
}

const cache = new Map<string, CubeLut>();

/** UTF-8 length, not UTF-16 code units: the cap is about bytes on the wire and in memory. */
export function utf8ByteLength(text: string): number {
  return new TextEncoder().encode(text).byteLength;
}

function parseInWorker(text: string, timeoutMs: number, fallbackMaxBytes: number, bootTimeoutMs: number): Promise<CubeLut> | undefined {
  if (typeof Worker === 'undefined') return undefined;
  let worker: Worker;
  try {
    worker = new Worker(new URL('./cube-lut-worker.ts', import.meta.url), { type: 'module' });
  } catch {
    return undefined;
  }
  return new Promise<CubeLut>((resolve, reject) => {
    let settled = false;
    let posted = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const finish = (run: () => void) => {
      if (settled) return;
      settled = true;
      if (timer !== null) clearTimeout(timer);
      worker.terminate();
      run();
    };
    // Parse here instead, under `cap` bytes (a bigger file is refused rather than freezing the UI).
    const parseHere = (cap: number) =>
      finish(() => {
        if (utf8ByteLength(text) > cap) {
          reject(new CubeLutError('too_large'));
          return;
        }
        try {
          resolve(parseCubeLut(text));
        } catch (err) {
          reject(err);
        }
      });
    // A worker that never says it is listening never ran this module: in the production bundle a second worker entry
    // booted the decode worker's module instead, so the text went unanswered and every pick ended as "could not be
    // read" (lut-1d-support, the owner's Samsung LUT). That is a bundling failure, not a bad file: parse it here, at
    // the full cap. Once the worker has the text, a crash only falls back for a small file, as before.
    const fallback = () => parseHere(posted ? fallbackMaxBytes : CUBE_LUT_MAX_BYTES);
    worker.onmessage = (event: MessageEvent<CubeLutWorkerResponse>) => {
      const msg: unknown = event.data;
      if (typeof msg !== 'object' || msg === null) return;
      if ('ready' in msg) {
        if (posted) return;
        posted = true;
        if (timer !== null) clearTimeout(timer);
        timer = setTimeout(() => finish(() => reject(new CubeLutError('malformed', 'the .cube parser did not finish in time'))), timeoutMs);
        worker.postMessage(text);
        return;
      }
      if (!posted || !('ok' in msg)) return; // anything else is not this worker's answer
      const reply = msg as Exclude<CubeLutWorkerResponse, { ready: true }>;
      finish(() => {
        if (reply.ok) resolve(reply.lut);
        else reject(reply.code === 'unknown' ? new CubeLutError('malformed') : new CubeLutError(reply.code, undefined, reply.details));
      });
    };
    worker.onerror = () => fallback();
    timer = setTimeout(fallback, bootTimeoutMs);
  });
}

/** Reads a UTF-8 body under a hard byte cap, cancelling the stream as soon as the cap is passed. */
export async function readCappedText(body: ReadableStream<Uint8Array>, maxBytes: number = CUBE_LUT_MAX_BYTES): Promise<string> {
  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (!value) continue;
      total += value.byteLength;
      if (total > maxBytes) {
        await reader.cancel();
        throw new CubeLutError('too_large');
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const merged = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    merged.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(merged);
}

export async function loadViewingLut(fetchText: () => Promise<string>, opts: LoadViewingLutOptions = {}): Promise<CubeLut> {
  const { key, sizeHint, maxBytes = CUBE_LUT_MAX_BYTES, parseTimeoutMs = WORKER_PARSE_TIMEOUT_MS, bootTimeoutMs = WORKER_BOOT_TIMEOUT_MS } = opts;
  if (sizeHint !== undefined && sizeHint > maxBytes) throw new CubeLutError('too_large');
  if (key !== undefined) {
    const hit = cache.get(key);
    if (hit) {
      cache.delete(key);
      cache.set(key, hit);
      return hit;
    }
  }
  const text = await fetchText();
  if (utf8ByteLength(text) > maxBytes) throw new CubeLutError('too_large');
  const lut = await (parseInWorker(text, parseTimeoutMs, Math.min(maxBytes, FALLBACK_PARSE_MAX_BYTES), bootTimeoutMs) ?? Promise.resolve().then(() => parseCubeLut(text)));
  if (key !== undefined) {
    cache.set(key, lut);
    while (cache.size > VIEWING_LUT_CACHE_MAX) {
      const oldest = cache.keys().next().value;
      if (oldest === undefined) break;
      cache.delete(oldest);
    }
  }
  return lut;
}

/** Drops one cached table, or all of them. */
export function disposeViewingLut(key?: string): void {
  if (key === undefined) cache.clear();
  else cache.delete(key);
}

export function viewingLutCacheSize(): number {
  return cache.size;
}

const f32 = new Float32Array(1);
const u32 = new Uint32Array(f32.buffer);

/** float32 -> IEEE half bits, round-to-nearest-even; clamps to +-65504; NaN becomes 0. */
export function floatToHalf(value: number): number {
  if (Number.isNaN(value)) return 0;
  f32[0] = value;
  const x = u32[0] as number;
  const sign = (x >>> 16) & 0x8000;
  const abs = Math.abs(f32[0] as number);
  if (abs >= 65520) return sign | 0x7bff;
  if (abs < 2 ** -24 / 2) return sign;
  const exp = ((x >>> 23) & 0xff) - 127;
  if (exp < -14) {
    // Subnormal half: value = m * 2^-24.
    const m = abs / 2 ** -24;
    let r = Math.floor(m);
    const diff = m - r;
    if (diff > 0.5 || (diff === 0.5 && (r & 1) === 1)) r += 1;
    return sign | r;
  }
  let mant = x & 0x7fffff;
  let e = exp + 15;
  const round = mant & 0x1fff;
  mant >>>= 13;
  if (round > 0x1000 || (round === 0x1000 && (mant & 1) === 1)) {
    mant += 1;
    if (mant === 0x400) {
      mant = 0;
      e += 1;
    }
  }
  if (e >= 31) return sign | 0x7bff;
  return sign | (e << 10) | mant;
}

/** RGBA half-float texels (alpha 1), R fastest, ready for `texImage3D(RGBA16F, HALF_FLOAT)`. */
export function toRgba16fTexture3dData(lut: CubeLut): Uint16Array {
  const texels = lut.size * lut.size * lut.size;
  const out = new Uint16Array(texels * 4);
  const one = floatToHalf(1);
  for (let i = 0; i < texels; i += 1) {
    out[i * 4] = floatToHalf(lut.data[i * 3] as number);
    out[i * 4 + 1] = floatToHalf(lut.data[i * 3 + 1] as number);
    out[i * 4 + 2] = floatToHalf(lut.data[i * 3 + 2] as number);
    out[i * 4 + 3] = one;
  }
  return out;
}
