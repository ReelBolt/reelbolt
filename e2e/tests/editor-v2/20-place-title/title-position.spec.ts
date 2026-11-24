import { test, expect } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Editor v2 Wave 2 leftovers (T-036): R10.5 title position pad. Every assertion reads the stored document.
 */

const titles = (tl: Timeline) => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'titleClip') as Array<{ position?: string | null; template?: string | null }>;

test('R10.5 the title dialog stores the chosen position; Template default stores none', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.tap(ed.testId('add-menu'));
  await ed.tap(ed.testId('add-title-options'));
  await expect(ed.testId('add-title-dialog')).toBeVisible();
  await ed.tap(ed.testId('title-position-topRight'));
  await expect(ed.testId('title-position-topRight')).toHaveAttribute('aria-checked', 'true');
  await ed.tap(ed.testId('title-add'));
  await ed.expectServer((t) => titles(t).some((i) => i.position === 'topRight'), 'a title at the top right is stored');

  await ed.seek(200);
  await ed.tap(ed.testId('add-menu'));
  await ed.tap(ed.testId('add-title-options'));
  await ed.tap(ed.testId('title-position-default'));
  await ed.tap(ed.testId('title-add'));
  await ed.expectServer((t) => titles(t).length === 2 && titles(t).some((i) => i.position == null), 'a title with the template default has no position');
});

test('R10.5 the inspector sets the position by click and by keyboard, one undo step each', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.tap(ed.testId('add-menu'));
  await ed.tap(ed.testId('add-title-options'));
  await ed.tap(ed.testId('title-add'));
  await ed.expectServer((t) => titles(t).length === 1, 'a title is stored');
  await ed.openInspector();
  await ed.tap(ed.testId('inspector-title-position-bottomLeft'));
  await ed.expectServer((t) => titles(t)[0]?.position === 'bottomLeft', 'bottom left is stored');
  await ed.testId('inspector-title-position-bottomLeft').focus();
  await ed.page.keyboard.press('ArrowUp');
  await ed.expectServer((t) => titles(t)[0]?.position === 'middleLeft', 'the arrow key moved it to middle left');
  // a focused radio owns its keys (shortcutBlocked), so hand focus back to the page before the global undo chord
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((t) => titles(t)[0]?.position === 'bottomLeft', 'one undo steps back to bottom left');
  await ed.tap(ed.testId('inspector-title-position-default'));
  await ed.expectServer((t) => titles(t)[0]?.position == null, 'Template default clears the position');
});
