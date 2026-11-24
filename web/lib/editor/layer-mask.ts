/**
 * Layer masks (Wave 4): the data side of "paint a mask on a picture layer". No DOM, no React.
 *
 * A mask is one 8-bit "visible amount" per pixel of the layer's own bitmap: 255 shows the pixel,
 * 0 hides it. The server stores it as a grayscale PNG (luminance = visible amount) and the compositor
 * turns that luminance into alpha before `destination-in` ({@link lumaToAlphaInPlace}).
 */

import type { Point } from './compose';

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface MaskBrush {
  /** Diameter in LAYER pixels. */
  size: number;
  /** 0 = soft edge all the way in, 1 = hard edge. */
  hardness: number;
  /** True paints black (hides), false paints white (shows). */
  hide: boolean;
}

/** How many strokes can be undone in one session, and a cap on the memory the snapshots may hold. */
export const MASK_UNDO_LIMIT = 30;
export const MASK_UNDO_BYTES = 256 * 1024 * 1024;

/** Turns luminance into alpha, in place, over RGBA pixels: RGB becomes white and A the luminance. */
export function lumaToAlphaInPlace(rgba: Uint8ClampedArray): void {
  for (let i = 0; i + 3 < rgba.length; i += 4) {
    const luma = Math.round(0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2]);
    rgba[i] = 255;
    rgba[i + 1] = 255;
    rgba[i + 2] = 255;
    rgba[i + 3] = luma;
  }
}

/** The matte route is stable, so a new mask needs a new URL to be fetched again. */
export function versionedMatteUrl(url: string, revision: number): string {
  const bare = url.replace(/([?&])v=\d+(&|$)/, (_m, lead: string, tail: string) => (tail === '&' ? lead : '')).replace(/[?&]$/, '');
  return `${bare}${bare.includes('?') ? '&' : '?'}v=${revision}`;
}

export class LayerMaskBuffer {
  readonly width: number;
  readonly height: number;
  /** The visible amount of every pixel, row by row. */
  readonly data: Uint8ClampedArray;
  private undoStack: Uint8ClampedArray[] = [];
  private redoStack: Uint8ClampedArray[] = [];
  private dirty: Rect | null = null;
  private unsaved = false;

  constructor(width: number, height: number, data?: Uint8ClampedArray) {
    if (!(width > 0) || !(height > 0)) throw new Error('A mask needs a size.');
    this.width = Math.floor(width);
    this.height = Math.floor(height);
    this.data = data ? new Uint8ClampedArray(data) : new Uint8ClampedArray(this.width * this.height).fill(255);
    if (this.data.length !== this.width * this.height) throw new Error('The mask pixels do not match its size.');
  }

  /** Reads a mask from RGBA pixels whose ALPHA is the visible amount (the compositor's matte bitmap). */
  static fromAlphaRgba(width: number, height: number, rgba: Uint8ClampedArray): LayerMaskBuffer {
    const data = new Uint8ClampedArray(width * height);
    for (let i = 0; i < data.length; i += 1) data[i] = rgba[i * 4 + 3];
    return new LayerMaskBuffer(width, height, data);
  }

  /** True when nothing is hidden: such a mask changes nothing. */
  get isAllVisible(): boolean {
    for (let i = 0; i < this.data.length; i += 1) if (this.data[i] !== 255) return false;
    return true;
  }

  /** True when the pixels differ from the last {@link markSaved}. */
  get changed(): boolean {
    return this.unsaved;
  }

  get canUndo(): boolean {
    return this.undoStack.length > 0;
  }

  get canRedo(): boolean {
    return this.redoStack.length > 0;
  }

  markSaved(): void {
    this.unsaved = false;
  }

  /** Remembers the pixels so the next change can be undone. Call once per stroke or whole-mask change. */
  checkpoint(): void {
    this.undoStack.push(this.data.slice());
    this.redoStack = [];
    const per = Math.max(1, this.data.length);
    const cap = Math.max(1, Math.min(MASK_UNDO_LIMIT, Math.floor(MASK_UNDO_BYTES / per)));
    while (this.undoStack.length > cap) this.undoStack.shift();
    this.unsaved = true;
  }

  undo(): boolean {
    const previous = this.undoStack.pop();
    if (!previous) return false;
    this.redoStack.push(this.data.slice());
    this.data.set(previous);
    this.unsaved = true;
    this.touch({ x: 0, y: 0, w: this.width, h: this.height });
    return true;
  }

  redo(): boolean {
    const next = this.redoStack.pop();
    if (!next) return false;
    this.undoStack.push(this.data.slice());
    this.data.set(next);
    this.unsaved = true;
    this.touch({ x: 0, y: 0, w: this.width, h: this.height });
    return true;
  }

  /** Hides what is shown and shows what is hidden. */
  invert(): void {
    this.checkpoint();
    for (let i = 0; i < this.data.length; i += 1) this.data[i] = 255 - this.data[i];
    this.touch({ x: 0, y: 0, w: this.width, h: this.height });
  }

  /** Shows everything again. */
  reveal(): void {
    this.checkpoint();
    this.data.fill(255);
    this.touch({ x: 0, y: 0, w: this.width, h: this.height });
  }

  /** Paints a round-capped segment from `from` to `to` in layer pixels (a zero-length one is a dot). */
  stroke(from: Point, to: Point, brush: MaskBrush): void {
    const radius = Math.max(0.5, brush.size / 2);
    const hardness = Math.min(1, Math.max(0, brush.hardness));
    const dx = to.x - from.x;
    const dy = to.y - from.y;
    const length = Math.hypot(dx, dy);
    const spacing = Math.max(1, radius * 0.25);
    const steps = Math.max(1, Math.ceil(length / spacing));
    for (let s = 0; s <= steps; s += 1) {
      const t = length === 0 ? 0 : s / steps;
      this.stamp(from.x + dx * t, from.y + dy * t, radius, hardness, brush.hide);
    }
  }

