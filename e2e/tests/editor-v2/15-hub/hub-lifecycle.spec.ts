import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';
import type { EditorApi, Timeline } from '../../../fixtures/editor-v2/api';
import type { Page } from '@playwright/test';

/**
 * Wave 1 hub (T-007): timeline lifecycle (R01.1 / R04.3), creation pickers (R01.3), timecode entry (R01.5).
 * Every row asserts what the SERVER stored (document, list, trash), not that a control exists. Each test makes its own
 * temporary project so the lists it counts are its own.
 */

const hubUrl = (projectId: string) => `/app/editor?project=${projectId}`;
const names = async (api: EditorApi, projectId: string) => (await api.listTimelines(projectId)).map((t) => t.name);
const trash = (api: EditorApi, projectId: string) =>
  api.json<Array<{ id: string; name: string; deletedAt: string; purgesAt: string }>>('GET', `${api.timelinesUrl(projectId)}?state=deleted`);

async function openMenu(page: Page, id: string, item: string): Promise<void> {
  await page.getByTestId(`timeline-more-${id}`).click();
  await page.getByTestId(`${item}-${id}`).click();
}

async function pick(page: Page, field: string, option: string): Promise<void> {
  await page.getByTestId(field).click();
  await page.getByRole('option', { name: option }).click();
}

test('R01.3 creating with Format Vertical and 24 frames per second stores those settings', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'hub-create', async (project) => {
    await page.goto(hubUrl(project.id));
    await page.getByTestId('timeline-create').first().click();
    await expect(page.getByTestId('timeline-name')).toHaveValue('Untitled video');
    await page.getByTestId('timeline-name').fill('Vertical twenty-four');
    await pick(page, 'timeline-format', 'Vertical 9:16');
    await pick(page, 'timeline-fps', '24 frames per second');
    await page.getByTestId('timeline-create-confirm').click();
    await expect(page).toHaveURL(/\/timeline\//, { timeout: 30_000 });

    let created: Timeline | undefined;
    await expect.poll(async () => {
      const row = (await api.listTimelines(project.id)).find((t) => t.name === 'Vertical twenty-four');
      if (row) created = await api.getTimeline(project.id, row.id);
      return created?.document.settings.width;
    }, { message: 'the new timeline is stored' }).toBe(1080);
    const s = created!.document.settings;
    expect([s.width, s.height]).toEqual([1080, 1920]);
    expect(s.rate).toEqual({ num: 24, den: 1 });
  });
});

test('R01.3 the default name is Untitled video and Create keeps it', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'hub-default-name', async (project) => {
    await page.goto(hubUrl(project.id));
    await page.getByTestId('timeline-create').first().click();
    const name = page.getByTestId('timeline-name');
    await expect(name).toHaveValue('Untitled video');
    await page.getByTestId('timeline-create-confirm').click();
    await expect(page).toHaveURL(/\/timeline\//, { timeout: 30_000 });
    await expect.poll(() => names(api, project.id)).toEqual(['Untitled video']);
    const [row] = await api.listTimelines(project.id);
    const s = (await api.getTimeline(project.id, row.id)).document.settings;
    expect([s.width, s.height, s.rate.num, s.rate.den]).toEqual([1920, 1080, 30, 1]);
  });
});

test('R01.5 typing digits in Start timecode stores that time, and a bad entry reverts with a reason', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'hub-timecode', async (project) => {
    await page.goto(hubUrl(project.id));
    await page.getByTestId('timeline-create').first().click();
    await page.getByTestId('timeline-name').fill('Timecode start');
    await page.getByTestId('timeline-advanced-toggle').click();
    const tc = page.getByTestId('timeline-start-timecode');
    await tc.click();
    await tc.pressSequentially('1215');
    await expect(tc).toHaveValue('00:00:12:15');
    await tc.press('Enter');
    await expect(tc).toHaveValue('00:00:12:15');

    // A frame number the frame rate cannot have: the field goes back to the stored time and says why.
    await tc.click();
    await tc.pressSequentially('0099');
    await tc.press('Enter');
    await expect(tc).toHaveValue('00:00:12:15');
    await expect(page.getByText(/Frames go up to 29/)).toBeVisible();

    // Esc cancels what was typed.
    await tc.click();
    await tc.pressSequentially('5');
    await tc.press('Escape');
    await expect(tc).toHaveValue('00:00:12:15');

    await page.getByTestId('timeline-create-confirm').click();
    await expect(page).toHaveURL(/\/timeline\//, { timeout: 30_000 });
    let startFrames: unknown;
    await expect.poll(async () => {
      const row = (await api.listTimelines(project.id)).find((t) => t.name === 'Timecode start');
      if (row) startFrames = (await api.getTimeline(project.id, row.id)).document.settings.startTimecodeFrames;
      return startFrames;
    }).toBe(12 * 30 + 15);
  });
});

