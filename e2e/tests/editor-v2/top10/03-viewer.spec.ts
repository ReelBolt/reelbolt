import * as path from 'path';
import { test, expect, SEEDED_LAYOUT } from '../../../fixtures/editor-v2';
import { MEDIA_DIR } from '../../../fixtures/editor-v2/env';
import { columnLuma, distance, meanLuma, referenceGrid, sampleElement, type Grid } from '../../../fixtures/editor-v2/pixels';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #3 (report.md §3): the program viewer must show the clip under the playhead on a cold load,
 * the second file after a cut, and a vertical clip pillarboxed (not stretched to 16:9).
 * Cut/cold/warm pixel guards: guards/viewer-pixels.spec.ts.
 */
const FPS = 30;

test('TOP10-03 P3-VIEW-01 P3-VIEW-05 cold load shows clipA, then clipB after the cut, then a pillarboxed vertical clip', async ({ seed, openEditor }) => {
  knownFailure('TOP10-03');
  const ed = await openEditor(seed.seededTimelineId);
  let grid: Grid = [];
  const sample = async (ok: (g: Grid) => boolean, message: string) => {
    await expect.poll(async () => ok((grid = await sampleElement(ed.page, ed.viewer))), { message, timeout: 20_000, intervals: [500, 1000, 2000] }).toBe(true);
  };
  await sample((g) => meanLuma(g) > 20, 'cold load: frame 0 is not black');

  const b = SEEDED_LAYOUT.clipB[0] + 75;
  const refB = referenceGrid(path.join(MEDIA_DIR, 'clipB.mp4'), 75 / FPS);
  const refA = referenceGrid(path.join(MEDIA_DIR, 'clipA.mp4'), 75 / FPS);
  await ed.seek(b);
  await sample((g) => distance(g, refB) < distance(g, refA), `frame ${b} shows clipB's picture, not clipA's`);

  await ed.seek(SEEDED_LAYOUT.vertical[0] + 75);
  // 720x1280 fitted into 16:9: picture in the centre ~10 of 32 columns, black pillars either side.
  await sample((g) => columnLuma(g, 13, 19) > 40 && columnLuma(g, 0, 8) < 12 && columnLuma(g, 24, 32) < 12, 'vertical clip is pillarboxed, not stretched');
});
