/** @vitest-environment node */
/**
 * Unit tests for the hover-scrub arithmetic.
 *
 * The fixture index mirrors the LANDED wire shape: density 1 s, a 10x10 tile area, one full
 * 100-thumbnail sheet and a PARTIAL second sheet of 37 — so the boundary cases that actually go
 * wrong in production (the sheet edge and the last, incomplete sheet) are the ones covered here.
 *
 * The index is now real data rather than a shape the API could not supply, so these tests also pin
 * the two things that replaced the old gap: a sheet is addressed by its OWN `contentUrl` (it has no
 * storage leaf on the wire, and sheets 1..N-1 have no derivative row of their own), and the total
 * scrub length comes from the measured thumbnail count rather than from a duration.
 */

import { describe, expect, it, vi } from 'vitest';

import {
  filmstripCellBackground,
  filmstripDurationSec,
  resolveFilmstripThumb,
  type SheetUrlResolver,
} from '../filmstrip';
import { FILMSTRIP_INDEX, FILMSTRIP_INDEX_NO_SHEETS } from '../fixtures';
import type { FilmstripIndex } from '../types';

/** A resolver that yields a URL derived from the sheet's own content path. */
const urlResolver: SheetUrlResolver = (sheet) => `/sheets/${sheet.contentUrl}`;

/** A resolver that never produces a URL — the per-sheet degradation. */
const noUrlResolver: SheetUrlResolver = () => null;

/** A sheet literal with the shape the wire actually carries. */
const sheet = (
  name: string,
  firstThumbIndex: number,
  thumbCount: number,
  firstTimeSec: number,
): FilmstripIndex['densities'][number]['sheets'][number] => ({
  firstThumbIndex,
  thumbCount,
  firstTimeSec,
  contentUrl: name,
});

const withSheets = (...sheets: FilmstripIndex['densities'][number]['sheets']): FilmstripIndex => ({
  densities: [
    { densitySec: 1, thumbWidth: 160, thumbHeight: 90, cols: 10, rows: 10, sheetCount: sheets.length, sheets },
  ],
});

