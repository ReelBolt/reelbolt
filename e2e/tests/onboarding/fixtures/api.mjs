// Fixture API for the screenshot harness. `respond(method, pathname, search)` returns
//   { status, json }            a JSON answer,
//   { media: <clip index> }     a video (served with Range support by capture.mjs),
//   { sse: [{event,data}] }     the first answer of an SSE stream (later calls are held open),
//   null                        unmatched (capture.mjs logs it and answers 404 {}).
// Everything is fictional. Prices/plans come straight from the canonical catalog file.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Resolved from the working directory (Playwright runs from e2e/), so this file also loads as CommonJS.
const CATALOG = JSON.parse(readFileSync(resolve(process.cwd(), '../inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json'), 'utf8'));

// The browser clock is frozen to this instant (capture.mjs), so relative dates are stable.
export const NOW = new Date('2026-10-26T14:20:00+01:00');
const ago = (mins) => new Date(NOW.getTime() - mins * 60_000).toISOString();
const inDays = (d) => new Date(NOW.getTime() + d * 86_400_000).toISOString();

export const ORG_ID = 'b3f1c2d4-0a11-4c55-9a10-5d8e7f2a0001';
export const USER_EMAIL = 'maya.lindqvist@northwind.example';
export const MEDIA_CLIPS = 5;

export const cookieUser = {
  email: USER_EMAIL,
  isPlatformAdmin: false,
  mustChangePassword: false,
  emailVerified: true,
  org: ORG_ID,
  orgName: 'Northwind Studio',
  orgKind: 'Personal',
  orgRole: 'Owner',
};

// ---------------------------------------------------------------- projects
const P = (n, id, name, description, createdAgo) => ({
  id, name, description, status: 'Active', userId: 'u-maya', createdAt: ago(createdAgo), updatedAt: ago(createdAgo / 3),
});
const projects = [
  P(1, 'p-aurora', 'Aurora Trail Series', 'Weekly trail-running episodes for the Aurora outdoor brand.', 60 * 24 * 40),
  P(2, 'p-harbor', 'Harbor Coffee Launch', 'Launch film and cutdowns for the new cold-brew line.', 60 * 24 * 25),
  P(3, 'p-lumen', 'Lumen Product Explainers', 'Short explainers for the Lumen smart-lamp range.', 60 * 24 * 19),
  P(4, 'p-orbit', 'Orbit Podcast Clips', 'Vertical highlight clips from the Orbit weekly podcast.', 60 * 24 * 12),
  P(5, 'p-fjord', 'Fjord Travel Diaries', 'Documentary-style travel shorts from Norway.', 60 * 24 * 7),
  P(6, 'p-pixel', 'Pixel & Pine Tutorials', 'Hands-on woodworking tutorials for a craft channel.', 60 * 24 * 3),
];

const out = (id, projectId, projectName, fileName, producedMinAgo) => ({
  stepResultId: id, projectId, projectName, fileName, producedAt: ago(producedMinAgo),
});
const outputs = [
  out('sr-out-1', 'p-aurora', 'Aurora Trail Series', 'Ep12-Ridgeline-Sunrise.mp4', 38),
  out('sr-out-2', 'p-harbor', 'Harbor Coffee Launch', 'ColdBrew-Hero-30s.mp4', 140),
  out('sr-out-3', 'p-orbit', 'Orbit Podcast Clips', 'Ep88-Highlight-Vertical.mp4', 60 * 5),
  out('sr-out-4', 'p-lumen', 'Lumen Product Explainers', 'Lumen-Glow-Explainer-v2.mp4', 60 * 27),
  out('sr-out-5', 'p-fjord', 'Fjord Travel Diaries', 'Lofoten-Day3-Cut.mp4', 60 * 30),
  out('sr-out-6', 'p-aurora', 'Aurora Trail Series', 'Ep11-Night-Run.mp4', 60 * 52),
];

