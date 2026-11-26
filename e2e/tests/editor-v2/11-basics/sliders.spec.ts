import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 11 basics: sliders move live (remediation-plan.md W0.6 "sliders update live (`onChange` preview,
 * `onChangeEnd` commit)"; report.md §5 UI2-02, verdict rows P5-MIX-01/02; T-031 item 4).
 *
 * The audit saw the thumb and its label frozen for the whole drag and jumping on release, by mouse (d3, d4)
 * and by real touch (m2, m3). The row: mid-drag the thumb already shows the new value, and the release writes
 * the value once (one op, so one undo step).
 */
test('UI2-02 the mixer fader moves while it is dragged and writes once on release', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7');
  knownFailure('UI2-02');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.workspace('audio');
  const audio = (await ed.serverTimeline()).document.tracks.find((t) => t.kind === 'audio')!;
  // The phone's Audio sheet has its own volume slider per track (same drag, same one write on release).
  const fader = ed.testId(ed.isPhone ? `phone-audio-gain-${audio.id}` : `mixer-fader-${audio.id}`);
  const thumb = fader.getByRole('slider').or(ed.page.locator(`[data-testid="mixer-fader-${audio.id}"][role="slider"]`)).first();
  await thumb.scrollIntoViewIfNeeded();
  const start = Number(await thumb.getAttribute('aria-valuenow'));
  const box = (await thumb.boundingBox())!;
  const track = (await fader.boundingBox())!;
  const x0 = box.x + box.width / 2;
  const y0 = box.y + box.height / 2;
  // Drag left, well inside the slider (0 dB sits near the right end of [-60, 12]).
  const x1 = Math.max(track.x + 8, x0 - track.width * 0.4);
  const sent = ed.patches.length;
  const steps = 10;

  let midDrag: number;
  if (ed.isPhone) {
    const cdp = await ed.page.context().newCDPSession(ed.page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: x0, y: y0, id: 1 }] });
    for (let i = 1; i <= steps; i++) {
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: x0 + ((x1 - x0) * i) / steps, y: y0, id: 1 }] });
      await ed.page.waitForTimeout(16);
    }
    await ed.page.waitForTimeout(300);
    midDrag = Number(await thumb.getAttribute('aria-valuenow'));
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  } else {
    await ed.page.mouse.move(x0, y0);
    await ed.page.mouse.down();
    await ed.page.mouse.move(x1, y0, { steps });
    await ed.page.waitForTimeout(300);
    midDrag = Number(await thumb.getAttribute('aria-valuenow'));
    await ed.page.mouse.up();
  }
  expect(midDrag, `mid-drag the fader already shows the dragged value (start ${start} dB)`).toBeLessThan(start - 5);

  const stored = await ed.expectServer((tl) => {
    const g = tl.document.tracks.find((t) => t.id === audio.id)!.gainDb as { value?: number } | number | undefined;
    const v = typeof g === 'number' ? g : g?.value;
    return typeof v === 'number' && v < start - 5;
  }, 'the released fader value is stored');
  const g = stored.document.tracks.find((t) => t.id === audio.id)!.gainDb as { value?: number } | number;
  const value = typeof g === 'number' ? g : g.value!;
  await expect(thumb, 'after release the thumb shows the stored value').toHaveAttribute('aria-valuenow', String(value));
  await ed.page.waitForTimeout(1500);
  const ops = ed.patches.slice(sent).flatMap((p) => p.ops);
  expect(ops, `one op for the whole drag: ${ed.patchSummary()}`).toHaveLength(1);
});
