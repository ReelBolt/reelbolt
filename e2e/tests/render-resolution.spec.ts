import { test, expect, request as pwRequest, APIRequestContext, Browser } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { grantCredits, initialAdminPasswordFromLogs, setOrgPlan, PlanKey } from '../scripts/plan-seed';

/**
 * Output resolution as a run choice + plan cap (initiative saas-render-resolution, T-005).
 *
 * Runs ONLY against the isolated stack (BASE_URL=http://localhost:8111, compose project
 * reelbolt-saas-render-resolution): plans and credits are seeded with SQL by ../scripts/plan-seed.ts,
 * which refuses any other postgres. Needs host `ffmpeg`/`ffprobe` and `docker`.
 *
 * Every run here is deterministic (VideoAnalyze with transcription and vision Off → VideoCompile
 * keepWholeSources; TimelineRender), so no LLM or paid provider is reachable.
 */

const BASE_URL = process.env.BASE_URL || 'http://localhost:8111';
const ADMIN_EMAIL = process.env.ADMIN_EMAIL || 'admin@reelforge.local';
/** What the e2e run changes the generated admin password to (forced change on first login). */
const ADMIN_E2E_PASSWORD = process.env.E2E_ADMIN_PASSWORD || 'srr-e2e-admin-password-2026';
const USER_PASSWORD = 'srr-e2e-user-password-2026';
const CAP_HINT_1080 = 'Your plan renders up to 1080p. Upgrade to render in 4K.';
const RUN_TIMEOUT_MS = 6 * 60_000;

interface Account {
  plan: PlanKey;
  email: string;
  orgId: string;
  api: APIRequestContext;
  projectId: string;
  workflowId: string;
  fileId: string;
}

test.describe.configure({ mode: 'serial' });
test.setTimeout(15 * 60_000);

async function login(email: string, password: string): Promise<{ api: APIRequestContext; body: any; status: number }> {
  const api = await pwRequest.newContext({ baseURL: BASE_URL });
  // nginx limits /api/v1/auth/token to 5 r/min per IP (burst 5): wait out a 429 instead of failing.
  let res = await api.post('/api/v1/auth/token', { data: { email, password } });
  for (let i = 0; i < 8 && res.status() === 429; i++) {
    await new Promise((r) => setTimeout(r, 15_000));
    res = await api.post('/api/v1/auth/token', { data: { email, password } });
  }
  const body = res.ok() ? await res.json() : await res.text();
  return { api, body, status: res.status() };
}

async function changePassword(api: APIRequestContext, current: string, next: string): Promise<void> {
  const res = await api.post('/api/v1/auth/change-password', { data: { currentPassword: current, newPassword: next } });
  expect(res.status(), await res.text()).toBe(200);
}

/** Accept-terms with the served versions (a 409 names them). */
async function acceptTerms(api: APIRequestContext): Promise<void> {
  let res = await api.post('/api/v1/auth/accept-terms', { data: { acceptTerms: true, termsVersion: '?', privacyVersion: '?' } });
  if (res.status() === 409) {
    const cur = await res.json();
    res = await api.post('/api/v1/auth/accept-terms', {
      data: { acceptTerms: true, termsVersion: cur.currentTermsVersion, privacyVersion: cur.currentPrivacyVersion },
    });
  }
  expect(res.ok(), `accept-terms ${res.status()} ${await res.text()}`).toBeTruthy();
}

async function adminSession(): Promise<APIRequestContext> {
  let s = await login(ADMIN_EMAIL, ADMIN_E2E_PASSWORD);
  if (s.status === 200 && !s.body.mustChangePassword) return s.api;
  await s.api.dispose();
  const initial = process.env.ADMIN_PASSWORD || initialAdminPasswordFromLogs();
  expect(initial, 'initial admin password (ADMIN_PASSWORD or go-api logs)').toBeTruthy();
  s = await login(ADMIN_EMAIL, initial!);
  expect(s.status, JSON.stringify(s.body)).toBe(200);
  // change-password re-issues the session cookie, so no second login is needed.
  await changePassword(s.api, initial!, ADMIN_E2E_PASSWORD);
  return s.api;
}

