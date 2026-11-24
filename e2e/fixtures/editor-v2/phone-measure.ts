import { expect, type CDPSession, type Page } from '@playwright/test';

/**
 * Measurements and touch scripts shared by the phone acceptance spec (30-phone/acceptance.spec.ts). Pure helpers:
 * importing this file registers no tests.
 */

/** No horizontal page scroll: the document and the body are no wider than the window. */
export async function noHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => ({ doc: document.documentElement.scrollWidth - window.innerWidth, body: document.body.scrollWidth - window.innerWidth }));
  expect(overflow.doc, 'the page does not scroll sideways').toBeLessThanOrEqual(0);
  expect(overflow.body, 'the body does not scroll sideways').toBeLessThanOrEqual(0);
}

const CONTROLS = 'button, a[href], [role="button"], [role="tab"], [role="menuitem"], [role="switch"], [role="checkbox"], [role="radio"], input:not([type="hidden"]), select, textarea';

/** Rects of every visible interactive control inside `scope` (hidden, inert and off-screen scroll children skipped). */
export async function controlRects(page: Page, scope: string): Promise<Array<{ name: string; x: number; y: number; width: number; height: number; right: number; bottom: number }>> {
  return page.evaluate(({ sel, controls }) => {
    const root = document.querySelector(sel);
    if (!root) return [];
    const out: Array<{ name: string; x: number; y: number; width: number; height: number; right: number; bottom: number }> = [];
    for (const el of Array.from(root.querySelectorAll<HTMLElement>(controls))) {
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      if (cs.visibility === 'hidden' || cs.display === 'none' || r.width === 0 || r.height === 0) continue;
      if (el.closest('[hidden], [aria-hidden="true"], [inert]')) continue;
      // A control inside a deliberately scrolling row (chips, tiles) may sit past the edge until scrolled to.
      let scrolls = false;
      for (let p = el.parentElement; p && p !== root.parentElement; p = p.parentElement) {
        const o = getComputedStyle(p).overflowX;
        if ((o === 'auto' || o === 'scroll') && p.scrollWidth > p.clientWidth + 1) { scrolls = true; break; }
      }
      if (scrolls) continue;
      out.push({ name: `${el.tagName} ${el.getAttribute('data-testid') ?? el.getAttribute('aria-label') ?? (el.textContent ?? '').trim().slice(0, 20)}`, x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom });
    }
    return out;
  }, { sel: scope, controls: CONTROLS });
}

/** Controls inside `scope` smaller than 44x44 CSS px. */
export async function smallControls(page: Page, scope: string): Promise<string[]> {
  const rects = await controlRects(page, scope);
  return rects.filter((r) => r.width < 43.5 || r.height < 43.5).map((r) => `${r.name} ${Math.round(r.width)}x${Math.round(r.height)}`);
}

/** Controls inside `scope` that stick out of the window sideways (clipped). */
export async function clippedControls(page: Page, scope: string): Promise<string[]> {
  const vw = page.viewportSize()!.width;
  const rects = await controlRects(page, scope);
  return rects.filter((r) => r.x < -0.5 || r.right > vw + 0.5).map((r) => `${r.name} ${Math.round(r.x)}..${Math.round(r.right)} of ${vw}`);
}

/** The element is fully inside the window. */
export async function insideViewport(page: Page, testId: string): Promise<void> {
  const box = await page.getByTestId(testId).boundingBox();
  const vp = page.viewportSize()!;
  expect(box, `${testId} is on screen`).not.toBeNull();
  expect(box!.x, `${testId} left edge`).toBeGreaterThanOrEqual(-0.5);
  expect(box!.y, `${testId} top edge`).toBeGreaterThanOrEqual(-0.5);
  expect(box!.x + box!.width, `${testId} right edge`).toBeLessThanOrEqual(vp.width + 0.5);
  expect(box!.y + box!.height, `${testId} bottom edge`).toBeLessThanOrEqual(vp.height + 0.5);
}

export type Finger = { id: number; x: number; y: number };

/** A multi-finger touch script over CDP (Chromium only): start, interpolated moves, end. */
export class TouchScript {
  private constructor(readonly page: Page, private readonly cdp: CDPSession) {}
  static async on(page: Page): Promise<TouchScript> {
    return new TouchScript(page, await page.context().newCDPSession(page));
  }
  start(fingers: Finger[]) { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: fingers }); }
  move(fingers: Finger[]) { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: fingers }); }
  end() { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); }
  /** Moves every finger from `from` to `to` in `steps` frames. */
  async glide(from: Finger[], to: Finger[], steps = 12): Promise<void> {
    for (let i = 1; i <= steps; i++) {
      await this.move(from.map((f, k) => ({ id: f.id, x: f.x + ((to[k].x - f.x) * i) / steps, y: f.y + ((to[k].y - f.y) * i) / steps })));
      await this.page.waitForTimeout(16);
    }
  }
}

/** Counts every pointercancel the page sees from now on (capture phase, so nothing can hide one). */
export async function countPointerCancels(page: Page): Promise<void> {
  await page.evaluate(() => {
    const w = window as unknown as { __pointerCancels?: number };
    w.__pointerCancels = 0;
    window.addEventListener('pointercancel', () => { w.__pointerCancels = (w.__pointerCancels ?? 0) + 1; }, true);
  });
}
export const pointerCancels = (page: Page): Promise<number> => page.evaluate(() => (window as unknown as { __pointerCancels?: number }).__pointerCancels ?? -1);
