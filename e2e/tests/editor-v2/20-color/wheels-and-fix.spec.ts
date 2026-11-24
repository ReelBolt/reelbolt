import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * color-tools (T-012): the round colour wheels on the Colour page and Fix exposure.
 *
 * The wheels: arrow keys nudge the puck (each key press is one `setProperty grade`, so one undo step),
 * one mouse drag of the puck is one undo step (commit on release), and Reset clears the cast but keeps
 * the master. The wheel's exact R/G/B fields must show a zero-sum cast.
 *
 * Fix exposure: measuring shows progress, then a result line and one `Auto fix` node first in the stack
 * (or, on a clip that already looks fine, the "looks fine" line and no node at all). The write is one
 * undo step; re-running replaces the node in place and stays one node.
 */

type Doc = Awaited<ReturnType<EditorPage['serverTimeline']>>['document'];
type GradeNodeJson = { id: string; name: string; primaries?: Record<string, unknown> };
type Wheel = { r?: number | null; g?: number | null; b?: number | null; master?: number | null };

const items = (doc: Doc) => doc.tracks.flatMap((t) => t.items);
const clipNamed = (doc: Doc, name: string) => items(doc).find((i) => i.name === name)!;
const nodesOf = (doc: Doc, name: string): GradeNodeJson[] =>
  ((clipNamed(doc, name).grade as { nodes?: GradeNodeJson[] } | null | undefined)?.nodes ?? []);
const wheelOf = (doc: Doc, name: string, key: string): Wheel =>
  ((nodesOf(doc, name)[0]?.primaries ?? {}) as Record<string, Wheel>)[key] ?? {};
const autoFixNodes = (doc: Doc, name: string) => nodesOf(doc, name).filter((n) => n.name === 'Auto fix');
const castSum = (w: Wheel) => Math.abs(w.r ?? 0) + Math.abs(w.g ?? 0) + Math.abs(w.b ?? 0);
const zeroSum = (w: Wheel) => Math.abs((w.r ?? 0) + (w.g ?? 0) + (w.b ?? 0)) <= 0.002;

async function openColorOnClipA(ed: EditorPage, frame = 60): Promise<void> {
  await ed.seek(frame);
  await ed.selectClipAt(frame);
  await ed.workspace('color');
  await expect(ed.testId('color-workspace')).toBeVisible();
}

/** Blur whatever holds focus: Ctrl+Z must reach the editor, not a number box. */
const blur = (ed: EditorPage) => ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
const wheelBox = async (ed: EditorPage, key: string) => (await ed.testId(`grade-wheel-${key}-disc`).boundingBox())!;

async function readGamma(ed: EditorPage): Promise<[number, number, number]> {
  const values: number[] = [];
  for (const c of ['r', 'g', 'b'] as const) values.push(Number.parseFloat(await ed.testId(`grade-wheel-gamma-${c}`).inputValue()));
  return [values[0], values[1], values[2]];
}

