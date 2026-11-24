import * as path from 'path';
import { test, expect, SEEDED_LAYOUT } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { distance, meanLuma, sampleElement, type Grid } from '../../../fixtures/editor-v2/pixels';
import { runOn, uniqueCopy } from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { knownFailure } from '../expected-failures';

/**
 * 04 shell, viewer and proxies ❌ rows. Acceptance: remediation-plan.md W0.3 (preview) and W0.4 (upload
 * pipeline); verdict-table.md P2-LAZY / P3-VIEW / P3-XPORT rows. On the local stack the program viewer
 * requests no media at all (PA-1), so the viewer rows fail at their first step there.
 */

const tcFrames = (tc: string) => {
  const [h, m, s, f] = tc.split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * 30 + f;
};

/** P2-LAZY-04 (W0.4): an uploaded file put on a timeline gets a proxy without anyone opening the Media tab. */
test('P2-LAZY-04 an uploaded clip on an opened timeline gets its proxy with no Media-tab click', async ({ freshTimeline, openEditor, api, seed }, testInfo) => {
  runOn('desktop');
  knownFailure('P2-LAZY-04');
  test.setTimeout(240_000);
  const fileId = await api.upload(seed.projectId, uniqueCopy(path.join(ensureMedia(), 'clipB.mp4')), 'video/mp4', `lazy04-${testInfo.project.name}-${Date.now()}.mp4`);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'lazy.mp4', fileId, 0, 120)];
  });
  await openEditor(id);
  await expect.poll(async () => {
    const rows = await api.json<Array<{ kind: string }>>('GET', `/api/v1/projects/${seed.projectId}/media/file:${fileId}/derivatives`);
    return rows.some((r) => r.kind === 'proxy');
  }, { message: 'a proxy exists for the uploaded clip (upload or timeline open requested it)', timeout: 150_000, intervals: [3000] }).toBe(true);
});

/** P3-VIEW-01 (M) (W0.3, PA-1/PA-3): on a phone the program viewer shows the clip on a cold load and the picture moves while playing. */
test('P3-VIEW-01 phone program viewer shows the timeline on a cold load and plays it', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P3-VIEW-01');
  const ed = await openEditor(seed.seededTimelineId);
  let g0: Grid = [];
  await expect.poll(async () => meanLuma((g0 = await sampleElement(ed.page, ed.viewer))), { message: 'cold load: frame 0 is not black', timeout: 20_000, intervals: [1000] }).toBeGreaterThan(20);
  await ed.tap(ed.testId('transport-play'));
  await ed.page.waitForTimeout(2000);
  await ed.tap(ed.testId('transport-play'));
  const g1 = await sampleElement(ed.page, ed.viewer);
  expect(meanLuma(g1), 'still not black after playing').toBeGreaterThan(20);
  expect(distance(g0, g1), 'the picture changed while playing').toBeGreaterThan(1);
});

/**
 * P3-VIEW-05 (W0.3, PA-13): a clip whose preview is not available says why ("Preparing preview…" or the
 * failure) instead of a black viewer with a "faithful" badge. A fresh upload has no proxy when the editor
 * opens; once the pipeline makes one the viewer may simply show it: both are accepted, black silence is not.
 */
test('P3-VIEW-05 a clip without a preview says why instead of showing black', async ({ freshTimeline, openEditor, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  knownFailure('P3-VIEW-05');
  const fileId = await api.upload(seed.projectId, uniqueCopy(path.join(ensureMedia(), 'clipB.mp4')), 'video/mp4', `view05-${testInfo.project.name}-${Date.now()}.mp4`);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'noproxy.mp4', fileId, 0, 120)];
  });
  const ed = await openEditor(id);
  await ed.seek(30);
  const unavailable = ed.testId('preview-unavailable');
  await expect.poll(async () => {
    if (await unavailable.isVisible().catch(() => false)) {
      const text = ((await unavailable.textContent()) ?? '').trim();
      return /preparing|processing|not ready|failed|could not|no preview yet/i.test(text) ? 'explained' : `unexplained: ${text}`;
    }
    const luma = meanLuma(await sampleElement(ed.page, ed.viewer));
    if (luma > 20) return 'playing';
    const badge = ((await ed.testId('parity-badge').textContent().catch(() => '')) ?? '').trim();
    return `black, badge "${badge}"`;
  }, { message: 'the viewer either explains the missing preview or shows the clip', timeout: 20_000, intervals: [1000] }).toMatch(/^(explained|playing)$/);
});

/** P3-XPORT-03 (M): the phone timecode field offers a numeric keypad and reads digits right-aligned (1215 -> 00:00:12:15). */
test('P3-XPORT-03 phone timecode entry: numeric keypad, 1215 goes to 00:00:12:15', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P3-XPORT-03');
  const ed = await openEditor(seed.seededTimelineId);
  const field = ed.testId('timecode-field');
  await expect(field, 'the field asks the phone for a numeric keypad').toHaveAttribute('inputmode', /numeric|decimal|tel/);
  await field.click();
  await field.fill('1215');
  await field.press('Enter');
  await expect(ed.testId('status-timecode')).toHaveText('00:00:12:15');
});

/**
 * P3-XPORT-05 (W0.3, d7 N1/N2, PA-8): with in/out marks the ruler shades the range, and Play in to out
 * (Shift+Space) plays from in and stops at out. The audit: marks lit the buttons but nothing was shaded,
 * and play in->out / loop did not honour the range.
 */
test('P3-XPORT-05 in/out range is shaded on the ruler and Play in to out stops at out', async ({ seed, openEditor }) => {
  runOn('desktop');
  knownFailure('P3-XPORT-05');
  const ed = await openEditor(seed.seededTimelineId);
  const cv = await ed.canvasBox();
  const band = { x: await ed.frameX(70), y: cv.y + 2, width: (await ed.frameX(110)) - (await ed.frameX(70)), height: 22 };
  const before = await ed.page.screenshot({ clip: band });
  await ed.seek(60);
  await ed.tap(ed.testId('mark-in'));
  await ed.seek(120);
  await ed.tap(ed.testId('mark-out'));
  await ed.page.waitForTimeout(300);
  const after = await ed.page.screenshot({ clip: band });
  expect(Buffer.compare(before, after) !== 0, 'the ruler between in and out is drawn differently once marked').toBe(true);

  await ed.seek(SEEDED_LAYOUT.vertical[0]);
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Shift+Space');
  await expect.poll(async () => tcFrames((await ed.testId('status-timecode').textContent()) ?? '0:0:0:0'), { message: 'play in to out starts at in (60)', timeout: 3000, intervals: [100] }).toBeLessThan(120);
  await ed.page.waitForTimeout(3000);
  const stoppedAt = tcFrames((await ed.testId('status-timecode').textContent()) ?? '0:0:0:0');
  expect(stoppedAt, 'playback stopped at the out mark').toBeGreaterThanOrEqual(118);
  expect(stoppedAt).toBeLessThanOrEqual(120);
});
