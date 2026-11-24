/**
 * Marketing screenshots for the blog, taken from the isolated e2e stack with invented demo data only.
 *
 *   source e2e/.stack/<project>/env.sh           # BASE_URL, E2E_STACK_DIR (users.json lives there)
 *   MARKETING_WORK_DIR=/tmp/...  node --experimental-strip-types e2e/scripts/marketing-screenshots.ts seed
 *   MARKETING_WORK_DIR=/tmp/...  node --experimental-strip-types e2e/scripts/marketing-screenshots.ts capture
 *
 * `seed` creates one demo user (invented address, @acme-notes.example) through the admin API, then uses
 * that user's own session to create the "Acme Notes — launch promo" project, upload synthetic media,
 * apply a workflow template, write a timeline and a picture document, and import a CSS theme as the
 * brand kit. Nothing here touches the seeded e2e users' data beyond reading `users.json` for the admin.
 *
 * `capture` drives a headless Chromium at 1440x900 (deviceScaleFactor 2) and writes raw PNGs to
 * MARKETING_WORK_DIR/screens-raw. Each PNG's visible text is checked against the banned-word list
 * before the file is kept; hits are reported, never silently written to the final set.
 *
 * Erasable TypeScript only (no enums, no parameter properties) so Node's built-in type stripping runs it.
 */
