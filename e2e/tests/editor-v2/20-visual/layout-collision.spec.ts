import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { VIEWPORTS, VISUAL_WORKSPACES, findCollisions, openWorkspace, shot } from '../../../fixtures/editor-v2/visual';

/**
 * T-026 / VF5: no two interactive controls overlap in any workspace at 1440x900 or 1024x768, and the page does not scroll
 * sideways. Measured from real bounding boxes (clipped by their scrolling ancestors), not from class names.
 */
for (const viewport of VIEWPORTS) {
  test(`T026.VF5 ${viewport.name}: no interactive controls overlap in any workspace`, async ({ page, freshTimeline, openEditor }) => {
    runOn('desktop');
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    const ed = await openEditor(await freshTimeline('seeded'));
    const problems: string[] = [];
    for (const workspace of VISUAL_WORKSPACES) {
      await openWorkspace(ed, workspace);
      await shot(page, `${workspace}-${viewport.name}`);
      for (const c of await findCollisions(page)) problems.push(`${workspace}: ${c.a} overlaps ${c.b} (${c.area}px2)`);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      if (overflow > 1) problems.push(`${workspace}: the page scrolls sideways by ${overflow}px`);
    }
    expect(problems, problems.join('\n')).toEqual([]);
  });
}
