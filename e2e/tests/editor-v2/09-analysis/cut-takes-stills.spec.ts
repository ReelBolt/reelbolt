import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import { extendAnalysis, frameRgb, runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 09 analysis-driven editing: boring shots, take stacks and Save still (remediation-plan.md W0.5 and W0.3;
 * verdict-table.md P6-CUT-04, P6-TAKE-01/02, P6-STILL-01).
 */

const items = (tl: Timeline) => [...tl.document.tracks[0].items].sort((a, b) => a.recordIn - b.recordIn);

/**
 * P6-CUT-04 (W0.5): the Cut workspace ranks boring shots from the run's analysis. No stub: the free
 * analysis of clipA measures one 12 s shot with motion 0.016 (>= 4 s, <= 0.08), and Remove cuts it.
 */
test('P6-CUT-04 the Cut workspace lists the analysed boring shot, and Remove cuts it', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-CUT-04');
  const ed = await openEditor(await freshTimeline('derush'));
  await ed.workspace('cut');
  const shot = ed.page.locator('[data-testid^="cut-boring-remove-"]').first();
  await expect(shot, 'a boring shot is listed with Remove').toBeVisible();
  await ed.tap(shot);
  await ed.expectServer((tl) => items(tl).reduce((n, i) => n + (i.recordOut - i.recordIn), 0) < 360, 'the boring shot is cut from the timeline');
});

/**
 * Take stacks need near-duplicate groups, which the free analysis does not compute (vision is off), so the
 * run's VideoAnalyze artifact is given two shots of one group (s0 0-4.5 s shown, s1 8-12 s best take).
 * The derush clip is trimmed to [0,120) so it shows s0.
 */
const TAKES = {
  shots: [
    { id: 's0', startSec: 0, endSec: 4.5, visual: { motionMean: 0.02, motionClass: 'Subtle', duplicateGroupId: 'k0', isBestTake: false } },
    { id: 's1', startSec: 8, endSec: 12, visual: { motionMean: 0.02, motionClass: 'Subtle', duplicateGroupId: 'k0', isBestTake: true } },
  ],
  duplicateGroups: [{ id: 'k0', shotIds: ['s0', 's1'], bestShotId: 's1', meanSimilarity: 0.93 }],
};

async function takesTimeline(api: import('../../../fixtures/editor-v2/api').EditorApi, seed: import('../../../fixtures/editor-v2').Seed, freshTimeline: (f: 'derush') => Promise<string>): Promise<{ id: string; clipId: string }> {
  const id = await freshTimeline('derush');
  const tl = await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[0].recordOut = 120; });
  return { id, clipId: tl.document.tracks[0].items[0].id };
}

test('P6-TAKE-01 the Takes panel stacks the clip with its alternate, marked best take', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('P6-TAKE-01');
  await extendAnalysis(page, TAKES);
  const { id, clipId } = await takesTimeline(api, seed, freshTimeline);
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-takes');
  const stack = ed.page.getByTestId(`take-stack-${clipId}`);
  await expect(stack, 'the clip has a take stack').toBeVisible();
  await expect(stack).toContainText(/best/i);
  await expect(ed.page.getByTestId(`take-use-${clipId}-s1`)).toBeVisible();
});

test('P6-TAKE-02 "Use this take" is one replace that keeps the slot and length', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('P6-TAKE-02');
  await extendAnalysis(page, TAKES);
  const { id, clipId } = await takesTimeline(api, seed, freshTimeline);
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-takes');
  await ed.tap(ed.page.getByTestId(`take-use-${clipId}-s1`));
  const swapped = await ed.expectServer((tl) => items(tl)[0]?.sourceIn === 240, 'the clip now shows s1 (source 8 s = frame 240)');
  expect([items(swapped)[0].recordIn, items(swapped)[0].recordOut], 'slot and length kept').toEqual([0, 120]);
  expect(ed.patches.filter((p) => p.status === 200).map((p) => p.ops.join(',')), 'one replace').toEqual(['replace']);
});

/** P6-STILL-01 (W0.3, PA-9): Save still writes the picture the viewer shows (a red colour card), not a black PNG. */
test('P6-STILL-01 Save still downloads the viewer picture, not a black frame', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('P6-STILL-01');
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [
      { type: 'generatorClip', id: crypto.randomUUID(), name: 'Red', recordIn: 0, recordOut: 90, generatorType: 'color', generatorConfig: { color: '#FF0000' }, enabled: true },
    ];
  });
  const ed = await openEditor(id);
  await ed.seek(30);
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('save-still'))]);
  expect(download.suggestedFilename(), 'file name <name>-<tc>-preview.png').toMatch(/-00-00-01-00-preview\.png$/);
  const dir = path.join(ARTIFACTS, 'stills');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${Date.now()}-${download.suggestedFilename()}`);
  await download.saveAs(file);
  const [r, g, b] = frameRgb(file, 0);
  expect(r, `the still is red like the viewer (mean rgb ${r},${g},${b})`).toBeGreaterThan(150);
  expect(g + b).toBeLessThan(100);
});
