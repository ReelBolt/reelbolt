import { createHmac } from 'node:crypto';
import type { Page } from '@playwright/test';

/** The signing key the config hands the built server (`JWT_SIGNING_KEY`); the proxy verifies the session token with it. */
export const SMOKE_JWT_KEY = 'error-states-smoke-signing-key-0123456789';

function sessionToken(): string {
  const b64 = (o: unknown) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const head = b64({ alg: 'HS256', typ: 'JWT' });
  const body = b64({ sub: '00000000-0000-4000-8000-000000000001', email: 'smoke@example.com', iss: 'reelbolt-api', aud: 'reelbolt-inference', exp: Math.floor(Date.now() / 1000) + 3600 });
  return `${head}.${body}.${createHmac('sha256', SMOKE_JWT_KEY).update(`${head}.${body}`).digest('base64url')}`;
}

export const PROJECT_ID = 'p1';
export const WID = 'wf-run';
export const EXEC_ID = 'ex1';

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

// ---------------------------------------------------------------------------------------------
// Failure-state helpers (error-and-loading-states).
// ---------------------------------------------------------------------------------------------

const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

/** Longer than the 10 s notice by the page's own hydration time, so the notice is asserted before the answer lands. */
export const SLOW_MS = 14_000;

export type Failure = 'json500' | 'html502' | 'network' | 'slow' | 'e401' | 'e403' | 'e404' | 'e402';

/** The raw server wording that must never reach the screen. */
export const RAW_TEXT = [
  'Bad Gateway', 'Internal Server Error', 'NullReferenceException', 'Object reference not set', 'nginx', 'Service Unavailable',
  'Not Found', 'Forbidden', 'Unauthorized', 'Payment Required', 'ProblemDetails', 'System.', 'Stack trace',
];

const problem = (status: number, title: string, extra: Record<string, unknown> = {}) => ({
  status, contentType: 'application/problem+json',
  body: JSON.stringify({ type: `https://httpstatuses.io/${status}`, title, status, ...extra }),
});

/** Holds the failure state of one endpoint: `mode` is read on every request, so a test can unblock it. */
export interface Blocker { mode: Failure | 'ok'; hits: number }

export function fulfillFailure(route: import('@playwright/test').Route, mode: Failure) {
  switch (mode) {
    case 'json500': return route.fulfill(problem(500, 'Internal Server Error'));
    case 'html502':
      return route.fulfill({ status: 502, contentType: 'text/html', body: '<html><head><title>502 Bad Gateway</title></head><body><center><h1>502 Bad Gateway</h1></center><hr><center>nginx</center></body></html>' });
    case 'network': return route.abort('failed');
    case 'e401': return route.fulfill(problem(401, 'Unauthorized'));
    case 'e403': return route.fulfill(problem(403, 'Forbidden'));
    case 'e404': return route.fulfill(problem(404, 'Not Found'));
    case 'e402': return route.fulfill(problem(402, 'Payment Required', { code: 'INSUFFICIENT_CREDITS', required: 14, balance: 2 }));
    case 'slow': return route.fallback();
  }
}

/** Fails every request matching `target` while `blocker.mode` is not 'ok' (registered last, so it wins). */
export async function block(page: Page, target: RegExp, mode: Failure | 'ok'): Promise<Blocker> {
  const blocker: Blocker = { mode, hits: 0 };
  await page.route(target, async (route) => {
    blocker.hits += 1;
    if (blocker.mode === 'ok') return route.fallback();
    if (blocker.mode === 'slow') {
      await new Promise((r) => setTimeout(r, SLOW_MS));
      return route.fallback().catch(() => undefined);
    }
    return fulfillFailure(route, blocker.mode);
  });
  return blocker;
}

const api = `/api/v1/projects/${PROJECT_ID}`;

/** One failure target per journey page: the request whose failure the page must word itself. */
export interface JourneyPage {
  name: string;
  path: string;
  target: RegExp;
  /** Text that is on screen once the page has loaded for real. */
  ready: RegExp;
}

export const JOURNEY: JourneyPage[] = [
  { name: 'dashboard', path: '/app/dashboard', target: /\/api\/v1\/dashboard\/overview/, ready: /You have a story\. Make the video\./ },
  { name: 'project', path: `/app/projects/${PROJECT_ID}`, target: new RegExp(`${api}$`), ready: /Smoke project/ },
  { name: 'files', path: `/app/projects/${PROJECT_ID}?tab=files`, target: new RegExp(`${api}/files(\\?.*)?$`), ready: /beach\.mp4/ },
  { name: 'gallery', path: `/app/projects/${PROJECT_ID}?tab=workflows`, target: new RegExp(`${api}/workflow-apps(\\?.*)?$`), ready: /Launch teaser/ },
  { name: 'run', path: `/app/projects/${PROJECT_ID}/workflows/${WID}`, target: new RegExp(`${api}/workflows/${WID}/app(\\?.*)?$`), ready: /Launch teaser/ },
  { name: 'detail', path: `/app/projects/${PROJECT_ID}/workflows/${WID}/executions/${EXEC_ID}`, target: new RegExp(`${api}/executions/${EXEC_ID}(\\?.*)?$`), ready: /Launch teaser/ },
  { name: 'workflow', path: `/app/projects/${PROJECT_ID}/workflows/${WID}?view=advanced`, target: new RegExp(`${api}/workflows/${WID}(\\?.*)?$`), ready: /Launch teaser/ },
];

