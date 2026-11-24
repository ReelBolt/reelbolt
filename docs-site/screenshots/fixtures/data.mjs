// Fictional data behind the screenshot harness. Everything here is invented: the company
// "Northwind Studio", its people (example.com addresses only), projects, files and runs.
// Numbers are round on purpose. Nothing is read from a real account or a real server.
// Prices and plans come from the canonical catalog file in the repository, never typed by hand.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
export const CATALOG = JSON.parse(
  readFileSync(resolve(here, '../../../inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json'), 'utf8'),
);

// The browser clock is frozen to this instant, so relative dates ("2 hours ago") never move.
export const NOW = new Date('2026-10-27T09:20:00Z');
export const ago = (mins) => new Date(NOW.getTime() - mins * 60_000).toISOString();
export const inDays = (d) => new Date(NOW.getTime() + d * 86_400_000).toISOString();
export const MIN = 1;
export const HOUR = 60;
export const DAY = 60 * 24;

export const ORG_ID = 'b3f1c2d4-0a11-4c55-9a10-5d8e7f2a0001';
export const USER_EMAIL = 'maya.lindqvist@example.com';
export const MEDIA_CLIPS = 4;

export const cookieUser = {
  email: USER_EMAIL,
  isPlatformAdmin: false,
  mustChangePassword: false,
  emailVerified: true,
  org: ORG_ID,
  orgName: 'Northwind Studio',
  orgKind: 'Team',
  orgRole: 'Owner',
};

export const orgs = [
  { id: ORG_ID, name: 'Northwind Studio', slug: 'northwind-studio', kind: 'Team', role: 'Owner', createdAt: ago(90 * DAY), computePreference: 'cloud' },
];

export const members = [
  { userId: 'u-maya', email: USER_EMAIL, displayName: 'Maya Lindqvist', role: 'Owner', joinedAt: ago(90 * DAY) },
  { userId: 'u-jonas', email: 'jonas.berg@example.com', displayName: 'Jonas Berg', role: 'Member', joinedAt: ago(40 * DAY) },
];

// ---------------------------------------------------------------- projects
export const P = 'p-aurora';
export const W = 'w-aurora';
export const LIVE_EXEC = 'e-live-1';
export const DONE_EXEC = 'e-done-1';

const project = (id, name, description, createdAgoDays) => ({
  id, name, description, status: 'Active', userId: 'u-maya', createdAt: ago(createdAgoDays * DAY), updatedAt: ago((createdAgoDays * DAY) / 3),
});
export const projects = [
  project('p-aurora', 'Aurora Trail Series', 'Weekly trail-running episodes for the Aurora outdoor brand.', 40),
  project('p-harbor', 'Harbor Coffee Launch', 'Launch film and cutdowns for the new cold-brew line.', 25),
  project('p-lumen', 'Lumen Product Explainers', 'Short explainers for the Lumen smart-lamp range.', 20),
  project('p-orbit', 'Orbit Podcast Clips', 'Vertical highlight clips from the Orbit weekly podcast.', 12),
  project('p-fjord', 'Fjord Travel Diaries', 'Documentary-style travel shorts from Norway.', 8),
  project('p-pixel', 'Pixel & Pine Tutorials', 'Hands-on woodworking tutorials for a craft channel.', 3),
];

const out = (id, projectId, projectName, fileName, producedMinAgo) => ({
  stepResultId: id, projectId, projectName, fileName, producedAt: ago(producedMinAgo),
});
const recentOutputs = [
  out('sr-out-1', 'p-aurora', 'Aurora Trail Series', 'Ep12-Ridgeline-Sunrise.mp4', 40),
  out('sr-out-2', 'p-harbor', 'Harbor Coffee Launch', 'ColdBrew-Hero-30s.mp4', 2 * HOUR),
  out('sr-out-3', 'p-orbit', 'Orbit Podcast Clips', 'Ep88-Highlight-Vertical.mp4', 5 * HOUR),
  out('sr-out-4', 'p-lumen', 'Lumen Product Explainers', 'Lumen-Glow-Explainer-v2.mp4', 27 * HOUR),
  out('sr-out-5', 'p-fjord', 'Fjord Travel Diaries', 'Lofoten-Day3-Cut.mp4', 30 * HOUR),
  out('sr-out-6', 'p-aurora', 'Aurora Trail Series', 'Ep11-Night-Run.mp4', 52 * HOUR),
];

