import { test, expect } from '../../../fixtures/editor-v2';
import type { EditorPage } from '../../../fixtures/editor-v2/editor-page';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { audioTimeline } from '../../../fixtures/editor-v2/audio-lane';

/**
 * R08.6 (Wave 4): the expert audio tools are heard in the viewer. Advanced audio is hidden on a phone
 * (D3, W0.7), so these rows run on desktop only. The level is read from the preview graph's live peak
 * telemetry (`window.__rbAudioGraph.meters()`, published only when `__RB_TEST_HOOKS__` is set before
 * load); no OfflineAudioContext, no render.
 */

type Hook = { meters(): { master: Float32Array[] } };

async function enableHook(page: import('@playwright/test').Page): Promise<void> {
  await page.addInitScript(() => {
    (window as unknown as { __RB_TEST_HOOKS__: boolean }).__RB_TEST_HOOKS__ = true;
  });
}

/** Plays for a moment and returns the loudest master sample seen, in dBFS. */
async function playedPeakDb(ed: EditorPage): Promise<number> {
  await ed.tap(ed.testId('transport-play'));
  await expect(ed.testId('transport-play')).toHaveAttribute('aria-label', 'Pause');
  const peak = await ed.page.evaluate(async () => {
    const hook = (window as unknown as { __rbAudioGraph?: Hook }).__rbAudioGraph;
    if (!hook) return -1;
    let max = 0;
    for (let i = 0; i < 20; i++) {
      await new Promise((r) => setTimeout(r, 100));
      for (const ch of hook.meters().master) for (const v of ch) max = Math.max(max, Math.abs(v));
    }
    return max;
  });
  await ed.tap(ed.testId('transport-play'));
  expect(peak, 'the preview graph published telemetry').toBeGreaterThanOrEqual(0);
  return 20 * Math.log10(Math.max(peak, 1e-9));
}

test('R08.6 compressor on a track is audible in preview', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  await enableHook(page);
  const id = await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'tone', recordIn: 0, recordOut: 120 }] }]);
  const bypassed = await playedPeakDb(await openEditor(id));
  expect(bypassed, 'the tone is loud enough to compress').toBeGreaterThan(-30);

  await api.putDocument(seed.projectId, id, (doc) => {
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    a.dynamics = { compressor: { thresholdDb: -40, ratio: 20, attackMs: 1, releaseMs: 100, makeupDb: 0 } };
  });
  const ed = await openEditor(id);
  const stored = (await ed.serverTimeline()).document.tracks.find((t) => t.kind === 'audio') as { dynamics?: { compressor?: { ratio?: number } } };
  expect(stored.dynamics?.compressor?.ratio, 'the compressor is stored on the track').toBe(20);
  const compressed = await playedPeakDb(ed);
  expect(bypassed - compressed, `a loud tone drops by a clear margin (bypassed ${bypassed.toFixed(1)} dB, compressed ${compressed.toFixed(1)} dB)`).toBeGreaterThan(4);
  expect(bypassed - compressed, 'but stays audible').toBeLessThan(40);
});

test('R08.6 bus mute silences its tracks', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  await enableHook(page);
  const id = await audioTimeline(freshTimeline, api, seed, [{ name: 'Music', clips: [{ name: 'tone', recordIn: 0, recordOut: 120 }] }]);
  const busId = crypto.randomUUID();
  await api.putDocument(seed.projectId, id, (doc) => {
    (doc as { buses?: unknown[] }).buses = [{ id: busId, name: 'Music bus', pan: 0, outputBusId: null }];
    doc.tracks.find((t) => t.kind === 'audio')!.busId = busId;
  });
  const open = await playedPeakDb(await openEditor(id));
  expect(open, 'routed through an open bus the track is heard').toBeGreaterThan(-30);

  await api.putDocument(seed.projectId, id, (doc) => {
    (doc as { buses?: Array<{ gainDb?: { value: number } }> }).buses![0].gainDb = { value: -60 };
  });
  const muted = await playedPeakDb(await openEditor(id));
  expect(muted, 'the bus at its quietest setting silences its tracks').toBeLessThan(-60);
});
