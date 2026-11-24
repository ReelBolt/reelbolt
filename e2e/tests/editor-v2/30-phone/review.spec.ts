import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import type { Item } from '../../../fixtures/editor-v2/api';
import { uuid } from '../../../fixtures/editor-v2/lane-b';
import { renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 3 / T-009: the phone proposal review sheet (RM.13, R11.3) and the ONE responsive review page
 * (R11.5, VF16; x1 acceptance criterion 9). The AI endpoints are stubbed with page.route (no provider, no
 * credits); renders run through the local ffmpeg. Every outcome is read from real state: the stored document,
 * the comments API, the <video> element's readyState and currentTime.
 */
test.use({ serviceWorkers: 'block' });

const PROPOSALS = /\/edit-timelines\/[^/]+\/proposals(\?.*)?$/;
const ESTIMATE = /\/edit-timelines\/[^/]+\/proposals\/estimate(\?.*)?$/;
const quote = { billingEnabled: true, credits: 2.5, creditsLow: 1, creditsHigh: 2.5, balanceCredits: 50, affordable: true, included: false, modelLabel: 'test-model', used: 8, limit: 20, retryAfterSeconds: null,
  disclosure: { itemCount: 3, rangeSeconds: 6, soundFileCount: 0, briefChars: 0, transcriptSeconds: 0, summary: 'Sent to the model: 3 clips covering 6 seconds. No video or audio.' } };
const titleItem = (id: string, text: string, recordIn: number, recordOut: number): Item => ({
  type: 'titleClip', id, name: text, recordIn, recordOut, text, fontSize: 32, fontFamily: 'Inter', color: '#ffffff', enabled: true, provenance: null,
});

test.describe('RM.13 phone proposal review sheet', () => {
  test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

  async function openAssist(ed: EditorPage, names: [string, string]) {
    const [a, b] = [uuid(), uuid()];
    await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: quote }));
    await ed.page.route(PROPOSALS, (route) =>
      route.request().method() === 'POST'
        ? route.fulfill({ status: 200, contentType: 'application/json', json: { proposalId: 'p9', agent: 'graphics', summary: 'Two titles.', rejected: [], disclosure: quote.disclosure,
            groups: [[a, names[0], 0, 20], [b, names[1], 30, 50]].map(([id, text, i, o]) => ({ label: `Title ${text}`, reason: null, touchesUserOwned: false, touchedItemIds: [id], ops: [{ type: 'addTrack', index: 1, track: { id: `${id}`.replace(/^./, '9'), kind: 'video', name: `Lane ${text}`, height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } }, { type: 'placeOnTop', trackId: `${id}`.replace(/^./, '9'), item: titleItem(id as string, text as string, i as number, o as number) }] })) } })
        : route.continue(),
    );
    await ed.tap(ed.testId('phone-more'));
    await ed.tap(ed.testId('phone-more-assist'));
    await expect(ed.testId('proposal-sheet')).toBeVisible();
    await expect(ed.testId('proposal-estimate'), 'the price shows before asking').toContainText('about 2.5 credits');
  }
  async function setup(freshTimeline: (kind: 'seeded') => Promise<string>, openEditor: (id: string) => Promise<EditorPage>) {
    const id = await freshTimeline('seeded');
    return { id, ed: await openEditor(id) };
  }

  test('R11.3 the price shows before asking, a tapped group is highlighted, Accept applies in ONE undo step', async ({ freshTimeline, openEditor, api, seed }) => {
    const { id, ed } = await setup(freshTimeline, openEditor);
    await openAssist(ed, ['Alpha', 'Bravo']);
    await ed.tap(ed.testId('proposal-ask'));
    await expect(ed.testId('proposal-group-1')).toBeVisible();
    const row = (await ed.testId('proposal-group-focus-0').boundingBox())!;
    expect(row.height, 'a group row is a 44 px target').toBeGreaterThanOrEqual(43.5);
    await ed.tap(ed.testId('proposal-group-focus-1'));
    await expect(ed.testId('proposal-group-1')).toHaveAttribute('data-focused', 'true');
    await ed.tap(ed.testId('proposal-accept-selected'));
    const tl = await ed.expectServer((t) => JSON.stringify(t.document).includes('Alpha'), 'the accepted titles are stored');
    expect(JSON.stringify(tl.document)).toContain('Bravo');
    await expect(ed.testId('undo-snackbar')).toContainText('applied');
    await ed.tap(ed.testId('undo-snackbar-undo'));
    await ed.expectServer((t) => !JSON.stringify(t.document).includes('Alpha') && !JSON.stringify(t.document).includes('Bravo'), 'one Undo removes both');
    expect((await api.getTimeline(seed.projectId, id)).document.tracks.flatMap((t) => t.items).some((i) => i.name === 'Alpha')).toBe(false);
  });

  test('R11.3 Reject leaves the stored document unchanged', async ({ freshTimeline, openEditor, api, seed }) => {
    const { id, ed } = await setup(freshTimeline, openEditor);
    const before = await api.getTimeline(seed.projectId, id);
    await openAssist(ed, ['Alpha', 'Bravo']);
    await ed.tap(ed.testId('proposal-ask'));
    await expect(ed.testId('proposal-group-0')).toBeVisible();
    await ed.tap(ed.testId('proposal-reject'));
    await expect(ed.testId('proposal-group-0')).toHaveCount(0);
    const after = await api.getTimeline(seed.projectId, id);
    expect(after.revision, 'no revision was written').toBe(before.revision);
    expect(JSON.stringify(after.document)).toBe(JSON.stringify(before.document));
  });
});

