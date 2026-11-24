import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { downloadRender, expectLargerScreenState, frameRgb, runOn, waitRender } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 10 grade and compose ❌ rows (remediation-plan.md W0.6 curve editor, W0.1 render, W0.3 proxy URL for
 * tracking; curves and window tracking are cut / deferred on phone, R10).
 */

/** Color workspace with clipA selected and one correction node on it. */
async function gradeClipA(ed: EditorPage): Promise<void> {
  await ed.workspace('color');
  await ed.selectClipAt(60);
  await ed.tap(ed.testId('grade-add-node'));
  const select0 = ed.page.getByTestId('grade-node-select-0');
  if (await select0.isVisible().catch(() => false)) await ed.tap(select0);
}

/**
 * P7-GRADE-06 (W0.6, UI2-04): dragging a curve point is ONE undo step (on origin/master every mouse move
 * writes its own history entry, so one Ctrl+Z only steps the point back one move).
 */
test('P7-GRADE-06 dragging a curve point is one undo step', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P7-GRADE-06');
  const ed = await openEditor(await freshTimeline('seeded'));
  await gradeClipA(ed);
  // Curves is its own tab of the Colour page's tools (Primaries | Curves | LUT | Secondaries | Groups | Gallery).
  await ed.tap(ed.testId('color-tab-curves'));
  const svg = ed.page.getByTestId('curve-svg');
  await svg.scrollIntoViewIfNeeded();
  const box = (await svg.boundingBox())!;
  const at = (x: number, y: number) => [box.x + x * box.width, box.y + (1 - y) * box.height] as const;
  const [x0, y0] = at(0.3, 0.3);
  const [x1, y1] = at(0.3, 0.8);
  await ed.page.mouse.move(x0, y0);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x1, y1, { steps: 12 });
  await ed.page.mouse.up();
  const cy = async () => {
    const pts = ed.page.locator('[data-testid^="curve-point-"]');
    return (await pts.count()) === 0 ? null : Number(await pts.first().getAttribute('cy'));
  };
  const dragged = await cy();
  expect(dragged, 'the point followed the drag up').not.toBeNull();
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+z');
  // One step back: the whole drag is undone (the point returns to where it was added, or the add+drag goes as one).
  const addedAt = (1 - 0.3) * box.height;
  await expect.poll(async () => {
    const v = await cy();
    return v === null || Math.abs(v - addedAt) < 6 ? 'drag undone' : `point still at cy ${v} (added at ${addedAt.toFixed(0)}, dragged to ${dragged})`;
  }, { message: 'one Ctrl+Z undoes the whole drag' }).toBe('drag undone');
});

test('P7-GRADE-06 phone: the curve editor shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P7-GRADE-06', 'phone');
  const ed = await openEditor(seed.seededTimelineId);
  await gradeClipAOnPhone(ed);
  await expectLargerScreenState(ed.page, /curve/i, 'Curves on a phone');
});

/**
 * Phone: the Color workspace puts the grade stack in its own "Color" pane (Timeline | Inspector | Color).
 * Select clipA on the timeline, open that pane and add one correction node, as a touch user would.
 */
async function gradeClipAOnPhone(ed: EditorPage): Promise<void> {
  await ed.workspace('color');
  await ed.selectClipAt(60);
  await ed.tap(ed.page.locator('label', { hasText: /^Color$/ }).first());
  await ed.tap(ed.testId('grade-add-node'));
  const select0 = ed.page.getByTestId('grade-node-select-0');
  if (await select0.isVisible().catch(() => false)) await ed.tap(select0);
}

/** P7-GRADE-18 (M) (W0.1): a graded clip renders with its grade (saturation 0 = grey picture), started from the phone. */
test('P7-GRADE-18 phone: a clip graded to saturation 0 renders grey from Deliver', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P7-GRADE-18');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks[0].items[0].grade = { enabled: true, nodes: [{ id: crypto.randomUUID(), name: 'Grey', enabled: true, primaries: { saturation: 0, exposure: Math.round(Math.random() * 20) / 100 } }] };
  });
  const ed = await openEditor(id);
  const posts: string[] = [];
  ed.page.on('response', async (res) => {
    if (res.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(res.url()).pathname)) posts.push(`${res.status()} ${await res.text().catch(() => '')}`);
  });
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => posts.length, { message: 'the render was requested' }).toBeGreaterThan(0);
  expect(posts[0], 'the render starts').toMatch(/^202 /);
  const r = await waitRender(api, seed.projectId, id, (JSON.parse(posts[0].slice(4)) as { executionId: string }).executionId);
  expect(r.status, `grade render ${r.errorMessage ?? ''}`).toBe('Passed');
  const [red, green, blue] = frameRgb(await downloadRender(api, seed.projectId, r), 2.5);
  expect(red + green + blue, 'the graded frame is not black').toBeGreaterThan(60);
  expect(Math.max(Math.abs(red - green), Math.abs(green - blue), Math.abs(red - blue)), `saturation 0 renders grey (rgb ${red},${green},${blue})`).toBeLessThan(10);
});

/** P9-GRADE-03 (D) (W0.3, proxy URL): "Track through clip" tracks a power window and writes its keys. */
test('P9-GRADE-03 Track through clip writes window keys', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P9-GRADE-03');
  test.setTimeout(180_000);
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await gradeClipA(ed);
  // The power window lives in the Secondaries tab.
  await ed.tap(ed.testId('color-tab-secondaries'));
  await ed.testId('grade-window-toggle').check();
  await ed.tap(ed.testId('grade-window-track'));
  await expect(ed.page.getByText(/no playable media to track/i), 'the clip\'s media is found').toHaveCount(0);
  await expect(ed.page.getByRole('button', { name: /Clear track \(\d+ keys?\)/ }), 'the window is tracked').toBeVisible({ timeout: 90_000 });
});

test('P9-GRADE-03 phone: window tracking shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P9-GRADE-03', 'phone');
  const ed = await openEditor(seed.seededTimelineId);
  await gradeClipAOnPhone(ed);
  // The power window itself works on a phone; tracking it through the clip is what is cut.
  await ed.tap(ed.testId('grade-window-toggle'));
  await expectLargerScreenState(ed.page, /track/i, 'Window tracking on a phone');
});
