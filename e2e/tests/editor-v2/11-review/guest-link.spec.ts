import { test, expect } from '../../../fixtures/editor-v2';
import { renderAndWait } from '../../../fixtures/editor-v2/rows';
import { ignoreHTTPSErrorsFor } from '../../../fixtures/editor-v2/env';

/**
 * Wave 4 / T-012: guest review links. PENDING RUN (needs the real stack and a finished render; the lead owns the
 * e2e slot). The guest runs in a SECOND browser context with no storageState at all: no cookies, no session.
 * Outcomes are read from real state: the member review page, the owner list API, the media bytes.
 */
test.use({ serviceWorkers: 'block' });


type Created = { id: string; url: string };

async function shareFullRender(api: import('../../../fixtures/editor-v2/api').EditorApi, projectId: string, timelineId: string, body: Record<string, unknown> = {}): Promise<Created> {
  // The link pins a FULL render; the short range render above is a range render, so render the whole timeline.
  await renderAndWait(api, projectId, timelineId, { quality: 'draft' });
  return api.json<Created>('POST', `${api.timelinesUrl(projectId, timelineId)}/review-links`, { label: 'GL spec', expiresInDays: 1, ...body }, [201]);
}

/** A browser context that has never signed in. */
async function guestContext(browser: import('@playwright/test').Browser, baseURL: string, device: Record<string, unknown> = {}) {
  return browser.newContext({ baseURL, storageState: { cookies: [], origins: [] }, ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL), serviceWorkers: 'block', ...device });
}

const FAILURE = 'This link has expired or was turned off.';

