import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip, type Item, type Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { allSentOps, trackIndex, uuid, selectAt } from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B: dialogs, menus and shared widgets (inventory timeline.md 2.L): the keymap editor (TL-450..456),
 * the compound clip editor (TL-470..475), the preset picker (TL-480..482), the keyframe lane (TL-490..494).
 * Desktop only where the control is desktop-only by design (keymap, compound editor); the preset picker and
 * keyframe lane run on every project.
 */

const prefsUrl = '/api/v1/editor/preferences';

async function openKeymap(ed: EditorPage) {
  await ed.tap(ed.testId('open-keymap'));
  // T-007: the header icon opens the read-only cheatsheet; Edit shortcuts opens the editable keymap.
  const cheatsheet = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(cheatsheet).toBeVisible();
  await ed.tap(cheatsheet.getByTestId('shortcuts-edit'));
  const dialog = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(dialog).toBeVisible();
  return dialog;
}

test.describe('keymap editor (desktop)', () => {
  /** The shortcuts are the signed-in user's, and this stack has one admin: leave them as found. */
  test.afterEach(async ({ api }) => {
    await api.request.put(prefsUrl, { data: { keymap: {}, layouts: [] }, failOnStatusCode: false }).catch(() => undefined);
  });

  test('TL-450 the dialog lists every action with its default chord', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openKeymap(ed);
    // 36 actions at the start of this suite; later work adds some (Delete vs Lift), so it is "at least 36" with the known ones present.
    await expect.poll(() => ed.page.locator('[data-testid^="keymap-row-"]').count()).toBeGreaterThanOrEqual(36);
    for (const action of ['toolSelect', 'toolHand', 'liftSelection', 'bladeAtPlayhead', 'addMarker']) await expect(ed.testId(`keymap-row-${action}`)).toBeVisible();
    await expect(ed.testId('keymap-row-toolHand')).toContainText('H');
    await expect(ed.testId('keymap-row-toolSelect')).toContainText('V');
  });

  test('TL-451 TL-452 TL-455 rebind an action, save (PUT 200), use the new key, and it survives a reload; reset one restores the default', async ({ freshTimeline, openEditor, api }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    let dialog = await openKeymap(ed);
    await ed.tap(ed.testId('keymap-change-toolHand'));
    await ed.page.keyboard.press('F7');
    await expect(ed.testId('keymap-row-toolHand')).toContainText('F7');
    const put = ed.page.waitForResponse((r) => r.url().includes(prefsUrl) && r.request().method() === 'PUT');
    await ed.tap(ed.testId('keymap-save'));
    expect((await put).status(), 'the preferences are stored').toBeLessThan(300);
    await expect(dialog).toBeHidden();
    expect(JSON.stringify(await api.json<unknown>('GET', prefsUrl))).toContain('F7');

    await ed.canvas.click({ position: { x: 5, y: 5 } }).catch(() => undefined);
    await ed.page.keyboard.press('F7');
    await expect(ed.testId('tool-hand'), 'the new key selects the hand tool').toHaveAttribute('aria-pressed', 'true');
    await ed.page.keyboard.press('v');
    await expect(ed.testId('tool-select')).toHaveAttribute('aria-pressed', 'true');

    await ed.reload();
    dialog = await openKeymap(ed);
    await expect(ed.testId('keymap-row-toolHand'), 'still F7 after a reload').toContainText('F7');
    await ed.tap(ed.testId('keymap-reset-toolHand'));
    await expect(ed.testId('keymap-row-toolHand')).toContainText('H');
    await expect(ed.testId('keymap-reset-toolHand'), 'the reset button goes away on a default row').toHaveCount(0);
    await ed.tap(dialog.getByRole('button', { name: 'Cancel' }));
    await expect(dialog).toBeHidden();
  });

  test('TL-453 TL-454 TL-456 two actions on one chord show a conflict and block Save; Reset all clears it; Cancel sends nothing', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    const dialog = await openKeymap(ed);
    let puts = 0;
    ed.page.on('request', (r) => {
      if (r.url().includes(prefsUrl) && r.method() === 'PUT') puts++;
    });
    await ed.tap(ed.testId('keymap-change-toolHand'));
    await ed.page.keyboard.press('V'); // already Select's chord
    await expect(ed.testId('keymap-conflicts')).toBeVisible();
    await expect(ed.testId('keymap-save')).toBeDisabled();
    await ed.tap(ed.testId('keymap-reset-all'));
    await expect(ed.testId('keymap-conflicts')).toHaveCount(0);
    await expect(ed.testId('keymap-save')).toBeEnabled();
    await ed.tap(ed.testId('keymap-change-toolHand'));
    await ed.page.keyboard.press('F8');
    await ed.tap(dialog.getByRole('button', { name: 'Cancel' }));
    await expect(dialog).toBeHidden();
    await ed.page.waitForTimeout(1000);
    expect(puts, 'Cancel sends nothing').toBe(0);
    const reopened = await openKeymap(ed);
    await expect(ed.testId('keymap-row-toolHand'), 'the cancelled change is gone').toContainText('H');
    await ed.tap(reopened.getByRole('button', { name: 'Cancel' }));
  });
});

