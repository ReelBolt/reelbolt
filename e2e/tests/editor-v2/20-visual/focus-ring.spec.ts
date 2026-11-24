import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { VISUAL_WORKSPACES, openWorkspace } from '../../../fixtures/editor-v2/visual';

/** T-026 / VF15: tabbing through the header and the panels, every focused control shows a visible focus ring. */
for (const workspace of VISUAL_WORKSPACES) {
  test(`T026.VF15 ${workspace}: keyboard focus shows a ring on each control`, async ({ page, freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await openWorkspace(ed, workspace);
    await page.locator('body').click({ position: { x: 2, y: 2 } });
    const missing: string[] = [];
    let seen = 0;
    for (let i = 0; i < 40; i++) {
      await page.keyboard.press('Tab');
      const info = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        if (!el || el === document.body) return null;
        const cs = getComputedStyle(el);
        const ring = (cs.outlineStyle !== 'none' && parseFloat(cs.outlineWidth) >= 1) || cs.boxShadow !== 'none';
        const name = el.getAttribute('aria-label') ?? (el.textContent ?? '').trim().slice(0, 30) ?? el.tagName;
        return { ring, name: `${el.tagName.toLowerCase()} "${name}"`, inEditor: !!el.closest('[data-editor-surface]'), canvas: el.tagName === 'CANVAS' };
      });
      if (!info || !info.inEditor || info.canvas) continue;
      seen++;
      if (!info.ring) missing.push(info.name);
    }
    expect(seen, 'tabbing reaches editor controls').toBeGreaterThan(10);
    expect(missing, `controls without a focus ring: ${missing.join(', ')}`).toEqual([]);
  });
}
