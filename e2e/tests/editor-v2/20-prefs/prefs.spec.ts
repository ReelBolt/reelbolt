import * as fs from 'fs';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { EditorApi } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R12.4 / R12.5 / R12.7 / R12.8 (T-019): preferences are per user (shared by every test), so the specs run one at a time and
 * every test starts from, and leaves, empty preferences. Outcomes are read from the preferences API, never from the dialog.
 */
test.describe.configure({ mode: 'serial' });

type Prefs = { keymap: Record<string, string>; layouts: Array<{ name: string; workspace: string; sizes: Record<string, number>; fractions?: { left: number; right: number; timeline: number } }>; updatedAt: string | null };
const PREFS = '/api/v1/editor/preferences';
const getPrefs = (api: EditorApi) => api.json<Prefs>('GET', PREFS);
const clearPrefs = (api: EditorApi) => api.json('PUT', PREFS, { keymap: {}, layouts: [] });

test.beforeEach(async ({ api }) => { await clearPrefs(api); });
test.afterEach(async ({ api }) => { await clearPrefs(api); });

async function openKeymap(ed: EditorPage): Promise<void> {
  // On a tablet the header keeps Keyboard shortcuts inside its More menu.
  if (!(await ed.testId('open-keymap').isVisible())) await ed.tap(ed.testId('header-more'));
  await ed.tap(ed.testId('open-keymap'));
  // T-007: the header icon opens the read-only cheatsheet; Edit shortcuts opens the editable keymap.
  await ed.tap(ed.page.getByTestId('shortcuts-edit'));
  await expect(ed.testId('keymap-search')).toBeVisible();
}

async function rebind(ed: EditorPage, action: string, key: string): Promise<void> {
  await ed.tap(ed.testId(`keymap-change-${action}`));
  await ed.page.keyboard.press(key);
}

test('R12.5 a rebound shortcut is saved, shows after reload, and really triggers the action', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await openKeymap(ed);
  await ed.testId('keymap-search').fill('add marker');
  await expect(ed.page.locator('[data-testid^="keymap-row-"]')).toHaveCount(1);
  await rebind(ed, 'addMarker', 'Q');
  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(async () => (await getPrefs(api)).keymap.addMarker, { message: 'the stored keymap carries the new key' }).toBe('Q');

  await ed.reload();
  await openKeymap(ed);
  await expect(ed.testId('keymap-row-addMarker')).toContainText('Q');
  await ed.page.keyboard.press('Escape');
  await expect(ed.testId('keymap-search')).toBeHidden();

  await ed.canvas.click({ position: { x: 5, y: 5 } }).catch(() => undefined);
  await ed.page.keyboard.press('Q');
  const tl = await ed.expectServer((t) => (t.document.markers ?? []).length === 1, 'Q added a marker');
  expect(tl.document.markers).toHaveLength(1);
  await ed.page.keyboard.press('m');
  await ed.page.waitForTimeout(600);
  expect((await ed.serverTimeline()).document.markers ?? [], 'M no longer adds one').toHaveLength(1);
  expect(seed.projectId).toBeTruthy();
});

test('R12.5 two actions on one key are warned about, cannot be saved, and per-action and reset-all return the defaults', async ({ openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.seededTimelineId);
  await openKeymap(ed);
  await rebind(ed, 'addMarker', 'V');
  await expect(ed.testId('keymap-conflicts')).toContainText('Add marker');
  await expect(ed.testId('keymap-save')).toBeDisabled();
  await ed.tap(ed.testId('keymap-reset-addMarker'));
  await expect(ed.testId('keymap-conflicts')).toHaveCount(0);
  await rebind(ed, 'addMarker', 'Q');
  await rebind(ed, 'toolHand', 'W');
  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(async () => Object.keys((await getPrefs(api)).keymap).sort()).toEqual(['addMarker', 'toolHand']);
  await openKeymap(ed);
  await ed.tap(ed.testId('keymap-reset-all'));
  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(async () => Object.keys((await getPrefs(api)).keymap)).toEqual([]);
});

