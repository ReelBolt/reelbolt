/**
 * Hover-scrub arithmetic: mapping a pointer position on a filmstrip strip to one sprite-sheet cell.
 *
 * Pure and I/O-free. The index this consumes is a `FilmstripIndex`, whose shape is the landed wire
 * contract (`GET …/media/{mediaRef}/filmstrip` →
 * `ReelBolt.Inference.Api/Controllers/Dto/MediaDtos.cs#MediaFilmstripResponse`), which is itself a
 * verbatim pass-through of the sidecar the media writer produced.
 *
 * ## The model, stated once
 *
 * **One `media_derivatives` row is one density, and that row owns the density's PRIMARY sheet only.**
 * Sheets 1..N-1 have no row of their own, so they are addressed through the owning row plus their
 * own 0-based index in the density's `sheets[]` array — which is what each sheet's server-built
 * `contentUrl` already encodes. Nothing here rebuilds that URL and nothing here renumbers the flat
 * thumbnail sequence.
 *
 * ## Nothing is inferred
 *
 * `thumbCount`/`firstThumbIndex` are MEASURED decoded frame counts. **No value is ever derived** —
 * not from a sheet count, not from a duration, and never from a hardcoded 10x10 tile convention.
 * Inferring any of them is exactly what makes hover-scrub land on the wrong frame, and a wrong
 * frame is worse than a stated absence.
 */

import type { FilmstripIndex, FilmstripSheet } from './types';

/** Resolves a sheet's bytes to a URL, or null when the body carries none. */
export type SheetUrlResolver = (sheet: FilmstripSheet) => string | null;

/** One resolved filmstrip cell. Every field is a usable number — never NaN, never null except the URL. */
export interface FilmstripThumbPosition {
  /** Seconds between thumbnails, read from the index. */
  densitySec: number;
  /** Which sheet of the chosen density holds the cell. */
  sheetIndex: number;
  /** Flat index of the thumbnail in the whole filmstrip, after clamping. */
  thumbIndex: number;
  /** Column within the sheet (0-based). */
  thumbX: number;
  /** Row within the sheet (0-based). */
  thumbY: number;
  /** Columns per sheet, from the chosen density — so a renderer needs no second lookup. */
  cols: number;
  /** Rows per sheet, from the chosen density. */
  rows: number;
  /** Pixel size of one cell, from the chosen density. */
  thumbWidth: number;
  /** Pixel size of one cell, from the chosen density. */
  thumbHeight: number;
  /**
   * Source time of this thumbnail: the sheet's measured `firstTimeSec` plus the cell's offset within
   * that sheet. Read from the index rather than multiplied out of the density, so a strip that
   * labels a position labels what the writer actually recorded.
   */
  thumbTimeSec: number;
  /** A usable URL for the sheet image, or null when none can be built. */
  sheetUrl: string | null;
}

const isPositiveFinite = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value) && value > 0;

/**
 * The density a strip scrubs with: the FIRST one carrying any usable sheet.
 *
 * The server orders densities `densitySec` ascending, so "first" is the finest granularity — which
 * is the one a hover-scrub wants. An index whose every density is empty is the same state as no
 * index at all, and both return null rather than a guessed density.
 */
function scrubDensity(index: FilmstripIndex | null | undefined) {
  if (!index || !Array.isArray(index.densities)) return null;
  return index.densities.find((candidate) => usableSheets(candidate?.sheets).length > 0) ?? null;
}

/**
 * The sheets of a density that can carry a thumbnail, in the sidecar's own order.
 *
 * A sheet missing a finite `firstThumbIndex` or carrying `thumbCount <= 0` is dropped — it
 * contributes no cell. A sheet with an unusable **URL** is deliberately KEPT: dropping it would
 * renumber the flat sequence everything else indexes into, so a missing image is rendered as a
 * stated per-cell state instead of shifting every later thumbnail by one.
 */
function usableSheets(sheets: FilmstripSheet[] | null | undefined): FilmstripSheet[] {
  if (!Array.isArray(sheets)) return [];
  return sheets.filter(
    (sheet): sheet is FilmstripSheet =>
      !!sheet &&
      Number.isFinite(sheet.firstThumbIndex) &&
      Number.isFinite(sheet.thumbCount) &&
      sheet.thumbCount > 0,
  );
}

/**
 * Total scrub length in source seconds, or 0 when nothing can be scrubbed.
 *
 * Derived from the MEASURED thumbnail count (`last.firstThumbIndex + last.thumbCount`), never from
 * the probe's `durationSec`: the two disagree, and reconciling them is precisely the guesswork a
 * wrong thumbnail comes from.
 */
