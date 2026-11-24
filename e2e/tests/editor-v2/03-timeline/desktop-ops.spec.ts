import { test, expect, SEEDED_LAYOUT } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { expectLargerScreenState, runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 03 timeline editing, rows that fail on desktop (and the phone "needs a larger screen" half of the
 * compound rows). Acceptance: remediation-plan.md W0.2 / W0.6; verdict-table.md P3/P4/P8 rows.
 */

/** P4-OPS-07 (W0.2, eng D1): "+ video / + audio" add a track with a fresh Guid id that saves and survives reload. */
test('P4-OPS-07 + video and + audio add tracks that are stored and survive reload', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  knownFailure('P4-OPS-07');
  const ed = await openEditor(await freshTimeline('blank'));
  const before = await ed.serverTimeline();
  await ed.tap(ed.testId('add-video-track'));
  await ed.tap(ed.testId('add-audio-track'));
  const both = (tl: Timeline) => tl.document.tracks.filter((t) => t.kind === 'video').length === 2 && tl.document.tracks.filter((t) => t.kind === 'audio').length === 2;
  await ed.expectServer(both, '+V and +A are stored');
  await expect(ed.chip).not.toContainText(/rejected/i);
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  for (const t of (await ed.serverTimeline()).document.tracks) expect(t.id, 'every track id is a Guid').toMatch(guid);
  await ed.reload();
  expect(both(await ed.serverTimeline()), 'the tracks survive reload').toBe(true);
  expect(before.document.tracks.length).toBe(2);
});

/**
 * P4-OPS-12 (W0.6): marks set with Mark in / Mark out are written to the document (`inOut`), survive
 * reload and drive Deliver's range. Mark out takes the frame under the playhead and the stored range
 * is end-exclusive, so marking in at 60 and out at 120 keeps frames 60..120: `{ inFrame: 60, outFrame: 121 }`.
 * The program viewer shows marks as the lit Mark in / Mark out buttons (no timecode readout: the
 * `mark-*-value` readouts belong to the Media workspace's source viewer), so after reload the values
 * are read where they act: the range Deliver sends with a render.
 */
test('P4-OPS-12 mark in and mark out persist to the document and survive reload', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P4-OPS-12');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(60);
  await ed.tap(ed.testId('mark-in'));
  await ed.seek(120);
  await ed.tap(ed.testId('mark-out'));
  const marked = (tl: Timeline) => {
    const io = tl.document.inOut as { inFrame?: number; outFrame?: number } | null | undefined;
    return io?.inFrame === 60 && io?.outFrame === 121;
  };
  await ed.expectServer(marked, 'the document stores inOut 60..121 (frames 60 to 120)');
  await ed.reload();
  expect(marked(await ed.serverTimeline()), 'the stored range survives reload').toBe(true);
  await expect(ed.testId('mark-in'), 'the in mark is lit after reload').toHaveAttribute('data-variant', 'light');
  await expect(ed.testId('mark-out'), 'the out mark is lit after reload').toHaveAttribute('data-variant', 'light');

  // Deliver reads the reloaded marks: the range switch starts on and the render request carries 60..121.
  const sent: unknown[] = [];
  await ed.page.route(/\/edit-timelines\/[^/]+\/render$/, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    sent.push(route.request().postDataJSON());
    await route.abort();
  });
  await ed.workspace('deliver');
  await expect(ed.testId('render-range'), 'Deliver offers the marked range after reload').toBeEnabled();
  await expect(ed.testId('render-range'), 'and starts with it switched on').toBeChecked();
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => sent.length, { message: 'the Render button sent a request' }).toBeGreaterThan(0);
  const range = (sent[0] as { range?: { inFrame?: number; outFrame?: number } | null }).range;
  expect(range, 'the render covers the reloaded marks').toEqual({ inFrame: 60, outFrame: 121 });
});

/**
 * P3-SEL-03 (W0.6, eng D17): select forward from the playhead and select all on a track have a menu path
 * (the audit found no keymap action, no context menu and no text for them). The path is the timeline
 * header's Select menu (keys A / Shift+A / Alt+A do the same); the timeline has no right-click menu.
 */