/** A fresh user with its own Personal org on `plan`, credits granted, terms accepted. */
async function provisionAccount(admin: APIRequestContext, plan: PlanKey, tag: string): Promise<Pick<Account, 'plan' | 'email' | 'orgId' | 'api'>> {
  const email = `srr-${plan}-${tag}@example.com`;
  const created = await admin.post('/api/v1/admin/users', { data: { email, displayName: `SRR ${plan}` } });
  expect(created.status(), await created.text()).toBe(201);
  const temp = (await created.json()).temporaryPassword as string;

  let s = await login(email, temp);
  expect(s.status).toBe(200);
  const orgId = s.body.org as string;
  // Seed BEFORE the org's first gated request, so no 30 s entitlement cache holds the trial plan.
  setOrgPlan(orgId, plan);
  grantCredits(orgId, 5000);
  // Both calls re-issue the session cookie on s.api (one login per user: /auth/token is rate limited).
  await changePassword(s.api, temp, USER_PASSWORD);
  await acceptTerms(s.api);
  const me = await s.api.get('/api/v1/projects');
  expect(me.status(), 'session usable after password change + terms').toBe(200);
  return { plan, email, orgId, api: s.api };
}

function makeClip(dir: string): string {
  const out = path.join(dir, 'srr-testsrc-1280x720.mp4');
  execFileSync('ffmpeg', [
    '-y', '-v', 'error',
    '-f', 'lavfi', '-i', 'testsrc=size=1280x720:rate=30:duration=3',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=3',
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', out,
  ]);
  return out;
}

function probe(file: string): { width: number; height: number } {
  const out = execFileSync('ffprobe', [
    '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0', file,
  ], { encoding: 'utf-8' }).trim();
  const [w, h] = out.split(',').map(Number);
  return { width: w, height: h };
}

async function setupProject(api: APIRequestContext, clip: string): Promise<{ projectId: string; workflowId: string; fileId: string }> {
  const proj = await api.post('/api/v1/projects', { data: { name: `SRR ${Date.now()}`, description: 'render-resolution e2e' } });
  expect(proj.ok(), await proj.text()).toBeTruthy();
  const projectId = (await proj.json()).id as string;

  const up = await api.post(`/api/v1/projects/${projectId}/files`, {
    multipart: { file: { name: path.basename(clip), mimeType: 'video/mp4', buffer: fs.readFileSync(clip) } },
  });
  expect(up.ok(), `upload ${up.status()} ${await up.text()}`).toBeTruthy();
  const fileId = (await up.json()).id as string;

  const agents = (await (await api.get('/api/v1/agents')).json()) as Array<{ id: string; agentType: string }>;
  const vt = agents.find((a) => a.agentType === 'VideoTransform');
  expect(vt, 'VideoTransform placeholder agent').toBeTruthy();

  const wf = await api.post(`/api/v1/projects/${projectId}/workflows`, {
    data: {
      name: 'SRR analyze → compile',
      requiresUserInput: false,
      steps: [
        {
          agentDefinitionId: vt!.id, stepOrder: 1, edgeConditionJson: null, label: 'Analyze', stepType: 'VideoAnalyze',
          videoAnalyzeConfigJson: JSON.stringify({
            version: 1,
            source: { kind: 'ProjectFile', projectFileId: fileId },
            transcription: 'Off',
            vision: 'Off',
            derushPrePass: 'Off',
          }),
        },
        {
          agentDefinitionId: vt!.id, stepOrder: 2, edgeConditionJson: null, label: 'Compile', stepType: 'VideoCompile',
          videoCompileConfigJson: JSON.stringify({
            version: 1,
            decision: { from: 'Previous' },
            analysisStepOrder: 1,
            keepWholeSources: true,
            mode: 'Reencode',
            outputFormat: 'Landscape',
          }),
        },
      ],
    },
  });
  expect(wf.ok(), `create workflow ${wf.status()} ${await wf.text()}`).toBeTruthy();
  return { projectId, workflowId: (await wf.json()).id as string, fileId };
}

