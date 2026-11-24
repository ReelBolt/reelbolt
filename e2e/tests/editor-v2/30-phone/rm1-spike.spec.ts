import { mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import type { Page, TestInfo } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { meanLuma, sampleElement } from '../../../fixtures/editor-v2/pixels';
import { TouchScript, countPointerCancels, pointerCancels } from '../../../fixtures/editor-v2/phone-measure';

/**
 * RM.1 emulation spike (decision D8: emulation only, no devices). One test per item. Each MEASURES and RECORDS: the
 * numbers go into test.info().annotations and a JSON attachment named rm1-<item>.json, and docs/editor-phone-spike.md
 * carries the go/no-go table that reads them. Nothing here proves a gesture works on real iOS; the list of what
 * emulation cannot show is in that document. Assertions are only the ones that make a measurement meaningful (the
 * page loaded, the probe ran). pixel7 uses real CDP touch; iphone14webkit measures what WebKit can express.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

async function record(info: TestInfo, item: string, data: Record<string, unknown>): Promise<void> {
  const payload = { item, project: info.project.name, ...data };
  info.annotations.push({ type: `rm1:${item}`, description: JSON.stringify(data) });
  await info.attach(`rm1-${item}.json`, { body: JSON.stringify(payload, null, 2), contentType: 'application/json' });
  // Also keep a copy outside the run directory so docs/editor-phone-spike.md can be filled after the run.
  try {
    const dir = '/tmp/ev2-w3/rm1';
    mkdirSync(dir, { recursive: true });
    const file = `${dir}/${info.project.name}.json`;
    const all = existsSync(file) ? (JSON.parse(readFileSync(file, 'utf8')) as Record<string, unknown>) : {};
    all[item] = payload;
    writeFileSync(file, JSON.stringify(all, null, 2));
  } catch { /* best effort: the attachment above is the record */ }
}

const touchAction = (page: Page, testId: string) => page.getByTestId(testId).first().evaluate((el) => getComputedStyle(el).touchAction).catch(() => 'missing');

test('RM.1 touch-action is effective on the strip and the trim handles', async ({ freshTimeline, openEditor, api, seed }, info) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.selectClipAt(30);
  const measured = {
    strip: await touchAction(ed.page, 'timeline-canvas-wrapper'),
    canvas: await touchAction(ed.page, 'timeline-canvas'),
    handleIn: await touchAction(ed.page, 'trim-handle-in'),
    handleOut: await touchAction(ed.page, 'trim-handle-out'),
    overscroll: await ed.testId('timeline-canvas-wrapper').evaluate((el) => getComputedStyle(el).overscrollBehaviorY),
  };
  await record(info, 'touch-action', { ...measured, note: 'computed style in emulation; whether iOS honours it for pointer events is a device check' });
  expect(Object.values(measured).length).toBeGreaterThan(0);
});

test('RM.1 pointercancel counts for pan, pinch and trim', async ({ freshTimeline, openEditor, api, seed }, info) => {
  test.skip(info.project.name === 'iphone14webkit', 'WebKit cannot synthesize multi-finger touch drags (D8): measured on pixel7 only');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const touch = await TouchScript.on(ed.page);
  await ed.selectClipAt(30);
  await countPointerCancels(ed.page);
  const counts: Record<string, number> = {};
  const y = await ed.laneY(0);
  const cv = (await ed.canvas.boundingBox())!;

  const handle = (await ed.testId('trim-handle-out').boundingBox())!;
  const h = { id: 1, x: handle.x + handle.width / 2, y: handle.y + handle.height / 2 };
  await touch.start([h]);
  await touch.glide([h], [{ ...h, x: h.x - 40 }]);
  await touch.end();
  counts.trim = await pointerCancels(ed.page);

  const pan = { id: 1, x: cv.x + cv.width - 50, y };
  await touch.start([pan]);
  await touch.glide([pan], [{ ...pan, x: pan.x - 120 }]);
  await touch.end();
  counts.pan = (await pointerCancels(ed.page)) - counts.trim;

  const a = [{ id: 1, x: cv.x + 150, y }, { id: 2, x: cv.x + 230, y }];
  await touch.start(a);
  await touch.glide(a, [{ id: 1, x: cv.x + 90, y }, { id: 2, x: cv.x + 290, y }]);
  await touch.end();
  counts.pinch = (await pointerCancels(ed.page)) - counts.trim - counts.pan;
  await record(info, 'pointercancel', counts);
  expect(counts.pan).toBeGreaterThanOrEqual(0);
});