describe('resolveFilmstripThumb', () => {
  it('resolves time 0 to the first cell of the first sheet', () => {
    const position = resolveFilmstripThumb(FILMSTRIP_INDEX, 0, urlResolver);

    expect(position).not.toBeNull();
    expect(position).toMatchObject({
      densitySec: 1,
      sheetIndex: 0,
      thumbIndex: 0,
      thumbX: 0,
      thumbY: 0,
      cols: 10,
      rows: 10,
      thumbTimeSec: 0,
    });
    // The URL comes from THIS sheet's own content path — never from a rebuilt storage leaf.
    expect(position?.sheetUrl).toContain('/sheet/0/content');
  });

  it('resolves a mid-sheet time to the right column and row', () => {
    // Thumbnail 25 of a 10-column sheet is column 5, row 2.
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 25, urlResolver)).toMatchObject({
      sheetIndex: 0,
      thumbIndex: 25,
      thumbX: 5,
      thumbY: 2,
    });
  });

  it('resolves the last cell of a sheet, and the first cell of the next', () => {
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 99.999, urlResolver)).toMatchObject({
      sheetIndex: 0,
      thumbIndex: 99,
      thumbX: 9,
      thumbY: 9,
    });

    // Time 100 is exactly the boundary: the first thumbnail of sheet 2, not the 101st of sheet 1.
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 100, urlResolver)).toMatchObject({
      sheetIndex: 1,
      thumbIndex: 100,
      thumbX: 0,
      thumbY: 0,
      // Measured, not multiplied out of the density: the sidecar recorded 100 s.
      thumbTimeSec: 100,
    });
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 100, urlResolver)?.sheetUrl).toContain(
      '/sheet/1/content',
    );
  });

  it('clamps a time past the last thumbnail to the last thumbnail', () => {
    // Sheet 2 carries only 37 of its 100 possible cells, so the last addressable thumbnail is 136.
    const position = resolveFilmstripThumb(FILMSTRIP_INDEX, 500, urlResolver);

    expect(position).toMatchObject({
      sheetIndex: 1,
      thumbIndex: 136,
      thumbX: 6,
      thumbY: 3,
    });
  });

  it('never addresses a cell beyond a partially filled final sheet', () => {
    // The 38th cell of sheet 2 (index 137) does not exist, and no time may resolve to it.
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 137, urlResolver)?.thumbIndex).toBe(136);
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 10_000, urlResolver)?.thumbIndex).toBe(136);

    // The last REAL cell is addressable and in range of the sheet's own thumb count.
    const last = resolveFilmstripThumb(FILMSTRIP_INDEX, 136, urlResolver);
    expect(last?.thumbIndex).toBe(136);
    expect(last?.thumbX).toBe(6);
    expect(last?.thumbY).toBe(3);
  });

  it('returns null for a thumbnail index that falls in a gap between sheets', () => {
    // Sheet 1 covers 0..9 and sheet 2 starts at 20: index 12 is addressable by no sheet at all.
    const gapped = withSheets(sheet('a.jpg', 0, 10, 0), sheet('c.jpg', 20, 5, 20));

    expect(resolveFilmstripThumb(gapped, 12, urlResolver)).toBeNull();
    // ...and the sheets either side of the gap still resolve.
    expect(resolveFilmstripThumb(gapped, 5, urlResolver)?.sheetIndex).toBe(0);
    expect(resolveFilmstripThumb(gapped, 20, urlResolver)?.sheetIndex).toBe(1);
  });

  it('returns null when the index carries no sheets', () => {
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX_NO_SHEETS, 1, urlResolver)).toBeNull();
    expect(resolveFilmstripThumb({ densities: [] }, 1, urlResolver)).toBeNull();
    expect(resolveFilmstripThumb(null, 1, urlResolver)).toBeNull();
    expect(resolveFilmstripThumb(undefined, 1, urlResolver)).toBeNull();
  });

  it('returns null rather than a NaN cell for an unusable time', () => {
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, Number.NaN, urlResolver)).toBeNull();
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, Number.POSITIVE_INFINITY, urlResolver)).toBeNull();
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, -1, urlResolver)).toBeNull();
  });

  it('returns null rather than dividing by an unusable density or geometry', () => {
    const zeroDensity: FilmstripIndex = {
      densities: [
        {
          densitySec: 0,
          thumbWidth: 160,
          thumbHeight: 90,
          cols: 10,
          rows: 10,
          sheetCount: 1,
          sheets: [sheet('a.jpg', 0, 10, 0)],
        },
      ],
    };
    expect(resolveFilmstripThumb(zeroDensity, 1, urlResolver)).toBeNull();

    const zeroColumns: FilmstripIndex = {
      densities: [
        {
          densitySec: 1,
          thumbWidth: 160,
          thumbHeight: 90,
          cols: 0,
          rows: 10,
          sheetCount: 1,
          sheets: [sheet('a.jpg', 0, 10, 0)],
        },
      ],
    };
    expect(resolveFilmstripThumb(zeroColumns, 1, urlResolver)).toBeNull();
  });

  it('skips a density that carries no sheets and uses the next one that does', () => {
    const index: FilmstripIndex = {
      densities: [
        { densitySec: 1, thumbWidth: 160, thumbHeight: 90, cols: 10, rows: 10, sheetCount: 0, sheets: [] },
        {
          densitySec: 2,
          thumbWidth: 320,
          thumbHeight: 180,
          cols: 5,
          rows: 5,
          sheetCount: 1,
          sheets: [sheet('b.jpg', 0, 25, 0)],
        },
      ],
    };

    const position = resolveFilmstripThumb(index, 4, urlResolver);
    expect(position).toMatchObject({ densitySec: 2, cols: 5, rows: 5, thumbIndex: 2, thumbX: 2, thumbY: 0 });
  });

  it('ignores a sheet entry with no usable thumb count', () => {
    const index = withSheets(
      sheet('empty.jpg', 0, 0, 0),
      sheet('real.jpg', 100, 4, 100),
    );

    // The only usable sheet starts at thumbnail 100, so the first 100 thumbnails are covered by
    // nothing at all. Time 0 addresses a cell no sheet holds, and the honest answer is null — not a
    // clamped cell from a sheet that disclaims it.
    expect(resolveFilmstripThumb(index, 0, urlResolver)).toBeNull();

    // Inside the usable sheet's own range it resolves, against that sheet.
    expect(resolveFilmstripThumb(index, 100, urlResolver)).toMatchObject({
      thumbIndex: 100,
      sheetIndex: 0,
    });
    expect(resolveFilmstripThumb(index, 100, urlResolver)?.sheetUrl).toBe('/sheets/real.jpg');
    // And its own 4-thumb range is honoured: index 103 is the last one it can address.
    expect(resolveFilmstripThumb(index, 10_000, urlResolver)?.thumbIndex).toBe(103);
  });

  it('KEEPS a sheet whose content URL is empty, so later thumbnails are not renumbered', () => {
    // Dropping an unusable sheet would shift every following thumbnail by one, which is precisely
    // how a scrub lands on the wrong frame. The cell is still addressed; only its image is missing.
    const index = withSheets(sheet('', 0, 10, 0), sheet('later.jpg', 10, 10, 10));

    const position = resolveFilmstripThumb(index, 5, urlResolver);
    expect(position?.thumbIndex).toBe(5);
    expect(position?.sheetIndex).toBe(0);
    expect(position?.sheetUrl).toBe('/sheets/');

    expect(resolveFilmstripThumb(index, 15, urlResolver)).toMatchObject({
      thumbIndex: 15,
      sheetIndex: 1,
      sheetUrl: '/sheets/later.jpg',
    });
  });

  it('returns a resolved cell with a null URL when no sheet URL can be built', () => {
    const position = resolveFilmstripThumb(FILMSTRIP_INDEX, 0, noUrlResolver);

    expect(position).not.toBeNull();
    expect(position?.sheetUrl).toBeNull();
    // The cell is still fully addressed — the strip reports the position even when it cannot show it.
    expect(position?.thumbIndex).toBe(0);
    expect(position?.sheetIndex).toBe(0);
  });

  it('treats an empty or throwing resolver as no URL, never as an exception', () => {
    expect(resolveFilmstripThumb(FILMSTRIP_INDEX, 0, () => '')?.sheetUrl).toBeNull();
    expect(
      resolveFilmstripThumb(FILMSTRIP_INDEX, 0, () => {
        throw new Error('resolver exploded');
      })?.sheetUrl,
    ).toBeNull();
  });

  it('never returns NaN in any field', () => {
    for (const time of [0, 0.5, 25, 99.999, 100, 136, 137, 500]) {
      const position = resolveFilmstripThumb(FILMSTRIP_INDEX, time, urlResolver);
      expect(position).not.toBeNull();
      for (const value of Object.values(position ?? {})) {
        expect(typeof value === 'number' ? Number.isFinite(value) : true).toBe(true);
      }
    }
  });
});

