import { test, expect } from '../../../fixtures/editor-v2';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #4 (report.md §3): on a timeline seeded from a run, Remove silence must read the run's analysis
 * (clipA has silences at 3.0-4.5 s and 8.0-9.5 s). On origin/master the dialog reads the VideoCompile EDL
 * and says "The analysis lists no silence"; on phones the button is hidden below `sm`.
 */
test('TOP10-04 P4-SILENCE-01 Remove silence on a derush timeline finds the analysed silences', async ({ freshTimeline, openEditor }) => {
  knownFailure('TOP10-04');
  const ed = await openEditor(await freshTimeline('derush'));
  await ed.openRemoveSilence();
  // The test id sits on the Mantine Modal root (no box of its own): wait for the dialog itself.
  await expect(ed.page.getByRole('dialog')).toBeVisible();
  await expect(ed.page.getByTestId('remove-silence-none'), 'the dialog does not say the analysis lists no silence').toHaveCount(0);
  await expect(ed.page.getByTestId('remove-silence-apply')).toBeEnabled();
});
