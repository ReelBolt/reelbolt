import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { type EditorPage } from '../../../fixtures/editor-v2';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import { frameRgb, runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane B, S12 (inventory TL-070 / TL-381): "Save still" writes the picture on screen. In the degraded
 * single-clip preview the program canvas is hidden and a <video> shows the picture, so the saved PNG used to be
 * blank. Both sessions must save a real picture; the proof is the downloaded file's pixels (host ffmpeg).
 */

async function saveStill(ed: EditorPage): Promise<string> {
  const download = ed.page.waitForEvent('download');
  await ed.tap(ed.testId('save-still'));
  const d = await download;
  expect(d.suggestedFilename()).toMatch(/preview\.png$/);
  const dir = path.join(ARTIFACTS, 'stills');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${Date.now()}-${d.suggestedFilename()}`);
  await d.saveAs(file);
  return file;
}

const isPicture = (file: string): boolean => {
  const [r, g, b] = frameRgb(file, 0);
  return Math.max(r, g, b) > 30;
};

test('S12 TL-070 Save still saves a real picture from the live preview and from the degraded single-clip preview', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await expect.poll(async () => isPicture(await saveStill(ed)), { message: 'the live preview saves a picture', timeout: 30_000, intervals: [1000] }).toBe(true);

  // Degraded preview (Preview options > Degraded preview): a <video> shows the picture.
  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  await ed.page.getByTestId('degraded-toggle').click({ force: true });
  await ed.page.keyboard.press('Escape');
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'fallback', { timeout: 30_000 });
  await ed.seek(45);
  await expect
    .poll(async () => ed.page.getByTestId('program-video').evaluate((v: HTMLVideoElement) => v.readyState >= 2 && v.videoWidth > 0), { message: 'the video has a frame', timeout: 30_000 })
    .toBe(true);
  await expect.poll(async () => isPicture(await saveStill(ed)), { message: 'the degraded preview saves a picture, not a blank canvas', timeout: 30_000, intervals: [1000] }).toBe(true);
});
