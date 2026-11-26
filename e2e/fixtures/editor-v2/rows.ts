import { expect, test, type Locator, type Page } from '@playwright/test';
import { execFileSync, spawnSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { EditorApi } from './api';
import { ARTIFACTS } from './env';
import type { EditorPage } from './editor-page';

/**
 * Helpers shared by the per-row specs (T-002: one spec per audit ❌ row, remediation-plan.md §2.9).
 */

export type ProjectName = 'desktop' | 'pixel7' | 'iphone14webkit' | 'tablet';

/** Runs the row only on the listed projects (desktop rows on desktop, (M) rows on the phones). */
export function runOn(...projects: ProjectName[]): void {
  const name = test.info().project.name as ProjectName;
  test.skip(!projects.includes(name), `row is checked on ${projects.join(', ')}`);
}

/** WebKit cannot drag by touch: rows that need a real touch drag skip there. */
export function skipSyntheticPointers(): void {
  test.skip(test.info().project.name === 'iphone14webkit', 'synthetic pointers: Playwright WebKit cannot drag by touch (audit D8)');
}

/**
 * The designed "needs a larger screen" state (plan W0.7) that replaces a control a verdict cut or
 * deferred on phone: a visible element with `data-testid="needs-larger-screen"` or a plain sentence
 * saying the action needs a larger screen. It must name the cut action (`topic`, e.g. /compound/i), so one
 * generic banner cannot satisfy every row. `scope` narrows where it must appear.
 */
export async function expectLargerScreenState(page: Page, topic: RegExp, what: string, scope?: Locator): Promise<void> {
  const root = scope ?? page.locator('body');
  const state = root.getByTestId('needs-larger-screen').filter({ hasText: topic })
    .or(root.getByText(/larger screen/i).filter({ hasText: topic }));
  await expect(state.first(), `${what}: the phone says it needs a larger screen instead of a broken control`).toBeVisible({ timeout: 10_000 });
}

/**
 * Finds an action control by its accessible name (button or menu item), the way a touch user would:
 * on the timeline first, then in the phone Inspector. Returns the first visible match or fails.
 */
export async function findAction(ed: EditorPage, name: RegExp): Promise<Locator> {
  const candidates = () => ed.page.getByRole('menuitem', { name }).or(ed.page.getByRole('button', { name }));
  const visible = async (): Promise<Locator | null> => {
    const all = await candidates().all();
    for (const c of all) if (await c.isVisible().catch(() => false)) return c;
    return null;
  };
  let found = await visible();
  if (!found && ed.isPhone) {
    await ed.openInspector();
    found = await visible();
  }
  expect(found, `a visible control named ${name} (reachable by tap)`).toBeTruthy();
  return found!;
}

/* ------------------------------------------------------------------ analysis (W0.5) */

/** The derush timeline's VideoAnalyze step result (the run that seeded it), found through its execution. */
export async function analysisStepResultId(api: EditorApi, projectId: string, timelineId: string): Promise<string> {
  const tl = await api.getTimeline(projectId, timelineId);
  const seed = (tl.document as { seed?: { executionId?: string } }).seed;
  expect(seed?.executionId, 'the derush timeline names its seed execution').toBeTruthy();
  const exec = await api.json<{ stepResults: Array<{ id: string; output?: string | null }> }>('GET', `/api/v1/projects/${projectId}/executions/${seed!.executionId}`);
  const analysis = exec.stepResults.find((s) => (s.output ?? '').includes('"videoAnalyze"'));
  expect(analysis, 'the seed run has a VideoAnalyze step result').toBeTruthy();
  return analysis!.id;
}

/**
 * Adds what the free analysis path cannot produce (no transcription or duplicate detection runs on
 * the local stack: both are paid or off) to the VideoAnalyze artifact ONLY. A panel that reads the
 * run's analysis sees it; a panel that reads the VideoCompile EDL instead (the audit's ui1 D3) does
 * not: the stub never hides the bug, it only supplies the paid half of the data.
 */
export async function extendAnalysis(page: Page, extra: Record<string, unknown>): Promise<void> {
  await page.route(/\/api\/v1\/projects\/[^/]+\/.*(artifact|analysis)/i, async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const res = await route.fetch();
    const text = await res.text();
    let body: unknown;
    try { body = JSON.parse(text); } catch { return route.fulfill({ response: res, body: text }); }
    const isAnalysis = !!body && typeof body === 'object' && Array.isArray((body as { shots?: unknown }).shots) && (body as { media?: unknown }).media != null;
    if (!isAnalysis) return route.fulfill({ response: res, body: text });
    return route.fulfill({ response: res, json: { ...(body as object), ...extra } });
  });
}

