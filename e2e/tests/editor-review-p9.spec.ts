import { expect, test } from '@playwright/test';
import type { Route } from '@playwright/test';

/**
 * Wave H / P9 — the browser assertion for the read-only review page on a phone-sized viewport: the
 * timeline's name, the statement that no render exists yet, an existing timecoded comment, and a new
 * comment posted at the current frame (the POST body is asserted).
 *
 * Stated limit, as for every editor spec: `e2e/` has no CI job and `/api/v1/**` is STUBBED with
 * hand-written payloads in the `EditTimelineCommentResponse` / `EditTimelineResponse` shapes that
 * `EditorP9BackendTests` pins on the server. This is the rendering half, advisory until someone runs it.
 */

const BASE_URL = process.env.BASE_URL || 'http://localhost';
const PROJECT_ID = '11111111-1111-1111-1111-111111111111';
const TIMELINE_ID = '77777777-7777-7777-7777-777777777777';

const timeline = {
  id: TIMELINE_ID,
  projectId: PROJECT_ID,
  name: 'P9 review fixture',
  revision: 4,
  seedStepResultId: null,
  createdAt: '2026-10-01T10:00:00Z',
  updatedAt: '2026-10-01T10:00:00Z',
  document: {
    schemaVersion: 2,
    id: TIMELINE_ID,
    projectId: PROJECT_ID,
    name: 'P9 review fixture',
    settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
    tracks: [],
    markers: [],
    inOut: null,
    seed: null,
  },
};

test.describe('P9 mobile review page (stubbed API)', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('shows the thread and posts a comment at the current frame', async ({ page }) => {
    const posted: unknown[] = [];
    const comments = [{ id: 'c1', timelineId: TIMELINE_ID, parentId: null, authorUserId: 'u1', authorName: 'ana@example.com', frame: 45, durationFrames: null, body: 'Tighten the intro', resolved: false, createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z' }];
    await page.route('**/api/v1/**', async (route: Route) => {
      const request = route.request();
      const pathname = new URL(request.url()).pathname;
      const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
      if (pathname.endsWith(`/edit-timelines/${TIMELINE_ID}/renders`)) return json([]);
      if (pathname.endsWith(`/edit-timelines/${TIMELINE_ID}/comments`)) {
        if (request.method() === 'POST') {
          const body = JSON.parse(request.postData() ?? '{}');
          posted.push(body);
          const created = { ...comments[0], id: 'c2', body: body.body, frame: body.frame };
          comments.push(created);
          return json(created, 201);
        }
        return json(comments);
      }
      if (pathname.endsWith(`/edit-timelines/${TIMELINE_ID}`)) return json(timeline);
      return json({}, 404);
    });
    const user = encodeURIComponent(JSON.stringify({ email: 'qa@example.com', isAdmin: false, mustChangePassword: false }));
    await page.context().addCookies([
      { name: 'reelbolt_token', value: 'qa-stub-token', url: BASE_URL },
      { name: 'reelbolt_user', value: user, url: BASE_URL },
    ]);
    await page.goto(`${BASE_URL}/app/editor/${PROJECT_ID}/review/${TIMELINE_ID}`);

    await expect(page.getByTestId('mobile-review')).toBeVisible();
    await expect(page.getByText('P9 review fixture')).toBeVisible();
    await expect(page.getByTestId('mobile-review-no-render')).toBeVisible();
    await expect(page.getByTestId('review-thread-c1')).toContainText('Tighten the intro');

    await page.getByTestId('review-body').fill('Logo too small');
    await page.getByTestId('review-add').click();
    await expect.poll(() => posted.length).toBe(1);
    expect(posted[0]).toEqual({ body: 'Logo too small', frame: 0, durationFrames: null });
  });
});