const overview = (projectId, o) => ({
  projectId, sourceFileCount: 0, videoCount: 0, audioCount: 0, imageCount: 0, totalBytes: 0,
  workflowCount: 2, renderCount: 0, activeExecutionCount: 0, failedExecutionCount: 0, lastActivityAt: ago(HOUR), latestOutput: null, ...o,
});
const GB = 1_000_000_000;
const projectOverview = [
  overview('p-aurora', { sourceFileCount: 50, videoCount: 30, audioCount: 10, imageCount: 10, totalBytes: 18 * GB, renderCount: 12, activeExecutionCount: 1, lastActivityAt: ago(10), latestOutput: recentOutputs[0] }),
  overview('p-harbor', { sourceFileCount: 20, videoCount: 14, audioCount: 2, imageCount: 4, totalBytes: 6 * GB, renderCount: 7, lastActivityAt: ago(2 * HOUR), latestOutput: recentOutputs[1] }),
  overview('p-lumen', { sourceFileCount: 15, videoCount: 9, audioCount: 2, imageCount: 4, totalBytes: 4 * GB, renderCount: 5, lastActivityAt: ago(27 * HOUR), latestOutput: recentOutputs[3] }),
  overview('p-orbit', { sourceFileCount: 10, videoCount: 6, audioCount: 4, imageCount: 0, totalBytes: 2 * GB, renderCount: 9, lastActivityAt: ago(5 * HOUR), latestOutput: recentOutputs[2] }),
  overview('p-fjord', { sourceFileCount: 35, videoCount: 29, audioCount: 3, imageCount: 3, totalBytes: 15 * GB, renderCount: 4, lastActivityAt: ago(30 * HOUR), latestOutput: recentOutputs[4] }),
  overview('p-pixel', { sourceFileCount: 10, videoCount: 7, audioCount: 1, imageCount: 2, totalBytes: 1 * GB, renderCount: 1, lastActivityAt: ago(70 * HOUR), latestOutput: null }),
];

const execRow = (id, projectId, projectName, workflowName, status, startedAgo, tookMin, extra = {}) => ({
  id, projectId, projectName, workflowDefinitionId: 'w-' + projectId.slice(2), workflowName, status,
  startedAt: ago(startedAgo), completedAt: tookMin == null ? null : ago(startedAgo - tookMin),
  errorMessage: null, userRequest: null, outputStepResultId: null, outputFileName: null, runAttention: null, ...extra,
});
const liveRow = execRow(LIVE_EXEC, 'p-aurora', 'Aurora Trail Series', 'Episode edit', 'Running', 10, null, {
  userRequest: 'Cut episode 13 (Glacier Pass) into a 6 minute episode with the drone footage up front.',
});
const recentRows = [
  liveRow,
  execRow(DONE_EXEC, 'p-aurora', 'Aurora Trail Series', 'Episode edit', 'Passed', 60 + 40, 25, { outputStepResultId: 'sr-out-1', outputFileName: 'Ep12-Ridgeline-Sunrise.mp4', userRequest: 'Episode 12, sunrise ridgeline run' }),
  execRow('e-rec-2', 'p-harbor', 'Harbor Coffee Launch', 'Hero film 30s', 'Passed', 2 * HOUR + 20, 20, { outputStepResultId: 'sr-out-2', outputFileName: 'ColdBrew-Hero-30s.mp4' }),
  execRow('e-rec-3', 'p-lumen', 'Lumen Product Explainers', 'Explainer', 'Passed', 27 * HOUR + 20, 20, { outputStepResultId: 'sr-out-4', outputFileName: 'Lumen-Glow-Explainer-v2.mp4' }),
  execRow('e-rec-4', 'p-orbit', 'Orbit Podcast Clips', 'Vertical highlight', 'Passed', 5 * HOUR + 10, 10, { outputStepResultId: 'sr-out-3', outputFileName: 'Ep88-Highlight-Vertical.mp4' }),
  execRow('e-fail-1', 'p-fjord', 'Fjord Travel Diaries', 'Travel short', 'Failed', 29 * HOUR, 6, { errorMessage: 'The footage step could not read LOF_0412.mov (the file looks truncated). Re-upload it and run again.' }),
  execRow('e-rec-6', 'p-fjord', 'Fjord Travel Diaries', 'Travel short', 'Passed', 30 * HOUR + 30, 30, { outputStepResultId: 'sr-out-5', outputFileName: 'Lofoten-Day3-Cut.mp4' }),
];
export const dashboardOverview = {
  projects: projectOverview,
  activeExecutions: [liveRow],
  recentExecutions: recentRows,
  needsAttention: [recentRows[5]],
  recentOutputs,
  totals: { projects: 6, sourceFiles: 140, renders: 40, passedLast7Days: 14, failedLast7Days: 1 },
};

