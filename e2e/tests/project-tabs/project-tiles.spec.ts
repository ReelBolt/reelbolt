import { expect, test, type Locator, type Page } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import * as os from 'os';
import * as path from 'path';
import { loginAs } from '../../fixtures/stack';

/**
 * Project Overview stat tiles and the "latest renders" thumbnails (initiative project-tabs-phone-overlap).
 * The usability stack has no rendered videos, so the outputs list and the download bytes are mocked in the
 * browser (real <video> decoding of a real clip, with Range support like the API's download route).
 * Screenshots: PROJECT_TABS_SHOTS (optional), file names prefixed with PROJECT_TILES_PREFIX (default "after").
 */
const SHOTS = process.env.PROJECT_TABS_SHOTS;
const PREFIX = process.env.PROJECT_TILES_PREFIX ?? 'after';
if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });

// Playwright's Chromium has no H.264 decoder; WebKit and real phones do. Same clip, different codec.
function clip(codec: 'h264' | 'vp9'): Buffer {
  const file = path.join(os.tmpdir(), `project-tiles-${codec}.mp4`);
  if (!fs.existsSync(file)) {
    const enc = codec === 'h264' ? ['-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-movflags', '+faststart'] : ['-c:v', 'libvpx-vp9', '-pix_fmt', 'yuv420p', '-f', 'mp4'];
    // A bright test card that fades in from black over the first 20 frames, like a real render.
    execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=s=640x360:r=30:d=5', '-vf', 'fade=in:0:20', ...enc, file]);
  }
  return fs.readFileSync(file);
}

