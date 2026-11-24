import * as path from 'path';
import { test, expect, SEEDED_LAYOUT } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import {
  downloadRender, meanVolumeDb, channelVolumesDb, frameRgb, probe, renderAndWait, runOn, startRender, uniqueCopy, waitRender, type RenderSummary,
} from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { knownFailure } from '../expected-failures';

/**
 * 06 render and deliver ❌ rows (remediation-plan.md W0.1; verdict-table.md P4-RENDER / P4-DELIVER /
 * P5-REND). The audit could not run any render (POST /render 400 on the preset string), so each row was
 * ❌ "blocked by RENDER-01"; here each row asserts its own acceptance through a real ffmpeg render on the
 * local stack (no paid provider is involved). Every render gets a small random change so the render cache
 * of an earlier run cannot answer it.
 */

const rnd = (lo: number, hi: number) => lo + Math.floor(Math.random() * (hi - lo + 1));

function expectPassed(r: RenderSummary, what: string): void {
  expect(r.status, `${what}: render ${r.status}${r.errorMessage ? ` (${r.errorMessage})` : ''}`).toBe('Passed');
  expect(r.hasOutput, `${what}: the render has an output`).toBe(true);
}

/** P4-RENDER-01: Deliver > Render accepts every preset string the web sends (202) and the render runs. */
test('P4-RENDER-01 every preset the Format list offers starts a render (202), and one from the Deliver panel finishes', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  knownFailure('P4-RENDER-01');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  if (test.info().project.name === 'desktop') {
    // 10-frame ranges keep the seven renders cheap.
    for (const preset of ['Hd1080p16x9', 'Uhd4k16x9', 'Vertical9x16', 'Square1x1', 'Portrait4x5', 'Master', 'AudioOnly']) {
      const res = await api.request.post(`${api.timelinesUrl(seed.projectId, id)}/render`, { data: { preset, range: { inFrame: 0, outFrame: 10 } }, failOnStatusCode: false });
      expect(res.status(), `preset ${preset}: ${(await res.text()).slice(0, 200)}`).toBe(202);
    }
  }
  const ed = await openEditor(id);
  const posts: Array<{ status: number; body: string }> = [];
  ed.page.on('response', async (res) => {
    if (res.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(res.url()).pathname)) posts.push({ status: res.status(), body: (await res.text().catch(() => '')).slice(0, 300) });
  });
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => posts.length, { message: 'the Render button sent a request' }).toBeGreaterThan(0);
  expect(posts[0].status, `render POST: ${posts[0].body}`).toBe(202);
  const started = JSON.parse(posts[0].body) as { executionId: string };
  expectPassed(await waitRender(api, seed.projectId, id, started.executionId), 'Deliver render');
});

/** P4-RENDER-04: the executor re-validates and resolves AssetRefs: a clip whose file is gone fails with a stated reason. */
test('P4-RENDER-04 a render whose clip file was deleted fails with a stated reason, shown in Deliver', async ({ freshTimeline, openEditor, api, seed }, testInfo) => {
  runOn('desktop');
  knownFailure('P4-RENDER-04');
  const fileId = await api.upload(seed.projectId, uniqueCopy(path.join(ensureMedia(), 'clipB.mp4')), 'video/mp4', `render04-${testInfo.project.name}-${Date.now()}.mp4`);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'gone.mp4', fileId, 0, 60)];
  });
  await api.json('DELETE', `/api/v1/projects/${seed.projectId}/files/${fileId}`, undefined, [200, 204]);
  const res = await api.request.post(`${api.timelinesUrl(seed.projectId, id)}/render`, { data: { preset: 'Hd1080p16x9' }, failOnStatusCode: false });
  const text = await res.text();
  let reason: string;
  if (res.status() === 202) {
    const r = await waitRender(api, seed.projectId, id, (JSON.parse(text) as { executionId: string }).executionId);
    expect(r.status, 'the render of a clip whose file is gone fails').toBe('Failed');
    reason = r.errorMessage ?? '';
    const ed = await openEditor(id);
    await ed.workspace('deliver');
    await expect(ed.testId('render-error'), 'Deliver shows why').toBeVisible();
  } else {
    expect(res.status(), `refused up front: ${text.slice(0, 200)}`).toBeGreaterThanOrEqual(400);
    expect(res.status()).toBeLessThan(500);
    reason = text;
  }
  expect(reason, 'the reason says the media is missing').toMatch(/missing|not found|no longer|deleted|unresolved|unresolvable|unknown|gone/i);
});