const po = (projectId, o) => ({
  projectId, sourceFileCount: 0, videoCount: 0, audioCount: 0, imageCount: 0, totalBytes: 0,
  workflowCount: 2, renderCount: 0, activeExecutionCount: 0, failedExecutionCount: 0, lastActivityAt: ago(60), latestOutput: null, ...o,
});
const projectOverview = [
  po('p-aurora', { sourceFileCount: 48, videoCount: 31, audioCount: 9, imageCount: 8, totalBytes: 18_400_000_000, renderCount: 12, activeExecutionCount: 1, lastActivityAt: ago(12), latestOutput: outputs[0] }),
  po('p-harbor', { sourceFileCount: 22, videoCount: 14, audioCount: 3, imageCount: 5, totalBytes: 6_200_000_000, renderCount: 7, lastActivityAt: ago(140), latestOutput: outputs[1] }),
  po('p-lumen', { sourceFileCount: 17, videoCount: 9, audioCount: 2, imageCount: 6, totalBytes: 3_900_000_000, renderCount: 5, failedExecutionCount: 0, lastActivityAt: ago(60 * 27), latestOutput: outputs[3] }),
  po('p-orbit', { sourceFileCount: 11, videoCount: 6, audioCount: 5, imageCount: 0, totalBytes: 2_100_000_000, renderCount: 9, lastActivityAt: ago(60 * 5), latestOutput: outputs[2] }),
  po('p-fjord', { sourceFileCount: 36, videoCount: 29, audioCount: 4, imageCount: 3, totalBytes: 14_800_000_000, renderCount: 4, lastActivityAt: ago(60 * 30), latestOutput: outputs[4] }),
  po('p-pixel', { sourceFileCount: 9, videoCount: 7, audioCount: 1, imageCount: 1, totalBytes: 1_300_000_000, renderCount: 1, lastActivityAt: ago(60 * 70), latestOutput: null }),
];

const ex = (id, projectId, projectName, workflowName, status, startedAgo, tookMin, extra = {}) => ({
  id, projectId, projectName, workflowDefinitionId: 'w-' + projectId.slice(2), workflowName, status,
  startedAt: ago(startedAgo), completedAt: tookMin == null ? null : ago(startedAgo - tookMin),
  errorMessage: null, userRequest: null, outputStepResultId: null, outputFileName: null, runAttention: null, ...extra,
});
const live = ex('e-live-1', 'p-aurora', 'Aurora Trail Series', 'Episode edit', 'Running', 9, null, {
  userRequest: 'Cut episode 13 (Glacier Pass) into a 6 minute episode with the drone footage up front.',
});
const recent = [
  live,
  ex('e-rec-1', 'p-aurora', 'Aurora Trail Series', 'Episode edit', 'Passed', 60 + 38, 24, { outputStepResultId: 'sr-out-1', outputFileName: 'Ep12-Ridgeline-Sunrise.mp4', userRequest: 'Episode 12, sunrise ridgeline run' }),
  ex('e-rec-2', 'p-harbor', 'Harbor Coffee Launch', 'Hero film 30s', 'Passed', 140 + 18, 18, { outputStepResultId: 'sr-out-2', outputFileName: 'ColdBrew-Hero-30s.mp4' }),
  ex('e-rec-3', 'p-lumen', 'Lumen Product Explainers', 'Explainer', 'Passed', 60 * 27 + 20, 20, { outputStepResultId: 'sr-out-4', outputFileName: 'Lumen-Glow-Explainer-v2.mp4', runAttention: { reason: 'quality', stepOrder: 6, score: 6.4, minScore: 8, iterations: 3, message: 'The cut is ready, but the review stopped at 6.4 out of 10 after 3 tries. Open it to ask for changes.' } }),
  ex('e-rec-4', 'p-orbit', 'Orbit Podcast Clips', 'Vertical highlight', 'Passed', 60 * 5 + 9, 9, { outputStepResultId: 'sr-out-3', outputFileName: 'Ep88-Highlight-Vertical.mp4' }),
  ex('e-rec-5', 'p-fjord', 'Fjord Travel Diaries', 'Travel short', 'Failed', 60 * 29, 6, { errorMessage: 'The footage step could not read LOF_0412.mov (the file looks truncated). Re-upload it and run again.' }),
  ex('e-rec-6', 'p-fjord', 'Fjord Travel Diaries', 'Travel short', 'Passed', 60 * 30 + 31, 31, { outputStepResultId: 'sr-out-5', outputFileName: 'Lofoten-Day3-Cut.mp4' }),
];
const dashboardOverview = {
  projects: projectOverview,
  activeExecutions: [live],
  recentExecutions: recent,
  needsAttention: [recent[5]],
  recentOutputs: outputs,
  totals: { projects: 6, sourceFiles: 143, renders: 38, passedLast7Days: 14, failedLast7Days: 1 },
};