// ---------------------------------------------------------------- files of the Aurora project
const file = (id, name, dir, category, mime, size, minsAgo, extra = {}) => ({
  id, projectId: P, originalFileName: name, originalPath: dir ? `${dir}/${name}` : name, directoryPath: dir || '',
  sourceKind: null, category, mimeType: mime, sizeBytes: size, storageKey: `files/${P}/${id}`, agentSummary: null,
  summaryStatus: 'Done', indexingStatus: 'Indexed', uploadedAt: ago(minsAgo), ...extra,
});
export const files = [
  file('f1', 'LOF_0101_glacier-drone.mov', 'footage/drone', 'userFiles', 'video/quicktime', 1_800_000_000, 9 * DAY, { agentSummary: 'Drone pass over the glacier at sunrise, steady, 4K, no people in frame.' }),
  file('f2', 'LOF_0102_ridgeline-runner.mov', 'footage/trail', 'userFiles', 'video/quicktime', 2_300_000_000, 9 * DAY, { agentSummary: 'Runner climbs the ridgeline, handheld, good light in the first minute.' }),
  file('f3', 'LOF_0103_creek-crossing.mp4', 'footage/trail', 'userFiles', 'video/mp4', 800_000_000, 8 * DAY, { agentSummary: 'Two runners cross a shallow creek, wide shot, steady light.' }),
  file('f4', 'LOF_0104_summit-wide.mp4', 'footage/trail', 'userFiles', 'video/mp4', 1_100_000_000, 8 * DAY, { agentSummary: 'Wide shot from the summit, clouds moving, no one in frame.' }),
  file('f5', 'narration-ep12.wav', 'audio', 'userFiles', 'audio/wav', 100_000_000, 6 * DAY, { agentSummary: 'Narration take 2, clean, one cough at 02:14.' }),
  file('f6', 'aurora-theme.mp3', 'audio', 'userFiles', 'audio/mpeg', 8_000_000, 6 * DAY),
  file('f7', 'aurora-logo.png', 'brand', 'userFiles', 'image/png', 200_000, 30 * DAY),
  file('f8', 'brand-guidelines.pdf', 'brand', 'userFiles', 'application/pdf', 3_000_000, 30 * DAY),
  file('f9', 'episode-notes.md', '', 'userFiles', 'text/markdown', 6_000, 2 * DAY, { agentSummary: 'Notes for episode 13: open on the drone shot, keep narration under 90 seconds.' }),
  file('f10', 'Ep12-Ridgeline-Sunrise.mp4', 'renders', 'outputFiles', 'video/mp4', 400_000_000, 2 * HOUR),
];

export const outputs = [
  ['sr-out-1', 'Ep12-Ridgeline-Sunrise.mp4', 100, { isPinned: true }],
  ['sr-out-2', 'Ep11-Glacier-Pass.mp4', 7 * DAY, { runNumber: 11, takeGroupKey: 'ep11' }],
  ['sr-out-3', 'Ep10-Creek-Crossing.mp4', 14 * DAY, { runNumber: 10, takeGroupKey: 'ep10' }],
].map(([id, name, mins, extra]) => ({
  stepResultId: id, workflowExecutionId: DONE_EXEC, workflowStepId: 's6', storageKey: `outputs/${P}/${name}`, fileName: name, producedAt: ago(mins),
  workflowDefinitionId: W, workflowName: 'Episode edit', runNumber: 12, runStartedAt: ago(mins + 25), executionStatus: 'Passed', stepLabel: 'Render final episode',
  stepType: 'TimelineRender', iterationNumber: 1, durationSec: 360, width: 1920, height: 1080, isFinal: true, warnings: null, takeGroupKey: 'ep12', takeIndex: 1, takeCount: 1, isLatestTake: true,
  isIntermediate: false, isPinned: false, ...extra,
}));

// ---------------------------------------------------------------- workflows and apps
export const agents = [
  ['a-script', 'Scriptwriter', 'Writer'], ['a-analyst', 'Footage Analyst', 'Analyst'], ['a-voice', 'Narrator', 'Voice'],
  ['a-editor', 'Editor', 'Editor'], ['a-review', 'Director', 'Reviewer'], ['a-render', 'Renderer', 'Renderer'],
].map(([id, name, agentType]) => ({
  id, name, description: `${name} agent for ${agentType.toLowerCase()} work.`, agentType, systemPrompt: `You are the ${name}. Work from the project notes and keep answers short.`,
  isBuiltIn: false, ownerId: 'u-maya', createdAt: ago(30 * DAY), color: null, outputSchemaJson: null, availableTools: [], generatesOutput: false, outputSchemaName: null,
  inferenceProviderId: null, inferenceProviderName: null, assignedSkills: [], effectiveSkills: [], reasoningEffort: null,
}));

export const wfSteps = [
  ['s1', 'a-script', 'Write the episode script', 'Agent'],
  ['s2', 'a-analyst', 'Review drone and trail footage', 'VideoAnalyze'],
  ['s3', 'a-voice', 'Record narration', 'Voiceover'],
  ['s4', 'a-editor', 'Assemble the rough cut', 'EditRoom'],
  ['s5', 'a-review', 'Director review', 'Agent'],
  ['s6', 'a-render', 'Render final episode', 'TimelineRender'],
].map(([id, agentDefinitionId, label, stepType], i) => ({ id, agentDefinitionId, stepOrder: i + 1, label, stepType, maxIterations: 1 }));