/** clipA (12 s) transcript: three paragraphs, a filler "um", a repeat "the the", a 1.6 s hole inside paragraph 0. */
export const TRANSCRIPT = {
  segments: [
    { id: 't0', startSec: 0.2, endSec: 2.9, text: 'so um the the quick brown fox' },
    { id: 't1', startSec: 4.6, endSec: 7.9, text: 'jumps over the lazy dog' },
    { id: 't2', startSec: 9.6, endSec: 11.8, text: 'and that is all' },
  ],
  words: [
    { id: 'w0', startSec: 0.2, endSec: 0.5, text: 'so' },
    { id: 'w1', startSec: 0.5, endSec: 0.9, text: 'um' },
    { id: 'w2', startSec: 0.9, endSec: 1.2, text: 'the' },
    { id: 'w3', startSec: 1.25, endSec: 1.5, text: 'the' },
    { id: 'w4', startSec: 1.5, endSec: 1.9, text: 'quick' },
    { id: 'w5', startSec: 1.9, endSec: 2.3, text: 'brown' },
    { id: 'w6', startSec: 2.3, endSec: 2.9, text: 'fox' },
    { id: 'w7', startSec: 4.6, endSec: 5.0, text: 'jumps' },
    { id: 'w8', startSec: 5.0, endSec: 5.4, text: 'over' },
    { id: 'w9', startSec: 7.0, endSec: 7.3, text: 'the' },
    { id: 'w10', startSec: 7.3, endSec: 7.6, text: 'lazy' },
    { id: 'w11', startSec: 7.6, endSec: 7.9, text: 'dog' },
    { id: 'w12', startSec: 9.6, endSec: 10.0, text: 'and' },
    { id: 'w13', startSec: 10.0, endSec: 10.4, text: 'that' },
    { id: 'w14', startSec: 10.4, endSec: 10.8, text: 'is' },
    { id: 'w15', startSec: 10.8, endSec: 11.8, text: 'all' },
  ],
};

/* ------------------------------------------------------------------ renders (W0.1) */

export type RenderSummary = {
  executionId: string;
  status: string;
  revision: number | null;
  preset: string;
  errorMessage: string | null;
  stepResultId: string | null;
  hasOutput: boolean;
  regions: { total: number; cached: number; list: Array<{ startFrame: number; endFrame: number; cached: boolean }> } | null;
  loudness?: { integratedLufs: number; truePeakDbtp: number; loudnessRange: number; normalised: boolean } | null;
};

/** `POST …/render` through the API (string preset, as the web sends it); returns the 202 body. */
export async function startRender(api: EditorApi, projectId: string, timelineId: string, body: Record<string, unknown> = {}): Promise<{ executionId: string }> {
  return api.json('POST', `${api.timelinesUrl(projectId, timelineId)}/render`, { preset: 'Hd1080p16x9', ...body }, [200, 201, 202]);
}

/** Polls the render queue until the execution finishes (Passed, Failed or Cancelled). */
export async function waitRender(api: EditorApi, projectId: string, timelineId: string, executionId: string, timeoutMs = 240_000): Promise<RenderSummary> {
  let row: RenderSummary | undefined;
  let list: RenderSummary[] = [];
  try {
    await expect.poll(async () => {
      list = await api.json<RenderSummary[]>('GET', `${api.timelinesUrl(projectId, timelineId)}/renders`);
      row = list.find((r) => r.executionId === executionId);
      return row && ['Passed', 'Failed', 'Cancelled'].includes(row.status) ? 'finished' : row?.status ?? 'missing';
    }, { message: `render ${executionId} finishes`, timeout: timeoutMs, intervals: [2000] }).toBe('finished');
  } catch (e) {
    // A render that never leaves Running is an engine-side wait (admission, the ffmpeg gate), invisible from the status
    // alone: say what this timeline's renders and the execution looked like when the wait gave up (CI run 45796910736).
    const detail = await api.json<{ status?: string; startedAt?: string; createdAt?: string; stepResults?: Array<{ status: string; workflowStepId: string }> }>('GET', `/api/v1/projects/${projectId}/executions/${executionId}`).catch((err: unknown) => ({ error: String(err) }));
    const note = `renders of this timeline: ${JSON.stringify(list.map((r) => ({ id: r.executionId, status: r.status, preset: r.preset })))}; execution ${executionId}: ${JSON.stringify(detail).slice(0, 800)}; waited ${timeoutMs} ms, gave up at ${new Date().toISOString()}`;
    throw new Error(`${(e as Error).message}\n${note}`);
  }
  return row!;
}

export async function renderAndWait(api: EditorApi, projectId: string, timelineId: string, body: Record<string, unknown> = {}): Promise<RenderSummary> {
  const started = await startRender(api, projectId, timelineId, body);
  return waitRender(api, projectId, timelineId, started.executionId);
}

