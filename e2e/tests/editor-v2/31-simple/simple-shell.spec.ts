import { test, expect, SEEDED_LAYOUT } from '../../../fixtures/editor-v2';

/**
 * The Simple shell (docs/design/editor-simple-mode.md): the same editor laid out for a first video. One viewer, one
 * timeline, three places, one panel that follows the selection; the full editor one click away. Desktop and tablet
 * only: the phone has its own surface and no shell choice.
 */
test.use({ editorShell: 'simple' });

test.describe('the Simple shell', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop' && test.info().project.name !== 'tablet', 'the Simple shell is a desktop and tablet layout');
  });

  test('opens with three places, the task list and a live picture, and none of the seven pages', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    const root = ed.page.getByTestId('editor-workspace');
    await expect(root).toHaveAttribute('data-shell', 'simple');
    await expect(ed.page.getByTestId('simple-place-edit')).toHaveAttribute('aria-current', 'page');
    await expect(ed.page.getByTestId('simple-place-look')).not.toHaveAttribute('aria-current', 'page');
    await expect(ed.page.getByTestId('simple-tasks')).toBeVisible();
    await expect(ed.page.getByTestId('simple-ask')).toBeVisible();
    await expect(ed.page.getByTestId('page-band')).toHaveCount(0);
    await expect(ed.page.getByTestId('timeline-toolbar')).toHaveAttribute('data-simple', 'true');
    // The same preview session as the full editor: the live compositor, not a reduced copy.
    await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live');
    await expect(ed.viewer).toBeVisible();
    // Nothing runs under the side panel: the page never scrolls sideways.
    expect(await ed.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const viewer = await ed.page.getByTestId('viewer-zone').boundingBox();
    const panel = await ed.page.getByTestId('simple-panel').boundingBox();
    expect(viewer!.x + viewer!.width).toBeLessThanOrEqual(panel!.x + 1);
  });

  test('the panel follows the selection; a change is one saved edit and one undo step', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.selectClipAt(SEEDED_LAYOUT.clipA[0] + 30);
    await expect(ed.page.getByTestId('simple-selection')).toHaveAttribute('data-kind', 'clip');
    await expect(ed.page.getByTestId('clip-sheet')).toBeVisible();
    const before = ed.patches.length;
    // Fill on a clip whose picture already matches the frame is a no-op (nothing to save), so the edit is a real change: Rotate 90 degrees.
    await expect(ed.page.getByTestId('clip-rotate-readout')).toHaveText('Upright');
    await ed.page.getByTestId('clip-rotate').click();
    await expect(ed.page.getByTestId('clip-rotate-readout')).toHaveText('90°');
    await ed.expectSaved();
    expect(ed.patches.length).toBeGreaterThan(before);
    await ed.page.getByTestId('undo').click();
    await expect(ed.page.getByTestId('clip-rotate-readout'), 'one undo step takes the rotation back').toHaveText('Upright');
    await ed.expectSaved();
    // Escape clears the selection and the task list comes back.
    await ed.canvas.click({ position: { x: 5, y: 5 } });
    await ed.page.keyboard.press('Escape');
    await expect(ed.page.getByTestId('simple-tasks')).toBeVisible();
  });

  test('Look and Export are places over the same viewer and timeline', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    const frameBefore = await ed.page.getByTestId('viewer-zone').boundingBox();
    await ed.page.getByTestId('simple-place-look').click();
    await expect(ed.page.getByTestId('look-sheet')).toBeVisible();
    await ed.page.getByTestId('look-warm').click();
    await ed.expectSaved();
    await ed.page.getByTestId('open-deliver').click();
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-place', 'export');
    await expect(ed.page.getByTestId('deliver-workspace')).toHaveAttribute('data-simple', 'true');
    await expect(ed.page.getByTestId('render-start')).toBeVisible();
    await expect(ed.page.getByTestId('simple-more-export')).toBeVisible();
    // The viewer did not move or remount between places.
    expect(await ed.page.getByTestId('viewer-zone').boundingBox()).toEqual(frameBefore);
    await expect(ed.canvas).toBeVisible();
  });

  test('the switch opens the full editor on the same timeline and the choice survives a reload', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.selectClipAt(SEEDED_LAYOUT.clipB[0] + 30);
    await ed.page.getByTestId('shell-switch').getByText('Full', { exact: true }).click();
    const root = ed.page.getByTestId('editor-workspace');
    await expect(root).toHaveAttribute('data-shell', 'full');
    await expect(ed.page.getByTestId('inspector-panel')).toBeVisible();
    expect(await ed.page.evaluate(() => window.localStorage.getItem('reelbolt:editor:shell'))).toBe('full');
    await ed.page.reload();
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-shell', 'full', { timeout: 45_000 });
    await ed.page.getByTestId('shell-switch').getByText('Simple', { exact: true }).click();
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-shell', 'simple');
  });

  test('a shortcut for a page the Simple shell does not have offers the full editor instead of jumping there', async ({ freshTimeline, openEditor }) => {
    test.skip(test.info().project.name !== 'desktop', 'keyboard');
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.canvas.click({ position: { x: 5, y: 5 } });
    await ed.page.keyboard.press('Shift+5');
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-shell', 'simple');
    await ed.page.getByTestId('simple-open-full-notice').click();
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-shell', 'full');
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-workspace', 'color');
  });
});
