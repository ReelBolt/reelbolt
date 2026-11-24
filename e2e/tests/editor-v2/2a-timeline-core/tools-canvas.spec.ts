import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A, sections 2.E (toolbar tools and menus), 2.F (track headers) and 2.G (canvas gestures).
 * Every test reads the stored document (or the visible selection/zoom) after a real gesture on the
 * device's own pointer: mouse on desktop, touch on the phones (WebKit cannot drag by touch, so its
 * drags are skipped and the tap variants run instead).
 */

const FOUR: ClipSpec[] = [
  { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
  { name: 'clipC.mp4', file: 'clipA', recordIn: 120, recordOut: 180 },
  { name: 'clipD.mp4', file: 'clipB', recordIn: 180, recordOut: 240 },
];
const TONE: ClipSpec[] = [{ name: 'tone.wav', file: 'tone', recordIn: 0, recordOut: 150 }];

/** "N clips selected": the Clip menu line on desktop and tablet, the strip description on a phone (EditorPage.selectedCount). */
const selectedCount = (ed: EditorPage): Promise<number> => ed.selectedCount();

async function drag(ed: EditorPage, x0: number, y0: number, x1: number, y1: number): Promise<void> {
  if (ed.isPhone) await ed.touchDrag(x0, y0, x1, y1, 16);
  else await ed.mouseDrag(x0, y0, x1, y1, 16);
}

test('TL-095..099 the Select menu: all, forward, backward, all on this track, deselect', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // phone: Select mode on the action bar picks clips one by one (30-phone/actions.spec); no all/forward/backward menu there
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR, { audioClips: TONE }));
  await ed.seek(100);
  const pick = async (id: string) => {
    await ed.tap(ed.testId('select-menu'));
    await ed.tap(ed.testId(id));
  };
  await pick('select-menu-all');
  expect(await selectedCount(ed), 'all: four clips and the tone').toBe(5);
  await pick('select-menu-none');
  expect(await selectedCount(ed), 'deselect').toBe(0);
  await pick('select-menu-forward');
  expect(await selectedCount(ed), 'forward from frame 100: clips ending after it (B, C, D, tone)').toBe(4);
  await pick('select-menu-backward');
  expect(await selectedCount(ed), 'backward from frame 100: clips starting before it (A, B, tone)').toBe(3);
  await pick('select-menu-none');
  await pick('select-menu-track');
  expect(await selectedCount(ed), 'all on the targeted track (V1): four clips').toBe(4);
});

test('TL-100 TL-101 TL-102 the Add menu stores a title and a colour card at the playhead', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.seek(90);
  if (ed.isPhone) {
    // The phone's action bar adds a title at the playhead ("Add text"); colour cards are desktop and tablet (suite-skips.md).
    await ed.tap(ed.testId('phone-action-add-text'));
    const phoneTitle = await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'titleClip')), 'the title is stored');
    expect(phoneTitle.document.tracks.flatMap((t) => t.items).find((i) => i.type === 'titleClip')!.recordIn, 'at the playhead').toBe(90);
    return;
  }
  await ed.openAddMenu();
  await ed.tap(ed.testId('add-title'));
  const withTitle = await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'titleClip')), 'the title is stored');
  const title = withTitle.document.tracks.flatMap((t) => t.items).find((i) => i.type === 'titleClip')!;
  expect([title.recordIn, title.recordOut - title.recordIn], 'at the playhead, three seconds long').toEqual([90, 90]);
  await ed.openAddMenu();
  await ed.tap(ed.testId('add-colour-card'));
  const withCard = await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'generatorClip')), 'the colour card is stored');
  const card = withCard.document.tracks.flatMap((t) => t.items).find((i) => i.type === 'generatorClip')!;
  expect(card.generatorType).toBe('color');
  expect(card.recordIn).toBe(90);
});