test('R12.5 shortcuts export to a JSON file and import from one', async ({ openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.seededTimelineId);
  await openKeymap(ed);
  await rebind(ed, 'toolHand', 'W');
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('keymap-export'))]);
  const file = test.info().outputPath('keys.json');
  await download.saveAs(file);
  expect(fs.statSync(file).size, 'the exported file has content').toBeGreaterThan(20);
  expect(JSON.parse(fs.readFileSync(file, 'utf8')).shortcuts).toEqual({ toolHand: 'W' });
  await ed.tap(ed.testId('keymap-reset-all'));
  await ed.testId('keymap-import-file').setInputFiles(file);
  await expect(ed.testId('keymap-row-toolHand')).toContainText('W');
  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(async () => (await getPrefs(api)).keymap.toolHand).toBe('W');
});

test('R12.4 a failed save says why in plain words and keeps the dialog; Restore previous settings puts the earlier copy back', async ({ openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.seededTimelineId);
  await openKeymap(ed);
  await rebind(ed, 'toolHand', 'W');
  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(async () => (await getPrefs(api)).keymap.toolHand).toBe('W');

  await ed.page.route('**/api/v1/editor/preferences', (route) => (route.request().method() === 'PUT' ? route.fulfill({ status: 500, contentType: 'application/json', body: '{"message":"boom"}' }) : route.continue()));
  await openKeymap(ed);
  await rebind(ed, 'toolHand', 'F7');
  await ed.tap(ed.testId('keymap-save'));
  await expect(ed.testId('keymap-save-error')).toBeVisible();
  await expect(ed.testId('keymap-search'), 'the dialog stays open').toBeVisible();
  expect((await getPrefs(api)).keymap.toolHand, 'nothing was stored').toBe('W');
  await ed.page.unroute('**/api/v1/editor/preferences');

  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(async () => (await getPrefs(api)).keymap.toolHand).toBe('F7');
  await openKeymap(ed);
  await ed.tap(ed.testId('prefs-restore'));
  await expect.poll(async () => (await getPrefs(api)).keymap.toolHand, { message: 'the copy from before the last save is back' }).toBe('W');
});

/** Opens the Layout menu unless it is already open (it stays open after Save and Apply). */
async function openLayouts(ed: EditorPage): Promise<void> {
  if (await ed.page.getByTestId('layout-name').isVisible()) return;
  await ed.tap(ed.testId('layout-menu'));
  await expect(ed.page.getByTestId('layout-name')).toBeVisible();
}

async function leftWidth(ed: EditorPage): Promise<number> {
  return Number(await ed.page.getByRole('separator', { name: 'Resize left panel' }).getAttribute('aria-valuenow'));
}

test('R12.7 a saved layout stores fractions, Reset layout restores the default, applying brings it back and Delete removes it server-side', async ({ openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.seededTimelineId);
  const sep = ed.page.getByRole('separator', { name: 'Resize left panel' });
  const start = await leftWidth(ed);
  await sep.focus();
  await ed.page.keyboard.press('Shift+ArrowRight');
  await expect.poll(() => leftWidth(ed)).toBe(start + 64);

  await openLayouts(ed);
  await ed.testId('layout-name').fill('Wide left');
  await ed.tap(ed.testId('layout-save'));
  const stored = await expect.poll(async () => (await getPrefs(api)).layouts.length).toBe(1).then(() => getPrefs(api));
  const f = stored.layouts[0].fractions!;
  expect(f.left, 'fractions of the window, not pixels').toBeGreaterThan(0);
  expect(f.left).toBeLessThan(1);
  expect(f.timeline).toBeLessThan(1);

  await ed.tap(ed.testId('layout-reset-page'));
  await expect.poll(() => leftWidth(ed), { message: 'Reset layout restores the default' }).toBe(start);
  await ed.reload();
  expect(await leftWidth(ed), 'the default survives a reload').toBe(start);

  await openLayouts(ed);
  await ed.tap(ed.testId('layout-apply-Wide left'));
  await expect.poll(() => leftWidth(ed), { message: 'applying the saved layout brings the wide panel back' }).toBeGreaterThan(start + 40);

  await openLayouts(ed);
  await ed.tap(ed.testId('layout-delete-Wide left'));
  await ed.tap(ed.testId('layout-delete-confirm-Wide left'));
  await expect.poll(async () => (await getPrefs(api)).layouts.length, { message: 'the layout is gone on the server' }).toBe(0);

  // The applied wide panel is stored for this account: put the page back, or every later test starts with a cramped header.
  await openLayouts(ed);
  await ed.tap(ed.testId('layout-reset-page'));
  await expect.poll(() => leftWidth(ed), { message: 'the default is back for the next test' }).toBe(start);
});

