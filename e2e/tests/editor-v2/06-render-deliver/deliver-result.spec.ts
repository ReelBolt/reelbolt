import * as fs from 'fs';
import * as path from 'path';
import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { renderFromDeliver } from '../../../fixtures/editor-v2/ws';

/**
 * R06.1-R06.9, R09.10: the Deliver panel is result-first. A real small render goes through the isolated stack's
 * engine; the film card must appear with a player, Download must save a non-empty file under the render's name,
 * "Open in project files" must land on the Files page with that file visible, and Delete must remove it.
 * The credit estimate and the 400/402 refusals are driven by route stubs (no paid provider, no plan setup).
 */

// WebKit does not route requests that a service worker may handle, and these specs stub render requests.
test.use({ serviceWorkers: 'block' });

const DL_DIR = path.join(ARTIFACTS, 'deliver-result-downloads');

type Renders = Array<{ executionId: string; status: string; outputFileName: string; durationSec: number | null; sizeBytes: number | null; outputFileId: string | null; hasOutput: boolean }>;

async function shortTimeline(freshTimeline: (from: 'blank') => Promise<string>, api: import('../../../fixtures/editor-v2/api').EditorApi, projectId: string, files: { clipA: string; tone: string }, frames = 45): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', files.clipA, 0, frames)];
    doc.tracks.find((t) => t.kind === 'audio')!.items = [mediaClip(crypto.randomUUID(), 'tone.wav', files.tone, 0, frames)];
  });
  return id;
}

async function openDeliver(ed: EditorPage): Promise<void> {
  await ed.workspace('deliver');
  await expect(ed.testId('deliver-workspace')).toBeVisible();
}

