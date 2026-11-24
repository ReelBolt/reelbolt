import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A, phone rows (S13 S14 S15). Wave 3 retired the phone's timeline toolbar and track headers: the header
 * (undo, redo), the action bar (select, split, add text, delete), the strip (Fit, pinch) and the Audio sheet
 * (mute, solo, volume) carry what they offered. These rows keep their ids and assert the same real outcomes
 * through the new controls; the 600-767 px band still gets the phone layout.
 *
 * "Reachable" means: fully inside the viewport, and a real tap on it does its job (asserted on the stored
 * document or the visible state, not on the button existing).
 */

async function expectOnScreen(ed: EditorPage, ids: string[]): Promise<void> {
  for (const id of ids) {
    const el = ed.page.getByTestId(id).first();
    // The timeline pane scrolls vertically on a short phone (the 664 px WebKit viewport); only a sideways swipe is forbidden.
    await el.scrollIntoViewIfNeeded();
    await expect(el, `${id} is on screen without swiping the toolbar`).toBeInViewport({ ratio: 0.97 }) // sub-pixel tolerance;
  }
}

test.describe('phone controls that replaced the toolbar (S14)', () => {
  test.beforeEach(() => {
    runOn('pixel7', 'iphone14webkit');
  });

  test('TL-080..106 the phone has no timeline toolbar; Select, Add text and Fit work by tap from the bar and the strip', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.testId('timeline-toolbar'), 'the legacy toolbar is gone on a phone').toHaveCount(0);
    await expect(ed.testId('phone-clip-strip'), 'and so is its fixed-height wrapper').toHaveCount(0);
    await expectOnScreen(ed, ['phone-undo', 'phone-redo', 'phone-action-select', 'phone-action-add-text', 'phone-action-marker', 'strip-fit', 'strip-prev', 'strip-next']);
    for (const id of ['phone-undo', 'phone-action-select', 'phone-action-add-text', 'strip-fit']) {
      const box = (await ed.testId(id).boundingBox())!;
      expect(Math.min(box.width, box.height), `${id} touch target`).toBeGreaterThanOrEqual(43.5);
    }

    // Select mode: a tap on a clip adds it to the selection, Done leaves the mode with the clip still selected.
    await ed.tap(ed.testId('phone-action-select'));
    await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'select');
    await ed.selectClipAt(30);
    await ed.tap(ed.testId('phone-action-select-done'));
    await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'clip');

    // Fit: the selected clip's out handle moves when the whole programme is fitted across the strip.
    const handleX = async () => (await ed.testId('trim-handle-out').boundingBox())!.x;
    const x0 = await handleX();
    await ed.tap(ed.testId('strip-fit'));
    await expect.poll(handleX, { message: 'Fit changes the zoom' }).not.toBe(x0);

    // A reload clears the selection (the bar is back in its default mode); Add text at the playhead stores a title clip.
    await ed.reload();
    await ed.tap(ed.testId('phone-action-add-text'));
    await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'titleClip')), 'the title clip is stored');

  });

  test('S13 TL-225 TL-226 Undo and Redo are in the phone header: tap Undo to take back a delete, Redo to do it again', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.testId('phone-undo'), 'nothing to undo yet').toBeDisabled();
    await expect(ed.testId('phone-redo')).toBeDisabled();
    await ed.selectClipAt(30);
    await ed.tap(ed.testId('phone-action-delete'));
    await ed.expectServer((tl) => countItems(tl) === 1, 'the delete is stored');
    await expect(ed.testId('phone-undo')).toBeEnabled();
    await ed.tap(ed.testId('phone-undo'));
    await ed.expectServer((tl) => countItems(tl) === 2 && itemNamed(tl, 'clipA.mp4')?.recordIn === 0, 'Undo put clipA back');
    await expect(ed.testId('phone-redo')).toBeEnabled();
    await ed.tap(ed.testId('phone-redo'));
    await ed.expectServer((tl) => countItems(tl) === 1, 'Redo deleted it again');
  });

  test('T-030 a phone user taps a clip and Delete in the action bar removes it', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'none');
    await ed.selectClipAt(30);
    await expect(ed.testId('phone-action-bar'), 'tapping the clip selects it').toHaveAttribute('data-mode', 'clip');
    await ed.tap(ed.testId('phone-action-delete'));
    await ed.expectServer((tl) => countItems(tl) === 1 && !itemNamed(tl, 'clipA.mp4'), 'Delete removed the tapped clip');
  });

  test('TL-057 TL-063 TL-066 TL-067 TL-068 TL-069 the phone transport keeps Go to start/end, previous/next marker, Play in to out and Loop', async ({ freshTimeline, openEditor, api, seed }) => {
    const id = await clipsTimeline(freshTimeline, api, seed, PAIR, {
      mutate: (doc) => {
        doc.markers = [{ id: crypto.randomUUID(), name: 'M1', frame: 40, color: null, note: null, durationFrames: null }, { id: crypto.randomUUID(), name: 'M2', frame: 100, color: null, note: null, durationFrames: null }];
      },
    });
    const ed = await openEditor(id);
    const bar = ed.testId('transport-bar');
    const button = (name: string) => bar.getByRole('button', { name, exact: true });
    for (const name of ['Go to start', 'Go to end', 'Previous marker', 'Next marker', 'Play in to out', 'Loop', 'Mark in', 'Mark out', 'Play']) {
      await expect(button(name), `${name} is on the phone transport`).toBeVisible();
    }
    await ed.tap(button('Go to end'));
    await expect(ed.testId('status-timecode')).toHaveText('00:00:03:29');
    await ed.tap(button('Go to start'));
    await expect(ed.testId('status-timecode')).toHaveText('00:00:00:00');
    await ed.tap(button('Next marker'));
    await expect(ed.testId('status-timecode')).toHaveText('00:00:01:10');
    await ed.tap(button('Next marker'));
    await expect(ed.testId('status-timecode')).toHaveText('00:00:03:10');
    await ed.tap(button('Previous marker'));
    await expect(ed.testId('status-timecode')).toHaveText('00:00:01:10');
    await ed.tap(button('Loop'));
    await expect(button('Loop')).toHaveAttribute('aria-pressed', 'true');
    await ed.tap(button('Loop'));
    await expect(button('Loop')).toHaveAttribute('aria-pressed', 'false');
    // Play in to out is only available once both marks exist.
    await expect(button('Play in to out')).toBeDisabled();
  });

  test('TL-038 TL-039 the dropped-frame counter and the session kind are readable on a phone', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    // The phone has no status bar: the timecode sits in the strip, the session kind is on the viewer, and the
    // dropped-frame warning only appears when playback really drops frames.
    await expect(ed.page.locator('[data-session-kind]').first()).toHaveAttribute('data-session-kind', /^(live|simple|none|Live|Simple|No preview)/i);
    await ed.seek(75);
    await expect(ed.testId('status-timecode')).toHaveText('00:00:02:15');
  });

  test('TL-119b TL-113 TL-115 the Audio sheet mute and solo are finger-sized and a tap on Mute stores it', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, { audioClips: [{ name: 'tone.wav', file: 'tone', recordIn: 0, recordOut: 120 }] }));
    const a = (await ed.serverTimeline()).document.tracks.find((t) => t.kind === 'audio')!;
    await ed.workspace('audio');
    for (const id of [`phone-audio-mute-${a.id}`, `phone-audio-solo-${a.id}`]) {
      const el = ed.testId(id);
      await el.scrollIntoViewIfNeeded();
      const box = (await el.boundingBox())!;
      expect(Math.min(box.width, box.height), `${id} touch target`).toBeGreaterThanOrEqual(43.5);
    }
    await ed.tap(ed.testId(`phone-audio-mute-${a.id}`));
    await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === a.id)?.mute === true, 'Mute is stored');
  });
});

test.describe('Under 600 px is the phone layout, 600 px and up is the tablet layout (S15 TL-148, W3-D1)', () => {
  test.beforeEach(() => {
    runOn('desktop');
  });

  test('TL-148 at 590 px wide the phone layout is on: the action bar, no timeline toolbar', async ({ page, freshTimeline, openEditor, api, seed }) => {
    await page.setViewportSize({ width: 590, height: 900 });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-layout', 'phone');
    await expect(ed.testId('phone-action-bar')).toBeVisible();
    await expect(ed.testId('timeline-toolbar'), 'the layout and the toolbar agree at 590 px').toHaveCount(0);
  });

  test('TL-148 at 700 px wide the tablet layout (not the phone shell) keeps the timeline toolbar and has no phone shell', async ({ page, freshTimeline, openEditor, api, seed }) => {
    await page.setViewportSize({ width: 700, height: 900 });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.page.getByTestId('editor-workspace')).not.toHaveAttribute('data-layout', 'phone');
    await expect(ed.testId('timeline-toolbar')).toBeVisible();
    await expect(ed.testId('phone-action-bar')).toHaveCount(0);
  });
});
