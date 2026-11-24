import type { Page, Route } from '@playwright/test';
import { PROJECT_ID, mockApi, signIn } from '../workflow-apps/fixtures';

// Mocked-network fixtures for the run page "Request changes" flow (docs/change-requests.md).
// Shapes mirror web/lib/types/change-requests.ts; no stack is needed.
export { PROJECT_ID };
export const WID = 'wf1';
export const SOURCE_EXECUTION = 'ex1';
export const NEW_EXECUTION = 'ex2';
const now = new Date().toISOString();
export const base = `/api/v1/projects/${PROJECT_ID}`;
export const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

/** Step ids are `st<n>`, step-result ids `sr<n>`. */
export const STEPS = [
  { n: 1, label: 'Research', stepType: 'Agent' },
  { n: 2, label: 'Narration', stepType: 'Voiceover' },
  { n: 3, label: 'Clips', stepType: 'VideoGenerate' },
  { n: 4, label: 'Music', stepType: 'AudioGenerate' },
  { n: 5, label: 'Thumbnail', stepType: 'ThumbnailGenerate' },
  { n: 6, label: 'Film', stepType: 'VideoCompile' },
] as const;

export const workflow = {
  id: WID, projectId: PROJECT_ID, name: 'Launch teaser', requiresUserInput: false, createdAt: now, updatedAt: now,
  steps: STEPS.map((s) => ({ id: `st${s.n}`, agentDefinitionId: `ag${s.n}`, stepOrder: s.n, label: s.label, stepType: s.stepType })),
};

export const execution = (id: string) => ({
  id, workflowDefinitionId: WID, status: 'Passed', startedAt: now, completedAt: now, iterationCount: 1, resultJson: null,
  reviewScores: [],
  stepResults: STEPS.map((s) => ({
    id: `sr${s.n}`, workflowStepId: `st${s.n}`, output: '', tokensUsed: 0, durationMs: 1000, executedAt: now, status: 'Completed',
    outputJson: null, iterationNumber: 1, completedAt: now,
  })),
});

const spec = (key: string, type: string, over: Record<string, unknown> = {}) => ({
  key, type, min: null, max: null, maxLength: null, maxEntries: null, values: null, ...over,
});
const plain = { notSupportedReason: null, route: null } as const;

export const capabilities = {
  executionId: SOURCE_EXECUTION,
  runFinished: true,
  maxTargets: 6,
  steps: [
    { stepOrder: 1, stepResultId: 'sr1', stepType: 'Agent', label: 'Research', supported: false, route: null, itemKind: 'None', items: [], overrides: [],
      notSupportedReason: 'Research is not changed one step at a time.' },
    { stepOrder: 2, stepResultId: 'sr2', stepType: 'Voiceover', label: 'Narration', supported: true, ...plain, itemKind: 'Line',
      items: [
        { id: 'vo1', status: 'Completed', durationSec: 3, text: 'Hello there.' },
        { id: 'vo2', status: 'Completed', durationSec: 4, text: 'Welcome to ReelBolt.' },
      ],
      overrides: [
        spec('text', 'Text', { maxLength: 500 }), spec('voiceId', 'Text', { maxLength: 100 }),
        spec('speed', 'Number', { min: 0.5, max: 2 }), spec('pronunciation', 'PronunciationList', { maxEntries: 8 }),
      ] },
    { stepOrder: 3, stepResultId: 'sr3', stepType: 'VideoGenerate', label: 'Clips', supported: true, ...plain, itemKind: 'Shot',
      items: [
        { id: 'v1', status: 'succeeded', durationSec: 5, text: 'A drone shot over the harbour' },
        { id: 'v1t1', status: 'succeeded', durationSec: 5, text: 'A drone shot over the harbour' },
        { id: 'v1t2', status: 'failed', durationSec: null, text: 'A drone shot over the harbour' },
        { id: 'v2', status: 'succeeded', durationSec: 6, text: 'Waves at sunset' },
      ],
      overrides: [spec('prompt', 'Text', { maxLength: 2000 }), spec('retake', 'Flag')] },
    { stepOrder: 4, stepResultId: 'sr4', stepType: 'AudioGenerate', label: 'Music', supported: true, ...plain, itemKind: 'Take',
      items: [{ id: 'a1', status: 'succeeded', durationSec: 30, text: 'Calm piano' }],
      overrides: [spec('prompt', 'Text', { maxLength: 4000 }), spec('durationSec', 'Number', { min: 1, max: 120 }), spec('retake', 'Flag')] },
    { stepOrder: 5, stepResultId: 'sr5', stepType: 'ThumbnailGenerate', label: 'Thumbnail', supported: true, ...plain, itemKind: 'None',
      items: [{ id: 'thumbnail', status: 'Completed', durationSec: null, text: 'thumb.png' }],
      overrides: [spec('overlayText', 'Text', { maxLength: 400 }), spec('atTimeSec', 'Number', { min: 0, max: 3600 })] },
    { stepOrder: 6, stepResultId: 'sr6', stepType: 'VideoCompile', label: 'Film', supported: true, notSupportedReason: null,
      route: 'composition-revision', combinable: true, itemKind: 'None', items: [], overrides: [] },
  ],
};

