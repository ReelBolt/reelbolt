import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, countItems, itemNamed, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A, section 2.M: every keyboard action of lib/editing/keymap.ts (37 now: Delete selection was
 * split from Lift selection by decision D7). Desktop only: a phone has no hardware keyboard and every
 * action also exists as a button (asserted in phone-layout.spec.ts and delete-split.spec.ts).
 *
 * Each key press is checked on its REAL effect: the playhead timecode, a tool's pressed state, the
 * Clip menu's selection count, or the stored document.
 */

const FOUR: ClipSpec[] = [
  { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
  { name: 'clipC.mp4', file: 'clipA', recordIn: 120, recordOut: 180 },
  { name: 'clipD.mp4', file: 'clipB', recordIn: 180, recordOut: 240 },
];
const TONE: ClipSpec[] = [{ name: 'tone.wav', file: 'tone', recordIn: 0, recordOut: 150 }];

const frameOf = (tc: string | null): number => {
  const [h, m, s, f] = (tc ?? '0:0:0:0').split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * 30 + f;
};
const playhead = async (ed: EditorPage) => frameOf(await ed.testId('status-timecode').textContent());
const key = (ed: EditorPage, k: string) => ed.page.keyboard.press(k);

/** Takes keyboard focus off the timecode field (shortcuts never fire inside a text field). */
async function blurField(ed: EditorPage): Promise<void> {
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
}

/** Moves the playhead through the timecode field, then leaves the field so the next key is a shortcut. */
async function seekTo(ed: EditorPage, frame: number): Promise<void> {
  await ed.seek(frame);
  await blurField(ed);
}

async function selectedCount(ed: EditorPage): Promise<number> {
  const open = ed.testId('clip-actions-open');
  if (await open.isDisabled()) return 0;
  if (!(await ed.testId('clip-actions-menu').isVisible())) await open.click();
  const text = (await ed.testId('clip-actions-menu').textContent()) ?? '';
  await ed.page.keyboard.press('Escape');
  await expect(ed.testId('clip-actions-menu')).toBeHidden();
  const m = /(\d+) clips? selected/.exec(text);
  return m ? Number(m[1]) : -1;
}

test.beforeEach(() => {
  runOn('desktop');
});

test('TL-500 TL-503 TL-502 TL-504 Space plays and pauses, K stops, J and L shuttle', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await blurField(ed);
  await key(ed, 'Space');
  await expect(ed.testId('transport-play')).toHaveAttribute('aria-label', 'Pause');
  await expect.poll(() => playhead(ed), { message: 'the playhead advances while playing' }).toBeGreaterThan(5);
  await key(ed, 'Space');
  await expect(ed.testId('transport-play')).toHaveAttribute('aria-label', 'Play');
  const stopped = await playhead(ed);
  await ed.page.waitForTimeout(600);
  expect(await playhead(ed), 'paused means the timecode stays').toBe(stopped);

  await seekTo(ed, 100);
  await key(ed, 'KeyL');
  await key(ed, 'KeyL');
  await expect(ed.testId('shuttle-speed'), 'L twice is 2x forward').toContainText('2');
  await key(ed, 'KeyK');
  await expect(ed.testId('shuttle-speed')).toHaveCount(0);
  const kStopped = await playhead(ed);
  await key(ed, 'KeyJ');
  await expect(ed.testId('shuttle-speed'), 'J shuttles in reverse').toContainText('◀');
  await expect.poll(() => playhead(ed), { message: 'reverse moves the playhead back' }).toBeLessThan(kStopped);
  await key(ed, 'KeyK');
});

test('TL-501 Shift+Space plays from In to Out and stops at Out', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await seekTo(ed, 30);
  await key(ed, 'KeyI');
  await seekTo(ed, 60);
  await key(ed, 'KeyO');
  await seekTo(ed, 0);
  await key(ed, 'Shift+Space');
  await expect.poll(() => playhead(ed), { message: 'playback started at the In point' }).toBeGreaterThanOrEqual(30);
  await expect(ed.testId('transport-play'), 'and stopped on its own at Out').toHaveAttribute('aria-label', 'Play', { timeout: 20_000 });
  expect(await playhead(ed)).toBeLessThanOrEqual(62);
  expect(await playhead(ed)).toBeGreaterThanOrEqual(58);
});