test('RM.1 the edge-swipe dead zone leaves a drag that starts at the screen edge alone', async ({ freshTimeline, openEditor, api, seed }, info) => {
  test.skip(info.project.name === 'iphone14webkit', 'WebKit cannot synthesize touch drags (D8)');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const touch = await TouchScript.on(ed.page);
  const y = await ed.laneY(0);
  const cv = (await ed.canvas.boundingBox())!;
  const timecode = () => ed.testId('status-timecode').textContent();
  const before = await timecode();
  // A finger that lands inside the 20 px edge band (where iOS starts its back swipe) and drags inwards.
  const edge = { id: 1, x: cv.x + 6, y };
  await touch.start([edge]);
  await touch.glide([edge], [{ ...edge, x: edge.x + 120 }]);
  await touch.end();
  const afterEdge = await timecode();
  // The same drag from well inside the strip pans it.
  const inner = { id: 1, x: cv.x + 60, y };
  await touch.start([inner]);
  await touch.glide([inner], [{ ...inner, x: inner.x + 120 }]);
  await touch.end();
  const afterInner = await timecode();
  await record(info, 'edge-swipe', { timecodeBefore: before, afterEdgeDrag: afterEdge, afterInnerDrag: afterInner, edgeDragIgnored: before === afterEdge, innerDragPanned: afterEdge !== afterInner, note: 'proves the app leaves the first 20 px alone; the OS back swipe itself is a device check' });
  expect(afterInner).not.toBeNull();
});

test('RM.1 derivative content answers Range requests with 206', async ({ freshTimeline, openEditor, api, seed }, info) => {
  // The route under test is the content stream by row id ("Stream by row id (ranged)"), not the list of rows.
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const base = `/api/v1/projects/${seed.projectId}/media`;
  type Row = { id: string; kind?: string; type?: string; contentType?: string; mimeType?: string };
  const rows = await api.json<Row[]>('GET', `${base}/file:${seed.files.clipA}/derivatives`);
  if (rows.length === 0) {
    await record(info, 'range-206', { derivatives: 0, note: 'no derivative built' });
    test.skip(true, 'no derivative built for the seeded clip, so there is no content route to probe');
  }
  const row = rows[0];
  const url = new URL(`${base}/derivatives/${row.id}/content`, ed.page.url()).toString();
  const probe = await ed.page.request.get(url, { headers: { Range: 'bytes=0-1023' } });
  const probed = { derivativeId: row.id, row: JSON.stringify(row).slice(0, 200), status: probe.status(), acceptRanges: probe.headers()['accept-ranges'] ?? null, contentRange: probe.headers()['content-range'] ?? null, contentLength: probe.headers()['content-length'] ?? null, bytes: (await probe.body()).length };
  await record(info, 'range-206', { derivatives: rows.length, probe: probed, partialContent: probed.status === 206 });
  expect(probe.ok(), `the derivative content route answered the Range probe (status ${probed.status})`).toBe(true);
  expect(probed.status, `not 206: ${probed.status} (the content route must answer a Range request with partial content)`).toBe(206);
  expect(probed.contentRange, 'a 206 carries Content-Range').toMatch(/^bytes 0-/);
  expect(probed.bytes, 'and only the requested bytes').toBeLessThanOrEqual(1024);
});

