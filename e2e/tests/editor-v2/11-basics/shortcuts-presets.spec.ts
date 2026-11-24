import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 11 basics: shortcuts, the keymap dialog and preset deletion (remediation-plan.md W0.6 "Shortcuts do not fire
 * behind dialogs, on sliders or buttons; keymap save failure keeps the dialog open; preset Delete confirms";
 * report.md §5 UI2-08, UI2-09 / ui1 D18, UI2-14 / d5 N10; T-031 item 5).
 */

/**
 * UI2-08 (desktop): a failed shortcut save keeps the dialog open with the reason, and the change the person
 * made is still there. The save is forced to fail with a 500 on the preferences PUT (only that request); the
 * shared admin's stored preferences are never written by this test.
 */
test('UI2-08 a failed shortcut save keeps the dialog and the change open with the reason', async ({ freshTimeline, openEditor, api }) => {
  runOn('desktop');
  knownFailure('UI2-08');
  const ed = await openEditor(await freshTimeline('seeded'));
  const stored = await api.json<unknown>('GET', '/api/v1/editor/preferences', undefined, [200, 204, 404]);
  let puts = 0;
  await ed.page.route('**/api/v1/editor/preferences', async (route) => {
    if (route.request().method() === 'GET') return route.continue();
    puts++;
    return route.fulfill({ status: 500, contentType: 'application/json', json: { message: 'The preferences store is unavailable.' } });
  });
  await ed.tap(ed.testId('open-keymap'));
  // T-007: Edit shortcuts opens the editable dialog from the read-only cheatsheet.
  await ed.tap(ed.page.getByTestId('shortcuts-edit'));
  const dialog = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(dialog).toBeVisible();
  await ed.tap(ed.testId('keymap-change-toolHand'));
  await ed.page.keyboard.press('F9');
  await expect(ed.testId('keymap-row-toolHand'), 'the new chord is in the draft').toContainText('F9');
  await ed.tap(ed.testId('keymap-save'));
  await expect.poll(() => puts, { message: 'Save sent the preferences' }).toBeGreaterThan(0);
  const error = ed.testId('keymap-save-error');
  await expect(error, 'the dialog says the shortcuts were not saved').toBeVisible();
  await expect(error).toContainText(/not saved/i);
  expect((await error.innerText()).trim().length, 'the reason is not empty').toBeGreaterThan(20);
  await expect(dialog, 'the dialog stays open').toBeVisible();
  await expect(ed.testId('keymap-row-toolHand'), 'the change is kept for another try').toContainText('F9');
  await ed.tap(dialog.getByRole('button', { name: 'Cancel' }));
  await expect(dialog).toBeHidden();
  expect(await api.json<unknown>('GET', '/api/v1/editor/preferences', undefined, [200, 204, 404]), 'nothing was stored').toEqual(stored);
});

/**
 * UI2-09 (desktop, ui1 D18): a global shortcut does not act behind an open dialog, on a focused slider, or with
 * Space on a focused button. Each variant checks the timeline the shortcut would have changed.
 */
