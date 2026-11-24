import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { EditorApi } from '../../../fixtures/editor-v2/api';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, HUB (inventory/workspaces.md 2.1/2.2, WS-001..WS-036). Hub rows are written against the target shape of the
 * sibling initiative editor-v2-seed-and-hub (tabs Videos + Pictures): the Videos tab is found by role, either label
 * ("Videos" after the sibling, "Timelines" before), and nothing here depends on the Media tab (being removed, not tested).
 */

const VIDEOS_TAB = /^(Videos|Timelines)$/;

/** WS-001: the project select is real navigation: the URL, the list, localStorage and a reload all follow it. */
test('WS-001 choosing another project in the hub switches the lists, the URL and the remembered project', async ({ page, seed }) => {
  const api = lightApi(page);
  await withTempProject(api, 'ws001-other', async (other) => {
    const tl = await api.createTimeline(other.id, 'only-in-other');
    await page.goto(`/app/editor?project=${seed.projectId}`);
    await expect(page.getByTestId(`timeline-open-${seed.seededTimelineId}`)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId(`timeline-open-${tl.id}`), 'the other project’s timeline is not listed here').toHaveCount(0);

    await page.getByTestId('editor-project').click();
    await page.getByRole('option', { name: other.name }).click();
    await expect(page).toHaveURL(new RegExp(`project=${other.id}`));
    await expect(page.getByTestId(`timeline-open-${tl.id}`)).toBeVisible();
    await expect(page.getByTestId(`timeline-open-${seed.seededTimelineId}`), 'project A’s timelines are gone').toHaveCount(0);
    const listed = await api.listTimelines(other.id);
    expect(listed.map((t) => t.id)).toEqual([tl.id]);
    expect(await page.evaluate(() => localStorage.getItem('reelbolt:editor:project'))).toBe(other.id);

    // Reload with no ?project=: the remembered project comes back.
    await page.goto('/app/editor');
    await expect(page.getByTestId(`timeline-open-${tl.id}`)).toBeVisible({ timeout: 30_000 });
  });
});

/** WS-002/003/027/028/032: tabs and the legacy routes all land on a hub view. */
test('WS-002 the hub opens on the Videos list, Pictures is a tab, and the legacy hub routes land on it', async ({ page, seed }) => {
  await page.goto(`/app/editor?project=${seed.projectId}`);
  const videos = page.getByRole('tab', { name: VIDEOS_TAB });
  await expect(videos).toHaveAttribute('aria-selected', 'true');
  expect(new URL(page.url()).searchParams.get('view'), 'the default view is not written to the URL').toBeNull();
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });

  await page.getByRole('tab', { name: 'Pictures' }).click();
  await expect(page).toHaveURL(/view=pictures/);
  await expect(page.getByText('Start a picture')).toBeVisible();

  // WS-027 /editor/{p}/timelines, WS-032 /editor/preview.
  await page.goto(`/app/editor/${seed.projectId}/timelines`);
  await expect(page).toHaveURL(new RegExp(`/app/editor\\?project=${seed.projectId}`));
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });
  await page.goto('/app/editor/preview');
  await expect(page).toHaveURL(/\/app\/editor(\?|$)/);
  await expect(page.getByRole('tab', { name: VIDEOS_TAB })).toBeVisible();

  // WS-028 /editor/{p}/media lands on Videos (the hub is Videos + Pictures; there is no Media tab).
  await page.goto(`/app/editor/${seed.projectId}/media`);
  await expect(page).toHaveURL(/\/app\/editor/);
  await expect(page.getByRole('tab', { name: 'Media' })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: VIDEOS_TAB })).toHaveAttribute('aria-selected', 'true');
});

/** WS-034: the sidebar Editor link leads to the hub (desktop and tablet layouts show the sidebar). */
test('WS-034 the sidebar Editor link opens the hub', async ({ page }) => {
  runOn('desktop');
  await page.goto('/app/dashboard');
  await page.getByRole('link', { name: /^Editor$/ }).first().click();
  await expect(page).toHaveURL(/\/app\/editor/);
  await expect(page.getByRole('tab', { name: VIDEOS_TAB })).toBeVisible();
});

/** WS-005: a user with no project is told to create one (stubbed: the suite account always has projects). */
test('WS-005 with no projects the hub says to create one and links to Projects', async ({ page }) => {
  await page.route('**/api/v1/projects', async (route) => {
    if (route.request().method() === 'GET') return route.fulfill({ json: [] });
    return route.continue();
  });
  await page.goto('/app/editor');
  await expect(page.getByText('Create a project first')).toBeVisible({ timeout: 30_000 });
  await page.getByRole('link', { name: 'Go to projects' }).click();
  await expect(page).toHaveURL(/\/app\/projects/);
});

