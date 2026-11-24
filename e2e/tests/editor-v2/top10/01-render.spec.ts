import { test, expect } from '../../../fixtures/editor-v2';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #1 (report.md §3): Deliver > Render must start (2xx, not "Render refused ... $.preset") and the
 * render must finish. On origin/master the POST is a 400 (enum without a string converter) and, once that
 * is bypassed, a 500 because the TimelineRender workflow is queued as a Standard one.
 */
test('TOP10-01 P4-RENDER-01 Deliver > Render starts and the render finishes', async ({ freshTimeline, openEditor, api, seed }) => {
  knownFailure('TOP10-01');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const posts: Array<{ status: number; body: string }> = [];
  ed.page.on('response', async (res) => {
    if (res.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(res.url()).pathname)) {
      posts.push({ status: res.status(), body: (await res.text().catch(() => '')).slice(0, 300) });
    }
  });
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => posts.length, { message: 'the Render button sent a request' }).toBeGreaterThan(0);
  expect(posts[0].status, `render POST answered ${posts[0].status}: ${posts[0].body}`).toBeLessThan(300);

  // The render is listed and reaches a finished state (no paid provider involved: ffmpeg on the stack).
  let last = '';
  await expect.poll(async () => {
    const list = await api.json<unknown>('GET', `${api.timelinesUrl(seed.projectId, id)}/renders`);
    last = JSON.stringify(list).slice(0, 400);
    if (/"status":"(Failed|Cancelled)"/i.test(last)) return 'failed';
    return /"status":"(Passed|Completed|Succeeded|Done)"/i.test(last) ? 'finished' : 'running';
  }, { message: `render finishes (last list: ${last})`, timeout: 240_000, intervals: [3000] }).toBe('finished');
  await expect(ed.testId('render-queue')).toBeVisible();
});
