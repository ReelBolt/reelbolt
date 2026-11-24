import AxeBuilder from '@axe-core/playwright';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { VISUAL_WORKSPACES, openWorkspace } from '../../../fixtures/editor-v2/visual';

/**
 * T-026 / VF15: axe-core finds no serious or critical violation (including WCAG AA colour contrast) in any workspace.
 * The viewer canvas and the Early preview label are part of the page: nothing is excluded except the WebGL canvas, which axe
 * cannot read.
 */
for (const workspace of VISUAL_WORKSPACES) {
  test(`T026.VF15 ${workspace}: axe finds no serious or critical violation`, async ({ page, freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openWorkspace(ed, workspace);
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .exclude('canvas')
      .analyze();
    const bad = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
    const text = bad.map((v) => `${v.id} (${v.impact}): ${v.nodes.slice(0, 6).map((n) => `${n.target.join(' ')} <${(n.html ?? '').slice(0, 140)}>`).join('\n     ')}`).join('\n');
    expect(bad, text).toEqual([]);
  });
}
