import type { Locator } from '@playwright/test';
import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 2 T-007: place from media (R03.6), titles and colour cards (R10.5), freeze frame (R07.5),
 * colour card editing (R07.6). Every assertion reads the stored document.
 */

const ref = (id: string) => `file:${id}`;
const tile = (ed: EditorPage, r: string): Locator => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${r}"]`).first();
const videoItems = (tl: Timeline) => tl.document.tracks.filter((t) => t.kind === 'video').flatMap((t) => t.items);
const allItems = (tl: Timeline) => tl.document.tracks.flatMap((t) => t.items);

async function pickSource(ed: EditorPage, fileId: string): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  await ed.tap(tile(ed, ref(fileId)));
  await expect(ed.testId('source-viewer')).toBeVisible();
  await ed.testId('media-insert-length').waitFor();
}

async function seekSource(ed: EditorPage, seconds: number): Promise<void> {
  const video = ed.testId('source-viewer-video');
  await video.evaluate((v: HTMLVideoElement, t: number) => new Promise<void>((resolve) => { v.addEventListener('seeked', () => resolve(), { once: true }); v.currentTime = t; }), seconds);
}

test('R03.6 Overwrite lands exactly at the playhead, says where, and Undo reverts it', async ({ freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  const before = await ed.serverTimeline();
  await ed.seek(30);
  await pickSource(ed, seed.files.clipB);
  await ed.tap(ed.testId('media-overwrite'));
  const tl = await ed.expectServer((t) => videoItems(t).some((i) => i.recordIn === 30 && i.recordOut === 270), 'clipB is overwritten in at frame 30 for its 8 s');
  const first = videoItems(tl).find((i) => i.recordIn === 0)!;
  expect(first.recordOut, 'clipA is cut back to the playhead').toBe(30);
  await expect(ed.page.getByText(/Placed on .* at 00:00:01:00/)).toBeVisible();
  await ed.tap(ed.testId('landed-undo'));
  await ed.expectServer((t) => JSON.stringify(videoItems(t).map((i) => [i.recordIn, i.recordOut])) === JSON.stringify(videoItems(before).map((i) => [i.recordIn, i.recordOut])), 'Undo restores the video track');
  void SEEDED_LAYOUT;
});

test('R03.6 Insert pushes later clips; Add to end appends; Place on top never touches clips', async ({ freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(150);
  await pickSource(ed, seed.files.clipB);
  await ed.tap(ed.testId('media-insert-at-playhead'));
  let tl = await ed.expectServer((t) => videoItems(t).some((i) => i.recordIn === 150 && i.recordOut === 390), 'inserted at the cut at 150');
  expect(videoItems(tl).some((i) => i.recordIn === 390 && i.recordOut === 540), 'the old clipB was pushed later').toBe(true);

  await ed.tap(ed.testId('media-append'));
  tl = await ed.expectServer((t) => videoItems(t).some((i) => i.recordIn === 840), 'added to the end of the track');

  const count = allItems(tl).length;
  await ed.tap(ed.testId('media-place-on-top'));
  tl = await ed.expectServer((t) => allItems(t).length > count, 'placed on top');
  const placed = tl.document.tracks.filter((t) => t.kind === 'video').flatMap((t, i) => t.items.map((it) => ({ i, it }))).find(({ it }) => it.recordIn === 150 && it.recordOut === 390 && it.name === 'clipB.mp4' && videoItems(tl).filter((x) => x.recordIn === 150).length > 1);
  expect(placed, 'a second clip at the playhead on its own track').toBeTruthy();
});

test('R03.6 three-point edit: the source marks set the range, the playhead sets where', async ({ freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(90);
  await pickSource(ed, seed.files.clipA);
  await seekSource(ed, 2);
  await ed.tap(ed.testId('mark-in'));
  await seekSource(ed, 5);
  await ed.tap(ed.testId('mark-out'));
  await ed.tap(ed.testId('media-overwrite'));
  const tl = await ed.expectServer((t) => videoItems(t).some((i) => i.recordIn === 90 && i.recordOut === 180), 'the marked range is placed at the playhead');
  const clip = videoItems(tl).find((i) => i.recordIn === 90) as { sourceIn: number };
  expect(clip.sourceIn, 'from the In mark').toBe(60);
});

test.describe('titles and colour cards', () => {
  test('R10.5 a title with each template stores template, text and length, and says where it landed', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    const templates = ['plain', 'lowerThird', 'boxed', 'outline', 'card'] as const;
    for (const [n, template] of templates.entries()) {
      await ed.seek(30 + n * 100);
      await ed.openAddMenu();
      await ed.tap(ed.testId('add-title-options'));
      await expect(ed.testId('add-title-dialog')).toBeVisible();
      await ed.tap(ed.page.getByTestId(`title-template-${template}`));
      await expect(ed.testId('title-preview')).toHaveAttribute('data-template', template);
      await ed.testId('title-text').fill(`Hello ${template}`);
      await ed.testId('title-seconds').fill('2');
      await ed.tap(ed.testId('title-add'));
      await ed.expectServer((t) => allItems(t).some((i) => i.type === 'titleClip' && (i as { text?: string }).text === `Hello ${template}` && ((i as { template?: string | null }).template ?? 'plain') === template && i.recordIn === 30 + n * 100 && i.recordOut - i.recordIn === 60), `a ${template} title is stored`);
      await expect(ed.page.getByText(/Title added on .* at 00:/).first()).toBeVisible();
    }
  });

  test('R10.5 a colour card stores the chosen colour; R07.6 the inspector swatches change it', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.seek(30);
    await ed.openAddMenu();
    await ed.tap(ed.testId('add-colour-card-options'));
    await expect(ed.testId('add-card-dialog')).toBeVisible();
    await expect(ed.page.getByRole('radio', { name: 'bars' })).toHaveCount(0);
    await ed.tap(ed.testId('card-swatch-blue'));
    await ed.tap(ed.testId('card-add'));
    const colourOf = (t: Timeline) => (allItems(t).find((i) => i.type === 'generatorClip') as { generatorConfig?: { color?: string } } | undefined)?.generatorConfig?.color;
    await ed.expectServer((t) => colourOf(t) === '#1a73e8', 'the blue card is stored');
    await ed.openInspector();
    await ed.tap(ed.testId('inspector-generator-swatch-red'));
    await ed.expectServer((t) => colourOf(t) === '#d93025', 'the red swatch in the inspector is stored');
  });
});

test('R07.5 Freeze frame here splits the clip and holds 2 s, silent, and one undo reverts it all', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  const before = await ed.serverTimeline();
  await ed.seek(60);
  await ed.selectClipAt(60, 0);
  await ed.clipAction('freeze-frame');
  const tl = await ed.expectServer((t) => videoItems(t).some((i) => (i as { hold?: boolean }).hold === true), 'a hold clip is stored');
  const hold = videoItems(tl).find((i) => (i as { hold?: boolean }).hold === true)!;
  expect([hold.recordIn, hold.recordOut], 'two seconds at the playhead').toEqual([60, 120]);
  expect(videoItems(tl).filter((i) => i.recordIn < 60).map((i) => i.recordOut)).toContain(60);
  expect(videoItems(tl).some((i) => i.recordIn === 120 && i.recordOut === 210), 'the rest of clipA resumes after the hold').toBe(true);
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((t) => videoItems(t).length === videoItems(before).length && !videoItems(t).some((i) => (i as { hold?: boolean }).hold === true), 'one undo reverts split, hold and shift');
});

