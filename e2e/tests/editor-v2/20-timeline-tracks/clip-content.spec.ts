import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { describeRect } from './canvas-pixels';

/**
 * R03.4 / VF11: clip content painted on the timeline canvas. The thumbnails and the waveform come
 * from derivatives the media worker makes after the editor opens the timeline, so each check polls
 * the canvas pixels until the art arrives.
 */

test.describe('R03.4 clip content', () => {
  test('R03.4 a seeded video clip shows a poster / filmstrip (not a flat colour) and the Show thumbnails toggle removes it', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 240 }], { mutate: (doc) => { doc.tracks[0].height = 96; } }));
    // The art area: under the 18 px label band, inside the clip body (lane top + 4 + 18 .. + 88).
    // Measured fresh each time: the layout can shift (the early-access banner) while the page settles.
    const rectNow = async () => ({ x: await ed.frameX(8), y: (await ed.canvasBox()).y + 42 + 4 + 18 + 4, width: 360, height: 40 });
    const coloursNow = async () => (await describeRect(ed.page, await rectNow())).colours;
    await expect.poll(coloursNow, { timeout: 90_000, message: 'thumbnails arrive: the clip body is varied, not one colour' }).toBeGreaterThan(40);

    await ed.tap(ed.testId(`track-menu-${(await ed.serverTimeline()).document.tracks[0].id}`));
    await ed.tap(ed.testId('timeline-view-thumbnails'));
    await expect.poll(coloursNow, { message: 'thumbnails off: back to the flat clip colour' }).toBeLessThan(12);
    await ed.tap(ed.testId(`track-menu-${(await ed.serverTimeline()).document.tracks[0].id}`));
    await ed.tap(ed.testId('timeline-view-thumbnails'));
  });

  test('R03.4 an audio clip paints a waveform that the Show waveforms toggle removes', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, {
      audioClips: [{ name: 'tone.wav', file: 'tone', recordIn: 0, recordOut: 120 }],
      mutate: (doc) => { for (const t of doc.tracks) t.height = 96; },
    }));
    const audio = (await ed.serverTimeline()).document.tracks.find((t) => t.kind === 'audio')!;
    // Measured fresh each time: the layout can shift (the early-access banner) while the page settles.
    const rectNow = async () => ({ x: await ed.frameX(8), y: (await ed.canvasBox()).y + 42 + 96 + 4 + 18 + 4, width: 200, height: 40 });
    const brightNow = async () => (await describeRect(ed.page, await rectNow())).bright;
    await expect.poll(brightNow, { timeout: 90_000, message: 'the waveform arrives: bright peaks on the audio clip' }).toBeGreaterThan(0.02);
    await ed.tap(ed.testId(`track-menu-${audio.id}`));
    await ed.tap(ed.testId('timeline-view-waveforms'));
    await expect.poll(brightNow, { message: 'waveforms off: no peaks' }).toBeLessThan(0.005);
    await ed.tap(ed.testId(`track-menu-${audio.id}`));
    await ed.tap(ed.testId('timeline-view-waveforms'));
  });

  test('R03.4 a clip trimmed inside its source is hatched on the edge that has unused media behind it', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    // clipA is a few seconds long; sourceIn 24 frames in means the left edge has a handle.
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60, extra: { sourceIn: 24 } }], { mutate: (doc) => { doc.tracks[0].height = 96; } }));
    // Pictures off, so the hatch is the only light thing on the clip body.
    await ed.page.evaluate(() => window.localStorage.setItem('reelforge.editor.timelineView', JSON.stringify({ waveforms: false, thumbnails: false, names: true })));
    await ed.reload();
    const rectAt = async (dx: number) => ({ x: (await ed.frameX(0)) + dx, y: (await ed.canvasBox()).y + 42 + 4 + 18, width: 12, height: 60 });
    const edgeColours = async () => (await describeRect(ed.page, await rectAt(1))).colours;
    await expect.poll(edgeColours, { timeout: 90_000, message: 'hatch lines appear on the trimmed edge once the source length is known' }).toBeGreaterThan(5);
    expect((await describeRect(ed.page, await rectAt(30))).colours, 'the clip body away from the edges is one flat colour').toBeLessThan(4);
  });
});