test.describe('compound clip editor (desktop)', () => {
  async function openCompound(ed: EditorPage) {
    await selectAt(ed, 60);
    await ed.openPanelTab('tab-structure');
    await ed.tap(ed.testId('compound-create'));
    await ed.expectServer((tl) => (tl.document.sequences ?? []).length === 1, 'the compound is stored');
    await ed.tap(ed.page.locator('[data-testid^="compound-open-"]').first());
    const dialog = ed.page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    return dialog;
  }
  /** The dialog animates in: take the box once it stops moving. */
  const stableBox = async (loc: import('@playwright/test').Locator) => {
    let last = JSON.stringify(await loc.boundingBox());
    await expect.poll(async () => { const now = JSON.stringify(await loc.boundingBox()); const same = now === last; last = now; return same; }, { timeout: 10_000, intervals: [300] }).toBe(true);
    return (await loc.boundingBox())!;
  };
  const seqItems = (tl: Timeline) => {
    const seq = (tl.document.sequences ?? [])[0] as { tracks?: Array<{ items: Array<{ recordIn: number; recordOut: number }> }> } | undefined;
    return (seq?.tracks ?? []).flatMap((t) => t.items);
  };

  test('TL-470 TL-471 TL-472 TL-473 TL-474 inside a compound: cut, undo, cut again, lift a piece, then save as one step', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    const dialog = await openCompound(ed);
    const box = await stableBox(dialog.getByTestId('timeline-canvas'));
    await expect(dialog.getByTestId('sequence-save'), 'nothing changed yet').toBeDisabled();
    await ed.page.mouse.click(box.x + 120, box.y + 10); // ruler: playhead inside the compound
    await expect(dialog.getByText('00:00:02:00')).toBeVisible();
    await ed.tap(dialog.getByTestId('sequence-blade'));
    await expect(dialog.getByTestId('sequence-save')).toBeEnabled();
    await ed.tap(dialog.getByTestId('sequence-undo')); // takes the cut back (checked by the piece count at the end)
    await ed.tap(dialog.getByTestId('sequence-blade'));
    // Select the first piece on the inner canvas and lift it.
    // The first piece spans the first ~120 px of the lane: click inside it (a lane's top offset depends on the
    // layout, so try a few heights) until the selection enables Lift.
    const lift = dialog.getByTestId('sequence-lift');
    for (const dy of [60, 75, 90, 110]) {
      await ed.page.mouse.click(box.x + 30, box.y + dy);
      if (await lift.isEnabled()) break;
    }
    await expect(lift, 'a click on the first piece selects it').toBeEnabled();
    await ed.tap(dialog.getByTestId('sequence-lift'));
    const revBefore = (await ed.serverTimeline()).revision;
    await ed.tap(dialog.getByTestId('sequence-save'));
    const saved = await ed.expectServer((tl) => seqItems(tl).length === 1 && seqItems(tl)[0].recordIn > 0, 'the compound now holds the one piece that was kept');
    expect(saved.revision, 'one stored step for the whole session').toBe(revBefore + 1);
    expect(seqItems(saved)[0].recordIn, 'the lifted piece left a gap before the kept one').toBeGreaterThan(0);
  });

  test('TL-475 Discard changes closes the compound editor and stores nothing', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    const dialog = await openCompound(ed);
    const box = await stableBox(dialog.getByTestId('timeline-canvas'));
    await ed.page.mouse.click(box.x + 120, box.y + 10);
    await ed.tap(dialog.getByTestId('sequence-blade'));
    const before = (await ed.serverTimeline()).revision;
    await ed.tap(dialog.getByRole('button', { name: 'Discard changes' }));
    await expect(dialog).toHaveCount(0);
    await ed.page.waitForTimeout(1500);
    const tl = await ed.serverTimeline();
    expect(tl.revision).toBe(before);
    expect(seqItems(tl).length, 'the compound is as it was').toBe(1);
  });
});

