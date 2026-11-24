import * as path from 'path';
import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import { MEDIA_DIR } from '../../../fixtures/editor-v2/env';
import { distance, meanLuma, referenceGrid, sampleElement, type Grid } from '../../../fixtures/editor-v2/pixels';
import { knownFailure } from '../expected-failures';

/**
 * T0.4 viewer pixel guards on the seeded timeline (audit top-10 #3, PA-1/PA-3/PA-6): what the program
 * monitor SHOWS (element screenshot, so no `preserveDrawingBuffer` needed) must be a real frame of the
 * source under the playhead, on a cold load (fresh browser context, empty cache) and a warm one.
 */

const FPS = 30;
const NON_BLACK = 20; // mean luma 0-255; testsrc2 and mandelbrot both sit far above it

async function sampleUntil(ed: EditorPage, ok: (g: Grid) => boolean, message: string, timeout = 20_000): Promise<Grid> {
  let grid: Grid = [];
  await expect.poll(async () => {
    grid = await sampleElement(ed.page, ed.viewer);
    return ok(grid);
  }, { message, timeout, intervals: [500, 1000, 2000] }).toBe(true);
  return grid;
}

async function checkCut(ed: EditorPage): Promise<void> {
  const clipA = path.join(MEDIA_DIR, 'clipA.mp4');
  const clipB = path.join(MEDIA_DIR, 'clipB.mp4');

  // Frame 0: clipA's first frame, not black.
  const refA0 = referenceGrid(clipA, 0);
  const refB0 = referenceGrid(clipB, 0);
  const atStart = await sampleUntil(ed, (g) => meanLuma(g) > NON_BLACK, 'frame 0 is not black');
  expect(distance(atStart, refA0), 'frame 0 shows clipA (closer to clipA than to clipB)').toBeLessThan(distance(atStart, refB0));

  // After the cut: record frame 225 = clipB source frame 75 (2.5 s), never clipA's picture.
  const recordFrame = SEEDED_LAYOUT.clipB[0] + 75;
  await ed.seek(recordFrame);
  const refB = referenceGrid(clipB, 75 / FPS);
  const refA = referenceGrid(clipA, recordFrame / FPS);
  const refAEarly = referenceGrid(clipA, 75 / FPS);
  await sampleUntil(
    ed,
    (g) => meanLuma(g) > NON_BLACK && distance(g, refB) < Math.min(distance(g, refA), distance(g, refAEarly)),
    `record frame ${recordFrame} shows clipB, not clipA`,
  );
}

test.describe('guards: the program monitor shows the right picture', () => {
  test('GUARD-VIEWER-CUT-COLD P3-VIEW-01 P3-VIEW-05 cold load: non-black, second source after the cut', async ({ seed, openEditor }) => {
    knownFailure('GUARD-VIEWER-CUT-COLD');
    // Each test gets a fresh browser context: empty HTTP cache, no prior proxy fetches (a cold load).
    const ed = await openEditor(seed.seededTimelineId);
    await checkCut(ed);
  });

  test('GUARD-VIEWER-CUT-WARM P3-VIEW-05 warm load: non-black, second source after the cut', async ({ seed, openEditor }) => {
    knownFailure('GUARD-VIEWER-CUT-WARM');
    const ed = await openEditor(seed.seededTimelineId);
    await ed.page.waitForLoadState('networkidle');
    await ed.reload(); // warm: proxies and timeline already fetched once in this context
    await checkCut(ed);
  });
});
