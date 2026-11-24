import { createHmac } from 'node:crypto';
import type { Page } from '@playwright/test';

/** The signing key the config hands the built server (`JWT_SIGNING_KEY`); the proxy verifies the session token with it. */
export const SMOKE_JWT_KEY = 'workflow-apps-smoke-signing-key-0123456789';

function sessionToken(): string {
  const b64 = (o: unknown) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const head = b64({ alg: 'HS256', typ: 'JWT' });
  const body = b64({ sub: '00000000-0000-4000-8000-000000000001', email: 'smoke@example.com', iss: 'reelbolt-api', aud: 'reelbolt-inference', exp: Math.floor(Date.now() / 1000) + 3600 });
  return `${head}.${body}.${createHmac('sha256', SMOKE_JWT_KEY).update(`${head}.${body}`).digest('base64url')}`;
}

export const PROJECT_ID = 'p1';

const now = new Date().toISOString();

export const project = {
  id: PROJECT_ID, name: 'Smoke project', description: '', status: 'Active', userId: 'u1', createdAt: now, updatedAt: now,
};

export const workflows = [
  { id: 'wf-a', projectId: PROJECT_ID, name: 'Launch teaser', steps: [], createdAt: now, updatedAt: now },
  { id: 'wf-b', projectId: PROJECT_ID, name: 'Footage recap', steps: [], createdAt: now, updatedAt: now },
];

export const gallery = {
  apps: [
    { workflowId: 'wf-a', title: 'Launch teaser', tagline: 'A short promo', category: 'promo', costClass: 'Uses credits and a voice', inputsCount: 2, lastRun: { status: 'Passed', finishedAt: now }, source: 'promo-teaser' },
    { workflowId: 'wf-b', title: 'Footage recap', tagline: 'Cut your clips', category: 'footage', costClass: 'No credits', inputsCount: 1, lastRun: null, source: 'footage-recap' },
  ],
  recipes: [
    { key: 'r-ready', title: 'Product promo', tagline: 'Promo from your files', category: 'promo', costClass: 'Uses credits and AI shots', inputsCount: 2, fit: 'ready', missing: [] },
    { key: 'r-brand', title: 'Brand intro', tagline: 'Intro in your brand', category: 'brand', costClass: 'No credits', inputsCount: 1, fit: 'ready', missing: [] },
    { key: 'r-clips', title: 'Highlight reel', tagline: 'Best moments', category: 'footage', costClass: 'Uses credits', inputsCount: 1, fit: 'needs', missing: [{ need: 'clips', message: 'Add video clips', action: 'add-files' }] },
  ],
};

/** The session the proxy accepts: both cookies present, no legal version (so the re-acceptance gate is skipped). */
export async function signIn(page: Page, baseURL: string) {
  const user = encodeURIComponent(JSON.stringify({ email: 'smoke@example.com', mustChangePassword: false }));
  await page.context().addCookies([
    { name: 'reelbolt_user', value: user, url: baseURL },
    { name: 'reelbolt_token', value: sessionToken(), url: baseURL },
  ]);
}

export interface MockOptions {
  /** Resolves when the recipe POST may answer (lets a test hold one recipe in "provisioning"). */
  holdApply?: Promise<void>;
  onApply?: (key: string) => void;
}

/** Mocks every /api/v1/* call. Later routes win, so the catch-all goes first. */
export async function mockApi(page: Page, opts: MockOptions = {}) {
  const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/v1/**', (route) => {
    const req = route.request();
    if (req.method() !== 'GET') return route.fulfill(json({}));
    // Object-shaped shell endpoints; every other GET is a list.
    const objectShaped = /\/api\/v1\/(billing|storage|dashboard)\//.test(req.url());
    return route.fulfill(json(/dashboard\/overview/.test(req.url()) ? { projects: [], activeExecutions: [] } : /billing\/summary/.test(req.url()) ? { plans: [] } : objectShaped ? {} : []));
  });
  await page.route(new RegExp(`/api/v1/projects/${PROJECT_ID}$`), (r) => r.fulfill(json(project)));
  await page.route(new RegExp(`/api/v1/projects/${PROJECT_ID}/workflows$`), (r) =>
    r.request().method() === 'GET' ? r.fulfill(json(workflows)) : r.fulfill(json({})),
  );
  await page.route(new RegExp(`/api/v1/projects/${PROJECT_ID}/workflow-apps$`), (r) => r.fulfill(json(gallery)));
  await page.route(new RegExp(`/api/v1/projects/${PROJECT_ID}/workflows/templates/[^/]+/apply$`), async (r) => {
    const key = r.request().url().split('/templates/')[1].split('/')[0];
    opts.onApply?.(key);
    if (opts.holdApply) await opts.holdApply;
    await r.fulfill(json({ id: 'wf-new', projectId: PROJECT_ID, name: key, steps: [], createdAt: now, updatedAt: now }));
  });
  await page.route(new RegExp(`/api/v1/projects/${PROJECT_ID}/workflows/(wf-[a-z]+)$`), (r) => {
    const id = r.request().url().split('/workflows/')[1];
    return r.fulfill(json({ id, projectId: PROJECT_ID, name: id, steps: [], createdAt: now, updatedAt: now }));
  });
}