/** WS-007..012, WS-019: the empty project, the New timeline dialog (Cancel sends nothing), and Create. */
test('WS-012 New timeline: Cancel sends nothing, Create stores a blank timeline with the typed name and opens it', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'ws012', async (project) => {
    await page.goto(`/app/editor?project=${project.id}`);
    // WS-008 + WS-019: the empty state offers New timeline and a link to the workflows.
    await expect(page.getByTestId('timelines-empty')).toBeVisible({ timeout: 30_000 });
    const run = page.getByRole('link', { name: 'Run a workflow' });
    await expect(run).toHaveAttribute('href', new RegExp(`/projects/${project.id}\\?tab=workflows`));

    const posts: string[] = [];
    page.on('request', (r) => { if (r.method() === 'POST' && /\/edit-timelines$/.test(new URL(r.url()).pathname)) posts.push(r.url()); });
    await page.getByTestId('timeline-create').first().click();
    await expect(page.getByRole('dialog', { name: 'New timeline' })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(posts, 'Cancel sent no create request').toHaveLength(0);

    await page.getByTestId('timeline-create').first().click();
    const name = `Typed name ${Date.now()}`;
    await page.getByTestId('timeline-name').fill(name);
    await page.getByTestId('timeline-create-confirm').click();
    await expect(page).toHaveURL(new RegExp(`/app/editor/${project.id}/timeline/[0-9a-f-]{36}`), { timeout: 30_000 });
    const id = page.url().split('/timeline/')[1].split(/[?#]/)[0];
    await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('save-state-chip')).toContainText(/Saved|rev/, { timeout: 30_000 });

    const stored = await api.getTimeline(project.id, id);
    expect(stored.name).toBe(name);
    expect(stored.revision).toBe(1);
    expect(stored.document.tracks.map((t) => t.kind).sort(), 'a blank timeline: one video and one audio track').toEqual(['audio', 'video']);
    expect(stored.document.tracks.every((t) => t.items.length === 0)).toBe(true);

    // WS-036: the header's Back link returns to the hub with the new row listed.
    await page.getByTestId('back-to-timelines').or(page.getByTestId('phone-back')).click(); // the phone header's Back is phone-back
    await expect(page).toHaveURL(/\/app\/editor/);
    await expect(page.getByTestId(`timeline-row-${id}`)).toContainText(name, { timeout: 30_000 });
  });
});

/** WS-013..017: opening a row, the row menu, Duplicate, the review page and Copy review link. */
test('WS-014 the row menu duplicates a timeline, opens its review page and copies the review link', async ({ page, context, seed }) => {
  const browser = test.info().project.name;
  if (browser !== 'iphone14webkit') await context.grantPermissions(['clipboard-read', 'clipboard-write']).catch(() => undefined);
  const api = lightApi(page);
  const sourceId = seed.seededTimelineId;
  const source = await api.getTimeline(seed.projectId, sourceId);
  await page.goto(`/app/editor?project=${seed.projectId}`);
  await expect(page.getByTestId(`timeline-row-${sourceId}`)).toBeVisible({ timeout: 30_000 });

  // WS-013: the row link opens the timeline.
  await page.getByTestId(`timeline-open-${sourceId}`).click();
  await expect(page).toHaveURL(new RegExp(`/timeline/${sourceId}`));
  await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
  await page.goBack();

  // WS-014: the menu lists its items.
  await page.getByTestId(`timeline-more-${sourceId}`).click();
  for (const item of ['Rename', 'Duplicate', 'Open review page', 'Copy review link', 'Delete']) {
    await expect(page.getByRole('menuitem', { name: item })).toBeVisible();
  }

  // WS-015: Duplicate adds "<name> copy" with the same document.
  await page.getByRole('menuitem', { name: 'Duplicate' }).click();
  await expect(page.getByText(`${source.name} copy`).first()).toBeVisible({ timeout: 20_000 });
  const copies = (await api.listTimelines(seed.projectId)).filter((t) => t.name === `${source.name} copy`);
  expect(copies.length).toBeGreaterThan(0);
  const copy = await api.getTimeline(seed.projectId, copies[copies.length - 1].id);
  const strip = (t: typeof source) => JSON.stringify(t.document.tracks.map((tr) => tr.items.map((i) => [i.type, i.recordIn, i.recordOut, i.assetRef?.projectFileId])));
  expect(strip(copy), 'the copy holds the same clips').toBe(strip(source));
  expect((await api.getTimeline(seed.projectId, sourceId)).revision, 'the source is untouched').toBe(source.revision);

  // WS-017: Copy review link (Chromium: read the clipboard back; WebKit: the fallback toast carries the URL).
  await page.getByTestId(`timeline-more-${sourceId}`).click();
  await page.getByRole('menuitem', { name: 'Copy review link' }).click();
  const link = `${new URL(page.url()).origin}/app/editor/${seed.projectId}/review/${sourceId}`;
  if (browser !== 'iphone14webkit') {
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText()), { message: 'the review link is on the clipboard' }).toBe(link);
    await expect(page.getByText('Review link copied.')).toBeVisible();
  } else {
    await expect(page.getByText(/Review link copied|Copy this review link/).first()).toBeVisible();
  }

  // WS-016: Open review page.
  await page.getByTestId(`timeline-more-${sourceId}`).click();
  await page.getByRole('menuitem', { name: 'Open review page' }).click();
  await expect(page).toHaveURL(new RegExp(`/review/${sourceId}`));
  await expect(page.getByTestId('mobile-review')).toBeVisible({ timeout: 30_000 });

  // Clean up the copies this test made.
  for (const c of copies) await api.json('DELETE', `/api/v1/projects/${seed.projectId}/edit-timelines/${c.id}`, undefined, [200, 204, 404]);
});