export const workflows = [
  { id: W, name: 'Episode edit', createdAt: ago(30 * DAY), updatedAt: ago(2 * DAY), steps: wfSteps, requiresUserInput: true },
  { id: 'w-teaser', name: 'Social teaser 20s', createdAt: ago(21 * DAY), updatedAt: ago(5 * DAY), steps: wfSteps.slice(0, 3).map((s, i) => ({ ...s, id: `t${i}`, stepOrder: i + 1 })), requiresUserInput: false },
];
export const templates = [
  { key: 'episode', name: 'Episode edit', description: 'Script, narration and a full cut from your footage.', version: 3, autoCreateOnProject: true, requiresUserInput: true, stepCount: 6, requiresProfile: false },
  { key: 'teaser', name: 'Social teaser', description: 'A 20 second vertical teaser from the best moments.', version: 1, autoCreateOnProject: false, requiresUserInput: false, stepCount: 4, requiresProfile: false },
];
export const apps = {
  apps: [
    { workflowId: W, title: 'Episode edit', tagline: 'Turn raw trail footage into a finished episode.', category: 'footage', costClass: 'Uses credits and a voice', inputsCount: 3, lastRun: { status: 'Passed', finishedAt: ago(100) }, source: 'episode' },
    { workflowId: 'w-teaser', title: 'Social teaser 20s', tagline: 'A vertical teaser for social feeds.', category: 'promo', costClass: 'Uses credits', inputsCount: 2, lastRun: { status: 'Passed', finishedAt: ago(2 * DAY) }, source: 'teaser' },
    { workflowId: 'w-captions', title: 'Caption clean-up', tagline: 'Fix and style the captions of a finished video.', category: 'utility', costClass: 'No credits', inputsCount: 1, lastRun: null, source: 'custom' },
  ],
  recipes: [
    { key: 'promo-ds', title: 'Brand promo', tagline: 'A short promo that follows your brand profile.', category: 'brand', costClass: 'Uses credits and AI shots', inputsCount: 4, fit: 'needs', missing: [{ need: 'Brand profile', message: 'Build and approve a brand profile first.', action: 'build-profile' }] },
    { key: 'recap', title: 'Season recap', tagline: 'Stitch the best moments of a season.', category: 'footage', costClass: 'Uses credits', inputsCount: 2, fit: 'ready', missing: [] },
    { key: 'subtitles', title: 'Add subtitles', tagline: 'Caption a finished video from its speech.', category: 'utility', costClass: 'Uses credits', inputsCount: 1, fit: 'ready', missing: [] },
  ],
};
export const appManifest = {
  workflowId: W, name: 'Episode edit', hasExplicitManifest: true, costClass: 'Uses credits and a voice', source: 'episode',
  manifest: {
    version: 1, title: 'Episode edit', tagline: 'Turn raw trail footage into a finished episode.', category: 'footage', suggestedStylePreset: 'documentary',
    inputs: [
      { kind: 'brief', field: 'userRequest', label: 'What should the episode be about?' },
      { kind: 'files', role: 'video', label: 'Footage to use' },
      { kind: 'notes', label: 'Notes for the editor' },
    ],
    result: { kind: 'video' }, advanced: { allowGraph: true },
  },
};
export const estimate = { billingEnabled: true, hasHistory: true, lowCredits: 200, highCredits: 300, typicalCredits: 250, rangeBasis: 'history', pastRunCount: 6, spreadReasons: [], requiredCredits: 300, balanceCredits: 2300, affordable: true, steps: [] };

// ---------------------------------------------------------------- runs
const stepResult = (id, step, status, startAgo, dur, credits, extra = {}) => {
  const s = wfSteps[step - 1];
  return {
    id, workflowStepId: s.id, output: status === 'Completed' ? 'Done.' : '', tokensUsed: status === 'Pending' ? 0 : 4000, durationMs: dur, executedAt: ago(startAgo), status,
    completedAt: status === 'Completed' || status === 'Failed' ? ago(startAgo - dur / 60000) : null, iterationNumber: 1, creditsCharged: credits,
    stepOrder: step, stepLabel: s.label, stepType: s.stepType, fromCache: false, ...extra,
  };
};
const execBase = {
  workflowDefinitionId: W, iterationCount: 1, resultJson: null, steps: wfSteps, canResume: false, resumeCount: 0, reviewScores: [], needsAttention: null,
  brief: null, designSystemPin: null, designSystemWarnings: null, styleName: 'Documentary', outputFormat: 'Landscape', outputResolution: '1080p',
};
// The run in progress is half way: three of six steps done, the fourth (the rough cut) working.
export const liveResults = [
  stepResult('r1', 1, 'Completed', 10, 40_000, 5),
  stepResult('r2', 2, 'Completed', 9, 120_000, 40),
  stepResult('r3', 3, 'Completed', 7, 80_000, 20, { outputStorageKey: `outputs/${P}/narration.mp3` }),
  stepResult('r4', 4, 'Running', 5, 0, null),
];
export const executions = {
  [LIVE_EXEC]: {
    ...execBase, id: LIVE_EXEC, status: 'Running', startedAt: ago(10), completedAt: null, userRequest: liveRow.userRequest, stepResults: liveResults,
    reviewScores: [{ id: 'rv1', iterationNumber: 1, score: 8.5, comments: 'Strong opening on the glacier; pacing tightens nicely.', createdAt: ago(2.5) }],
  },
  [DONE_EXEC]: {
    ...execBase, id: DONE_EXEC, status: 'Passed', startedAt: ago(125), completedAt: ago(100), userRequest: 'Episode 12, sunrise ridgeline run',
    stepResults: [
      stepResult('d1', 1, 'Completed', 125, 40_000, 5), stepResult('d2', 2, 'Completed', 124, 120_000, 40),
      stepResult('d3', 3, 'Completed', 120, 80_000, 20, { outputStorageKey: `outputs/${P}/narration.mp3` }),
      stepResult('d4', 4, 'Completed', 116, 140_000, 65, { outputStorageKey: `outputs/${P}/rough-cut.mp4` }),
      stepResult('d5', 5, 'Completed', 112, 40_000, 10), stepResult('d6', 6, 'Completed', 108, 480_000, 120, { outputStorageKey: `outputs/${P}/Ep12.mp4` }),
    ],
    reviewScores: [{ id: 'rv1', iterationNumber: 1, score: 8.5, comments: 'Strong opening on the glacier; pacing tightens nicely.', createdAt: ago(110) }],
  },
  'e-fail-1': {
    ...execBase, id: 'e-fail-1', status: 'Failed', startedAt: ago(29 * HOUR), completedAt: ago(29 * HOUR - 6), userRequest: 'Episode 11, glacier pass',
    errorMessage: 'The footage step could not read LOF_0412.mov (the file looks truncated). Re-upload it and run again.',
    stepResults: [stepResult('x1', 1, 'Completed', 29 * HOUR, 40_000, 5), stepResult('x2', 2, 'Failed', 29 * HOUR - 1, 60_000, 10, { errorDetails: 'LOF_0412.mov: moov atom not found. The file looks truncated.' })],
  },
};
export const executionList = Object.values(executions).map((e) => ({ ...e, stepResults: [] }));

