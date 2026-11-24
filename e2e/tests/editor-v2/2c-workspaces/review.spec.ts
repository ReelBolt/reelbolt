import { test, expect } from '../../../fixtures/editor-v2';
import { mediaClip, type EditorApi } from '../../../fixtures/editor-v2/api';
import { renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane C, REVIEW (inventory/workspaces.md 2.8, WS-180..WS-194; timeline.md header entry points WS-036). The comments panel in
 * the editor, the review page, and the header links that lead to them. Multi-user rows (WS-185/186 for a View-only member)
 * need a second account and are not exercised on the single-admin stack.
 */

type Comment = { id: string; parentId: string | null; frame: number; durationFrames: number | null; body: string; resolved?: boolean };
const commentsOf = (api: EditorApi, projectId: string, timelineId: string) => api.json<Comment[]>('GET', `${api.timelinesUrl(projectId, timelineId)}/comments`);

/** WS-180..187, 193: the editor's Comments tab end to end. */
test('WS-181 the Comments tab pins a comment to the playhead or the marked range, resolves, replies, deletes - and never changes the document', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => { doc.inOut = { inFrame: 30, outFrame: 90 }; });
  const ed = await openEditor(id);
  const revision = (await ed.serverTimeline()).revision;
  await ed.openPanelTab('tab-review');
  const panel = ed.testId('review-panel');
  await expect(panel).toBeVisible();

  // WS-180 the placeholder names the playhead timecode.
  await ed.seek(45);
  await expect(ed.testId('review-body')).toHaveAttribute('placeholder', /Comment at 00:00:01:15/);

  // WS-181 a comment pinned to the playhead frame.
  await ed.testId('review-body').fill('check this cut');
  await ed.tap(ed.testId('review-add'));
  const thread = (text: string) => panel.locator('[data-testid^="review-thread-"]').filter({ hasText: text });
  await expect(thread('check this cut'), 'the thread is listed').toBeVisible();
  let list = await commentsOf(api, seed.projectId, id);
  const first = list.find((c) => c.body === 'check this cut')!;
  expect(first.frame).toBe(45);
  expect(first.durationFrames).toBeNull();
  expect((await ed.serverTimeline()).revision, 'a comment is not part of the document').toBe(revision);

  // WS-182 cover the marked range.
  await ed.testId('review-body').fill('this whole stretch');
  await panel.getByLabel('Cover the marked range').check();
  await ed.tap(ed.testId('review-add'));
  await expect(thread('this whole stretch')).toBeVisible();
  list = await commentsOf(api, seed.projectId, id);
  const ranged = list.find((c) => c.body === 'this whole stretch')!;
  expect([ranged.frame, ranged.durationFrames]).toEqual([30, 60]);

  // WS-184 the timecode button moves the playhead.
  await ed.seek(200);
  await ed.tap(ed.testId(`review-seek-${first.id}`));
  await expect(ed.testId('status-timecode')).toHaveText('00:00:01:15');

  // WS-187 reply.
  await ed.tap(ed.testId(`review-reply-${first.id}`));
  await ed.testId(`review-reply-body-${first.id}`).fill('agreed');
  await ed.tap(ed.testId(`review-reply-send-${first.id}`));
  await expect(ed.testId(`review-thread-${first.id}`)).toContainText('agreed');

  // WS-185 resolve hides the thread until "Show resolved" is on; reopen brings it back.
  await ed.testId(`review-thread-${first.id}`).getByRole('button', { name: 'Resolve' }).click();
  await expect(ed.testId(`review-thread-${first.id}`)).toHaveCount(0);
  expect((await commentsOf(api, seed.projectId, id)).find((c) => c.id === first.id)!.resolved).toBe(true);
  await panel.getByLabel('Show resolved').check();
  await expect(ed.testId(`review-thread-${first.id}`)).toContainText('resolved');
  await ed.testId(`review-thread-${first.id}`).getByRole('button', { name: 'Reopen' }).click();
  await expect.poll(async () => (await commentsOf(api, seed.projectId, id)).find((c) => c.id === first.id)!.resolved).toBeFalsy();

  // WS-186 delete.
  await ed.testId(`review-thread-${ranged.id}`).getByRole('button', { name: 'Delete comment' }).click();
  await expect(ed.testId(`review-thread-${ranged.id}`)).toHaveCount(0);
  expect((await commentsOf(api, seed.projectId, id)).some((c) => c.id === ranged.id)).toBe(false);
  await ed.reload();
  await ed.openPanelTab('tab-review');
  await expect(ed.testId(`review-thread-${first.id}`)).toContainText('agreed');
  expect((await ed.serverTimeline()).revision, 'still no revision from comments').toBe(revision);
});