export function filmstripDurationSec(index: FilmstripIndex | null | undefined): number {
  const density = scrubDensity(index);
  if (!density || !isPositiveFinite(density.densitySec)) return 0;

  const usable = usableSheets(density.sheets);
  const last = usable[usable.length - 1];
  return (last.firstThumbIndex + last.thumbCount) * density.densitySec;
}

/**
 * Resolves a hover time to a sprite-sheet cell, or null when no cell can be addressed.
 *
 * Returns null for: a missing/empty index, a density with no usable sheet, a non-finite or negative
 * `timeSec`, an unusable `densitySec` or `cols`/`rows` geometry, and a thumbnail index that falls in
 * a gap the measured sheets do not cover. A null result means "we cannot honestly show a frame",
 * which the strip renders as a stated state — the alternative is a wrong frame.
 *
 * `timeSec` beyond the last thumbnail CLAMPS to the last one rather than returning null: hovering
 * past the end of a strip is a normal overshoot, and the honest answer there is the final frame.
 */
export function resolveFilmstripThumb(
  index: FilmstripIndex | null | undefined,
  timeSec: number,
  sheetUrlFor: SheetUrlResolver,
): FilmstripThumbPosition | null {
  if (typeof timeSec !== 'number' || !Number.isFinite(timeSec) || timeSec < 0) return null;

  const density = scrubDensity(index);
  if (!density) return null;

  const { densitySec, cols, rows } = density;
  if (!isPositiveFinite(densitySec)) return null;
  if (!isPositiveFinite(cols) || !isPositiveFinite(rows)) return null;
  const thumbWidth = isPositiveFinite(density.thumbWidth) ? density.thumbWidth : 0;
  const thumbHeight = isPositiveFinite(density.thumbHeight) ? density.thumbHeight : 0;

  const usable = usableSheets(density.sheets);
  const last = usable[usable.length - 1];
  const lastThumbIndex = last.firstThumbIndex + last.thumbCount - 1;
  const requested = Math.floor(timeSec / densitySec);
  const thumbIndex = Math.min(Math.max(requested, 0), lastThumbIndex);

  // The sheets are in ascending firstThumbIndex order, and the sheet covering a flat index is the
  // last one that starts at or before it.
  const sheetIndex = usable.findIndex(
    (sheet, i) =>
      thumbIndex >= sheet.firstThumbIndex &&
      (i === usable.length - 1 || thumbIndex < usable[i + 1].firstThumbIndex),
  );
  if (sheetIndex < 0) return null;

  const sheet = usable[sheetIndex];
  const offset = thumbIndex - sheet.firstThumbIndex;
  if (offset < 0 || offset >= sheet.thumbCount) return null;

  const thumbX = offset % cols;
  const thumbY = Math.floor(offset / cols);
  if (thumbY >= rows) return null;

  let sheetUrl: string | null = null;
  try {
    const resolved = sheetUrlFor(sheet);
    sheetUrl = typeof resolved === 'string' && resolved.length > 0 ? resolved : null;
  } catch {
    sheetUrl = null;
  }

  const firstTimeSec = Number.isFinite(sheet.firstTimeSec) ? sheet.firstTimeSec : 0;

  return {
    densitySec,
    sheetIndex,
    thumbIndex,
    thumbX,
    thumbY,
    cols,
    rows,
    thumbWidth,
    thumbHeight,
    thumbTimeSec: firstTimeSec + offset * densitySec,
    sheetUrl,
  };
}

/**
 * The CSS that shows one thumbnail of a contact sheet in a box: `background-size` scales the sheet
 * so ONE cell fills the box, and `background-position` picks the cell.
 *
 * A percentage `background-position` is NOT an offset of the image: `x%` aligns the point x% across
 * the image with the point x% across the box, i.e. it moves the image by `x% × (box − image)`. With
 * the sheet `cols` boxes wide, column `c` is therefore at `c / (cols − 1) × 100%` — the old
 * `-c × 100%` pushed every cell but the first off the box (audit d2 N1: only thumbnail 0 painted).
 * The sheet is always a full `cols × rows` grid (ffmpeg's `tile` pads a partial last sheet).
 */
export function filmstripCellBackground(
  position: Pick<FilmstripThumbPosition, 'thumbX' | 'thumbY' | 'cols' | 'rows'>,
): { backgroundSize: string; backgroundPosition: string } {
  const axis = (index: number, count: number): string =>
    count > 1 ? `${Number(((index / (count - 1)) * 100).toFixed(4))}%` : '0%';
  return {
    backgroundSize: `${position.cols * 100}% ${position.rows * 100}%`,
    backgroundPosition: `${axis(position.thumbX, position.cols)} ${axis(position.thumbY, position.rows)}`,
  };
}