// ---------------------------------------------------------------- the run page
const RUN_PROJECT = 'p-aurora';
const RUN_WORKFLOW = 'w-aurora';
const RUN_EXEC = 'e-live-1';
const agentsList = [
  ['a-script', 'Scriptwriter', 'Writer'], ['a-analyst', 'Footage Analyst', 'Analyst'], ['a-voice', 'Narrator', 'Voice'],
  ['a-editor', 'Editor', 'Editor'], ['a-review', 'Director', 'Reviewer'], ['a-render', 'Renderer', 'Renderer'],
].map(([id, name, agentType]) => ({
  id, name, description: name, agentType, systemPrompt: '', isBuiltIn: false, ownerId: 'u-maya', createdAt: ago(60 * 24 * 30),
  color: null, outputSchemaJson: null, availableTools: [], generatesOutput: false, outputSchemaName: null,
  inferenceProviderId: null, inferenceProviderName: null, assignedSkills: [], effectiveSkills: [],
}));
const wfSteps = [
  ['s1', 'a-script', 'Write the episode script', 'Agent'],
  ['s2', 'a-analyst', 'Review drone and trail footage', 'VideoAnalyze'],
  ['s3', 'a-voice', 'Record narration', 'Voiceover'],
  ['s4', 'a-editor', 'Assemble the rough cut', 'EditRoom'],
  ['s5', 'a-review', 'Director review', 'Agent'],
  ['s6', 'a-render', 'Render final episode', 'TimelineRender'],
].map(([id, agentDefinitionId, label, stepType], i) => ({
  id, agentDefinitionId, stepOrder: i + 1, label, stepType, maxIterations: 1,
}));
const workflow = { id: RUN_WORKFLOW, name: 'Episode edit', createdAt: ago(60 * 24 * 30), updatedAt: ago(60 * 24 * 2), steps: wfSteps, requiresUserInput: true };

const sr = (id, stepId, order, label, type, status, startAgo, dur, credits, extra = {}) => ({
  id, workflowStepId: stepId, output: '', tokensUsed: 4200, durationMs: dur, executedAt: ago(startAgo),
  status, completedAt: status === 'Completed' ? ago(startAgo - dur / 60000) : null, iterationNumber: 1,
  creditsCharged: credits, stepOrder: order, stepLabel: label, stepType: type, fromCache: false, ...extra,
});
const stepResults = [
  sr('r1', 's1', 1, wfSteps[0].label, 'Agent', 'Completed', 9, 41_000, 6),
  sr('r2', 's2', 2, wfSteps[1].label, 'VideoAnalyze', 'Completed', 8.3, 126_000, 38),
  sr('r3', 's3', 3, wfSteps[2].label, 'Voiceover', 'Completed', 6.1, 78_000, 22, { outputStorageKey: 'outputs/p-aurora/narration.mp3' }),
  sr('r4', 's4', 4, wfSteps[3].label, 'EditRoom', 'Completed', 4.7, 142_000, 64, { outputStorageKey: 'outputs/p-aurora/rough-cut.mp4' }),
  sr('r5', 's5', 5, wfSteps[4].label, 'Agent', 'Completed', 2.2, 36_000, 9),
  sr('r6', 's6', 6, wfSteps[5].label, 'TimelineRender', 'Running', 1.4, 0, null),
];
const execution = {
  id: RUN_EXEC, workflowDefinitionId: RUN_WORKFLOW, status: 'Running', startedAt: ago(9), completedAt: null, iterationCount: 1,
  resultJson: null, stepResults, steps: wfSteps, canResume: false, resumeCount: 0,
  reviewScores: [{ id: 'rv1', iterationNumber: 1, score: 8.7, comments: 'Strong opening on the glacier; pacing tightens nicely.', createdAt: ago(2.2) }],
  needsAttention: null, userRequest: live.userRequest, styleName: 'Documentary', outputFormat: 'Landscape', outputResolution: '1080p',
  brief: null, designSystemPin: null, designSystemWarnings: null,
};
const t = (m) => ago(m);
const sseEvents = [
  { event: 'connected', data: {} },
  ...stepResults.slice(0, 5).flatMap((r, i) => [
    { event: 'step.started', data: { type: 'step.started', executionId: RUN_EXEC, timestamp: t(9 - i * 1.6), data: { stepId: r.workflowStepId, stepLabel: r.stepLabel, stepOrder: r.stepOrder } } },
    { event: 'step.completed', data: { type: 'step.completed', executionId: RUN_EXEC, timestamp: t(8.4 - i * 1.6), data: { stepId: r.workflowStepId, stepLabel: r.stepLabel, stepOrder: r.stepOrder, durationMs: r.durationMs, tokensUsed: 4200, inputTokens: 3100, outputTokens: 1100 } } },
  ]),
  { event: 'step.started', data: { type: 'step.started', executionId: RUN_EXEC, timestamp: t(1.4), data: { stepId: 's6', stepLabel: wfSteps[5].label, stepOrder: 6 } } },
  { event: 'step.progress', data: { type: 'step.progress', executionId: RUN_EXEC, timestamp: t(0.2), data: { stepId: 's6', stepLabel: wfSteps[5].label, stage: 'Rendering frames', percentComplete: 62 } } },
];