const sse = (type, data, minAgo) => ({ event: type, data: { type, executionId: LIVE_EXEC, timestamp: ago(minAgo), data } });
export const liveEvents = [
  { event: 'connected', data: {} },
  ...liveResults.slice(0, 3).flatMap((r, i) => [
    sse('step.started', { stepId: r.workflowStepId, stepLabel: r.stepLabel, stepOrder: r.stepOrder }, 10 - i * 1.7),
    sse('step.completed', { stepId: r.workflowStepId, stepLabel: r.stepLabel, stepOrder: r.stepOrder, durationMs: r.durationMs, tokensUsed: 4000, inputTokens: 3000, outputTokens: 1000 }, 9.4 - i * 1.7),
  ]),
  sse('step.started', { stepId: 's4', stepLabel: wfSteps[3].label, stepOrder: 4 }, 5),
  sse('step.progress', { stepId: 's4', stepLabel: wfSteps[3].label, stage: 'Cutting the episode', percentComplete: 50 }, 0.2),
];

// ---------------------------------------------------------------- billing
const periodStart = ago(11 * DAY);
const periodEnd = inDays(19);
const billingPlans = CATALOG.plans.filter((p) => p.kind === 'subscription' || p.kind === 'perSeat').map((p) => ({
  key: p.id, name: p.name, kind: p.kind, priceMonthlyUsd: p.priceMonthly ?? 0, minSeats: p.minSeats ?? null,
  includedCredits: p.includedCredits, priceId: `pri_${p.id}_monthly`, current: p.id === 'pro',
}));
export const billingSummary = {
  billingProvider: 'paddle',
  billingProviderDisplayName: 'Paddle',
  paddle: null,
  canManageBilling: true,
  subscription: {
    planKey: 'pro', planName: 'Pro', status: 'active', seats: 1, periodStart, periodEnd,
    cancelAtPeriodEnd: false, cancelEffectiveAt: null, scheduledChange: null, hasBillingCustomer: true, promoDiscount: null,
  },
  credits: {
    balance: 2400, reserved: 100, available: 2300,
    grants: [
      { source: 'plan', remaining: 2100, expiresAt: periodEnd },
      { source: 'topUp', remaining: 300, expiresAt: inDays(240) },
    ],
  },
  topUp: { available: true, packs: CATALOG.topUp.packs.map((p) => p.credits) },
  plans: billingPlans,
  assistantAllowance: { includedCredits: 300, usedCredits: 80, periodStart, periodEnd },
  entitlements: { maxOutputHeight: 2160 },
  firstVideoGrant: null,
  firstVideoGrantStatus: 'none',
};
export const usageFor = (qs) => ({
  from: periodStart, to: NOW.toISOString(), groupBy: qs.get('groupBy') ?? 'day',
  totals: { credits: qs.get('executionId') ? 140 : 1400, events: qs.get('executionId') ? 30 : 400, costUsd: null },
  byBillingMode: [{ mode: 'Credits', credits: 1400, events: 400, costUsd: null }],
  groups: [],
});

// The platform provides every capability, as on the hosted service, so the dashboard's "set up
// providers" card stays out of the picture.
export const selectableProviders = [
  ['Chat', 'Claude Sonnet', 'Anthropic'], ['Transcription', 'Speech to text', 'OpenAI'], ['Vision', 'Gemini Flash', 'Google'],
  ['SpeechSynthesis', 'Studio voices', 'ElevenLabs'], ['VideoGeneration', 'Clip generator', 'MiniMax'], ['Embedding', 'Search embeddings', 'OpenAI'],
  ['ImageGeneration', 'Image generator', 'fal.ai'], ['ImageEdit', 'Image editor', 'fal.ai'], ['Segmentation', 'Cut-outs', 'fal.ai'], ['Depth', 'Depth maps', 'fal.ai'],
].map(([capability, model, providerName], i) => ({
  id: `sp-${i}`, name: model, capability, model, isPlatformManaged: true, kind: providerName, providerName, costIndex: 2, isDefault: true,
}));

