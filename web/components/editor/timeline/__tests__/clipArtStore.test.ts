/** @vitest-environment node */
import { describe, expect, it, vi } from 'vitest';
import { media } from '@/lib/editing/__tests__/fixtures';
import { ClipArtStore, type ClipArtLoaders } from '../clipArtStore';

const filmstrip = {
  filmstrip: {
    densities: [{ densitySec: 1, thumbWidth: 160, thumbHeight: 90, cols: 2, rows: 2, sheetCount: 1, sheets: [{ firstThumbIndex: 0, thumbCount: 4, firstTimeSec: 0, contentUrl: '/sheet/0' }] }],
  },
};

function loaders(over: Partial<ClipArtLoaders> = {}): ClipArtLoaders {
  const peaks = new Int16Array([-1000, 1000, -2000, 2000, -1000, 1000, -2000, 2000]);
  return {
    derivatives: vi.fn(async () => [{ id: 'd1', kind: 'waveformPeaks' as const, mimeType: null, sizeBytes: peaks.byteLength, durationSec: 4, width: null, height: null, contentUrl: '/peaks/d1' }]),
    filmstrip: vi.fn(async () => filmstrip),
    bytes: vi.fn(async () => peaks.buffer.slice(0) as ArrayBuffer),
    image: vi.fn(async () => ({}) as CanvasImageSource),
    ...over,
  };
}
const flush = () => new Promise((r) => setTimeout(r, 0));

describe('R03.4 ClipArtStore', () => {
  const clip = media('c1', 0, 96);
  it('loads once per source, then answers peaks, duration and thumbnails synchronously', async () => {
    const l = loaders();
    const store = new ClipArtStore('p1', l);
    const seen = vi.fn();
    store.subscribe(seen);
    expect(store.peaks(clip)).toBeNull(); // not arrived yet
    store.peaks(clip);
    await flush();
    await flush();
    expect(l.derivatives).toHaveBeenCalledTimes(1);
    expect(seen).toHaveBeenCalled();
    expect(store.peaks(clip)?.channels).toBe(2);
    expect(store.sourceDurationSec(clip)).toBeGreaterThan(0);
    expect(store.thumbAspect(clip)).toBeCloseTo(160 / 90);
    expect(store.thumb(clip, 1)).toBeNull(); // sheet image requested
    await flush();
    const cell = store.thumb(clip, 1);
    expect(cell).toMatchObject({ sx: 160, sy: 0, sw: 160, sh: 90 });
  });
  it('T-023 a source whose length never arrives is "not checked" only once the lookup settled', async () => {
    const store = new ClipArtStore('p1', loaders({ derivatives: async () => [], filmstrip: async () => { throw new Error('none'); } }));
    expect(store.lengthUnchecked(clip)).toBe(false); // still loading
    await flush();
    await flush();
    expect(store.sourceDurationSec(clip)).toBeNull();
    expect(store.lengthUnchecked(clip)).toBe(true);
    const known = new ClipArtStore('p1', loaders());
    known.sourceDurationSec(clip);
    await flush();
    await flush();
    expect(known.lengthUnchecked(clip)).toBe(false);
  });
  it('a source that 404s everywhere is offline; other items never are', async () => {
    const err = Object.assign(new Error('nope'), { status: 404 });
    const store = new ClipArtStore('p1', loaders({ derivatives: async () => { throw err; }, filmstrip: async () => { throw err; } }));
    store.offline(clip);
    await flush();
    await flush();
    expect(store.offline(clip)).toBe(true);
    expect(store.offline({ ...clip, type: 'titleClip' } as never)).toBe(false);
  });
});