test.describe('colour wheels', () => {
  test('the wheel keyboard nudge writes a zero-sum cast, and one nudge is one undo step', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(120_000);
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('grade-add-node'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 1, 'the correction node is stored');

    const disc = ed.testId('grade-wheel-gamma-disc');
    await expect(disc).toBeVisible();
    await disc.focus();
    const readout0 = (await ed.testId('grade-wheel-gamma-readout').textContent()) ?? '';

    // One ArrowRight nudges the puck: the exact fields hold a zero-sum cast.
    await ed.page.keyboard.press('ArrowRight');
    await expect.poll(async () => { const [r, g, b] = await readGamma(ed); return Math.abs(r) + Math.abs(g) + Math.abs(b); }).toBeGreaterThan(0);
    const [r1, g1, b1] = await readGamma(ed);
    expect(Math.abs(r1) + Math.abs(g1) + Math.abs(b1), 'the nudge moves r/g/b off identity').toBeGreaterThan(0);
    expect(r1 + g1 + b1, 'the cast is zero-sum (hue only, no brightness)').toBeCloseTo(0, 2);
    await ed.expectServer((tl) => castSum(wheelOf(tl.document, 'clipA.mp4', 'gamma')) > 0, 'the keyboard nudge is stored');

    // One Ctrl+Z undoes that one nudge: identity again in one step. (The readout rounds a
    // 0.05-radius nudge to "Neutral, 0%", so the exact fields are the honest witness.)
    await blur(ed);
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4')[0]?.primaries?.gamma == null, 'one undo restores the wheel to identity');
    await expect.poll(async () => readGamma(ed)).toEqual([0, 0, 0]);

    // Five nudges: each press is its own history entry, so single undos (never one) walk back to identity.
    await disc.focus();
    for (let i = 0; i < 5; i++) await ed.page.keyboard.press('ArrowRight');
    await expect(ed.testId('grade-wheel-gamma-readout')).not.toHaveText(readout0);
    const [r5, g5, b5] = await readGamma(ed);
    expect(Math.abs(r5) + Math.abs(g5) + Math.abs(b5), 'five nudges leave a visible cast').toBeGreaterThan(0.01);
    expect(r5 + g5 + b5, 'five nudges keep the cast zero-sum').toBeCloseTo(0, 2);
    await ed.expectServer((tl) => castSum(wheelOf(tl.document, 'clipA.mp4', 'gamma')) > 0.01, 'the five nudges are stored');

    await blur(ed);
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((tl) => castSum(wheelOf(tl.document, 'clipA.mp4', 'gamma')) > 0, 'one undo steps back one nudge, not all five');
    for (let i = 0; i < 4; i++) await ed.page.keyboard.press('Control+z');
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4')[0]?.primaries?.gamma == null, 'five undos restore identity');
    await expect.poll(async () => readGamma(ed)).toEqual([0, 0, 0]);
  });

  test('one mouse drag of the lift puck writes a zero-sum cast in one undo step', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(120_000);
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('grade-add-node'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 1, 'the correction node is stored');

    const disc = ed.testId('grade-wheel-lift-disc');
    await expect(disc).toBeVisible();
    const box = await wheelBox(ed, 'lift');
    const cx = box.x + box.width / 2;
    const cy = box.y + box.height / 2;
    const readout0 = (await ed.testId('grade-wheel-lift-readout').textContent()) ?? '';
    await ed.mouseDrag(cx, cy, cx + box.width * 0.25, cy - box.height * 0.15, 8);

    await expect(ed.testId('grade-wheel-lift-readout')).not.toHaveText(readout0);
    const lift = await ed.expectServer((tl) => castSum(wheelOf(tl.document, 'clipA.mp4', 'lift')) > 0.01, 'the drag stores a lift cast');
    const w = wheelOf(lift.document, 'clipA.mp4', 'lift');
    expect(w.r, 'the drag moves red').toBeGreaterThan(0);
    expect(w.g, 'the drag moves green').toBeLessThan(0);
    expect((w.r ?? 0) + (w.g ?? 0) + (w.b ?? 0), 'the drag cast is zero-sum').toBeCloseTo(0, 2);

    // The whole drag committed once on release: one Ctrl+Z returns the wheel to identity in one step.
    await blur(ed);
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4')[0]?.primaries?.lift == null, 'one undo removes the whole drag');
    await expect(ed.testId('grade-wheel-lift-readout')).toHaveText(readout0);
  });

  test('Reset clears the cast and keeps the master', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(120_000);
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('grade-add-node'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 1, 'the correction node is stored');

    // A master via the exact values field, then a cast via a drag.
    const master = ed.testId('grade-exact-lift').getByTestId('grade-wheel-lift-master');
    await master.fill('0.25');
    await master.blur();
    await ed.expectServer((tl) => wheelOf(tl.document, 'clipA.mp4', 'lift').master === 0.25, 'the master is stored');
    // The panel scrolls the field into view: re-read the disc box after the fill.
    const box = await wheelBox(ed, 'lift');
    await ed.mouseDrag(box.x + box.width / 2, box.y + box.height / 2, box.x + box.width * 0.75, box.y + box.height * 0.35, 8);
    await ed.expectServer((tl) => castSum(wheelOf(tl.document, 'clipA.mp4', 'lift')) > 0.01, 'the drag stores the cast');

    const reset = ed.testId('grade-wheel-lift-reset');
    await expect(reset).toBeEnabled();
    await ed.tap(reset);
    const mean = await ed.expectServer((tl) => castSum(wheelOf(tl.document, 'clipA.mp4', 'lift')) === 0, 'Reset clears the cast');
    const w = wheelOf(mean.document, 'clipA.mp4', 'lift');
    expect([w.r ?? 0, w.g ?? 0, w.b ?? 0], 'the cast is zero').toEqual([0, 0, 0]);
    expect(w.master, 'Reset keeps the master').toBe(0.25);
  });
});

