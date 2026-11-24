import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';

/**
 * color-tools (T-012): Fix exposure on the phone "Look and fix" sheet (R10.6). The button is a 44 px
 * touch target, measuring shows its progress, and the outcome reads back from the SAVED document: the
 * result line and the node list must agree (a fix line and one `Auto fix` node, or the "looks fine"
 * line and no node at all).
 *
 * The suite has no local read-only fixture (the read-only seed is a deployed-box mode, `EDITOR_E2E_READONLY`),
 * so the disabled state is exercised where it is reachable: the button is enabled here.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7'); });

type Clip = Item & { grade?: { nodes?: { name?: string }[] } | null };
const clipNamed = (tl: Timeline, name: string) => itemNamed(tl, name) as Clip | undefined;
const autoFixNodes = (tl: Timeline, name: string) => (clipNamed(tl, name)?.grade?.nodes ?? []).filter((n) => n.name === 'Auto fix');

test('the Look and fix sheet Fix exposure button is 44 px and writes one Auto fix node', async ({ freshTimeline, openEditor, api, seed }) => {
  test.setTimeout(180_000);
  const ed: EditorPage = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.selectClipAt(30);
  await ed.tap(ed.testId('dock-look'));
  await expect(ed.testId('look-sheet')).toBeVisible();

  const run = ed.testId('fix-exposure-run');
  await run.scrollIntoViewIfNeeded();
  await expect(run, 'the sheet offers Fix exposure').toBeVisible();
  expect((await run.boundingBox())!.height, 'the button is a 44 px touch target').toBeGreaterThanOrEqual(44);

  await ed.tap(run);
  await expect(ed.testId('fix-exposure-progress'), 'the measuring progress appears').toBeVisible({ timeout: 10_000 });
  await expect(ed.testId('fix-exposure-result'), 'the result line appears').toBeVisible({ timeout: 90_000 });
  const line = (await ed.testId('fix-exposure-result').textContent()) ?? '';

  if (/looks fine/i.test(line)) {
    await ed.expectServer((t) => autoFixNodes(t, 'clipA.mp4').length === 0, 'the looks-fine line and no Auto fix node');
  } else {
    const tl = await ed.expectServer((t) => autoFixNodes(t, 'clipA.mp4').length === 1, 'a fix line and exactly one Auto fix node');
    expect(autoFixNodes(tl, 'clipA.mp4')[0].name, 'the node is named Auto fix').toBe('Auto fix');
  }
});