test.describe('R11.5 / VF16 review page', () => {
  test.beforeEach(() => { runOn('pixel7', 'iphone14webkit', 'desktop'); });
  test.setTimeout(300_000);

  const reviewUrl = (projectId: string, id: string) => `/app/editor/${projectId}/review/${id}`;

  test('R11.5 AC9 the page plays the newest full-timeline render WITH its picture', async ({ freshTimeline, api, seed, page }) => {
    const id = await freshTimeline('seeded');
    const done = await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
    expect(done.status).toBe('Passed');
    await page.goto(reviewUrl(seed.projectId, id));
    const video = page.getByTestId('mobile-review-video');
    await expect(video).toBeVisible();
    await expect(video).toHaveAttribute('src', new RegExp(`/outputs/${done.stepResultId}/download$`));
    await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { message: 'the video loaded', timeout: 30_000 }).toBeGreaterThanOrEqual(2);
    await video.evaluate(async (v: HTMLVideoElement) => { v.muted = true; await v.play(); });
    await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the video advances', timeout: 20_000 }).toBeGreaterThan(0.2);
    expect(await video.evaluate((v: HTMLVideoElement) => v.videoWidth), 'it has a picture').toBeGreaterThan(0);
    await expect(page.getByTestId('review-stale-banner')).toHaveCount(0);
    await expect(page.getByTestId('review-access-line')).toContainText('Anyone in this workspace with access can open it');
  });

  test('R11.5 an edit after the render shows the "Newer edits exist" banner with a link to the editor', async ({ freshTimeline, api, seed, page }) => {
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
    await api.putDocument(seed.projectId, id, (doc) => { doc.name = `${doc.name} edited`; });
    await page.goto(reviewUrl(seed.projectId, id));
    const banner = page.getByTestId('review-stale-banner');
    await expect(banner).toContainText('Newer edits exist');
    await expect(page.getByTestId('review-stale-open-editor')).toHaveAttribute('href', new RegExp(`/editor/${seed.projectId}/timeline/${id}$`));
  });

  test('UI2-01 a comment posted while paused at 2 s stores a timecode near 2 s, not frame 0', async ({ freshTimeline, api, seed, page }) => {
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
    const tl = await api.getTimeline(seed.projectId, id);
    const rate = (tl.document as unknown as { settings: { rate: { num: number; den: number } } }).settings.rate;
    const fps = rate.num / rate.den;
    await page.goto(reviewUrl(seed.projectId, id));
    const video = page.getByTestId('mobile-review-video');
    await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { timeout: 30_000 }).toBeGreaterThanOrEqual(1);
    await video.evaluate((v: HTMLVideoElement) => new Promise<void>((resolve) => { v.pause(); v.addEventListener('seeked', () => resolve(), { once: true }); v.currentTime = 2; }));
    await expect(page.getByTestId('review-pinned-chip')).not.toContainText('00:00:00:00');
    await page.getByTestId('review-body').fill('Fix the cut here');
    await page.getByTestId('review-add').click();
    const url = `${api.timelinesUrl(seed.projectId, id)}/comments`;
    await expect.poll(async () => {
      const list = await api.json<Array<{ body: string; frame: number }>>('GET', url);
      return list.find((c) => c.body === 'Fix the cut here')?.frame ?? -1;
    }, { message: 'the stored frame is the paused frame' }).toBeGreaterThan(0);
    const stored = (await api.json<Array<{ body: string; frame: number }>>('GET', url)).find((c) => c.body === 'Fix the cut here')!;
    expect(Math.abs(stored.frame - Math.round(2 * fps)), 'about 2 s').toBeLessThanOrEqual(2);
  });

  test('R11.5 Copy review link copies the page address and says who can open it', async ({ freshTimeline, api, seed, page }) => {
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
    await page.addInitScript(() => {
      (window as unknown as { __copied: string[] }).__copied = [];
      Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (t: string) => { (window as unknown as { __copied: string[] }).__copied.push(t); } } });
    });
    await page.goto(reviewUrl(seed.projectId, id));
    await page.getByTestId('review-copy-link').click();
    await expect.poll(() => page.evaluate(() => (window as unknown as { __copied: string[] }).__copied.at(-1) ?? '')).toContain(`/editor/${seed.projectId}/review/${id}`);
    await expect(page.getByTestId('review-access-line')).toContainText('Link copied');
  });

  test('VF16 desktop puts the video beside the comments; a phone stacks them', async ({ freshTimeline, api, seed, page }) => {
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
    await page.goto(reviewUrl(seed.projectId, id));
    const video = (await page.getByTestId('mobile-review-video').boundingBox())!;
    const panel = (await page.getByTestId('review-panel').boundingBox())!;
    const wide = (page.viewportSize()?.width ?? 0) >= 992;
    await expect(page.getByTestId('mobile-review')).toHaveAttribute('data-layout', wide ? 'side-by-side' : 'single-column');
    if (wide) expect(panel.x, 'comments sit to the right of the video').toBeGreaterThanOrEqual(video.x + video.width - 1);
    else expect(panel.y, 'comments sit under the video').toBeGreaterThanOrEqual(video.y + video.height - 1);
  });

  test('R11.5 phone comment actions: reply and resolve are stored', async ({ freshTimeline, api, seed, page }) => {
    const id = await freshTimeline('seeded');
    await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
    const url = `${api.timelinesUrl(seed.projectId, id)}/comments`;
    await api.json('POST', url, { body: 'first note', frame: 30, durationFrames: null }, [200, 201]);
    await page.goto(reviewUrl(seed.projectId, id));
    const list = () => api.json<Array<{ id: string; body: string; parentId: string | null; resolved: boolean }>>('GET', url);
    const root = (await list())[0].id;
    await page.getByTestId(`review-reply-${root}`).click();
    await page.getByTestId(`review-reply-body-${root}`).fill('on it');
    await page.getByTestId(`review-reply-send-${root}`).click();
    await expect.poll(async () => (await list()).some((c) => c.body === 'on it' && c.parentId === root)).toBe(true);
    await page.getByTestId(`review-resolve-${root}`).click();
    await expect.poll(async () => (await list()).find((c) => c.id === root)?.resolved).toBe(true);
  });
});