test.describe('tablet 1024x768 with touch', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R03.6 the place buttons are 44 px targets and work by touch', async ({ freshTimeline, openEditor, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('blank'));
    await pickSource(ed, seed.files.clipB);
    for (const id of ['media-insert-at-playhead', 'media-overwrite', 'media-append', 'media-place-on-top']) {
      const box = await ed.testId(id).boundingBox();
      expect(box?.height ?? 0, `${id} is a 44 px target`).toBeGreaterThanOrEqual(43.5);
    }
    await ed.testId('media-insert-at-playhead').tap();
    await ed.expectServer((t) => videoItems(t).some((i) => i.recordIn === 0 && i.recordOut === 240), 'inserted by touch');
  });
});

test('R03.6 R10.5 on a phone: Add to end works by touch (the Add-menu dialogs are not reachable on a phone; the timeline toolbar is hidden there)', async ({ freshTimeline, openEditor, seed }) => {
  runOn('pixel7');
  const ed = await openEditor(await freshTimeline('seeded'));
  // The Add media sheet (RM.8): tap the tile to preview it, then Add to end.
  await ed.workspace('media');
  await ed.tap(ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${ref(seed.files.clipB)}"]`).first());
  await expect(ed.testId('source-viewer')).toBeVisible();
  await ed.tap(ed.testId('preview-append'));
  await ed.expectServer((t) => videoItems(t).some((i) => i.recordIn === 600), 'added to the end by touch');
});