test('TL-092 snapping: on, a dragged clip lands exactly on its neighbour\'s edge; off, on the raw frame', async ({ freshTimeline, openEditor, api, seed }) => {
  skipSyntheticPointers();
  runOn('desktop', 'tablet'); // phone: clip drag is behind the gestures flag and Snapping lives in More (30-phone/touch.spec)
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 40 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 80, recordOut: 120 },
    ]),
  );
  const y = await ed.laneY(0);
  const dragB = async (deltaFrames: number) => {
    await ed.selectClipAt(100);
    const x = await ed.frameX(100);
    await drag(ed, x, y, x + deltaFrames * 2, y);
  };
  await expect(ed.testId('snap-toggle')).toHaveAttribute('aria-pressed', 'true');
  await dragB(-38); // raw landing: B.recordIn = 42, two frames from clipA's end
  await ed.expectServer((tl) => itemNamed(tl, 'clipB.mp4')?.recordIn !== 80, 'clipB moved');
  expect((await ed.serverTimeline()).document.tracks[0].items.find((i) => i.name === 'clipB.mp4')!.recordIn, 'snapped onto clipA\'s end').toBe(40);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((tl) => itemNamed(tl, 'clipB.mp4')?.recordIn === 80, 'undo puts it back');

  await ed.tap(ed.testId('snap-toggle'));
  await expect(ed.testId('snap-toggle')).toHaveAttribute('aria-pressed', 'false');
  await dragB(-38);
  const tl = await ed.expectServer((t) => itemNamed(t, 'clipB.mp4')?.recordIn !== 80, 'clipB moved with snapping off');
  expect(itemNamed(tl, 'clipB.mp4')!.recordIn, 'raw frame, not the edge').toBeGreaterThanOrEqual(41);
  expect(itemNamed(tl, 'clipB.mp4')!.recordIn).toBeLessThanOrEqual(43);
  expect(itemNamed(tl, 'clipB.mp4')!.recordIn).not.toBe(40);
});

test('TL-081 TL-130 the Blade tool cuts the clip where the canvas is clicked (a phone splits at the playhead from the action bar)', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  if (ed.isPhone) {
    await ed.seek(30);
    await ed.splitAtPlayhead();
    const cutTl = await ed.expectServer((t) => countItems(t) === 3, 'clipA was cut in two');
    expect(cutTl.document.tracks[0].items[0].recordOut, 'at the playhead').toBe(30);
    return;
  }
  await ed.tap(ed.testId('tool-blade'));
  await expect(ed.testId('tool-blade')).toHaveAttribute('aria-pressed', 'true');
  await ed.tapAt(await ed.frameX(30), await ed.laneY(0));
  const tl = await ed.expectServer((t) => countItems(t) === 3, 'clipA was cut in two');
  const cut = tl.document.tracks[0].items[0].recordOut;
  expect(Math.abs(cut - 30), 'at the clicked frame (snapping may move it a few frames)').toBeLessThanOrEqual(4);
  await expect.poll(() => ed.patches.at(-1)?.ops).toEqual(['blade']);
  await ed.tap(ed.testId('tool-select'));
  await expect(ed.testId('tool-select')).toHaveAttribute('aria-pressed', 'true');
  await expect(ed.testId('tool-blade')).toHaveAttribute('aria-pressed', 'false');
});

test('TL-082 TL-131 the Pan tool scrolls the timeline and writes nothing', async ({ freshTimeline, openEditor, api, seed }) => {
  skipSyntheticPointers();
  runOn('desktop', 'tablet'); // phone: a one-finger drag on the strip pans by default (30-phone/strip.spec)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.selectClipAt(30);
  const handleX = async () => (await ed.testId('trim-handle-out').boundingBox())!.x;
  const x0 = await handleX();
  await ed.tap(ed.testId('tool-hand'));
  await expect(ed.testId('tool-hand')).toHaveAttribute('aria-pressed', 'true');
  const y = await ed.laneY(0);
  const x = await ed.frameX(100);
  await drag(ed, x, y, x - 40, y);
  await expect.poll(handleX, { message: 'dragging left scrolls the timeline right: the clip end moved left' }).toBeLessThan(x0 - 20);
  expect(ed.patches, 'panning writes nothing').toEqual([]);
});

