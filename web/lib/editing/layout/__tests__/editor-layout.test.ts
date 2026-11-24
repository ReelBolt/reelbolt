import { describe, expect, it } from 'vitest';
import {
  LAYOUT_MINIMUMS,
  PAGE_GEOMETRY,
  fractionsToPixels,
  layoutStorageKey,
  migratePoolPanes,
  parseLayoutRecord,
  pageFractions,
  pixelsToFractions,
  readLayoutRecord,
  resetAllPages,
  resetPage,
  resolvePagePixels,
  setPageFractions,
  writeLayoutRecord,
  type LayoutRecord,
  type LayoutStorage,
} from '../editor-layout';
import { POOL_PANES_STORAGE_KEY } from '../../media/pool-panes';

class MemoryStorage implements LayoutStorage {
  private readonly values = new Map<string, string>();
  getItem(key: string): string | null {
    return this.values.get(key) ?? null;
  }
  setItem(key: string, value: string): void {
    this.values.set(key, value);
  }
  removeItem(key: string): void {
    this.values.delete(key);
  }
}

describe('the geometry table (§6.1)', () => {
  it('declares every page’s zones and the default sizes', () => {
    expect(PAGE_GEOMETRY.media.left).toMatchObject({ kind: 'bins', width: 280 });
    expect(PAGE_GEOMETRY.media.right).toMatchObject({ kind: 'viewer-metadata', width: 420 });
    expect(PAGE_GEOMETRY.media.timeline).toBe(200);
    expect(PAGE_GEOMETRY.media.timelineVisible).toBe(false);
    expect(PAGE_GEOMETRY.media.timelineOnDemand).toBe(200);
    expect(PAGE_GEOMETRY.cut).toMatchObject({ timeline: 240, overview: 120 });
    expect(PAGE_GEOMETRY.edit).toMatchObject({ timeline: 320 });
    expect(PAGE_GEOMETRY.edit.left?.width).toBe(300);
    expect(PAGE_GEOMETRY.edit.right?.width).toBe(320);
    expect(PAGE_GEOMETRY.compose.left?.width).toBe(340);
    expect(PAGE_GEOMETRY.compose.right?.width).toBe(340);
    expect(PAGE_GEOMETRY.compose.timeline).toBe(180);
    expect(PAGE_GEOMETRY.colour.right?.width).toBe(360);
    expect(PAGE_GEOMETRY.audio.left?.width).toBe(360);
    expect(PAGE_GEOMETRY.audio.right?.width).toBe(320);
    expect(PAGE_GEOMETRY.audio.timeline).toBe(280);
  });

  it('is a two-column page with no centre and no timeline on Deliver', () => {
    expect(PAGE_GEOMETRY.deliver.centre).toBeNull();
    expect(PAGE_GEOMETRY.deliver.timeline).toBeNull();
    expect(PAGE_GEOMETRY.deliver.viewer).toBe('none');
    expect(PAGE_GEOMETRY.deliver.right?.fills).toBe(true);
  });
});

describe('fractions are authoritative and pixels are derived', () => {
  it('opens a fresh page at the table sizes', () => {
    expect(resolvePagePixels('edit', {}, { width: 1280, height: 900 })).toEqual({ left: 300, right: 320, timeline: 320, overview: 0 });
    expect(resolvePagePixels('media', {}, { width: 1280, height: 900 })).toEqual({ left: 280, right: 420, timeline: 200, overview: 0 });
    expect(resolvePagePixels('cut', {}, { width: 1280, height: 900 })).toEqual({ left: 320, right: 320, timeline: 240, overview: 120 });
  });

  it('clamps a tiny fraction to the minimums', () => {
    const pixels = fractionsToPixels('cut', { left: 0.001, right: 0.001, timeline: 0.001, overview: 0.001 }, { width: 800, height: 600 });
    expect(pixels.left).toBe(LAYOUT_MINIMUMS.side);
    expect(pixels.right).toBe(LAYOUT_MINIMUMS.side);
    expect(pixels.timeline).toBe(LAYOUT_MINIMUMS.timeline);
    expect(pixels.overview).toBe(LAYOUT_MINIMUMS.overview);
  });

  it('never squeezes the centre under its minimum', () => {
    const pixels = fractionsToPixels('edit', { left: 0.5, right: 0.5, timeline: 0.5 }, { width: 1000, height: 600 });
    expect(pixels.left).toBe(240);
    expect(pixels.right).toBe(440);
    expect(pixels.left + pixels.right).toBeLessThanOrEqual(1000 - LAYOUT_MINIMUMS.centre);
  });

  it('round-trips a layout across a window resize', () => {
    const first = { width: 1200, height: 800 };
    const fractions = pixelsToFractions('edit', { left: 300, right: 320, timeline: 320, overview: 0 }, first);
    expect(fractions).toEqual({ left: 0.25, right: 0.2667, timeline: 0.4 });
    const second = { width: 1600, height: 1000 };
    const pixels = fractionsToPixels('edit', fractions, second);
    expect(pixels.left).toBe(400);
    expect(pixels.right).toBe(427);
    expect(pixels.timeline).toBe(400);
    const again = pixelsToFractions('edit', pixels, second);
    expect(again.left).toBeCloseTo(0.25, 3);
    expect(again.right).toBeCloseTo(0.2667, 3);
    expect(again.timeline).toBeCloseTo(0.4, 3);
  });

  it('fills Deliver’s result column with the rest of the width', () => {
    expect(resolvePagePixels('deliver', {}, { width: 1000, height: 700 })).toEqual({ left: 420, right: 580, timeline: 0, overview: 0 });
  });
});

