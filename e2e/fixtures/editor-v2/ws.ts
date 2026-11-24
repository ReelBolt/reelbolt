import { expect, type Locator, type Page } from '@playwright/test';
import { execFileSync, spawnSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { ARTIFACTS } from './env';
import type { EditorPage } from './editor-page';
import { waitRender, type RenderSummary } from './rows';

/**
 * Helpers for the workspace specs (lane C, e2e/tests/editor-v2/2c-workspaces): sliders that commit
 * exactly once, render started from the real Deliver button, and audio measurements of a rendered file.
 */

/**
 * Sets a Mantine slider by clicking its track at `fraction` (0..1) of its width: one mouse down/up,
 * so a LiveSlider commits exactly once. Returns the value the thumb shows afterwards.
 */
export async function clickSlider(page: Page, root: Locator, fraction: number): Promise<number> {
  await root.scrollIntoViewIfNeeded();
  const box = (await root.boundingBox())!;
  const thumb = root.getByRole('slider').first();
  const before = await thumb.getAttribute('aria-valuenow');
  await page.mouse.click(box.x + box.width * fraction, box.y + box.height / 2);
  // The thumb follows the click on the next render: wait for it to move (it may legitimately stay if the value was already there).
  await expect.poll(() => thumb.getAttribute('aria-valuenow'), { timeout: 3000 }).not.toBe(before).catch(() => undefined);
  return Number(await thumb.getAttribute('aria-valuenow'));
}

/** Clicks Render in Deliver on the open editor and waits for the render the request started. */
export async function renderFromDeliver(ed: EditorPage, timelineId: string, timeoutMs = 240_000, trigger = 'render-start'): Promise<RenderSummary> {
  const posts: Array<{ status: number; body: string }> = [];
  const onResponse = async (res: import('@playwright/test').Response) => {
    if (res.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(res.url()).pathname)) {
      posts.push({ status: res.status(), body: await res.text().catch(() => '') });
    }
  };
  ed.page.on('response', onResponse);
  try {
    await ed.workspace('deliver');
    await ed.tap(ed.testId(trigger));
    await expect.poll(() => posts.length, { message: 'the Render button sent a request' }).toBeGreaterThan(0);
    expect(posts[0].status, `render POST: ${posts[0].body.slice(0, 300)}`).toBe(202);
    const started = JSON.parse(posts[0].body) as { executionId: string };
    return await waitRender(ed.seed.api, ed.seed.projectId, timelineId, started.executionId, timeoutMs);
  } finally {
    ed.page.off('response', onResponse);
  }
}

export type AudioStats = { meanDb: number; maxDb: number };

/** Mean and peak level (dB) of `file` between `fromSec` and `toSec`, optionally of one channel (host ffmpeg volumedetect). */
export function audioStats(file: string, fromSec = 0, toSec = 1e6, channel?: 'left' | 'right'): AudioStats {
  const filters = [channel ? `pan=mono|c0=${channel === 'left' ? 'c0' : 'c1'}` : null, 'volumedetect'].filter(Boolean).join(',');
  const r = spawnSync('ffmpeg', ['-hide_banner', '-ss', String(fromSec), '-to', String(toSec), '-i', file, '-vn', '-af', filters, '-f', 'null', '-'], { encoding: 'utf8' });
  const pick = (key: string) => {
    const m = new RegExp(`${key}:\\s*(-?[\\d.]+|-inf) dB`).exec(r.stderr);
    return !m || m[1] === '-inf' ? -Infinity : Number(m[1]);
  };
  return { meanDb: pick('mean_volume'), maxDb: pick('max_volume') };
}

/** Integrated loudness (LUFS) of `file` measured with ffmpeg's ebur128, or -Infinity when silent. */
export function integratedLufs(file: string): number {
  const r = spawnSync('ffmpeg', ['-hide_banner', '-nostats', '-i', file, '-vn', '-af', 'ebur128=peak=true', '-f', 'null', '-'], { encoding: 'utf8' });
  const summary = r.stderr.slice(r.stderr.lastIndexOf('Summary:'));
  const m = /I:\s*(-?[\d.]+|-inf) LUFS/.exec(summary);
  return !m || m[1] === '-inf' ? -Infinity : Number(m[1]);
}