async function waitForExecution(api: APIRequestContext, projectId: string, executionId: string): Promise<any> {
  const deadline = Date.now() + RUN_TIMEOUT_MS;
  let last: any;
  while (Date.now() < deadline) {
    const res = await api.get(`/api/v1/projects/${projectId}/executions/${executionId}`);
    expect(res.ok()).toBeTruthy();
    last = await res.json();
    if (['Passed', 'Failed', 'Cancelled'].includes(last.status)) return last;
    await new Promise((r) => setTimeout(r, 3000));
  }
  throw new Error(`execution ${executionId} did not finish: ${JSON.stringify(last)?.slice(0, 2000)}`);
}

/** Downloads the (last) media output of a completed run and probes its video stream. */
async function probeRunOutput(api: APIRequestContext, projectId: string, execution: any, dir: string) {
  const withMedia = (execution.stepResults as any[]).filter((r) => r.outputStorageKey && /\.mp4$/i.test(r.outputStorageKey));
  expect(withMedia.length, `media output in ${JSON.stringify(execution.stepResults.map((r: any) => [r.status, r.outputStorageKey]))}`).toBeGreaterThan(0);
  const result = withMedia.sort((a, b) => String(a.completedAt).localeCompare(String(b.completedAt))).at(-1);
  const dl = await api.get(`/api/v1/projects/${projectId}/outputs/${result.id}/download`);
  expect(dl.ok(), `download ${dl.status()}`).toBeTruthy();
  const file = path.join(dir, `${execution.id}.mp4`);
  fs.writeFileSync(file, await dl.body());
  return { ...probe(file), stepResult: result };
}

async function execute(acc: Account, body: Record<string, unknown>) {
  return acc.api.post(`/api/v1/projects/${acc.projectId}/workflows/${acc.workflowId}/execute`, { data: body });
}