test('TL-505 TL-506 TL-507 TL-508 TL-509 arrows step frames and edits, Shift+arrows ten frames and markers, Home and End jump', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await clipsTimeline(freshTimeline, api, seed, FOUR, {
    audioClips: TONE,
    mutate: (doc) => {
      doc.markers = [{ id: crypto.randomUUID(), name: 'M1', frame: 40, color: null, note: null, durationFrames: null }, { id: crypto.randomUUID(), name: 'M2', frame: 100, color: null, note: null, durationFrames: null }];
    },
  });
  const ed = await openEditor(id);
  await seekTo(ed, 10);
  await key(ed, 'ArrowRight');
  expect(await playhead(ed)).toBe(11);
  await key(ed, 'ArrowLeft');
  expect(await playhead(ed)).toBe(10);
  await key(ed, 'Shift+ArrowRight');
  expect(await playhead(ed)).toBe(20);
  await key(ed, 'Shift+ArrowLeft');
  expect(await playhead(ed)).toBe(10);
  await key(ed, 'ArrowDown');
  expect(await playhead(ed), 'next edit is the clip boundary at 60').toBe(60);
  await key(ed, 'ArrowUp');
  expect(await playhead(ed), 'previous edit is the start').toBe(0);
  await key(ed, 'Shift+ArrowDown');
  expect(await playhead(ed), 'next marker').toBe(40);
  await key(ed, 'Shift+ArrowDown');
  expect(await playhead(ed)).toBe(100);
  await key(ed, 'Shift+ArrowUp');
  expect(await playhead(ed), 'previous marker').toBe(40);
  await key(ed, 'End');
  expect(await playhead(ed), 'End goes to the last frame').toBe(239);
  await key(ed, 'Home');
  expect(await playhead(ed)).toBe(0);
});

test('TL-510 TL-511 TL-512 I, O and Alt+X set and clear the in and out marks in the document', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await seekTo(ed, 40);
  await key(ed, 'KeyI');
  await seekTo(ed, 100);
  await key(ed, 'KeyO');
  const range = (tl: { document: object }) => (tl.document as { inOut?: unknown }).inOut as { inFrame?: number; outFrame?: number } | null | undefined;
  await ed.expectServer((tl) => range(tl)?.inFrame === 40 && range(tl)?.outFrame === 101, 'the marks are stored: in 40, out 101 (end-exclusive)');
  await key(ed, 'Alt+KeyX');
  await ed.expectServer((tl) => !range(tl) || (range(tl)?.inFrame == null && range(tl)?.outFrame == null), 'Alt+X clears the stored range');
});

test('TL-513 M adds a marker at the playhead, stored', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await seekTo(ed, 75);
  await key(ed, 'KeyM');
  const tl = await ed.expectServer((t) => (t.document.markers ?? []).length === 1, 'the marker is stored');
  expect(tl.document.markers![0].frame).toBe(75);
});

test('TL-514 TL-515 TL-516 TL-517 B, V and H pick the tool and N toggles snapping', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await key(ed, 'KeyB');
  await expect(ed.testId('tool-blade')).toHaveAttribute('aria-pressed', 'true');
  await key(ed, 'KeyH');
  await expect(ed.testId('tool-hand')).toHaveAttribute('aria-pressed', 'true');
  await key(ed, 'KeyV');
  await expect(ed.testId('tool-select')).toHaveAttribute('aria-pressed', 'true');
  await expect(ed.testId('snap-toggle')).toHaveAttribute('aria-pressed', 'true');
  await key(ed, 'KeyN');
  await expect(ed.testId('snap-toggle')).toHaveAttribute('aria-pressed', 'false');
  await key(ed, 'KeyN');
  await expect(ed.testId('snap-toggle')).toHaveAttribute('aria-pressed', 'true');
});

test('TL-520 TL-521 comma and period nudge the selected clip by one frame, Shift by ten', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [
    { name: 'clipA.mp4', file: 'clipA', recordIn: 20, recordOut: 60 },
    { name: 'clipB.mp4', file: 'clipB', recordIn: 100, recordOut: 140 },
  ]));
  await ed.selectClipAt(40);
  await key(ed, 'Period');
  await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 21, 'period nudges +1');
  await key(ed, 'Comma');
  await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 20, 'comma nudges -1');
  await key(ed, 'Shift+Period');
  await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 30, 'Shift+period nudges +10');
  await key(ed, 'Shift+Comma');
  await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 20, 'Shift+comma nudges -10');
});

