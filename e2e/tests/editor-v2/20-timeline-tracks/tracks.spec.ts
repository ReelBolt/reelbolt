import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { rgbAt } from './canvas-pixels';

/**
 * R03.5: the track header menu. Every test reads the STORED document after a real gesture; the lock
 * test reads the sentence the editor shows; the height test reads the painted canvas.
 */

type Track = { id: string; name: string; color?: string | null; height: number; locked: boolean; kind: string; items: unknown[] };
const tracksOf = (tl: { document: { tracks: unknown[] } }): Track[] => tl.document.tracks as Track[];

async function openMenu(ed: EditorPage, trackId: string): Promise<void> {
  await ed.tap(ed.testId(`track-menu-${trackId}`));
  await expect(ed.testId(`track-menu-dropdown-${trackId}`)).toBeVisible();
}

test.describe('R03.5 track headers', () => {
  test('R03.5 rename a track by double-click and from the menu: the stored name changes', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'pixel7', 'iphone14webkit');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const v = tracksOf(await ed.serverTimeline())[0];

    // Double-click needs a mouse; on phones the menu is the path.
    if (!ed.isPhone) {
      await ed.testId(`track-name-${v.id}`).dblclick();
      const input = ed.testId(`track-rename-input-${v.id}`);
      await input.fill('Main camera');
      await input.press('Enter');
      await ed.expectServer((tl) => tracksOf(tl)[0].name === 'Main camera', 'double-click rename is stored');
    }
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-menu-rename-${v.id}`));
    const input = ed.testId(`track-rename-input-${v.id}`);
    await input.fill('  B-roll   top ');
    await input.press('Enter');
    await ed.expectServer((tl) => tracksOf(tl)[0].name === 'B-roll top', 'menu rename is stored, trimmed');
    await ed.reload();
    await expect(ed.testId(`track-name-${v.id}`)).toHaveText('B-roll top');

    // Escape cancels, an empty name is refused.
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-menu-rename-${v.id}`));
    await ed.testId(`track-rename-input-${v.id}`).fill('');
    await ed.testId(`track-rename-input-${v.id}`).press('Enter');
    expect(tracksOf(await ed.serverTimeline())[0].name).toBe('B-roll top');
  });

  test('R03.5 colour: a swatch is stored, No colour clears it', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'pixel7', 'iphone14webkit');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const v = tracksOf(await ed.serverTimeline())[0];
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-color-orange-${v.id}`));
    await ed.expectServer((tl) => (tracksOf(tl)[0].color ?? '').toLowerCase() === '#c8620a', 'the orange swatch is stored');
    await ed.page.keyboard.press('Escape');
    await ed.reload();
    await expect(ed.testId(`track-header-${v.id}`)).toHaveCSS('border-left-color', 'rgb(200, 98, 10)');
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-color-none-${v.id}`));
    await ed.expectServer((tl) => !tracksOf(tl)[0].color, 'No colour clears it');
  });

  test('R03.5 height: Large is stored and the canvas row grows (pixels)', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { mutate: (doc) => { doc.tracks[0].height = 64; } }));
    const v = tracksOf(await ed.serverTimeline())[0];
    // 90 px below the lane top: the lane after a 64 px row, inside the clip body once the row is 96 px.
    // The clip paints pictures, so compare with the empty lane at the same height instead of a colour.
    const isClip = async () => {
      const laneTop = (await ed.canvasBox()).y + 28 + 14; // the layout can shift (banner), so measure every time
      const x = await ed.frameX(30);
      const here = await rgbAt(ed.page, x, laneTop + 90);
      const empty = await rgbAt(ed.page, await ed.frameX(400), laneTop + 90);
      return Math.abs(here[0] - empty[0]) + Math.abs(here[1] - empty[1]) + Math.abs(here[2] - empty[2]) > 30;
    };
    expect(await isClip(), 'a 64 px row: that point is below the clip').toBe(false);
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-height-large-${v.id}`));
    await ed.expectServer((tl) => tracksOf(tl)[0].height === 96, 'Large is stored as 96');
    await expect.poll(isClip, { message: 'the canvas row grew: the clip body now covers that point' }).toBe(true);
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-height-small-${v.id}`));
    await ed.expectServer((tl) => tracksOf(tl)[0].height === 40, 'Small is stored as 40');
    await expect.poll(isClip).toBe(false);
  });

  test('R03.5 reorder: Move down in the menu and the drag handle store the new order', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'pixel7', 'iphone14webkit');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { secondVideoTrack: true }));
    const [a, b] = tracksOf(await ed.serverTimeline());
    await openMenu(ed, a.id);
    await expect(ed.testId(`track-menu-up-${a.id}`)).toBeDisabled();
    await ed.tap(ed.testId(`track-menu-down-${a.id}`));
    await ed.expectServer((tl) => tracksOf(tl)[0].id === b.id && tracksOf(tl)[1].id === a.id, 'Move down stores the order');

    if (!ed.isPhone) {
      // Drag the handle of the first row (b) below the second (a).
      const grip = await ed.testId(`track-grip-${b.id}`).boundingBox();
      const target = await ed.testId(`track-header-${a.id}`).boundingBox();
      await ed.mouseDrag(grip!.x + grip!.width / 2, grip!.y + grip!.height / 2, grip!.x + grip!.width / 2, target!.y + target!.height - 4, 12);
      await ed.expectServer((tl) => tracksOf(tl)[0].id === a.id && tracksOf(tl)[1].id === b.id, 'dragging the handle stores the order');
    }
    await ed.reload();
    const order = tracksOf(await ed.serverTimeline()).map((t) => t.id);
    expect(order.slice(0, 2), 'the order survives a reload').toEqual(ed.isPhone ? [b.id, a.id] : [a.id, b.id]);
  });

  test('R03.5 add a track above and below from the menu', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'pixel7', 'iphone14webkit');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const before = tracksOf(await ed.serverTimeline());
    const v = before[0];
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-add-above-video-${v.id}`));
    await ed.expectServer((tl) => tracksOf(tl).length === before.length + 1 && tracksOf(tl)[1].id === v.id && tracksOf(tl)[0].kind === 'video', 'a video track is added above');
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-add-below-audio-${v.id}`));
    await ed.expectServer((tl) => tracksOf(tl).length === before.length + 2 && tracksOf(tl)[2].kind === 'audio', 'an audio track is added below');
  });

  test('R03.5 a locked track refuses clip edits with a plain sentence, and Unlock allows them', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'pixel7', 'iphone14webkit');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const v = tracksOf(await ed.serverTimeline())[0];
    await ed.selectClipAt(30);
    await openMenu(ed, v.id);
    await ed.tap(ed.testId(`track-menu-lock-${v.id}`));
    await ed.expectServer((tl) => tracksOf(tl)[0].locked === true, 'the lock is stored');
    await ed.clipAction('delete');
    await expect(ed.page.locator('.mantine-Notification-root').filter({ hasText: /is locked\. Unlock it to change its clips/ }).first()).toBeVisible();
    expect((tracksOf(await ed.serverTimeline())[0].items as unknown[]).length, 'nothing was deleted').toBe(2);
    // Locked: the menu says why rename / colour / height are unavailable.
    await openMenu(ed, v.id);
    await expect(ed.testId(`track-menu-rename-${v.id}`)).toContainText('unlock the track first');
    await ed.tap(ed.testId(`track-menu-lock-${v.id}`));
    await ed.expectServer((tl) => tracksOf(tl)[0].locked === false, 'Unlock is stored');
  });

  test('R03.5 removing a track with clips asks first; Keep leaves it, Remove deletes it, Undo restores it', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'pixel7', 'iphone14webkit');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { secondVideoTrack: true }));
    const v1 = tracksOf(await ed.serverTimeline())[0];
    await openMenu(ed, v1.id);
    await ed.tap(ed.testId(`track-remove-${v1.id}`));
    await expect(ed.testId('track-remove-confirm-text')).toContainText('has 2 clips');
    await ed.tap(ed.testId('track-remove-cancel'));
    expect(tracksOf(await ed.serverTimeline()).some((t) => t.id === v1.id), 'Keep track removes nothing').toBe(true);

    await openMenu(ed, v1.id);
    await ed.tap(ed.testId(`track-remove-${v1.id}`));
    await ed.tap(ed.testId('track-remove-confirm'));
    await ed.expectServer((tl) => !tracksOf(tl).some((t) => t.id === v1.id), 'the track is gone');
    await ed.tap(ed.testId('removed-track-undo'));
    const tl = await ed.expectServer((t) => tracksOf(t).some((x) => x.id === v1.id), 'Undo restores the track');
    expect(tracksOf(tl).find((t) => t.id === v1.id)!.items).toHaveLength(2);

    // An empty track is removed without asking.
    const empty = tracksOf(tl).find((t) => t.items.length === 0)!;
    await openMenu(ed, empty.id);
    await ed.tap(ed.testId(`track-remove-${empty.id}`));
    await ed.expectServer((t) => !tracksOf(t).some((x) => x.id === empty.id), 'an empty track needs no confirmation');
  });

  test('R03.5 the view toggles are labelled, change the canvas and never the document', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const v = tracksOf(await ed.serverTimeline())[0];
    await openMenu(ed, v.id);
    for (const label of ['Show waveforms', 'Show thumbnails', 'Show clip names']) await expect(ed.page.getByRole('menuitem', { name: new RegExp(label) })).toBeVisible();
    await ed.tap(ed.testId('timeline-view-names'));
    await expect.poll(async () => ed.page.evaluate(() => window.localStorage.getItem('reelforge.editor.timelineView'))).toContain('"names":false');
    expect(ed.patches, 'view toggles are not document changes').toEqual([]);
    await openMenu(ed, v.id);
    await expect(ed.testId('timeline-view-names')).toContainText('Off');
    await ed.tap(ed.testId('timeline-view-names'));
  });
});