test.describe('Fix exposure', () => {
  /** Colour page on the gallery tab, clipA selected (the seeded clip). */
  async function openFixPanel(ed: EditorPage): Promise<void> {
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('color-tab-gallery'));
    await expect(ed.testId('look-panel')).toBeVisible();
  }

  /** Run the fix and wait for the measuring progress and the final line. */
  async function runFix(ed: EditorPage): Promise<string> {
    await ed.tap(ed.testId('fix-exposure-run'));
    await expect(ed.testId('fix-exposure-progress'), 'the measuring progress appears').toBeVisible({ timeout: 10_000 });
    await expect(ed.testId('fix-exposure-result'), 'the result line appears').toBeVisible({ timeout: 90_000 });
    return (await ed.testId('fix-exposure-result').textContent()) ?? '';
  }

  test('the seeded clip gets a result line and one Auto fix node first in the stack, removed by one undo', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(180_000);
    const ed = await openEditor(await freshTimeline('seeded'));
    await openFixPanel(ed);
    const line = await runFix(ed);

    if (/looks fine/i.test(line)) {
      // The seeded footage is healthy: the line says so and NOTHING is written.
      await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 0, 'looks fine: no node was added');
    } else {
      // A fix: exactly one Auto fix node, first in the stack (server) and first in the node list (UI).
      const tl = await ed.expectServer((t) => autoFixNodes(t.document, 'clipA.mp4').length === 1, 'the fix line and exactly one Auto fix node');
      expect(nodesOf(tl.document, 'clipA.mp4')[0].name, 'Auto fix is first in the stack').toBe('Auto fix');
      await expect(ed.testId('grade-node-select-0')).toHaveText('Auto fix');
      // The fix write is ONE undo step: one Ctrl+Z removes the node.
      await blur(ed);
      await ed.page.keyboard.press('Control+z');
      await ed.expectServer((t) => autoFixNodes(t.document, 'clipA.mp4').length === 0, 'one undo removes the fix');
      await expect(ed.testId('grade-nodes-empty')).toBeVisible();
    }
  });

  test('Fix exposure again replaces the node in place and stays exactly one Auto fix node', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(240_000);
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    // A user node first, so "first in the stack" is a real assertion.
    await ed.tap(ed.testId('grade-add-node'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 1, 'the user correction is stored');
    await ed.tap(ed.testId('color-tab-gallery'));
    await expect(ed.testId('look-panel')).toBeVisible();

    const firstLine = await runFix(ed);
    if (/looks fine/i.test(firstLine)) test.skip(true, 'the seeded footage already looks fine: there is no Auto fix node to re-run');
    let tl = await ed.expectServer((t) => autoFixNodes(t.document, 'clipA.mp4').length === 1, 'the first fix writes one Auto fix node');
    expect(nodesOf(tl.document, 'clipA.mp4').map((n) => n.name), 'Auto fix goes first, the user node keeps its place').toEqual(['Auto fix', 'Correction 1']);
    await expect(ed.testId('grade-node-select-0')).toHaveText('Auto fix');
    await expect(ed.testId('fix-exposure-run')).toHaveText('Fix exposure again');

    const secondLine = await runFix(ed);
    expect(/looks fine/i.test(secondLine), 're-running a fixed clip measures with the grade bypassed, so it fixes again').toBe(false);
    tl = await ed.expectServer((t) => autoFixNodes(t.document, 'clipA.mp4').length === 1, 're-running keeps exactly one Auto fix node');
    expect(nodesOf(tl.document, 'clipA.mp4').map((n) => n.name), 'the node is replaced in place, not appended').toEqual(['Auto fix', 'Correction 1']);

    // The re-run is its own one-step edit: one undo steps back to the first fix, a second removes the node
    // and keeps the user's correction.
    await blur(ed);
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((t) => autoFixNodes(t.document, 'clipA.mp4').length === 1, 'one undo reverts only the re-run');
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((t) => autoFixNodes(t.document, 'clipA.mp4').length === 0 && nodesOf(t.document, 'clipA.mp4').map((n) => n.name).join() === 'Correction 1', 'the next undo removes Auto fix and keeps the user node');
  });
});
