import type { Page } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';

/** T-012: VF2 12 px text floor (R-VF2) and the 44 px touch targets on a phone (design review 1). */

test.use({ serviceWorkers: 'block' });

/** Every visible text node's computed font-size, smaller than 12 px, as "text (size)". */
async function smallText(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const out: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const text = (n.textContent ?? '').trim();
      const el = n.parentElement;
      if (!text || !el || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(el.tagName)) continue;
      const style = getComputedStyle(el);
      const rect = el.getBoundingClientRect();
      if (style.visibility === 'hidden' || style.display === 'none' || rect.width === 0 || rect.height === 0) continue;
      const size = parseFloat(style.fontSize);
      if (size < 12) out.push(`${text.slice(0, 40)} (${size}px)`);
    }
    return out;
  });
}

test('VF2 no visible text under 12 px in the editor workspaces and the deliver page', async ({ openEditor, freshTimeline }) => {
  runOn('desktop', 'pixel7');
  const ed = await openEditor(await freshTimeline('blank'));
  const workspaces = ['media', 'cut', 'edit', 'compose', 'color', 'audio', 'deliver'] as const;
  // The phone surface has no Cut or Compose workspace (docs/editor.md "Not on a phone"); its dock sheets stand for the rest.
  for (const name of workspaces.filter((w) => !ed.isPhone || (w !== 'cut' && w !== 'compose'))) {
    await ed.workspace(name);
    expect(await smallText(ed.page), `${name} workspace`).toEqual([]);
  }
});

test('VF2 no visible text under 12 px on the editor hub', async ({ page }) => {
  runOn('desktop', 'pixel7');
  const api = lightApi(page);
  await withTempProject(api, 'sweep-hub', async (project) => {
    await page.goto(`/app/editor?project=${project.id}`);
    await expect(page.getByTestId('timeline-create').first()).toBeVisible({ timeout: 30_000 });
    expect(await smallText(page)).toEqual([]);
  });
});

test('DR1 phone touch targets are at least 44 px: Advanced toggle, export links, bin buttons', async ({ openEditor, freshTimeline, seed, api }) => {
  runOn('pixel7');
  const binName = `Sweep ${Date.now()}`;
  await api.json('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name: binName }, [200, 201]);
  const ed = await openEditor(await freshTimeline('blank'));
  const high = async (loc: ReturnType<Page['locator']>, what: string) => {
    await expect(loc, what).toBeVisible({ timeout: 30_000 });
    const box = await loc.boundingBox();
    expect(box?.height ?? 0, `${what} height`).toBeGreaterThanOrEqual(44);
    expect(box?.width ?? 0, `${what} width`).toBeGreaterThanOrEqual(44);
  };
  await ed.workspace('media');
  // The Add media sheet (RM.8) lists bins as chips.
  await high(ed.page.getByRole('button', { name: binName }), 'bin chip');
  await ed.workspace('deliver');
  const adv = ed.testId('render-advanced-toggle');
  await expect(adv).toBeVisible();
  expect((await adv.boundingBox())?.height ?? 0, 'Advanced toggle height').toBeGreaterThanOrEqual(44);
  // On a phone the export file links sit under "More options".
  await ed.tap(adv);
  for (const f of ['otio', 'edl', 'fcpxml']) {
    const link = ed.testId(`deliver-export-${f}`);
    expect((await link.boundingBox())?.height ?? 0, `export ${f}`).toBeGreaterThanOrEqual(44);
  }
});