describe('filmstripDurationSec', () => {
  it('measures the scrub length from the thumbnail count, not from a duration', () => {
    // 100 + 37 thumbnails at 1 s each. The clip's probe says 12.5 s; the strip must ignore it.
    expect(filmstripDurationSec(FILMSTRIP_INDEX)).toBe(137);
  });

  it('is 0 — not a guess — when there is nothing to scrub with', () => {
    expect(filmstripDurationSec(null)).toBe(0);
    expect(filmstripDurationSec(undefined)).toBe(0);
    expect(filmstripDurationSec({ densities: [] })).toBe(0);
    expect(filmstripDurationSec(FILMSTRIP_INDEX_NO_SHEETS)).toBe(0);
  });

  it('scales with the density the index actually declares', () => {
    const index: FilmstripIndex = {
      densities: [
        {
          densitySec: 0.5,
          thumbWidth: 160,
          thumbHeight: 90,
          cols: 10,
          rows: 10,
          sheetCount: 1,
          sheets: [sheet('a.jpg', 0, 20, 0)],
        },
      ],
    };
    expect(filmstripDurationSec(index)).toBe(10);
  });

  it('is 0 rather than Infinity for an unusable density', () => {
    const index: FilmstripIndex = {
      densities: [
        {
          densitySec: 0,
          thumbWidth: 160,
          thumbHeight: 90,
          cols: 10,
          rows: 10,
          sheetCount: 1,
          sheets: [sheet('a.jpg', 0, 20, 0)],
        },
      ],
    };
    expect(filmstripDurationSec(index)).toBe(0);
  });
});

describe('the sheet URL resolver seam', () => {
  it('receives the SHEET, not a storage leaf, and never throws on a resolver that does', () => {
    const spy = vi.fn<SheetUrlResolver>(() => null);
    resolveFilmstripThumb(FILMSTRIP_INDEX, 0, spy);

    expect(spy).toHaveBeenCalledTimes(1);
    // The argument is the whole sheet: the wire carries no storage leaf to pass instead.
    expect(spy.mock.calls[0][0]).toMatchObject({ firstThumbIndex: 0, thumbCount: 100 });
  });
});

describe('filmstripCellBackground (audit d2 N1)', () => {
  it('positions the cell with container-relative percentages, so every thumbnail paints', () => {
    expect(filmstripCellBackground({ thumbX: 0, thumbY: 0, cols: 10, rows: 10 })).toEqual({ backgroundSize: '1000% 1000%', backgroundPosition: '0% 0%' });
    expect(filmstripCellBackground({ thumbX: 1, thumbY: 0, cols: 10, rows: 10 }).backgroundPosition).toBe('11.1111% 0%');
    expect(filmstripCellBackground({ thumbX: 9, thumbY: 9, cols: 10, rows: 10 }).backgroundPosition).toBe('100% 100%');
    expect(filmstripCellBackground({ thumbX: 5, thumbY: 6, cols: 10, rows: 10 }).backgroundPosition).toBe('55.5556% 66.6667%');
  });

  it('never yields a negative offset (which moved the sheet out of the box)', () => {
    for (let x = 0; x < 10; x++) {
      expect(filmstripCellBackground({ thumbX: x, thumbY: x, cols: 10, rows: 10 }).backgroundPosition).not.toMatch(/-/);
    }
  });

  it('a one-column or one-row sheet stays at 0%', () => {
    expect(filmstripCellBackground({ thumbX: 0, thumbY: 3, cols: 1, rows: 5 }).backgroundPosition).toBe('0% 75%');
  });
});