test('TL-120 TL-136 scrubbing the ruler moves the playhead and writes nothing (tap, and drag where the device can)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // phone: scrubbing is a pan of the strip and clip moves need the touch-drag mode (30-phone/strip.spec.ts, touch.spec.ts)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  // frameX() brings the canvas on screen again, which can move it: read the box after it, not before.
  const x60 = await ed.frameX(60);
  // On a short phone the pane is lower than the canvas: centering hides the ruler under the viewer, so put the ruler first.
  await ed.canvas.evaluate((el) => el.scrollIntoView({ block: 'start', inline: 'nearest' }));
  await ed.page.waitForTimeout(300);
  const cv = (await ed.canvas.boundingBox())!;
  await ed.tapAt(x60, cv.y + 10);
  await expect.poll(async () => frameOf(await ed.testId('status-timecode').textContent())).toBeGreaterThanOrEqual(58);
  await expect.poll(async () => frameOf(await ed.testId('status-timecode').textContent())).toBeLessThanOrEqual(62);
  if (test.info().project.name !== 'iphone14webkit') {
    await drag(ed, await ed.frameX(10), cv.y + 10, await ed.frameX(100), cv.y + 10);
    await expect.poll(async () => frameOf(await ed.testId('status-timecode').textContent())).toBeGreaterThan(90);
  }
  expect(ed.patches, 'scrubbing writes nothing').toEqual([]);
});

const frameOf = (tc: string | null): number => {
  const [h, m, s, f] = (tc ?? '0:0:0:0').split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * 30 + f;
};

test('TL-121 TL-124 a click selects a clip and a click on empty timeline deselects', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 }]));
  await ed.selectClipAt(30);
  expect(await selectedCount(ed)).toBe(1);
  if (ed.isPhone) return; // the phone keeps the selection until Done or the sheet closes; a tap on empty strip only moves the playhead
  await ed.tapAt(await ed.frameX(100), await ed.laneY(0));
  expect(await selectedCount(ed), 'empty space deselects').toBe(0);
});

test.describe('desktop pointer grammar', () => {
  test.beforeEach(() => {
    runOn('desktop');
  });

  test('TL-122 TL-123 Shift-click selects a range, Ctrl-click toggles one', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR));
    await ed.selectClipAt(30);
    await ed.selectClipAt(150, 0, ['Shift']);
    expect(await selectedCount(ed), 'range from clipA to clipC').toBe(3);
    await ed.selectClipAt(30);
    await ed.selectClipAt(150, 0, ['Control']);
    expect(await selectedCount(ed), 'toggle adds clipC').toBe(2);
    await ed.selectClipAt(150, 0, ['Control']);
    expect(await selectedCount(ed), 'toggle removes it again').toBe(1);
  });

  test('TL-125 a marquee drag from empty space selects what it crosses', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { audioClips: [] }));
    // Start in the empty audio lane to the right of the clips and sweep up and left over both clips.
    const x0 = await ed.frameX(150);
    const y0 = await ed.laneY(1);
    await ed.mouseDrag(x0, y0, await ed.frameX(10), await ed.laneY(0), 12);
    expect(await selectedCount(ed)).toBe(2);
    expect(ed.patches, 'selecting writes nothing').toEqual([]);
  });

  test('TL-132 TL-133 Ctrl+wheel zooms the timeline (not the page) and Shift+wheel scrolls it', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    const handleX = async () => (await ed.testId('trim-handle-out').boundingBox())!.x;
    const x0 = await handleX();
    const y = await ed.laneY(0);
    await ed.page.mouse.move(await ed.frameX(10), y);
    await ed.page.keyboard.down('Control');
    await ed.page.mouse.wheel(0, -300);
    await ed.page.keyboard.up('Control');
    await expect.poll(handleX, { message: 'Ctrl+wheel up zooms in' }).toBeGreaterThan(x0 + 20);
    expect(await ed.page.evaluate(() => window.visualViewport?.scale ?? 1), 'the browser page was not zoomed').toBe(1);
    const x1 = await handleX();
    await ed.page.keyboard.down('Shift');
    await ed.page.mouse.wheel(0, 120);
    await ed.page.keyboard.up('Shift');
    await expect.poll(handleX, { message: 'Shift+wheel scrolls sideways' }).toBeLessThan(x1 - 20);
  });
});

