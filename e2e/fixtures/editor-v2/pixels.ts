import { execFileSync } from 'child_process';
import type { Locator, Page } from '@playwright/test';

/** Downscaled RGB grid: GRID_W x GRID_H cells, 3 bytes each. */
export const GRID_W = 32;
export const GRID_H = 18;
export type Grid = number[];

/**
 * Samples what the user SEES in an element: an element screenshot (the composited WebGL output, so it
 * works without `preserveDrawingBuffer`), decoded and downscaled in the page with a 2D canvas.
 */
export async function sampleElement(page: Page, el: Locator): Promise<Grid> {
  const png = await el.screenshot({ animations: 'disabled' });
  return page.evaluate(async ({ b64, w, h }) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    const ctx = c.getContext('2d')!;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, 0, 0, w, h);
    const d = ctx.getImageData(0, 0, w, h).data;
    const out: number[] = [];
    for (let i = 0; i < d.length; i += 4) out.push(d[i], d[i + 1], d[i + 2]);
    return out;
  }, { b64: png.toString('base64'), w: GRID_W, h: GRID_H });
}

export function meanLuma(grid: Grid): number {
  let s = 0;
  for (let i = 0; i < grid.length; i += 3) s += 0.2126 * grid[i] + 0.7152 * grid[i + 1] + 0.0722 * grid[i + 2];
  return s / (grid.length / 3);
}

/** Mean luma of the cells whose column is in [c0, c1). */
export function columnLuma(grid: Grid, c0: number, c1: number): number {
  let s = 0;
  let n = 0;
  for (let y = 0; y < GRID_H; y++) {
    for (let x = c0; x < c1; x++) {
      const i = (y * GRID_W + x) * 3;
      s += 0.2126 * grid[i] + 0.7152 * grid[i + 1] + 0.0722 * grid[i + 2];
      n++;
    }
  }
  return s / n;
}

export function distance(a: Grid, b: Grid): number {
  let s = 0;
  for (let i = 0; i < a.length; i++) s += Math.abs(a[i] - b[i]);
  return s / a.length;
}

/**
 * The reference frame of a source at `seconds`, fitted (letterboxed) into a 16:9 frame the way the
 * program monitor shows it, at grid size. Uses the host ffmpeg.
 */
export function referenceGrid(file: string, seconds: number): Grid {
  const raw = execFileSync('ffmpeg', [
    '-v', 'error', '-ss', seconds.toFixed(3), '-i', file, '-frames:v', '1',
    '-vf', `scale=${GRID_W * 8}:${GRID_H * 8}:force_original_aspect_ratio=decrease,pad=${GRID_W * 8}:${GRID_H * 8}:(ow-iw)/2:(oh-ih)/2,scale=${GRID_W}:${GRID_H}:flags=area`,
    '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-',
  ]);
  return Array.from(raw);
}