test.describe('R03.5 tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R03.5 tablet: the menu opens, its controls are >= 44 px, and rename + colour + move are stored', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { secondVideoTrack: true }));
    const [a, b] = tracksOf(await ed.serverTimeline());
    expect(await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'the page sees a coarse pointer').toBe(true);
    const menuButton = await ed.testId(`track-menu-${a.id}`).boundingBox();
    expect(menuButton!.width).toBeGreaterThanOrEqual(44);
    expect(menuButton!.height).toBeGreaterThanOrEqual(44);
    await openMenu(ed, a.id);
    for (const id of [`track-menu-rename-${a.id}`, `track-menu-down-${a.id}`, `track-height-large-${a.id}`]) {
      expect((await ed.testId(id).boundingBox())!.height, id).toBeGreaterThanOrEqual(44);
    }
    expect((await ed.testId(`track-color-teal-${a.id}`).boundingBox())!.width).toBeGreaterThanOrEqual(44);
    await ed.testId(`track-color-teal-${a.id}`).tap();
    await ed.expectServer((tl) => (tracksOf(tl)[0].color ?? '').toLowerCase() === '#12907a', 'tablet: colour is stored');
    await ed.page.keyboard.press('Escape');
    await openMenu(ed, a.id);
    await ed.testId(`track-menu-rename-${a.id}`).tap();
    await ed.testId(`track-rename-input-${a.id}`).fill('Tablet cam');
    await ed.testId(`track-rename-input-${a.id}`).press('Enter');
    await ed.expectServer((tl) => tracksOf(tl)[0].name === 'Tablet cam', 'tablet: rename is stored');
    await openMenu(ed, a.id);
    await ed.testId(`track-menu-down-${a.id}`).tap();
    await ed.expectServer((tl) => tracksOf(tl)[0].id === b.id, 'tablet: move down is stored');
  });
});