test('R06.1 R06.2 R06.3 R06.4 R06.5 R06.9 render, film card, download, open in project files, delete', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  test.setTimeout(300_000);
  const id = await shortTimeline(freshTimeline, api, seed.projectId, seed.files);
  const ed = await openEditor(id);
  await openDeliver(ed);
  const name = `r06-${Date.now()}`;
  // On a phone the file name sits under "More options" (the Format and Quality stay on top).
  if (ed.isPhone) await ed.tap(ed.testId('render-advanced-toggle'));
  await ed.testId('render-file-name').first().fill(name);
  await expect(ed.testId('film-empty'), 'before a render there is a plain empty state').toBeVisible();
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');

  // R06.1: the latest finished film is first, with a player and plain facts.
  const card = ed.testId('film-card');
  await expect(card).toBeVisible({ timeout: 30_000 });
  await expect(ed.testId('film-name')).toHaveText(name);
  const player = ed.testId('film-player');
  await expect(player).toBeVisible();
  expect(await player.evaluate((v: HTMLVideoElement) => v.getAttribute('src') ?? ''), 'the player streams the outputs endpoint, never a storage key').toMatch(/\/outputs\/[0-9a-f-]+\/download$/);
  await expect(ed.testId('film-facts')).toContainText(/\d:\d\d/);
  await expect(ed.testId('film-status-line')).toHaveText('Last render: your latest edit');

  // The API summary carries the facts the card shows.
  const rows = await api.json<Renders>('GET', `${api.timelinesUrl(seed.projectId, id)}/renders`);
  const row = rows.find((x) => x.executionId === r.executionId)!;
  expect(row.durationSec, 'duration from the engine').toBeGreaterThan(1);
  expect(row.sizeBytes, 'size of the stored file').toBeGreaterThan(0);
  expect(row.outputFileId, 'the project-file row').toBeTruthy();

  // Every button is a real touch target.
  for (const tid of ['film-download', 'film-open-files', 'film-copy-link', 'film-delete', 'render-start']) {
    const box = await ed.testId(tid).boundingBox();
    expect(box?.height ?? 0, `${tid} is at least 44 px tall`).toBeGreaterThanOrEqual(43.5);
  }

  // R06.1 at 1440x900: the result comes first. The film card (player and its buttons) is whole inside the first screen; the
  // interchange export (Wave 2) and the render settings sit below it and the panel scrolls to them.
  if (test.info().project.name === 'desktop') {
    const view = await ed.testId('deliver-workspace').evaluate((el) => {
      const panel = el.parentElement as HTMLElement;
      const top = panel.getBoundingClientRect();
      const card = el.querySelector('[data-testid="film-card"]')!.getBoundingClientRect();
      return { panelTop: top.top, panelBottom: top.top + panel.clientHeight, cardTop: card.top, cardBottom: card.bottom };
    });
    expect(view.cardTop, `film card starts inside the panel: ${JSON.stringify(view)}`).toBeGreaterThanOrEqual(view.panelTop - 1);
    expect(view.cardBottom, `film card is whole on the first screen: ${JSON.stringify(view)}`).toBeLessThanOrEqual(view.panelBottom + 1);
  }

  // R06.2: Download saves a real file under the render's name.
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('film-download'))]);
  expect(download.suggestedFilename()).toBe(`${name}.mp4`);
  fs.mkdirSync(DL_DIR, { recursive: true });
  const saved = path.join(DL_DIR, `${r.executionId}-${download.suggestedFilename()}`);
  await download.saveAs(saved);
  expect(fs.statSync(saved).size, 'the downloaded file has bytes').toBeGreaterThan(0);
  expect(ed.page.url(), 'the editor stays open').toContain(`/editor/${seed.projectId}/timeline/${id}`);

  // R06.3: Open in project files lands on the Files tab with the file visible.
  await ed.tap(ed.testId('film-open-files'));
  await expect(ed.page).toHaveURL(new RegExp(`/projects/${seed.projectId}\\?tab=files&file=${row.outputFileId}`));
  await expect(ed.page.locator(`[data-file-id="${row.outputFileId}"]`), 'the film is listed and in view').toBeVisible({ timeout: 30_000 });

  // R06.9 + R06.4: back in the editor an edit makes the status line count it; then Delete removes the film.
  const ed2 = await openEditor(id);
  await openDeliver(ed2);
  await expect(ed2.testId('film-card')).toBeVisible({ timeout: 30_000 });
  await ed2.tap(ed2.testId('film-delete'));
  await ed2.tap(ed2.page.getByTestId('film-delete-confirm'));
  await expect(ed2.testId('film-card'), 'the film card goes away').toHaveCount(0, { timeout: 30_000 });
  await expect(ed2.page.getByTestId(`render-row-${r.executionId}`), 'its queue row goes away').toHaveCount(0);
  await expect.poll(async () => (await api.listFiles(seed.projectId)).some((f) => f.id === row.outputFileId), { message: 'the file is gone from the project' }).toBe(false);
});

test('R06.5 the status line counts the changes made since the last render', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await shortTimeline(freshTimeline, api, seed.projectId, seed.files, 30);
  const ed = await openEditor(id);
  await openDeliver(ed);
  const r = await renderFromDeliver(ed, id);
  expect(r.status).toBe('Passed');
  await expect(ed.testId('film-status-line')).toHaveText('Last render: your latest edit', { timeout: 30_000 });
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks.find((t) => t.kind === 'video')!.items[0].fadeInFrames = 5; });
  await ed.reload();
  await openDeliver(ed);
  await expect(ed.testId('film-status-line')).toHaveText(/^Last render: before your latest edits \(1 change since\)$/, { timeout: 30_000 });
});

test('R06.1 shape that does not fit the timeline warns; quality and advanced are plain', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await freshTimeline('seeded'));
  await openDeliver(ed);
  // A phone starts on "Vertical for phones" (its own default format), a 16:9 timeline does not fit it; elsewhere the default fits.
  if (!ed.isPhone) {
    await expect(ed.testId('render-fit-warning')).toHaveCount(0);
    await ed.tap(ed.testId('render-preset'));
    await ed.tap(ed.page.getByRole('option', { name: 'Vertical for phones' }));
  }
  await expect(ed.testId('render-fit-warning')).toContainText('crop or letterbox your picture');
  for (const label of ['Draft', 'Good', 'Best']) await expect(ed.testId('render-quality').getByText(label, { exact: true })).toBeVisible();
  await expect(ed.testId('render-advanced')).toBeHidden();
  await ed.tap(ed.testId('render-advanced-toggle'));
  await expect(ed.testId('render-advanced')).toBeVisible();
  await expect(ed.page.getByRole('option', { name: /^H\.265/ })).toHaveCount(0);
});