/** WS-191 + WS-190 + WS-188/189/184/192/036: the review page in each render state, the header links to it, and its video. */
test('WS-191 the review page: no render -> alert (comments still work), ranged render -> partial notice, full render -> plays it with a live timecode', async ({ freshTimeline, openEditor, api, seed, page }) => {
  runOn('desktop', 'pixel7');
  test.setTimeout(420_000);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 60)];
  });

  // WS-036 from the editor header: the Review page link (phone: through the More menu), and Export opens Deliver.
  const ed = await openEditor(id);
  if (ed.isPhone) {
    await ed.tap(ed.testId('phone-more'));
    await ed.tap(ed.testId('phone-more-review-page'));
  } else await ed.tap(ed.testId('open-review-page'));
  await expect(page).toHaveURL(new RegExp(`/review/${id}`));
  await expect(page.getByTestId('mobile-review')).toBeVisible({ timeout: 30_000 });

  // WS-191 no render yet.
  await expect(page.getByTestId('mobile-review-no-render')).toBeVisible();
  await page.getByTestId('review-body').fill('no render yet');
  await page.getByTestId('review-add').tap().catch(async () => page.getByTestId('review-add').click());
  await expect(page.getByTestId('review-panel').locator('[data-testid^="review-thread-"]').filter({ hasText: 'no render yet' })).toBeVisible();
  expect((await commentsOf(api, seed.projectId, id)).some((c) => c.body === 'no render yet')).toBe(true);

  // WS-190 a ranged render only: the partial notice.
  await renderAndWait(api, seed.projectId, id, { range: { inFrame: 0, outFrame: 20 } });
  await page.reload();
  await expect(page.getByTestId('mobile-review-video')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('mobile-review-partial')).toBeVisible();

  // WS-188/189/184 the full render plays, the timecode follows a pause, a thread's timecode button seeks the video.
  const full = await renderAndWait(api, seed.projectId, id);
  await page.reload();
  const video = page.getByTestId('mobile-review-video');
  await expect(video).toHaveAttribute('src', new RegExp(`/outputs/${full.stepResultId}/download$`), { timeout: 30_000 });
  await expect(page.getByTestId('mobile-review-partial')).toHaveCount(0);
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState)).toBeGreaterThanOrEqual(1);
  await video.evaluate(async (v: HTMLVideoElement) => { v.pause(); await new Promise<void>((r) => { v.addEventListener('seeked', () => r(), { once: true }); v.currentTime = 1; }); });
  await expect(page.getByTestId('mobile-review-timecode')).toHaveText('00:00:01:00');
  const note = await api.json<Comment>('POST', `${api.timelinesUrl(seed.projectId, id)}/comments`, { body: 'jump here', frame: 15, durationFrames: null }, [200, 201]);
  await page.reload();
  await expect(video).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState)).toBeGreaterThanOrEqual(1);
  await page.getByTestId(`review-seek-${note.id}`).click();
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the video jumped to the comment' }).toBeGreaterThan(0.4);
  expect(await video.evaluate((v: HTMLVideoElement) => v.currentTime)).toBeLessThan(0.6);

  // WS-192 Open in the editor: the one responsive review page (R11.5) offers it on every layout.
  const open = page.getByRole('link', { name: /Open in the editor/i }).first();
  await ed.tap(open);
  await expect(page).toHaveURL(new RegExp(`/timeline/${id}`));
});

/** WS-036 (Export): the header's Export button opens Deliver with the render button. */
test('WS-036 the header Export button opens Deliver', async ({ openEditor, seed }) => {
  runOn('desktop', 'pixel7');
  const ed = await openEditor(seed.blankTimelineId);
  if (ed.isPhone) {
    await ed.tap(ed.testId('phone-export'));
  } else await ed.tap(ed.testId('open-deliver'));
  await expect(ed.testId('render-start')).toBeVisible();
});
