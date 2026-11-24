import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
/**
 * R10.4 (T-005): "Preview 3 s" for render-only effects. The preview endpoints are stubbed at the API
 * (page.route) for the UI rows; the real-stack row uses the real engine (pending run). The document is
 * read back from the server to prove the overlay never writes it. Desktop only: the phone has no
 * render-only control (the Look sheet offers no mask, key, pin or qualifier), so none is added there.
 */
const MASK = { kind: 'rect', x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05, invert: false };
async function open(
  ft: Parameters<typeof clipsTimeline>[0],
  api: Parameters<typeof clipsTimeline>[1],
  seed: Parameters<typeof clipsTimeline>[2],
  openEditor: (id: string) => Promise<EditorPage>,
  stubs?: () => Promise<void>,
) {
  const clips: ClipSpec[] = [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150, extra: { mask: MASK } as never }];
  const id = await clipsTimeline(ft, api, seed, clips);
  // The estimate fetch fires ~400 ms after mount, so route stubs go in BEFORE the editor opens.
  if (stubs) await stubs();
  const ed = await openEditor(id);
  await ed.selectClipAt(30);
  await ed.seek(30);
  return { ed, id };
}
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

test('R10.4 Preview 3 s shows the price, then plays the stubbed result', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  let contentAsked = false;
  const { ed } = await open(freshTimeline, api, seed, openEditor, async () => {
    await page.route(/\/render\/estimate$/, (route) => route.fulfill(json({ billingEnabled: true, credits: 2, balanceCredits: 50, affordable: true })));
    await page.route(/\/render\/preview$/, (route) => route.fulfill(json({ executionId: 'job-1' }, 202)));
    await page.route(/\/render\/preview\/job-1$/, (route) => route.fulfill(json({ executionId: 'job-1', status: 'Passed', ready: true, durationSec: 3 })));
    await page.route(/\/render\/preview\/job-1\/content$/, (route) => {
      contentAsked = true;
      return route.fulfill({ status: 200, contentType: 'video/mp4', body: Buffer.from('stub') });
    });
  });
  const button = ed.testId('preview3s-button').first();
  await expect(button).toContainText('about 2 credits', { timeout: 15_000 });
  await button.click();
  await expect(ed.testId('preview3s-video')).toBeVisible({ timeout: 20_000 });
  expect(await ed.testId('preview3s-video').getAttribute('src')).toContain('/render/preview/job-1/content');
  expect(contentAsked, 'the player asks for the stubbed file').toBe(true);
});

test('R10.4 a start refused with 402 says why and plays nothing', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  let started = 0;
  const { ed } = await open(freshTimeline, api, seed, openEditor, async () => {
    await page.route(/\/render\/estimate$/, (route) => route.fulfill(json({ billingEnabled: true, credits: 5, balanceCredits: 50, affordable: true })));
    await page.route(/\/render\/preview$/, (route) => {
      started += 1;
      return route.fulfill(json({ code: 'INSUFFICIENT_CREDITS', message: 'You need 2 more credits for this preview.', missing: 2 }, 402));
    });
  });
  const button = ed.testId('preview3s-button').first();
  await expect(button).toContainText('about 5 credits', { timeout: 15_000 });
  await button.click();
  await expect(ed.testId('preview3s-problem')).toContainText('You need 2 more credits', { timeout: 20_000 });
  await expect(ed.testId('preview3s-video')).toHaveCount(0);
  expect(started, 'the start was attempted once and refused').toBe(1);
});

test('R10.4 closing the preview leaves the document untouched', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const { ed } = await open(freshTimeline, api, seed, openEditor, async () => {
    await page.route(/\/render\/estimate$/, (route) => route.fulfill(json({ billingEnabled: false, affordable: true })));
    await page.route(/\/render\/preview$/, (route) => route.fulfill(json({ executionId: 'job-2' }, 202)));
    await page.route(/\/render\/preview\/job-2$/, (route) => route.fulfill(json({ executionId: 'job-2', status: 'Passed', ready: true })));
    await page.route(/\/render\/preview\/job-2\/content$/, (route) => route.fulfill({ status: 200, contentType: 'video/mp4', body: Buffer.from('stub') }));
  });
  const before = await ed.serverTimeline();
  await expect(ed.testId('preview3s-button').first()).toContainText(/included/i, { timeout: 15_000 });
  await ed.testId('preview3s-button').first().click();
  await expect(ed.testId('preview3s-video')).toBeVisible({ timeout: 20_000 });
  await ed.testId('preview3s-close').click();
  await expect(ed.testId('preview3s-overlay')).toHaveCount(0);
  const after = await ed.serverTimeline();
  expect(after.revision, 'no revision was written').toBe(before.revision);
  expect(after.document).toEqual(before.document);
});

// pending run: uses the real engine and real ffmpeg, so it needs the full stack.
test('R10.4 preview job renders a real file', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const { ed, id } = await open(freshTimeline, api, seed, openEditor);
  const base = `/api/v1/projects/${seed.projectId}/edit-timelines/${id}/render`;
  const started = await ed.page.evaluate(async (url) => {
    const r = await fetch(`${url}/preview`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ atFrame: 30, seconds: 2 }) });
    return { status: r.status, body: (await r.json()) as { executionId?: string } };
  }, base);
  expect(started.status).toBe(202);
  const jobId = started.body.executionId!;
  await expect
    .poll(
      async () => ed.page.evaluate(async (u) => ((await (await fetch(u)).json()) as { ready: boolean }).ready, `${base}/preview/${jobId}`),
      { timeout: 240_000, intervals: [2000] },
    )
    .toBe(true);
  const bytes = await ed.page.evaluate(async (u) => (await (await fetch(u)).arrayBuffer()).byteLength, `${base}/preview/${jobId}/content`);
  expect(bytes, 'the rendered file has bytes').toBeGreaterThan(0);
});