// ---------------------------------------------------------------- runners
export const runners = [
  { id: 'rn1', name: 'Render box (studio)', os: 'linux', arch: 'x64', runnerVersion: '0.9.4', createdAt: ago(30 * DAY), lastSeenAt: ago(1), pairedByUserId: 'u-maya', capabilities: { gpu: true } },
  { id: 'rn2', name: 'Maya laptop', os: 'darwin', arch: 'arm64', runnerVersion: '0.9.4', createdAt: ago(12 * DAY), lastSeenAt: ago(2 * DAY), pairedByUserId: 'u-maya', capabilities: {} },
];

// ================================================================ the "Spring launch teaser" project
// A fresh project a new customer makes in the guides: root-level files, one code folder, one finished render.
export const SPRING = 'p-spring';
export const springProject = project('p-spring', 'Spring launch teaser', '30 second teaser cut from the April shoot.', 1);
const sfile = (id, name, dir, category, mime, size, minsAgo, extra = {}) => ({
  id, projectId: SPRING, originalFileName: name, originalPath: dir ? `${dir}/${name}` : name, directoryPath: dir || '',
  sourceKind: null, category, mimeType: mime, sizeBytes: size, storageKey: `files/${SPRING}/${id}`, agentSummary: null,
  summaryStatus: 'Done', indexingStatus: 'Indexed', uploadedAt: ago(minsAgo), ...extra,
});
export const springFiles = [
  sfile('sf1', 'product-demo.mp4', '', 'userFiles', 'video/mp4', 84_000_000, 8, { agentSummary: 'Screen recording of the app: sign-in, dashboard, the new report view. Clear, no audio.' }),
  sfile('sf2', 'launch-music.mp3', '', 'userFiles', 'audio/mpeg', 6_000_000, 8, { agentSummary: 'Upbeat instrumental, 2 minutes, steady tempo.' }),
  sfile('sf3', 'logo.png', '', 'userFiles', 'image/png', 120_000, 7),
  sfile('sf4', 'launch-brief.md', '', 'userFiles', 'text/markdown', 3_000, 7, { agentSummary: 'Brief: 30 second teaser, highlight the new report view, end on the logo.' }),
  sfile('sf5', 'package.json', 'product-app', 'userFiles', 'application/json', 2_000, 6),
  sfile('sf6', 'README.md', 'product-app', 'userFiles', 'text/markdown', 4_000, 6),
  sfile('sf7', 'ReportView.tsx', 'product-app/src', 'userFiles', 'text/plain', 5_000, 6),
  sfile('sf8', 'Teaser-Final.mp4', 'renders', 'outputFiles', 'video/mp4', 30_000_000, 3),
];
export const springOutputs = [{
  stepResultId: 'sr-spring-1', workflowExecutionId: 'e-spring-1', workflowStepId: 's6', storageKey: `outputs/${SPRING}/Teaser-Final.mp4`, fileName: 'Teaser-Final.mp4', producedAt: ago(3),
  workflowDefinitionId: 'w-spring', workflowName: 'Promo teaser', runNumber: 1, runStartedAt: ago(20), executionStatus: 'Passed', stepLabel: 'Render final video',
  stepType: 'TimelineRender', iterationNumber: 1, durationSec: 30, width: 1920, height: 1080, isFinal: true, warnings: null, takeGroupKey: 'spring', takeIndex: 1, takeCount: 1, isLatestTake: true,
  isIntermediate: false, isPinned: false,
}];

// ================================================================ the first run: one finished run with "Request changes"
export const revisionHistory = {
  workspaceId: 'ws-1', availability: 'Snapshot', canRevise: true, unavailableReason: null, currentVersionId: 'cv-2',
  scenes: [
    { name: 'Opening', fromFrame: 0, durationInFrames: 150, description: 'Title over the glacier' },
    { name: 'Stats', fromFrame: 150, durationInFrames: 240, description: 'Numbers for the season' },
    { name: 'Closing', fromFrame: 390, durationInFrames: 150, description: 'Logo and call to action' },
  ],
  versions: [
    { id: 'cv-1', versionNumber: 1, parentVersionId: null, parentVersionNumber: null, createdAt: ago(3 * HOUR), instruction: null, source: 'RenderCapture', executionId: DONE_EXEC, workflowId: W, renderStepResultId: 'sr-out-1', hasRender: true, compositionId: 'main', durationInFrames: 540, fps: 30, width: 1920, height: 1080, sceneCount: 3 },
    { id: 'cv-2', versionNumber: 2, parentVersionId: 'cv-1', parentVersionNumber: 1, createdAt: ago(100), instruction: 'Make the numbers in the stats section bigger', source: 'RenderCapture', executionId: DONE_EXEC, workflowId: W, renderStepResultId: 'sr-out-1', hasRender: true, compositionId: 'main', durationInFrames: 540, fps: 30, width: 1920, height: 1080, sceneCount: 3 },
  ],
  revisions: [],
  estimate: { billingEnabled: true, lowCredits: 30, highCredits: 60, requiredCredits: 80, balanceCredits: 2400, affordable: true },
  replay: null,
};