/** P4-RENDER-05: the planner splits into constant-composition regions (seeded timeline: cuts at 150, 300, 450). */
test('P4-RENDER-05 the render is planned as constant-composition regions', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-RENDER-05');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[3].fadeInFrames = rnd(1, 30); });
  const r = await renderAndWait(api, seed.projectId, id);
  expectPassed(r, 'seeded render');
  const list = r.regions?.list ?? [];
  expect(list.length, 'regions are reported').toBeGreaterThan(0);
  expect(list[0].startFrame).toBe(0);
  expect(list[list.length - 1].endFrame).toBe(600);
  for (let i = 1; i < list.length; i++) expect(list[i].startFrame, 'regions are contiguous').toBe(list[i - 1].endFrame);
  const bounds = new Set(list.map((g) => g.startFrame));
  for (const cut of [SEEDED_LAYOUT.clipB[0], SEEDED_LAYOUT.vertical[0], SEEDED_LAYOUT.still[0]]) expect(bounds.has(cut), `a region starts at the cut ${cut}`).toBe(true);
});

/** P4-RENDER-06: the mezzanine cache: a re-render is all hits; changing one clip re-renders only its region. */
test('P4-RENDER-06 a re-render hits the cache and a one-clip change re-renders only that region', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-RENDER-06');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[2].fadeInFrames = rnd(1, 30); });
  expectPassed(await renderAndWait(api, seed.projectId, id), 'first render');
  const again = await renderAndWait(api, seed.projectId, id);
  expectPassed(again, 'second render');
  expect(again.regions?.cached, `re-render: every region cached (${JSON.stringify(again.regions)})`).toBe(again.regions?.total);
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[3].fadeInFrames = rnd(31, 60); }); // the still, [450,600)
  const changed = await renderAndWait(api, seed.projectId, id);
  expectPassed(changed, 'render after the change');
  const miss = (changed.regions?.list ?? []).filter((g) => !g.cached);
  expect(miss.map((g) => [g.startFrame, g.endFrame]), 'only the changed region is rendered again').toEqual([[450, 600]]);
});

/** P4-RENDER-07: one-pass audio mix (keyframed gain, atempo, adelay, amix) and the loudness measurement. */
test('P4-RENDER-07 the audio mix honours keyed gain and a delayed sped-up clip, and the loudness is measured', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-RENDER-07');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    // Picture: the still only, so no clip sound from the video track can mask what the audio track does.
    doc.tracks[0].items = [mediaClip(crypto.randomUUID(), 'still.png', seed.files.still, 0, 300)];
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    a.items[0].gainDb = { value: 0, keys: [{ frame: 0, value: -40, interp: 'linear' }, { frame: 149, value: 0, interp: 'linear' }] };
    a.items.push(mediaClip(crypto.randomUUID(), 'tone fast', seed.files.tone, 210, 270, { playbackRate: 2, sourceIn: rnd(0, 20) }));
  });
  const r = await renderAndWait(api, seed.projectId, id);
  expectPassed(r, 'mix render');
  expect(Number.isFinite(r.loudness?.integratedLufs), `loudness measured: ${JSON.stringify(r.loudness)}`).toBe(true);
  const file = await downloadRender(api, seed.projectId, r);
  const [early, late, delayed, silent] = [meanVolumeDb(file, 0, 1), meanVolumeDb(file, 4, 5), meanVolumeDb(file, 7.2, 8.8), meanVolumeDb(file, 5.5, 6.5)];
  expect(late - early, `keyed gain: 4-5 s (${late} dB) is louder than 0-1 s (${early} dB)`).toBeGreaterThan(10);
  expect(delayed, 'the delayed sped-up clip is audible at 7-9 s').toBeGreaterThan(-45);
  expect(silent, 'nothing plays between the two tone clips (5.5-6.5 s)').toBeLessThan(-60);
});