test('TL-126 dragging a clip moves it: one move, stored, survives reload', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // phone: scrubbing is a pan of the strip and clip moves need the touch-drag mode (30-phone/strip.spec.ts, touch.spec.ts)
  skipSyntheticPointers();
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 50 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 100, recordOut: 140 },
    ]),
  );
  await ed.selectClipAt(25);
  const y = await ed.laneY(0);
  const x = await ed.frameX(25);
  await drag(ed, x, y, x + 24, y); // 12 frames later
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4')?.recordIn ?? 0) > 5, 'clipA moved');
  expect(itemNamed(tl, 'clipA.mp4')!.recordIn).toBeGreaterThanOrEqual(10);
  expect(itemNamed(tl, 'clipA.mp4')!.recordIn).toBeLessThanOrEqual(14);
  await expect.poll(() => ed.patches.at(-1)?.ops, { message: 'one move op' }).toEqual(['move']);
  await ed.reload();
  expect(itemNamed(await ed.serverTimeline(), 'clipA.mp4')!.recordIn).toBeGreaterThanOrEqual(10);
});

test('TL-127 dragging a clip onto another video track moves it there', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // phone: scrubbing is a pan of the strip and clip moves need the touch-drag mode (30-phone/strip.spec.ts, touch.spec.ts)
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { secondVideoTrack: true }));
  await ed.selectClipAt(30);
  const [y0, y1, x] = [await ed.laneY(0), await ed.laneY(1), await ed.frameX(30)];
  await drag(ed, x, y0, x, y1);
  await ed.expectServer((tl) => !!itemNamed(tl, 'clipA.mp4', 1) && !itemNamed(tl, 'clipA.mp4', 0), 'clipA now sits on Video 2');
});

test('TL-134 a pointer cancel mid-drag drops the drag: nothing is written', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.selectClipAt(30);
  const y = await ed.laneY(0);
  const x = await ed.frameX(30);
  await ed.page.mouse.move(x, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x + 40, y, { steps: 6 });
  await ed.canvas.dispatchEvent('pointercancel', { pointerId: 1, pointerType: 'mouse' });
  await ed.page.mouse.up();
  await expect(ed.chip).toHaveText(/^\s*Saved/);
  expect(ed.patches, 'a cancelled drag writes nothing').toEqual([]);
  expect(itemNamed(await ed.serverTimeline(), 'clipA.mp4')!.recordIn).toBe(0);
});

test('TL-135 a clip on a locked track cannot be dragged', async ({ freshTimeline, openEditor, api, seed }) => {
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { mutate: (doc) => { doc.tracks[0].locked = true; } }));
  await ed.selectClipAt(30);
  const y = await ed.laneY(0);
  const x = await ed.frameX(30);
  await drag(ed, x, y, x + 30, y);
  await expect(ed.chip).toHaveText(/^\s*Saved/);
  expect(ed.patches).toEqual([]);
  expect(itemNamed(await ed.serverTimeline(), 'clipA.mp4')!.recordIn).toBe(0);
});

/* ------------------------------------------------------------------ track headers (2.F) */

