import * as fs from 'fs';
import * as path from 'path';
import { test, expect, type EditorPage, type Seed } from '../../../fixtures/editor-v2';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { MEDIA_DIR } from '../../../fixtures/editor-v2/env';
import { columnLuma, distance, referenceGrid, sampleElement, type Grid } from '../../../fixtures/editor-v2/pixels';
import { meanVolumeDb, probe, runOn } from '../../../fixtures/editor-v2/rows';
import {
  JOURNEY_INSTRUMENT, TABLET, allItems, audioItems, expectMenusOnlyAndNoSaveErrors, fileIdByName, mediaDir, seekSource, seekTo, startedDurations, tapCanvas, tapOn,
  uniqueNamedCopy, videoItems,
} from './journey-helpers';

/**
 * T-027 Journey 1 (Wave 2 exit): three clips uploaded from the pool -> inserted with In/Out marks -> ripple delete a
 * shot -> close the gap -> a title -> music ducked under the camera sound -> Look Warm on every clip -> a 9:16
 * render -> the download. MENUS ONLY: no key is pressed anywhere but inside a text field. Every outcome is read from
 * the stored document, the downloaded bytes (ffprobe, volumedetect) or the viewer's pixels.
 */

const FPS = 30;
const DL_DIR = '/tmp/editor-remediation/journeys-downloads';

type Ctx = { ed: EditorPage; seed: Seed; touch: boolean };
const trackOf = (tl: Timeline, item: Item) => tl.document.tracks.find((t) => t.items.some((i) => i.id === item.id))!;
const fileOf = (i: Item) => i.assetRef?.projectFileId ?? null;
const sound = (tl: Timeline, fileId: string) => audioItems(tl).filter((i) => fileOf(i) === fileId);
const picture = (tl: Timeline, fileId: string) => videoItems(tl).filter((i) => fileOf(i) === fileId);
const videoTrackIndex = (tl: Timeline) => tl.document.tracks.findIndex((t) => t.kind === 'video' && t.items.some((i) => i.type === 'mediaClip'));