test.describe('GL guest review links', () => {
  test('GL create a link, open it in a fresh context with no cookies, watch, comment and approve', async ({ freshTimeline, api, seed, browser, baseURL, page }) => {
    test.setTimeout(420_000);
    const id = await freshTimeline('seeded');
    const link = await shareFullRender(api, seed.projectId, id);
    expect(link.url, 'the create response carries the one-time URL').toMatch(/\/app\/review\/g\/[A-Za-z0-9_-]{43}$/);

    // The owner list never contains the URL or the token.
    const list = await api.json<{ links: Array<Record<string, unknown>> }>('GET', `${api.timelinesUrl(seed.projectId, id)}/review-links`);
    expect(JSON.stringify(list)).not.toContain(link.url.split('/').pop()!);
    expect(JSON.stringify(list)).not.toMatch(/https?:\/\//);

    const guest = await guestContext(browser, baseURL!);
    const gp = await guest.newPage();
    expect(await guest.cookies(), 'the guest has no cookies').toHaveLength(0);
    await gp.goto(new URL(link.url).pathname);
    await expect(gp.getByTestId('guest-title')).toBeVisible();
    await expect(gp.getByTestId('guest-video')).toBeVisible();
    // Watch: the media route streams real bytes.
    const bytes = await gp.evaluate(async () => {
      const token = location.pathname.split('/').pop();
      const res = await fetch(`/api/v1/review-links/${token}/media`, { credentials: 'omit', headers: { Range: 'bytes=0-1023' } });
      return { status: res.status, size: (await res.arrayBuffer()).byteLength };
    });
    expect(bytes.status).toBe(206);
    expect(bytes.size).toBeGreaterThan(0);

    await gp.getByTestId('guest-name').fill('Ana');
    await gp.getByTestId('guest-comment-body').fill('The logo is too small');
    await gp.getByTestId('guest-comment-send').click();
    await expect(gp.getByTestId('guest-threads')).toContainText('The logo is too small');
    await gp.getByTestId('guest-approve').click();
    await expect(gp.getByTestId('guest-decision-banner')).toContainText('Approved by Ana');
    await guest.close();

    // The member sees both on the review page.
    await page.goto(`/app/editor/${seed.projectId}/review/${id}`);
    await expect(page.getByTestId('review-panel')).toContainText('The logo is too small');
    await expect(page.getByTestId('review-panel')).toContainText('Ana (guest)');
    await expect(page.getByTestId(`guest-decision-${link.id}`)).toContainText('Approved by Ana');
    // Stored comments carry the guest flag.
    const comments = await api.json<Array<{ guest?: boolean; guestName?: string | null; body: string }>>('GET', `${api.timelinesUrl(seed.projectId, id)}/comments`);
    expect(comments.some((c) => c.guest === true && c.guestName === 'Ana' && c.body === 'The logo is too small')).toBe(true);
  });

  test('GL a revoked link shows the single failure message', async ({ freshTimeline, api, seed, browser, baseURL }) => {
    test.setTimeout(420_000);
    const id = await freshTimeline('seeded');
    const link = await shareFullRender(api, seed.projectId, id);
    await api.json('DELETE', `${api.timelinesUrl(seed.projectId, id)}/review-links/${link.id}`, undefined, [204]);
    const guest = await guestContext(browser, baseURL!);
    const gp = await guest.newPage();
    await gp.goto(new URL(link.url).pathname);
    await expect(gp.getByTestId('guest-failure')).toHaveText(FAILURE);
    // A made-up token looks identical.
    await gp.goto(`/app/review/g/${'a'.repeat(43)}`);
    await expect(gp.getByTestId('guest-failure')).toHaveText(FAILURE);
    await guest.close();
  });

  test('GL an expired link shows the same single failure message (the API answers its uniform 404, stubbed here)', async ({ browser, baseURL }) => {
    // The API's expiry itself is covered by its xUnit tests; this checks what a guest SEES for any 404.
    const guest = await guestContext(browser, baseURL!);
    const gp = await guest.newPage();
    await gp.route('**/api/v1/review-links/**', (route) => route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }));
    await gp.goto(`/app/review/g/${'b'.repeat(43)}`);
    await expect(gp.getByTestId('guest-failure')).toHaveText(FAILURE);
    await guest.close();
  });

  test('GL the guest page cannot fetch other media', async ({ freshTimeline, api, seed, browser, baseURL }) => {
    test.setTimeout(420_000);
    const id = await freshTimeline('seeded');
    const link = await shareFullRender(api, seed.projectId, id);
    const token = link.url.split('/').pop()!;
    const renders = await api.json<Array<{ stepResultId: string | null; outputFileId?: string | null }>>('GET', `${api.timelinesUrl(seed.projectId, id)}/renders`);
    const guest = await guestContext(browser, baseURL!);
    const gp = await guest.newPage();
    await gp.goto(new URL(link.url).pathname);
    const probes = [
      ...renders.flatMap((r) => [r.stepResultId ? `/api/v1/projects/${seed.projectId}/outputs/${r.stepResultId}/video` : null, r.outputFileId ? `/api/v1/projects/${seed.projectId}/files/${r.outputFileId}/download` : null]),
      `/api/v1/projects/${seed.projectId}/edit-timelines/${id}`,
      `/api/v1/review-links/${token}/../media`,
      `/api/v1/review-links/${token}/files/${seed.files.clipA}`,
    ].filter((u): u is string => !!u);
    for (const url of probes) {
      const status = await gp.evaluate(async (u) => (await fetch(u, { credentials: 'omit' })).status, url);
      expect([401, 403, 404], `${url} must not serve anything to the guest`).toContain(status);
    }
    await guest.close();
  });

  test('GL the guest page fits a phone: no sideways scroll and 44 px controls', async ({ freshTimeline, api, seed, browser, baseURL }, testInfo) => {
    test.skip(testInfo.project.name !== 'pixel7', 'phone viewport check runs on pixel7 only');
    test.setTimeout(420_000);
    const id = await freshTimeline('seeded');
    const link = await shareFullRender(api, seed.projectId, id);
    const guest = await guestContext(browser, baseURL!, { viewport: { width: 360, height: 640 }, hasTouch: true, isMobile: true });
    const gp = await guest.newPage();
    await gp.goto(new URL(link.url).pathname);
    await expect(gp.getByTestId('guest-video')).toBeVisible();
    expect(await gp.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    for (const tid of ['guest-approve', 'guest-request-changes']) {
      const box = (await gp.getByTestId(tid).boundingBox())!;
      expect(box.height, `${tid} is a 44 px control`).toBeGreaterThanOrEqual(43.5);
    }
    await guest.close();
  });
});
