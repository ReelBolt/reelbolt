import { expect, test, type Page } from '@playwright/test';
import * as fs from 'fs';
import { loginAs } from '../../fixtures/stack';

const SHOTS = process.env.PROJECT_TABS_SHOTS;
if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });

interface Box { label: string; x: number; y: number; width: number; height: number; right: number }

async function tabBoxes(page: Page): Promise<Box[]> {
  return page.evaluate(() =>
    Array.from(document.querySelectorAll<HTMLElement>('[role="tablist"] [role="tab"]'))
      .filter((el) => el.getBoundingClientRect().width > 0)
      .map((el) => {
        const r = el.getBoundingClientRect();
        return { label: (el.textContent ?? '').trim(), x: r.x, y: r.y, width: r.width, height: r.height, right: r.right };
      }),
  );
}

for (const locale of ['en', 'it'] as const) {
  test(`project tab strip: no overlap, 44px targets, active tab in view (${locale})`, async ({ page, request }, info) => {
    const { baseURL } = info.project.use;
    const login = await loginAs(page, 'creator');
    const created = await page.context().request.post(`${baseURL}/api/v1/projects`, { data: { name: `tabs-${locale}-${Date.now()}`, description: 'scratch' } });
    expect(created.ok()).toBeTruthy();
    const id = ((await created.json()) as { id: string }).id;
    void login; void request;
    try {
      await page.context().addCookies([{ name: 'rb_locale', value: locale, url: baseURL! }]);
      await page.goto(`/app/projects/${id}`);
      const list = page.getByRole('tablist').first();
      await expect(list).toBeVisible({ timeout: 30_000 });
      await page.waitForLoadState('networkidle');

      const boxes = await tabBoxes(page);
      expect(boxes.length).toBeGreaterThanOrEqual(8);
      for (let i = 1; i < boxes.length; i++) {
        const a = boxes[i - 1], b = boxes[i];
        expect(b.x, `"${b.label}" starts before "${a.label}" ends (labels overlap)`).toBeGreaterThanOrEqual(a.right - 0.5);
      }
      // A tab is at least as wide as its own label: it never shrinks under the text.
      const clipped = await page.evaluate(() =>
        Array.from(document.querySelectorAll<HTMLElement>('[role="tablist"] [role="tab"]'))
          .filter((el) => el.getBoundingClientRect().width > 0 && el.scrollWidth > el.clientWidth + 1)
          .map((el) => (el.textContent ?? '').trim()));
      expect(clipped, 'tabs whose label is wider than the tab').toEqual([]);
      for (const b of boxes) expect(b.height, `"${b.label}" touch target`).toBeGreaterThanOrEqual(44);

      const phone = (page.viewportSize()?.width ?? 1000) < 500;
      const m = await list.evaluate((el) => ({ sw: el.scrollWidth, cw: el.clientWidth }));
      if (phone) expect(m.sw, 'strip scrolls sideways on a phone').toBeGreaterThan(m.cw);
      await page.screenshot({ path: SHOTS ? `${SHOTS}/${info.project.name}-${locale}-overview.png` : undefined });

      // ?tab=folders is the last tab: it must be scrolled into the strip's visible bounds.
      await page.goto(`/app/projects/${id}?tab=folders`);
      const active = page.locator('[role="tablist"] [role="tab"][aria-selected="true"]').first();
      await expect(active).toBeVisible({ timeout: 30_000 });
      await page.waitForTimeout(800);
      const inView = await page.evaluate(() => {
        const l = document.querySelector('[role="tablist"]')!.getBoundingClientRect();
        const r = document.querySelector('[role="tablist"] [role="tab"][aria-selected="true"]')!.getBoundingClientRect();
        return { left: r.left - l.left, right: l.right - r.right };
      });
      expect(inView.left, 'active tab left edge inside the strip').toBeGreaterThanOrEqual(-1);
      expect(inView.right, 'active tab right edge inside the strip').toBeGreaterThanOrEqual(-1);
      if (SHOTS) await page.screenshot({ path: `${SHOTS}/${info.project.name}-${locale}-folders.png` });
    } finally {
      await page.context().request.delete(`${baseURL}/api/v1/projects/${id}`);
    }
  });
}