/** WS-021: rename and delete a timeline from the row menu (the server routes existed; the UI did not). */
test('WS-021 a timeline can be renamed and deleted from the hub, and its review page then 404s', async ({ page, seed }) => {
  const api = lightApi(page);
  const created = await api.createTimeline(seed.projectId, `ws021-${test.info().project.name}-${Date.now()}`);
  const id = created.id;
  await page.goto(`/app/editor?project=${seed.projectId}`);
  const row = page.getByTestId(`timeline-row-${id}`);
  await expect(row).toBeVisible({ timeout: 30_000 });

  await page.getByTestId(`timeline-more-${id}`).click();
  await page.getByTestId(`timeline-rename-${id}`).click();
  const next = `Renamed ${Date.now()}`;
  await page.getByTestId('timeline-rename-input').fill(next);
  await page.getByTestId('timeline-rename-confirm').click();
  await expect(row).toContainText(next, { timeout: 20_000 });
  const renamed = await api.getTimeline(seed.projectId, id);
  expect(renamed.name).toBe(next);
  expect(renamed.revision, 'a rename is row metadata and does not advance the revision').toBe(created.revision);
  await page.reload();
  await expect(page.getByTestId(`timeline-row-${id}`)).toContainText(next, { timeout: 30_000 });

  // Delete: Cancel keeps it, Delete removes it.
  await page.getByTestId(`timeline-more-${id}`).click();
  await page.getByTestId(`timeline-delete-${id}`).click();
  await expect(page.getByTestId('timeline-delete-text')).toContainText(next);
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByTestId(`timeline-row-${id}`)).toBeVisible();
  await page.getByTestId(`timeline-more-${id}`).click();
  await page.getByTestId(`timeline-delete-${id}`).click();
  await page.getByTestId('timeline-delete-confirm').click();
  await expect(page.getByTestId(`timeline-row-${id}`)).toHaveCount(0, { timeout: 20_000 });
  expect((await api.listTimelines(seed.projectId)).some((t) => t.id === id)).toBe(false);
  const gone = await api.request.get(`/api/v1/projects/${seed.projectId}/edit-timelines/${id}`, { failOnStatusCode: false });
  expect(gone.status(), 'a deleted timeline is a 404').toBe(404);

  // WS-194: its review page can no longer be opened.
  await page.goto(`/app/editor/${seed.projectId}/review/${id}`);
  await expect(page.getByText(/could not be opened/i)).toBeVisible({ timeout: 30_000 });
});

