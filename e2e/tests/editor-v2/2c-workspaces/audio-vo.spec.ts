import { test, expect } from '../../../fixtures/editor-v2';
import { downloadRender, probe, runOn } from '../../../fixtures/editor-v2/rows';
import { audioStats, renderFromDeliver } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, AUDIO > Voiceover (WS-156, TL-420..422). Headless Chromium needs a fake microphone; the flags and the microphone
 * permission are set for THIS file only (the shared config is untouched). Playwright WebKit has no fake device, so the
 * WebKit project is not exercised here (its Voiceover panel is checked for an honest message instead).
 */
test.skip(({ browserName }) => browserName !== 'chromium', 'needs Chromium\'s fake microphone device');

test.use({
  launchOptions: { args: ['--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required', '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] },
  permissions: ['microphone'],
});

test('WS-156 Voiceover: Record, Stop and place stores an audio take, puts a ~2 s clip at the playhead, one undo removes it, and a render has the sound', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  test.setTimeout(300_000);
  const id = await freshTimeline('blank');
  const ed = await openEditor(id);
  ed.page.on('requestfailed', (r) => console.log('DBG-FAILED', r.method(), r.url().slice(0, 140), r.failure()?.errorText));
  ed.page.on('response', (r) => { if (r.status() >= 400 || /upload/.test(r.url())) console.log('DBG-RESP', r.request().method(), r.status(), r.url().slice(0, 140)); });
  await ed.workspace('audio');
  await expect(ed.testId('vo-unavailable'), 'the browser can record (fake microphone)').toHaveCount(0);
  const before = (await api.listFiles(seed.projectId)).length;

  await ed.tap(ed.testId('vo-record'));
  await expect(ed.testId('vo-stop')).toBeVisible();
  await ed.page.waitForTimeout(2300); // the take has to be recorded for real: the recorder measures its own duration
  await ed.tap(ed.testId('vo-stop'));
  // A failure shows its reason in the panel: surface it instead of timing out on the document.
  await expect.poll(async () => {
    if (await ed.testId('vo-error').count()) throw new Error(`Voiceover failed: ${await ed.testId('vo-error').textContent()}`);
    return (await ed.serverTimeline()).document.tracks.some((tr) => tr.kind === 'audio' && tr.items.length === 1);
  }, { timeout: 60_000, message: 'the take is placed' }).toBe(true);
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.kind === 'audio' && tr.items.length === 1), 'the take is placed on an audio track', 60_000);
  const track = tl.document.tracks.find((tr) => tr.kind === 'audio' && tr.items.length === 1)!;
  const clip = track.items[0];
  expect(clip.recordIn, 'at the playhead where recording started').toBe(0);
  expect(clip.recordOut - clip.recordIn, 'about two seconds at 30 fps').toBeGreaterThan(40);
  expect(clip.recordOut - clip.recordIn).toBeLessThan(110);

  // The take is an ordinary project file, typed as audio (not video).
  const files = await api.json<Array<{ id: string; originalFileName: string; mimeType: string }>>('GET', `/api/v1/projects/${seed.projectId}/files`);
  const list = Array.isArray(files) ? files : ((files as unknown as { items: typeof files }).items ?? []);
  const take = list.find((f) => /^vo-.*\.webm$/.test(f.originalFileName));
  expect(take, `a vo-*.webm project file exists (${list.length} files, was ${before})`).toBeTruthy();
  expect(take!.mimeType, 'the take is stored as audio').toMatch(/^audio\//);
  expect(clip.assetRef?.projectFileId).toBe(take!.id);

  // The render has the recorded sound (the fake microphone beeps).
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const file = await downloadRender(api, seed.projectId, r, 'mp4');
  expect(probe(file).audio, 'the render has an audio stream').toBeTruthy();
  expect(audioStats(file, 0, 2).maxDb, 'the recorded take is audible in the render').toBeGreaterThan(-50);

  // One undo removes the placement (focus out of any field first).
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((t) => !t.document.tracks.some((tr) => tr.kind === 'audio' && tr.items.length > 0), 'undo removes the voiceover clip');
});

test('WS-156 Discard throws the take away: nothing is uploaded or placed', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await freshTimeline('blank');
  const ed = await openEditor(id);
  await ed.workspace('audio');
  const before = (await api.listFiles(seed.projectId)).length;
  await ed.tap(ed.testId('vo-record'));
  await ed.tap(ed.testId('vo-cancel'));
  await expect(ed.testId('vo-record')).toBeVisible();
  await ed.page.waitForTimeout(1000);
  expect((await api.listFiles(seed.projectId)).length, 'no file was uploaded').toBe(before);
  expect((await ed.serverTimeline()).document.tracks.every((t) => t.items.length === 0)).toBe(true);
});

