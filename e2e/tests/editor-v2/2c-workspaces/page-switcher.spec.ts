import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-002 (§4.3): at 1280px the page band labels every page, and a switch updates the document title and
 * the page h1 (the screen-reader new-page cue). The phone dock is untouched.
 *
 * e2e dependencies are not installed in this worktree, so this spec was written to the existing
 * editor-v2 conventions but not run here.
 */

const PAGES: readonly [string, string][] = [
  ['media', 'Media'],
  ['cut', 'Cut'],
  ['edit', 'Edit'],
  ['compose', 'Compose'],
  ['color', 'Colour'],
  ['audio', 'Audio'],
  ['deliver', 'Deliver'],
];

test('T-002 at 1280px every page is reachable by its visible name and the title/h1 follow', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  const ed = await openEditor(await freshTimeline('seeded'));
  const film = (await ed.serverTimeline()).document.name;
  const band = ed.testId('page-band');
  await expect(band).toBeVisible();

  // Every label is on screen at 1280 (no width rule hides it) and the active page is announced.
  for (const [id, label] of PAGES) {
    const button = band.getByTestId(`workspace-${id}`);
    await expect(button, `${label} is labelled in the band`).toBeVisible();
    await expect(button).toContainText(label);
  }
  await expect(band.getByTestId('workspace-edit')).toHaveAttribute('aria-current', 'page');

  // A switch updates the h1, the document title, focus and aria-current.
  for (const [id, label] of PAGES) {
    await band.getByTestId(`workspace-${id}`).click();
    await expect(ed.testId('page-heading')).toHaveText(label);
    await expect(ed.testId('page-heading')).toBeFocused();
    await expect(page).toHaveTitle(`${label} · ${film}`);
    await expect(band.getByTestId(`workspace-${id}`)).toHaveAttribute('aria-current', 'page');
  }
});
