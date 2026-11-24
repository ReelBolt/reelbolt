import { describe, expect, it } from 'vitest';
import {
  LayerMaskBuffer,
  MASK_SAVE_BACKOFF_MS,
  lumaToAlphaInPlace,
  maskSaveMessage,
  saveWithRetry,
  versionedMatteUrl,
  type MaskSaveState,
} from './layer-mask';

const HIDE = { size: 4, hardness: 1, hide: true } as const;
const SHOW = { size: 4, hardness: 1, hide: false } as const;

describe('LayerMaskBuffer', () => {
  it('starts fully visible', () => {
    const b = new LayerMaskBuffer(8, 8);
    expect(b.isAllVisible).toBe(true);
    expect(b.changed).toBe(false);
  });

  it('paints Hide to 0 under the brush and leaves the rest visible', () => {
    const b = new LayerMaskBuffer(20, 20);
    b.checkpoint();
    b.stroke({ x: 10, y: 10 }, { x: 10, y: 10 }, HIDE);
    expect(b.data[10 * 20 + 10]).toBe(0);
    expect(b.data[0]).toBe(255);
    expect(b.isAllVisible).toBe(false);
    expect(b.changed).toBe(true);
  });

  it('paints Show back to 255', () => {
    const b = new LayerMaskBuffer(20, 20);
    b.checkpoint();
    b.stroke({ x: 10, y: 10 }, { x: 10, y: 10 }, HIDE);
    b.stroke({ x: 10, y: 10 }, { x: 10, y: 10 }, SHOW);
    expect(b.isAllVisible).toBe(true);
  });

  it('a soft brush leaves partial values near the edge', () => {
    const b = new LayerMaskBuffer(40, 40);
    b.stroke({ x: 20, y: 20 }, { x: 20, y: 20 }, { size: 20, hardness: 0, hide: true });
    const edge = b.data[20 * 40 + 28];
    expect(edge).toBeGreaterThan(0);
    expect(edge).toBeLessThan(255);
  });

  it('covers the whole segment, not just its ends', () => {
    const b = new LayerMaskBuffer(40, 10);
    b.stroke({ x: 5, y: 5 }, { x: 35, y: 5 }, HIDE);
    expect(b.data[5 * 40 + 20]).toBe(0);
  });

  it('clips a stroke at the edge of the layer', () => {
    const b = new LayerMaskBuffer(10, 10);
    b.stroke({ x: -50, y: -50 }, { x: -40, y: -40 }, HIDE);
    expect(b.isAllVisible).toBe(true);
    b.stroke({ x: 0, y: 0 }, { x: 0, y: 0 }, HIDE);
    expect(b.data[0]).toBe(0);
  });

  it('inverts and reveals, each as one undo step', () => {
    const b = new LayerMaskBuffer(6, 6);
    b.checkpoint();
    b.stroke({ x: 3, y: 3 }, { x: 3, y: 3 }, HIDE);
    b.invert();
    expect(b.data[3 * 6 + 3]).toBe(255);
    expect(b.data[0]).toBe(0);
    b.reveal();
    expect(b.isAllVisible).toBe(true);
    expect(b.undo()).toBe(true);
    expect(b.data[0]).toBe(0);
    expect(b.undo()).toBe(true);
    expect(b.data[3 * 6 + 3]).toBe(0);
    expect(b.data[0]).toBe(255);
  });

  it('undo and redo walk the strokes; a new stroke drops the redo', () => {
    const b = new LayerMaskBuffer(10, 10);
    b.checkpoint();
    b.stroke({ x: 2, y: 2 }, { x: 2, y: 2 }, HIDE);
    b.checkpoint();
    b.stroke({ x: 7, y: 7 }, { x: 7, y: 7 }, HIDE);
    b.undo();
    expect(b.data[7 * 10 + 7]).toBe(255);
    expect(b.data[2 * 10 + 2]).toBe(0);
    expect(b.canRedo).toBe(true);
    b.redo();
    expect(b.data[7 * 10 + 7]).toBe(0);
    b.undo();
    b.checkpoint();
    expect(b.canRedo).toBe(false);
    const empty = new LayerMaskBuffer(2, 2);
    expect(empty.undo()).toBe(false);
    expect(empty.redo()).toBe(false);
  });

  it('markSaved clears changed until the next edit', () => {
    const b = new LayerMaskBuffer(4, 4);
    b.invert();
    expect(b.changed).toBe(true);
    b.markSaved();
    expect(b.changed).toBe(false);
    b.undo();
    expect(b.changed).toBe(true);
  });

  it('tracks the dirty rectangle and hands it out once', () => {
    const b = new LayerMaskBuffer(30, 30);
    b.stroke({ x: 10, y: 10 }, { x: 10, y: 10 }, HIDE);
    const rect = b.takeDirty();
    expect(rect).not.toBeNull();
    expect(rect!.x).toBeLessThanOrEqual(10);
    expect(b.takeDirty()).toBeNull();
  });

  it('maps the mask to display alpha and to upload gray', () => {
    const b = new LayerMaskBuffer(2, 1, new Uint8ClampedArray([0, 200]));
    expect(Array.from(b.toAlphaRgba())).toEqual([255, 255, 255, 0, 255, 255, 255, 200]);
    expect(Array.from(b.toGrayRgba())).toEqual([0, 0, 0, 255, 200, 200, 200, 255]);
  });

  it('reads a mask back from matte alpha', () => {
    const b = LayerMaskBuffer.fromAlphaRgba(2, 1, new Uint8ClampedArray([255, 255, 255, 10, 255, 255, 255, 250]));
    expect(Array.from(b.data)).toEqual([10, 250]);
  });
});