const mastering = { targetLufs: -16, truePeakDbtp: -1, loudnessRange: 11 };

test('R12.8 presets: preview, duplicate, rename and delete are stored on the server', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const name = `Podcast mix ${Date.now()}`;
  const base = `/api/v1/projects/${seed.projectId}/edit-timelines/presets`;
  await api.json('POST', base, { kind: 'mastering', name, payload: mastering });
  const list = () => api.json<Array<{ id: string; name: string }>>('GET', `${base}?kind=mastering`);
  try {
    const ed = await openEditor(await freshTimeline('blank'));
    await ed.workspace('deliver');
    await ed.page.getByText('Advanced', { exact: true }).click();
    await ed.page.getByLabel('Normalise loudness').check();
    await ed.tap(ed.testId('mastering-presets-manage'));
    await expect(ed.testId(`mastering-presets-preview-${name}`)).toContainText('-16 LUFS');

    await ed.tap(ed.testId(`mastering-presets-manage-duplicate-${name}`));
    await expect.poll(async () => (await list()).map((p) => p.name)).toContain(`${name} copy`);

    await ed.tap(ed.testId(`mastering-presets-manage-rename-${name} copy`));
    await ed.testId('mastering-presets-rename-input').fill(`${name} renamed`);
    await ed.tap(ed.testId('mastering-presets-rename-confirm'));
    await expect.poll(async () => (await list()).map((p) => p.name)).toContain(`${name} renamed`);
    // rename is a create followed by a delete on the server: the old name leaves a moment after the new one appears
    await expect.poll(async () => (await list()).map((p) => p.name)).not.toContain(`${name} copy`);

    await ed.tap(ed.testId(`mastering-presets-manage-delete-${name} renamed`));
    await ed.tap(ed.testId('mastering-presets-manage-delete-confirm'));
    await expect.poll(async () => (await list()).map((p) => p.name), { message: 'delete removed it server-side' }).not.toContain(`${name} renamed`);
    expect((await list()).map((p) => p.name)).toContain(name);
  } finally {
    for (const p of (await list()).filter((x) => x.name.startsWith(name))) await api.json('DELETE', `${base}/${p.id}`, undefined, [200, 204]);
  }
});

test.describe('tablet (coarse pointer, 1024x768)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });
  test('R12.5 R12.7 tablet: keymap search and export by touch, Reset layout by touch', async ({ openEditor, seed }) => {
    runOn('desktop');
    const ed = await openEditor(seed.seededTimelineId);
    await openKeymap(ed);
    await ed.testId('keymap-search').fill('undo');
    await expect(ed.page.locator('[data-testid^="keymap-row-"]')).not.toHaveCount(0);
    const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('keymap-export'))]);
    const file = test.info().outputPath('tablet-keys.json');
    await download.saveAs(file);
    expect(fs.statSync(file).size).toBeGreaterThan(20);
    await ed.page.keyboard.press('Escape');
    await openLayouts(ed);
    await ed.tap(ed.testId('layout-reset-page'));
    await expect(ed.page.getByRole('separator', { name: 'Resize left panel' })).toBeVisible();
  });
});
