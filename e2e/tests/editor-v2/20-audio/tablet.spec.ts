import { test, expect } from '../../../fixtures/editor-v2';
import { audioTimeline, audioTrack } from '../../../fixtures/editor-v2/audio-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';

/** R08.3 / R08.5 on a tablet (1024x768, coarse pointer): same journeys as desktop, 44 px controls. */
test.describe('R08 audio tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R08.3 tablet: the dB field and reset are finger-sized and store gainDb', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 90 }] }]));
    expect(await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'the page sees a coarse pointer').toBe(true);
    const id = audioTrack(await ed.serverTimeline(), 'Voice')!.id;
    await ed.workspace('audio');
    const field = ed.testId(`mixer-fader-${id}-db`);
    await field.scrollIntoViewIfNeeded();
    expect((await field.boundingBox())!.height, 'dB field').toBeGreaterThanOrEqual(44);
    await field.tap();
    await field.fill('-4.5');
    await field.press('Enter');
    await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === -4.5, 'tablet: typed dB is stored');
    const reset = ed.testId(`mixer-fader-${id}-reset`);
    expect((await reset.boundingBox())!.width, 'reset button').toBeGreaterThanOrEqual(44);
    await reset.tap();
    await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === 0, 'tablet: reset stored 0 dB');
  });

  test('R08.5 tablet: Duck music under voice stores keyframes with a 44 px button', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [
      { name: 'Voice', clips: [{ name: 'voice-1', recordIn: 20, recordOut: 70 }] },
      { name: 'Music', clips: [{ name: 'music-1', recordIn: 0, recordOut: 150 }] },
    ]));
    await ed.workspace('audio');
    const duck = ed.testId('ducking-derive');
    await duck.scrollIntoViewIfNeeded();
    expect((await duck.boundingBox())!.height, 'duck button').toBeGreaterThanOrEqual(44);
    await duck.tap();
    await ed.expectServer((tl) => (audioTrack(tl, 'Music')?.gainDb?.keys ?? []).length > 0, 'tablet: ducking keyframes stored');
    await expect(ed.testId('ducking-state')).toHaveAttribute('data-on', 'true');
  });
});
