import type { Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Events lane (video events analysis): the free `events` analysis of clipA is stubbed ready (the detector is
 * not what is under test), the lane reads it through the real code path, and every outcome is read back from
 * the STORED timeline document through the API, never from the stub.
 *
 * clipA plays record frames 0..60 from source frame 0, so an event at second 1 is record frame 30 and one at
 * second 1.5 is frame 45.
 */
test.use({ serviceWorkers: 'block' });

const EVENTS = [
  { id: 'e0', type: 'hit', frame: 30, timeSec: 1, strength: 0.9, region: { x: 0.6, y: 0.4, w: 0.2, h: 0.2 }, confidence: 0.9 },
  { id: 'e1', type: 'beat', frame: 45, timeSec: 1.5, strength: 0.4, region: null, confidence: 0.8 },
];

async function stubEvents(page: Page, fileId: string): Promise<{ starts: number }> {
  const control = { starts: 0 };
  const status = {
    mediaRef: `file:${fileId}`,
    kinds: Object.fromEntries(['silence', 'shots', 'motion', 'transcript', 'beats', 'events'].map((k) => [k, k === 'events'
      ? { state: 'ready', reason: null, requestId: '00000000-0000-4000-8000-000000000abc', updatedAt: new Date().toISOString(), summary: { eventCount: 2, byType: { hit: 1, beat: 1 }, suppressed: 0 } }
      : { state: 'none', reason: null, requestId: null, updatedAt: null, summary: null }])),
  };
  await page.route(new RegExp(`/media/file(:|%3A)${fileId}/analysis(/events)?$`), async (route) => {
    const url = route.request().url();
    if (route.request().method() === 'POST') {
      control.starts += 1;
      return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ requestId: '00000000-0000-4000-8000-000000000abc', mediaRef: `file:${fileId}`, states: { events: { state: 'ready', reason: null } } }) });
    }
    if (url.endsWith('/events')) return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ fps: 30, frameCount: 900, events: EVENTS, suppressed: 0, truncated: false }) });
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(status) });
  });
  return control;
}

const clipsOf = (tl: Timeline, track = 0): Item[] => tl.document.tracks[track].items;
type Keyed = Item & { transform?: { scale?: { keys?: Array<{ frame: number; value: number }> | null } | null } | null };

async function showLane(ed: EditorPage): Promise<void> {
  await ed.page.getByTestId('events-lane-menu').click();
  await ed.page.getByTestId('events-lane-toggle').click();
  await ed.page.keyboard.press('Escape');
  await expect(ed.page.getByTestId('events-lane-header')).toBeVisible();
}

/** Opens "Act on this event" on the tick at `frame` with a real right click on the lane. */
async function rightClickTick(ed: EditorPage, frame: number): Promise<void> {
  const cv = await ed.canvasBox();
  // Ruler 28 px + marker lane 14 px, then the 26 px lane.
  await ed.page.mouse.click(await ed.frameX(frame), cv.y + 28 + 14 + 13, { button: 'right' });
  await expect(ed.page.getByTestId('event-actions-menu')).toBeVisible();
}