// ---------------------------------------------------------------- billing
const periodStart = new Date(NOW.getTime() - 11 * 86_400_000).toISOString();
const periodEnd = inDays(19);
const planOf = (p) => ({
  key: p.id, name: p.name, kind: p.kind, priceMonthlyUsd: p.priceMonthly ?? 0, minSeats: p.minSeats ?? null,
  includedCredits: p.includedCredits, priceId: `pri_${p.id}_monthly`, current: p.id === 'pro',
});
const billingPlans = CATALOG.plans.filter((p) => p.kind === 'subscription' || p.kind === 'perSeat').map(planOf);
const billingSummary = {
  billingProvider: 'paddle',
  billingProviderDisplayName: 'Paddle',
  paddle: null,
  canManageBilling: true,
  subscription: {
    planKey: 'pro', planName: 'Pro', status: 'active', seats: 1, periodStart, periodEnd,
    cancelAtPeriodEnd: false, cancelEffectiveAt: null, scheduledChange: null, hasBillingCustomer: true, promoDiscount: null,
  },
  credits: {
    balance: 2412, reserved: 140, available: 2272,
    grants: [
      { source: 'plan', remaining: 2112, expiresAt: periodEnd },
      { source: 'topUp', remaining: 300, expiresAt: inDays(240) },
    ],
  },
  topUp: { available: true, packs: CATALOG.topUp.packs.map((p) => p.credits) },
  plans: billingPlans,
  assistantAllowance: { includedCredits: 300, usedCredits: 84, periodStart, periodEnd },
  entitlements: { maxOutputHeight: 2160 },
  firstVideoGrant: null,
  firstVideoGrantStatus: 'none',
};
const usageFor = (qs) => ({
  from: periodStart, to: NOW.toISOString(), groupBy: qs.get('groupBy') ?? 'day',
  totals: { credits: qs.get('executionId') ? 139 : 1388, events: qs.get('executionId') ? 31 : 412, costUsd: null },
  byBillingMode: [{ mode: 'Credits', credits: 1388, events: 412, costUsd: null }],
  groups: [],
});

const orgs = [{ id: ORG_ID, name: 'Northwind Studio', slug: 'northwind-studio', kind: 'Personal', role: 'Owner', createdAt: ago(60 * 24 * 90), computePreference: 'cloud' }];

const selectableProviders = [
  ['sp-chat', 'Chat', 'Claude Sonnet', 'Chat'], ['sp-vision', 'Vision', 'Gemini Flash', 'Vision'], ['sp-voice', 'Speech', 'ElevenLabs Studio', 'TextToSpeech'],
].map(([id, name, model, capability], i) => ({
  id, name, capability, model, isPlatformManaged: true, kind: ['Anthropic', 'Gemini', 'ElevenLabs'][i], providerName: ['Anthropic', 'Google', 'ElevenLabs'][i], costIndex: 2, isDefault: true,
}));

