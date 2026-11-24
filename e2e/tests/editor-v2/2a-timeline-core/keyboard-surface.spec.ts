import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-007 (§10.3): the keyboard surface. `?` opens the read-only cheatsheet, Mod+K opens the command
 * palette (the same `runCommand` as the keys), and Shift+3 switches page. Chords in the UI come from the
 * live keymap, and the workspace buttons state theirs in `aria-keyshortcuts`.
 *
 * e2e dependencies are not installed in this worktree, so this spec was written to the existing
 * editor-v2 conventions but not run here.
 */

const key = (ed: EditorPage, chord: string) => ed.page.keyboard.press(chord);
const countItems = async (ed: EditorPage): Promise<number> => (await ed.serverTimeline()).document.tracks.reduce((n, t) => n + t.items.length, 0);

test('T-007 ? opens the searchable cheatsheet; Edit shortcuts opens the keymap editor', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  test.setTimeout(120_000);
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());

  await key(ed, 'Shift+Slash');
  const cheatsheet = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(cheatsheet).toBeVisible();
  await expect(cheatsheet.getByTestId('shortcuts-row-pageEdit')).toContainText('Shift+3');
  await expect(cheatsheet.getByTestId('shortcuts-row-commandPalette')).toContainText('Ctrl+K');

  await cheatsheet.getByTestId('shortcuts-search').fill('colour');
  await expect(cheatsheet.getByTestId('shortcuts-row-pageColour')).toBeVisible();
  await expect(cheatsheet.getByTestId('shortcuts-row-pageMedia')).toHaveCount(0);
  await cheatsheet.getByTestId('shortcuts-search').fill('nothing matches this');
  await expect(cheatsheet.getByTestId('shortcuts-none')).toBeVisible();

  await ed.tap(cheatsheet.getByTestId('shortcuts-edit'));
  await expect(ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeVisible();
  await key(ed, 'Escape');
  await expect(ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeHidden();
});

test('T-007 Mod+K runs a page action from the palette; the palette Undo is the same path as Mod+Z', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  test.setTimeout(120_000);
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());

  // Page actions state their live chord on the switcher.
  await expect(ed.testId('workspace-edit')).toHaveAttribute('aria-keyshortcuts', 'Shift+3');

  await key(ed, 'Control+KeyK');
  const palette = ed.page.getByRole('dialog', { name: 'Command palette' });
  await expect(palette).toBeVisible();
  await palette.getByTestId('palette-search').fill('colour');
  await expect(palette.getByTestId('palette-command-pageColour')).toBeVisible();
  await ed.tap(palette.getByTestId('palette-command-pageColour'));
  await expect(palette).toBeHidden();
  await expect(ed.testId('workspace-color')).toHaveAttribute('aria-current', 'page');

  // Shift+3 goes to Edit, one keystroke, no palette.
  await key(ed, 'Shift+3');
  await expect(ed.testId('workspace-edit')).toHaveAttribute('aria-current', 'page');

  // Undo parity: Delete closes the gap; the palette's Undo (runCommand) restores it like Mod+Z.
  await ed.selectClipAt(30);
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  const before = await countItems(ed);
  await key(ed, 'Delete');
  await expect.poll(() => countItems(ed)).toBe(before - 1);
  await key(ed, 'Control+KeyK');
  await palette.getByTestId('palette-search').fill('undo');
  await ed.tap(palette.getByTestId('palette-command-undo'));
  await expect.poll(() => countItems(ed)).toBe(before);
});
