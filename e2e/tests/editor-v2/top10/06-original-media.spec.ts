import { test, expect } from '../../../fixtures/editor-v2';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #6 (report.md §3): an audio-track clip has no proxy, so the preview loads the original file.
 * That URL must be a media route that answers 2xx/206. On origin/master it is `/files/{id}/content`, the
 * text-editor route, which answers 409 "File is not a text-editable asset".
 */
test('TOP10-06 P2-VIEW-01 P8-MCAM-02 the audio-track clip loads its original media (no 409)', async ({ seed, openEditor }) => {
  knownFailure('TOP10-06');
  const tone: Array<{ status: number; url: string }> = [];
  const page = (await openEditor(seed.seededTimelineId)).page;
  page.on('response', (res) => {
    if (res.url().includes(seed.files.tone) && !/derivatives(\?|$)/.test(res.url())) tone.push({ status: res.status(), url: new URL(res.url()).pathname });
  });
  await page.reload();
  await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
  await page.getByTestId('transport-play').first().click();
  await expect.poll(() => tone.length, { message: 'the preview fetched tone.wav', timeout: 15_000 }).toBeGreaterThan(0);
  await page.getByTestId('transport-play').first().click();
  const bad = tone.filter((r) => r.status >= 400);
  expect(bad, `tone.wav fetches: ${JSON.stringify(tone.slice(0, 4))}`).toEqual([]);
  expect(tone.some((r) => r.status === 200 || r.status === 206), 'tone.wav served').toBe(true);
});
