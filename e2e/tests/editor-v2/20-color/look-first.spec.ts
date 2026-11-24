import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import { sampleElement } from '../../../fixtures/editor-v2/pixels';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { clickSlider, meanRgb } from '../../../fixtures/editor-v2/ws';

/**
 * R10.1 / R10.2 / R10.8: Color opens look-first. Looks are real grade nodes in the stored document
 * (asserted through the API), the viewer shows them (pixels), Apply to all is one undo step.
 */

type Doc = Awaited<ReturnType<EditorPage['serverTimeline']>>['document'];
type GradeNodeJson = { id: string; name: string; primaries?: Record<string, unknown> };
const items = (doc: Doc) => doc.tracks.flatMap((t) => t.items);
const clipNamed = (doc: Doc, name: string) => items(doc).find((i) => i.name === name)!;
const nodesOf = (doc: Doc, name: string): GradeNodeJson[] => ((clipNamed(doc, name).grade as { nodes?: GradeNodeJson[] } | null | undefined)?.nodes ?? []);
const redOverBlue = async (ed: EditorPage) => {
  const [r, , b] = meanRgb(await sampleElement(ed.page, ed.viewer));
  return r / Math.max(1, b);
};

/**
 * The viewer grades with the export's maths (colorbalance weights the cast by lightness: shadows, midtones, highlights),
 * so a Warm cast barely moves clipA's saturated colour bars. The viewer-pixel specs grade clipB, whose picture is full of
 * midtones, and measure the same frame the look was applied to.
 */
const CLIP_B_FRAME = SEEDED_LAYOUT.clipB[0] + 60;
/** Warm at 100% lifts clipB's red/blue ratio by about 1.3% in the faithful maths: "warmer" is above 0.6%, "original" below 0.3%. */
const WARM_RISE = 1.006;
const WARM_BACK = 1.003;

async function openColorOnClipA(ed: EditorPage, frame = 60): Promise<void> {
  await ed.seek(frame);
  await ed.selectClipAt(frame);
  await ed.workspace('color');
  // The phone's Colour pane opens straight on the looks (no mode label to tap first).
  // Colour has no hidden mode (wave 2 T-006): the looks are the Gallery tab of the grade tools (the phone pane opens on them).
  if (!ed.isPhone) await ed.tap(ed.testId('color-tab-gallery'));
  // The phone's look sheet has no panel frame: its first look button is the proof the pane is open.
  await expect(ed.testId(ed.isPhone ? 'look-warm' : 'look-panel')).toBeVisible();
}

test.describe('R10.1 look-first Color', () => {
  test('R10.1 choosing Warm and a strength writes a named look node; the viewer gets warmer', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed, CLIP_B_FRAME);
    await expect(ed.testId('look-gallery')).toBeVisible();
    const plain = await redOverBlue(ed);
    await ed.tap(ed.testId('look-warm'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipB.mp4').some((n) => n.name === 'Look: Warm 100%'), 'Warm look at 100% is stored on clipB');
    await expect.poll(() => redOverBlue(ed), { message: 'warmer: red/blue ratio rises' }).toBeGreaterThan(plain * WARM_RISE);

    const strength = await clickSlider(ed.page, ed.testId('look-strength'), 0.5);
    expect(strength).toBeLessThan(100);
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipB.mp4').some((n) => n.name === `Look: Warm ${strength}%`), 'the strength is stored in the look node');
    expect(nodesOf((await ed.serverTimeline()).document, 'clipB.mp4'), 'the look stays one node').toHaveLength(1);
  });

  test('R10.1 the five scalars write one Fix node (brightness is exposure, warmth is temperature)', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    const plain = await redOverBlue(ed);
    await clickSlider(ed.page, ed.testId('fix-warmth'), 0.95);
    const tl = await ed.expectServer((t) => nodesOf(t.document, 'clipA.mp4').some((n) => n.name === 'Fix'), 'the Fix node is stored');
    const fix = nodesOf(tl.document, 'clipA.mp4').find((n) => n.name === 'Fix')!;
    expect(Number(fix.primaries?.temperature), 'warmth is stored as temperature').toBeGreaterThan(0.5);
    await expect.poll(() => redOverBlue(ed), { message: 'warmth warms the picture' }).toBeGreaterThan(plain * 1.03);
  });

  test('R10.1 Compare: the before toggle shows the original and writes nothing; split draws the reference', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed, CLIP_B_FRAME);
    const plain = await redOverBlue(ed);
    await ed.tap(ed.testId('look-warm'));
    await ed.expectSaved();
    await expect.poll(() => redOverBlue(ed)).toBeGreaterThan(plain * WARM_RISE);
    const sent = ed.patches.length;
    await ed.page.getByTestId('color-bypass').check({ force: true });
    await expect.poll(() => redOverBlue(ed), { message: 'before: the original colours' }).toBeLessThan(plain * WARM_BACK);
    await ed.page.getByTestId('color-bypass').uncheck({ force: true });
    await expect.poll(() => redOverBlue(ed), { message: 'after: warm again' }).toBeGreaterThan(plain * WARM_RISE);
    expect(ed.patches.length, 'compare is a view state').toBe(sent);

    await ed.tap(ed.testId('look-split'));
    await expect(ed.testId('reference-wipe'), 'the before picture is drawn over the left of the viewer').toBeVisible({ timeout: 15_000 });
    await expect(ed.testId('look-split-position')).toBeVisible();
    await ed.tap(ed.testId('look-split-off'));
    await expect(ed.testId('reference-wipe')).toHaveCount(0);
  });

  test('R10.1 Apply to all clips grades every media clip in one undo step', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('look-warm'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 1, 'clipA has the look');
    await ed.tap(ed.testId('look-apply-all'));
    const names = ['clipA.mp4', 'clipB.mp4', 'vertical.mp4', 'still.png'];
    const tl = await ed.expectServer((t) => names.every((n) => nodesOf(t.document, n).some((x) => x.name === 'Look: Warm 100%')), 'every media clip stores the Warm look');
    const media = items(tl.document).filter((i) => i.type === 'mediaClip' && names.includes(i.name));
    expect(media.length).toBe(names.length);
    // The viewer is on clipA; seek into clipB to see the look applied there too.
    await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((t) => ['clipB.mp4', 'vertical.mp4', 'still.png'].every((n) => nodesOf(t.document, n).length === 0) && nodesOf(t.document, 'clipA.mp4').length === 1, 'one Undo reverts all the others and keeps clipA');
  });
});