/** The REST client on the page's own session (no heavy seed): for tests that only need a few rows of their own. */
export function lightApi(page: Page): import('./api').EditorApi {
  // Imported lazily so ws.ts stays importable from helpers that never touch the API.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { EditorApi } = require('./api') as typeof import('./api');
  return new EditorApi(page.request);
}

/** An empty project of this test's own, deleted when the callback ends. */
export async function withTempProject<T>(api: import('./api').EditorApi, name: string, run: (project: { id: string; name: string }) => Promise<T>): Promise<T> {
  const project = await api.createProject(`${name}-${Date.now()}`);
  try {
    return await run(project);
  } finally {
    await api.request.delete(`/api/v1/projects/${project.id}`, { failOnStatusCode: false });
  }
}

/** Luma of one grid cell (column cx, row cy) of a `referenceGrid`/`sampleElement` grid. */
export function cellLuma(grid: number[], cx: number, cy: number, cols = 32): number {
  const i = (cy * cols + cx) * 3;
  return 0.2126 * grid[i] + 0.7152 * grid[i + 1] + 0.0722 * grid[i + 2];
}

/** Mean absolute channel spread (|r-g|+|g-b|)/2 over a grid: 0 for a grey picture. */
export function chromaSpread(grid: number[]): number {
  let s = 0;
  for (let i = 0; i < grid.length; i += 3) s += (Math.abs(grid[i] - grid[i + 1]) + Math.abs(grid[i + 1] - grid[i + 2])) / 2;
  return s / (grid.length / 3);
}

/** Mean R, G, B over a grid. */
export function meanRgb(grid: number[]): [number, number, number] {
  let r = 0; let g = 0; let b = 0;
  const n = grid.length / 3;
  for (let i = 0; i < grid.length; i += 3) { r += grid[i]; g += grid[i + 1]; b += grid[i + 2]; }
  return [r / n, g / n, b / n];
}

/** Sum of absolute neighbour differences of a 320x180 grey frame: a sharpness measure (a blur lowers it). */
export function edgeEnergy(file: string, seconds: number): number {
  const raw = execFileSync('ffmpeg', ['-v', 'error', '-ss', seconds.toFixed(3), '-i', file, '-frames:v', '1', '-vf', 'scale=320:180,format=gray', '-f', 'rawvideo', '-'], { maxBuffer: 1 << 24 });
  let s = 0;
  for (let y = 0; y < 180; y++) for (let x = 1; x < 320; x++) s += Math.abs(raw[y * 320 + x] - raw[y * 320 + x - 1]);
  return s;
}

/** A 2 s green-screen clip (green field, red square in the middle) written under the suite's media dir; returns the path. */
export function makeGreenScreen(): string {
  const dir = path.join(ARTIFACTS, 'media', 'c-fixtures');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `green-${Date.now()}-${Math.floor(Math.random() * 1e6)}.mp4`);
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-f', 'lavfi', '-i', 'color=c=0x00FF00:s=640x360:r=30:d=2,drawbox=x=220:y=110:w=200:h=140:color=0xFF0000:t=fill', '-pix_fmt', 'yuv420p', '-c:v', 'libx264', '-preset', 'veryfast', file]);
  return file;
}

/** A 2-point .cube LUT that inverts every channel. */
export function makeInvertLut(): string {
  const dir = path.join(ARTIFACTS, 'media', 'c-fixtures');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `invert-${Date.now()}.cube`);
  // Row order is red fastest: (r,g,b) in {0,1}^3 -> (1-r, 1-g, 1-b).
  const rows: string[] = [];
  for (let b = 0; b < 2; b++) for (let g = 0; g < 2; g++) for (let r = 0; r < 2; r++) rows.push(`${1 - r}.0 ${1 - g}.0 ${1 - b}.0`);
  fs.writeFileSync(file, `TITLE "invert"\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n${rows.join('\n')}\n`);
  return file;
}

/** Channel spread (|r-g|+|g-b|)/2 of one grid cell: 0 for grey. */
export function cellChroma(grid: number[], cx: number, cy: number, cols = 32): number {
  const i = (cy * cols + cx) * 3;
  return (Math.abs(grid[i] - grid[i + 1]) + Math.abs(grid[i + 1] - grid[i + 2])) / 2;
}