/** Downloads a finished render's output to /tmp/editor-remediation/renders and returns the path. */
export async function downloadRender(api: EditorApi, projectId: string, render: RenderSummary, ext = 'mp4'): Promise<string> {
  expect(render.stepResultId, 'the render has an output step result').toBeTruthy();
  const res = await api.request.get(`/api/v1/projects/${projectId}/outputs/${render.stepResultId}/download`, { failOnStatusCode: false });
  expect(res.status(), 'the output downloads').toBeLessThan(300);
  const dir = path.join(ARTIFACTS, 'renders');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${render.executionId}.${ext}`);
  fs.writeFileSync(file, await res.body());
  return file;
}

export type Probe = {
  durationSec: number;
  video?: { width: number; height: number; codec: string };
  audio?: { channels: number; codec: string };
};

/** Host ffprobe facts of a media file. */
export function probe(file: string): Probe {
  const out = JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', file]).toString()) as {
    format: { duration?: string };
    streams: Array<{ codec_type: string; codec_name: string; width?: number; height?: number; channels?: number }>;
  };
  const v = out.streams.find((s) => s.codec_type === 'video');
  const a = out.streams.find((s) => s.codec_type === 'audio');
  return {
    durationSec: Number(out.format.duration ?? 0),
    video: v ? { width: v.width!, height: v.height!, codec: v.codec_name } : undefined,
    audio: a ? { channels: a.channels!, codec: a.codec_name } : undefined,
  };
}

/** Mean volume (dB) of the audio between `fromSec` and `toSec` (host ffmpeg volumedetect). */
export function meanVolumeDb(file: string, fromSec: number, toSec: number): number {
  const r = spawnSync('ffmpeg', ['-hide_banner', '-ss', String(fromSec), '-to', String(toSec), '-i', file, '-vn', '-af', 'volumedetect', '-f', 'null', '-'], { encoding: 'utf8' });
  return parseVolume(r.stderr);
}

function parseVolume(log: string): number {
  const m = /mean_volume:\s*(-?[\d.]+|-inf) dB/.exec(log);
  return !m || m[1] === '-inf' ? -Infinity : Number(m[1]);
}

/** Mean volume per stereo channel ([left, right], dB) of the whole file. */
export function channelVolumesDb(file: string): [number, number] {
  const one = (ch: 'c0' | 'c1') => {
    const r = spawnSync('ffmpeg', ['-hide_banner', '-i', file, '-vn', '-af', `pan=mono|c0=${ch},volumedetect`, '-f', 'null', '-'], { encoding: 'utf8' });
    return parseVolume(r.stderr);
  };
  return [one('c0'), one('c1')];
}

/** Mean RGB of one frame at `seconds` (host ffmpeg, 1x1 area average). */
export function frameRgb(file: string, seconds: number): [number, number, number] {
  const raw = execFileSync('ffmpeg', ['-v', 'error', '-ss', seconds.toFixed(3), '-i', file, '-frames:v', '1', '-vf', 'scale=1:1:flags=area', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-']);
  return [raw[0], raw[1], raw[2]];
}

/**
 * A byte-unique copy of a seed medium (stream copy, a random metadata tag). Derivatives are shared by
 * content, so an upload of the very same bytes as a seed file arrives with that file's proxy already made.
 */
export function uniqueCopy(file: string): string {
  const dir = path.join(ARTIFACTS, 'media', 'unique');
  fs.mkdirSync(dir, { recursive: true });
  const tag = crypto.randomUUID();
  const out = path.join(dir, `${path.basename(file, path.extname(file))}-${tag}${path.extname(file)}`);
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', file, '-c', 'copy', '-map_metadata', '-1', '-metadata', `comment=${tag}`, out]);
  return out;
}

/**
 * The picture as the eye sees it. Its own box stays on screen when an ancestor clips it, and comparing it to a pane that also
 * holds other controls hides the cut, so this intersects the frame with every clipping ancestor and wants nothing cut off,
 * and asks what a finger at the frame's centre reaches.
 */
export async function expectPictureWhole(page: Page, minHeight: number, what: string): Promise<void> {
  const frame = page.getByTestId('program-frame');
  await expect(frame, `${what}: the picture`).toBeVisible();
  const r = await frame.evaluate((el) => {
    const box = el.getBoundingClientRect();
    let [top, bottom] = [box.top, box.bottom];
    for (let a = el.parentElement; a; a = a.parentElement) {
      if (getComputedStyle(a).overflowY === 'visible') continue;
      const c = a.getBoundingClientRect();
      top = Math.max(top, c.top);
      bottom = Math.min(bottom, c.bottom);
    }
    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    return { height: box.height, shown: bottom - top, reachable: !!hit && el.contains(hit) && !hit.closest('button, a, [role=button]') };
  });
  expect(r.height, `${what}: the picture has room`).toBeGreaterThanOrEqual(minHeight);
  expect(r.shown, `${what}: no part of the picture is cut`).toBeGreaterThanOrEqual(r.height - 0.5);
  expect(r.reachable, `${what}: a finger at the picture's centre reaches the picture`).toBe(true);
}

/**
 * Skips a row on the phones whose subject is the desktop panel layout (docs/editor.md "Phone and tablet"): the phone surface
 * has its own sheets for these jobs, covered by the 30-phone suite, and the control the row drives is "not on a phone".
 */
export function desktopLayoutOnly(why: string): void {
  const name = test.info().project.name;
  test.skip(name === 'pixel7' || name === 'iphone14webkit', `desktop layout: ${why}`);
}