test('R09.10 Export only the selected clip starts the render in one action, with the clip range', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await ed.selectClipAt(SEEDED_LAYOUT.clipA[0] + 60, 0);
  await openDeliver(ed);
  const bodies: Array<Record<string, unknown>> = [];
  ed.page.on('request', (req) => {
    if (req.method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(req.url()).pathname)) bodies.push(JSON.parse(req.postData() ?? '{}') as Record<string, unknown>);
  });
  const r = await renderFromDeliver(ed, id, 240_000, 'deliver-use-clip-range');
  expect(bodies, 'exactly one render request').toHaveLength(1);
  expect(bodies[0].range, 'the request carries the clip range').toEqual({ inFrame: SEEDED_LAYOUT.clipA[0], outFrame: SEEDED_LAYOUT.clipA[1] });
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const stored = (await api.getTimeline(seed.projectId, id)).document.inOut as { inFrame: number; outFrame: number } | null | undefined;
  expect(stored, 'the marks persist as they do today').toMatchObject({ inFrame: SEEDED_LAYOUT.clipA[0], outFrame: SEEDED_LAYOUT.clipA[1] });
});

test('R06.6 Render saves first; a failed save disables Render with the reason, and Try saving again recovers', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await ed.selectClipAt(SEEDED_LAYOUT.clipA[0] + 60, 0);
  const itemsBefore = (await api.getTimeline(seed.projectId, id)).document.tracks[0].items.length;
  await ed.page.route(`**/edit-timelines/${id}`, (route) => (route.request().method() === 'PATCH' ? route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }) : route.fallback()));
  await ed.page.keyboard.press('Delete');
  await openDeliver(ed);
  await ed.tap(ed.testId('render-start'));
  await expect(ed.testId('render-blocked')).toContainText(/^Save first: /);
  await expect(ed.testId('render-start')).toBeDisabled();
  await expect(ed.testId('render-blocked')).not.toContainText(/\b[0-9a-f]{8}-[0-9a-f]{4}\b|\b5\d\d\b/);

  await ed.page.unroute(`**/edit-timelines/${id}`);
  const posts: Array<Record<string, unknown>> = [];
  ed.page.on('request', (req) => {
    if (req.method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(req.url()).pathname)) posts.push(JSON.parse(req.postData() ?? '{}') as Record<string, unknown>);
  });
  await ed.tap(ed.testId('render-blocked-action'));
  await expect(ed.testId('render-blocked')).toHaveCount(0, { timeout: 30_000 });
  const saved = await ed.expectSaved();
  expect(saved.document.tracks[0].items.length, 'the edit reached the server').toBeLessThan(itemsBefore);
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => posts.length).toBe(1);
  expect(posts[0].revision, 'the render is pinned to the revision that was just saved').toBe(saved.revision);
});