// ================================================================ billing: a workspace on the free Trial plan
export const trialSummary = {
  ...billingSummary,
  subscription: { planKey: 'trial', planName: 'Trial', status: 'active', seats: 1, periodStart: null, periodEnd: null, cancelAtPeriodEnd: false, cancelEffectiveAt: null, scheduledChange: null, hasBillingCustomer: false, promoDiscount: null },
  credits: { balance: 300, reserved: 0, available: 300, grants: [{ source: 'trial', remaining: 300, expiresAt: inDays(14) }] },
  plans: billingPlans.map((p) => ({ ...p, current: false })),
  topUp: { available: false, packs: [] },
  assistantAllowance: { includedCredits: 100, usedCredits: 0, periodStart, periodEnd: inDays(14) },
  entitlements: { maxOutputHeight: 720 },
};
// "By what was consumed": what each kind of work used, round numbers.
export const usageByKind = [
  ['RenderOutputSeconds', 'Rendered seconds', 400, 60, [{ kind: 'RenderOutputSeconds', quantity: 1800 }]],
  ['LlmInputTokens', 'Input tokens', 320, 150, [{ kind: 'LlmInputTokens', quantity: 2_400_000 }]],
  ['LlmOutputTokens', 'Output tokens', 280, 150, [{ kind: 'LlmOutputTokens', quantity: 180_000 }]],
  ['AsrAudioSeconds', 'Transcribed seconds', 200, 20, [{ kind: 'AsrAudioSeconds', quantity: 3600 }]],
  ['TtsInputBytes', 'Speech bytes', 120, 12, [{ kind: 'TtsInputBytes', quantity: 48_000 }]],
  ['AnalyzeMediaSeconds', 'Analysed media seconds', 80, 8, [{ kind: 'AnalyzeMediaSeconds', quantity: 1200 }]],
].map(([key, label, credits, events, units]) => ({ key, label, credits, events, costUsd: null, units }));

// ================================================================ runners
export const pairing = { userCode: 'BRK7-QX4M', name: 'Edit bay mini', os: 'linux', arch: 'x64', version: '0.9.4', requestedFromIp: '203.0.113.24', requestedAt: ago(2), expiresAt: inDays(0.006) };
export const oneRunner = [runners[0]];

// ================================================================ editor: hub and one open timeline
// Clips of the "Ep12 rough cut" timeline; ids are GUID-shaped because the editor's validator says so.
export const TL = '7e1a2b3c-4d5e-4f60-8a1b-2c3d4e5f6a70';
const fid = (n) => `c1a0${String(n).padStart(4, '0')}-0000-4000-8000-00000000000${n}`;
export const timelineFiles = [
  { n: 1, name: 'LOF_0101_glacier-drone.mov', kind: 'video', mime: 'video/quicktime', size: 1_800_000_000, dur: 30 },
  { n: 2, name: 'LOF_0102_ridgeline-runner.mov', kind: 'video', mime: 'video/quicktime', size: 2_300_000_000, dur: 45 },
  { n: 3, name: 'LOF_0104_summit-wide.mp4', kind: 'video', mime: 'video/mp4', size: 1_100_000_000, dur: 20 },
  { n: 4, name: 'aurora-theme.mp3', kind: 'audio', mime: 'audio/mpeg', size: 8_000_000, dur: 120 },
].map((f) => ({ ...f, id: fid(f.n) }));
export const mediaPool = timelineFiles.map((f) => ({
  mediaRef: `file:${f.id}`, kind: f.kind, fileName: f.name, mimeType: f.mime, sizeBytes: f.size, category: 'userFiles',
  probe: { durationSec: f.dur, fpsNum: f.kind === 'video' ? 30 : null, fpsDen: f.kind === 'video' ? 1 : null, width: f.kind === 'video' ? 1920 : null, height: f.kind === 'video' ? 1080 : null,
    videoCodec: f.kind === 'video' ? 'h264' : null, audioCodec: f.kind === 'video' ? 'aac' : 'mp3', container: f.kind === 'video' ? 'mov,mp4' : 'mp3', audioChannels: 2, audioSampleRateHz: 48000, bitrateBps: 8_000_000, hasAudio: true },
  bins: [], filmstripUrl: `/api/v1/projects/${P}/media/file:${f.id}/filmstrip`,
}));
const clip = (n, name, fileN, recordIn, recordOut, sourceIn = 0) => ({
  id: `aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeee0${n}`, type: 'mediaClip', name, recordIn, recordOut, assetRef: { kind: 'projectFile', projectFileId: fid(fileN) },
  sourceIn, playbackRate: 1, enabled: true, provenance: null,
});
const track = (n, kind, name, items) => ({ id: `11111111-2222-4333-8444-5555555555${n}`, kind, name, color: null, height: 64, locked: false, enabled: true, mute: false, solo: false, busId: null, items });
export const timelineDoc = {
  schemaVersion: 2, id: TL, projectId: P, name: 'Ep12 rough cut',
  settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
  tracks: [
    track('01', 'video', 'V1', [clip(1, 'Glacier drone', 1, 0, 300), clip(2, 'Ridgeline runner', 2, 300, 750, 60), clip(3, 'Summit wide', 3, 750, 1050)]),
    track('02', 'audio', 'A1', [clip(4, 'Aurora theme', 4, 0, 1050)]),
  ],
  markers: [], inOut: null, seed: null,
};
export const timelineSummary = { id: TL, name: 'Ep12 rough cut', revision: 4, seedStepResultId: null, createdAt: ago(2 * DAY), updatedAt: ago(50) };
export const timelineRenders = [
  { stepResultId: 'sr-out-1', workflowExecutionId: DONE_EXEC, workflowStepId: 's6', timelineStorageKey: 'x', outputVideoStorageKey: 'y', fileName: 'Ep12-Ridgeline-Sunrise.mp4', durationSec: 360, createdAt: ago(100) },
  { stepResultId: 'sr-out-2', workflowExecutionId: DONE_EXEC, workflowStepId: 's6', timelineStorageKey: 'x', outputVideoStorageKey: 'y', fileName: 'Ep11-Glacier-Pass.mp4', durationSec: 300, createdAt: ago(7 * DAY) },
];

