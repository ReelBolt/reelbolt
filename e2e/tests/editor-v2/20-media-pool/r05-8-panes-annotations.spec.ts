import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

const ref = (id: string) => `file:${id}`;
const tile = (ed: EditorPage, mediaRef: string): Locator => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${mediaRef}"]`).first();

async function openPool(ed: EditorPage): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible();
}

test('T-003 the Media page’s bins are the frame’s left zone: it resizes by keyboard and survives a reload', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  // The pool no longer owns its pane widths: on the Media page the bins are the frame's left zone.
  const bins = ed.testId('panel-left');
  const widthOf = async () => Math.round((await bins.boundingBox())!.width);
  const before = await widthOf();
  expect(before).toBe(280);
  const sep = ed.page.getByRole('separator', { name: 'Resize left panel' }).first();
  await sep.focus();
  await ed.page.keyboard.press('ArrowRight');
  await ed.page.keyboard.press('ArrowRight');
  await expect.poll(widthOf).toBe(before + 32);
  await expect(sep).toHaveAttribute('aria-valuenow', String(before + 32));

  await ed.page.reload();
  await openPool(ed);
  await expect.poll(widthOf).toBe(before + 32);
  await ed.page.evaluate(() => window.localStorage.removeItem('reelbolt:editor:layout:v2'));
});

test.describe('R05.8 on a tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });
  test('T-003 the drawer is named Viewer and is a finger-sized target that opens the viewer+metadata', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('blank'));
    await openPool(ed);
    const toggle = ed.testId('inspector-toggle');
    expect(await toggle.textContent()).toBe('Viewer');
    expect((await toggle.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await toggle.tap();
    await expect(ed.testId('panel-right')).toBeVisible();
    await expect(ed.testId('media-detail-pane')).toBeVisible();
  });
});

/** Opens the annotation editor of a clip: a click on the tile on desktop, the tile's menu and "Annotate" in the phone Add media sheet. */
async function openAnnotation(ed: EditorPage, mediaRef: string): Promise<void> {
  if (ed.isPhone) {
    await ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${mediaRef}"]`).first());
    await ed.tap(ed.page.getByTestId('tile-annotate'));
  } else {
    await ed.tap(tile(ed, mediaRef));
  }
  await expect(ed.testId('annotation-editor')).toBeVisible();
}

test('R05.8 annotations save on change with a Saved state and no Save button; the metadata panel hides rows with no value', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const mediaRef = ref(seed.files.clipB);
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  await openAnnotation(ed, mediaRef);
  await expect(ed.testId('annotation-save')).toHaveCount(0);
  const stored = async () =>
    (await seed.api.json<Array<{ mediaRef: string; note?: string; flag?: boolean }>>('GET', `/api/v1/projects/${seed.projectId}/media/annotations`)).find((a) => a.mediaRef === mediaRef);
  try {
    const note = `take-${Date.now()}`;
    await ed.testId('annotation-note').fill(note);
    await expect.poll(async () => (await stored())?.note).toBe(note);
    await expect(ed.testId('annotation-message')).toHaveText('Saved.');
    await ed.page.getByTestId('annotation-flag').check({ force: true });
    await expect.poll(async () => (await stored())?.flag).toBe(true);
    await ed.reload();
    await openPool(ed);
    await openAnnotation(ed, mediaRef);
    await expect(ed.testId('annotation-note')).toHaveValue(note);
    // No metadata row shows "Not probed" or "Not analysed": a row with no value is not drawn.
    const panel = ed.testId('metadata-panel');
    if (await panel.count()) {
      const rows = await panel.locator('[data-testid^="metadata-value-"]').allTextContents();
      for (const text of rows) expect(text).not.toMatch(/Not probed|Not analysed/);
    }
  } finally {
    await seed.api.json('PUT', `/api/v1/projects/${seed.projectId}/media/annotations/${encodeURIComponent(mediaRef)}`, { note: '', flag: false }, [200, 201]);
  }
});
