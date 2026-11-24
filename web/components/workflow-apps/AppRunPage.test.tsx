// @vitest-environment jsdom
/**
 * The app run page (spec 4.3 to 4.6). Each row of the "States the page must specify" table is a
 * test below, next to the readiness strip's semantics, the file slots, the price line and the phone
 * layout. Network, billing, assistant and routing are mocked; the form hook, the manifest rendering
 * and the readiness logic are the real ones.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import { enT } from '@/lib/i18n-default';
import { VideoEditorFlagProvider } from '@/components/shell/VideoEditorFlag';
import type {
  AppManifest,
  WorkflowAppResponse,
  WorkflowDefinition,
  WorkflowExecution,
  WorkflowLintResponse,
} from '@/lib/types/workflow';

const env = vi.hoisted(() => ({
  push: vi.fn(),
  notify: vi.fn(),
  executeWorkflow: vi.fn(),
  lintWorkflow: vi.fn(),
  retryExecution: vi.fn(),
  resumeExecution: vi.fn(),
  stopExecution: vi.fn(),
  uploadFile: vi.fn(),
  openWithPrompt: vi.fn(),
  mutateExecutions: vi.fn(),
  mutateEstimate: vi.fn(),
  mutateFiles: vi.fn(),
  markOnboarding: vi.fn(),
  resendVerification: vi.fn(),
  applyWorkflowTemplate: vi.fn(),
  estimateCalls: [] as Array<string | undefined>,
  briefEstimateCalls: [] as Array<unknown>,
  speechCalls: 0,
  phone: false,
  search: '',
  intercept: vi.fn(() => false) as (err: unknown, resend: (n: number) => unknown) => boolean,
  state: {} as Record<string, unknown>,
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: env.push, replace: vi.fn() }), useSearchParams: () => new URLSearchParams(env.search) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: env.notify } }));
// `emailVerified` is absent by default (an older session): only an explicit `false` warns.
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' }, emailVerified: env.state.emailVerified }) }));
// The welcome answers and the account's onboarding state: none by default (an API that sent nothing).
vi.mock('@/lib/hooks/use-onboarding', () => ({
  useOnboarding: () => ({ profile: null, server: null, mirror: {}, settled: true, userKey: 'u-ada', mark: env.markOnboarding, ...(env.state.onboarding as object | undefined) }),
}));
vi.mock('@/lib/api/auth', () => ({ resendVerification: env.resendVerification }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p1', name: 'Spring launch' } }) }));
vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({
    data: [
      { id: 'ag-script', agentType: 'Scriptwriter' },
      { id: 'ag-edit', agentType: 'VideoStoryEditor' },
    ],
  }),
}));
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({
    data: [{ key: 'corporate-explainer', name: 'Corporate explainer', description: '', config: {}, suggestedFormat: 'Landscape' },
      { key: 'tiktok-reel', name: 'Fast social reel', description: '', config: {}, suggestedFormat: 'Vertical' },
      { key: 'dynamic-edit', name: 'Dynamic edit', description: '', config: { dynamics: 'Fast' }, suggestedFormat: 'Vertical' }],
  }),
  useEditingStyles: () => ({ data: [] }),
}));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflow: () => ({ data: env.state.workflow }) }));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({
  useWorkflowApp: () => ({ data: env.state.app, error: env.state.appError }),
}));
vi.mock('@/lib/hooks/use-executions', () => ({
  useExecutions: () => ({ data: env.state.executions, mutate: env.mutateExecutions }),
}));
vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: env.state.files, mutate: env.mutateFiles }),
}));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: env.state.pool }) }));
vi.mock('@/lib/hooks/use-voices', () => ({ useProjectVoices: () => ({ data: env.state.voices }) }));
// The avatars answer only when the operator switch is on: `avatarsOff` plays the 404.
vi.mock('@/lib/hooks/use-avatars', () => ({
  useAvatarRequirements: (projectId: string | null) => (projectId && !env.state.avatarsOff
    ? { data: { entitled: true }, error: undefined }
    : { data: undefined, error: projectId ? new Error('404') : undefined }),
  useProjectAvatars: (_p: string, enabled: boolean) => ({ data: enabled ? env.state.avatars : undefined }),
}));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => ({ data: env.state.outputs }) }));
vi.mock('@/lib/hooks/use-billing', () => ({
  useBillingSummary: () => ({ data: env.state.summary }),
  useOutputHeightCap: () => null,
  useWorkflowCostEstimate: (p: string | undefined, _w: string, resolution?: string) => {
    if (p) env.estimateCalls.push(resolution);
    return { data: env.state.estimate, error: env.state.estimateError, mutate: env.mutateEstimate };
  },
  useWorkflowCostEstimateWithBrief: (_p: string, _w: string, body: unknown) => {
    if (body) env.briefEstimateCalls.push(body);
    return { data: env.state.estimate, error: env.state.estimateError, mutate: env.mutateEstimate };
  },
}));
vi.mock('@/lib/api/inference-providers', () => ({
  getProviderStatus: () => { env.speechCalls += 1; return Promise.resolve(env.state.speech); },
}));
vi.mock('@/lib/api/workflows', () => ({
  executeWorkflow: env.executeWorkflow,
  lintWorkflow: env.lintWorkflow,
  applyWorkflowTemplate: env.applyWorkflowTemplate,
}));
vi.mock('@/lib/api/executions', () => ({
  retryExecution: env.retryExecution,
  resumeExecution: env.resumeExecution,
  stopExecution: env.stopExecution,
}));
vi.mock('@/lib/api/files', async (orig) => ({ ...(await orig<typeof import('@/lib/api/files')>()), uploadFile: env.uploadFile }));
const fetchedKeys = vi.hoisted(() => [] as string[]);
vi.mock('@/lib/api/client', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/client')>()),
  fetcher: (key: string) => {
    fetchedKeys.push(key);
    if (key.includes('/app/inputs/')) return Promise.resolve(env.state.modelOptions);
    // The person's workspaces: one (their own), unless a test says otherwise.
    if (key === '/api/v1/orgs') return Promise.resolve([{ id: 'o1', kind: 'Personal' }]);
    return Promise.resolve(env.state.detail ?? { key });
  },
}));
vi.mock('@/lib/telemetry', async (orig) => ({ ...(await orig<typeof import('@/lib/telemetry')>()), emitFunnelEvent: vi.fn() }));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistant: () => ({ openWithPrompt: env.openWithPrompt }) }));
vi.mock('@/components/assistant/AssistantPageContext', () => ({ AssistantPageContext: () => null }));
vi.mock('@/components/workflows/ExecutionHistory', () => ({ ExecutionHistory: () => <div>History</div> }));
vi.mock('@/components/billing/RunCostEstimate', () => ({ RunCostEstimate: () => <div>Breakdown</div> }));
vi.mock('@/components/design-system/EstimateGateModal', () => ({ useEstimateGate: () => ({ intercept: env.intercept, modal: null }) }));

import { AppRunPage } from './AppRunPage';
import { emitFunnelEvent } from '@/lib/telemetry';

const MANIFEST: AppManifest = {
  version: 1,
  title: 'Explainer video',
  tagline: 'A narrated walkthrough of your product.',
  category: 'promo',
  suggestedStylePreset: 'corporate-explainer',
  inputs: [
    { kind: 'brief', field: 'language', label: 'Language', default: 'auto' },
    { kind: 'brief', field: 'lengthSec', label: 'Length', default: 60, choices: [30, 60, 90] },
    { kind: 'brief', field: 'format', label: 'Shape' },
    { kind: 'brief', field: 'resolution', label: 'Resolution', more: false },
    { kind: 'brief', field: 'voice', label: 'Narration' },
    { kind: 'brief', field: 'stylePreset', label: 'Style', more: false },
    { kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 20 },
    { kind: 'notes', label: 'Anything else?' },
  ],
  result: { kind: 'video' },
  advanced: { allowGraph: true },
};

function appResponse(manifest: AppManifest = MANIFEST): WorkflowAppResponse {
  return { workflowId: 'w1', name: 'Explainer', manifest, hasExplicitManifest: true, costClass: 'Uses credits and a voice', source: 'promo' };
}

const OVERLAY_MANIFEST: AppManifest = {
  ...MANIFEST,
  // The real footage app carries at most eight inputs (AppManifestVocabulary.MaxInputs): the
  // optional picture slot and the card-audio select take the place of resolution and narration.
  inputs: [
    { kind: 'brief', field: 'language', label: 'Language', default: 'auto' },
    { kind: 'brief', field: 'lengthSec', label: 'Length', default: 60, choices: [30, 60, 90] },
    { kind: 'brief', field: 'format', label: 'Shape' },
    { kind: 'brief', field: 'stylePreset', label: 'Style' },
    { kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 20 },
    { kind: 'files', role: 'overlay', label: 'A picture or video to show', min: 0, max: 4 },
    { kind: 'brief', field: 'overlayCardAudio', label: 'Under a full-screen picture', default: 'agent', choices: ['agent', 'keep', 'mute'], more: false },
    { kind: 'notes', label: 'Anything else?' },
  ],
};

const OVERLAY_OUTPUT = {
  mediaOverlays: {
    enabled: true,
    applied: true,
    appliedCount: 1,
    offered: [{ mediaId: 'm0', name: 'testimonial.png' }],
    unusedOffered: [{ mediaId: 'm1', name: 'logo.png' }],
    dropped: [{ mediaId: 'm3', windowId: 'o9', reason: 'window_cut_away' }],
    items: [{ media: 'testimonial.png', form: 'Card', startSec: 1.2, endSec: 6.4, opacityPercent: 100, cardAudio: 'Keep' }],
  },
};

function overlayRun(id: string, node: unknown): WorkflowExecution {
  return run({
    id,
    status: 'Passed',
    stepResults: [{
      id: 'r9', workflowStepId: 's3', status: 'Completed', stepType: 'VideoCompile',
      output: '', outputJson: JSON.stringify(node), tokensUsed: 0, durationMs: 0, executedAt: '2026-10-25T10:05:00Z',
    }] as never,
  });
}

const WORKFLOW = {
  id: 'w1',
  name: 'Explainer',
  requiresUserInput: false,
  steps: [
    { id: 's1', agentDefinitionId: 'ag-script', stepOrder: 1, label: '', stepType: 'Agent' },
    { id: 's2', agentDefinitionId: 'ag-edit', stepOrder: 2, label: '', stepType: 'Voiceover' },
    { id: 's3', agentDefinitionId: 'ag-edit', stepOrder: 3, label: '', stepType: 'VideoCompile' },
  ],
} as unknown as WorkflowDefinition;

const FILES = [
  { id: 'f1', originalFileName: 'intro.mp4', mimeType: 'video/mp4' },
  { id: 'f2', originalFileName: 'demo.mp4', mimeType: 'video/mp4' },
  { id: 'f3', originalFileName: 'notes.txt', mimeType: 'text/plain' },
];

const ESTIMATE = {
  billingEnabled: true,
  hasHistory: true,
  lowCredits: 30,
  highCredits: 45,
  typicalCredits: 40,
  requiredCredits: 45,
  balanceCredits: 500,
  affordable: true,
  steps: [],
};

const CLEAR: WorkflowLintResponse = { findings: [], errors: 0, warnings: 0, infos: 0 };

function run(overrides: Partial<WorkflowExecution> & { id: string; status: WorkflowExecution['status'] }): WorkflowExecution {
  return {
    workflowDefinitionId: 'w1',
    startedAt: '2026-10-25T10:00:00Z',
    completedAt: null,
    iterationCount: 0,
    resultJson: null,
    stepResults: [],
    reviewScores: [],
    ...overrides,
  } as WorkflowExecution;
}

function mountPage(opts: { editor?: boolean } = {}) {
  return render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <VideoEditorFlagProvider enabled={opts.editor ?? false}>
          <AppRunPage projectId="p1" workflowId="w1" />
        </VideoEditorFlagProvider>
      </SWRConfig>
    </MantineProvider>,
  );
}

const runButton = () => screen.getByTestId('run-button');
const isBlocked = () => runButton().getAttribute('aria-disabled') === 'true';

beforeAll(() => {
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});

beforeEach(() => {
  env.phone = false;
  env.search = '';
  global.matchMedia = ((q: string) => ({
    matches: env.phone && q.includes('max-width'),
    media: q,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  for (const fn of [env.push, env.notify, env.executeWorkflow, env.lintWorkflow, env.retryExecution, env.resumeExecution, env.stopExecution, env.uploadFile, env.openWithPrompt, env.mutateExecutions, env.mutateEstimate, env.mutateFiles, env.markOnboarding, env.resendVerification, env.applyWorkflowTemplate]) fn.mockReset();
  window.localStorage.clear();
  env.estimateCalls.length = 0;
  env.briefEstimateCalls.length = 0;
  env.speechCalls = 0;
  env.intercept = vi.fn(() => false);
  env.lintWorkflow.mockResolvedValue(CLEAR);
  env.executeWorkflow.mockResolvedValue(run({ id: 'e-new', status: 'Queued' }));
  env.state = {
    workflow: WORKFLOW,
    app: appResponse(),
    appError: undefined,
    executions: [],
    files: FILES,
    voices: [],
    outputs: [],
    estimate: ESTIMATE,
    summary: undefined as Record<string, unknown> | undefined,
    estimateError: undefined,
    speech: { providerName: 'Voices Inc', healthy: true, usingFallback: false },
    detail: undefined,
    emailVerified: undefined as boolean | undefined,
    onboarding: undefined as Record<string, unknown> | undefined,
  };
});
afterEach(cleanup);

describe('AppRunPage inputs', () => {
  it('renders the inputs from the manifest in order, with the existing controls', async () => {
    mountPage();
    const group = screen.getByRole('group', { name: 'Choices' });
    const text = group.textContent ?? '';
    expect(text.indexOf('Language')).toBeLessThan(text.indexOf('Length'));
    expect(text.indexOf('Length')).toBeLessThan(text.indexOf('Video shape'));
    expect(text.indexOf('Narration')).toBeLessThan(text.indexOf('Editing style'));
    expect(text.indexOf('Your clips')).toBeLessThan(text.indexOf('Anything else?'));
    expect(within(group).getByRole('radio', { name: '60s' }).hasAttribute('checked') || (within(group).getByRole('radio', { name: '60s' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText('A narrated walkthrough of your product.')).toBeTruthy();
    // The suggested style is a pre-selection, never forced.
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Editing style' }) as HTMLInputElement).value).toBe('Corporate explainer'));
  });

  it('sends the page and the form as one brief when Run is pressed, with no legacy members', async () => {
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('radio', { name: '90s' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Anything else?' }), { target: { value: 'keep the intro short' } });
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    const [project, workflow, userRequest, regenerate, choices, accept, briefFile, brief] = env.executeWorkflow.mock.calls[0]!;
    expect([project, workflow, userRequest, regenerate, choices, accept, briefFile]).toEqual(['p1', 'w1', null, false, {}, undefined, undefined]);
    expect(brief).toMatchObject({
      lengthSec: 90,
      stylePreset: 'corporate-explainer',
      notes: 'keep the intro short',
      voice: { mode: 'Narrated' },
      sourceFiles: [{ role: 'Video', fileIds: ['f1', 'f2'] }],
    });
    await waitFor(() => expect(env.mutateExecutions).toHaveBeenCalled());
  });

  it('?sample=<id> preselects only the sample clip, not every video', async () => {
    env.search = 'view=app&sample=f2';
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ sourceFiles: [{ role: 'Video', fileIds: ['f2'] }] });
  });

  it('ignores a sample id that is not in the project', async () => {
    env.search = 'sample=nope';
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ sourceFiles: [{ role: 'Video', fileIds: ['f1', 'f2'] }] });
  });
});

describe('design input', () => {
  it('sends designSystem none when the person runs without the brand design', async () => {
    env.state.app = appResponse({
      ...MANIFEST,
      inputs: [{ kind: 'brief', field: 'designSystem', label: 'Design', more: false }, { kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 20 }],
    });
    mountPage();
    fireEvent.click(screen.getByRole('combobox', { name: 'Design' }));
    fireEvent.click(await screen.findByRole('option', { name: 'No brand design' }));
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ designSystem: 'none' });
  });
});

const AVATAR_MANIFEST: AppManifest = {
  ...MANIFEST,
  inputs: [{ kind: 'brief', field: 'format', label: 'Shape' }, { kind: 'brief', field: 'avatar', label: 'Avatar' }],
};

const AVATAR = (id: string, name: string, selectable = true) => ({
  id, name, selectable, unselectableReason: selectable ? null : 'consent_revoked', retentionUntil: '2099-01-01T00:00:00Z', references: [],
});

describe('a workflow that needs a description', () => {
  it('does not block Run for a description when the page has no notes box (the fields carry it)', async () => {
    // The avatar clip: requiresUserInput is set on the workflow, but its own required fields are the brief and the
    // manifest has no "Anything else?" box, so there is nothing the person could fill in to clear the block.
    env.state.workflow = { ...WORKFLOW, requiresUserInput: true } as unknown as WorkflowDefinition;
    env.state.app = appResponse(AVATAR_MANIFEST);
    env.state.avatars = [AVATAR('a1', 'Anna')];
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(screen.queryByText(/Describe what you want in the box above/)).toBeNull();
    expect(isBlocked()).toBe(false);
  });

  it('still blocks Run on an empty notes box when the page has one', async () => {
    env.state.workflow = { ...WORKFLOW, requiresUserInput: true } as unknown as WorkflowDefinition;
    env.state.app = appResponse(OVERLAY_MANIFEST);
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(isBlocked()).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /Anything else\?/ }), { target: { value: 'a short film' } });
    await waitFor(() => expect(isBlocked()).toBe(false));
  });
});

describe('avatar input', () => {
  it('lets the person pick a usable avatar and sends it as brief.avatar', async () => {
    env.state.app = appResponse(AVATAR_MANIFEST);
    env.state.avatars = [AVATAR('a1', 'Anna'), AVATAR('a2', 'Marco', false)];
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('combobox', { name: 'Avatar' }));
    expect((await screen.findByRole('option', { name: /Marco/, hidden: true })).getAttribute('data-combobox-disabled')).toBe('true');
    // The strip's first answer can close the list as it lands, so the option is clicked wherever it is.
    fireEvent.click(await screen.findByRole('option', { name: /Anna/, hidden: true }));
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Avatar' }) as HTMLInputElement).value).toMatch(/Anna/));
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ avatar: { avatarId: 'a1' } });
  });

  it('notes, without blocking, that no avatar is chosen', async () => {
    env.state.app = appResponse(AVATAR_MANIFEST);
    env.state.avatars = [AVATAR('a1', 'Anna')];
    mountPage();
    expect(await screen.findByText(/No avatar chosen/)).toBeTruthy();
    expect(isBlocked()).toBe(false);
  });

  it('is hidden, and sends nothing, when the avatars API answers 404', async () => {
    env.state.app = appResponse(AVATAR_MANIFEST);
    env.state.avatarsOff = true;
    mountPage();
    expect(screen.queryByRole('combobox', { name: 'Avatar' })).toBeNull();
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).not.toHaveProperty('avatar');
  });
});

describe('AppRunPage states table', () => {
  it('first visit, no run: the result pane says what will appear and what the app needs; Run is enabled', async () => {
    mountPage();
    const result = screen.getByRole('region', { name: 'Result' });
    expect(within(result).getByText('Your video will appear here')).toBeTruthy();
    expect(within(result).getByText(/This app needs: Your clips/)).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId('readiness-strip').getAttribute('data-state')).toBe('ready'));
    expect(isBlocked()).toBe(false);
  });

  it('an empty file slot disables Run with the reason, keeps it in the tab order and offers the upload', async () => {
    env.state.files = [{ id: 'f3', originalFileName: 'notes.txt', mimeType: 'text/plain' }];
    mountPage();
    expect(isBlocked()).toBe(true);
    const button = runButton();
    expect(button.hasAttribute('disabled')).toBe(false);
    expect(button.getAttribute('tabindex')).not.toBe('-1');
    const reason = document.getElementById(button.getAttribute('aria-describedby')!.split(' ')[0]!);
    expect(reason?.textContent).toContain('Add at least one video clip');
    expect(within(screen.getByTestId('readiness-strip')).getByRole('button', { name: 'Add a clip' })).toBeTruthy();
    fireEvent.click(button);
    expect(env.executeWorkflow).not.toHaveBeenCalled();
  });

  it('uploads a clip in one click and selects it', async () => {
    env.state.files = [];
    env.uploadFile.mockResolvedValue({ id: 'new1', originalFileName: 'a.mp4', mimeType: 'video/mp4' });
    mountPage();
    const input = screen.getByTestId('upload-video') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(['x'], 'a.mp4', { type: 'video/mp4' })] } });
    await waitFor(() => expect(env.uploadFile).toHaveBeenCalledWith('p1', expect.any(File)));
    await waitFor(() => expect(env.mutateFiles).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByText(/^1 chosen/)).toBeTruthy());
  });

  it('no voice service: narration needs a voice, Run is disabled with the reason', async () => {
    env.state.speech = { providerName: null, healthy: false, usingFallback: false };
    mountPage();
    await waitFor(() => expect(isBlocked()).toBe(true));
    expect(screen.getAllByText('No voice is set up yet. Ask your workspace admin to add one.').length).toBeGreaterThan(0);
    // Turning narration off clears the blocker.
    fireEvent.click(screen.getByRole('switch'));
    await waitFor(() => expect(isBlocked()).toBe(false));
  });

  it('a usable Library project voice: no "narration needs a voice" warning, Run stays enabled', async () => {
    // A Library voice the project can use makes the default speech service a server-side detail.
    env.state.speech = { providerName: null, healthy: false, usingFallback: false };
    env.state.voices = [{ id: 'v1', name: 'Aria', selectable: true, source: 'Library', remoteVoiceId: 'remote-aria', inferenceProviderId: 'provider-1' }];
    mountPage();
    // The status request landing is what makes the absence meaningful.
    await waitFor(() => expect(env.speechCalls).toBeGreaterThan(0));
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    expect(screen.queryByText('No voice is set up yet. Ask your workspace admin to add one.')).toBeNull();
    await waitFor(() => expect(screen.getByTestId('readiness-strip').getAttribute('data-state')).toBe('ready'));
    expect(isBlocked()).toBe(false);
  });

  it('a selectable voice with a blank remote id still needs a voice: the notice shows', async () => {
    // Selectable alone is not usable: the engine also needs a remote voice id on the wire.
    env.state.speech = { providerName: null, healthy: false, usingFallback: false };
    env.state.voices = [{ id: 'v1', name: 'Aria', selectable: true, source: 'Library', remoteVoiceId: '   ', inferenceProviderId: 'provider-1' }];
    mountPage();
    await waitFor(() => expect(env.speechCalls).toBeGreaterThan(0));
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    expect(screen.getAllByText('No voice is set up yet. Ask your workspace admin to add one.').length).toBeGreaterThan(0);
    await waitFor(() => expect(isBlocked()).toBe(true));
  });

  it('a selectable clone whose provider was deleted still needs a voice: the notice shows', async () => {
    env.state.speech = { providerName: null, healthy: false, usingFallback: false };
    env.state.voices = [{ id: 'v1', name: 'Aria', selectable: true, source: 'Cloned', remoteVoiceId: 'remote-aria', inferenceProviderId: null }];
    mountPage();
    await waitFor(() => expect(env.speechCalls).toBeGreaterThan(0));
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    expect(screen.getAllByText('No voice is set up yet. Ask your workspace admin to add one.').length).toBeGreaterThan(0);
  });

  it('a chosen voice without consent disables Run and says so', async () => {
    env.state.voices = [
      { id: 'v1', name: 'Aria', selectable: true, source: 'Library', remoteVoiceId: 'remote-aria', inferenceProviderId: 'provider-1' },
      { id: 'v2', name: 'Marco', selectable: false, source: 'Cloned', remoteVoiceId: 'remote-marco', inferenceProviderId: 'provider-1' },
    ];
    // Prefilled from a previous run that used the voice.
    env.state.executions = [run({ id: 'e0', status: 'Passed', brief: { voice: { mode: 'Narrated', projectVoiceId: 'v2' } } })];
    env.state.outputs = [{ stepResultId: 'sr0', workflowExecutionId: 'e0', fileName: 'x.mp4', producedAt: '2026-10-25T10:05:00Z', isFinal: true }];
    mountPage();
    fireEvent.click(screen.getByRole('button', { name: 'Change one thing and re-run' }));
    await waitFor(() => expect(isBlocked()).toBe(true));
    expect(screen.getAllByText('You need consent on file for this voice').length).toBeGreaterThan(0);
  });

  it('a plan refusal shows the plan sentence, preselects the allowed alternative and disables Run until a choice changes', async () => {
    env.state.summary = { canManageBilling: true, entitlements: { maxOutputHeight: 720, unlocks: [{ feature: 'resolution', planKey: 'creator', planName: 'Creator' }] } };
    env.executeWorkflow.mockRejectedValue(new ApiError('Your plan allows up to 1080p.', 402, [], { code: 'PLAN_NOT_ENTITLED', feature: 'resolution' }));
    mountPage();
    fireEvent.click(screen.getByRole('radio', { name: '4K' }));
    fireEvent.click(runButton());
    await waitFor(() => expect(isBlocked()).toBe(true));
    expect(screen.getByTestId('run-block-reason').textContent).toContain('Your plan allows up to 1080p.');
    // The shared callout: the plan that unlocks it (from the billing summary) and the way to it.
    expect(screen.getByTestId('run-block-upgrade').textContent).toContain('Available on Creator.');
    expect(screen.getByRole('link', { name: 'Upgrade to Creator' }).getAttribute('href')).toBe('/settings/billing?plan=creator#plans');
    expect((screen.getByRole('radio', { name: 'Auto' }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Use Auto resolution' }));
    await waitFor(() => expect(isBlocked()).toBe(false));
  });

  it('a plan refusal for a member who cannot manage billing says to ask the owner, with no button', async () => {
    env.state.summary = { canManageBilling: false, entitlements: { maxOutputHeight: 720, unlocks: [] } };
    env.executeWorkflow.mockRejectedValue(new ApiError('Your plan has no video generation.', 402, [], {
      code: 'PLAN_NOT_ENTITLED', feature: 'video_generation', unlockPlan: 'pro', unlockPlanName: 'Pro',
    }));
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(isBlocked()).toBe(true));
    const callout = screen.getByTestId('run-block-upgrade');
    expect(callout.textContent).toContain('Available on Pro.');
    expect(callout.textContent).toContain('Ask the workspace owner to upgrade.');
    expect(screen.queryByTestId('run-block-upgrade-button')).toBeNull();
  });

  it('not enough credits: the shortfall sentence, Run disabled, a way to add credits', () => {
    env.state.estimate = { ...ESTIMATE, requiredCredits: 40, balanceCredits: 12, affordable: false };
    mountPage();
    expect(isBlocked()).toBe(true);
    const reason = screen.getByTestId('run-shortfall');
    expect(reason.textContent).toContain('You have 12 credits; this app needs about 40.');
    expect(within(reason).getByRole('link', { name: 'Add credits' }).getAttribute('href')).toBe('/settings/billing');
    expect(runButton().getAttribute('aria-describedby')).toContain(reason.id);
  });

  it('the shortfall shows beside another blocker and carries the balance itself', () => {
    env.state.files = [];
    env.state.estimate = { ...ESTIMATE, requiredCredits: 472.56, balanceCredits: 100, affordable: false };
    mountPage();
    expect(screen.getByTestId('run-shortfall').textContent).toContain('You have 100 credits; this app needs about 480.');
    // The sentence already says the balance, so the price line does not repeat it.
    expect(screen.queryByTestId('run-balance')).toBeNull();
    expect(isBlocked()).toBe(true);
  });

  it('affordable: no shortfall sentence, the balance still shows', () => {
    env.state.estimate = { ...ESTIMATE, balanceCredits: 900, affordable: true };
    mountPage();
    expect(screen.queryByTestId('run-shortfall')).toBeNull();
    expect(screen.getByTestId('run-balance').textContent).toBe('You have 900 credits');
  });

  it('estimate unavailable: says so, offers a retry, and Run stays available', () => {
    env.state.estimate = undefined;
    env.state.estimateError = new Error('boom');
    mountPage();
    expect(screen.getByText(/We could not work out the price just now/)).toBeTruthy();
    expect(isBlocked()).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(env.mutateEstimate).toHaveBeenCalled();
  });

  it('a recipe still being created shows a skeleton and a disabled Run', () => {
    env.state.app = undefined;
    mountPage();
    expect(screen.getByTestId('app-run-skeleton')).toBeTruthy();
    const button = screen.getByRole('button', { name: 'Run' });
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(document.getElementById(button.getAttribute('aria-describedby')!)?.textContent).toBe('Getting this app ready…');
  });

  it('while running: plain-language progress, and Stop replaces Run', async () => {
    const active = run({ id: 'e1', status: 'Running', stepResults: [{ id: 'r1', workflowStepId: 's1', status: 'Completed' }] as never });
    env.state.executions = [active];
    env.state.detail = active;
    env.stopExecution.mockResolvedValue(undefined);
    mountPage();
    expect(screen.queryByTestId('run-button')).toBeNull();
    const progress = screen.getByRole('list', { name: 'Progress' });
    expect(within(progress).getAllByRole('listitem').length).toBe(3);
    expect(progress.textContent).toContain('(Done)');
    expect(progress.textContent).toContain('(In progress)');
    // No step type or internal name leaks into the copy.
    expect(progress.textContent).not.toMatch(/VideoCompile|Voiceover|Scriptwriter/);
    fireEvent.click(screen.getByTestId('stop-button'));
    await waitFor(() => expect(env.stopExecution).toHaveBeenCalledWith('p1', 'w1', 'e1'));
  });

  it('polls the summary detail only while the latest run is in flight', async () => {
    fetchedKeys.length = 0;
    const active = run({ id: 'e1', status: 'Running', stepResults: [] as never });
    env.state.executions = [active];
    env.state.detail = active;
    mountPage();
    await waitFor(() => expect(fetchedKeys.some((k) => k.endsWith('/executions/e1?detail=summary'))).toBe(true));
    cleanup();
    fetchedKeys.length = 0;
    env.state.executions = [run({ id: 'e9', status: 'Passed' })];
    env.state.detail = undefined;
    mountPage();
    await new Promise((r) => setTimeout(r, 50));
    expect(fetchedKeys.some((k) => k.includes('/executions/e9'))).toBe(false);
  });

  it('failed on a step the plan does not include: the banner carries the upgrade callout for that feature, and none when billing is off', async () => {
    env.state.summary = { canManageBilling: true, entitlements: { maxOutputHeight: 720, unlocks: [{ feature: 'video_generation', planKey: 'pro', planName: 'Pro' }] } };
    env.state.executions = [
      run({ id: 'e2', status: 'Failed', stepResults: [{ id: 'r', workflowStepId: 's2', status: 'Failed', failureCode: 'PLAN_NOT_ENTITLED', stepType: 'VideoGenerate' }] as never }),
    ];
    mountPage();
    const callout = within(screen.getByTestId('failed-banner')).getByTestId('plan-refusal-upgrade');
    expect(callout.textContent).toContain('Generating video, animating images and lip sync');
    expect(callout.textContent).toContain('Available on Pro.');
    expect(within(callout).getByRole('link', { name: 'Upgrade to Pro' }).getAttribute('href')).toBe('/settings/billing?plan=pro#plans');
    cleanup();
    // Billing off: the summary does not exist, so there is nothing to upgrade to.
    env.state.summary = undefined;
    mountPage();
    expect(screen.queryByTestId('plan-refusal-upgrade')).toBeNull();
  });

  it('failed: a banner with the plain reason, Retry and Resume through the existing calls', async () => {
    env.state.executions = [
      run({ id: 'e2', status: 'Failed', canResume: true, stepResults: [{ id: 'r', workflowStepId: 's2', status: 'Failed', failureCode: 'VOICE_CONSENT_MISSING' }] as never }),
    ];
    env.retryExecution.mockResolvedValue(run({ id: 'e3', status: 'Queued' }));
    env.resumeExecution.mockResolvedValue(run({ id: 'e4', status: 'Queued' }));
    mountPage();
    const banner = screen.getByTestId('failed-banner');
    expect(within(banner).getByText('Failed')).toBeTruthy();
    expect(within(banner).getByText('The chosen voice has no consent on file. Choose another voice.')).toBeTruthy();
    expect(isBlocked()).toBe(false); // a new run stays available
    fireEvent.click(within(banner).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(env.retryExecution).toHaveBeenCalledWith('p1', 'w1', 'e2', false, undefined));
    fireEvent.click(within(banner).getByRole('button', { name: 'Resume' }));
    await waitFor(() => expect(env.resumeExecution).toHaveBeenCalledWith('p1', 'w1', 'e2', undefined));
    fireEvent.click(within(banner).getByRole('button', { name: 'Ask the assistant' }));
    expect(env.openWithPrompt).toHaveBeenCalledWith(expect.any(String), { projectId: 'p1', workflowId: 'w1', executionId: 'e2' });
  });

  describe('finished', () => {
    beforeEach(() => {
      env.state.executions = [
        run({ id: 'e5', status: 'Passed', completedAt: '2026-10-25T10:06:00Z', userRequest: 'short', brief: { language: 'it', lengthSec: 30, stylePreset: 'corporate-explainer', sourceFiles: [{ role: 'Video', fileIds: ['f2'] }] } }),
      ];
      env.state.outputs = [{ stepResultId: 'sr5', workflowExecutionId: 'e5', fileName: 'Explainer.mp4', producedAt: '2026-10-25T10:05:00Z', isFinal: true }];
    });

    it('leads with the video, a download, and hides the editor handoff when the editor is off', () => {
      mountPage({ editor: false });
      const result = screen.getByRole('region', { name: 'Result' });
      expect(within(result).getByText('Your video is ready')).toBeTruthy();
      expect(result.querySelector('video')?.getAttribute('src')).toBe('/api/v1/projects/p1/outputs/sr5/download');
      expect(within(result).getByRole('link', { name: 'Download' }).getAttribute('href')).toContain('saveAs=Explainer.mp4');
      expect(within(result).queryByRole('link', { name: 'Edit in the editor' })).toBeNull();
      expect(isBlocked()).toBe(false);
    });

    it('offers "Edit in the editor" only when the video editor is enabled', () => {
      mountPage({ editor: true });
      expect(screen.getByRole('link', { name: 'Edit in the editor' }).getAttribute('href')).toBe('/projects/p1/editor/sr5');
    });

    it('"Change one thing and re-run" prefills the form from the previous run, and the assistant opens with the run in context', async () => {
      mountPage();
      fireEvent.click(screen.getByRole('button', { name: 'Change one thing and re-run' }));
      await waitFor(() => expect((screen.getByRole('radio', { name: '30s' }) as HTMLInputElement).checked).toBe(true));
      expect((screen.getByRole('textbox', { name: 'Anything else?' }) as HTMLTextAreaElement).value).toBe('short');
      expect((screen.getByRole('combobox', { name: 'Language' }) as HTMLInputElement).value).toBe('Italiano');
      expect(screen.getByText(/^1 chosen/)).toBeTruthy();
      fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant to change something' }));
      expect(env.openWithPrompt).toHaveBeenCalledWith(expect.not.stringMatching(/e5/), { projectId: 'p1', workflowId: 'w1', executionId: 'e5' });
    });

    it('announces completion from the result region', () => {
      mountPage();
      const status = within(screen.getByRole('region', { name: 'Result' })).getByRole('status');
      expect(status.getAttribute('aria-live')).toBe('polite');
      expect(status.textContent).toBe('Your video is ready');
    });
  });
});

describe('picture overlays', () => {
  it('says what clip a green-screen app needs before the run, with a guide link, and only for such an app', () => {
    mountPage();
    expect(screen.queryByText(/one flat colour/)).toBeNull();
    cleanup();

    env.state.app = appResponse({
      ...MANIFEST,
      inputs: [
        { kind: 'files', role: 'video', label: 'Your clip with the green screen', min: 1, max: 1 },
        { kind: 'files', role: 'insertContent', label: 'What goes on the screen', min: 1, max: 1 },
      ],
    });
    mountPage();
    expect(screen.getByText(/one flat colour, such as green or blue/)).toBeTruthy();
    const link = screen.getByRole('link', { name: 'What counts as a screen' });
    expect(link.getAttribute('href')).toBe('/docs/recipes/tracked-screen-insert#which-clip-works');
  });

  it('renders the optional picture slot only when the manifest carries it, with a 0..4 choice that starts empty', async () => {
    mountPage();
    expect(screen.queryByText('A picture or video to show')).toBeNull();
    expect(screen.queryByTestId('upload-overlay')).toBeNull();
    cleanup();

    env.state.app = appResponse(OVERLAY_MANIFEST);
    env.state.files = [
      ...FILES,
      ...Array.from({ length: 6 }, (_, i) => ({ id: `img${i}`, originalFileName: `shot${i}.png`, mimeType: 'image/png' })),
    ];
    mountPage();
    expect(screen.getByText('A picture or video to show')).toBeTruthy();
    const upload = screen.getByTestId('upload-overlay') as HTMLInputElement;
    expect(upload.getAttribute('accept')).toBe('image/*,video/*');

    screen.getByRole('group', { name: 'A picture or video to show' });
    expect(screen.getByText('None chosen (up to 4)')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Choose a picture or video/ }));
    const dialog = await screen.findByRole('dialog');
    const boxes = within(dialog).getAllByRole('checkbox') as HTMLElement[];
    for (const box of boxes.slice(0, 5)) fireEvent.click(box);
    expect(within(dialog).getByText('4 of 4 selected')).toBeTruthy();
    expect(boxes.filter((b) => b.getAttribute('aria-checked') === 'true')).toHaveLength(4);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));
    expect(screen.getByText('4 chosen (up to 4)')).toBeTruthy();
    expect(isBlocked()).toBe(false); // the slot is optional
  });

  it('writes the card-audio choice to the brief', async () => {
    env.state.app = appResponse(OVERLAY_MANIFEST);
    mountPage();
    fireEvent.click(screen.getByRole('combobox', { name: 'Under a full-screen picture' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Mute the sound' }));
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ overlayCardAudio: 'mute' });
  });

  describe('pace control', () => {
    const PACE_MANIFEST: AppManifest = {
      ...MANIFEST,
      inputs: [...(MANIFEST.inputs ?? []).slice(0, 7), { kind: 'brief', field: 'pace', label: 'Pace' }],
    };

    it('renders only when the app has a pace input', () => {
      mountPage();
      expect(screen.queryByText('Pace')).toBeNull();
    });

    it('is Off by default and sends no pace; a chosen pace is sent with the whole-film bounds in its help', async () => {
      env.state.app = appResponse(PACE_MANIFEST);
      mountPage();
      expect(screen.getByText('Pace')).toBeTruthy();
      expect((screen.getByLabelText('Off') as HTMLInputElement).checked).toBe(true);
      fireEvent.click(runButton());
      await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
      expect(env.executeWorkflow.mock.calls[0]![7]).not.toHaveProperty('pace');
    });

    it('sends the chosen pace in the brief', async () => {
      env.state.app = appResponse(PACE_MANIFEST);
      mountPage();
      fireEvent.click(screen.getByLabelText('Medium'));
      expect(screen.getByText('Medium: at most 3 effects per 10 seconds of the whole film (9 in a 30-second film).')).toBeTruthy();
      fireEvent.click(screen.getByLabelText('Calm'));
      expect(screen.getByText('Calm: at most 1 effect per 10 seconds of the whole film (3 in a 30-second film).')).toBeTruthy();
      fireEvent.click(runButton());
      await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
      expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ pace: 'calm' });
    });

    it('follows the chosen style dynamics until the person picks one', async () => {
      env.state.app = appResponse(PACE_MANIFEST);
      mountPage();
      fireEvent.click(screen.getByRole('combobox', { name: 'Editing style' }));
      fireEvent.click(await screen.findByRole('option', { name: 'Dynamic edit' }));
      expect((screen.getByLabelText('Fast') as HTMLInputElement).checked).toBe(true);
      fireEvent.click(runButton());
      await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
      expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ pace: 'fast', stylePreset: 'dynamic-edit' });
    });
  });

  it('shows what the compile applied and the quiet Not shown list, with no ids or geometry', async () => {
    env.state.app = appResponse(OVERLAY_MANIFEST);
    env.state.executions = [run({ id: 'e5', status: 'Passed', completedAt: '2026-10-25T10:06:00Z' })];
    env.state.detail = overlayRun('e5', OVERLAY_OUTPUT);

    mountPage();
    const card = await screen.findByRole('region', { name: 'Pictures over the film' });
    expect(within(card).getByText('testimonial.png')).toBeTruthy();
    expect(within(card).getByText(/Full screen/)).toBeTruthy();
    expect(within(card).getByText('sound kept')).toBeTruthy();
    const notShown = within(card).getByRole('list', { name: 'Not shown' });
    expect(notShown.textContent).toContain('logo.png: not chosen for this edit');
    expect(notShown.textContent).toContain('A picture: the moment was cut out');
    const text = card.textContent ?? '';
    for (const leak of ['m0', 'm1', 'm3', 'o9', 'storageKey']) expect(text).not.toContain(leak);
  });

  it('adds nothing to a run whose compile wrote no overlay report', async () => {
    env.state.app = appResponse(OVERLAY_MANIFEST);
    env.state.executions = [run({ id: 'e5', status: 'Passed', completedAt: '2026-10-25T10:06:00Z' })];
    env.state.detail = run({ id: 'e5', status: 'Passed', stepResults: [] as never });

    mountPage();
    // No file is listed in this run, so the heading says it is being prepared rather than "ready".
    await waitFor(() => expect(screen.getAllByText('The video is being prepared.').length).toBeGreaterThan(0));
    expect(screen.queryByText('Your video is ready')).toBeNull();
    expect(screen.queryByTestId('media-overlays')).toBeNull();
  });
});

describe('readiness strip', () => {
  const FINDINGS: WorkflowLintResponse = {
    findings: [
      { ruleId: 'WL016', severity: 'warning', message: 'The film would be silent.', fix: { kind: 'addStep', label: 'Add a voiceover', patchId: 'addVoiceover' } },
      { ruleId: 'WL019', severity: 'info', message: 'Captions come from the narration.' },
    ],
    errors: 0,
    warnings: 1,
    infos: 1,
  };

  it('is a labelled polite status region, each state with an icon and a word', async () => {
    env.lintWorkflow.mockResolvedValue(FINDINGS);
    mountPage();
    const strip = await screen.findByRole('status', { name: 'Readiness check' });
    expect(strip.getAttribute('aria-live')).toBe('polite');
    await waitFor(() => expect(within(strip).getByText('Check:')).toBeTruthy());
    expect(within(strip).getByText('Note:')).toBeTruthy();
    expect(strip.querySelectorAll('svg').length).toBeGreaterThanOrEqual(2);
  });

  it('lets a warning through: Run stays enabled', async () => {
    env.lintWorkflow.mockResolvedValue(FINDINGS);
    mountPage();
    await screen.findByText('The film would be silent.');
    expect(isBlocked()).toBe(false);
  });

  it('an error says "Fix needed", offers its fix, and blocks Run with the reason linked', async () => {
    env.lintWorkflow.mockResolvedValue({
      findings: [{ ruleId: 'WL012', severity: 'error', message: 'The project has no approved design yet.', fix: { kind: 'approveDesignSystem', label: 'Approve the design' } }],
      errors: 1,
      warnings: 0,
      infos: 0,
    });
    mountPage();
    const strip = screen.getByTestId('readiness-strip');
    const fix = await within(strip).findByRole('button', { name: 'Approve the design' });
    expect(within(strip).getByText('Fix needed:')).toBeTruthy();
    expect(isBlocked()).toBe(true);
    expect(document.getElementById(runButton().getAttribute('aria-describedby')!.split(' ')[0]!)?.textContent).toContain('The project has no approved design yet.');
    fireEvent.click(fix);
    expect(env.push).toHaveBeenCalledWith('/projects/p1?tab=profile');
  });

  it('says plainly when the check itself could not be made, and does not block', async () => {
    env.lintWorkflow.mockRejectedValue(new Error('down'));
    mountPage();
    await screen.findByText(/We could not check this app just now/);
    expect(isBlocked()).toBe(false);
  });
});

describe('price line', () => {
  it('puts the number on the button, the range under it, and the breakdown behind a disclosure', () => {
    mountPage();
    expect(runButton().textContent).toBe('Run, about 40 credits');
    expect(screen.getByText(/usually 30 to 50/)).toBeTruthy();
    expect(screen.queryByText('Breakdown')).toBeTruthy();
  });

  it('says "No credits used" for a free app', () => {
    env.state.app = { ...appResponse(), costClass: 'No credits' };
    env.state.estimate = { ...ESTIMATE, typicalCredits: 0, lowCredits: 0, highCredits: 0 };
    mountPage();
    expect(runButton().textContent).toBe('Run');
    expect(screen.getByText(/No credits used/)).toBeTruthy();
  });

  it('asks for a new estimate only when the resolution changes, never for language, length or narration', async () => {
    mountPage();
    const before = new Set(env.estimateCalls);
    expect([...before]).toEqual([undefined]);
    fireEvent.click(screen.getByRole('radio', { name: '90s' }));
    fireEvent.click(screen.getByRole('switch'));
    const language = screen.getByRole('combobox', { name: 'Language' });
    fireEvent.click(language);
    fireEvent.click(await screen.findByRole('option', { name: 'Italiano' }));
    expect(new Set(env.estimateCalls)).toEqual(before);
    fireEvent.click(screen.getByRole('radio', { name: '1080p' }));
    expect(new Set(env.estimateCalls)).toEqual(new Set([undefined, '1080p']));
  });
});

describe('phone layout (under 768 px)', () => {
  beforeEach(() => {
    env.phone = true;
  });

  it('uses a file sheet button for the slot, and the overflow menu carries "Edit the steps (advanced)"', async () => {
    mountPage();
    expect(screen.getByRole('button', { name: /Choose video clips/ })).toBeTruthy();
    // The upload sits next to the picker button (the old file sheet is gone, so it is no longer hidden behind it).
    expect(screen.getByTestId('upload-video')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'More options' }));
    expect((await screen.findByRole('menuitem', { name: 'Edit the steps (advanced)' })).getAttribute('href')).toBe('/projects/p1/workflows/w1?view=advanced');
  });

  it('when an error blocks, the full-width Run reads the fix and does it', async () => {
    env.state.files = [];
    mountPage();
    expect(runButton().textContent).toBe('Add a clip');
    fireEvent.click(runButton());
    expect(await screen.findByText('No video clips in this project yet.')).toBeTruthy();
    expect(env.executeWorkflow).not.toHaveBeenCalled();
  });

  it('keeps the strip to one summary line with the full list behind it', async () => {
    env.lintWorkflow.mockResolvedValue({
      findings: [
        { ruleId: 'WL016', severity: 'warning', message: 'The film would be silent.' },
        { ruleId: 'WL019', severity: 'info', message: 'Captions come from the narration.' },
      ],
      errors: 0,
      warnings: 1,
      infos: 1,
    });
    mountPage();
    const summary = (await screen.findByText('The film would be silent.')).closest('summary');
    expect(summary).toBeTruthy();
    expect(summary!.parentElement!.tagName).toBe('DETAILS');
    expect(screen.getByText('Captions come from the narration.')).toBeTruthy();
  });

  it('gives the strip fix button a 44 px target on a phone', async () => {
    env.lintWorkflow.mockResolvedValue({
      findings: [{ ruleId: 'WL012', severity: 'error', message: 'No design.', fix: { kind: 'approveDesignSystem', label: 'Approve the design' } }],
      errors: 1, warnings: 0, infos: 0,
    });
    mountPage();
    const fix = await within(screen.getByTestId('readiness-strip')).findByRole('button', { name: 'Approve the design' });
    expect(fix.style.minHeight).toBe('44px');
  });
});

describe('advanced link', () => {
  it('is hidden for a locked app', () => {
    env.state.app = appResponse({ ...MANIFEST, advanced: { allowGraph: false } });
    mountPage();
    expect(screen.queryByRole('button', { name: 'More options' })).toBeNull();
  });
});

describe('follow-up fixes', () => {
  it('an AI-shots plan refusal keeps the other choices and does not touch the resolution', async () => {
    env.executeWorkflow.mockRejectedValue(new ApiError('Your plan has no AI shots.', 402, [], { code: 'PLAN_NOT_ENTITLED', feature: 'aiShots' }));
    mountPage();
    fireEvent.click(screen.getByRole('radio', { name: '1080p' }));
    fireEvent.click(runButton());
    await waitFor(() => expect(isBlocked()).toBe(true));
    expect(screen.getByTestId('run-block-reason').textContent).toContain('Your plan has no AI shots.');
    expect((screen.getByRole('radio', { name: '1080p' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.queryByRole('button', { name: 'Use Auto resolution' })).toBeNull();
  });

  it('a run refused because a video step has no video says so next to Run, with no plan link', async () => {
    env.executeWorkflow.mockRejectedValue(new ApiError('The step 1 (Analyze) needs a video.', 400, [], {
      code: 'VIDEO_SOURCE_NOT_CHOSEN', message: 'The step 1 (Analyze) needs a video.', stepOrder: 1, stepLabel: 'Analyze source video',
    }));
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(isBlocked()).toBe(true));
    const reason = screen.getByTestId('run-block-reason').textContent ?? '';
    expect(reason).toContain('“Analyze source video” needs a video');
    expect(reason).toContain('Choose a video above');
    expect(screen.queryByRole('link', { name: 'See plans' })).toBeNull();
  });

  it('an avatar step with no avatar chosen is explained next to Run', async () => {
    env.executeWorkflow.mockRejectedValue(new ApiError('x', 400, [], { code: 'AVATAR_NOT_CHOSEN', message: 'x', stepLabel: 'Avatar clip' }));
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(isBlocked()).toBe(true));
    expect(screen.getByTestId('run-block-reason').textContent).toContain('“Avatar clip” makes an avatar say its narration');
  });

  it('says Checking, not Ready, until the first check comes back', async () => {
    env.lintWorkflow.mockReturnValue(new Promise(() => {}));
    mountPage();
    const strip = screen.getByTestId('readiness-strip');
    expect(strip.textContent).toContain('Checking:');
    expect(strip.textContent).not.toContain('Ready');
  });

  it('an upload fix with no matching slot opens the Files tab', async () => {
    env.state.app = appResponse({ ...MANIFEST, inputs: [{ kind: 'notes', label: 'Anything else?' }] });
    env.lintWorkflow.mockResolvedValue({
      findings: [{ ruleId: 'WL010', severity: 'error', message: 'No footage.', fix: { kind: 'uploadFiles', label: 'Add clips' } }],
      errors: 1, warnings: 0, infos: 0,
    });
    mountPage();
    fireEvent.click(await within(screen.getByTestId('readiness-strip')).findByRole('button', { name: 'Add clips' }));
    expect(env.push).toHaveBeenCalledWith('/projects/p1?tab=files');
  });

  it('sends at most the slot maximum and says so', async () => {
    env.state.app = appResponse({ ...MANIFEST, inputs: [{ kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 1 }] });
    mountPage();
    expect(screen.getByText(/up to 1\)/)).toBeTruthy();
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalled());
    expect(env.executeWorkflow.mock.calls[0]![7].sourceFiles).toEqual([{ role: 'Video', fileIds: ['f1'] }]);
  });

  it('on a phone the status, price and Run live in one sticky bar, and the breakdown opens outside it', () => {
    env.phone = true;
    mountPage();
    const bar = screen.getByTestId('run-bar');
    expect(bar.contains(screen.getByTestId('readiness-strip'))).toBe(true);
    expect(bar.contains(runButton())).toBe(true);
    fireEvent.click(within(bar).getByRole('button', { name: 'Why this price?' }));
    expect(bar.contains(screen.getByText('Breakdown'))).toBe(false);
  });

  it('keeps Stop and a status line in the bar while a run is going', () => {
    const active = run({ id: 'e1', status: 'Running' });
    env.state.executions = [active];
    env.state.detail = active;
    mountPage();
    const bar = screen.getByTestId('run-bar');
    expect(within(bar).getByTestId('stop-button')).toBeTruthy();
    expect(within(bar).getByText('Your video is being made.')).toBeTruthy();
  });

  it('the empty result copy does not say "on the left" on a phone', () => {
    env.phone = true;
    mountPage();
    expect(screen.queryByText(/on the left/)).toBeNull();
  });
});

describe('blocked look, gate, refusals and fixes', () => {
  it('marks a blocked Run with aria-disabled and the blocked class, and clears both when ready', async () => {
    env.state.files = [];
    mountPage();
    expect(runButton().className).toMatch(/blocked/);
    expect(isBlocked()).toBe(true);
    cleanup();
    env.state.files = FILES;
    mountPage();
    expect(runButton().className).not.toMatch(/blocked/);
    expect(runButton().getAttribute('aria-disabled')).toBe('false');
  });

  it('resends with acceptEstimateCredits only after the estimate gate is confirmed', async () => {
    let resend: ((n: number) => unknown) | undefined;
    env.intercept = vi.fn((_e: unknown, r: (n: number) => unknown) => {
      resend = r;
      return true;
    });
    env.executeWorkflow.mockRejectedValueOnce(new ApiError('accept', 409));
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(resend).toBeDefined());
    expect(env.executeWorkflow).toHaveBeenCalledTimes(1);
    expect(env.executeWorkflow.mock.calls[0]![5]).toBeUndefined();
    await resend!(42);
    expect(env.executeWorkflow).toHaveBeenCalledTimes(2);
    expect(env.executeWorkflow.mock.calls[1]![5]).toBe(42);
  });

  it('a 402 INSUFFICIENT_CREDITS on execute shows the shortfall alert and refreshes the estimate', async () => {
    env.executeWorkflow.mockRejectedValue(new ApiError('no credits', 402, [], { code: 'INSUFFICIENT_CREDITS', required: 40, balance: 12 }));
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(env.mutateEstimate).toHaveBeenCalled());
    expect(isBlocked()).toBe(false);
  });

  it('the voice-consent lint fix opens the project Voices tab when there are no voices to pick', async () => {
    env.lintWorkflow.mockResolvedValue({
      findings: [{ ruleId: 'WL008', severity: 'error', message: 'No consent on file.', fix: { kind: 'chooseVoice', label: 'Choose another voice' } }],
      errors: 1, warnings: 0, infos: 0,
    });
    mountPage();
    fireEvent.click(await within(screen.getByTestId('readiness-strip')).findByRole('button', { name: 'Choose another voice' }));
    expect(env.push).toHaveBeenCalledWith('/projects/p1?tab=voices');
  });
});

describe('usability: run form', () => {
  it('a costly run asks first, with the range and balance, and Cancel starts nothing', async () => {
    env.state.estimate = { ...ESTIMATE, highCredits: 80 };
    mountPage();
    fireEvent.click(runButton());
    const dialog = await screen.findByTestId('run-confirm');
    expect(within(dialog).getByText(/You have 500 credits/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(env.executeWorkflow).not.toHaveBeenCalled();
    fireEvent.click(runButton());
    fireEvent.click(within(await screen.findByTestId('run-confirm')).getByRole('button', { name: 'Run' }));
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
  });

  it('a free run starts at once, with no dialog', async () => {
    env.state.app = { ...appResponse(), costClass: 'No credits' };
    env.state.estimate = { ...ESTIMATE, typicalCredits: 0, lowCredits: 0, highCredits: 0 };
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId('run-confirm')).toBeNull();
  });

  it('a Trial workspace short of credits is offered "Choose a plan", not "Add credits"', () => {
    env.state.estimate = { ...ESTIMATE, requiredCredits: 40, balanceCredits: 12, affordable: false };
    env.state.summary = { topUp: { available: false } };
    mountPage();
    expect(screen.getByText('Choose a plan')).toBeTruthy();
    expect(screen.queryByText('Add credits')).toBeNull();
  });

  it('an empty required brief is listed as left to do (never Ready), and errors on the field only after blur', async () => {
    env.state.workflow = { ...WORKFLOW, requiresUserInput: true };
    mountPage();
    expect(screen.getByTestId('run-block-reason').textContent).toMatch(/Describe what you want/);
    const strip = await screen.findByTestId('readiness-strip');
    expect(strip.getAttribute('data-state')).toBe('fix');
    expect(strip.textContent).toMatch(/Describe what you want in the box above/);
    expect(strip.textContent).not.toMatch(/Ready/);
    expect(screen.queryByText('Tell us what you want to make')).toBeNull();
    const box = document.getElementById('app-input-notes') as HTMLTextAreaElement;
    fireEvent.blur(box);
    expect(screen.getByText('Tell us what you want to make')).toBeTruthy();
    fireEvent.change(box, { target: { value: 'A launch video' } });
    await waitFor(() => expect(screen.getByTestId('readiness-strip').getAttribute('data-state')).toBe('ready'));
  });

  it('refreshes the run list when the polled run reaches a terminal state, so the history card does not stay Queued', async () => {
    env.state.executions = [run({ id: 'e1', status: 'Running' })];
    env.state.detail = run({ id: 'e1', status: 'Passed', completedAt: '2026-10-25T10:05:00Z' });
    mountPage();
    await waitFor(() => expect(env.mutateExecutions).toHaveBeenCalled());
  });

  it('never says the video is ready while the run list still shows the run going', async () => {
    env.state.executions = [run({ id: 'e1', status: 'Running' })];
    env.state.detail = run({ id: 'e1', status: 'Passed', completedAt: '2026-10-25T10:05:00Z' });
    mountPage();
    await waitFor(() => expect(env.mutateExecutions).toHaveBeenCalled());
    expect(screen.queryByText('Your video is ready')).toBeNull();
    expect(screen.getByText('Making your video')).toBeTruthy();
  });

  it('defaults the shape to As set up when the chosen clips are landscape, and says so; no probe keeps the style shape', () => {
    const probe = (w: number, h: number) => ({ width: w, height: h });
    env.state.pool = [
      { mediaRef: 'file:f1', probe: probe(1920, 1080) },
      { mediaRef: 'file:f2', probe: probe(1280, 720) },
    ];
    env.state.app = appResponse({ ...MANIFEST, suggestedStylePreset: 'tiktok-reel' });
    mountPage();
    expect(screen.getByText(/keeps their shape/)).toBeTruthy();
    cleanup();
    env.state.pool = [{ mediaRef: 'file:f1', probe: null }];
    mountPage();
    expect(screen.queryByText(/keeps their shape/)).toBeNull();
  });

  it('does not refresh the run list while the run is still going', async () => {
    env.state.executions = [run({ id: 'e1', status: 'Running' })];
    env.state.detail = run({ id: 'e1', status: 'Running' });
    mountPage();
    await new Promise((r) => setTimeout(r, 50));
    expect(env.mutateExecutions).not.toHaveBeenCalled();
  });

  it('two quick clicks on the dialog Run start exactly one run, and send the shown maximum', async () => {
    env.state.estimate = { ...ESTIMATE, highCredits: 80 };
    env.executeWorkflow.mockImplementation(() => new Promise(() => {}));
    mountPage();
    fireEvent.click(runButton());
    const go = within(await screen.findByTestId('run-confirm')).getByRole('button', { name: 'Run' });
    fireEvent.click(go);
    fireEvent.click(go);
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![5]).toBe(80);
  });

  it('a second press while the estimate gate resend is in flight does nothing', async () => {
    let resend: ((n: number) => unknown) | undefined;
    env.intercept = vi.fn((_e: unknown, r: (n: number) => unknown) => {
      resend = r;
      return true;
    });
    env.executeWorkflow.mockRejectedValueOnce(new ApiError('accept', 409));
    mountPage();
    fireEvent.click(runButton());
    await waitFor(() => expect(resend).toBeDefined());
    env.executeWorkflow.mockImplementation(() => new Promise(() => {}));
    void resend!(42);
    fireEvent.click(runButton());
    fireEvent.click(runButton());
    expect(env.executeWorkflow).toHaveBeenCalledTimes(2);
  });
});

// --------------------------------------------------------------------------- app inputs (docs/design/app-inputs.md 21)

const INPUTS_MANIFEST: AppManifest = {
  version: 2,
  title: 'Avatar clip',
  inputs: [
    { kind: 'input', id: 'videoPrompt', type: 'longText', label: 'What happens in the video', example: 'A barista pours latte art.', required: true, maxLength: 1500 },
    { kind: 'input', id: 'liteMode', type: 'toggle', label: 'Lite mode (cheaper)', default: false },
    { kind: 'input', id: 'lineToSay', type: 'shortText', label: 'What they say', showWhen: { input: 'liteMode', equals: false } },
    { kind: 'input', id: 'clipLength', type: 'duration', label: 'Clip length', default: 6, choices: [6, 10] },
    { kind: 'input', id: 'look', type: 'choice', label: 'Look', default: 'warm', options: [{ value: 'warm', label: 'Warm' }, { value: 'cool', label: 'Cool' }] },
    { kind: 'input', id: 'ratio', type: 'shape', label: 'Shape', default: '9:16', choices: ['9:16', '16:9'] },
    { kind: 'input', id: 'videoModel', type: 'model', label: 'Video model', capability: 'VideoGeneration' },
    { kind: 'brief', field: 'resolution', label: 'Resolution' },
  ],
  result: { kind: 'video' },
  advanced: { allowGraph: true },
};

const MODEL_OPTIONS = {
  inputId: 'videoModel',
  capability: 'VideoGeneration',
  job: 'textToVideo',
  options: [
    { id: 'm-rec', label: 'Hailuo 3', note: 'Recommended', costIndex: 1, recommended: true, available: true },
    { id: 'm-dear', label: 'Kling 3', note: null, costIndex: 2.1, recommended: false, available: true },
    { id: 'm-cheap', label: 'Wan Lite', note: 'Fast drafts', costIndex: 0.4, recommended: false, available: true },
    { id: 'm-off', label: 'Veo 4', note: null, costIndex: null, recommended: false, available: false, unavailableReason: 'Not offered by this app', unavailableCode: 'notAllowed' },
  ],
};

function inputsApp(manifest: AppManifest = INPUTS_MANIFEST): WorkflowAppResponse {
  return appResponse(manifest);
}

const promptBox = () => screen.getByRole('textbox', { name: /What happens in the video/ }) as HTMLTextAreaElement;

describe('app inputs on the run page', () => {
  beforeEach(() => {
    window.localStorage.clear();
    env.state.modelOptions = MODEL_OPTIONS;
  });

  it('an app with no app inputs looks exactly as today: no "More options", the plain estimate, no inputs sent', async () => {
    env.state.app = inputsApp(MANIFEST);
    mountPage();
    expect(screen.queryByTestId('app-run-more-toggle')).toBeNull();
    expect(screen.getByRole('radio', { name: '1080p' })).toBeTruthy();
    expect(env.estimateCalls).toContain(undefined);
    expect(env.briefEstimateCalls).toEqual([]);
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).not.toHaveProperty('inputs');
  });

  it('shows the essentials with their example, keeps the model and the resolution under a closed "More options"', () => {
    env.state.app = inputsApp();
    mountPage();
    expect(promptBox().getAttribute('placeholder')).toBe('A barista pours latte art.');
    expect(screen.getByRole('switch', { name: 'Lite mode (cheaper)' })).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Vertical' })).toBeTruthy();
    const more = screen.getByTestId('app-run-more-toggle');
    expect(more.textContent).toContain('More options (2)');
    expect(more.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByTestId('app-input-model-videoModel')).toBeNull();
    expect(screen.queryByRole('radio', { name: '1080p' })).toBeNull();
  });

  it('blocks Run until a required input is filled, with a fix that goes to the field', () => {
    env.state.app = inputsApp();
    mountPage();
    expect(isBlocked()).toBe(true);
    expect(screen.getAllByText('Fill in “What happens in the video” to run this app.').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Fill in “What happens in the video”' }));
    expect(document.activeElement?.id).toBe('app-input-in-videoPrompt');
    fireEvent.change(promptBox(), { target: { value: '  A dog surfing  ' } });
    expect(isBlocked()).toBe(false);
  });

  it('sends the visible values as brief.inputs; a field hidden by its switch is left out', async () => {
    env.state.app = inputsApp();
    mountPage();
    fireEvent.change(promptBox(), { target: { value: 'A dog surfing' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'What they say' }), { target: { value: 'Hello' } });
    fireEvent.click(screen.getByRole('radio', { name: '10s' }));
    fireEvent.click(screen.getByRole('radio', { name: 'Cool' }));
    fireEvent.click(screen.getByRole('switch', { name: 'Lite mode (cheaper)' }));
    // Lite mode on hides "What they say" (showWhen liteMode = false): it leaves the page and the request.
    expect(screen.queryByRole('textbox', { name: 'What they say' })).toBeNull();
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7].inputs).toEqual({
      videoPrompt: 'A dog surfing',
      liteMode: true,
      clipLength: 10,
      look: 'cool',
      ratio: '9:16',
    });
  });

  it('prices with the inputs that move the price, never with typed text', async () => {
    env.state.app = inputsApp();
    mountPage();
    await waitFor(() => expect(env.briefEstimateCalls.length).toBeGreaterThan(0));
    const first = env.briefEstimateCalls.at(-1) as { brief: { inputs: Record<string, unknown> } };
    expect(first.brief.inputs).toEqual({ liteMode: false, clipLength: 6, look: 'warm', ratio: '9:16' });
    expect(env.estimateCalls).toEqual([]);
    const calls = env.briefEstimateCalls.length;
    fireEvent.change(promptBox(), { target: { value: 'A dog surfing' } });
    await new Promise((r) => setTimeout(r, 500));
    expect(env.briefEstimateCalls.slice(calls).every((b) => !JSON.stringify(b).includes('dog'))).toBe(true);
    fireEvent.click(screen.getByRole('radio', { name: '10s' }));
    await waitFor(() => expect(JSON.stringify(env.briefEstimateCalls.at(-1))).toContain('"clipLength":10'));
  });

  it('model cards: Recommended first, the price in words, an unavailable model says why; nothing chosen picks the recommended', async () => {
    env.state.app = inputsApp();
    mountPage();
    fireEvent.click(screen.getByTestId('app-run-more-toggle'));
    const group = await screen.findByRole('radiogroup', { name: 'Video model' });
    const cards = () => within(group).getAllByRole('radio') as HTMLInputElement[];
    expect(cards().map((c) => c.value)).toEqual(['m-rec', 'm-dear', 'm-cheap', 'm-off']);
    await waitFor(() => expect(cards()[0]!.checked).toBe(true));
    expect(within(group).getByText('Recommended')).toBeTruthy();
    expect(within(group).getByText('About 2× the price')).toBeTruthy();
    expect(within(group).getByText('Cheapest')).toBeTruthy();
    expect(within(group).getByText('Not offered by this app')).toBeTruthy();
    expect(cards()[3]!.disabled).toBe(true);
    // No raw figure or money anywhere on the cards.
    expect(group.textContent).not.toMatch(/2\.1|credits|\$/);
    fireEvent.click(cards()[1]!);
    fireEvent.change(promptBox(), { target: { value: 'A dog surfing' } });
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7].inputs.videoModel).toBe('m-dear');
  });

  it('remembers "More options" open for this person and app', () => {
    env.state.app = inputsApp();
    const first = mountPage();
    fireEvent.click(screen.getByTestId('app-run-more-toggle'));
    first.unmount();
    mountPage();
    expect(screen.getByTestId('app-run-more-toggle').getAttribute('aria-expanded')).toBe('true');
  });

  it('a run-gate refusal is shown next to the field it names, which takes focus', async () => {
    env.state.app = inputsApp();
    env.executeWorkflow.mockRejectedValueOnce(
      new ApiError('The value cannot be used.', 400, [], { code: 'APP_INPUT_INVALID', message: 'x', inputId: 'videoPrompt' }),
    );
    mountPage();
    fireEvent.change(promptBox(), { target: { value: 'A dog surfing' } });
    fireEvent.click(runButton());
    expect(await screen.findByText('This value cannot be used here. Change it, then press Run again.')).toBeTruthy();
    expect(document.activeElement?.id).toBe('app-input-in-videoPrompt');
    expect(env.notify).not.toHaveBeenCalled();
    // Changing that field clears the sentence.
    fireEvent.change(promptBox(), { target: { value: 'A cat surfing' } });
    expect(screen.queryByText('This value cannot be used here. Change it, then press Run again.')).toBeNull();
  });

  it('F3: a Retry refused for an app field is shown next to that field with the run value, not as a run error', async () => {
    env.state.app = inputsApp();
    env.state.executions = [run({ id: 'e2', status: 'Failed', brief: { inputs: { videoPrompt: 'A dog surfing' } } })];
    env.retryExecution.mockRejectedValueOnce(
      new ApiError('Run-level text', 400, [], { code: 'APP_INPUT_INVALID', message: 'Run-level text', inputId: 'videoPrompt' }),
    );
    mountPage();
    fireEvent.click(within(screen.getByTestId('failed-banner')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText(/This value cannot be used here\. Change it, then press Run again\. Retry used this run's value/)).toBeTruthy();
    expect((promptBox() as HTMLTextAreaElement).value).toBe('A dog surfing');
    expect(document.activeElement?.id).toBe('app-input-in-videoPrompt');
    expect(screen.queryByText('Run-level text')).toBeNull();
  });

  it('a model refusal opens "More options" and says to choose another model', async () => {
    env.state.app = inputsApp();
    env.executeWorkflow.mockRejectedValueOnce(
      new ApiError('The model chosen cannot be used.', 400, [], { code: 'APP_INPUT_MODEL_UNAVAILABLE', message: 'x', inputId: 'videoModel' }),
    );
    mountPage();
    fireEvent.change(promptBox(), { target: { value: 'A dog surfing' } });
    fireEvent.click(runButton());
    expect(await screen.findByText('This model cannot be used for this app. Choose another one.')).toBeTruthy();
    expect(screen.getByTestId('app-run-more-toggle').getAttribute('aria-expanded')).toBe('true');
  });

  it('the chooseModel fix of the pre-run check opens the model cards', async () => {
    env.state.app = inputsApp();
    env.lintWorkflow.mockResolvedValue({
      findings: [{
        ruleId: 'WL030', severity: 'error', messageKey: 'WL030', args: { input: 'Video model', inputId: 'videoModel' },
        message: 'The model chosen for Video model can no longer be used.',
        fix: { kind: 'chooseModel', label: 'Choose another model', labelId: 'WL030.chooseModel' },
      }],
      errors: 1, warnings: 0, infos: 0,
    } satisfies WorkflowLintResponse);
    mountPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Choose another model' }));
    expect(screen.getByTestId('app-run-more-toggle').getAttribute('aria-expanded')).toBe('true');
    expect(await screen.findByRole('radiogroup', { name: 'Video model' })).toBeTruthy();
  });

  it('on a phone the model cards open in a sheet from one button', async () => {
    env.phone = true;
    env.state.app = inputsApp({ ...INPUTS_MANIFEST, inputs: INPUTS_MANIFEST.inputs!.map((i) => (i.type === 'model' ? { ...i, more: false } : i)) });
    mountPage();
    fireEvent.click(await screen.findByRole('button', { name: /Hailuo 3, Change/ }));
    const group = await screen.findByRole('radiogroup', { name: 'Video model' });
    fireEvent.click(within(group).getAllByRole('radio')[2]!);
    await waitFor(() => expect(screen.getByRole('button', { name: /Wan Lite, Change/ })).toBeTruthy());
  });

  it('"Change one thing and re-run" puts the previous run\'s input values back', async () => {
    env.state.app = inputsApp();
    env.state.executions = [run({
      id: 'e1', status: 'Passed',
      brief: { inputs: { videoPrompt: 'A cat on a skateboard', clipLength: 10, liteMode: true } },
    } as never)];
    mountPage();
    fireEvent.click(await screen.findByRole('button', { name: /Change one thing/ }));
    expect(promptBox().value).toBe('A cat on a skateboard');
    expect((screen.getByRole('switch', { name: 'Lite mode (cheaper)' }) as HTMLInputElement).checked).toBe(true);
  });
});

// ------------------------------------------------------------------------------------------------
// The first run and the first result (docs/design/new-user-funnel.md, journey steps 5 and 6).
// ------------------------------------------------------------------------------------------------

const FOOTAGE_MANIFEST: AppManifest = {
  version: 1,
  title: 'Highlight edit from footage',
  tagline: 'A tight cut of your clips.',
  category: 'footage',
  inputs: [
    { kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 20 },
    { kind: 'brief', field: 'lengthSec', label: 'Length', default: 60, choices: [30, 60, 90] },
    { kind: 'brief', field: 'format', label: 'Shape' },
    { kind: 'notes', label: 'Who is it for and what should it show?' },
  ],
  result: { kind: 'video' },
  advanced: { allowGraph: true },
};
const NOTES = 'Who is it for and what should it show?';
const notesBox = () => screen.getByRole('textbox', { name: new RegExp(`^${NOTES.replace('?', '\\?')}`) }) as HTMLTextAreaElement;
const profile = (persona: string | null, suggestedPersona: string | null = null) => ({
  profile: { persona, material: 'footage', suggestedPersona, answeredAt: persona ? '2026-11-11T09:00:00Z' : null, skippedAt: null },
});
const newcomer = (openResult = false) => ({
  server: { tour: { status: 'pending', version: null, updatedAt: null }, checklist: { dismissed: false, items: [{ key: 'open_result', done: openResult }] } },
});

describe('brief help: example briefs in the person\'s own terms', () => {
  beforeEach(() => {
    env.state.workflow = { ...WORKFLOW, requiresUserInput: true };
    env.state.app = appResponse(FOOTAGE_MANIFEST);
  });

  it('offers three footage examples for the audience the person chose, and a one-line prompt', () => {
    env.state.onboarding = profile('educator');
    mountPage();
    const examples = screen.getByRole('group', { name: 'Examples you can edit' });
    const chips = within(examples).getAllByRole('button');
    expect(chips).toHaveLength(3);
    expect(chips[0]!.textContent).toBe('For new students. The one idea of this lesson, explained once, without the tangents.');
    expect(notesBox().getAttribute('aria-describedby')).toBeTruthy();
    expect(screen.getByText('Say who is learning and the one idea they should keep. One or two sentences are enough.')).toBeTruthy();
  });

  it('a click puts the example in the box, where it is edited like anything typed, and unblocks Run', async () => {
    env.state.onboarding = profile('business');
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(isBlocked()).toBe(true);
    const chip = within(screen.getByTestId('brief-examples')).getAllByRole('button')[1]!;
    fireEvent.click(chip);
    expect(notesBox().value).toBe('For new customers. How we make a custom order, from the first sketch to the finished piece.');
    expect(document.activeElement).toBe(notesBox());
    await waitFor(() => expect(isBlocked()).toBe(false));
    // Edited, then sent exactly as it stands: the example is a start, never a hidden value.
    fireEvent.change(notesBox(), { target: { value: 'For new customers. How we make a wedding cake.' } });
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ notes: 'For new customers. How we make a wedding cake.' });
  });

  it('never replaces what the person typed: the example goes on a new line', () => {
    env.state.onboarding = profile('creator');
    mountPage();
    fireEvent.change(notesBox(), { target: { value: 'For my mum.' } });
    fireEvent.click(within(screen.getByTestId('brief-examples')).getAllByRole('button')[0]!);
    expect(notesBox().value).toBe('For my mum.\nFor my followers. The best moments of the hike, cut tight, starting with the view from the summit.');
  });

  it('uses the audience the landing page suggested when the questions were not answered', () => {
    env.state.onboarding = profile(null, 'nonprofit');
    mountPage();
    expect(within(screen.getByTestId('brief-examples')).getAllByRole('button')[0]!.textContent)
      .toBe("For our supporters. What last weekend's clean-up achieved, in the volunteers' own words.");
  });

  it('falls back to neutral examples when nothing is known about the person', () => {
    mountPage();
    const chips = within(screen.getByTestId('brief-examples')).getAllByRole('button');
    expect(chips.map((c) => c.textContent)).toEqual([
      'For people who were not there. What happened and why it matters, in the order it happened.',
      'For friends and family. The best moments of the day, with the quiet parts removed.',
      'For anyone new to this. The main point first, then the two clips that show it best.',
    ]);
    expect(screen.getByText('Say who it is for and what it should show. One or two sentences are enough.')).toBeTruthy();
  });

  it('a promo app gets promo examples (which screens to show), not footage ones', () => {
    env.state.app = appResponse({ ...MANIFEST, inputs: [...(MANIFEST.inputs ?? []).slice(0, -1), { kind: 'notes', label: NOTES }] });
    env.state.onboarding = profile('software');
    mountPage();
    expect(within(screen.getByTestId('brief-examples')).getAllByRole('button')[0]!.textContent)
      .toBe('For developers who have never seen the product. Show the dashboard, then creating a first project, then the result.');
    expect(screen.getByText('Say who it is for and which screens to show. One or two sentences are enough.')).toBeTruthy();
  });

  it('shows no examples where the box is optional, or for an app they were not written for', () => {
    env.state.workflow = WORKFLOW;
    mountPage();
    expect(screen.queryByTestId('brief-examples')).toBeNull();
    cleanup();
    env.state.workflow = { ...WORKFLOW, requiresUserInput: true };
    env.state.app = appResponse({ ...FOOTAGE_MANIFEST, category: 'utility' });
    mountPage();
    expect(screen.queryByTestId('brief-examples')).toBeNull();
  });

  it('changes nothing about the run: the same brief, the same gate, the same price request', async () => {
    env.state.onboarding = profile('agency');
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.change(notesBox(), { target: { value: 'typed by hand' } });
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    const [project, workflow, userRequest, regenerate, choices, accept, briefFile, brief] = env.executeWorkflow.mock.calls[0]!;
    expect([project, workflow, userRequest, regenerate, choices, accept, briefFile]).toEqual(['p1', 'w1', null, false, {}, undefined, undefined]);
    expect(brief).toEqual({ lengthSec: 60, notes: 'typed by hand', sourceFiles: [{ role: 'Video', fileIds: ['f1', 'f2'] }] });
    // The persona never travels with the run.
    expect(JSON.stringify(env.executeWorkflow.mock.calls[0])).not.toContain('agency');
  });
});

describe('an address that is not confirmed: said before Run, not after', () => {
  const refusal = () => new ApiError('Confirm your email address to run this.', 403, [], { code: 'EMAIL_NOT_VERIFIED', message: 'Confirm your email address to run this.' });

  it('says so next to Run before it is pressed, with the resend, and never blocks the button', async () => {
    env.state.emailVerified = false;
    env.resendVerification.mockResolvedValue({ message: 'ok' });
    mountPage();
    const notice = within(screen.getByTestId('run-bar')).getByTestId('confirm-email-notice');
    expect(notice.getAttribute('role')).toBe('status');
    expect(notice.textContent).toContain('Confirm your email before you run');
    expect(notice.textContent).toContain('ada@example.com');
    await waitFor(() => expect(screen.getByTestId('readiness-strip').getAttribute('data-state')).toBe('check'));
    expect(screen.getByTestId('readiness-strip').textContent).toContain('Confirm your email address first.');
    // The server decides: this browser's session can be older than the truth.
    expect(isBlocked()).toBe(false);
    fireEvent.click(within(notice).getByRole('button', { name: 'Resend the email' }));
    await waitFor(() => expect(env.resendVerification).toHaveBeenCalledWith({ email: 'ada@example.com' }));
    expect(await within(notice).findByText('Sent. Check your inbox.')).toBeTruthy();
    expect(env.executeWorkflow).not.toHaveBeenCalled();
  });

  it('shows nothing to a confirmed address, nor to a session that does not say', () => {
    env.state.emailVerified = true;
    mountPage();
    expect(screen.queryByTestId('confirm-email-notice')).toBeNull();
    cleanup();
    env.state.emailVerified = undefined;
    mountPage();
    expect(screen.queryByTestId('confirm-email-notice')).toBeNull();
  });

  it('on a phone the notice stands above the form and the strip leads to it', async () => {
    env.phone = true;
    env.state.emailVerified = false;
    mountPage();
    const notice = screen.getByTestId('confirm-email-notice');
    expect(screen.getByTestId('run-bar').contains(notice)).toBe(false);
    const strip = screen.getByTestId('readiness-strip');
    await waitFor(() => expect(strip.textContent).toContain('Confirm your email address first.'));
    fireEvent.click(within(strip).getByRole('button', { name: 'Resend the email' }));
    expect(document.activeElement).toBe(within(notice).getByRole('button', { name: 'Resend the email' }));
  });

  it('a run the server refuses for this reason lands in the notice, in words, instead of a toast', async () => {
    // The session said nothing (or said "confirmed"): the refusal is still explained in place.
    env.executeWorkflow.mockRejectedValue(refusal());
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(screen.queryByTestId('confirm-email-notice')).toBeNull();
    fireEvent.click(runButton());
    const notice = await screen.findByTestId('confirm-email-notice');
    expect(notice.getAttribute('role')).toBe('alert');
    expect(notice.textContent).toContain('That run did not start, because your email address is not confirmed yet.');
    expect(env.notify).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(within(notice).getByRole('button', { name: 'Resend the email' })));
    // Run stays available for when the link has been opened.
    expect(isBlocked()).toBe(false);
  });

  it('Retry and Resume failing with a 502 show the generic server sentence, not "Bad Gateway"', async () => {
    const sentence = enT('errors.generic.server');
    env.state.executions = [run({ id: 'e2', status: 'Failed', canResume: true })];
    env.retryExecution.mockRejectedValue(new ApiError('Bad Gateway', 502));
    env.resumeExecution.mockRejectedValue(new ApiError('<!DOCTYPE html><html><body>502 Bad Gateway</body></html>', 502));
    mountPage();
    const banner = screen.getByTestId('failed-banner');
    fireEvent.click(within(banner).getByRole('button', { name: 'Retry' }));
    expect(await within(banner).findByText(sentence)).toBeTruthy();
    expect(screen.queryByText(/Bad Gateway/)).toBeNull();
    fireEvent.click(within(banner).getByRole('button', { name: 'Resume' }));
    await waitFor(() => expect(env.resumeExecution).toHaveBeenCalled());
    expect(await within(screen.getByTestId('failed-banner')).findByText(sentence)).toBeTruthy();
    expect(screen.queryByText(/Bad Gateway|DOCTYPE/)).toBeNull();
  });

  it('Retry and Resume explain the same refusal the same way', async () => {
    env.state.executions = [run({ id: 'e2', status: 'Failed', canResume: true })];
    env.retryExecution.mockRejectedValue(refusal());
    mountPage();
    fireEvent.click(within(screen.getByTestId('failed-banner')).getByRole('button', { name: 'Retry' }));
    const notice = await screen.findByTestId('confirm-email-notice');
    expect(notice.textContent).toContain('That run did not start');
    expect(within(screen.getByTestId('failed-banner')).queryByText('Confirm your email address to run this.')).toBeNull();
  });
});

describe('the first result', () => {
  const OUTPUT = { stepResultId: 'sr5', workflowExecutionId: 'e5', fileName: 'Highlight.mp4', producedAt: '2026-11-11T10:05:00Z', isFinal: true, durationSec: 58 };
  const FOOTAGE_DETAIL = {
    id: 'e5',
    status: 'Passed',
    recentExecutions: [{ id: 'e5', status: 'Passed', startedAt: '2026-11-11T10:00:00Z' }],
    recentOutputs: [{ stepResultId: 'sr5', projectId: 'p1', projectName: 'P', fileName: 'Highlight.mp4', producedAt: '2026-11-11T10:05:00Z' }],
    totals: { projects: 1, sourceFiles: 1, renders: 1, passedLast7Days: 1, failedLast7Days: 0 },
    stepResults: [
      { id: 'r1', workflowStepId: 's1', stepType: 'VideoAnalyze', stepOrder: 1, status: 'Completed', output: '', outputJson: JSON.stringify({ view: { media: { durationSec: 252 } }, meta: {} }) },
      { id: 'r3', workflowStepId: 's3', stepType: 'VideoCompile', stepOrder: 3, status: 'Completed', output: '', outputJson: JSON.stringify({ status: 'completed', segments: 9, outputDurationSec: 58, retainedRatio: 0.23 }) },
    ],
  };

  beforeEach(() => {
    env.state.app = appResponse(FOOTAGE_MANIFEST);
    env.state.executions = [run({ id: 'e5', status: 'Passed', completedAt: '2026-11-11T10:06:00Z', userRequest: 'for my followers', brief: { lengthSec: 30, format: 'Vertical', sourceFiles: [{ role: 'Video', fileIds: ['f2'] }] } })];
    env.state.outputs = [OUTPUT];
    // One body answers both reads the moment makes (the workspace's recent runs and this run).
    env.state.detail = FOOTAGE_DETAIL;
    env.state.onboarding = newcomer();
  });

  it('a first finished video gets the one-time moment in the result pane: the video, this run\'s facts, three next steps', async () => {
    mountPage();
    const result = screen.getByRole('region', { name: 'Result' });
    const moment = await within(result).findByTestId('first-result');
    const status = within(result).getByRole('status');
    expect(status.textContent).toBe('Your first video is ready');
    expect(moment.querySelector('video')?.getAttribute('src')).toBe('/api/v1/projects/p1/outputs/sr5/download');
    await waitFor(() => expect(within(moment).getByTestId('first-result-facts').textContent).toContain('1 clip, 4:12 of footage'));
    expect(within(moment).getByTestId('first-result-facts').textContent).toContain('A finished video of 0:58, cut from 9 parts of that footage');
    expect(within(moment).getByRole('link', { name: 'Download' }).getAttribute('href')).toContain('saveAs=Highlight.mp4');
    expect(within(moment).getByRole('button', { name: 'Change something' })).toBeTruthy();
    expect(within(moment).getByRole('button', { name: 'Add subtitles or try another shape' })).toBeTruthy();
    // In the page, never over it; one video, one download.
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(result.querySelectorAll('video')).toHaveLength(1);
    expect(within(result).getAllByRole('link', { name: 'Download' })).toHaveLength(1);
    expect(within(result).queryByRole('button', { name: 'Change one thing and re-run' })).toBeNull();
    // Remembered on the account at the moment it is shown.
    expect(env.markOnboarding).toHaveBeenCalledWith('open_result');
    expect(isBlocked()).toBe(false);
  });

  it('"Change something" is the existing change entry point', async () => {
    mountPage();
    const moment = await screen.findByTestId('first-result');
    fireEvent.click(within(moment).getByRole('button', { name: 'Change something' }));
    await waitFor(() => expect((screen.getByRole('radio', { name: '30s' }) as HTMLInputElement).checked).toBe(true));
    expect(notesBox().value).toBe('for my followers');
  });

  it('"Try another shape" brings the run\'s choices back and puts the Shape choice in focus; nothing runs by itself', async () => {
    mountPage();
    const moment = await screen.findByTestId('first-result');
    fireEvent.click(within(moment).getByRole('button', { name: 'Add subtitles or try another shape' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Try another shape' }));
    await waitFor(() => expect((screen.getByRole('radio', { name: '30s' }) as HTMLInputElement).checked).toBe(true));
    await waitFor(() => expect(document.getElementById('app-input-format')?.contains(document.activeElement)).toBe(true));
    expect(env.executeWorkflow).not.toHaveBeenCalled();
  });

  it('"Add subtitles" sets up the subtitles recipe through the existing apply and opens it', async () => {
    env.applyWorkflowTemplate.mockResolvedValue({ id: 'w-sub' });
    mountPage();
    const moment = await screen.findByTestId('first-result');
    fireEvent.click(within(moment).getByRole('button', { name: 'Add subtitles or try another shape' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Add subtitles' }));
    await waitFor(() => expect(env.applyWorkflowTemplate).toHaveBeenCalledWith('p1', 'video-subtitles', true));
    await waitFor(() => expect(env.push).toHaveBeenCalledWith('/projects/p1/workflows/w-sub'));
  });

  it('closing it is for good, and the ordinary result is right there', async () => {
    mountPage();
    const moment = await screen.findByTestId('first-result');
    fireEvent.click(within(moment).getByRole('button', { name: 'Close' }));
    expect(screen.queryByTestId('first-result')).toBeNull();
    const result = screen.getByRole('region', { name: 'Result' });
    expect(within(result).getByRole('status').textContent).toBe('Your video is ready');
    expect(result.querySelector('video')).toBeTruthy();
    expect(within(result).getByRole('button', { name: 'Change one thing and re-run' })).toBeTruthy();
    cleanup();
    // The account now says a result was opened; this browser remembers it was closed.
    env.state.onboarding = newcomer(true);
    mountPage();
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByTestId('first-result')).toBeNull();
  });

  it('is not shown to someone who has opened a result before, or about whom the API said nothing', async () => {
    env.state.onboarding = newcomer(true);
    mountPage();
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByTestId('first-result')).toBeNull();
    expect(screen.getByText('Your video is ready')).toBeTruthy();
    cleanup();
    env.state.onboarding = undefined;
    mountPage();
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByTestId('first-result')).toBeNull();
    expect(env.markOnboarding).not.toHaveBeenCalled();
  });

  it('is not shown when this app already has an earlier finished video', async () => {
    env.state.executions = [
      ...(env.state.executions as WorkflowExecution[]),
      run({ id: 'e1', status: 'Passed', startedAt: '2026-10-01T10:00:00Z' }),
    ];
    mountPage();
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByTestId('first-result')).toBeNull();
  });

  it('waits for the video: a first run whose file is still being prepared shows no moment yet', async () => {
    env.state.outputs = [];
    mountPage();
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByTestId('first-result')).toBeNull();
    expect(screen.getAllByText(/The video is being prepared\./).length).toBeGreaterThan(0);
    expect(screen.queryByText('Your video is ready')).toBeNull();
  });

  it('the ordinary Download reports the export step too', () => {
    env.state.onboarding = undefined;
    mountPage();
    fireEvent.click(within(screen.getByRole('region', { name: 'Result' })).getByRole('link', { name: 'Download' }));
    expect(emitFunnelEvent).toHaveBeenCalledWith('alpha.first_export_initiated', expect.any(String));
  });
});

describe('a first run that failed', () => {
  const failed = (failureClass: string | null, failureCode = 'UNEXPECTED_ERROR') =>
    run({ id: 'e2', status: 'Failed', stepResults: [{ id: 'r', workflowStepId: 's2', status: 'Failed', failureCode, failureClass }] as never });

  beforeEach(() => {
    env.state.onboarding = newcomer();
  });

  it('says kindly what happened, that the credits held for a step that failed on our side are refunded, and offers Retry', async () => {
    env.state.executions = [failed('PlatformInternal')];
    env.retryExecution.mockResolvedValue(run({ id: 'e3', status: 'Queued' }));
    mountPage();
    const banner = screen.getByTestId('failed-banner');
    expect(within(banner).getByTestId('first-run-failed').textContent).toBe('This was your first run, and it did not finish. Here is what happened.');
    expect(within(banner).getByText('Something unexpected went wrong while making your video.')).toBeTruthy();
    expect(within(banner).getByTestId('first-run-failed-next').textContent)
      .toBe('This step failed on our side, so the credits held for it are refunded. Your files and choices are still here, so you can run it again as it is.');
    fireEvent.click(within(banner).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(env.retryExecution).toHaveBeenCalledWith('p1', 'w1', 'e2', false, undefined));
  });

  it('states the rule, not a refund, when the engine did not say whose side it was', () => {
    env.state.executions = [failed(null)];
    mountPage();
    expect(screen.getByTestId('first-run-failed-next').textContent)
      .toBe('If a step fails on our side, the credits held for it are refunded. Your files and choices are still here, so you can run it again as it is.');
  });

  it('promises no refund for a failure that was the input, and says what to do instead', () => {
    env.state.executions = [failed('UserInput', 'VOICE_CONSENT_MISSING')];
    mountPage();
    const banner = screen.getByTestId('failed-banner');
    expect(within(banner).getByText('The chosen voice has no consent on file. Choose another voice.')).toBeTruthy();
    expect(within(banner).getByTestId('first-run-failed-next').textContent)
      .toBe('Your files and choices are still here. Fix what the message above names, then run it again.');
    expect(banner.textContent).not.toMatch(/refund/i);
  });

  it('does not mention credits where there are none (billing off)', () => {
    env.state.estimate = { ...ESTIMATE, billingEnabled: false };
    env.state.executions = [failed('PlatformInternal')];
    mountPage();
    expect(screen.getByTestId('first-run-failed-next').textContent).toBe('Your files and choices are still here, so you can run it again as it is.');
  });

  it('is an ordinary failure for anyone who is not on their first run', () => {
    env.state.executions = [failed('PlatformInternal'), run({ id: 'e1', status: 'Passed', startedAt: '2026-10-01T10:00:00Z' })];
    mountPage();
    expect(screen.queryByTestId('first-run-failed')).toBeNull();
    cleanup();
    env.state.onboarding = newcomer(true);
    env.state.executions = [failed('PlatformInternal')];
    mountPage();
    expect(screen.queryByTestId('first-run-failed')).toBeNull();
    expect(screen.getByTestId('failed-banner').textContent).not.toMatch(/refund/i);
  });
});

describe('permission to send media (docs/design/app-inputs.md "Permission to send media as an app field")', () => {
  const PERMISSION: AppManifest['inputs'] = [
    { kind: 'input', id: 'allowMediaEgress', type: 'toggle', label: 'Allow sending your media to AI services', required: true, more: false, permission: 'mediaEgress' },
  ];
  const WL015 = {
    ruleId: 'WL015', severity: 'error' as const, stepOrder: 1, message: 'Step 1 would send your footage to an outside service, but the permission is off.',
    messageKey: 'WL015.tracksAnObjectByName', args: { stepOrder: '1', inputId: 'allowMediaEgress' },
    fix: { kind: 'fillInput' as const, label: 'Allow sending your media on the app page', labelId: 'WL015.allowOnAppPage' },
  };
  const withPermission = (required = true, alreadyAllowed = false) => {
    // The pre-run check is what says a run with the current choices needs it (WL015), as the server answers.
    env.lintWorkflow.mockResolvedValue({ findings: required ? [WL015] : [], errors: required ? 1 : 0, warnings: 0, infos: 0 });
    env.state.app = {
      ...appResponse({
        ...MANIFEST,
        inputs: [...(MANIFEST.inputs ?? []), { ...PERMISSION![0]!, required: required ? true : null, ...(alreadyAllowed ? { default: true } : {}) }],
      }),
      mediaEgress: {
        inputId: 'allowMediaEgress',
        required,
        alreadyAllowed,
        steps: [{ stepOrder: 1, stepName: 'Find the plate', stepType: 'VideoAnalyze', reason: 'tracksAnObjectByName', need: 'required', allowedInStep: alreadyAllowed }],
      },
    };
  };

  it('is one block of its own, apart from the creative fields, naming the steps that need it', () => {
    withPermission();
    mountPage();
    const block = screen.getByTestId('app-permission-block');
    expect(within(block).getByText('Permission')).toBeTruthy();
    expect((within(block).getByRole('switch', { name: /Allow sending your media to AI services/ }) as HTMLInputElement).checked).toBe(false);
    expect(block.textContent).toContain('Needed by: Find the plate (finds the object).');
    expect(block.textContent).toContain('It applies to this run only.');
    expect(screen.getByRole('group', { name: 'Choices' }).textContent).not.toContain('Allow sending your media');
  });

  it('when required and off, Run stays pressable and a press shows the sentence under the switch; nothing starts', async () => {
    withPermission();
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(isBlocked()).toBe(true);
    expect(runButton().hasAttribute('disabled')).toBe(false);
    expect(screen.queryByTestId('app-permission-error')).toBeNull();

    fireEvent.click(runButton());

    expect((await screen.findByTestId('app-permission-error')).textContent).toBe('Allow sending your media to AI services to run this app.');
    expect(env.executeWorkflow).not.toHaveBeenCalled();
  });

  it('switched on, it rides brief.inputs as true and Run goes through', async () => {
    withPermission();
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(screen.getByTestId('app-permission-switch'));
    await waitFor(() => expect(isBlocked()).toBe(false));
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]).toMatchObject({ inputs: { allowMediaEgress: true } });
  });

  it('an optional permission left off sends nothing and never blocks', async () => {
    withPermission(false);
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(screen.getByTestId('app-permission-block').textContent).toContain('Used by: Find the plate');
    expect(isBlocked()).toBe(false);
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalledTimes(1));
    expect(env.executeWorkflow.mock.calls[0]![7]?.inputs).toBeUndefined();
  });

  it('already allowed in the steps: shown on and locked, with the reason', () => {
    withPermission(false, true);
    mountPage();
    const toggle = screen.getByTestId('app-permission-switch') as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    expect(toggle.disabled).toBe(true);
    expect(screen.getByTestId('app-permission-block').textContent).toContain('Already allowed in the step settings for: Find the plate');
  });

  it('is the one place the page talks about it: the linter sentence is not repeated', async () => {
    withPermission();
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    const strip = screen.getByTestId('readiness-strip');
    await waitFor(() => expect(strip.textContent).toContain('Allow sending your media to AI services to run this app.'));
    expect(strip.textContent).not.toContain('outside service');
    expect(strip.textContent).not.toContain('Edit steps');
  });

  it('follows the pre-run check when a field picks a branch that sends nothing: no longer asked for', async () => {
    withPermission();
    // The app said "required" as the page opened, but with the current choices no step that sends is reached.
    env.lintWorkflow.mockResolvedValue({ findings: [], errors: 0, warnings: 0, infos: 0 });
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    await waitFor(() => expect(isBlocked()).toBe(false));
    expect(screen.getByTestId('app-permission-block').textContent).toContain('Used by: Find the plate');
  });
});

describe('loading failures', () => {
  it('app fetch failure: a plain sentence with Retry and a way back, not Edit steps', async () => {
    const { ApiError } = await import('@/lib/api/client');
    env.state.app = undefined;
    env.state.appError = new ApiError('raw boom', 500);
    mountPage();
    expect(screen.getByTestId('failure-card')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to the dashboard' })).toBeTruthy();
    expect(screen.queryByText('Edit steps (advanced)')).toBeNull();
    env.state.appError = undefined;
  });
  it('app 404: says not found and offers no Retry', async () => {
    const { ApiError } = await import('@/lib/api/client');
    env.state.app = undefined;
    env.state.appError = new ApiError('gone', 404);
    mountPage();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Back to the dashboard' })).toBeTruthy();
    env.state.appError = undefined;
  });
});

describe('funnel events on the run page', () => {
  const calls = () => vi.mocked(emitFunnelEvent).mock.calls.map(([name, , detail]) => [name, detail]);

  it('opens once on mount, and does not report a block just because the page starts empty', async () => {
    vi.mocked(emitFunnelEvent).mockClear();
    env.state.files = [{ id: 'f3', originalFileName: 'notes.txt', mimeType: 'text/plain' }];
    mountPage();
    expect(isBlocked()).toBe(true);
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    expect(calls()).toEqual([['alpha.run_page_opened', undefined]]);
  });

  it('reports run_blocked (readiness) when Run is pressed while blocked, and not run_clicked', async () => {
    vi.mocked(emitFunnelEvent).mockClear();
    env.state.files = [{ id: 'f3', originalFileName: 'notes.txt', mimeType: 'text/plain' }];
    mountPage();
    fireEvent.click(runButton());
    expect(calls()).toContainEqual(['alpha.run_blocked', 'readiness']);
    expect(calls().some(([n]) => n === 'alpha.run_clicked')).toBe(false);
  });

  it('reports run_clicked when Run is pressed, and run_blocked:plan when the server refuses the plan', async () => {
    vi.mocked(emitFunnelEvent).mockClear();
    env.executeWorkflow.mockRejectedValue(new ApiError('Your plan allows up to 1080p.', 402, [], { code: 'PLAN_NOT_ENTITLED', feature: 'resolution' }));
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(runButton());
    await waitFor(() => expect(calls()).toContainEqual(['alpha.run_blocked', 'plan']));
    expect(calls()).toContainEqual(['alpha.run_clicked', undefined]);
  });
});

describe('funnel events: estimate confirmation', () => {
  it('does not record the accept-the-estimate prompt as run_blocked', async () => {
    vi.mocked(emitFunnelEvent).mockClear();
    env.executeWorkflow.mockRejectedValue(new ApiError('accept', 409, [], { code: 'ESTIMATE_NOT_ACCEPTED', estimate: { low: 1, high: 2 } }));
    mountPage();
    await waitFor(() => expect(env.lintWorkflow).toHaveBeenCalled());
    fireEvent.click(runButton());
    await waitFor(() => expect(env.executeWorkflow).toHaveBeenCalled());
    expect(vi.mocked(emitFunnelEvent).mock.calls.some(([n]) => n === 'alpha.run_blocked')).toBe(false);
  });
});
