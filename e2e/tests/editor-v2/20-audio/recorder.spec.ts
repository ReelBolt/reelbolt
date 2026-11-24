import { test, expect } from '../../../fixtures/editor-v2';
import { audioTimeline, audioTrack } from '../../../fixtures/editor-v2/audio-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { MEDIA_DIR } from '../../../fixtures/editor-v2/env';
import path from 'node:path';

/**
 * R08.7 (T-010): the voiceover recorder against Chromium's fake microphone. Desktop Chromium only:
 * the phone projects have no fake device to record from (the WebKit and no-MediaRecorder path is the upload
 * fallback, covered by the unit tests and the upload spec below).
 */

// The Chromium launch flags below make WebKit refuse to start, so WebKit must skip before a browser launches.
test.skip(({ browserName }) => browserName !== 'chromium', 'row is checked on desktop Chromium');

test.use({
  launchOptions: {
    args: ['--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required', '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
  },
  permissions: ['microphone'],
});

type Clip = { recordIn: number; recordOut: number; assetRef?: { kind: string; projectFileId?: string | null } };

test('R08.7 recording after the count-in puts a take with a duration on a new audio track at the playhead', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 60 }] }]));
  await ed.seek(45);
  await ed.workspace('audio');
  await ed.testId('vo-record').scrollIntoViewIfNeeded();
  await ed.tap(ed.testId('vo-record'));
  await expect(ed.testId('vo-countin'), 'a 3-2-1 count-in first').toBeVisible();
  await expect(ed.testId('vo-stop')).toBeVisible({ timeout: 15_000 });
  await expect(ed.testId('vo-level'), 'the input level meter is on').toBeVisible();
  const t0 = Date.now();
  await expect.poll(() => Date.now() - t0, { message: 'record for a moment', intervals: [250] }).toBeGreaterThan(1500);
  await ed.tap(ed.testId('vo-stop'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.name === 'Voiceover'), 'the take landed on a new Voiceover track', 40_000);
  const vo = tl.document.tracks.find((t) => t.name === 'Voiceover')!;
  expect(vo.kind).toBe('audio');
  const clip = vo.items[0] as unknown as Clip;
  expect(clip.recordIn, 'at the playhead').toBe(45);
  expect(clip.recordOut - clip.recordIn, 'the take has a duration').toBeGreaterThan(0);
  expect(clip.assetRef?.kind, 'the take is a project file').toBe('projectFile');
  expect(clip.assetRef?.projectFileId).toBeTruthy();
  expect(audioTrack(tl, 'Voice'), 'the existing voice track is untouched').toBeTruthy();
  // Retake replaces the take (one apply): still one Voiceover track
  await ed.tap(ed.testId('vo-retake'));
  await expect(ed.testId('vo-stop')).toBeVisible({ timeout: 15_000 });
  await ed.tap(ed.testId('vo-stop'));
  await ed.expectServer((t) => {
    const tracks = t.document.tracks.filter((tr) => tr.name === 'Voiceover');
    return tracks.length === 1 && (tracks[0].items[0] as unknown as Clip).assetRef?.projectFileId !== clip.assetRef?.projectFileId;
  }, 'the retake replaced the first take', 40_000);
});

test('R08.7 with the microphone denied the recorder says so and a recording can be uploaded instead', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 60 }] }]));
  // The browser refuses the microphone: getUserMedia rejects like a blocked permission prompt.
  await ed.page.evaluate(() => {
    navigator.mediaDevices.getUserMedia = () => Promise.reject(new DOMException('denied', 'NotAllowedError'));
  });
  await ed.workspace('audio');
  await ed.tap(ed.testId('vo-record'));
  await expect(ed.testId('vo-error')).toContainText(/blocked the microphone/i);
  await expect(ed.testId('vo-error')).toContainText(/upload a recording/i);
  await ed.seek(30);
  const chooser = ed.page.waitForEvent('filechooser');
  await ed.tap(ed.testId('vo-upload'));
  await (await chooser).setFiles(path.join(MEDIA_DIR, 'tone.wav'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.name === 'Voiceover'), 'the uploaded recording became a clip', 40_000);
  const clip = tl.document.tracks.find((t) => t.name === 'Voiceover')!.items[0] as unknown as Clip;
  expect(clip.recordOut - clip.recordIn, 'about five seconds of tone at 30 fps').toBeGreaterThan(100);
});