test('TL-518 Delete ripples (the next clip closes up), Backspace does the same; Shift+Delete lifts', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [
    { name: 'clipA.mp4', file: 'clipA', recordIn: 20, recordOut: 60 },
    { name: 'clipB.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
  ]));
  await ed.selectClipAt(40);
  await key(ed, 'Delete');
  await ed.expectServer((tl) => countItems(tl) === 1 && itemNamed(tl, 'clipB.mp4')?.recordIn === 20, 'Delete removed clipA and clipB closed up by its 40 frames');
  await key(ed, 'Control+KeyZ');
  await ed.expectServer((tl) => countItems(tl) === 2 && itemNamed(tl, 'clipB.mp4')?.recordIn === 60, 'undo restored both');
  await ed.selectClipAt(40);
  await key(ed, 'Shift+Backspace');
  await ed.expectServer((tl) => countItems(tl) === 1 && itemNamed(tl, 'clipB.mp4')?.recordIn === 60, 'Shift+Backspace lifted clipA and left the gap');
});

test('TL-519 Ctrl+B cuts at the playhead', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await seekTo(ed, 30);
  await key(ed, 'Control+KeyB');
  await ed.expectServer((tl) => countItems(tl) === 5, 'clipA was cut at 30');
});

test('TL-522 TL-525 TL-526 TL-527 TL-528 Escape deselects; Ctrl+A, A, Shift+A and Alt+A select', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await seekTo(ed, 100);
  await key(ed, 'Control+KeyA');
  expect(await selectedCount(ed), 'Ctrl+A selects everything').toBe(5);
  await key(ed, 'Escape');
  expect(await selectedCount(ed), 'Escape deselects').toBe(0);
  await key(ed, 'KeyA');
  expect(await selectedCount(ed), 'A selects forward from the playhead').toBe(4);
  await key(ed, 'Escape');
  await key(ed, 'Shift+KeyA');
  expect(await selectedCount(ed), 'Shift+A selects backward').toBe(3);
  await key(ed, 'Escape');
  await key(ed, 'Alt+KeyA');
  expect(await selectedCount(ed), 'Alt+A selects the targeted track').toBe(4);
});

test('TL-523 TL-524 Ctrl+Z undoes and Ctrl+Shift+Z and Ctrl+Y redo, each saved', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await ed.selectClipAt(30);
  await key(ed, 'Shift+Delete');
  await ed.expectServer((tl) => countItems(tl) === 3, 'lifted');
  await key(ed, 'Control+KeyZ');
  await ed.expectServer((tl) => countItems(tl) === 4, 'Ctrl+Z undid it');
  await key(ed, 'Control+Shift+KeyZ');
  await ed.expectServer((tl) => countItems(tl) === 3, 'Ctrl+Shift+Z redid it');
  await key(ed, 'Control+KeyZ');
  await ed.expectServer((tl) => countItems(tl) === 4, 'undone again');
  await key(ed, 'Control+KeyY');
  await ed.expectServer((tl) => countItems(tl) === 3, 'Ctrl+Y redid it');
});

test('TL-529 a rebound key works at once and the old key stops (Blade tool moved from B to F7)', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  const before = await api.json<{ keymap?: Record<string, string>; layouts?: unknown[] } | undefined>('GET', '/api/v1/editor/preferences', undefined, [200, 204, 404]);
  await ed.tap(ed.testId('open-keymap'));
  // T-007: the header keyboard icon opens the read-only cheatsheet; its "Edit shortcuts" opens the editor.
  const cheatsheet = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(cheatsheet.getByTestId('shortcuts-row-toolBlade')).toBeVisible();
  await ed.tap(cheatsheet.getByTestId('shortcuts-edit'));
  const dialog = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await ed.tap(dialog.getByTestId('keymap-change-toolBlade'));
  await ed.page.keyboard.press('F7');
  await ed.tap(dialog.getByTestId('keymap-save'));
  await expect(dialog).toBeHidden();
  try {
    await key(ed, 'F7');
    await expect(ed.testId('tool-blade')).toHaveAttribute('aria-pressed', 'true');
    await key(ed, 'KeyV');
    await key(ed, 'KeyB');
    await expect(ed.testId('tool-blade'), 'the old key no longer picks Blade').toHaveAttribute('aria-pressed', 'false');
  } finally {
    // Put the user's keymap back (preferences are per user, shared by every test of this run).
    await api.json('PUT', '/api/v1/editor/preferences', { keymap: before?.keymap ?? {}, layouts: before?.layouts ?? [] }, [200, 204]);
  }
});