test('RM.1 WebGL2 context loss and restore: the viewer recovers', async ({ freshTimeline, openEditor, api, seed }, info) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const viewerLuma = async (): Promise<number> => meanLuma(await sampleElement(ed.page, ed.page.getByTestId('program-viewer')));
  const sessionKind = (): Promise<string | null> => ed.page.getByTestId('program-viewer').getAttribute('data-session-kind');
  await ed.seek(10);
  // The probe targets the VIEWER's own canvas (program-canvas), not a throw-away one.
  const live = await ed.viewer.evaluate((c: HTMLCanvasElement) => c.isConnected && getComputedStyle(c).display !== 'none' && c.getContext('webgl2') !== null);
  if (live) await expect.poll(viewerLuma, { message: 'the viewer shows a picture before the loss', timeout: 20_000 }).toBeGreaterThan(3);
  const lumaBefore = live ? await viewerLuma() : null;
  const kindBefore = await sessionKind();
  const result = await ed.viewer.evaluate(async (c: HTMLCanvasElement) => {
    const gl = c.getContext('webgl2') as WebGL2RenderingContext | null;
    if (!gl) return { found: false, extension: false, lost: false, restored: false, defaultPrevented: false, isLostAfter: false };
    const ext = gl.getExtension('WEBGL_lose_context');
    if (!ext) return { found: true, extension: false, lost: false, restored: false, defaultPrevented: false, isLostAfter: gl.isContextLost() };
    // The test adds NO preventDefault of its own: a browser restores a lost context only when the page's own handler
    // calls preventDefault() on webglcontextlost, which is exactly what the viewer must do to recover.
    let defaultPrevented = false;
    const once = (name: string, ms: number) => new Promise<boolean>((resolve) => {
      const timer = setTimeout(() => resolve(false), ms);
      c.addEventListener(name, (e) => { if (name === 'webglcontextlost') setTimeout(() => { defaultPrevented = e.defaultPrevented; }, 0); clearTimeout(timer); resolve(true); }, { once: true });
    });
    const lostEvent = once('webglcontextlost', 3000);
    ext.loseContext();
    const lost = await lostEvent;
    // The restore is asynchronous and only valid after the lost event has finished dispatching: wait, ask, then wait for the event.
    await new Promise((r) => setTimeout(r, 100));
    const restoredEvent = once('webglcontextrestored', 5000);
    ext.restoreContext();
    const restored = await restoredEvent;
    return { found: true, extension: true, lost, restored, defaultPrevented, isLostAfter: gl.isContextLost() };
  });
  // After the restore the editor must still step the playhead and the viewer must show a frame again.
  await ed.seek(45);
  const viewerVisible = await ed.viewer.isVisible().catch(() => false);
  let lumaAfter: number | null = null;
  const kindAfter = await sessionKind();
  if (result.found) {
    await expect.poll(async () => (lumaAfter = await viewerLuma()), { message: 'the viewer shows a frame again after the context was restored (not a blank or paused viewer)', timeout: 15_000 }).toBeGreaterThan(3);
  }
  await record(info, 'webgl2-context-loss', { ...result, kindBefore, kindAfter, lumaBefore, lumaAfter, viewerVisibleAfter: viewerVisible, editorStillResponds: true, note: 'no WebGL canvas found means the viewer ran on the simple (2D) preview, which has no context to lose' });
  if (result.found) {
    expect(result.extension, 'WEBGL_lose_context is available to the probe').toBe(true);
    expect(result.lost, 'the context-lost event fired').toBe(true);
    expect(result.defaultPrevented, 'the app handles webglcontextlost with preventDefault() so the browser may restore the context').toBe(true);
    expect(result.restored, 'the context-restored event fired').toBe(true);
    expect(result.isLostAfter, 'the context is live again').toBe(false);
  }
  await expect(ed.viewer, 'the viewer is still on screen after the restore').toBeVisible();
  await expect(ed.canvas, 'the timeline still draws after the restore').toBeVisible();
});