async function openRunModal(browser: Browser, acc: Account) {
  const context = await browser.newContext({ storageState: await acc.api.storageState(), baseURL: BASE_URL, viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  page.setDefaultTimeout(30_000);
  page.on('console', (m) => { if (m.type() === 'error') console.log(`[srr console ${acc.plan}] ${m.text()}`); });
  // The dashboard is served under the /app basePath.
  // The workflow page opens on its App view; the run dialog with the resolution choice is the builder's.
  await page.goto(`/app/projects/${acc.projectId}/workflows/${acc.workflowId}?view=advanced`);
  await expect(page).toHaveURL(new RegExp(`/app/projects/${acc.projectId}/workflows/${acc.workflowId}`));
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText('Resolution', { exact: true })).toBeVisible();
  return { context, page, dialog };
}

test.describe('output resolution: plan cap and run choice', () => {
  const accounts = {} as Record<'pro' | 'creator' | 'trial', Account>;
  let workDir = '';

  test.beforeAll(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'API + desktop modal coverage; runs once');
    workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'srr-e2e-'));
    const clip = makeClip(workDir);
    expect(probe(clip)).toEqual({ width: 1280, height: 720 });
    const admin = await adminSession();
    const tag = `${Date.now()}`;
    for (const plan of ['pro', 'creator', 'trial'] as const) {
      const base = await provisionAccount(admin, plan, tag);
      const proj = await setupProject(base.api, clip);
      accounts[plan] = { ...base, ...proj };
    }
    await admin.dispose();
  });

  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'API + desktop modal coverage; runs once');
  });

  test('billing summary exposes each plan cap', async () => {
    for (const [plan, cap] of [['pro', 2160], ['creator', 1080], ['trial', 720]] as const) {
      const res = await accounts[plan].api.get('/api/v1/billing/summary');
      expect(res.ok(), `${plan} summary ${res.status()}`).toBeTruthy();
      const body = await res.json();
      expect(body.entitlements?.maxOutputHeight, `${plan} ${JSON.stringify(body.entitlements)}`).toBe(cap);
    }
  });

  test('run dialog: Pro sees 4K enabled', async ({ browser }) => {
    const { context, dialog } = await openRunModal(browser, accounts.pro);
    const fourK = dialog.getByRole('radio', { name: '4K' });
    await expect(fourK).toBeEnabled();
    await expect(dialog.getByTestId('run-resolution-cap')).toHaveCount(0);
    await context.close();
  });

  test('run dialog: Creator sees 4K disabled with the upgrade hint', async ({ browser }) => {
    const { context, dialog } = await openRunModal(browser, accounts.creator);
    await expect(dialog.getByRole('radio', { name: '4K' })).toBeDisabled();
    await expect(dialog.getByRole('radio', { name: '1080p' })).toBeEnabled();
    await expect(dialog.getByTestId('run-resolution-cap')).toContainText(CAP_HINT_1080);
    await context.close();
  });

  test('API: Creator asking 2160p gets 402 PLAN_NOT_ENTITLED; 1440p is a 400', async () => {
    const creator = accounts.creator;
    const refused = await execute(creator, { outputResolution: '2160p' });
    const body = await refused.json();
    expect(refused.status(), JSON.stringify(body)).toBe(402);
    expect(body).toMatchObject({ code: 'PLAN_NOT_ENTITLED', feature: 'resolution', limit: 1080, current: 2160 });

    const bad = await execute(creator, { outputResolution: '1440p' });
    const badBody = await bad.text();
    expect(bad.status(), badBody).toBe(400);
    expect(badBody).toContain("Unknown output resolution '1440p'");

    // Nothing was queued by either refusal.
    const runs = await creator.api.get(`/api/v1/projects/${creator.projectId}/workflows/${creator.workflowId}/executions`);
    const list = await runs.json();
    const items = Array.isArray(list) ? list : list.items ?? list.executions ?? [];
    expect(items.length, JSON.stringify(list).slice(0, 500)).toBe(0);
  });

  test('Pro run at 2160p renders 3840x2160', async () => {
    const pro = accounts.pro;
    const res = await execute(pro, { outputResolution: '2160p' });
    const queued = await res.json();
    expect(res.status(), JSON.stringify(queued)).toBe(201);
    expect(queued.outputResolution).toBe('2160p');
    const done = await waitForExecution(pro.api, pro.projectId, queued.id);
    expect(done.status, `${done.errorMessage} ${JSON.stringify(done.stepResults?.map((r: any) => r.errorDetails))}`).toBe('Passed');
    const out = await probeRunOutput(pro.api, pro.projectId, done, workDir);
    console.log(`[srr] pro 2160p execution ${done.id} -> ${out.width}x${out.height}`);
    expect({ width: out.width, height: out.height }).toEqual({ width: 3840, height: 2160 });
  });

  test('Pro run with Auto stays at or below 1080 short side', async () => {
    const pro = accounts.pro;
    const res = await execute(pro, {});
    const queued = await res.json();
    expect(res.status(), JSON.stringify(queued)).toBe(201);
    expect(queued.outputResolution ?? null).toBeNull();
    const done = await waitForExecution(pro.api, pro.projectId, queued.id);
    expect(done.status, `${done.errorMessage}`).toBe('Passed');
    const out = await probeRunOutput(pro.api, pro.projectId, done, workDir);
    console.log(`[srr] pro Auto execution ${done.id} -> ${out.width}x${out.height}`);
    expect(Math.min(out.width, out.height)).toBeLessThanOrEqual(1080);
  });

  /**
   * The render endpoint binds `preset` by NAME only (an ordinal is a 400): the web's Deliver workspace
   * sends the name, and a number would silently change meaning if the enum were ever reordered.
   *
   * Order matters (serial): the timeline is created and the gate checked first, then the
   * ordinal-refusal contract, and the accepted render (which actually queues an execution) runs last,
   * so a failure to queue cannot hide the two refusal checks.
   */
  const PRESET = { Hd1080p16x9: 'Hd1080p16x9', Uhd4k16x9: 'Uhd4k16x9' } as const;
  let trialRenderUrl = '';

  test('editor export as Trial: Uhd4k16x9 is 402 PLAN_NOT_ENTITLED', async () => {
    const trial = accounts.trial;
    // A timeline seeded from the Trial org's own compile run (Auto, deterministic).
    const res = await execute(trial, {});
    const queued = await res.json();
    expect(res.status(), JSON.stringify(queued)).toBe(201);
    const run = await waitForExecution(trial.api, trial.projectId, queued.id);
    expect(run.status, `${run.errorMessage}`).toBe('Passed');
    const seed = (run.stepResults as any[]).find((r) => r.outputStorageKey && /\.mp4$/i.test(r.outputStorageKey));
    expect(seed).toBeTruthy();

    const tl = await trial.api.post(`/api/v1/projects/${trial.projectId}/edit-timelines`, {
      data: { name: 'SRR trial export', seedStepResultId: seed.id },
    });
    expect(tl.status(), await tl.text()).toBe(201);
    const timelineId = (await tl.json()).id as string;
    trialRenderUrl = `/api/v1/projects/${trial.projectId}/edit-timelines/${timelineId}/render`;

    const refused = await trial.api.post(trialRenderUrl, { data: { preset: PRESET.Uhd4k16x9 } });
    const refusedText = await refused.text();
    expect(refused.status(), refusedText.slice(0, 500)).toBe(402);
    expect(JSON.parse(refusedText)).toMatchObject({ code: 'PLAN_NOT_ENTITLED', feature: 'resolution', limit: 720, current: 2160 });
  });

  // The editor's Deliver workspace (web/lib/editing/sync/api.ts renderEditTimeline) posts the preset
  // by NAME. This pins the other half of that contract: a numeric preset is refused as a 400 (bad
  // request, not the plan gate), independently of the plan cap above.
  test('editor export contract: the render endpoint refuses a numeric preset', async () => {
    expect(trialRenderUrl, 'previous test created the trial timeline').toBeTruthy();
    const trial = accounts.trial;
    const refused = await trial.api.post(trialRenderUrl, { data: { preset: 1 } });
    const body = await refused.text();
    expect(refused.status(), body.slice(0, 500)).toBe(400);
    expect(body).toContain('preset');
  });

  test('editor export as Trial: Hd1080p16x9 is accepted and clamped to 1280x720', async () => {
    expect(trialRenderUrl, 'first trial test created the timeline').toBeTruthy();
    const trial = accounts.trial;
    const accepted = await trial.api.post(trialRenderUrl, { data: { preset: PRESET.Hd1080p16x9 } });
    const acceptedText = await accepted.text();
    // Status first: a 500 carries the developer exception page as text, not JSON.
    expect(accepted.status(), acceptedText.slice(0, 500)).toBe(202);
    const acceptedBody = JSON.parse(acceptedText);
    const render = await waitForExecution(trial.api, trial.projectId, acceptedBody.executionId);
    expect(render.status, `${render.errorMessage} ${JSON.stringify(render.stepResults?.map((r: any) => r.errorDetails))}`).toBe('Passed');
    const out = await probeRunOutput(trial.api, trial.projectId, render, workDir);
    console.log(`[srr] trial Hd1080p16x9 render ${render.id} -> ${out.width}x${out.height}`);
    expect({ width: out.width, height: out.height }).toEqual({ width: 1280, height: 720 });
  });
});