import { chromium, request as pwRequest, type APIRequestContext, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { randomBytes } from 'node:crypto';

const BASE_URL = process.env.BASE_URL || 'http://127.0.0.1:8117';
const STACK_DIR = process.env.E2E_STACK_DIR || '';
const WORK_DIR = process.env.MARKETING_WORK_DIR || fs.mkdtempSync(path.join(os.tmpdir(), 'marketing-shots-'));
const RAW_DIR = path.join(WORK_DIR, 'screens-raw');
const MEDIA_DIR = path.join(WORK_DIR, 'media');

const PROJECT_NAME = 'Acme Notes — launch promo';
const DEMO_EMAIL = process.env.DEMO_EMAIL || 'maya.demo@acme-notes.example';
const DEMO_NAME = 'Maya Demo (Acme Notes)';

/** Words that must not appear in any kept screenshot's visible text (plan.md Global constraints). */
const BANNED = [
  'ffmpeg', 'remotion', 'docker', 'postgres', 'postgresql', 'rabbitmq', 'qdrant', 'workflow engine',
  'minimax', 'higgsfield', 'fal.ai', 'fish audio', 'comfyui', 'typesafe', 'paddle',
];

function log(msg: string): void {
  process.stdout.write(`[marketing] ${msg}\n`);
}

function readJson<T>(file: string): T {
  return JSON.parse(fs.readFileSync(file, 'utf8')) as T;
}

// ------------------------------------------------------------------ API helpers

interface Session {
  token: string;
  org: string;
}

async function signInApi(api: APIRequestContext, email: string, password: string): Promise<Session> {
  for (let attempt = 1; ; attempt++) {
    const res = await api.post(`${BASE_URL}/api/v1/auth/token`, { data: { email, password } });
    if (res.status() === 429 && attempt < 8) {
      await new Promise((r) => setTimeout(r, 200 * attempt + Math.floor(Math.random() * 250)));
      continue;
    }
    if (res.status() !== 200) throw new Error(`sign-in ${email} failed: ${res.status()} ${await res.text()}`);
    const body = (await res.json()) as { accessToken: string; org: string };
    return { token: body.accessToken, org: body.org };
  }
}

async function must<T>(label: string, res: { status(): number; text(): Promise<string>; json(): Promise<unknown> }): Promise<T> {
  if (res.status() >= 300) throw new Error(`${label}: HTTP ${res.status()} ${(await res.text()).slice(0, 400)}`);
  return (await res.json()) as T;
}

function auth(s: Session): Record<string, string> {
  return { Authorization: `Bearer ${s.token}` };
}

// ------------------------------------------------------------------ synthetic media

function makeMedia(): void {
  fs.mkdirSync(MEDIA_DIR, { recursive: true });
  const ff = (args: string[]) => execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...args], { stdio: 'inherit' });
  // Product clip: the colour-bar test pattern with a 440 Hz tone (requested command, 12 s).
  ff(['-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=30', '-f', 'lavfi', '-i', 'sine=frequency=440',
    '-t', '12', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', path.join(MEDIA_DIR, 'acme-product-demo.mp4')]);
  // Title card: flat colour with drawtext, 8 s, silent audio track.
  ff(['-f', 'lavfi', '-i', 'color=c=0x0f766e:s=1920x1080:r=30', '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo',
    '-t', '8', '-vf',
    "drawtext=text='Acme Notes':fontcolor=white:fontsize=140:x=(w-text_w)/2:y=(h-text_h)/2-60,"
      + "drawtext=text='Launch week':fontcolor=white:fontsize=56:x=(w-text_w)/2:y=(h-text_h)/2+90",
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', path.join(MEDIA_DIR, 'acme-launch-card.mp4')]);
  // Logo: a single PNG frame, invented monogram.
  ff(['-f', 'lavfi', '-i', 'color=c=0x0f766e:s=512x512', '-vf',
    "drawtext=text='AN':fontcolor=white:fontsize=240:x=(w-text_w)/2:y=(h-text_h)/2",
    '-frames:v', '1', path.join(MEDIA_DIR, 'acme-logo.png')]);
  fs.writeFileSync(path.join(MEDIA_DIR, 'README.md'), [
    '# Acme Notes (demo product)',
    '',
    'Acme Notes is an invented note-taking app used only for marketing screenshots.',
    'It keeps short notes in folders and syncs them between invented devices.',
    '',
  ].join('\n'));
  // Brand kit source: a DTCG tokens file (the format the design-system import accepts) with invented values.
  const token = (value: string, type: string) => ({ $value: value, $type: type });
  fs.writeFileSync(path.join(MEDIA_DIR, 'acme-tokens.json'), `${JSON.stringify({
    color: {
      brand: { primary: token('#0f766e', 'color'), accent: token('#f59e0b', 'color') },
      ink: { base: token('#111827', 'color') },
      paper: { base: token('#f8fafc', 'color') },
    },
    fontFamily: { heading: token('Chakra Petch', 'fontFamily'), body: token('Inter', 'fontFamily') },
  }, null, 2)}\n`);
}

// ------------------------------------------------------------------ seed

/** Runs one seed step; a failure is logged and returns undefined so the remaining steps still run. */
async function step<T>(label: string, fn: () => Promise<T>): Promise<T | undefined> {
  try {
    const out = await fn();
    log(`ok: ${label}`);
    return out;
  } catch (err) {
    log(`FAILED: ${label}: ${err instanceof Error ? err.message.split('\n')[0] : String(err)}`);
    return undefined;
  }
}

/** Deterministic GUIDs for timeline item ids (the server types them as Guid). */
const guid = (n: number): string => `00000000-0000-4000-8000-${n.toString(16).padStart(12, '0')}`;

/** A 30 fps, 1920x1080 timeline: the product clip (12 s) and the title card (8 s) on V1, the tone under it on A1. */
function timelineDocument(projectId: string, timelineId: string, files: Record<string, string>) {
  const clip = (n: number, fileId: string, recordIn: number, recordOut: number, sourceIn: number) => ({
    id: guid(n), type: 'mediaClip', name: `Clip ${n}`, recordIn, recordOut,
    assetRef: { kind: 'projectFile', projectFileId: fileId }, sourceIn, playbackRate: 1, enabled: true, provenance: null,
  });
  const track = (n: number, kind: 'video' | 'audio', name: string, items: unknown[]) => ({
    id: guid(n), kind, name, height: 64, locked: false, enabled: true, mute: false, solo: false, items,
  });
  const demo = files['acme-product-demo.mp4'];
  const card = files['acme-launch-card.mp4'];
  return {
    schemaVersion: 2,
    id: timelineId,
    projectId,
    name: 'Launch cut, v2',
    settings: {
      width: 1920, height: 1080, rate: { num: 30, den: 1 }, sampleRate: 48000,
      pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0,
    },
    tracks: [
      track(1, 'video', 'V1', [
        clip(11, demo, 0, 90, 0),
        clip(12, card, 90, 330, 0),
        clip(13, demo, 330, 450, 120),
      ]),
      track(2, 'audio', 'A1', [clip(21, demo, 0, 330, 0)]),
    ],
    markers: [{ id: guid(31), frame: 90, name: 'Title card', color: null, note: null, durationFrames: null }],
    inOut: null,
    seed: null,
  };
}

interface DemoState {
  email: string;
  password: string;
  projectId: string;
  files: Record<string, string>;
  workflowId: string;
  timelineId: string;
  documentId: string;
  brandImported: boolean;
}

async function ensureDemoUser(api: APIRequestContext, adminSession: Session): Promise<{ email: string; password: string }> {
  const credsFile = path.join(WORK_DIR, 'demo-user.json');
  if (fs.existsSync(credsFile)) {
    const saved = readJson<{ email: string; password: string }>(credsFile);
    if (saved.email === DEMO_EMAIL) return saved;
  }
  const email = DEMO_EMAIL;
  const created = await api.post(`${BASE_URL}/api/v1/admin/users`, {
    headers: auth(adminSession), data: { email, displayName: DEMO_NAME },
  });
  if (created.status() === 409) {
    throw new Error(`demo user ${email} already exists but ${credsFile} is missing; set DEMO_EMAIL to a new address`);
  }
  const body = await must<{ user: { id: string }; temporaryPassword: string }>('create demo user', created);
  const password = `Demo-${randomBytes(9).toString('base64url')}-1a`;
  const own = await pwRequest.newContext({ baseURL: BASE_URL });
  try {
    const first = await signInApi(own, email, body.temporaryPassword);
    const cp = await own.post(`${BASE_URL}/api/v1/auth/change-password`, {
      headers: auth(first), data: { currentPassword: body.temporaryPassword, newPassword: password },
    });
    await must('change demo password', cp);
    const again = await signInApi(own, email, password);
    const legal = await must<{ termsVersion: string; privacyVersion: string }>('legal', await own.get(`${BASE_URL}/api/v1/legal/documents`));
    const at = await own.post(`${BASE_URL}/api/v1/auth/accept-terms`, {
      headers: auth(again), data: { acceptTerms: true, termsVersion: legal.termsVersion, privacyVersion: legal.privacyVersion },
    });
    await must('accept terms', at);
  } finally {
    await own.dispose();
  }
  const creds = { email, password };
  fs.writeFileSync(credsFile, JSON.stringify(creds, null, 2), { mode: 0o600 });
  return creds;
}

async function seed(): Promise<DemoState> {
  makeMedia();
  const users = readJson<Record<string, { email: string; password: string }>>(path.join(STACK_DIR, 'users.json'));
  const api = await pwRequest.newContext({ baseURL: BASE_URL });
  try {
    const admin = await signInApi(api, users.admin.email, users.admin.password);
    const demo = await ensureDemoUser(api, admin);
    const s = await signInApi(api, demo.email, demo.password);
    const H = auth(s);
    log(`signed in as demo user in org ${s.org}`);

    const projects = await must<Array<{ id: string; name: string }>>('list projects', await api.get(`${BASE_URL}/api/v1/projects`, { headers: H }));
    let project = projects.find((p) => p.name === PROJECT_NAME);
    if (!project) {
      // On the isolated stack without the workflow-engine tables, POST /projects answers 500 after the
      // project row is inserted (42P01 workflow_definitions). The row is what the seed needs, so the
      // seed looks it up by name instead of trusting the response.
      const created = await api.post(`${BASE_URL}/api/v1/projects`, {
        headers: H, data: { name: PROJECT_NAME, description: 'Invented demo product for marketing screenshots.' },
      });
      if (created.status() >= 300 && created.status() !== 500) await must('create project', created);
      if (created.status() === 500) log(`create project answered 500 (${(await created.text()).slice(0, 80)}); looking it up`);
      const after = await must<Array<{ id: string; name: string }>>('list projects', await api.get(`${BASE_URL}/api/v1/projects`, { headers: H }));
      project = after.find((p) => p.name === PROJECT_NAME);
      if (!project) throw new Error('project was not created');
    }
    const P = `${BASE_URL}/api/v1/projects/${project.id}`;
    log(`project ${project.id}`);

    const existing = await must<Array<{ id: string; originalFileName: string }>>('list files', await api.get(`${P}/files`, { headers: H }));
    const files: Record<string, string> = {};
    const wanted = ['acme-product-demo.mp4', 'acme-launch-card.mp4', 'acme-logo.png', 'README.md', 'acme-tokens.json'];
    // The first seed imported a CSS theme; the brand kit now takes DTCG tokens, so the stale file goes.
    for (const old of existing.filter((f) => f.originalFileName === 'acme-theme.css')) {
      await api.delete(`${P}/files/${old.id}`, { headers: H });
    }
    for (const name of wanted) {
      const hit = existing.find((f) => f.originalFileName === name);
      if (hit) { files[name] = hit.id; continue; }
      const mime = name.endsWith('.mp4') ? 'video/mp4' : name.endsWith('.png') ? 'image/png' : name.endsWith('.json') ? 'application/json' : 'text/markdown';
      const up = await must<{ id: string }>(`upload ${name}`, await api.post(`${P}/files`, {
        headers: H, multipart: { file: { name, mimeType: mime, buffer: fs.readFileSync(path.join(MEDIA_DIR, name)) } },
      }));
      files[name] = up.id;
      log(`uploaded ${name} -> ${up.id}`);
    }

    const state: DemoState = {
      email: demo.email, password: demo.password, projectId: project.id, files,
      workflowId: '', timelineId: '', documentId: '', brandImported: false,
    };

    // Workflow from a template (the catalog's own key, applied as the user would from the picker).
    const wf = await step('apply workflow template', async () => {
      const r = await api.post(`${P}/workflows/templates/quick-win-promo/apply`, { headers: H, data: { skipIfExists: true } });
      return must<{ id: string; name: string }>('apply template', r);
    });
    if (wf) state.workflowId = wf.id;

    // Timeline with the demo clips on it: the stack has no workflow engine, so there is no compile run to
    // seed from; the document is written directly with the same shape the editor's own fixtures use.
    const tl = await step('create timeline', async () => {
      const list = await must<Array<{ id: string; name: string }>>('list timelines', await api.get(`${P}/edit-timelines`, { headers: H }));
      const found = list.find((t) => t.name === 'Launch cut, v2');
      if (found) {
        const full = await must<{ id: string; revision: number }>('get timeline', await api.get(`${P}/edit-timelines/${found.id}`, { headers: H }));
        return { id: full.id, revision: full.revision };
      }
      return must<{ id: string; revision: number }>('create timeline', await api.post(`${P}/edit-timelines`, { headers: H, data: { name: 'Launch cut, v2' } }));
    });
    if (tl) {
      state.timelineId = tl.id;
      const doc = timelineDocument(project.id, tl.id, files);
      await step('write timeline clips', async () =>
        must('patch timeline', await api.patch(`${P}/edit-timelines/${tl.id}`, {
          headers: H, data: { baseRevision: tl.revision, ops: [], document: doc },
        })));
    }

    // Picture document: the logo as the base layer, plus a text layer.
    const doc = await step('create image document', async () => {
      const list = await must<Array<{ id: string; name: string }>>('list image documents', await api.get(`${P}/image-documents`, { headers: H }));
      const found = list.find((d) => d.name === 'Launch tile');
      if (found) return { id: found.id, existed: true };
      const made = await must<{ id: string }>('create image document', await api.post(`${P}/image-documents`, {
        headers: H, data: { sourceProjectFileId: files['acme-logo.png'], name: 'Launch tile' },
      }));
      return { id: made.id, existed: false };
    });
    if (doc) {
      state.documentId = doc.id;
      if (!doc.existed) await step('add text layer', async () =>
        must('text layer', await api.post(`${P}/image-documents/${doc.id}/layers/text`, {
          headers: H,
          data: {
            content: { text: 'Acme Notes', fontFamily: 'Chakra Petch', fontWeight: 700, fontSize: 64, color: '#ffffff', align: 'left' },
            transform: { x: 40, y: 380, scale: 1, rotation: 0 },
            label: 'Headline',
          },
        })));
    }

    // Brand kit: import the invented DTCG tokens as the project's design system, then approve the draft. An
    // import lands as a draft that a person approves before the profile shows; the demo user is that person here.
    // Re-runs reuse the current version and do not import again.
    await step('import and approve brand kit', async () => {
      type DS = { design: { currentVersion: unknown; latestDraft: { id: string; state: string } | null } };
      let ds = await must<DS>('design-system', await api.get(`${P}/design-system`, { headers: H }));
      if (!ds.design.currentVersion && !ds.design.latestDraft) {
        await must('design-system import', await api.post(`${P}/design-system/import`, {
          headers: H, data: { kinds: ['design'], projectFileIds: [files['acme-tokens.json']] },
        }));
        ds = await must<DS>('design-system', await api.get(`${P}/design-system`, { headers: H }));
      }
      // Earlier runs left pending drafts behind; approve until none is pending (capped, the tokens are the same).
      for (let i = 0; i < 5; i++) {
        const draft = ds.design.latestDraft;
        if (!draft || draft.state !== 'Draft') break;
        await must('approve draft', await api.post(`${P}/design-system/versions/${draft.id}/approve`, { headers: H }));
        ds = await must<DS>('design-system', await api.get(`${P}/design-system`, { headers: H }));
      }
      return true;
    });
    state.brandImported = true;

    fs.writeFileSync(path.join(WORK_DIR, 'seed-state.json'), JSON.stringify(state, null, 2), { mode: 0o600 });
    return state;
  } finally {
    await api.dispose();
  }
}

// ------------------------------------------------------------------ capture

const EMAIL_RE = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
const DEMO_DOMAIN = '@acme-notes.example';

/** Banned words and non-demo e-mail addresses found in a page's visible text. */
export function findBanned(visibleText: string): string[] {
  const lower = visibleText.toLowerCase();
  const hits = BANNED.filter((w) => lower.includes(w));
  for (const addr of visibleText.match(EMAIL_RE) ?? []) {
    if (!addr.toLowerCase().endsWith(DEMO_DOMAIN)) hits.push(`email:${addr}`);
  }
  return hits;
}

interface Region {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface Capture {
  name: string;
  /** Opens the view; returns once it is the state to photograph. */
  open: (page: Page) => Promise<void>;
  /** Crop in document space (full page); defaults to the 1440x900 viewport at the top of the page. */
  region?: (page: Page) => Promise<Region>;
  /** Crop in viewport space, for overlays such as a modal that scrolls inside the viewport. */
  viewportRegion?: (page: Page) => Promise<Region>;
}

async function signedInPage(browser: Browser, state: DemoState): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, colorScheme: 'dark', locale: 'en-US',
  });
  const res = await context.request.post(`${BASE_URL}/api/v1/auth/token`, { data: { email: state.email, password: state.password } });
  if (res.status() !== 200) throw new Error(`browser sign-in failed: ${res.status()}`);
  const page = await context.newPage();
  return { context, page };
}

async function settle(page: Page): Promise<void> {
  await page.addStyleTag({ content: 'nextjs-portal, #__next-build-watcher, [data-nextjs-toast] { display: none !important; }' });
  await page.waitForLoadState('load');
  await page.waitForTimeout(1500);
}

/** Visible text of the text nodes that intersect `region` (document coordinates). */
async function regionText(page: Page, region: Region): Promise<string> {
  return page.evaluate((r) => {
    const out: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node: Node | null;
    while ((node = walker.nextNode())) {
      const text = (node.textContent ?? '').trim();
      const parent = node.parentElement;
      if (!text || !parent || parent.getClientRects().length === 0) continue;
      const range = document.createRange();
      range.selectNodeContents(node);
      const b = range.getBoundingClientRect();
      const top = b.top + window.scrollY;
      const left = b.left + window.scrollX;
      const hit = left < r.x + r.width && left + b.width > r.x && top < r.y + r.height && top + b.height > r.y;
      if (hit) out.push(text);
    }
    return out.join('\n');
  }, region);
}

async function capture(): Promise<void> {
  const state = readJson<DemoState>(path.join(WORK_DIR, 'seed-state.json'));
  const P = `${BASE_URL}/app/projects/${state.projectId}`;
  const screens: Capture[] = [
    { name: 'workflow-builder', open: async (page) => {
      await page.goto(`${P}/workflows/${state.workflowId}`);
      await page.getByRole('button', { name: /^Run$/ }).first().waitFor();
      await page.waitForTimeout(1500);
    },
      // The step list below the canvas names an internal agent; the crop stops above that node.
      viewportRegion: async (page) => {
        // Canvas nodes are not all in the DOM at once; a fixed stop above the fifth node is used.
        const top = 60;
        const bottom = 640;
        return { x: 250, y: top, width: 1190, height: bottom - top };
      } },
    { name: 'timeline-editor', open: async (page) => {
      await page.goto(`${BASE_URL}/app/editor/${state.projectId}/timeline/${state.timelineId}`);
      await page.waitForTimeout(4000);
    },
      // The preview viewer stays black on the isolated stack: no proxies or render exist for this
      // revision and the stack has no transcode worker to make them. The crop therefore takes the
      // timeline toolbar and the V1/A1 tracks only, below the viewer, so the black frame is not shown.
      viewportRegion: async () => ({ x: 180, y: 551, width: 1260, height: 211 }) },
    { name: 'run-estimate', open: async (page) => {
      await page.goto(`${P}/workflows/${state.workflowId}`);
      await page.getByRole('button', { name: /^Run$/ }).first().click();
      await page.getByRole('dialog').first().waitFor();
      await page.getByText('This run should cost', { exact: false }).first().scrollIntoViewIfNeeded();
      await page.waitForTimeout(800);
    },
      // The modal lists the workflow's steps above the estimate, and one step is named for an internal tool.
      // The crop starts at the estimate line, so it shows the estimate, the credits note and the buttons only.
      viewportRegion: async (page) => {
        const dlg = await page.getByRole('dialog').first().boundingBox();
        const est = await page.getByText('This run should cost', { exact: false }).first().boundingBox();
        if (!dlg || !est) throw new Error('run modal estimate not found');
        // "What each step costs" lists every step by name, so the crop ends above it.
        const breakdown = await page.getByText('What each step costs', { exact: false }).first().boundingBox();
        const top = Math.max(est.y - 20, dlg.y);
        const bottom = breakdown ? breakdown.y - 8 : dlg.y + dlg.height;
        return { x: dlg.x, y: top, width: dlg.width, height: bottom - top };
      } },
    { name: 'dashboard', open: async (page) => { await page.goto(`${BASE_URL}/app/dashboard`); await page.getByText('Good morning', { exact: false }).first().waitFor(); } },
    { name: 'project-files', open: async (page) => {
      await page.goto(P); await page.getByRole('link', { name: /^Files \(/ }).or(page.getByRole('tab', { name: /^Files/ })).first().click();
      await page.getByText('acme-product-demo.mp4').first().waitFor();
    } },
    { name: 'picture-editor', open: async (page) => {
      await page.goto(`${BASE_URL}/app/editor/thumbnails/${state.documentId}?project=${state.projectId}`);
      await page.waitForTimeout(1500);
    } },
    { name: 'brand-kit', open: async (page) => {
      await page.goto(P); await page.getByText('Brand & product').first().click();
      await page.getByText('Brand profile', { exact: true }).first().waitFor();
      await page.waitForTimeout(800);
    } },
    { name: 'assistant', open: async (page) => {
      await page.goto(`${BASE_URL}/app/dashboard`);
      await page.getByRole('button', { name: /^ask the assistant$/i }).first().click().catch(async () => {
        await page.locator('header button').filter({ has: page.locator('svg') }).nth(0).click();
      });
      await page.waitForTimeout(1000);
    } },
    { name: 'billing-plans', open: async (page) => {
      await page.goto(`${BASE_URL}/app/settings/billing`); await page.getByText('Choose a plan', { exact: true }).waitFor();
    },
      // The page's header says the plans are billed through the payment provider and carries a "Manage in"
      // link to it; the crop starts at the plan cards so the buyer-facing cards are what is photographed.
      region: async (page) => {
        const heading = await page.getByText('Choose a plan', { exact: true }).boundingBox();
        if (!heading) throw new Error('plan cards heading not found');
        const scrollY = await page.evaluate(() => window.scrollY);
        return { x: 266, y: heading.y + scrollY - 36, width: 1158, height: 440 };
      } },
  ];
  const browser = await chromium.launch({ headless: true });
  const kept: string[] = [];
  const skipped: Array<{ name: string; reason: string }> = [];
  try {
    fs.mkdirSync(RAW_DIR, { recursive: true });
    for (const s of screens) {
      const { context, page } = await signedInPage(browser, state);
      try {
        await s.open(page);
        await settle(page);
        const viewportOnly = Boolean(s.viewportRegion);
        const region: Region = s.viewportRegion
          ? await s.viewportRegion(page)
          : s.region
            ? await s.region(page)
            : { x: 0, y: 0, width: 1440, height: 900 };
        const hits = findBanned(await regionText(page, region));
        const file = path.join(RAW_DIR, `${s.name}.png`);
        if (hits.length) {
          skipped.push({ name: s.name, reason: `visible text contains ${hits.join(', ')}` });
          log(`SKIP ${s.name}: ${hits.join(', ')}`);
        } else {
          await page.screenshot({ path: file, fullPage: !viewportOnly, clip: region });
          kept.push(file);
          log(`captured ${s.name} -> ${file} ${JSON.stringify(region)}`);
        }
      } catch (err) {
        skipped.push({ name: s.name, reason: err instanceof Error ? err.message.split('\n')[0] : String(err) });
        log(`FAILED ${s.name}: ${skipped[skipped.length - 1].reason}`);
      } finally {
        await context.close();
      }
    }
  } finally {
    await browser.close();
  }
  fs.writeFileSync(path.join(WORK_DIR, 'capture-report.json'), JSON.stringify({ kept, skipped }, null, 2));
}

// ------------------------------------------------------------------ main

/** Hosts the script may drive without ALLOW_NON_LOCAL=1: the isolated stack is only ever on loopback. */
const LOCAL_HOSTS = new Set(['127.0.0.1', 'localhost', '::1']);

function isLocalBaseUrl(raw: string): boolean {
  try {
    const host = new URL(raw).hostname.replace(/^\[|\]$/g, '');
    return LOCAL_HOSTS.has(host);
  } catch {
    return false;
  }
}

async function main(): Promise<void> {
  const mode = process.argv[2] || 'seed';
  if (!isLocalBaseUrl(BASE_URL) && process.env.ALLOW_NON_LOCAL !== '1') {
    process.stderr.write(
      `[marketing] refusing to run: BASE_URL (${BASE_URL}) is not a loopback host (127.0.0.1, localhost, ::1).\n` +
        '            Set ALLOW_NON_LOCAL=1 to run against a non-local host on purpose.\n',
    );
    process.exit(2);
  }
  if (!STACK_DIR) throw new Error('E2E_STACK_DIR is not set; source e2e/.stack/<project>/env.sh first');
  fs.mkdirSync(WORK_DIR, { recursive: true });
  if (mode === 'seed' || mode === 'all') await seed();
  if (mode === 'capture' || mode === 'all') await capture();
}

main().catch((err) => {
  process.stderr.write(`${err instanceof Error ? err.stack : String(err)}\n`);
  process.exit(1);
});