test('TL-480 TL-481 TL-482 transition presets: save the current settings, apply it, delete it (asks first)', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items = [mediaClip(uuid(), 'clipA.mp4', seed.files.clipA, 0, 150), mediaClip(uuid(), 'clipB.mp4', seed.files.clipB, 150, 300, { sourceIn: 60 })];
  });
  const name = `lane-b-wipe-${Date.now()}`;
  const ed = await openEditor(id);
  await selectAt(ed, 30);
  await ed.openInspector();
  await ed.tap(ed.testId('inspector-advanced-toggle')); // the by-frames form and its saved presets live under Advanced
  await ed.tap(ed.testId('inspector-add-transition-type'));
  await ed.tap(ed.page.getByRole('option', { name: 'wiperight' }));
  await ed.testId('inspector-add-transition-frames').fill('10');
  await ed.testId('transition-presets-name').fill(name);
  await ed.tap(ed.testId('transition-presets-save'));
  const list = () => api.json<Array<{ id: string; name: string }>>('GET', `${api.timelinesUrl(seed.projectId)}/presets?kind=transition`);
  await expect.poll(async () => (await list()).some((p) => p.name === name), { message: 'the preset is stored on the server' }).toBe(true);

  // Apply: change the form away, then pick the preset - the form takes its values back.
  await ed.tap(ed.testId('inspector-add-transition-type'));
  await ed.tap(ed.page.getByRole('option', { name: 'dissolve', exact: true }));
  await ed.testId('inspector-add-transition-frames').fill('4');
  await ed.tap(ed.testId('transition-presets-select'));
  await ed.tap(ed.page.getByRole('option', { name }));
  await expect(ed.testId('inspector-add-transition')).toContainText('wiperight');
  await expect(ed.testId('inspector-add-transition-frames')).toHaveValue('10');

  // Delete asks first; "Keep" leaves it, "Delete" removes it.
  await ed.tap(ed.testId('transition-presets-delete'));
  await ed.tap(ed.page.getByRole('option', { name }));
  await expect(ed.testId('transition-presets-delete-prompt')).toBeVisible();
  await ed.tap(ed.testId('transition-presets-delete-cancel'));
  expect((await list()).some((p) => p.name === name), 'Keep leaves the preset').toBe(true);
  await ed.tap(ed.testId('transition-presets-delete'));
  await ed.tap(ed.page.getByRole('option', { name }));
  await ed.tap(ed.testId('transition-presets-delete-confirm'));
  await expect.poll(async () => (await list()).some((p) => p.name === name), { message: 'Delete removes it' }).toBe(false);
});

test('TL-249 TL-490 TL-491 TL-492 TL-493 TL-494 keyframe lane: select a key, drag it, change its easing, delete it, double-click seeks', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  const op = (tl: Timeline) => {
    const t = tl.document.tracks[trackIndex(tl.document, 'video')].items[0] as Item;
    return (t.transform as { opacity?: { keys?: Array<{ frame: number; value: number; interp: string }> } } | null | undefined)?.opacity?.keys ?? [];
  };
  await selectAt(ed, 30);
  await ed.openInspector();
  // Two keys: frame 0 (value 1) and frame 60 (value 0.4).
  await ed.tap(ed.testId('inspector-finetune-toggle')); // the diamond lives under Fine-tune keyframes (R07.1)
  await ed.seek(0);
  await ed.tap(ed.testId('inspector-key-opacity'));
  await ed.seek(60);
  await ed.tap(ed.testId('inspector-key-opacity'));
  await ed.testId('inspector-opacity').fill('40'); // percent
  await ed.testId('inspector-opacity').press('Enter');
  await ed.expectServer((tl) => op(tl).length === 2 && op(tl).some((k) => k.frame === 60 && k.value === 0.4), 'two opacity keys are stored');
  const lane = ed.testId('keyframe-lane-opacity');
  await expect(lane).toBeVisible();

  // Select the key at 60: the lane names it.
  await ed.tap(ed.testId('keyframe-opacity-60'));
  await expect(lane).toContainText('Key at 60');

  // Easing.
  await ed.tap(ed.testId('keyframe-interp'));
  await ed.tap(ed.page.getByRole('option', { name: 'easeInQuad' }));
  await ed.expectServer((tl) => op(tl).find((k) => k.frame === 60)?.interp === 'easeInQuad', 'the key easing is stored');

  // Drag along the lane (mouse; a phone drags with touch and WebKit cannot synthesise it).
  if (!ed.isPhone) {
    const circle = (await ed.testId('keyframe-opacity-60').boundingBox())!;
    const svg = (await lane.locator('svg').first().boundingBox())!;
    const sx = circle.x + circle.width / 2;
    const sy = circle.y + circle.height / 2;
    await ed.mouseDrag(sx, sy, sx - svg.width * 0.2, sy, 8);
    const moved = await ed.expectServer((tl) => op(tl).length === 2 && !op(tl).some((k) => k.frame === 60), 'the key moved off frame 60');
    expect(op(moved).some((k) => k.frame > 0 && k.frame < 60), 'to an earlier frame').toBe(true);
    await expect.poll(() => allSentOps(ed), { message: 'a moveKeyframe op was sent' }).toContain('moveKeyframe');
  }

  // Double-click on the lane seeks.
  if (!ed.isPhone) {
    await ed.seek(0);
    await lane.scrollIntoViewIfNeeded(); // the lane sits under Fine-tune keyframes, further down the panel
    const svg = (await lane.locator('svg').first().boundingBox())!;
    await ed.page.mouse.dblclick(svg.x + svg.width * 0.5, svg.y + 4);
    await expect(ed.testId('status-timecode'), 'the playhead moved into the clip').not.toHaveText('00:00:00:00');
  }

  // Delete the selected key.
  const remaining = op(await ed.serverTimeline());
  const target = remaining.find((k) => k.frame !== 0)!;
  await ed.tap(ed.testId(`keyframe-opacity-${target.frame}`));
  await ed.tap(ed.testId('keyframe-delete'));
  await ed.expectServer((tl) => op(tl).length === 1, 'the key is deleted');
});
