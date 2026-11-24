import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 09 analysis-driven editing: Remove silence (remediation-plan.md W0.5: panels resolve the VideoAnalyze
 * artifact of the run that seeded the timeline; the dialog is reachable on phones). The derush timeline's
 * analysis lists clipA's silences g0 3.01-4.50 s and g1 8.00-9.51 s (no stub: the free analysis measures
 * them). On origin/master the dialog reads the VideoCompile EDL ("The analysis lists no silence") and is
 * hidden below `sm` on phones.
 */

const PHONES_TOO = ['desktop', 'pixel7', 'iphone14webkit'] as const;
const items = (tl: Timeline) => tl.document.tracks[0].items;
const length = (tl: Timeline) => items(tl).reduce((n, i) => n + (i.recordOut - i.recordIn), 0);

async function openDialog(ed: EditorPage): Promise<void> {
  await ed.openRemoveSilence();
  await expect(ed.page.getByRole('dialog')).toBeVisible();
}

const spans = (ed: EditorPage) => ed.page.locator('[data-testid^="remove-silence-span-"]');

test('P4-SILENCE-01 Remove silence lists the two silences the run\'s analysis measured', async ({ freshTimeline, openEditor }) => {
  runOn(...PHONES_TOO);
  knownFailure('P4-SILENCE-01');
  const ed = await openEditor(await freshTimeline('derush'));
  await openDialog(ed);
  await expect(ed.page.getByTestId('remove-silence-none'), 'not "the analysis lists no silence"').toHaveCount(0);
  await expect(spans(ed), 'g0 and g1 are listed').toHaveCount(2);
});

test('P4-SILENCE-02 the summary shows the cut count and the frames removed, and follows the padding', async ({ freshTimeline, openEditor }) => {
  runOn(...PHONES_TOO);
  knownFailure('P4-SILENCE-02');
  const ed = await openEditor(await freshTimeline('derush'));
  await openDialog(ed);
  const summary = ed.page.getByTestId('remove-silence-summary');
  await expect(summary).toContainText(/\b2 cuts?\b/);
  const frames = async () => Number(/(\d+) frames? removed/.exec((await summary.textContent()) ?? '')?.[1] ?? -1);
  const before = await frames();
  expect(before, 'about 3 s of silence is removed').toBeGreaterThan(60);
  const pad = ed.page.getByTestId('remove-silence-pad');
  await pad.fill('10');
  await pad.press('Tab');
  await expect.poll(frames, { message: 'more padding removes fewer frames' }).toBeLessThan(before);
});

test('P4-SILENCE-03 Apply cuts both silences as ONE undo step', async ({ freshTimeline, openEditor }) => {
  runOn(...PHONES_TOO);
  knownFailure('P4-SILENCE-03');
  const ed = await openEditor(await freshTimeline('derush'));
  await openDialog(ed);
  await ed.tap(ed.page.getByTestId('remove-silence-apply'));
  const cut = await ed.expectServer((tl) => items(tl).length === 3, 'two silences cut leave three pieces');
  expect(length(cut), 'the silences are gone').toBeLessThan(360 - 60);
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((tl) => items(tl).length === 1 && length(tl) === 360, 'one undo restores the whole clip');
});

test('P4-SILENCE-04 phone: on a timeline not seeded from an analysis the dialog says there is no measured silence', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P4-SILENCE-04');
  const ed = await openEditor(seed.seededTimelineId);
  await openDialog(ed);
  await expect(ed.page.getByRole('dialog').getByText(/no analysis to read from|no measured silence/i).first()).toBeVisible();
  await expect(ed.page.getByTestId('remove-silence-apply')).toBeDisabled();
});

test('P6-SIL-01 the Remove silence dialog cuts at the measured silences (track, padding, min length, spans, apply)', async ({ freshTimeline, openEditor }) => {
  runOn(...PHONES_TOO);
  knownFailure('P6-SIL-01');
  const ed = await openEditor(await freshTimeline('derush'));
  await openDialog(ed);
  await expect(ed.page.getByTestId('remove-silence-track')).toBeVisible();
  await expect(ed.page.getByTestId('remove-silence-min')).toBeVisible();
  await expect(spans(ed)).toHaveCount(2);
  await ed.tap(ed.page.getByTestId('remove-silence-apply'));
  const cut = await ed.expectServer((tl) => items(tl).length === 3, 'three pieces after the cut');
  const [a, b] = items(cut);
  // g0 starts at 3.01 s = frame 90: the first piece ends there (within the padding), the second resumes after 4.50 s.
  expect(Math.abs(a.recordOut - 90), `first cut at ~frame 90 (got ${a.recordOut})`).toBeLessThan(12);
  expect(Math.abs((b.sourceIn ?? 0) - 135), `second piece starts in the source at ~frame 135 (got ${b.sourceIn})`).toBeLessThan(12);
});
