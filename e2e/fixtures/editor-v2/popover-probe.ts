import { test, expect } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

/** Shared by 17-popover-blast: every option of an open dropdown must be inside the viewport, hit-test to itself and be pickable. */

export type Probe = {
  vw: number;
  vh: number;
  rows: Array<{
    text: string;
    ok: boolean;
    by: string;
    top: number;
    bottom: number;
    left: number;
    right: number;
  }>;
};

export async function probe(page: Page, items: Locator): Promise<Probe> {
  await expect(items.first()).toBeVisible();
  await page.waitForTimeout(300); // let the open transition and floating-ui settle
  return items.evaluateAll((els) => ({
    vw: window.innerWidth,
    vh: window.innerHeight,
    rows: els.map((el) => {
      // A long list scrolls inside its dropdown: bring each option into the scroll area first (the dropdown is
      // position:fixed, so this never scrolls the page), then measure it as a user would see it.
      el.scrollIntoView({ block: 'nearest' });
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return {
        text: (el.textContent ?? '').trim().slice(0, 40),
        ok: !!hit && (hit === el || el.contains(hit)),
        by: hit ? `${hit.tagName} ${(hit as HTMLElement).className?.toString().slice(0, 40)}` : 'none',
        top: Math.round(r.top),
        bottom: Math.round(r.bottom),
        left: Math.round(r.left),
        right: Math.round(r.right),
      };
    }),
  }));
}

export function expectReachable(p: Probe, what: string): void {
  test.info().annotations.push({
    type: 'probe',
    description: `${what}: ${JSON.stringify(p)}`,
  });
  expect(p.rows.length, `${what}: lists options`).toBeGreaterThan(0);
  expect(
    p.rows.filter((r) => !r.ok),
    `${what}: every option hit-tests to itself`,
  ).toEqual([]);
  expect(
    p.rows.filter((r) => r.top < 0 || r.bottom > p.vh || r.left < 0 || r.right > p.vw),
    `${what}: every option inside the viewport`,
  ).toEqual([]);
}

export async function press(page: Page, target: Locator): Promise<void> {
  if (test.info().project.name === 'pixel7') await target.tap();
  else await target.click();
}