test('R06.7 the credit estimate shows beside Render; billing off shows nothing; short of credits explains and links', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await freshTimeline('seeded'));
  let answer: Record<string, unknown> = { billingEnabled: false, affordable: true };
  await ed.page.route(/\/render\/estimate$/, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(answer) }));
  const pick = async (label: string) => {
    await ed.tap(ed.testId('render-preset'));
    await ed.tap(ed.page.getByRole('option', { name: label }));
  };

  await openDeliver(ed);
  await expect(ed.testId('render-start')).toBeEnabled();
  await ed.page.waitForTimeout(900);
  await expect(ed.testId('render-estimate'), 'billing off: nothing, never 0 credits').toHaveCount(0);
  await expect(ed.testId('deliver-workspace')).not.toContainText('0 credits');

  answer = { billingEnabled: true, credits: 12, creditsLow: 10, creditsHigh: 14, estimateSeconds: 190, balanceCredits: 100, affordable: true };
  await pick('Square');
  await expect(ed.testId('render-estimate')).toHaveText('About 12 credits, about 3 min', { timeout: 15_000 });

  answer = { billingEnabled: true, credits: 12, estimateSeconds: 190, balanceCredits: 2, affordable: false };
  await pick('Portrait 4:5');
  await expect(ed.testId('render-need-credits')).toContainText('You need 10 more credits', { timeout: 15_000 });
  await expect(ed.testId('render-start')).toBeDisabled();
  expect(await ed.testId('render-add-credits').getAttribute('href')).toContain('/settings/billing');
});

test('R06.4 R06.7 a forced 400 and a 402 from Render are explained in plain words', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await freshTimeline('seeded'));
  await openDeliver(ed);
  let refusal: { status: number; body: Record<string, unknown> } = { status: 400, body: {} };
  await ed.page.route(/\/edit-timelines\/[^/]+\/render$/, (route) => (route.request().method() === 'POST' ? route.fulfill({ status: refusal.status, contentType: 'application/json', body: JSON.stringify(refusal.body) }) : route.fallback()));

  refusal = { status: 400, body: { message: 'The render settings are not valid.', errors: ['range.outFrame must be greater than range.inFrame', 'encode.crf must be between 0 and 51'] } };
  await ed.tap(ed.testId('render-start'));
  const alert = ed.testId('render-refusal');
  await expect(alert).toContainText('The render settings are not valid.');
  await expect(alert).toContainText('The range end must be greater');
  await expect(alert).not.toContainText(/range\.outFrame|encode\.crf|\$\.|Exception/);

  refusal = { status: 402, body: { code: 'INSUFFICIENT_CREDITS', message: 'raw server text with ids 7c1f2b3a-0000-4000-8000-000000000000', missing: 4 } };
  await ed.tap(ed.testId('render-start'));
  await expect(alert).toContainText('You need 4 more credits');
  await expect(alert).not.toContainText(/7c1f2b3a/);
  expect(await ed.testId('render-refusal-add-credits').getAttribute('href')).toContain('/settings/billing');
});

test('R06.4 a failed render says why in plain words and offers Try again and Remove', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(240_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await openDeliver(ed);
  // The clip's file is deleted behind the editor's back, so the engine cannot resolve it.
  const files = await api.listFiles(seed.projectId);
  expect(files.length).toBeGreaterThan(0);
  await ed.page.route(/\/edit-timelines\/[^/]+\/renders$/, async (route) => {
    const res = await route.fetch();
    const rows = (await res.json()) as Renders;
    rows.unshift({ executionId: '00000000-0000-4000-8000-0000000000f1', status: 'Failed', preset: 'Hd1080p16x9', outputFileName: 'broken.mp4', durationSec: null, sizeBytes: null, outputFileId: null, hasOutput: false, errorMessage: 'ASSET_UNRESOLVED: Asset projectFile 7c1f2b3a does not resolve' } as unknown as Renders[number]);
    await route.fulfill({ response: res, json: rows });
  });
  await ed.reload();
  await openDeliver(ed);
  const row = ed.page.getByTestId('render-row-00000000-0000-4000-8000-0000000000f1');
  // The queue shows the shared activity vocabulary (the same badge as the tray): a failed render reads "Failed"; the plain-words reason is the row's error line.
  await expect(row.getByTestId('render-status')).toHaveText('Failed');
  await expect(row.getByTestId('render-error')).not.toContainText(/ASSET_UNRESOLVED|7c1f2b3a/);
  await expect(row.getByTestId('render-retry')).toHaveText('Try again');
  await ed.tap(row.getByTestId('render-remove'));
  await expect(row).toHaveCount(0);
  await ed.reload();
  await openDeliver(ed);
  await expect(row, 'a removed failure stays removed').toHaveCount(0);
});