describe('lumaToAlphaInPlace', () => {
  it('makes luminance the alpha and the colour white', () => {
    const px = new Uint8ClampedArray([0, 0, 0, 255, 255, 255, 255, 255, 128, 128, 128, 255]);
    lumaToAlphaInPlace(px);
    expect(Array.from(px)).toEqual([255, 255, 255, 0, 255, 255, 255, 255, 255, 255, 255, 128]);
  });
});

describe('versionedMatteUrl', () => {
  it('adds, replaces and keeps other query parts', () => {
    expect(versionedMatteUrl('/a/matte', 3)).toBe('/a/matte?v=3');
    expect(versionedMatteUrl('/a/matte?v=3', 4)).toBe('/a/matte?v=4');
    expect(versionedMatteUrl('/a/matte?sig=x', 4)).toBe('/a/matte?sig=x&v=4');
  });
});

describe('saveWithRetry', () => {
  const statusOf = (c: unknown) => (c as { status?: number | null }).status ?? null;
  const run = (outcomes: Array<'ok' | { status: number | null }>) => {
    const states: MaskSaveState[] = [];
    const slept: number[] = [];
    let calls = 0;
    const promise = saveWithRetry({
      send: async () => {
        const next = outcomes[Math.min(calls, outcomes.length - 1)];
        calls += 1;
        if (next === 'ok') return 'done';
        throw next;
      },
      onState: (s) => states.push(s),
      sleep: async (ms) => {
        slept.push(ms);
      },
      statusOf,
      messageOf: (c) => maskSaveMessage(statusOf(c), null, 'fallback'),
    });
    return { promise, states, slept, calls: () => calls };
  };

  it('retries a network failure, then succeeds', async () => {
    const r = run([{ status: null }, 'ok']);
    await expect(r.promise).resolves.toBe('done');
    expect(r.slept).toEqual([MASK_SAVE_BACKOFF_MS[0]]);
    expect(r.states.at(-1)).toEqual({ phase: 'saved' });
  });

  it('gives up after the backoff list and reports a plain failure (the caller keeps the bitmap)', async () => {
    const r = run([{ status: 503 }]);
    await expect(r.promise).rejects.toMatchObject({ status: 503 });
    expect(r.calls()).toBe(MASK_SAVE_BACKOFF_MS.length + 1);
    const last = r.states.at(-1);
    expect(last?.phase).toBe('failed');
    expect(last && last.phase === 'failed' && last.message).toMatch(/your painting is kept/);
  });

  it('does not retry a refusal', async () => {
    const r = run([{ status: 413 }]);
    await expect(r.promise).rejects.toBeDefined();
    expect(r.calls()).toBe(1);
    const last = r.states.at(-1);
    expect(last && last.phase === 'failed' && last.message).toMatch(/storage/);
  });

  it('says a stale document kept the mask', () => {
    expect(maskSaveMessage(409, null, 'x')).toMatch(/kept here/);
  });
});