test('R01.1 rename, search and sort work and the sort is remembered', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'hub-find', async (project) => {
    const beach = await api.createTimeline(project.id, 'Beach trip');
    const alpha = await api.createTimeline(project.id, 'alpha cut');
    const zebra = await api.createTimeline(project.id, 'Zebra');
    await page.goto(hubUrl(project.id));
    await expect(page.getByTestId(`timeline-row-${zebra.id}`)).toBeVisible({ timeout: 30_000 });

    // Rename through the row menu: stored on the server.
    await openMenu(page, beach.id, 'timeline-rename');
    await page.getByTestId('timeline-rename-input').fill('Beach holiday');
    await page.getByTestId('timeline-rename-confirm').click();
    await expect.poll(() => names(api, project.id)).toContain('Beach holiday');

    // Search: name contains, any case, instantly.
    await page.getByTestId('timeline-search').fill('ALPH');
    await expect(page.getByTestId(`timeline-row-${alpha.id}`)).toBeVisible();
    await expect(page.getByTestId(`timeline-row-${zebra.id}`)).toHaveCount(0);
    await expect(page.getByTestId(`timeline-row-${beach.id}`)).toHaveCount(0);
    await page.getByTestId('timeline-search').fill('');

    // Sort by name, then reload: the choice stays.
    await pick(page, 'timeline-sort', 'Name');
    const order = () => page.locator('[data-testid^="timeline-row-"]').evaluateAll((els) => els.map((e) => e.getAttribute('data-testid')));
    await expect.poll(order).toEqual([`timeline-row-${alpha.id}`, `timeline-row-${beach.id}`, `timeline-row-${zebra.id}`]);
    await page.reload();
    await expect(page.getByTestId('timeline-sort')).toHaveValue('Name', { timeout: 30_000 });
    await expect.poll(order).toEqual([`timeline-row-${alpha.id}`, `timeline-row-${beach.id}`, `timeline-row-${zebra.id}`]);
  });
});

test('R01.1 the Delete dialog tells the truth, and Delete then Undo brings the timeline back', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'hub-undo', async (project) => {
    const tl = await api.createTimeline(project.id, 'Undo me');
    await page.goto(hubUrl(project.id));
    await openMenu(page, tl.id, 'timeline-delete');
    const text = await page.getByTestId('timeline-delete-text').innerText();
    expect(text).toContain('30 days');
    expect(text).toMatch(/review page and comments stop working/i);
    expect(text).toMatch(/restore/i);
    await page.getByTestId('timeline-delete-confirm').click();

    await expect.poll(() => names(api, project.id), { message: 'the server dropped it from the list' }).not.toContain('Undo me');
    await expect(page.getByTestId(`timeline-row-${tl.id}`)).toHaveCount(0);
    await expect(page.getByTestId('timeline-undo-toast').first()).toContainText('Deleted “Undo me”');
    await page.getByTestId('timeline-undo-delete').first().click();

    await expect.poll(() => names(api, project.id), { message: 'Undo restored it on the server' }).toContain('Undo me');
    await expect(page.getByTestId(`timeline-row-${tl.id}`)).toBeVisible();
    expect(await trash(api, project.id)).toEqual([]);
  });
});

test('R04.3 a deleted timeline sits in Recently deleted with its days left and Restore brings it back', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'hub-trash', async (project) => {
    const keep = await api.createTimeline(project.id, 'Keeper');
    const tl = await api.createTimeline(project.id, 'Trash me');
    await page.goto(hubUrl(project.id));
    await openMenu(page, tl.id, 'timeline-delete');
    await page.getByTestId('timeline-delete-confirm').click();

    await expect.poll(async () => (await trash(api, project.id)).map((t) => t.id), { message: 'the server holds it in the trash' }).toEqual([tl.id]);
    const [row] = await trash(api, project.id);
    expect(Date.parse(row.purgesAt) - Date.parse(row.deletedAt)).toBeGreaterThan(29 * 86_400_000);

    await page.getByTestId('timelines-trash-toggle').click();
    const trashRow = page.getByTestId(`timeline-trash-row-${tl.id}`);
    await expect(trashRow).toContainText('Trash me');
    await expect(trashRow).toContainText(/Deleted today - removed for good in 30 days/);
    await page.getByTestId(`timeline-restore-${tl.id}`).click();

    await expect.poll(() => names(api, project.id), { message: 'restored on the server' }).toEqual(expect.arrayContaining(['Keeper', 'Trash me']));
    await expect(page.getByTestId(`timeline-row-${tl.id}`)).toBeVisible();
    await expect(page.getByTestId('timelines-trash')).toHaveCount(0);
    expect(await trash(api, project.id)).toEqual([]);
    expect(keep.id).toBeTruthy();
  });
});

test('R01.1 the row menu is a fingertip-sized target on a phone', async ({ page }) => {
  runOn('pixel7', 'iphone14webkit');
  const api = lightApi(page);
  await withTempProject(api, 'hub-touch', async (project) => {
    const tl = await api.createTimeline(project.id, 'Tap target');
    await page.goto(hubUrl(project.id));
    const box = await page.getByTestId(`timeline-more-${tl.id}`).boundingBox();
    expect(box, 'menu button box').toBeTruthy();
    expect(box!.width).toBeGreaterThanOrEqual(44);
    expect(box!.height).toBeGreaterThanOrEqual(44);
    await page.getByTestId(`timeline-more-${tl.id}`).click();
    const item = await page.getByTestId(`timeline-delete-${tl.id}`).boundingBox();
    expect(item!.height).toBeGreaterThanOrEqual(44);
  });
});