const json = (body, status = 200) => ({ status, json: body });

export function respond(method, pathname, search) {
  const qs = new URLSearchParams(search);
  const p = pathname;
  if (method !== 'GET') return json({ ok: true });

  if (p === '/api/v1/orgs') return json(orgs);
  if (p === '/api/v1/billing/summary') return json(billingSummary);
  if (p === '/api/v1/billing/usage') return json(usageFor(qs));
  if (p === '/api/v1/projects') return json(projects);
  if (p === '/api/v1/dashboard/overview') return json(dashboardOverview);
  if (p === '/api/v1/inference-providers/selectable') return json(selectableProviders);
  if (p === '/api/v1/assistant/activity') return json({ writing: [], unread: [], pendingWatches: 0 });
  if (p === '/api/v1/workflows/events') return { sse: [{ event: 'connected', data: {} }], holdOpen: true };
  if (p === '/api/v1/storage/usage') {
    return json({ usedBytes: 46_700_000_000, limitBytes: 50 * 1024 ** 3, byProject: projects.slice(0, 5).map((pr, i) => ({ projectId: pr.id, name: pr.name, usedBytes: [18_400_000_000, 14_800_000_000, 6_200_000_000, 3_900_000_000, 2_100_000_000][i] })) });
  }
  if (p === '/api/v1/billing/promotions/active') return json({ items: [] });
  if (p === '/api/v1/marketing/consent') return json({ status: 'none', wordingVersion: null, consentedAt: null, confirmedAt: null, withdrawnAt: null });
  if (p === '/api/v1/marketing/wording') return json({ version: '2026-01', text: 'Send me product news and offers by email. I can withdraw at any time.' });
  if (p === '/api/v1/agents') return json(agentsList);
  if (p === '/api/v1/runners') return json([]);
  const proj = /^\/api\/v1\/projects\/([^/]+)(\/.*)?$/.exec(p);
  if (proj) {
    const [, pid, rest = ''] = proj;
    const project = projects.find((x) => x.id === pid);
    if (rest === '' && project) return json(project);
    if (rest === `/workflows/${RUN_WORKFLOW}`) return json(workflow);
    if (rest === `/workflows/${RUN_WORKFLOW}/executions`) return json([{ ...execution, stepResults: [] }]);
    if (rest === `/executions/${RUN_EXEC}`) return json(execution);
    const sres = new RegExp(`^/executions/${RUN_EXEC}/step-results/(.+)$`).exec(rest);
    if (sres) return json(stepResults.find((r) => r.id === sres[1]) ?? {}, stepResults.some((r) => r.id === sres[1]) ? 200 : 404);
    if (rest === `/executions/${RUN_EXEC}/change-requests`) return json({ requests: [] });
    if (rest === `/executions/${RUN_EXEC}/change-capabilities`) return json({ executionId: RUN_EXEC, runFinished: false, steps: [] });
    // The real API answers 404 while a run has no finished video to revise; the page treats that as "nothing to show".
    if (rest === `/executions/${RUN_EXEC}/revisions`) return json({}, 404);
    const dl = /^\/outputs\/([^/]+)\/download$/.exec(rest);
    if (dl) {
      const all = ['r4', 'sr-out-1', 'sr-out-2', 'sr-out-3', 'sr-out-4', 'sr-out-5', 'sr-out-6'];
      const idx = Math.max(0, all.indexOf(dl[1]));
      return { media: idx % MEDIA_CLIPS };
    }
  }
  const stream = new RegExp(`^/api/v1/workflows/executions/${RUN_EXEC}/events$`).exec(p);
  if (stream) return { sse: sseEvents, holdOpen: true };
  return null;
}

export const ROUTES = {
  home: '/app/dashboard',
  run: `/app/projects/${RUN_PROJECT}/workflows/${RUN_WORKFLOW}/executions/${RUN_EXEC}`,
  billing: '/app/settings/billing',
};