/** WS-006 + WS-174 + WS-020: export a timeline as OTIO from Deliver, import the file on the hub: the same document; a foreign file with missing media opens with Offline markers. */
test('WS-174 an OTIO exported from Deliver imports back as the same timeline; a file naming missing media imports with an Offline marker', async ({ page, openEditor, seed }) => {
  runOn('desktop');
  const api = lightApi(page);
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('deliver');
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByTestId('deliver-export-otio').click()]);
  expect(download.suggestedFilename()).toMatch(/\.otio$/);
  const dir = path.join(ARTIFACTS, 'c-downloads');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `roundtrip-${Date.now()}.otio`);
  await download.saveAs(file);
  expect(fs.statSync(file).size, 'the exported file has content').toBeGreaterThan(500);

  const before = (await api.listTimelines(seed.projectId)).map((t) => t.id);
  await page.goto(`/app/editor?project=${seed.projectId}`);
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });
  const [chooser] = await Promise.all([page.waitForEvent('filechooser'), page.getByTestId('timeline-import').click()]);
  await chooser.setFiles(file);
  await expect(page).toHaveURL(new RegExp(`/app/editor/${seed.projectId}/timeline/[0-9a-f-]{36}`), { timeout: 30_000 });
  const importedId = page.url().split('/timeline/')[1].split(/[?#]/)[0];
  expect(before).not.toContain(importedId);
  const src = await api.getTimeline(seed.projectId, seed.seededTimelineId);
  const imp = await api.getTimeline(seed.projectId, importedId);
  const shape = (t: typeof src) => JSON.stringify(t.document.tracks.map((tr) => ({ kind: tr.kind, items: tr.items.map((i) => [i.type, i.recordIn, i.recordOut, i.sourceIn, i.assetRef?.projectFileId]) })));
  expect(shape(imp), 'the imported timeline equals the exported one (ignoring ids, name, revision)').toBe(shape(src));
  await api.json('DELETE', `/api/v1/projects/${seed.projectId}/edit-timelines/${importedId}`, undefined, [200, 204]);

  // WS-006 (refusal) + WS-020: a foreign OTIO whose media is not in this project.
  const foreign = {
    OTIO_SCHEMA: 'Timeline.1', name: 'foreign', tracks: { OTIO_SCHEMA: 'Stack.1', children: [{
      OTIO_SCHEMA: 'Track.1', kind: 'Video', children: [{
        OTIO_SCHEMA: 'Clip.1', name: 'ghost', media_reference: { OTIO_SCHEMA: 'ExternalReference.1', target_url: 'ghost-not-in-project.mp4' },
        source_range: { OTIO_SCHEMA: 'TimeRange.1', start_time: { OTIO_SCHEMA: 'RationalTime.1', value: 0, rate: 30 }, duration: { OTIO_SCHEMA: 'RationalTime.1', value: 30, rate: 30 } },
      }],
    }] },
  };
  const foreignFile = path.join(dir, `foreign-${Date.now()}.otio`);
  fs.writeFileSync(foreignFile, JSON.stringify(foreign));
  await page.goto(`/app/editor?project=${seed.projectId}`);
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });
  const [chooser2] = await Promise.all([page.waitForEvent('filechooser'), page.getByTestId('timeline-import').click()]);
  await chooser2.setFiles(foreignFile);
  // R12.2 (T-019): missing media no longer refuses the import; the timeline opens with an Offline marker and a Relink path.
  await expect(page).toHaveURL(new RegExp(`/app/editor/${seed.projectId}/timeline/[0-9a-f-]{36}`), { timeout: 30_000 });
  await expect(page.getByTestId('offline-message')).toContainText('ghost', { timeout: 45_000 });
  const ghostId = page.url().split('/timeline/')[1].split(/[?#]/)[0];
  await api.json('DELETE', `/api/v1/projects/${seed.projectId}/edit-timelines/${ghostId}`, undefined, [200, 204]);
});


/** WS-022..024: start a picture from a project image; opening it twice reuses the document. */
test('WS-023 Pictures: an image opens in the picture editor and opening it again reuses the same document', async ({ page, seed }) => {
  runOn('desktop', 'pixel7');
  await page.goto(`/app/editor?project=${seed.projectId}&view=pictures`);
  await expect(page.getByText('Start a picture')).toBeVisible({ timeout: 30_000 });
  const picker = page.getByRole('combobox', { name: 'Start from one of your images' });
  await picker.click();
  // WS-022: the options are the project's editable images (still.png is one; the mp4/wav files are not).
  const options = await page.getByRole('option').allTextContents();
  expect(options.some((o) => o.includes('still.png'))).toBe(true);
  expect(options.some((o) => /\.mp4|\.wav/.test(o)), 'only images are offered').toBe(false);
  await page.getByRole('option', { name: /still\.png/ }).click();
  await page.getByRole('button', { name: 'Open in the picture editor' }).click();
  await expect(page).toHaveURL(/\/editor\/thumbnails\/[0-9a-f-]{36}\?project=/, { timeout: 30_000 });
  const first = page.url().match(/thumbnails\/([0-9a-f-]{36})/)![1];

  await page.goto(`/app/editor?project=${seed.projectId}&view=pictures`);
  await page.getByRole('combobox', { name: 'Start from one of your images' }).click();
  await page.getByRole('option', { name: /still\.png/ }).click();
  await page.getByRole('button', { name: 'Open in the picture editor' }).click();
  await expect(page).toHaveURL(/\/editor\/thumbnails\//, { timeout: 30_000 });
  expect(page.url().match(/thumbnails\/([0-9a-f-]{36})/)![1], 'the same document is reused').toBe(first);

  // WS-024: the list row opens that document.
  await page.goto(`/app/editor?project=${seed.projectId}&view=pictures`);
  await page.getByRole('list', { name: 'Your pictures' }).getByRole('link').first().click();
  await expect(page).toHaveURL(/\/editor\/thumbnails\//);
});
