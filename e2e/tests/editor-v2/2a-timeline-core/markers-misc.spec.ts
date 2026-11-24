import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, toastWith } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A leftovers: marker rename (S16: Enter commits, Escape reverts), the Remove silence button, the parity badge.
 */

test('TL-153 renaming a marker commits on Enter and Escape puts the old name back (desktop and phone)', async ({ freshTimeline, openEditor, api, seed }) => {
  const markerId = crypto.randomUUID();
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, PAIR, {
      mutate: (doc) => {
        doc.markers = [{ id: markerId, name: 'Old name', frame: 30, color: null, note: null, durationFrames: null }];
      },
    }),
  );
  await ed.openPanelTab('Markers');
  const input = ed.page.getByRole('textbox', { name: 'Rename Old name' });
  await expect(input).toBeVisible();
  await input.fill('New name');
  await input.press('Enter');
  await ed.expectServer((tl) => tl.document.markers?.[0]?.name === 'New name', 'Enter stored the new name');

  const again = ed.page.getByRole('textbox', { name: 'Rename New name' });
  await again.fill('Throwaway');
  await again.press('Escape');
  await expect(again, 'Escape restores the stored name').toHaveValue('New name');
  expect((await ed.serverTimeline()).document.markers?.[0]?.name, 'nothing was stored').toBe('New name');

  await again.fill('   ');
  await again.press('Enter');
  await expect(toastWith(ed, /needs a name/i), 'an empty name is refused with a reason').toBeVisible();
  await expect(again).toHaveValue('New name');
});

test('TL-093 Remove silence opens its dialog and says so when there is no analysis to read from (or lists the gaps once the project has one)', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.openRemoveSilence();
  await expect(ed.page.getByRole('dialog')).toBeVisible();
  // The kept lane seed project is shared: once another spec analysed its footage the dialog lists the measured gaps instead.
  await expect(ed.testId('remove-silence-no-analysis').or(ed.page.getByRole('combobox', { name: 'Track' })).first()).toBeVisible();
  expect(ed.patches).toEqual([]);
});

test('TL-051 the preview badge says "Preview faithful" for plain footage and stops saying it in Single-clip mode', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const badge = ed.testId('parity-badge');
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  // R04.2: faithful only when nothing is degraded. A browser drawing with software graphics runs the live preview at reduced
  // quality (it says so under the viewer), and the badge then says "Approximate preview" and lists that reason.
  const reduced = await ed.testId('preview-reduced-note').isVisible();
  if (reduced) {
    await expect(badge).toHaveAttribute('data-parity', 'approximate', { timeout: 30_000 });
    await ed.tap(badge);
    await expect(ed.page.getByTestId('parity-reasons')).toContainText('Reduced quality');
    await ed.page.keyboard.press('Escape');
  } else {
    await expect(badge).toHaveText('Preview faithful', { timeout: 30_000 });
  }
  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  await ed.page.getByTestId('degraded-toggle').check({ force: true });
  await expect(badge, 'never "faithful" while the preview is the Simple preview').not.toHaveText('Preview faithful', { timeout: 30_000 });
  await expect(badge).toHaveText('Simple preview');
});