const replay = {
  steps: [
    { stepOrder: 2, label: 'Narration', disposition: 'Run', reason: 'Changed' },
    { stepOrder: 6, label: 'Film', disposition: 'Run', reason: 'Depends on narration' },
    { stepOrder: 3, label: 'Clips', disposition: 'Reuse', reason: 'Unchanged' },
  ],
};
export const estimateOk = {
  estimate: { billingEnabled: true, lowCredits: 1, highCredits: 2, requiredCredits: 2, balanceCredits: 500, affordable: true, targetExactCredits: 1.2 },
  replay,
};
export const estimateShortfall = {
  estimate: { billingEnabled: true, lowCredits: 40, highCredits: 60, requiredCredits: 60, balanceCredits: 5, affordable: false, targetExactCredits: 45 },
  replay,
};

export const historyRow = {
  id: 'cr1', executionId: NEW_EXECUTION, workflowDefinitionId: WID, sourceExecutionId: SOURCE_EXECUTION, targetStepOrder: 2,
  targetStepResultId: 'sr2', stepType: 'Voiceover', instruction: 'Slower first line', itemIds: ['vo1'], status: 'Completed',
  costCredits: 1.5, createdAt: '2026-10-25T10:00:00Z', plan: { redone: 2, reused: 5, skipped: 0 },
};

export interface Scenario {
  estimate?: unknown;
  /** Answer for `POST change-requests`; default is a 201 with `historyRow`. */
  create?: { status: number; body: unknown };
  history?: unknown[];
}

/** Signs in, mocks every call the run page makes and returns the recorded POST bodies. */
export async function setup(page: Page, baseURL: string, sc: Scenario = {}) {
  const posts = { estimate: [] as unknown[], create: [] as unknown[] };
  await signIn(page, baseURL);
  await mockApi(page);
  // The run page also reads the plan summary and the run's usage; the generic catch-all answers [] and the page needs objects.
  await page.route(/\/api\/v1\/billing\/summary(\?.*)?$/, (r: Route) => r.fulfill(json({
    billingProvider: null, paddle: null, canManageBilling: true, subscription: null,
    credits: { balance: 1000, reserved: 0, available: 1000, grants: [] },
    topUp: { available: true, packs: [500, 1000] }, plans: [],
  })));
  await page.route(/\/api\/v1\/billing\/usage(\?.*)?$/, (r: Route) => r.fulfill(json({
    from: now, to: now, groupBy: 'none', totals: { credits: 0, events: 0, costUsd: null, units: [] }, byBillingMode: [], groups: [],
  })));
  // The shared catch-all answers this with a partial object; the onboarding provider reads the run lists.
  await page.route(/\/api\/v1\/dashboard\/overview(\?.*)?$/, (r: Route) => r.fulfill(json({
    projects: [], activeExecutions: [], recentExecutions: [], needsAttention: [], recentOutputs: [],
  })));
  // The opened step is read in full from its own endpoint.
  await page.route(/\/executions\/(ex1|ex2)\/step-results\/sr\d+(\?.*)?$/, (r: Route) => {
    const id = r.request().url().split('/step-results/')[1].split('?')[0];
    return r.fulfill(json(execution('ex1').stepResults.find((x) => x.id === id)));
  });
  const re = (tail: string) => new RegExp(`${base}/${tail}(\\?.*)?$`);
  await page.route(re(`workflows/${WID}`), (r: Route) => r.fulfill(json(workflow)));
  await page.route(re('executions/(ex1|ex2)'), (r: Route) => r.fulfill(json(execution(r.request().url().split('/executions/')[1].split('?')[0]))));
  await page.route(re('executions/(ex1|ex2)/change-capabilities'), (r: Route) => r.fulfill(json(capabilities)));
  await page.route(re('executions/(ex1|ex2)/change-requests'), (r: Route) => {
    if (r.request().method() === 'POST') {
      posts.create.push(r.request().postDataJSON());
      const c = sc.create ?? { status: 201, body: historyRow };
      return r.fulfill(json(c.body, c.status));
    }
    return r.fulfill(json({ requests: sc.history ?? [] }));
  });
  await page.route(re('executions/(ex1|ex2)/change-requests/estimate'), (r: Route) => {
    posts.estimate.push(r.request().postDataJSON());
    return r.fulfill(json(sc.estimate ?? estimateOk));
  });
  return posts;
}

export const runUrl = (executionId = SOURCE_EXECUTION) => `/projects/${PROJECT_ID}/workflows/${WID}/executions/${executionId}`;

/** Selects a step by clicking its row in the step list; works on desktop (side panel) and phone (bottom drawer). */
export async function selectStep(page: Page, n: number) {
  // The run page opens on the plain step list (a button per step, in order).
  await page.getByTestId('run-step-row').nth(n - 1).getByRole('button').click();
}