const now2 = new Date().toISOString();
const workflowDef = { id: WID, projectId: PROJECT_ID, name: 'Launch teaser', steps: [], requiresUserInput: false, createdAt: now2, updatedAt: now2 };
const appResponse = {
  workflowId: WID, name: 'Launch teaser', hasExplicitManifest: true, costClass: 'Uses credits', source: 'promo-teaser',
  manifest: {
    version: 1, title: 'Launch teaser', tagline: 'A short promo', category: 'promo',
    inputs: [{ kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 3 }, { kind: 'brief', field: 'language', label: 'Language' }],
    result: { kind: 'video' },
  },
};
const clip = {
  id: 'f1', projectId: PROJECT_ID, originalFileName: 'beach.mp4', category: 'userFiles', mimeType: 'video/mp4', sizeBytes: 1000,
  storageKey: 'k1', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'Indexed',
};
const execution = {
  id: EXEC_ID, workflowDefinitionId: WID, status: 'Passed', startedAt: now2, completedAt: now2,
  iterationCount: 1, resultJson: null, stepResults: [], reviewScores: [],
};

/** Signs in and mocks every endpoint the journey pages read. Call before `block()`. */
export async function setupJourney(page: Page, baseURL: string, opts: { runStatus?: 'ok' | '402' } = {}) {
  await signIn(page, baseURL);
  await mockApi(page);
  const re = (tail: string) => new RegExp(`${api}/${tail}(\\?.*)?$`);
  await page.route(/\/api\/v1\/dashboard\/overview/, (r) => r.fulfill(json({
    projects: [{
      projectId: PROJECT_ID, sourceFileCount: 1, videoCount: 1, audioCount: 0, imageCount: 0, totalBytes: 1000, workflowCount: 2,
      renderCount: 0, activeExecutionCount: 0, failedExecutionCount: 0, lastActivityAt: now2, latestOutput: null,
    }],
    activeExecutions: [], recentExecutions: [], needsAttention: [], recentOutputs: [],
    totals: { projects: 1, sourceFiles: 1, renders: 0, passedLast7Days: 0, failedLast7Days: 0 },
  })));
  await page.route(/\/api\/v1\/onboarding(\?.*)?$/, (r) => r.fulfill(json({
    tour: { status: 'skipped', version: 1, updatedAt: now2 }, checklist: { dismissed: true, items: [] }, profile: null,
  })));
  await page.route(/\/api\/v1\/billing\/usage/, (r) => r.fulfill(json({ totals: { credits: 0, costUsd: 0, calls: 0 }, rows: [], items: [] })));
  await page.route(/\/api\/v1\/projects(\?.*)?$/, (r) => r.fulfill(json([project])));
  await page.route(re(`workflows/${WID}`), (r) => r.fulfill(json(workflowDef)));
  await page.route(re(`workflows/${WID}/app`), (r) => r.fulfill(json(appResponse)));
  await page.route(re(`workflows/${WID}/estimate`), (r) => r.fulfill(json({
    billingEnabled: true, hasHistory: true, lowCredits: 8, highCredits: 14, typicalCredits: 10, fixedMaxCredits: 0,
    requiredCredits: 14, balanceCredits: 500, affordable: true, steps: [],
  })));
  await page.route(re(`workflows/${WID}/lint`), (r) => r.fulfill(json({ findings: [], errors: 0, warnings: 0, infos: 0 })));
  await page.route(re(`workflows/${WID}/executions`), (r) => r.fulfill(json([])));
  await page.route(re(`workflows/${WID}/execute`), (r) => (opts.runStatus === '402' ? fulfillFailure(r, 'e402') : r.fulfill(json(execution, 201))));
  await page.route(re('files'), (r) => r.fulfill(json([clip])));
  await page.route(re('outputs'), (r) => r.fulfill(json([])));
  await page.route(re(`executions/${EXEC_ID}/change-capabilities`), (r) => r.fulfill(json({ maxTargets: 3, steps: [] })));
  await page.route(re(`executions/${EXEC_ID}/change-requests`), (r) => r.fulfill(json({ requests: [] })));
  await page.route(re(`executions/${EXEC_ID}/revisions`), (r) => r.fulfill(json({ revisions: [], versions: [], availability: 'Unavailable' })));
  await page.route(re(`executions/${EXEC_ID}`), (r) => r.fulfill(json(execution)));
}