/** P4-RENDER-08: delivery presets: Vertical 9:16 is 1080x1920, Audio only is an .m4a with no picture. */
test('P4-RENDER-08 Vertical renders 1080x1920 and Audio only renders sound with no picture', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-RENDER-08');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[0].fadeInFrames = rnd(1, 30); });
  const v = await renderAndWait(api, seed.projectId, id, { preset: 'Vertical9x16' });
  expectPassed(v, 'Vertical');
  const vp = probe(await downloadRender(api, seed.projectId, v));
  expect(vp.video, 'Vertical has a picture').toBeTruthy();
  expect(`${vp.video!.width}x${vp.video!.height}`).toBe('1080x1920');
  const a = await renderAndWait(api, seed.projectId, id, { preset: 'AudioOnly' });
  expectPassed(a, 'Audio only');
  const ap = probe(await downloadRender(api, seed.projectId, a, 'm4a'));
  expect(ap.video, 'Audio only has no picture').toBeUndefined();
  expect(ap.audio, 'Audio only has sound').toBeTruthy();
});

/** P4-RENDER-13: the output is a playable file served by the outputs endpoint (20 s, 1920x1080, sound). */
test('P4-RENDER-13 the finished render downloads as a playable 20 s 1080p file', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-RENDER-13');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[1].fadeInFrames = rnd(1, 30); });
  const r = await renderAndWait(api, seed.projectId, id);
  expectPassed(r, 'render');
  const p = probe(await downloadRender(api, seed.projectId, r));
  expect(Math.abs(p.durationSec - 20), `duration ${p.durationSec}s`).toBeLessThan(0.2);
  expect(`${p.video?.width}x${p.video?.height}`).toBe('1920x1080');
  expect(p.audio, 'the file has sound').toBeTruthy();
});

/** P4-RENDER-15: renders run as ordinary executions: a render started in Deliver shows its progress and Stop cancels it. */
test('P4-RENDER-15 a running render shows its progress and Stop cancels it', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-RENDER-15');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  // Every region uncached and a 4K output, so the render runs long enough to be watched and stopped.
  await api.putDocument(seed.projectId, id, (doc) => { for (const it of doc.tracks[0].items) it.fadeInFrames = rnd(1, 40); });
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-preset'));
  await ed.tap(ed.page.getByRole('option', { name: /^4K/ }));
  const posts: string[] = [];
  ed.page.on('response', async (res) => {
    if (res.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(res.url()).pathname)) posts.push(await res.text().catch(() => ''));
  });
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => posts.length, { message: 'the render was requested' }).toBeGreaterThan(0);
  const started = JSON.parse(posts[0]) as { executionId: string; workflowId: string };
  try {
    const row = ed.testId(`render-row-${started.executionId}`);
    await expect(row.getByTestId('render-progress-stage'), 'the running render shows its stage').toBeVisible({ timeout: 60_000 });
    await ed.tap(row.getByTestId('render-cancel'));
    const r = await waitRender(api, seed.projectId, id, started.executionId, 180_000);
    expect(r.status, 'Stop cancels the render').toBe('Cancelled');
  } finally {
    // Never leave a 4K render hogging the stack's render slot.
    await api.request.post(`/api/v1/projects/${seed.projectId}/workflows/${started.workflowId}/executions/${started.executionId}/stop`, { failOnStatusCode: false });
  }
});