test('P3-SEL-03 select forward from the playhead and select all on a track from the timeline menu', async ({ seed, openEditor }) => {
  runOn('desktop');
  knownFailure('P3-SEL-03');
  const ed = await openEditor(seed.seededTimelineId);
  await ed.seek(SEEDED_LAYOUT.clipB[0]);
  const selectMenu = ed.testId('select-menu');
  const menuItem = (name: RegExp) => ed.page.getByRole('menuitem', { name });

  await ed.tap(selectMenu);
  await ed.tap(menuItem(/select forward/i));
  // clipB, vertical and still start at or after the playhead.
  await expect(ed.page.getByText(/\b3 items selected/)).toBeVisible();

  await ed.tap(selectMenu);
  await ed.tap(menuItem(/select all on (this )?track/i));
  await expect(ed.page.getByText(/\b4 items selected/)).toBeVisible();
});

/** P8-SEQ-01 (W0.2, eng D2): Make compound from two selected clips is stored (inner track ids are Guids) and survives reload. */
test('P8-SEQ-01 make compound from two clips is stored and survives reload', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P8-SEQ-01');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.selectClipAt(60);
  await ed.selectClipAt(SEEDED_LAYOUT.clipB[0] + 60, 0, ['Control']);
  await expect(ed.page.getByText(/\b2 items selected/)).toBeVisible();
  await ed.openPanelTab('tab-structure');
  await ed.tap(ed.testId('compound-create'));
  const made = (tl: Timeline) => (tl.document.sequences?.length ?? 0) === 1 && !tl.document.tracks[0].items.some((i) => i.name === 'clipA.mp4' || i.name === 'clipB.mp4');
  await ed.expectServer(made, 'the compound clip replaces clipA and clipB on the server');
  await expect(ed.chip).not.toContainText(/rejected/i);
  await ed.reload();
  expect(made(await ed.serverTimeline()), 'the compound survives reload').toBe(true);
});

/** P8-SEQ-03 (W0.2 desktop half): an edit inside a compound, saved with "Save to compound", is ONE stored setSequence. */
test('P8-SEQ-03 an edit inside a compound is saved to the document as one step', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P8-SEQ-03');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.selectClipAt(60);
  await ed.openPanelTab('tab-structure');
  await ed.tap(ed.testId('compound-create'));
  const seqItems = (tl: Timeline) => {
    const seq = (tl.document.sequences ?? [])[0] as { tracks?: Array<{ items: unknown[] }> } | undefined;
    return (seq?.tracks ?? []).reduce((n, t) => n + t.items.length, 0);
  };
  await ed.expectServer((tl) => seqItems(tl) === 1, 'the compound is stored');
  const open = ed.page.locator('[data-testid^="compound-open-"]').first();
  await ed.tap(open);
  const dialog = ed.page.getByRole('dialog');
  const canvas = dialog.getByTestId('timeline-canvas');
  const box = (await canvas.boundingBox())!;
  await ed.page.mouse.click(box.x + 120, box.y + 10); // ruler: playhead to frame ~60 inside the compound
  await ed.tap(dialog.getByTestId('sequence-blade'));
  const revBefore = (await ed.serverTimeline()).revision;
  await ed.tap(dialog.getByTestId('sequence-save'));
  const saved = await ed.expectServer((tl) => seqItems(tl) === 2, 'the cut inside the compound is stored');
  expect(saved.revision, 'one stored step for the whole compound edit').toBe(revBefore + 1);
});

/* ------------------------------------------------------------------ phone: cut / deferred (W0.7) */

test('P8-SEQ-01 phone: Make compound shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P8-SEQ-01', 'phone');
  const ed = await openEditor(seed.seededTimelineId);
  await ed.selectClipAt(60);
  await ed.openPanelTab('tab-structure');
  await expectLargerScreenState(ed.page, /compound/i, 'Make compound on a phone', ed.testId('structure-panel'));
});

test('P8-SEQ-03 phone: editing inside a compound shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P8-SEQ-03', 'phone');
  const ed = await openEditor(seed.seededTimelineId);
  await ed.openPanelTab('tab-structure');
  await expectLargerScreenState(ed.page, /compound/i, 'Edit compound on a phone', ed.testId('structure-panel'));
});