test('TL-110 TL-111 +V and +A add tracks; TL-112 collapse is view only; TL-113 target; TL-115..118 lock, eye, mute, solo are stored', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no track headers or +V/+A buttons (x1-mobile-model verdict)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { audioClips: [{ name: 'tone.wav', file: 'tone', recordIn: 0, recordOut: 120 }] }));
  const doc0 = (await ed.serverTimeline()).document;
  const v = doc0.tracks.find((t) => t.kind === 'video')!;
  const a = doc0.tracks.find((t) => t.kind === 'audio')!;

  // Collapse: the header row shrinks, nothing is written, and after reload it is expanded again.
  const header = ed.testId(`track-header-${v.id}`);
  const h0 = (await header.boundingBox())!.height;
  await ed.tap(ed.page.getByRole('button', { name: 'Collapse track' }).first());
  await expect.poll(async () => (await header.boundingBox())!.height, { message: 'collapsing shrinks the track row' }).toBeLessThan(h0 - 10);
  expect(ed.patches, 'collapse is not a document change').toEqual([]);

  // Eye on the video track, mute / solo on the audio track, then Lock: each is one stored setTrackProperty.
  // (A locked track refuses every change but unlocking, by the engine's rule, so Lock comes last.)
  await ed.tap(ed.testId(`track-enable-${v.id}`));
  await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === v.id)?.enabled === false, 'the eye (disable) is stored');
  await ed.tap(ed.testId(`track-mute-${a.id}`));
  await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === a.id)?.mute === true, 'mute is stored');
  await ed.tap(ed.testId(`track-solo-${a.id}`));
  await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === a.id)?.solo === true, 'solo is stored');
  await ed.tap(ed.testId(`track-lock-${v.id}`));
  await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === v.id)?.locked === true, 'lock is stored');
  await ed.reload();
  const after = (await ed.serverTimeline()).document;
  expect([after.tracks.find((t) => t.id === v.id)?.locked, after.tracks.find((t) => t.id === a.id)?.mute]).toEqual([true, true]);
  await expect(ed.testId(`track-header-${v.id}`).locator('[aria-label="Collapse track"]'), 'collapse is not persisted').toBeVisible();
  await ed.tap(ed.testId(`track-lock-${v.id}`)); // unlock again so the video is editable
  await ed.tap(ed.testId(`track-enable-${v.id}`)); // and visible again

  // Target: untarget V1, target A1; a Split with nothing selected then cuts the audio clip, not the video.
  await ed.tap(ed.testId(`track-target-${v.id}`));
  await ed.tap(ed.testId(`track-target-${a.id}`));
  await expect(ed.testId(`track-target-${a.id}`)).toHaveAttribute('aria-pressed', 'true');
  await ed.seek(30);
  await ed.splitAtPlayhead();
  await ed.expectServer((tl) => tl.document.tracks.find((t) => t.kind === 'audio')!.items.length === 2 && tl.document.tracks[0].items.length === 2, 'only the targeted audio track was cut');

  // +V / +A
  await ed.tap(ed.testId('add-video-track'));
  await ed.tap(ed.testId('add-audio-track'));
  await ed.expectServer((tl) => tl.document.tracks.filter((t) => t.kind === 'video').length === 2 && tl.document.tracks.filter((t) => t.kind === 'audio').length === 2, '+V and +A are stored');
});

test('TL-114 removing a track says so and the toast\'s Undo brings the track and its clips back', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no track headers or +V/+A buttons (x1-mobile-model verdict)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { secondVideoTrack: true }));
  const doc0 = (await ed.serverTimeline()).document;
  const v1 = doc0.tracks[0];
  await ed.tap(ed.testId(`track-menu-${v1.id}`));
  await ed.tap(ed.testId(`track-remove-${v1.id}`));
  await ed.tap(ed.testId('track-remove-confirm')); // R03.5: a track with clips asks first
  await ed.expectServer((tl) => !tl.document.tracks.some((t) => t.id === v1.id), 'the track and its clips are gone');
  const toast = ed.page.locator('.mantine-Notification-root').filter({ hasText: /removed/i }).first();
  await expect(toast, 'the editor says the track was removed').toBeVisible();
  await ed.tap(ed.testId('removed-track-undo'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((x) => x.id === v1.id), 'Undo brings the track back');
  expect(tl.document.tracks.find((t) => t.id === v1.id)!.items.map((i) => i.name)).toEqual(['clipA.mp4', 'clipB.mp4']);
});