/** P4-DELIVER-03: after a finished render of THIS revision the ruler shows the render-cache strip and the status bar counts it. */
test('P4-DELIVER-03 a finished render of this revision shows the ruler render-cache strip', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('P4-DELIVER-03');
  const id = await freshTimeline('seeded');
  expectPassed(await renderAndWait(api, seed.projectId, id), 'render');
  const ed = await openEditor(id);
  await expect(ed.testId('render-cache-chip'), 'the status bar counts the cached regions').toHaveText(/Saved preview frames: \d+ of \d+/, { timeout: 20_000 });
  const cv = await ed.canvasBox();
  const x = await ed.frameX(75);
  const png = await ed.page.screenshot({ clip: { x, y: cv.y + 24, width: 40, height: 4 } });
  const rgb = await ed.page.evaluate(async (b64) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas');
    c.width = img.width; c.height = img.height;
    const ctx = c.getContext('2d')!;
    ctx.drawImage(img, 0, 0);
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    let strip = 0;
    for (let i = 0; i < d.length; i += 4) {
      const blue = d[i + 2] > 150 && d[i] < 90; // #1c7ed6: region served from the cache
      const red = d[i] > 180 && d[i + 1] < 90 && d[i + 2] < 90; // #e03131: region rendered
      if (blue || red) strip++;
    }
    return strip;
  }, png.toString('base64'));
  expect(rgb, 'render-cache strip pixels (blue cached / red rendered) at the foot of the ruler').toBeGreaterThan(10);
});

/** P5-REND-01: a transformed clip (rotation, scale, position, opacity) and a cropped clip render (RD-2: `rotw(a)` was illegal). */
test('P5-REND-01 a rotated, scaled, faded and cropped clip renders', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P5-REND-01');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    const [a, b] = doc.tracks[0].items;
    a.transform = { rotation: { value: rnd(10, 30) }, scale: { value: 0.8 }, positionX: { value: 0.1 }, opacity: { value: 0.7 } };
    b.crop = { left: 0.1, top: 0.05, right: 0.1, bottom: 0.05 };
  });
  const r = await renderAndWait(api, seed.projectId, id);
  expectPassed(r, 'transform render');
  const file = await downloadRender(api, seed.projectId, r);
  const [cr, cg, cb] = frameRgb(file, 2.5);
  expect(cr + cg + cb, 'the transformed frame is not black').toBeGreaterThan(30);
});

/** P5-REND-02: a dissolve renders as an xfade between its legs, and a colour generator renders its colour. */
test('P5-REND-02 a dissolve and a red colour card render', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P5-REND-02');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    const [a, b] = doc.tracks[0].items;
    Object.assign(a, { recordIn: 0, recordOut: 60 });
    Object.assign(b, { recordIn: 80, recordOut: 210, sourceIn: 20 });
    doc.tracks[0].items = [
      a,
      { type: 'transition', id: crypto.randomUUID(), name: 'Dissolve', recordIn: 60, recordOut: 80, transitionType: 'dissolve', enabled: true },
      b,
      { type: 'generatorClip', id: crypto.randomUUID(), name: 'Red', recordIn: 210, recordOut: 300 + rnd(0, 20), generatorType: 'color', generatorConfig: { color: '#FF0000' }, enabled: true },
    ];
    doc.tracks.find((t) => t.kind === 'audio')!.items = [];
  });
  const r = await renderAndWait(api, seed.projectId, id);
  expectPassed(r, 'transition + generator render');
  const file = await downloadRender(api, seed.projectId, r);
  const [red, green, blue] = frameRgb(file, 8.5);
  expect(red, `the colour card is red (rgb ${red},${green},${blue})`).toBeGreaterThan(180);
  expect(green + blue).toBeLessThan(80);
});

/** P5-REND-03: a panned track gets its own bus: full-left pan puts the sound in the left channel. */
test('P5-REND-03 a track panned hard left renders on the left channel', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P5-REND-03');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks[0].items = [mediaClip(crypto.randomUUID(), 'still.png', seed.files.still, 0, 150)];
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    a.pan = -1;
    a.items[0].fadeInFrames = rnd(1, 20);
  });
  const r = await renderAndWait(api, seed.projectId, id);
  expectPassed(r, 'pan render');
  const [left, right] = channelVolumesDb(await downloadRender(api, seed.projectId, r));
  expect(left - right, `left ${left} dB vs right ${right} dB`).toBeGreaterThan(12);
});
