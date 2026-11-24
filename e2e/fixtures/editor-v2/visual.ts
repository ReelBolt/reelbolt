import * as fs from 'fs';
import * as path from 'path';
import type { Page } from '@playwright/test';
import type { EditorPage } from './editor-page';

/** The workspaces the visual sweep checks, in bar order. */
export const VISUAL_WORKSPACES = ['media', 'cut', 'edit', 'compose', 'color', 'audio', 'deliver'] as const;
export type VisualWorkspace = (typeof VISUAL_WORKSPACES)[number];

export const VIEWPORTS = [
  { name: '1440x900', width: 1440, height: 900 },
  { name: '1024x768', width: 1024, height: 768 },
] as const;

export interface Overlap {
  a: string;
  b: string;
  area: number;
}

/** Waits for a workspace to settle (fonts, first paint) before it is measured. */
export async function settle(page: Page): Promise<void> {
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  await page.waitForTimeout(400);
}

/**
 * Opens a workspace on a (possibly resized) desktop page, in the state with the most controls on screen: a clip selected (so the
 * inspector, Compose and Color have a subject), Color's Advanced section open, and the mixer's Advanced audio switched on
 * (EQ, buses, automation).
 */
export async function openWorkspace(ed: EditorPage, workspace: VisualWorkspace): Promise<void> {
  await ed.workspace('edit');
  await ed.selectClipAt(30);
  if (workspace !== 'edit') await ed.workspace(workspace);
  if (workspace === 'color') await ed.page.getByTestId('color-workspace').waitFor({ state: 'visible' });
  if (workspace === 'audio') {
    const advanced = ed.page.getByRole('switch', { name: /advanced audio/i });
    if (await advanced.isVisible().catch(() => false)) {
      if (!(await advanced.isChecked())) await advanced.check({ force: true });
    }
  }
  await settle(ed.page);
}

/**
 * Measures every interactive element's on-screen box (clipped by any scrolling or hidden-overflow
 * ancestor, so a control scrolled out of view does not count) and returns each pair that overlaps by
 * more than a 2 x 2 px area. A control that contains another (a label around its input, a tab around
 * its icon) is one control, not a collision.
 */
export async function findCollisions(page: Page): Promise<Overlap[]> {
  return page.evaluate(() => {
    const SELECTOR = [
      'button', 'a[href]', 'input:not([type="hidden"])', 'select', 'textarea', 'summary',
      '[role="button"]', '[role="tab"]', '[role="switch"]', '[role="slider"]', '[role="checkbox"]', '[role="radio"]',
      '[role="menuitem"]', '[role="combobox"]', '[role="option"]', '[contenteditable="true"]',
    ].join(',');
    const describe = (el: Element): string => {
      const id = el.getAttribute('data-testid');
      const label = el.getAttribute('aria-label') ?? (el.textContent ?? '').trim().slice(0, 24);
      return `${el.tagName.toLowerCase()}${id ? `[${id}]` : ''}${label ? ` "${label}"` : ''}`;
    };
    interface R { l: number; t: number; r: number; b: number }
    const visibleRect = (el: Element): R | null => {
      const cs = getComputedStyle(el);
      if (cs.visibility === 'hidden' || cs.display === 'none' || Number(cs.opacity) === 0) return null;
      const b = el.getBoundingClientRect();
      if (b.width < 1 || b.height < 1) return null;
      let rect: R = { l: b.left, t: b.top, r: b.right, b: b.bottom };
      for (let p = el.parentElement; p; p = p.parentElement) {
        const ps = getComputedStyle(p);
        if (ps.display === 'none' || ps.visibility === 'hidden') return null;
        if (ps.overflowX !== 'visible' || ps.overflowY !== 'visible') {
          const pb = p.getBoundingClientRect();
          rect = { l: Math.max(rect.l, pb.left), t: Math.max(rect.t, pb.top), r: Math.min(rect.r, pb.right), b: Math.min(rect.b, pb.bottom) };
          if (rect.r - rect.l < 1 || rect.b - rect.t < 1) return null;
        }
      }
      rect = { l: Math.max(rect.l, 0), t: Math.max(rect.t, 0), r: Math.min(rect.r, window.innerWidth), b: Math.min(rect.b, window.innerHeight) };
      return rect.r - rect.l < 1 || rect.b - rect.t < 1 ? null : rect;
    };
    const items: Array<{ el: Element; rect: R }> = [];
    for (const el of Array.from(document.querySelectorAll(SELECTOR))) {
      if ((el as HTMLInputElement).disabled && el.tagName === 'INPUT') continue;
      if (el.closest('[aria-hidden="true"], [inert]')) continue;
      const rect = visibleRect(el);
      if (!rect) continue;
      // A control hidden underneath another surface (the app shell's alpha notice under the full-screen editor) is not on screen.
      const top = document.elementFromPoint((rect.l + rect.r) / 2, (rect.t + rect.b) / 2);
      if (top && !(top === el || el.contains(top) || top.contains(el))) continue;
      items.push({ el, rect });
    }
    const out: Array<{ a: string; b: string; area: number }> = [];
    // A control that spills out of its own panel lands on top of the neighbour (the viewer's transport over the inspector).
    const PANELS = ['panel-left', 'panel-viewer', 'panel-right', 'panel-timeline'];
    for (const item of items) {
      const raw = item.el.getBoundingClientRect();
      const clippedHere = raw.right > item.rect.r + 1 || raw.left < item.rect.l - 1;
      if (clippedHere) continue;
      let host: Element | null = item.el;
      while (host && !PANELS.includes(host.getAttribute('data-testid') ?? '')) host = host.parentElement;
      if (!host) continue;
      const box = host.getBoundingClientRect();
      if (raw.right > box.right + 1 || raw.left < box.left - 1) out.push({ a: describe(item.el), b: `${host.getAttribute('data-testid')} (spills out of its panel)`, area: Math.round(Math.max(raw.right - box.right, box.left - raw.left)) });
    }
    for (let i = 0; i < items.length; i++) {
      for (let j = i + 1; j < items.length; j++) {
        const x = items[i];
        const y = items[j];
        if (x.el.contains(y.el) || y.el.contains(x.el)) continue;
        // a label and its input / a Mantine input wrapper pair is one control
        const lab = x.el.closest('label');
        if (lab && lab === y.el.closest('label')) continue;
        const w = Math.min(x.rect.r, y.rect.r) - Math.max(x.rect.l, y.rect.l);
        const h = Math.min(x.rect.b, y.rect.b) - Math.max(x.rect.t, y.rect.t);
        if (w > 2 && h > 2) out.push({ a: describe(x.el), b: describe(y.el), area: Math.round(w * h) });
      }
    }
    return out;
  });
}

/** Where visual-sweep screenshots go (outside the repo). */
export const SHOTS_DIR = process.env.VISUAL_SHOTS_DIR ?? '';

export async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS_DIR) return;
  fs.mkdirSync(SHOTS_DIR, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS_DIR, `${name}.png`) });
}