async function pickTile(c: Ctx, fileId: string): Promise<void> {
  await tapOn(c.ed, c.ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="file:${fileId}"]`).first(), c.touch);
  await expect(c.ed.testId('source-viewer')).toBeVisible();
  await c.ed.testId('media-insert-length').waitFor();
}

/** Marks the source from `fromSec` to `toSec` with the In/Out buttons (clearing marks a previous pick left behind). */
async function mark(c: Ctx, fromSec: number, toSec: number): Promise<void> {
  const controls = c.ed.testId('mark-controls');
  if (await controls.getByTestId('mark-clear').isEnabled()) await tapOn(c.ed, controls.getByTestId('mark-clear'), c.touch);
  await expect(c.ed.testId('source-viewer-video')).toBeVisible();
  await seekSource(c.ed, fromSec);
  await tapOn(c.ed, controls.getByTestId('mark-in'), c.touch);
  await seekSource(c.ed, toSec);
  await tapOn(c.ed, controls.getByTestId('mark-out'), c.touch);
  await expect(c.ed.testId('mark-span-value')).toContainText(String(Math.round((toSec - fromSec) * FPS)));
  await expect(c.ed.testId('media-insert-length')).toContainText(/marked range/i);
}

async function rename(c: Ctx, trackId: string, name: string): Promise<void> {
  await tapOn(c.ed, c.ed.testId(`track-menu-${trackId}`), c.touch);
  await tapOn(c.ed, c.ed.testId(`track-menu-rename-${trackId}`), c.touch);
  const input = c.ed.testId(`track-rename-input-${trackId}`);
  await input.fill(name);
  await input.press('Enter');
}

async function ws(c: Ctx, name: 'media' | 'edit' | 'color' | 'audio' | 'deliver'): Promise<void> {
  await c.ed.closeDrawer(); // tablet: the Inspector drawer overlays the right edge
  await tapOn(c.ed, c.ed.page.getByTestId(`workspace-${name}`), c.touch);
}

async function clipMenu(c: Ctx, action: string): Promise<void> {
  await tapOn(c.ed, c.ed.testId('clip-actions-open'), c.touch);
  await tapOn(c.ed, c.ed.testId(`clip-action-${action}`), c.touch);
}

async function journey1(c: Ctx): Promise<void> {
  const { ed, seed, touch } = c;
  const { page } = ed;
  const tag = Date.now().toString(36);
  const names = { a: `j1-${tag}-a.mp4`, b: `j1-${tag}-b.mp4`, c: `j1-${tag}-c.mp4`, m: `j1-${tag}-music.wav` };
  const origin = { a: 'clipA.mp4', b: 'clipB.mp4', c: 'vertical.mp4', m: 'tone.wav' } as const;

  // 1. Upload three clips (with sound) and a music file from the pool.
  await ws(c, 'media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  const paths = (Object.keys(names) as Array<keyof typeof names>).map((k) => uniqueNamedCopy(path.join(mediaDir(), origin[k]), names[k]));
  await page.locator('input[type="file"][accept="video/*,audio/*,image/*"]').first().setInputFiles(paths);
  const ids = {} as Record<keyof typeof names, string>;
  for (const k of Object.keys(names) as Array<keyof typeof names>) ids[k] = await fileIdByName(seed, names[k]);
  for (const id of Object.values(ids)) await expect(page.locator(`[data-testid="media-clip-tile"][data-media-ref="file:${id}"]`).first()).toBeVisible({ timeout: 60_000 });
  await expect.poll(async () => {
    const media = await seed.api.json<Array<{ mediaRef: string; probe: unknown }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
    return Object.values(ids).every((id) => media.find((m) => m.mediaRef === `file:${id}`)?.probe != null);
  }, { message: 'every upload carries its ingest probe (the pool knows the sound)', timeout: 180_000, intervals: [2000] }).toBe(true);
  for (const id of [ids.a, ids.b, ids.c]) {
    await expect.poll(async () => {
      const d = await seed.api.json<Array<{ kind: string }>>('GET', `/api/v1/projects/${seed.projectId}/media/file:${id}/derivatives`);
      return d.some((x) => x.kind === 'proxy');
    }, { message: 'a preview proxy exists', timeout: 180_000, intervals: [2000] }).toBe(true);
  }

  // 2. Insert with marks: A 2-6 s at the start, B 1-4 s and C 1-3 s each at the end (the playhead stops on the last frame of the
  // film, one before the cut the insert snaps to).
  await seekTo(ed, 0, touch);
  await pickTile(c, ids.a);
  await mark(c, 2, 6);
  await tapOn(ed, ed.testId('media-insert-at-playhead'), touch);
  let tl = await ed.expectServer((t) => picture(t, ids.a).length === 1 && sound(t, ids.a).length === 1, 'A is inserted with its sound');
  const a = picture(tl, ids.a)[0];
  expect([a.recordIn, a.recordOut, a.sourceIn], 'A is the marked 4 s, from the In mark').toEqual([0, 120, 60]);
  expect([sound(tl, ids.a)[0].recordIn, sound(tl, ids.a)[0].recordOut], 'its sound is the same span').toEqual([0, 120]);

  await seekTo(ed, 119, touch);
  await pickTile(c, ids.b);
  await mark(c, 1, 4);
  await tapOn(ed, ed.testId('media-insert-at-playhead'), touch);
  tl = await ed.expectServer((t) => picture(t, ids.b).length === 1, 'B is inserted');
  expect([picture(tl, ids.b)[0].recordIn, picture(tl, ids.b)[0].recordOut, picture(tl, ids.b)[0].sourceIn], 'B is 3 s after A').toEqual([120, 210, 30]);

  await seekTo(ed, 209, touch);
  await pickTile(c, ids.c);
  await mark(c, 1, 3);
  await tapOn(ed, ed.testId('media-insert-at-playhead'), touch);
  tl = await ed.expectServer((t) => picture(t, ids.c).length === 1 && sound(t, ids.c).length === 1, 'C is inserted with its sound');
  expect([picture(tl, ids.c)[0].recordIn, picture(tl, ids.c)[0].recordOut, picture(tl, ids.c)[0].sourceIn], 'C is 2 s after B').toEqual([210, 270, 30]);

  // 3. Ripple delete the shot in the middle (Clip menu): everything after it moves up, sound included.
  await ws(c, 'edit');
  await ed.showTimeline();
  const vi = videoTrackIndex(tl);
  await tapCanvas(ed, 165, vi, touch);
  await clipMenu(c, 'delete');
  tl = await ed.expectServer((t) => picture(t, ids.b).length === 0, 'B is gone');
  expect(sound(tl, ids.b), 'its sound went with it').toHaveLength(0);
  expect([picture(tl, ids.c)[0].recordIn, sound(tl, ids.c)[0].recordIn], 'C and its sound moved up by the 3 s that were removed').toEqual([120, 120]);
  expect(picture(tl, ids.a)[0].recordIn, 'A did not move').toBe(0);

  // 4. Make a one-second gap before C with the Clip +10 button (three taps), then close it: click the gap, Close selected gap.
  await tapCanvas(ed, 150, vi, touch);
  for (let n = 1; n <= 3; n++) {
    if (touch) await tapOn(ed, ed.testId('step-clip-+10'), touch);
    else await ed.stepper('step-clip-+10');
    await ed.expectServer((t) => picture(t, ids.c)[0].recordIn === 120 + 10 * n, `C moved ${10 * n} frames later`);
  }
  tl = await ed.serverTimeline();
  expect([picture(tl, ids.c)[0].recordIn, sound(tl, ids.c)[0].recordIn], 'C\'s sound moved with its picture').toEqual([150, 150]);
  await tapCanvas(ed, 135, vi, touch);
  const gap = ed.testId('selected-gap');
  await expect(gap).toBeVisible();
  await expect(gap).toHaveAttribute('data-gap-start', '120');
  await expect(gap).toHaveAttribute('data-gap-end', '150');
  await tapOn(ed, ed.testId('close-selected-gap'), touch);
  tl = await ed.expectServer((t) => picture(t, ids.c)[0].recordIn === 120, 'the gap is closed');
  expect(sound(tl, ids.c)[0].recordIn, 'C\'s sound closed up with its picture (still in sync)').toBe(120);
  expect([picture(tl, ids.c)[0].recordOut, sound(tl, ids.c)[0].recordOut]).toEqual([180, 180]);

  // The preview is correct and audible after the cuts: pictures from the right sources, sound from the right sources.
  await playsAndShows(c, ids, 20, 130);

  // 5. A title at the start.
  await seekTo(ed, 0, touch);
  await tapOn(ed, ed.testId('add-menu'), touch);
  await tapOn(ed, ed.testId('add-title-options'), touch);
  await expect(ed.testId('add-title-dialog')).toBeVisible();
  await tapOn(ed, ed.page.getByTestId('title-template-lowerThird'), touch);
  await ed.testId('title-text').fill('Wave 2 exit');
  await ed.testId('title-seconds').fill('2');
  await tapOn(ed, ed.testId('title-add'), touch);
  tl = await ed.expectServer((t) => allItems(t).some((i) => i.type === 'titleClip' && (i as { text?: string }).text === 'Wave 2 exit'), 'the title is stored');
  const title = allItems(tl).find((i) => i.type === 'titleClip')!;
  expect([title.recordIn, title.recordOut - title.recordIn], 'two seconds at the playhead').toEqual([0, 60]);

  // 6. Music from the pool onto its own audio track, named Music, ducked under the camera sound.
  await ws(c, 'media');
  await seekTo(ed, 0, touch);
  await pickTile(c, ids.m);
  await tapOn(ed, ed.testId('media-place-on-top'), touch);
  tl = await ed.expectServer((t) => sound(t, ids.m).length === 1, 'the music is on an audio track');
  const musicTrack = trackOf(tl, sound(tl, ids.m)[0]);
  const cameraTrack = trackOf(tl, sound(tl, ids.a)[0]);
  expect(musicTrack.id, 'the music has a track of its own').not.toBe(cameraTrack.id);
  await ws(c, 'edit');
  await rename(c, musicTrack.id, 'Music');
  await ed.expectServer((t) => t.document.tracks.find((x) => x.id === musicTrack.id)?.name === 'Music', 'the track is named Music');
  await ws(c, 'audio');
  await expect(ed.testId('ducking-state')).toHaveAttribute('data-on', 'false');
  await tapOn(ed, ed.testId('ducking-derive'), touch);
  await expect(ed.testId('ducking-state')).toHaveAttribute('data-on', 'true');
  tl = await ed.expectServer((t) => ((t.document.tracks.find((x) => x.id === musicTrack.id)?.gainDb as { keys?: unknown[] } | undefined)?.keys?.length ?? 0) > 0, 'ducking keyframes are stored on the music track');
  const keys = (tl.document.tracks.find((x) => x.id === musicTrack.id)!.gainDb as { keys: Array<{ frame: number; value: number }> }).keys;
  expect(Math.min(...keys.map((k) => k.value)), 'the music is lowered under the camera sound').toBeLessThanOrEqual(-11);

  // 7. Look Warm on every picture: choose it on A, then Apply to all clips.
  await ws(c, 'edit');
  await tapCanvas(ed, 60, videoTrackIndex(tl), touch);
  await ws(c, 'color');
  // Colour has no hidden mode (wave 2 T-006): the looks live under the Gallery tab of the grade tools.
  await tapOn(ed, ed.testId('color-tab-gallery'), touch);
  await expect(ed.testId('look-panel')).toBeVisible();
  await tapOn(ed, ed.testId('look-warm'), touch);
  await ed.expectServer((t) => picture(t, ids.a).some((i) => ((i.grade as { nodes?: Array<{ name: string }> } | undefined)?.nodes ?? []).some((n) => n.name === 'Look: Warm 100%')), 'Warm is on A');
  await tapOn(ed, ed.testId('look-apply-all'), touch);
  tl = await ed.expectServer((t) => [ids.a, ids.c].every((id) => picture(t, id).every((i) => ((i.grade as { nodes?: Array<{ name: string }> } | undefined)?.nodes ?? []).some((n) => n.name === 'Look: Warm 100%'))), 'every picture stores the Warm look');
  for (const id of [ids.a, ids.c]) expect(picture(tl, id).length).toBeGreaterThan(0);

  // 8. Render 9:16 from Deliver and download it.
  await ws(c, 'deliver');
  await expect(ed.testId('deliver-workspace')).toBeVisible();
  const name = `j1-${tag}`;
  await ed.testId('render-file-name').fill(name);
  await tapOn(ed, ed.testId('render-preset'), touch);
  await tapOn(ed, page.getByRole('option', { name: 'Vertical for phones' }), touch);
  const started = page.waitForResponse((r) => r.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(r.url()).pathname));
  await tapOn(ed, ed.testId('render-start'), touch);
  const res = await started;
  expect(res.status(), 'the render was accepted').toBe(202);
  const { executionId } = (await res.json()) as { executionId: string };
  await expect.poll(async () => {
    const rows = await seed.api.json<Array<{ executionId: string; status: string; errorMessage: string | null; preset: string }>>('GET', `${seed.api.timelinesUrl(seed.projectId, ed.timelineId)}/renders`);
    const row = rows.find((r) => r.executionId === executionId);
    if (row?.status === 'Failed') throw new Error(`render failed: ${row.errorMessage}`);
    return row?.status;
  }, { message: 'the render passes', timeout: 300_000, intervals: [3000] }).toBe('Passed');
  await expect(ed.testId('film-card')).toBeVisible({ timeout: 30_000 });
  const [download] = await Promise.all([page.waitForEvent('download'), tapOn(ed, ed.testId('film-download'), touch)]);
  fs.mkdirSync(DL_DIR, { recursive: true });
  const file = path.join(DL_DIR, `${executionId}-${download.suggestedFilename()}`);
  await download.saveAs(file);
  expect(download.suggestedFilename()).toBe(`${name}.mp4`);
  expect(fs.statSync(file).size, 'the downloaded film has bytes').toBeGreaterThan(10_000);
  const facts = probe(file);
  expect([facts.video?.width, facts.video?.height], 'the film is 9:16 (1080x1920)').toEqual([1080, 1920]);
  expect(facts.audio, 'the film has an audio stream').toBeTruthy();
  expect(facts.durationSec, 'the film is the length of the edit (6 s)').toBeGreaterThan(5.5);
  expect(facts.durationSec).toBeLessThan(7.5);
  expect(meanVolumeDb(file, 0.2, 1.0), 'the film is audible').toBeGreaterThan(-60);

  // The whole journey kept the document safe and never used the keyboard outside a text field.
  await ed.expectSaved();
  await expectMenusOnlyAndNoSaveErrors(ed);
}

/** Plays the preview at `aFrame` (inside A) and `cFrame` (inside C): the pictures and the sounds are the right ones. */
async function playsAndShows(c: Ctx, ids: Record<'a' | 'b' | 'c' | 'm', string>, aFrame: number, cFrame: number): Promise<void> {
  const { ed, touch } = c;
  const sampleUntil = async (ok: (g: Grid) => boolean, message: string) => {
    await expect.poll(async () => ok(await sampleElement(ed.page, ed.viewer)), { message, timeout: 20_000, intervals: [500, 1000, 2000] }).toBe(true);
  };
  const play = async (seconds: number, expectDuration: number, message: string) => {
    const before = (await startedDurations(ed.page)).length;
    await tapOn(ed, ed.testId('transport-play'), touch);
    try {
      await expect.poll(async () => (await startedDurations(ed.page)).slice(before).some((d) => Math.abs(d - expectDuration) < 0.5), { message, timeout: 10_000 }).toBe(true);
    } finally {
      await tapOn(ed, ed.testId('transport-play'), touch);
    }
    void seconds;
  };
  const refA = referenceGrid(path.join(MEDIA_DIR, 'clipA.mp4'), 2 + aFrame / FPS);
  const refC = referenceGrid(path.join(MEDIA_DIR, 'vertical.mp4'), 1 + (cFrame - 120) / FPS);
  await seekTo(ed, aFrame, touch);
  await sampleUntil((g) => distance(g, refA) < distance(g, refC), `frame ${aFrame} shows A's picture (its marked second, not C)`);
  await play(1.5, 12, 'A plays its own 12 s source (the camera sound)');
  await seekTo(ed, cFrame, touch);
  await sampleUntil((g) => columnLuma(g, 13, 19) > 40 && columnLuma(g, 0, 8) < 12 && columnLuma(g, 24, 32) < 12 && distance(g, refC) < distance(g, refA), `frame ${cFrame} shows C's vertical picture, pillarboxed`);
  await play(1.5, 6, 'C plays its own 6 s source after the gap was closed');
  void ids;
}

test.describe('J1 first cut', () => {
  test('J1 desktop: upload, insert with marks, ripple delete, close gap, title, duck, Warm look, 9:16 render, download (menus only)', async ({ page, seed, freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(900_000);
    await page.addInitScript(JOURNEY_INSTRUMENT);
    const ed = await openEditor(await freshTimeline('blank'));
    await journey1({ ed, seed, touch: false });
  });

  test.describe('tablet 1024x768, touch, coarse pointer', () => {
    test.use({ viewport: TABLET, hasTouch: true });
    test('J1 tablet: the same journey by touch, through menus', async ({ page, seed, freshTimeline, openEditor }) => {
      runOn('desktop');
      test.setTimeout(900_000);
      await page.addInitScript(JOURNEY_INSTRUMENT);
      const ed = await openEditor(await freshTimeline('blank'));
      expect(await page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'the page sees a coarse pointer').toBe(true);
      await journey1({ ed, seed, touch: true });
    });
  });
});