test.describe('desktop', () => {
  test.beforeEach(() => { runOn('desktop'); });

  test('the lane is off until asked for, remembers the choice, and offers the eight actions (no glow)', async ({ freshTimeline, openEditor, api, seed, page }) => {
    await stubEvents(page, seed.files.clipA);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.page.getByTestId('events-lane-header')).toHaveCount(0);
    await showLane(ed);
    await rightClickTick(ed, 30);
    for (const id of ['addSound', 'addTitle', 'zoom', 'pan', 'shake', 'flash', 'speedRamp', 'cut']) await expect(ed.page.getByTestId(`event-action-${id}`)).toBeVisible();
    await expect(ed.page.getByTestId('event-action-glow')).toHaveCount(0);
    await ed.page.keyboard.press('Escape');
    await ed.reload();
    await expect(ed.page.getByTestId('events-lane-header'), 'the lane choice is remembered for this timeline').toBeVisible();
  });

  test('Cut here splits the clip on the event frame as one undo step', async ({ freshTimeline, openEditor, api, seed, page }) => {
    await stubEvents(page, seed.files.clipA);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await showLane(ed);
    await rightClickTick(ed, 30);
    await ed.page.getByTestId('event-action-cut').click();
    const cut = await ed.expectServer((tl) => clipsOf(tl).length === 3, 'the clip is split');
    expect(clipsOf(cut).map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 30], [30, 60], [60, 120]]);
    await ed.undo();
    await ed.expectServer((tl) => clipsOf(tl).length === 2, 'one undo takes the whole cut back');
  });

  test('Zoom here writes scale keyframes on the event frame and one undo removes them', async ({ freshTimeline, openEditor, api, seed, page }) => {
    await stubEvents(page, seed.files.clipA);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await showLane(ed);
    await rightClickTick(ed, 30);
    await ed.page.getByTestId('event-action-zoom').click();
    const zoomed = await ed.expectServer((tl) => ((clipsOf(tl)[0] as Keyed).transform?.scale?.keys?.length ?? 0) >= 3, 'scale keys are stored');
    const keys = (clipsOf(zoomed)[0] as Keyed).transform?.scale?.keys ?? [];
    expect(keys.find((k) => k.frame === 30)?.value, 'the zoom lands on the event frame').toBeCloseTo(1.25, 3);
    await ed.undo();
    await ed.expectServer((tl) => ((clipsOf(tl)[0] as Keyed).transform?.scale?.keys?.length ?? 0) === 0, 'one undo removes every key');
  });

  test('the keyboard reaches the lane: E toggles it, Shift+E opens the menu for the nearest event, the playhead snaps to a tick', async ({ freshTimeline, openEditor, api, seed, page }) => {
    await stubEvents(page, seed.files.clipA);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.keyboard.press('e');
    await expect(ed.page.getByTestId('events-lane-header')).toBeVisible();
    await ed.seek(32);
    // seek() leaves the cursor in the timecode field, where Shift+E would only type a letter: hand the keyboard back to the editor.
    await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    // The lane reads the events only once it is shown, so the header can be up before the ticks are: Shift+E on an empty lane
    // only says "no events here". Ask again until the lane has its events (bounded), instead of racing the fetch.
    await expect(async () => {
      await ed.page.keyboard.press('Shift+E');
      await expect(ed.page.getByTestId('event-actions-title')).toContainText('00:00:01:00', { timeout: 1_500 });
    }, 'Shift+E opens the menu of the nearest event once the lane has loaded').toPass({ timeout: 30_000, intervals: [500, 1_000] });
    await ed.page.keyboard.press('Escape');
    await expect(ed.page.getByTestId('event-actions-menu'), 'Escape closes the menu').toBeHidden();
    // Scrubbing near a tick lands on it.
    const cv = await ed.canvasBox();
    // The ruler is the top 28 px of the canvas; its lower part clears the toolbar above it when a banner narrows the timeline.
    await ed.page.mouse.click(await ed.frameX(46), cv.y + 22);
    await expect(ed.testId('status-timecode')).toHaveText('00:00:01:15');
  });

  test('Find events asks for the free analysis only when a file has none', async ({ freshTimeline, openEditor, api, seed, page }) => {
    const control = await stubEvents(page, seed.files.clipA);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await showLane(ed);
    // clipB has no stubbed events; the menu offers the run and states the price before anything starts.
    await ed.page.getByTestId('events-lane-menu').click();
    await expect(ed.page.getByTestId('events-find')).toBeVisible();
    expect(control.starts, 'nothing ran without a click').toBe(0);
  });
});

test.describe('phone', () => {
  test.beforeEach(() => { runOn('pixel7'); });

  test('More, Events at the playhead: tap-only actions that run as one undo step', async ({ freshTimeline, openEditor, api, seed, page }) => {
    await stubEvents(page, seed.files.clipA);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.seek(30);
    // The action bar's More sheet belongs to a selected clip: pick the one under the playhead first.
    await ed.selectClipUnderPlayhead();
    await ed.tap(ed.page.getByTestId('phone-action-more'));
    await ed.tap(ed.page.getByTestId('phone-more-events'));
    await expect(ed.page.getByTestId('phone-events-current')).toContainText('00:00:01:00');
    await ed.tap(ed.page.getByTestId('phone-event-action-cut'));
    const cut = await ed.expectServer((tl) => clipsOf(tl).length === 3, 'the clip is split on the event');
    expect(clipsOf(cut)[0].recordOut).toBe(30);
  });
});