// ================================================================ pictures
export const pictureDocs = [
  { id: 'pd-1', name: 'Episode 12 thumbnail', sourceProjectFileId: null, revision: 6, layerCount: 2, createdAt: ago(3 * DAY), updatedAt: ago(2 * HOUR) },
  { id: 'pd-2', name: 'Harbor Coffee poster', sourceProjectFileId: null, revision: 3, layerCount: 3, createdAt: ago(9 * DAY), updatedAt: ago(5 * DAY) },
];
const layer = (id, z, role, kind, label, extra = {}) => ({
  id, zOrder: z, role, kind, label, sourceProjectFileId: null, imageUrl: `/api/v1/projects/${P}/image-documents/pd-1/layers/${id}/image`, matteUrl: null, hasMatte: false,
  mimeType: 'image/png', width: 1280, height: 720, opacity: 1, transform: { x: 0, y: 0, scale: 1, rotation: 0 }, segmentationModel: null, segmentationPrompt: null, createdAt: ago(3 * DAY),
  visible: true, locked: false, blendMode: 'normal', content: null, provenance: null, crop: null, ...extra,
});
export const pictureDoc = {
  id: 'pd-1', projectId: P, name: 'Episode 12 thumbnail', sourceProjectFileId: null, revision: 6, createdAt: ago(3 * DAY), updatedAt: ago(2 * HOUR),
  canvasWidth: 1280, canvasHeight: 720, backgroundColor: null,
  layers: [
    layer('pl-1', 0, 'background', 'image', 'Sunrise ridge'),
    layer('pl-2', 1, 'overlay', 'text', 'Title: Episode 12', {
      imageUrl: '', mimeType: 'text/plain', width: null, height: null, transform: { x: 120, y: 420, scale: 1, rotation: 0 },
      content: { text: 'RIDGELINE SUNRISE', fontFamily: 'Inter', fontWeight: 800, fontSize: 96, lineHeight: 1.1, letterSpacing: 0, align: 'left', color: '#ffffff', stroke: { color: '#0f172a', width: 6 }, shadow: null, maxWidth: 900, uppercase: false },
    }),
  ],
};

// What the media pipeline has made of each timeline source: a small copy (proxy) with its length. The editor reads the
// source length from here, and "Preview not ready" / "Length not checked" appear when it is missing.
export const derivativesOf = (mediaRef) => {
  const f = timelineFiles.find((x) => `file:${x.id}` === mediaRef);
  if (!f) return [];
  return [{ id: `dv-${f.n}`, kind: 'proxy', mimeType: 'video/mp4', sizeBytes: 8_000, durationSec: f.dur, width: f.kind === 'video' ? 640 : null, height: f.kind === 'video' ? 360 : null,
    contentUrl: `/api/v1/projects/${P}/media/derivatives/dv-${f.n}/content` }];
};

// A text-only app (no footage to pick), used where a shot needs a run form with nothing left to fix.
export const PROMO = 'w-promo';
export const promoWorkflow = { id: PROMO, name: 'Promo from a brief', createdAt: ago(5 * DAY), updatedAt: ago(5 * DAY), steps: wfSteps.slice(0, 3).map((st, i) => ({ ...st, id: `pr${i}`, stepOrder: i + 1 })), requiresUserInput: true };
export const promoManifest = {
  workflowId: PROMO, name: 'Promo from a brief', hasExplicitManifest: true, costClass: 'Uses credits', source: 'custom',
  manifest: {
    version: 1, title: 'Promo from a brief', tagline: 'A short promo video from one paragraph about your product.', category: 'promo', suggestedStylePreset: 'documentary',
    inputs: [{ kind: 'notes', label: 'What should the video be about?' }],
    result: { kind: 'video' }, advanced: { allowGraph: true },
  },
};
