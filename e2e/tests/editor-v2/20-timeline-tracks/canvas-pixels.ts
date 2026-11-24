import type { Locator, Page } from '@playwright/test';

export type Rect = { x: number; y: number; width: number; height: number };

/** Decodes a PNG screenshot of `rect` (page coordinates) and returns its RGB pixels. */
async function pixels(page: Page, rect: Rect): Promise<{ w: number; h: number; rgb: number[] }> {
  const png = await page.screenshot({ clip: rect, animations: 'disabled' });
  return page.evaluate(async (b64) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas');
    c.width = img.width;
    c.height = img.height;
    const ctx = c.getContext('2d')!;
    ctx.drawImage(img, 0, 0);
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    const rgb: number[] = [];
    for (let i = 0; i < d.length; i += 4) rgb.push(d[i], d[i + 1], d[i + 2]);
    return { w: c.width, h: c.height, rgb };
  }, png.toString('base64'));
}

/** RGB of the pixel at page point (x, y). */
export async function rgbAt(page: Page, x: number, y: number): Promise<[number, number, number]> {
  const p = await pixels(page, { x: Math.floor(x), y: Math.floor(y), width: 2, height: 2 });
  return [p.rgb[0], p.rgb[1], p.rgb[2]];
}

/** How varied a rectangle of the screen is: distinct (quantised) colours and the share of bright pixels. */
export async function describeRect(page: Page, rect: Rect): Promise<{ colours: number; bright: number }> {
  const p = await pixels(page, rect);
  const seen = new Set<number>();
  let bright = 0;
  const n = p.rgb.length / 3;
  for (let i = 0; i < p.rgb.length; i += 3) {
    const [r, g, b] = [p.rgb[i], p.rgb[i + 1], p.rgb[i + 2]];
    seen.add(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
    if (r > 200 && g > 200 && b > 200) bright++;
  }
  return { colours: seen.size, bright: bright / n };
}

export async function boxOf(locator: Locator): Promise<Rect> {
  const box = await locator.boundingBox();
  if (!box) throw new Error('element has no box');
  return box;
}
