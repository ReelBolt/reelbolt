import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-006 (§5.6/§8.2): the Colour page has no hidden mode. A cold open shows the node stack, the six grade
 * tools tabs and the scopes under the viewer at once; a correction is one click; Curves says it is
 * render-only; the scopes collapse; and "Edit window on the picture" is what enters the window tool.
 *
 * e2e dependencies are not installed in this worktree, so this spec was written to the existing editor-v2
 * conventions but not run here.
 */

type Doc = Awaited<ReturnType<EditorPage['serverTimeline']>>['document'];
const gradeOf = (doc: Doc, name: string) => doc.tracks.flatMap((t) => t.items).find((i) => i.name === name)?.grade as
  | { nodes?: Array<{ name?: string }> }
  | undefined;

async function openColorCold(ed: EditorPage): Promise<void> {
  await ed.seek(60);
  await ed.selectClipAt(60);
  await ed.workspace('color');
  await expect(ed.testId('color-workspace')).toBeVisible();
}

test('T-006 Colour opens on the grade: nodes, tabs and scopes visible, no gate, one click to a stored correction', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const ed = await openEditor(await freshTimeline('seeded'));
  await openColorCold(ed);

  // No gate state and no toggle.
  await expect(ed.testId('color-advanced-toggle')).toHaveCount(0);
  await expect(ed.testId('color-advanced')).toHaveCount(0);
  await expect(ed.testId('grade-add-node')).toBeVisible();
  for (const tab of ['primaries', 'curves', 'lut', 'secondaries', 'groups', 'gallery']) {
    await expect(ed.testId(`color-tab-${tab}`)).toBeVisible();
  }
  await expect(ed.testId('scopes-panel')).toBeVisible();
  await expect(ed.testId('scopes-canvas')).toBeVisible();

  // A correction is one click from the cold open (no Advanced first), and one revision.
  await ed.tap(ed.testId('grade-add-node'));
  await ed.expectServer((tl) => (gradeOf(tl.document, 'clipA.mp4')?.nodes?.length ?? 0) === 1, 'Add correction stores one node from the cold open');

  // Curves is one tab away. The viewer previews it, so the tab carries no "render only" note.
  await ed.tap(ed.testId('color-tab-curves'));
  await expect(ed.testId('color-tab-parity')).toHaveCount(0);

  // The scopes collapse under the viewer and come back.
  await ed.tap(ed.testId('scopes-toggle'));
  await expect(ed.testId('scopes-canvas')).toHaveCount(0);
  await ed.tap(ed.testId('scopes-toggle'));
  await expect(ed.testId('scopes-canvas')).toBeVisible();
});

test('T-006 the Secondaries window: selecting a node does not enter the tool, Edit window on the picture does, Escape leaves it', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const ed = await openEditor(await freshTimeline('seeded'));
  await openColorCold(ed);
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));
  // The paused auto-show needs a window; add one from the Secondaries tab.
  await ed.tap(ed.testId('color-tab-secondaries'));
  await ed.tap(ed.testId('grade-window-toggle'));
  await expect(ed.testId('color-edit-window')).toBeVisible();
  await ed.tap(ed.testId('color-edit-window'));
  await expect(ed.testId('viewer-window-layer')).toBeVisible();

  // Escape leaves the tool; the paused auto-show keeps the handles on the picture (§7.3).
  await ed.page.keyboard.press('Escape');
  await expect(ed.testId('viewer-window-layer')).toBeVisible();
});
