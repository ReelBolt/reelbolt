import { test as base, expect, request as pwRequest, type Page } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { EditorApi } from './api';
import { ARTIFACTS, ignoreHTTPSErrorsFor, storageStatePath } from './env';
import { picturesUrl, type PictureLayer } from './picture';

/**
 * Lane D (initiative editor-v2-playback-verify): fixtures for the picture-editor control suite
 * (`tests/editor-v2/2d-picture`). A light seed (one project, three small files, no video, no workflow
 * run) instead of the timeline seed: the picture editor needs only still images. No paid provider is
 * ever configured on the isolated stack, and nothing here calls one.
 */

export type PictureSeed = {
  projectId: string;
  /** 1920 x 1080 PNG, four flat quadrants: top-left red, top-right green, bottom-left blue, bottom-right yellow. */
  quad: string;
  /** 640 x 360 flat magenta PNG (a second image to place as a layer). */
  badge: string;
  /** A text file (must never be offered as an image). */
  notes: string;
  api: EditorApi;
};

/** The full layer as the API serves it (what the specs read back). */
export type FullLayer = PictureLayer & {
  role: string;
  opacity: number;
  locked?: boolean;
  blendMode?: string;
  transform: { x: number; y: number; scale: number; rotation: number; flipX?: boolean; flipY?: boolean };
  content?: {
    text: string; fontFamily: string; fontWeight: number; fontSize: number; lineHeight: number; letterSpacing: number;
    align: string; color: string; uppercase: boolean; maxWidth: number | null;
    stroke: { color: string; width: number } | null; shadow: { color: string; blur: number; offsetX: number; offsetY: number } | null;
  } | null;
};
export type FullPicture = {
  id: string; name: string | null; revision: number; canvasWidth: number; canvasHeight: number;
  backgroundColor: string | null; layers: FullLayer[]; sourceProjectFileId: string | null;
};

type Worker = { pics: PictureSeed };
type Fixtures = { editorSuiteOnly: void; api: EditorApi };

export const test = base.extend<Fixtures, Worker>({
  editorSuiteOnly: [
    async ({}, use) => {
      test.skip(process.env.EDITOR_E2E_SUITE !== '1', 'editor-v2 suite: run with -c playwright.editor-v2.config.ts');
      await use();
    },
    { auto: true },
  ],
  storageState: async ({ baseURL }, use) => {
    await use(storageStatePath(baseURL!));
  },
  pics: [
    async ({}, use, workerInfo) => {
      if (process.env.EDITOR_E2E_SUITE !== '1') {
        await use(undefined as unknown as PictureSeed);
        return;
      }
      const baseURL = workerInfo.project.use.baseURL as string;
      const request = await pwRequest.newContext({ baseURL, storageState: storageStatePath(baseURL), ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL) });
      const api = new EditorApi(request);
      const run = process.env.EDITOR_E2E_RUN ?? 'run';
      const project = await api.createProject(`pic-${run}-${workerInfo.project.name}-w${workerInfo.workerIndex}`);
      const dir = path.join(ARTIFACTS, 'media', 'picture-lane');
      fs.mkdirSync(dir, { recursive: true });
      const quadFile = path.join(dir, 'quad.png');
      fs.writeFileSync(quadFile, quadrantPng(1920, 1080));
      const badgeFile = path.join(dir, 'badge.png');
      fs.writeFileSync(badgeFile, solidPng(640, 360, [255, 0, 255]));
      const notesFile = path.join(dir, 'notes.txt');
      fs.writeFileSync(notesFile, 'not a picture\n');
      const quad = await api.upload(project.id, quadFile, 'image/png');
      const badge = await api.upload(project.id, badgeFile, 'image/png');
      const notes = await api.upload(project.id, notesFile, 'text/plain');
      await use({ projectId: project.id, quad, badge, notes, api });
      await request.dispose();
    },
    { scope: 'worker', timeout: 120_000 },
  ],
  api: async ({ pics }, use) => {
    await use(pics.api);
  },
});

export { expect };

/* ------------------------------------------------------------------ PNG making (no dependencies) */

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf: Buffer): number {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
  return Buffer.concat([head, data, crc]);
}

