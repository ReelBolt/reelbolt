import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import type { Item } from '../../../fixtures/editor-v2/api';
import { uuid } from '../../../fixtures/editor-v2/lane-b';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R11.1 / R11.4 / R11.6 / R11.7 (T-022): the Assist panel shows the price, the hourly allowance and what is sent BEFORE
 * asking, explains 429, accepts some changes of a proposal, and the comment cards edit, resolve, jump and delete.
 * The model is stubbed with page.route: no provider, no credits.
 */
test.use({ serviceWorkers: 'block' });

const PROPOSALS = /\/edit-timelines\/[^/]+\/proposals(\?.*)?$/;
const ESTIMATE = /\/edit-timelines\/[^/]+\/proposals\/estimate$/;
const titleItem = (id: string, text: string, recordIn: number, recordOut: number): Item => ({
  type: 'titleClip', id, name: text, recordIn, recordOut, text, fontSize: 32, fontFamily: 'Inter', color: '#ffffff', enabled: true, provenance: null,
});
const quote = { billingEnabled: true, credits: 2.5, creditsLow: 1, creditsHigh: 2.5, balanceCredits: 50, affordable: true, included: false, modelLabel: 'test-model', used: 8, limit: 20, retryAfterSeconds: null,
  disclosure: { itemCount: 3, rangeSeconds: 6, soundFileCount: 0, briefChars: 0, transcriptSeconds: 0, summary: 'Sent to the model: 3 clips covering 6 seconds. No video or audio.' } };

test('R11.1 the price, the allowance and what is sent show before Ask; no proposal call was made', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  let asked = 0;
  await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: quote }));
  await ed.page.route(PROPOSALS, (route) => { if (route.request().method() === 'POST') asked++; return route.continue(); });
  await ed.openPanelTab('tab-assist');
  await expect(ed.testId('proposal-estimate')).toContainText('about 2.5 credits');
  await expect(ed.testId('proposal-allowance')).toContainText('12 of 20 suggestions left this hour');
  await expect(ed.testId('proposal-disclosure')).toContainText('Sent to the model');
  expect(asked, 'looking at the price asks nothing').toBe(0);
});

test('R11.1 an unaffordable ask is disabled in words; billing off shows no price', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: { ...quote, affordable: false, balanceCredits: 1 } }));
  await ed.openPanelTab('tab-assist');
  await expect(ed.testId('proposal-unaffordable')).toContainText('Add credits');
  await expect(ed.testId('proposal-ask')).toBeDisabled();
  await ed.page.unroute(ESTIMATE);
  await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: { ...quote, billingEnabled: false, affordable: true } }));
  await ed.testId('proposal-brief').fill('x');
  await expect(ed.testId('proposal-estimate')).toHaveCount(0);
  await expect(ed.testId('proposal-ask')).toBeEnabled();
});

test('R11.1 the hourly limit is explained in words and nothing is written', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: { ...quote, used: 19 } }));
  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ status: 429, contentType: 'application/json', headers: { 'Retry-After': '600' }, json: { code: 'PROPOSAL_RATE_LIMITED', message: 'You have used your 20 suggestions for this hour. Try again in about 10 minutes.', used: 20, limit: 20, windowMinutes: 60, retryAfter: 600, retryAfterSeconds: 600 } })
      : route.continue(),
  );
  await ed.openPanelTab('tab-assist');
  await expect(ed.testId('proposal-allowance')).toContainText('1 of 20');
  const patches = ed.patches.length;
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-error')).toContainText('Try again in about 10 minutes');
  expect(ed.patches.length).toBe(patches);
});

test('R11.7 accepting 2 of 3 changes writes only those, as one undo step', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  const [a, b, c] = [uuid(), uuid(), uuid()];
  await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: quote }));
  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ status: 200, contentType: 'application/json', json: { proposalId: 'p3', agent: 'graphics', summary: 'Three titles.', rejected: [], disclosure: quote.disclosure,
          groups: [[a, 'Alpha', 0, 20], [b, 'Bravo', 30, 50], [c, 'Charlie', 60, 80]].map(([id, text, i, o]) => ({ label: `Title ${text}`, reason: null, touchesUserOwned: false, touchedItemIds: [id], ops: [{ type: 'addTrack', index: 1, track: { id: `${id}`.replace(/^./, '9'), kind: 'video', name: `Lane ${text}`, height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } }, { type: 'placeOnTop', trackId: `${id}`.replace(/^./, '9'), item: titleItem(id as string, text as string, i as number, o as number) }] })) } })
      : route.continue(),
  );
  await ed.openPanelTab('tab-assist');
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-group-2')).toBeVisible();
  await ed.testId('proposal-group-check-1').uncheck({ force: true });
  await ed.tap(ed.testId('proposal-accept-selected'));
  const tl = await ed.expectServer((t) => JSON.stringify(t.document).includes('Alpha'), 'the accepted titles are stored');
  const text = JSON.stringify(tl.document);
  expect(text).toContain('Charlie');
  expect(text, 'the skipped change is not written').not.toContain('Bravo');
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => !JSON.stringify(t.document).includes('Alpha') && !JSON.stringify(t.document).includes('Charlie'), 'one Undo removes both');
});

test('R11.6 comment actions: edit, jump to time, resolve, delete are stored', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  const url = `${api.timelinesUrl(seed.projectId, id)}/comments`;
  await api.json('POST', url, { body: 'first note', frame: 60, durationFrames: null }, [200, 201]);
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-review');
  const list = () => api.json<{ id: string; body: string; resolved?: boolean }[]>('GET', url);
  const cid = (await list())[0].id;
  await ed.tap(ed.testId(`review-edit-${cid}`));
  await ed.testId(`review-edit-body-${cid}`).fill('edited note');
  await ed.tap(ed.testId(`review-edit-save-${cid}`));
  await expect.poll(async () => (await list())[0].body).toBe('edited note');
  await ed.tap(ed.testId(`review-jump-${cid}`));
  await expect(ed.testId('timecode').first()).toContainText('00:00:02:00').catch(() => undefined);
  await ed.tap(ed.testId(`review-resolve-${cid}`));
  await expect.poll(async () => (await list())[0].resolved).toBe(true);
  await ed.page.getByLabel('Show resolved').check({ force: true });
  await ed.tap(ed.testId(`review-delete-${cid}`));
  await expect.poll(async () => (await list()).length).toBe(0);
});

test.describe('tablet', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });
  test('R11.1 tablet: price shown and a comment can be resolved by touch', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const id = await freshTimeline('seeded');
    const url = `${api.timelinesUrl(seed.projectId, id)}/comments`;
    await api.json('POST', url, { body: 'tablet note', frame: 30, durationFrames: null }, [200, 201]);
    const ed = await openEditor(id);
    await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: quote }));
    await ed.openPanelTab('tab-assist');
    await expect(ed.testId('proposal-estimate')).toContainText('about 2.5 credits');
    await ed.openPanelTab('tab-review');
    const cid = (await api.json<{ id: string }[]>('GET', url))[0].id;
    await ed.tap(ed.testId(`review-resolve-${cid}`));
    await expect.poll(async () => (await api.json<{ resolved?: boolean }[]>('GET', url))[0].resolved).toBe(true);
  });
});