test.describe('R10.2 match and apply to many', () => {
  test('R10.2 Match this clip to another stores a Match correction on the clip', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    test.setTimeout(120_000);
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    const clipB = clipNamed((await ed.serverTimeline()).document, 'clipB.mp4');
    await ed.tap(ed.testId(`match-pick-${clipB.id}`));
    await ed.tap(ed.testId('match-run'));
    const tl = await ed.expectServer((t) => nodesOf(t.document, 'clipA.mp4').some((n) => n.name === 'Match'), 'a Match node is stored on clipA', 60_000);
    const node = nodesOf(tl.document, 'clipA.mp4').find((n) => n.name === 'Match')!;
    expect(Object.keys(node.primaries ?? {}).length, 'the match carries primaries').toBeGreaterThan(0);
    expect(clipNamed(tl.document, 'clipB.mp4').grade ?? null, 'the reference clip is untouched').toBeNull();
  });

  test('R10.2 copy a grade and paste it onto the selected clips', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('look-cool'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').length === 1, 'clipA has the Cool look');
    await ed.tap(ed.testId('look-copy'));
    await ed.selectClipAt(SEEDED_LAYOUT.clipB[0] + 60);
    await ed.selectClipAt(SEEDED_LAYOUT.vertical[0] + 60, 0, ['Shift']);
    await ed.tap(ed.testId('look-paste'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipB.mp4').length === 1 && nodesOf(tl.document, 'vertical.mp4').length === 1, 'the grade is pasted onto both selected clips');
  });
});

test.describe('R10.8 adjustment layers in Color', () => {
  test('R10.8 an adjustment layer is added over the clip with its range and takes a look', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('adjustment-add'));
    const tl = await ed.expectServer((t) => items(t.document).some((i) => i.type === 'adjustmentClip'), 'the adjustment clip is stored');
    const adj = items(tl.document).find((i) => i.type === 'adjustmentClip')!;
    expect([adj.recordIn, adj.recordOut], 'its range is the clip it was added over').toEqual([SEEDED_LAYOUT.clipA[0], SEEDED_LAYOUT.clipA[1]]);
    await expect(ed.testId(`adjustment-range-${adj.id}`)).toBeVisible();
    await ed.tap(ed.testId('look-cool'));
    await ed.expectServer((t) => {
      const a = items(t.document).find((i) => i.type === 'adjustmentClip');
      return ((a?.grade as { nodes?: GradeNodeJson[] } | undefined)?.nodes ?? []).some((n) => n.name === 'Look: Cool 100%');
    }, 'the look is stored on the adjustment clip, not on clipA');
    expect(nodesOf((await ed.serverTimeline()).document, 'clipA.mp4')).toHaveLength(0);
  });
});

test.describe('R10.1 tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R10.1 tablet: look tiles and buttons are >= 44 px and Warm is stored by touch', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    expect(await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches)).toBe(true);
    await openColorOnClipA(ed);
    for (const id of ['look-warm', 'look-apply-all', 'look-split']) {
      expect((await ed.testId(id).boundingBox())!.height, id).toBeGreaterThanOrEqual(44);
    }
    await ed.tap(ed.testId('look-warm'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').some((n) => n.name === 'Look: Warm 100%'), 'tablet: the Warm look is stored');
    await ed.tap(ed.testId('look-apply-all'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipB.mp4').some((n) => n.name === 'Look: Warm 100%'), 'tablet: apply to all reached clipB');
  });
});

test.describe('R10.1 phone', () => {
  test('R10.1 phone: the Color pane opens on looks and Warm is stored', async ({ freshTimeline, openEditor }) => {
    runOn('pixel7', 'iphone14webkit');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openColorOnClipA(ed);
    await ed.tap(ed.testId('look-warm'));
    await ed.expectServer((tl) => nodesOf(tl.document, 'clipA.mp4').some((n) => n.name === 'Look: Warm 100%'), 'phone: the Warm look is stored');
  });
});