/** An 8-bit RGB PNG whose pixel at (x, y) is `pixel(x, y)`. */
export function makePng(width: number, height: number, pixel: (x: number, y: number) => [number, number, number]): Buffer {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // RGB
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    const row = y * (width * 3 + 1);
    raw[row] = 0;
    for (let x = 0; x < width; x++) {
      const [r, g, b] = pixel(x, y);
      raw[row + 1 + x * 3] = r;
      raw[row + 2 + x * 3] = g;
      raw[row + 3 + x * 3] = b;
    }
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

export const QUADRANT = { tl: [220, 30, 30], tr: [30, 200, 30], bl: [30, 30, 220], br: [230, 220, 30] } as const;

export function quadrantPng(width: number, height: number): Buffer {
  return makePng(width, height, (x, y) => [...(y < height / 2 ? (x < width / 2 ? QUADRANT.tl : QUADRANT.tr) : x < width / 2 ? QUADRANT.bl : QUADRANT.br)] as [number, number, number]);
}

export function solidPng(width: number, height: number, rgb: [number, number, number]): Buffer {
  return makePng(width, height, () => rgb);
}

/* ------------------------------------------------------------------ reading what a file really is */

export type Decoded = { width: number; height: number; at: (x: number, y: number) => [number, number, number, number] };

/** Decodes an image file with the host ffmpeg (any format ffmpeg reads: PNG, JPEG, WebP) into RGBA pixels. */
export function decodeImage(file: string): Decoded {
  const probe = JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'json', file]).toString()) as {
    streams: Array<{ width: number; height: number }>;
  };
  const { width, height } = probe.streams[0];
  const raw = execFileSync('ffmpeg', ['-v', 'error', '-i', file, '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'rgba', '-'], { maxBuffer: 512 * 1024 * 1024 });
  return {
    width,
    height,
    at: (x, y) => {
      const i = (y * width + x) * 4;
      return [raw[i], raw[i + 1], raw[i + 2], raw[i + 3]];
    },
  };
}

/** True when each channel is within `tol` of the expected colour. */
export function near(actual: ArrayLike<number>, expected: ArrayLike<number>, tol = 24): boolean {
  for (let i = 0; i < 3; i++) if (Math.abs(actual[i] - expected[i]) > tol) return false;
  return true;
}

/** Saves a Playwright download under the artifact folder and returns its path. */
export async function saveDownload(download: { suggestedFilename(): string; saveAs(p: string): Promise<void> }, tag: string): Promise<string> {
  const dir = path.join(ARTIFACTS, 'downloads');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${tag}-${Date.now()}-${download.suggestedFilename()}`);
  await download.saveAs(file);
  return file;
}

/* ------------------------------------------------------------------ pictures */

export const getFull = (api: EditorApi, projectId: string, id: string): Promise<FullPicture> => api.json<FullPicture>('GET', picturesUrl(projectId, id));

export async function createFull(api: EditorApi, projectId: string, fileId: string, name: string): Promise<FullPicture> {
  const created = await api.json<{ id: string }>('POST', picturesUrl(projectId), { sourceProjectFileId: fileId, name }, [200, 201]);
  return getFull(api, projectId, created.id);
}

/** Polls the stored picture until `pick` returns a truthy value. */
export async function pollPicture<T>(api: EditorApi, projectId: string, id: string, pick: (p: FullPicture) => T, message: string): Promise<T> {
  let last: T | undefined;
  await expect.poll(async () => {
    last = pick(await getFull(api, projectId, id));
    return Boolean(last);
  }, { message, timeout: 20_000, intervals: [250, 500, 1000] }).toBe(true);
  return last as T;
}

export const isPhone = (name: string): boolean => name !== 'desktop';

/** A tap on phones (touch), a click on desktop. */
export async function press(locator: ReturnType<Page['locator']>, phone: boolean): Promise<void> {
  if (phone) await locator.tap();
  else await locator.click();
}

export const stageOf = (page: Page) => page.locator('[role=application][aria-label="Thumbnail canvas"]');

/** Opens the Layers / Text / AI / Canvas tab. */
export async function openTab(page: Page, name: 'Layers' | 'Text' | 'AI' | 'Canvas'): Promise<void> {
  await page.getByRole('tab', { name, exact: true }).click();
}

/* ------------------------------------------------------------------ fresh projects */

/** A project of its own with the seed images in it (for tests that need an empty hub or a known file list). */
export async function freshProject(api: EditorApi, tag: string, withImages = true): Promise<{ id: string; name: string; quad?: string; badge?: string }> {
  const project = await api.createProject(`pic-${tag}-${Date.now()}`);
  if (!withImages) return project;
  const dir = path.join(ARTIFACTS, 'media', 'picture-lane');
  fs.mkdirSync(dir, { recursive: true });
  const quadFile = path.join(dir, 'quad.png');
  if (!fs.existsSync(quadFile)) fs.writeFileSync(quadFile, quadrantPng(1920, 1080));
  const quad = await api.upload(project.id, quadFile, 'image/png');
  return { ...project, quad };
}

export const hubUrl = (projectId: string): string => `/app/editor?project=${projectId}&view=pictures`;

/* ------------------------------------------------------------------ editing helpers */

import { openPicture } from './picture';

/** Creates a picture of the seed photo and opens it; returns what a test needs. */
export async function openFresh(page: Page, api: EditorApi, pics: PictureSeed, tag: string, file: string = pics.quad) {
  const pic = await createFull(api, pics.projectId, file, `${tag}-${Date.now()}`);
  const stage = await openPicture(page, pics.projectId, pic.id);
  return { pic, stage };
}

/** Waits for the editor's save badge to say everything is saved (the autosave runs 700 ms after the last edit). */
export async function savedBadge(page: Page): Promise<void> {
  await expect(page.getByRole('status').filter({ hasText: /All changes saved/ }).first()).toBeVisible({ timeout: 20_000 });
}

/** The row of one layer in the Layers panel, by its label. */
export const layerRow = (page: Page, label: string | RegExp) =>
  page.getByRole('listitem').filter({ has: page.getByRole('button', { name: typeof label === 'string' ? `Select ${label}` : label }) });

/** Adds a text layer through the REST API (so a test can edit one without the Text tool), returns it. */
export async function addTextLayer(
  api: EditorApi, projectId: string, docId: string, text: string, at = { x: 300, y: 300 },
): Promise<FullLayer> {
  return api.json<FullLayer>('POST', `${picturesUrl(projectId, docId)}/layers/text`, {
    content: {
      text, fontFamily: 'Inter', fontWeight: 700, fontSize: 120, lineHeight: 1.1, letterSpacing: 0, align: 'left',
      color: '#ffffff', stroke: null, shadow: null, maxWidth: null, uppercase: false,
    },
    transform: { x: at.x, y: at.y, scale: 1, rotation: 0 },
    role: 'overlay',
    label: 'Headline',
  }, [200, 201]);
}

/** Places the seed badge (an opaque 640x360 magenta image) as a layer through the REST API. */
export async function addBadgeLayer(api: EditorApi, projectId: string, docId: string, pics: PictureSeed): Promise<FullLayer> {
  return api.json<FullLayer>('POST', `${picturesUrl(projectId, docId)}/layers/from-file`, { projectFileId: pics.badge, role: 'overlay' }, [200, 201]);
}

export const layerOf = (p: FullPicture, id: string): FullLayer => p.layers.find((l) => l.id === id)!;

/* ------------------------------------------------------------------ canvas geometry */

/**
 * Where the document sits on screen: the stage's first SVG group is <g transform="translate(tx ty) scale(zoom)"> in stage pixels,
 * i.e. the document origin and the zoom. From it a document point maps to the page.
 */
export async function stageInView(page: Page): Promise<void> {
  // Phones stack the stage above the panel under a sticky header: put the stage's top just below it so every point is on screen.
  await stageOf(page).evaluate((el) => window.scrollBy(0, el.getBoundingClientRect().top - 90));
}

export async function screenMapper(page: Page, _layer?: FullLayer) {
  await stageInView(page);
  const stage = stageOf(page);
  const box = (await stage.boundingBox())!;
  const attr = await stage.locator('svg > g').first().getAttribute('transform');
  const m = /translate\(([-\d.e]+) ([-\d.e]+)\) scale\(([-\d.e]+)\)/.exec(attr ?? '');
  if (!m) throw new Error(`no document group on the stage (${attr})`);
  const [tx, ty, zoom] = [Number(m[1]), Number(m[2]), Number(m[3])];
  return {
    zoom,
    /** Page coordinates of the document point (x, y). */
    toPage: (x: number, y: number) => ({ x: box.x + tx + x * zoom, y: box.y + ty + y * zoom }),
    box,
  };
}

/** The "Zoom N%" readout of the canvas, as a number. */
export async function zoomPercent(page: Page): Promise<number> {
  const label = (await page.getByRole('button', { name: /^Zoom \d+%/ }).getAttribute('aria-label')) ?? '';
  return Number(/Zoom (\d+)%/.exec(label)![1]);
}

export const handles = (page: Page) => page.locator('svg rect[fill="#ffffff"]');
export const rotateHandle = (page: Page) => page.locator('svg circle[fill="#101218"]');