  private stamp(cx: number, cy: number, radius: number, hardness: number, hide: boolean): void {
    const x0 = Math.max(0, Math.floor(cx - radius));
    const y0 = Math.max(0, Math.floor(cy - radius));
    const x1 = Math.min(this.width - 1, Math.ceil(cx + radius));
    const y1 = Math.min(this.height - 1, Math.ceil(cy + radius));
    if (x1 < x0 || y1 < y0) return;
    const solid = radius * hardness;
    const fade = radius - solid;
    for (let y = y0; y <= y1; y += 1) {
      for (let x = x0; x <= x1; x += 1) {
        const d = Math.hypot(x + 0.5 - cx, y + 0.5 - cy);
        if (d > radius) continue;
        const a = d <= solid || fade <= 0 ? 1 : (radius - d) / fade;
        const at = y * this.width + x;
        const v = this.data[at];
        this.data[at] = hide ? v * (1 - a) : v + (255 - v) * a;
      }
    }
    this.touch({ x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 });
  }

  private touch(rect: Rect): void {
    if (!this.dirty) {
      this.dirty = rect;
      return;
    }
    const x = Math.min(this.dirty.x, rect.x);
    const y = Math.min(this.dirty.y, rect.y);
    const right = Math.max(this.dirty.x + this.dirty.w, rect.x + rect.w);
    const bottom = Math.max(this.dirty.y + this.dirty.h, rect.y + rect.h);
    this.dirty = { x, y, w: right - x, h: bottom - y };
  }

  /** The area changed since the last call (the display redraws only that), or null. */
  takeDirty(): Rect | null {
    const rect = this.dirty;
    this.dirty = null;
    return rect;
  }

  /** RGBA for the display: white, with the visible amount as alpha (what `destination-in` reads). */
  toAlphaRgba(rect: Rect = { x: 0, y: 0, w: this.width, h: this.height }): Uint8ClampedArray {
    const out = new Uint8ClampedArray(rect.w * rect.h * 4);
    for (let y = 0; y < rect.h; y += 1) {
      for (let x = 0; x < rect.w; x += 1) {
        const o = (y * rect.w + x) * 4;
        out[o] = 255;
        out[o + 1] = 255;
        out[o + 2] = 255;
        out[o + 3] = this.data[(rect.y + y) * this.width + rect.x + x];
      }
    }
    return out;
  }

  /** RGBA for the upload: opaque gray, luminance = visible amount. */
  toGrayRgba(): Uint8ClampedArray {
    const out = new Uint8ClampedArray(this.data.length * 4);
    for (let i = 0; i < this.data.length; i += 1) {
      const v = this.data[i];
      out[i * 4] = v;
      out[i * 4 + 1] = v;
      out[i * 4 + 2] = v;
      out[i * 4 + 3] = 255;
    }
    return out;
  }
}

// ── upload ────────────────────────────────────────────────────────────────────────────────────

export type MaskSaveState =
  | { phase: 'idle' }
  | { phase: 'saving'; attempt: number }
  | { phase: 'failed'; message: string }
  | { phase: 'saved' };

/** The waits before each further attempt after a network blip or a 5xx. */
export const MASK_SAVE_BACKOFF_MS: readonly number[] = [600, 1_800, 4_000];

export interface MaskSaveOptions<T> {
  send(): Promise<T>;
  onState(state: MaskSaveState): void;
  /** Waits; a test passes an instant one. */
  sleep?(ms: number): Promise<void>;
  /** The HTTP status of a failure, when it has one. */
  statusOf(cause: unknown): number | null;
  messageOf(cause: unknown): string;
}

/** Plain sentences for the failures the user can act on. */
export function maskSaveMessage(status: number | null, code: string | null, fallback: string): string {
  if (status === 409) return 'This picture was changed somewhere else. Your mask is kept here; reload to continue.';
  if (status === 413 || status === 402) return 'The mask could not be saved because your storage is full. Free some space and try again.';
  if (code === 'MATTE_SIZE_MISMATCH') return 'The mask does not match the size of this layer, so it was not saved.';
  if (status === 404) return 'This layer is no longer there, so the mask could not be saved.';
  if (status === null || status >= 500) return 'The mask could not be saved. Check your connection and try again; your painting is kept.';
  return fallback;
}

/**
 * Sends a mask, retrying only failures that can pass (no answer, 5xx). The caller keeps the bitmap
 * until this resolves, so a failure never loses the painting; the final state says why in plain words.
 * Rejects with the last error after leaving the state `failed`.
 */
export async function saveWithRetry<T>(options: MaskSaveOptions<T>): Promise<T> {
  const sleep = options.sleep ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  let attempt = 0;
  for (;;) {
    options.onState({ phase: 'saving', attempt });
    try {
      const result = await options.send();
      options.onState({ phase: 'saved' });
      return result;
    } catch (cause: unknown) {
      const status = options.statusOf(cause);
      const retryable = status === null || status >= 500;
      if (retryable && attempt < MASK_SAVE_BACKOFF_MS.length) {
        await sleep(MASK_SAVE_BACKOFF_MS[attempt]);
        attempt += 1;
        continue;
      }
      options.onState({ phase: 'failed', message: options.messageOf(cause) });
      throw cause;
    }
  }
}