describe('hydration order and the record', () => {
  it('uses the stored fractions for that page, else the table default', () => {
    const record: LayoutRecord = { media: { fractions: { left: 0.3, right: 0.4 } } };
    expect(pageFractions(record, 'media')).toEqual({ left: 0.3, right: 0.4 });
    expect(resolvePagePixels('media', record, { width: 1280, height: 700 }).left).toBe(384);
    expect(resolvePagePixels('media', record, { width: 1280, height: 700 }).right).toBe(512);
    expect(resolvePagePixels('media', {}, { width: 1280, height: 700 }).left).toBe(280);
    expect(resolvePagePixels('media', {}, { width: 1280, height: 700 }).right).toBe(420);
  });

  it('keeps one record per page and per user when the id is known', () => {
    expect(layoutStorageKey()).toBe('reelbolt:editor:layout:v2');
    expect(layoutStorageKey('user-1')).toBe('reelbolt:editor:layout:v2:user-1');
    const storage = new MemoryStorage();
    writeLayoutRecord(storage, setPageFractions({}, 'edit', { left: 0.2, timeline: 0.3 }), 'user-1');
    expect(readLayoutRecord(storage, 'user-1').edit?.fractions).toEqual({ left: 0.2, timeline: 0.3 });
    expect(readLayoutRecord(storage).edit).toBeUndefined();
  });

  it('drops a bad fraction or a bad page instead of trusting it', () => {
    expect(parseLayoutRecord('not json')).toEqual({});
    expect(parseLayoutRecord('{"edit":{"fractions":{"left":2}}}')).toEqual({});
    expect(parseLayoutRecord('{"nope":{"fractions":{"left":0.2}}}')).toEqual({});
    expect(parseLayoutRecord('{"edit":{"fractions":{"left":0.2}}}')).toEqual({ edit: { fractions: { left: 0.2 } } });
  });

  it('treats unavailable storage as defaults', () => {
    expect(readLayoutRecord(null)).toEqual({});
    expect(() => writeLayoutRecord(null, {})).not.toThrow();
  });
});

describe('the two resets', () => {
  it('resets this page only', () => {
    const record: LayoutRecord = { edit: { fractions: { left: 0.2 } }, media: { fractions: { left: 0.3 } } };
    expect(resetPage(record, 'edit')).toEqual({ media: { fractions: { left: 0.3 } } });
  });

  it('resets every page', () => {
    expect(resetAllPages()).toEqual({});
  });
});

describe('the pool-panes migration', () => {
  it('reads the legacy key once, maps it into the Media page and deletes it', () => {
    const storage = new MemoryStorage();
    storage.setItem(POOL_PANES_STORAGE_KEY, JSON.stringify({ binsWidth: 320, detailWidth: 400, binsCollapsed: false, detailCollapsed: false }));
    const first = migratePoolPanes(storage, { width: 1600, height: 900 }, 'user-1');
    expect(first.migrated).toBe(true);
    expect(storage.getItem(POOL_PANES_STORAGE_KEY)).toBeNull();
    expect(readLayoutRecord(storage, 'user-1').media?.fractions).toMatchObject({ left: 0.2, right: 0.25 });
    const second = migratePoolPanes(storage, { width: 1600, height: 900 }, 'user-1');
    expect(second.migrated).toBe(false);
  });

  it('keeps media fractions that already exist, and still deletes the legacy key', () => {
    const storage = new MemoryStorage();
    writeLayoutRecord(storage, { media: { fractions: { left: 0.1, right: 0.2 } } }, 'user-1');
    storage.setItem(POOL_PANES_STORAGE_KEY, JSON.stringify({ binsWidth: 400, detailWidth: 500 }));
    const result = migratePoolPanes(storage, { width: 1600, height: 900 }, 'user-1');
    expect(result.record.media?.fractions).toEqual({ left: 0.1, right: 0.2 });
    expect(storage.getItem(POOL_PANES_STORAGE_KEY)).toBeNull();
  });
});
