import { expect, type Locator, type Page } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { EditorPage, Seed } from '../../../fixtures/editor-v2';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';

/**
 * Wave 2 exit journeys (T-027): helpers shared by the four journey specs. The same journey body runs on the
 * desktop (mouse) and on a tablet (1024x768, touch, coarse pointer): `touch` switches every tap to a real finger.
 */

export const TABLET = { width: 1024, height: 768 } as const;

/** Audio probe from top10/07-footage-sound, plus a log of every key press and every save-chip state. */
export const JOURNEY_INSTRUMENT = `(() => {
  const log = (window.__e2eAudio = { started: [] });
  const AC = window.AudioContext;
  if (AC) window.AudioContext = class extends AC {
    constructor(...a) { super(...a); const make = this.createBufferSource.bind(this);
      this.createBufferSource = () => { const n = make(); const s = n.start.bind(n); n.start = (...x) => { log.started.push(n.buffer ? n.buffer.duration : -1); return s(...x); }; return n; }; }
  };
  const play = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...a) { if (!this.muted && this.volume > 0) log.started.push(this.duration); return play.apply(this, a); };
  const keys = (window.__e2eKeys = []);
  window.addEventListener('keydown', (e) => {
    const t = e.target; const tag = t && t.tagName ? t.tagName.toLowerCase() : '';
    const typing = tag === 'input' || tag === 'textarea' || tag === 'select' || (t && t.isContentEditable);
    keys.push({ key: e.key, tag, typing: !!typing });
  }, true);
  const states = (window.__e2eChip = []);
  const watch = () => {
    const chip = document.querySelector('[data-testid="save-state-chip"]');
    if (!chip) return false;
    const note = () => { const s = chip.getAttribute('data-status'); if (s && states[states.length - 1] !== s) states.push(s); };
    note();
    new MutationObserver(note).observe(chip, { attributes: true, attributeFilter: ['data-status'] });
    return true;
  };
  const iv = setInterval(() => { if (watch()) clearInterval(iv); }, 50);
})();`;

export const startedDurations = (page: Page): Promise<number[]> =>
  page.evaluate(() => (window as unknown as { __e2eAudio: { started: number[] } }).__e2eAudio.started.slice());

/** The journey's "menus only" and "no save failures" invariants, read from the page's own logs. */
export async function expectMenusOnlyAndNoSaveErrors(ed: EditorPage): Promise<void> {
  const keys = await ed.page.evaluate(() => (window as unknown as { __e2eKeys: Array<{ key: string; tag: string; typing: boolean }> }).__e2eKeys);
  const stray = keys.filter((k) => !k.typing);
  expect(stray, `nothing is typed except in text fields (menus only): ${JSON.stringify(stray)}`).toEqual([]);
  const chip = await ed.page.evaluate(() => (window as unknown as { __e2eChip: string[] }).__e2eChip);
  const bad = chip.filter((s) => ['error', 'rejected', 'invalid', 'conflict', 'retrying'].includes(s));
  expect(bad, `the save chip never showed a failure state (saw ${chip.join(' > ')})`).toEqual([]);
  const failed = ed.patches.filter((p) => p.status >= 400);
  expect(failed.map((p) => `${p.status} ${p.ops.join(',')} ${p.body.slice(0, 120)}`), 'no PATCH answered 4xx/5xx').toEqual([]);
  expect(ed.patches.length, 'the journey saved through PATCH').toBeGreaterThan(5);
}

/** One tap: a real finger on the tablet, the mouse otherwise. */
export async function tapOn(ed: EditorPage, locator: Locator, touch: boolean): Promise<void> {
  if (!touch) return ed.tap(locator);
  await locator.scrollIntoViewIfNeeded();
  await locator.tap();
}

export async function tapCanvas(ed: EditorPage, frame: number, trackIndex: number, touch: boolean): Promise<void> {
  const x = await ed.frameX(frame);
  const y = await ed.laneY(trackIndex);
  if (touch) await ed.page.touchscreen.tap(x, y);
  else await ed.page.mouse.click(x, y);
}

/** Types a timecode into the playhead field (a text field), the one way to place the playhead without keys on the canvas. */
export async function seekTo(ed: EditorPage, frame: number, touch: boolean): Promise<void> {
  if (!touch) return ed.seek(frame);
  const fps = 30;
  const tc = [Math.floor(frame / fps / 3600), Math.floor(frame / fps / 60) % 60, Math.floor(frame / fps) % 60, frame % fps].map((n) => String(n).padStart(2, '0')).join(':');
  await ed.closeDrawer();
  const field = ed.testId('timecode-field');
  // Media and Deliver have no transport bar: come back to Edit first (as EditorPage.seek does).
  if (!(await field.isVisible().catch(() => false))) await ed.workspace('edit');
  // Wave 3 T-017: a placed source leaves the viewer on Source, whose transport is not the playhead's.
  if ((await ed.testId('viewer-mode-switch').getAttribute('data-mode', { timeout: 500 }).catch(() => null)) === 'source') await ed.tap(ed.testId('viewer-mode-record'));
  await field.tap();
  await field.fill(tc);
  await field.press('Enter');
  await expect(ed.testId('status-timecode')).toHaveText(tc);
}

/** A byte-unique copy of a seed medium under a readable, run-unique name (derivatives are shared by content). */
export function uniqueNamedCopy(source: string, name: string): string {
  const dir = path.join(ARTIFACTS, 'media', 'journeys');
  fs.mkdirSync(dir, { recursive: true });
  const out = path.join(dir, name);
  const tag = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', source, '-c', 'copy', '-map_metadata', '-1', '-metadata', `comment=${tag}`, out]);
  return out;
}

export const mediaDir = (): string => ensureMedia();

export async function fileIdByName(seed: Seed, name: string): Promise<string> {
  let id = '';
  await expect.poll(async () => {
    id = (await seed.api.listFiles(seed.projectId)).find((f) => f.originalFileName === name)?.id ?? '';
    return id;
  }, { message: `${name} was uploaded`, timeout: 60_000 }).not.toBe('');
  return id;
}

export async function seekSource(ed: EditorPage, seconds: number): Promise<void> {
  const video = ed.testId('source-viewer-video');
  await video.evaluate((v: HTMLVideoElement, t: number) => new Promise<void>((resolve) => { v.addEventListener('seeked', () => resolve(), { once: true }); v.currentTime = t; }), seconds);
}

export const allItems = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items);
export const videoItems = (tl: Timeline): Item[] => tl.document.tracks.filter((t) => t.kind === 'video').flatMap((t) => t.items);
export const audioItems = (tl: Timeline): Item[] => tl.document.tracks.filter((t) => t.kind === 'audio').flatMap((t) => t.items);
