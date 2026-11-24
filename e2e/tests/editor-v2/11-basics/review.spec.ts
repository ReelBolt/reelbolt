import type { Page } from '@playwright/test';
import { test, expect, type Seed } from '../../../fixtures/editor-v2';
import { mediaClip, type EditorApi } from '../../../fixtures/editor-v2/api';
import { renderAndWait, runOn, type RenderSummary } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 11 basics: the phone review page (remediation-plan.md W0.6 "Comments pin to the paused frame; newest full-range
 * render with picture is chosen; deleting a root comment removes or reparents replies", e2e on pixel7;
 * report.md §5 UI2-01 / seed 15, UI2-10, api D9; verdict rows P9-REVIEW-06/10/12; T-031 item 6).
 * Renders are real (ffmpeg on the stack), so these rows run on pixel7 only, where the review page is used.
 */

type Comment = { id: string; parentId: string | null; frame: number; body: string };

const reviewUrl = (seed: Seed, timelineId: string) => `/app/editor/${seed.projectId}/review/${timelineId}`;
const comments = (api: EditorApi, seed: Seed, timelineId: string) => api.json<Comment[]>('GET', `${api.timelinesUrl(seed.projectId, timelineId)}/comments`);

/** A 2 s timeline (clipA 0-60) so a render stays quick. */
async function shortTimeline(freshTimeline: (from: 'blank') => Promise<string>, api: EditorApi, seed: Seed): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 60)];
  });
  return id;
}

function passed(r: RenderSummary, what: string): RenderSummary {
  expect(r.status, `${what}: ${r.status} ${r.errorMessage ?? ''}`).toBe('Passed');
  return r;
}

async function pauseVideoAt(page: Page, seconds: number): Promise<void> {
  const video = page.getByTestId('mobile-review-video');
  await expect(video, 'the review page shows the render').toBeVisible({ timeout: 30_000 });
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { message: 'the render loads' }).toBeGreaterThanOrEqual(1);
  await video.evaluate(async (v: HTMLVideoElement, s: number) => {
    v.pause();
    await new Promise<void>((resolve) => {
      v.addEventListener('seeked', () => resolve(), { once: true });
      v.currentTime = s;
    });
  }, seconds);
}

/**
 * UI2-01 (phone m3): a comment pins to the frame the video is paused on. The audit's failing order is forced:
 * the timeline answer is held back until the render list has arrived, so the <video> mounts late.
 */
test('UI2-01 a review comment pins to the paused frame even when the video mounts late', async ({ freshTimeline, api, seed, page }) => {
  runOn('pixel7');
  knownFailure('UI2-01');
  test.setTimeout(420_000);
  const id = await shortTimeline(freshTimeline, api, seed);
  passed(await renderAndWait(api, seed.projectId, id), 'render');
  await page.route(`**${api.timelinesUrl(seed.projectId, id)}`, async (route) => {
    if (route.request().method() === 'GET') await new Promise((r) => setTimeout(r, 2000));
    await route.continue();
  });
  await page.goto(reviewUrl(seed, id));
  await pauseVideoAt(page, 1.0);
  await expect(page.getByTestId('mobile-review-timecode'), 'the timecode follows the paused video').toHaveText('00:00:01:00');
  const body = `paused at one second ${Date.now() % 100000}`;
  await page.getByTestId('review-body').fill(body);
  await page.getByTestId('review-add').tap();
  await expect(page.getByTestId('review-panel')).toContainText(body);
  let listed = '';
  await expect.poll(async () => {
    const list = await comments(api, seed, id);
    listed = JSON.stringify(list.map((c) => [c.body, c.frame]));
    return list.find((c) => c.body === body)?.frame;
  }, { message: 'the comment is pinned to frame 30 (1 s at 30 fps), not frame 0' }).toBe(30);
  expect(listed).toContain(body);
});

/** UI2-10: the page plays the newest finished FULL render with a picture, not a newer range or audio-only one. */
test('UI2-10 the review page plays the newest full render with a picture', async ({ freshTimeline, api, seed, page }) => {
  runOn('pixel7');
  knownFailure('UI2-10');
  test.setTimeout(600_000);
  const id = await shortTimeline(freshTimeline, api, seed);
  const full = passed(await renderAndWait(api, seed.projectId, id), 'full render');
  passed(await renderAndWait(api, seed.projectId, id, { range: { inFrame: 0, outFrame: 10 } }), 'newer range render');
  passed(await renderAndWait(api, seed.projectId, id, { preset: 'AudioOnly' }), 'newer audio-only render');
  await page.goto(reviewUrl(seed, id));
  const video = page.getByTestId('mobile-review-video');
  await expect(video).toBeVisible({ timeout: 30_000 });
  await expect(video, 'the full render is the one played').toHaveAttribute('src', new RegExp(`/outputs/${full.stepResultId}/download$`));
  await expect(page.getByTestId('mobile-review-partial'), 'a full render is not flagged partial').toHaveCount(0);
});

/**
 * api D9 (phone): deleting a thread's first comment keeps its replies on screen: the oldest reply takes the
 * root's place (and frame), the other reply hangs off it, nothing is left pointing at the deleted root.
 */
test('api D9 deleting a root comment keeps its replies in the thread', async ({ freshTimeline, api, seed, page }) => {
  runOn('pixel7');
  knownFailure('api D9');
  const id = await freshTimeline('seeded');
  const url = `${api.timelinesUrl(seed.projectId, id)}/comments`;
  const root = await api.json<Comment>('POST', url, { body: 'Root note', frame: 45, durationFrames: null }, [200, 201]);
  await page.goto(reviewUrl(seed, id));
  for (const text of ['First reply', 'Second reply']) {
    await page.getByTestId(`review-reply-${root.id}`).tap();
    await page.getByTestId(`review-reply-body-${root.id}`).fill(text);
    await page.getByTestId(`review-reply-send-${root.id}`).tap();
    await expect(page.getByTestId(`review-thread-${root.id}`)).toContainText(text);
  }
  await page.getByTestId(`review-thread-${root.id}`).getByRole('button', { name: 'Delete comment' }).tap();
  await expect(page.getByTestId(`review-thread-${root.id}`), 'the deleted root thread is gone').toHaveCount(0);
  const panel = page.getByTestId('review-panel');
  await expect(panel, 'the first reply is still shown').toContainText('First reply');
  await expect(panel, 'the second reply is still shown').toContainText('Second reply');
  await expect(panel).not.toContainText('Root note');

  const list = await comments(api, seed, id);
  expect(list.some((c) => c.id === root.id), 'the root is deleted').toBe(false);
  expect(list.filter((c) => c.parentId === root.id), 'no reply points at the deleted root').toEqual([]);
  const heir = list.find((c) => c.body === 'First reply')!;
  expect(heir.parentId, 'the oldest reply is the new first comment').toBeNull();
  expect(heir.frame, 'it keeps the thread at the root frame').toBe(45);
  expect(list.find((c) => c.body === 'Second reply')!.parentId, 'the other reply hangs off it').toBe(heir.id);
  await page.reload();
  await expect(page.getByTestId(`review-thread-${heir.id}`), 'after a reload the thread is there').toContainText('Second reply');
});