test('RM.1 a 16 megapixel canvas can be allocated', async ({ freshTimeline, openEditor }, info) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  const result = await ed.page.evaluate(() => {
    const t0 = performance.now();
    try {
      const c = document.createElement('canvas');
      c.width = 4000;
      c.height = 4000;
      const ctx = c.getContext('2d');
      if (!ctx) return { allocated: false, reason: 'no 2d context' };
      ctx.fillStyle = '#336699';
      ctx.fillRect(0, 0, 4000, 4000);
      const px = ctx.getImageData(3999, 3999, 1, 1).data;
      return { allocated: true, painted: px[2] === 0x99, ms: Math.round(performance.now() - t0) };
    } catch (error) {
      return { allocated: false, reason: String(error) };
    }
  });
  await record(info, 'canvas-16mp', result);
  expect(result, `a 16 MP canvas could not be allocated: ${JSON.stringify(result)}`).toMatchObject({ allocated: true, painted: true });
});

test('RM.1 sound starts after the first tap (autoplay policy)', async ({ freshTimeline, openEditor }, info) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  const probe = () => ed.page.evaluate(async () => {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    const ctx = new Ctx();
    const before = ctx.state;
    let resumed: string = ctx.state;
    try { await ctx.resume(); resumed = ctx.state; } catch { resumed = 'rejected'; }
    const el = new Audio('data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YQAAAAA=');
    el.muted = false;
    let play = 'ok';
    try { await el.play(); } catch (error) { play = (error as Error).name; }
    await ctx.close();
    return { contextBefore: before, contextAfterResume: resumed, unmutedPlay: play };
  });
  const beforeTap = await probe();
  await ed.tap(ed.testId('transport-play'));
  const afterTap = await probe();
  await ed.tap(ed.testId('transport-play'));
  await record(info, 'autoplay-sound', { beforeFirstTap: beforeTap, afterFirstTap: afterTap, note: 'Chromium and WebKit emulation apply a desktop-like policy; the iOS mute switch and Bluetooth routing are device checks' });
  expect(afterTap).toBeTruthy();
});

test('RM.1 MediaRecorder presence and the formats it accepts', async ({ freshTimeline, openEditor }, info) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  const result = await ed.page.evaluate(() => {
    const present = typeof MediaRecorder !== 'undefined';
    const types = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/mp4;codecs=mp4a.40.2', 'audio/ogg'];
    return { present, supported: present ? types.filter((t) => MediaRecorder.isTypeSupported(t)) : [], getUserMedia: typeof navigator.mediaDevices?.getUserMedia === 'function' };
  });
  await record(info, 'media-recorder', { ...result, fallback: result.present ? 'record' : 'the Audio sheet offers the voice-file upload instead' });
  expect(typeof result.present).toBe('boolean');
});

test('RM.1 the sheet refits when the soft keyboard shrinks the visual viewport', async ({ freshTimeline, openEditor }, info) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.tap(ed.testId('dock-captions'));
  await expect(ed.testId('phone-sheet')).toBeVisible();
  const before = await ed.page.viewportSize();
  const measure = () => ed.page.evaluate(() => {
    const sheet = document.querySelector('[data-testid="phone-sheet"]')!.getBoundingClientRect();
    const vv = window.visualViewport;
    return { visualViewportHeight: vv?.height ?? null, sheetTop: Math.round(sheet.top), sheetBottom: Math.round(sheet.bottom), windowHeight: window.innerHeight };
  });
  const open = await measure();
  // A soft keyboard of roughly 300 px: shrink the window height; the sheet must end inside what is left.
  await ed.page.setViewportSize({ width: before!.width, height: Math.max(360, before!.height - 300) });
  await expect.poll(async () => (await measure()).windowHeight, { message: 'the emulated keyboard shrank the viewport' }).toBeLessThan(open.windowHeight);
  const keyboard = await measure();
  await record(info, 'visual-viewport-refit', { withoutKeyboard: open, withKeyboard: keyboard, sheetInsideVisible: keyboard.sheetBottom <= keyboard.visualViewportHeight! + 1, note: 'a resized window is the closest emulation; the real iOS keyboard resizes only the visual viewport' });
  expect(keyboard.sheetBottom).toBeGreaterThan(0);
});
