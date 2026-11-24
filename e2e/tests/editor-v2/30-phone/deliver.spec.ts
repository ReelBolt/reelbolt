import { test, expect } from '../../../fixtures/editor-v2';
import { renderAndWait, runOn, startRender } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 3 / T-006: phone Deliver and share (RM.9, R06.8; x1 acceptance criterion 8). The header Export opens a
 * full-height sheet; Render sits in a sticky footer; a finished film is downloaded or shared. Renders run
 * through the local ffmpeg (no paid provider is involved). Every outcome is read from real state: the render
 * queue API, the downloaded file's size, the arguments the page handed to navigator.share.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const SHORT = { range: { inFrame: 0, outFrame: 12 }, quality: 'draft' } as const;

test.describe('RM.9 phone Deliver', () => {
  test('RM.9 header Export opens Deliver and Render is visible without scrolling', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.tap(ed.testId('phone-export'));
    await expect(ed.testId('deliver-workspace')).toBeVisible();
    await expect(ed.testId('deliver-light-preview'), 'the heavy viewer is replaced by the light preview').toBeVisible();
    const render = ed.testId('render-start');
    await expect(render).toBeVisible();
    const box = (await render.boundingBox())!;
    const viewport = ed.page.viewportSize()!;
    expect(box.y, 'Render starts inside the viewport').toBeGreaterThanOrEqual(0);
    expect(box.y + box.height, 'Render ends inside the viewport').toBeLessThanOrEqual(viewport.height);
    expect(box.height, 'Render is a 44 px control').toBeGreaterThanOrEqual(43.5);
    // Sticky: the same box after scrolling the body to its end.
    await ed.testId('deliver-phone-scroll').evaluate((el) => { el.scrollTop = el.scrollHeight; });
    const after = (await render.boundingBox())!;
    expect(Math.abs(after.y - box.y), 'Render did not move when the body scrolled').toBeLessThan(1);
    await expect(ed.testId('deliver-parity')).toContainText('Preview is paused');
  });

  test('RM.9 Render from the sheet renders the seeded timeline and the queue card shows the finished film', async ({ freshTimeline, openEditor, api, seed }) => {
    test.setTimeout(300_000);
    const id = await freshTimeline('seeded');
    const ed = await openEditor(id);
    await ed.tap(ed.testId('phone-export'));
    await ed.tap(ed.testId('render-start'));
    await expect.poll(async () => {
      const list = await api.json<Array<{ status: string; hasOutput: boolean }>>('GET', `${api.timelinesUrl(seed.projectId, id)}/renders`);
      return list.some((r) => r.status === 'Passed' && r.hasOutput);
    }, { message: 'the sheet started a render that finished with a film', timeout: 240_000, intervals: [2000] }).toBe(true);
    await expect(ed.testId('film-card')).toBeVisible({ timeout: 30_000 });
    await expect(ed.testId('film-player')).toHaveAttribute('playsinline', '');
  });

  test('RM.9 the finished film downloads under its own name with real bytes', async ({ freshTimeline, openEditor, api, seed }) => {
    test.setTimeout(300_000);
    const id = await freshTimeline('seeded');
    const done = await renderAndWait(api, seed.projectId, id, SHORT);
    expect(done.status).toBe('Passed');
    const ed = await openEditor(id);
    await ed.tap(ed.testId('phone-export'));
    await expect(ed.testId('film-card')).toBeVisible();
    const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('film-download'))]);
    expect(download.suggestedFilename(), 'the real file name, not a GUID').toMatch(/^[^/]+\.mp4$/);
    const stream = await download.createReadStream();
    let bytes = 0;
    for await (const chunk of stream) bytes += (chunk as Buffer).length;
    expect(bytes, 'the downloaded film has bytes').toBeGreaterThan(0);
  });

  test('RM.9 Share hands the film file to the share sheet when the device can share files', async ({ freshTimeline, openEditor, api, seed }) => {
    test.setTimeout(300_000);
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, SHORT);
    await openEditor(id).then(async (ed) => {
      await ed.page.addInitScript(() => {
        const shared: Array<{ files: Array<{ name: string; size: number }>; title?: string }> = [];
        (window as unknown as { __shared: typeof shared }).__shared = shared;
        Object.defineProperty(navigator, 'canShare', { value: (data?: ShareData) => Boolean(data?.files?.length), configurable: true });
        Object.defineProperty(navigator, 'share', {
          value: async (data: ShareData) => { shared.push({ files: (data.files ?? []).map((f) => ({ name: f.name, size: f.size })), title: data.title }); },
          configurable: true,
        });
      });
      await ed.page.reload();
      await ed.tap(ed.testId('phone-export'));
      await ed.tap(ed.testId('film-share'));
      await expect.poll(() => ed.page.evaluate(() => (window as unknown as { __shared: unknown[] }).__shared.length), { message: 'navigator.share was called' }).toBe(1);
      const call = await ed.page.evaluate(() => (window as unknown as { __shared: Array<{ files: Array<{ name: string; size: number }> }> }).__shared[0]);
      expect(call.files).toHaveLength(1);
      expect(call.files[0].size, 'the shared file has bytes').toBeGreaterThan(0);
      expect(call.files[0].name).toMatch(/\.mp4$/);
    });
  });

  test('RM.9 a device that cannot share shows no Share button, only Download', async ({ freshTimeline, openEditor, api, seed }) => {
    test.setTimeout(300_000);
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, SHORT);
    const ed = await openEditor(id);
    await ed.page.addInitScript(() => {
      Object.defineProperty(navigator, 'share', { value: undefined, configurable: true });
      Object.defineProperty(navigator, 'canShare', { value: undefined, configurable: true });
    });
    await ed.page.reload();
    await ed.tap(ed.testId('phone-export'));
    await expect(ed.testId('film-download')).toBeVisible();
    await expect(ed.testId('film-share')).toHaveCount(0);
  });

  test('RM.9 the queue is refetched when the page returns to the foreground', async ({ freshTimeline, openEditor, api, seed }) => {
    test.setTimeout(300_000);
    const id = await freshTimeline('seeded');
    const ed = await openEditor(id);
    await ed.tap(ed.testId('phone-export'));
    await expect(ed.testId('film-empty')).toBeVisible();
    // A render the page never heard about (started "while the tab slept").
    const started = await startRender(api, seed.projectId, id, SHORT);
    await ed.page.evaluate(() => {
      Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
      Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await expect.poll(async () => {
      // The queue rows carry the render's id and say it in the activity words (Waiting for a free slot, Running, Done), so ask for the row itself.
      return ed.testId(`render-row-${started.executionId}`).count();
    }, { message: `the queue shows render ${started.executionId} after the foreground refetch`, timeout: 30_000 }).toBe(1);
  });

  test('RM.9 a render that finishes while Export is closed puts a Ready badge on Export', async ({ freshTimeline, openEditor, api, seed }) => {
    test.setTimeout(300_000);
    const id = await freshTimeline('seeded');
    const ed = await openEditor(id);
    await startRender(api, seed.projectId, id, SHORT);
    const flip = () => ed.page.evaluate(() => {
      Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await expect.poll(async () => { await flip(); return ed.testId('phone-export-badge').count(); }, { message: 'Export shows Ready', timeout: 240_000, intervals: [3000] }).toBe(1);
    await expect(ed.testId('phone-export-badge')).toHaveText('Ready');
    await ed.tap(ed.testId('phone-export'));
    await expect(ed.testId('phone-export-badge'), 'opening Export clears the badge').toHaveCount(0);
  });
});