/** A real HTTP server with Range support (WebKit will not play media that Playwright fulfils in the route handler). */
async function serveClip(bytes: Buffer): Promise<{ url: string; close: () => void }> {
  const server = http.createServer((req, res) => {
    const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range ?? '');
    if (!range) {
      res.writeHead(200, { 'content-type': 'video/mp4', 'accept-ranges': 'bytes', 'content-length': bytes.length });
      res.end(bytes);
      return;
    }
    const start = range[1] ? Number(range[1]) : 0;
    const end = Math.min(range[2] ? Number(range[2]) : bytes.length - 1, bytes.length - 1);
    res.writeHead(206, { 'content-type': 'video/mp4', 'accept-ranges': 'bytes', 'content-range': `bytes ${start}-${end}/${bytes.length}`, 'content-length': end - start + 1 });
    res.end(bytes.subarray(start, end + 1));
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as { port: number };
  return { url: `http://127.0.0.1:${port}/clip.mp4`, close: () => { server.close(); } };
}

async function mockRenders(page: Page, projectId: string, codec: 'h264' | 'vp9'): Promise<() => void> {
  const clipServer = await serveClip(clip(codec));
  const now = Date.now();
  const render = (i: number, takeGroupKey: string, takeIndex: number) => ({
    stepResultId: `sr-${i}`, workflowExecutionId: `ex-${i}`, workflowStepId: `st-${takeGroupKey}`, storageKey: `k${i}`,
    fileName: `clip-${i}.mp4`, producedAt: new Date(now - i * 3_600_000).toISOString(), workflowName: 'Demo', runNumber: i,
    executionStatus: 'Completed', durationSec: 5, width: 640, height: 360, isFinal: true, takeGroupKey, takeIndex, takeCount: 6, isLatestTake: takeIndex === 1,
  });
  // 3 videos, the first with 3 earlier takes: "Videos 3 - 3 earlier versions".
  const outputs = [render(1, 'a', 1), render(2, 'a', 2), render(3, 'a', 3), render(4, 'a', 4), render(5, 'b', 1), render(6, 'c', 1)];
  await page.route(`**/api/v1/projects/${projectId}/outputs`, (route) => route.fulfill({ json: outputs }));
  // WebKit media loads bypass page.route(); there the download path must be served by the stack in front (TILES_CLIP_UPSTREAM=1).
  if (!process.env.TILES_CLIP_UPSTREAM) await page.route(`**/api/v1/projects/${projectId}/outputs/*/download**`, (route) => route.continue({ url: clipServer.url }));
  // Uploaded files with a size, so the first tile carries its secondary text too.
  await page.route('**/api/v1/dashboard/overview', async (route) => {
    const res = await route.fetch();
    const body = (await res.json()) as { projects: { projectId: string; sourceFileCount: number; totalBytes: number }[] };
    for (const p of body.projects) if (p.projectId === projectId) { p.sourceFileCount = 4; p.totalBytes = 4_400_000; }
    await route.fulfill({ status: res.status(), json: body });
  });
  return clipServer.close;
}

function summaryLabel(locale: 'en' | 'it'): string {
  const file = path.join(__dirname, '../../../web/messages', locale, 'projects.json');
  return (JSON.parse(fs.readFileSync(file, 'utf8')) as { sections: { summary: string } }).sections.summary;
}


type Locale = 'en' | 'it';

async function withOverview(page: Page, browserName: string, baseURL: string, locale: Locale, run: (figures: Locator) => Promise<void>): Promise<void> {
  await loginAs(page, 'creator');
  const created = await page.context().request.post(`${baseURL}/api/v1/projects`, { data: { name: `tiles-${locale}-${Date.now()}`, description: 'scratch' } });
  expect(created.ok()).toBeTruthy();
  const id = ((await created.json()) as { id: string }).id;
  let closeClip = () => {};
  try {
    await page.context().addCookies([{ name: 'rb_locale', value: locale, url: baseURL }]);
    closeClip = await mockRenders(page, id, browserName === 'webkit' ? 'h264' : 'vp9');
    await page.goto(`/app/projects/${id}`);
    const figures = page.locator(`[aria-label="${summaryLabel(locale)}"]`);
    await expect(figures).toBeVisible({ timeout: 30_000 });
    await page.waitForLoadState('networkidle');
    await run(figures);
  } finally {
    closeClip();
    await page.context().request.delete(`${baseURL}/api/v1/projects/${id}`);
  }
}

for (const locale of ['en', 'it'] as const) {
  test(`project overview: stat tile labels wrap at most 2 lines and never overflow (${locale})`, async ({ page, browserName }, info) => {
    await withOverview(page, browserName, info.project.use.baseURL!, locale, async (figures) => {
      if (SHOTS) await page.screenshot({ path: `${SHOTS}/${PREFIX}-${info.project.name}-${locale}.png` });
      // Every text line of a tile (its label and its secondary text) wraps at most 2 lines and stays inside the tile.
      const tiles = await figures.evaluate((root) =>
        Array.from(root.children).flatMap((tile) => {
          const tr = tile.getBoundingClientRect();
          return Array.from(tile.children).slice(1).map((label) => {
            const lr = label.getBoundingClientRect();
            const cs = getComputedStyle(label);
            const lh = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.4;
            return {
              text: (label.textContent ?? '').trim(), tileTop: tr.top, tileRight: tr.right, labelRight: lr.right,
              lines: Math.round(lr.height / lh), labelHeight: lr.height, lineHeight: lh, clipped: label.scrollWidth > (label as HTMLElement).clientWidth + 1,
            };
          });
        }),
      );
      const tileCount = await figures.evaluate((root) => root.children.length);
      expect(tileCount).toBe(4);
      const vw = page.viewportSize()!.width;
      for (const t of tiles) {
        expect(t.labelHeight, `"${t.text}" wraps to ${t.lines} lines`).toBeLessThanOrEqual(t.lineHeight * 2 + 1);
        expect(t.clipped, `"${t.text}" is wider than its tile`).toBe(false);
        expect(t.labelRight, `"${t.text}" runs past its tile`).toBeLessThanOrEqual(t.tileRight + 0.5);
        expect(t.tileRight, `tile of "${t.text}" runs past the viewport`).toBeLessThanOrEqual(vw + 0.5);
      }
      if (vw < 500) {
        // Phone: two rows of two, no tile left alone on a row beside empty space.
        const rows = new Set(tiles.map((t) => Math.round(t.tileTop)));
        expect(rows.size, 'tiles form two rows of two on a phone').toBe(2);
      }
      const overflowX = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflowX, 'page scrolls sideways').toBeLessThanOrEqual(0);
    });
  });

  test(`project overview: latest-render thumbnails paint a frame, not black (${locale})`, async ({ page, browserName }, info) => {
    test.skip(browserName === 'webkit' && !process.env.TILES_CLIP_UPSTREAM, 'WebKit media loads bypass page.route(); set TILES_CLIP_UPSTREAM=1 with a proxy that serves the download path');
    await withOverview(page, browserName, info.project.use.baseURL!, locale, async () => {
      const videos = page.locator('video');
      await expect(videos.first()).toBeAttached({ timeout: 30_000 });
      await expect.poll(async () => videos.first().evaluate((v: HTMLVideoElement) => v.readyState), { timeout: 20_000, message: 'thumbnail video has a decoded frame' }).toBeGreaterThanOrEqual(2);
      // Brightness of what is actually painted in the first card (a screenshot, so it works for any codec and browser).
      const thumb = page.locator('[class*=thumb]').first();
      await expect.poll(async () => {
        const png = (await thumb.screenshot()).toString('base64');
        return page.evaluate(async (b64) => {
          const img = new Image();
          img.src = `data:image/png;base64,${b64}`;
          await img.decode();
          const c = document.createElement('canvas');
          c.width = 32; c.height = 18;
          const ctx = c.getContext('2d')!;
          ctx.drawImage(img, 0, 0, 32, 18);
          const d = ctx.getImageData(0, 0, 32, 18).data;
          let sum = 0;
          for (let i = 0; i < d.length; i += 4) sum += (d[i] + d[i + 1] + d[i + 2]) / 3;
          return sum / (d.length / 4);
        }, png);
      }, { timeout: 20_000, message: 'thumbnail frame is not black' }).toBeGreaterThan(25);
    });
  });
}
