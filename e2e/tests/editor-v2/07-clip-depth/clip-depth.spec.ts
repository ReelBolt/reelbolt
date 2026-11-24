import { test, expect } from '../../../fixtures/editor-v2';
import { mediaClip, type Timeline } from '../../../fixtures/editor-v2/api';
import { downloadRender, meanVolumeDb, renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 07 clip depth ❌ rows (remediation-plan.md W0.6 "Add colour card / Add title at playhead", W0.2 first
 * keyframe, W0.1 render; verdict-table.md P5-GEN / P5-KEY / P5-TRANS rows).
 */

/**
 * P5-GEN-03 (W0.6, UI2-03): "Add colour card" and "Add title" create valid items at the playhead that save.
 * Both live in the timeline header's Add menu ("Add" > "Colour card at the playhead" / "Title at the playhead").
 */
test('P5-GEN-03 Add colour card and Add title create items at the playhead that are stored', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P5-GEN-03');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.openAddMenu();
  await ed.tap(ed.page.getByRole('menuitem', { name: /colou?r card at the playhead/i }));
  await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'generatorClip' && i.recordIn === 30)), 'a colour card starts at the playhead on the server');
  await ed.seek(200);
  await ed.openAddMenu();
  await ed.tap(ed.page.getByRole('menuitem', { name: /title at the playhead/i }));
  const both = (tl: Timeline) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'titleClip' && i.recordIn === 200));
  await ed.expectServer(both, 'a title starts at the playhead on the server');
  await expect(ed.chip).not.toContainText(/rejected/i);
});

/** P5-KEY-01 (M) (W0.2, eng D4): the first opacity keyframe set by tap on a phone is stored with a real value. */
test('P5-KEY-01 phone: the first keyframe set with the diamond is stored with the current value', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P5-KEY-01');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(60);
  await ed.selectClipAt(60);
  await ed.openInspector();
  await ed.tap(ed.testId('inspector-finetune-toggle'));
  await ed.tap(ed.testId('inspector-key-opacity'));
  const keyed = await ed.expectServer((tl) => {
    const op = (tl.document.tracks[0].items[0].transform as { opacity?: { keys?: Array<{ frame: number; value: unknown }> } } | null)?.opacity;
    return (op?.keys?.length ?? 0) === 1;
  }, 'the first keyframe is stored');
  const op = (keyed.document.tracks[0].items[0].transform as { opacity: { value: unknown; keys: Array<{ frame: number; value: unknown }> } }).opacity;
  expect(op.keys[0].value, 'the key holds the current opacity, never null').toBe(1);
  expect(op.value, 'the static value is not null').not.toBeNull();
  await expect(ed.chip).not.toContainText(/rejected/i);
});

/** P5-TRANS-07 (W0.1 render): an audio transition renders as an equal-length crossfade, never a hole. */
test('P5-TRANS-07 an audio transition renders as a crossfade with no silent hole', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P5-TRANS-07');
  const id = await freshTimeline('seeded');
  const salt = Math.floor(Math.random() * 10);
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks[0].items = [mediaClip(crypto.randomUUID(), 'still.png', seed.files.still, 0, 150)];
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    a.items = [
      mediaClip(crypto.randomUUID(), 'tone out', seed.files.tone, 0, 60, { sourceIn: salt }),
      { type: 'transition', id: crypto.randomUUID(), name: 'Crossfade', recordIn: 60, recordOut: 80, transitionType: 'dissolve', enabled: true },
      mediaClip(crypto.randomUUID(), 'tone in', seed.files.tone, 80, 150, { sourceIn: 40 }),
    ];
  });
  const r = await renderAndWait(api, seed.projectId, id);
  expect(r.status, `render ${r.status} ${r.errorMessage ?? ''}`).toBe('Passed');
  const file = await downloadRender(api, seed.projectId, r);
  const [before, during, after] = [meanVolumeDb(file, 1.0, 1.9), meanVolumeDb(file, 2.05, 2.6), meanVolumeDb(file, 3.0, 4.0)];
  expect(during, `the transition span is audible (before ${before}, during ${during}, after ${after} dB)`).toBeGreaterThan(-40);
  expect(Math.abs(during - before), 'no dip or jump through the crossfade').toBeLessThan(9);
});