test('UI2-09 shortcuts do not fire behind a dialog, on a slider or on a focused button', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('UI2-09');
  const ed = await openEditor(await freshTimeline('seeded'));
  const items = async () => (await ed.serverTimeline()).document.tracks.reduce((n, t) => n + t.items.length, 0);
  const count = await items();

  // 1. Behind the keymap dialog: Delete (lift) with a clip selected changes nothing.
  await ed.selectClipAt(30);
  const sent = ed.patches.length;
  await ed.tap(ed.testId('open-keymap'));
  await ed.tap(ed.page.getByTestId('shortcuts-edit'));
  const dialog = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(dialog).toBeVisible();
  await ed.page.keyboard.press('Delete');
  await ed.page.keyboard.press('Period');
  await ed.page.waitForTimeout(1500);
  expect(ed.patches.length, `no edit was sent while the dialog was open: ${ed.patchSummary()}`).toBe(sent);
  expect(await items(), 'every clip is still on the timeline').toBe(count);
  await ed.tap(dialog.getByRole('button', { name: 'Cancel' }));
  await expect(dialog).toBeHidden();

  // 2. On a focused slider: the arrow key moves the slider, not the playhead.
  await ed.seek(60);
  await ed.workspace('audio');
  const audio = (await ed.serverTimeline()).document.tracks.find((t) => t.kind === 'audio')!;
  const thumb = ed.testId(`mixer-fader-${audio.id}`).getByRole('slider').or(ed.page.locator(`[data-testid="mixer-fader-${audio.id}"][role="slider"]`)).first();
  await thumb.focus();
  await ed.page.keyboard.press('ArrowRight');
  await ed.page.keyboard.press('ArrowRight');
  await ed.page.waitForTimeout(500);
  await expect(ed.testId('status-timecode'), 'the playhead stays put while a slider has focus').toHaveText('00:00:02:00');

  // 3. Space on a focused button presses that button only: playback does not start.
  const shortcuts = ed.testId('open-keymap');
  await shortcuts.focus();
  await ed.page.keyboard.press('Space');
  await expect(dialog, 'Space pressed the focused button').toBeVisible();
  await ed.page.waitForTimeout(1200);
  await expect(ed.testId('status-timecode'), 'Space on a button did not start playback').toHaveText('00:00:02:00');
  await ed.tap(dialog.getByRole('button', { name: 'Cancel' }));
});

/**
 * UI2-14 (desktop d5, phone m3, d5 N10): choosing a preset in the Delete list asks first; Keep keeps it; a failed
 * delete says why; confirming deletes it.
 */
test('UI2-14 deleting a preset asks first and a failed delete says why', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  knownFailure('UI2-14');
  const name = `rem-ui214-${test.info().project.name}-${Date.now() % 100000}`;
  const presetsUrl = `${api.timelinesUrl(seed.projectId)}/presets`;
  const preset = await api.json<{ id: string }>('POST', presetsUrl, { kind: 'mastering', name, payload: { targetLufs: -14, truePeakDbtp: -1, loudnessRange: 11 } }, [200, 201]);
  const stored = async () => (await api.json<Array<{ id: string }>>('GET', `${presetsUrl}?kind=mastering`)).some((p) => p.id === preset.id);
  try {
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.workspace('deliver');
    // Mantine's switch input sits over its label; check it directly (the preset picker shows once it is on).
    await ed.tap(ed.testId('render-advanced-toggle'));
    const normalise = ed.page.getByRole('switch', { name: 'Normalise loudness' });
    // The input itself is the click target (it sits over the label); not forced, so it waits for the Advanced section to stop animating open.
    if (!(await normalise.isChecked())) await normalise.check();
    await expect(normalise).toBeChecked();
    const choose = async () => {
      await ed.tap(ed.testId('mastering-presets-delete'));
      await ed.tap(ed.page.getByRole('option', { name }));
    };

    await choose();
    const prompt = ed.testId('mastering-presets-delete-prompt');
    await expect(prompt, 'choosing a preset asks before deleting').toBeVisible();
    await expect(prompt).toContainText(name);
    expect(await stored(), 'nothing is deleted before the answer').toBe(true);
    await ed.tap(ed.testId('mastering-presets-delete-cancel'));
    await expect(prompt).toBeHidden();
    expect(await stored(), 'Keep keeps the preset').toBe(true);

    await ed.page.route(`**/presets/${preset.id}`, (route) =>
      route.request().method() === 'DELETE' ? route.fulfill({ status: 500, contentType: 'application/json', json: { message: 'The preset store is unavailable.' } }) : route.continue(),
    );
    await choose();
    await ed.tap(ed.testId('mastering-presets-delete-confirm'));
    await expect(ed.testId('mastering-presets').getByText(/unavailable|could not be deleted|went wrong/i), 'a failed delete says why').toBeVisible();
    expect(await stored(), 'a failed delete keeps the preset').toBe(true);
    await ed.page.unroute(`**/presets/${preset.id}`);

    await choose();
    await ed.tap(ed.testId('mastering-presets-delete-confirm'));
    await expect.poll(stored, { message: 'confirming deletes the preset' }).toBe(false);
  } finally {
    if (await stored().catch(() => false)) await api.json('DELETE', `${presetsUrl}/${preset.id}`, undefined, [200, 204, 404]);
  }
});
