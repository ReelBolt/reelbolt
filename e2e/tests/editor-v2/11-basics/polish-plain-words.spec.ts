import type { Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Item, Track } from '../../../fixtures/editor-v2/api';
import { uuid } from '../../../fixtures/editor-v2/lane-b';

/**
 * T-006 polish: R03.7 (a title says which characters it drops while you type), R01.4 (a refusal on a locked
 * track names the track), VF17 (nothing sits under the notch or the home bar). Each row checks what the person
 * sees AND what the server keeps. Phones reach all three, so all three projects run them.
 */

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

const titleItem = (id: string, text: string): Item => ({
  type: 'titleClip', id, name: text, recordIn: 0, recordOut: 120, text, fontSize: 32, fontFamily: 'Inter', color: '#ffffff', enabled: true, provenance: null,
});

async function selectTitleOnTop(ed: EditorPage): Promise<void> {
  await ed.selectClipAt(30, 0);
  await ed.openInspector();
}

test('R03.7 a character titles do not keep is called out as you type and the stored title is the cleaned one', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  const titleId = uuid();
  await api.putDocument(seed.projectId, id, (doc) => {
    const video = doc.tracks.find((t) => t.kind === 'video')!;
    const titles: Track = { ...video, id: uuid(), name: 'Titles', items: [titleItem(titleId, 'Welcome')] };
    doc.tracks.unshift(titles);
  });
  const ed = await openEditor(id);
  await selectTitleOnTop(ed);
  const field = ed.testId('inspector-text');
  await expect(field, 'the Inspector shows the selected title').toHaveValue('Welcome');
  const note = ed.testId('inspector-text-note');

  await field.fill('Welcome home');
  await expect(note, 'plain text says nothing').toHaveCount(0);

  await field.fill('Welcome 😀 home:');
  await expect(note, 'a plain sentence appears under the field while typing').toHaveText("Some characters aren't allowed in titles and were removed");
  await field.blur();
  await expect(field, 'the field shows the cleaned text').toHaveValue('Welcome  home');
  await ed.expectServer(
    (tl) => tl.document.tracks.flatMap((t) => t.items).some((i) => i.id === titleId && i.text === 'Welcome  home'),
    'the stored title is the cleaned text',
  );
});

async function lockVideoTrack(api: import('../../../fixtures/editor-v2/api').EditorApi, projectId: string, id: string): Promise<void> {
  await api.putDocument(projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.name = 'Interviews';
    v.locked = true;
  });
}

test('R01.4 a refused edit on a locked track names the track, never an id or an op name', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await lockVideoTrack(api, seed.projectId, id);
  const ed = await openEditor(id);
  const before = await ed.serverTimeline();
  await ed.selectClipAt(30, 0);
  await ed.openInspector();
  const name = ed.testId('inspector-name');
  await name.fill('Renamed on a locked track');
  await name.blur();
  const toast = ed.page.getByRole('alert').filter({ hasText: /Edit refused/ }).last();
  await expect(toast, 'an "Edit refused" toast is shown').toBeVisible({ timeout: 10_000 });
  await expect(toast, 'the toast names the track').toContainText('Interviews');
  await expect(toast, 'the toast says why').toContainText(/locked/i);
  const text = ((await toast.textContent()) ?? '').replace(/\s+/g, ' ');
  expect(text, 'no id').not.toMatch(GUID);
  expect(text, 'no op name, JSON or track key').not.toMatch(/\b(setProperty|setTrackProperty|nudge)\b|\{|\}|track-/);
  const first = (tl: Awaited<ReturnType<EditorPage['serverTimeline']>>) => tl.document.tracks.find((t) => t.kind === 'video')!.items[0].name;
  expect(first(await ed.serverTimeline()), 'the locked clip kept its name').toBe(first(before));
});

/** What the page reads as the notch and home-bar insets (the shim stands in for env(safe-area-inset-*)). */
const INSET = { top: 47, right: 21, bottom: 34, left: 19 };

async function emulateInsets(page: Page): Promise<void> {
  await page.addStyleTag({
    content: `html body:has([data-editor-surface]) {
      --rb-safe-top: ${INSET.top}px !important; --rb-safe-right: ${INSET.right}px !important;
      --rb-safe-bottom: ${INSET.bottom}px !important; --rb-safe-left: ${INSET.left}px !important;
    }`,
  });
}

const px = async (page: Page, selector: string, property: string): Promise<number> =>
  page.locator(selector).first().evaluate((el, prop) => parseFloat(getComputedStyle(el).getPropertyValue(prop)), property);

test('VF17 nothing is under the notch or the home bar: the editor asks for the full screen and pads by the insets', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await lockVideoTrack(api, seed.projectId, id);
  const ed = await openEditor(id);
  const page = ed.page;

  expect(await page.locator('meta[name="viewport"]').getAttribute('content'), 'the editor page reports the safe-area insets').toContain('viewport-fit=cover');

  // A toast (here the locked-track refusal) is raised first, with the page as it is; the insets are emulated after.
  await ed.selectClipAt(30, 0);
  await ed.openInspector();
  const name = ed.testId('inspector-name');
  await name.fill('x');
  await name.blur();
  await expect(page.getByRole('alert').filter({ hasText: /Edit refused/ }).last()).toBeVisible({ timeout: 10_000 });

  await emulateInsets(page);
  // Toasts stay inside the corners.
  expect(await px(page, '.mantine-Notifications-root', 'margin-top'), 'toasts clear the notch').toBeGreaterThanOrEqual(INSET.top);
  expect(await px(page, '.mantine-Notifications-root', 'margin-right'), 'toasts clear the right corner').toBeGreaterThanOrEqual(INSET.right);
  const phone = (await page.getByTestId('editor-workspace').getAttribute('data-layout')) === 'phone';

  expect(await px(page, '.mantine-AppShell-header', 'padding-top'), 'the header pads its content below the notch').toBeGreaterThanOrEqual(INSET.top);
  const headerHeight = await px(page, '.mantine-AppShell-header', 'height');
  expect(headerHeight, 'the header grew by the notch').toBeGreaterThanOrEqual(60 + INSET.top);
  // Mantine animates the page padding when the header changes size, so poll until it has settled.
  await expect.poll(() => px(page, '.mantine-AppShell-main', 'padding-top'), { message: 'the page starts below the taller header' }).toBeGreaterThanOrEqual(headerHeight);
  expect(await px(page, '[data-testid="editor-workspace"]', 'padding-left'), 'the workspace clears the left corner').toBeGreaterThanOrEqual(INSET.left);
  expect(await px(page, '[data-testid="editor-workspace"]', 'padding-right'), 'the workspace clears the right corner').toBeGreaterThanOrEqual(INSET.right);
  if (phone) {
    expect(await px(page, '[data-testid="workspace-bar"]', 'padding-bottom'), 'the dock clears the home bar').toBeGreaterThanOrEqual(INSET.bottom);
  } else {
    expect(await px(page, '[data-testid="editor-workspace"]', 'padding-bottom'), 'the status bar clears the home bar').toBeGreaterThanOrEqual(INSET.bottom);
  }
});
