// @vitest-environment jsdom
/**
 * Customer e2e findings 18/19: "Execute" started an hour-long run on one click with no way to give
 * a brief, the page kept saying "No executions" while it ran, and the breadcrumb said "Project".
 */
import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { act, cleanup, render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { useWorkflowCostEstimate } from '@/lib/hooks/use-billing';
import type { WorkflowDefinition, WorkflowExecution } from '@/lib/types/workflow';

const mocks = vi.hoisted(() => ({
  executeWorkflow: vi.fn(),
  mutateExecutions: vi.fn(),
  mutateCostEstimate: vi.fn(),
  push: vi.fn(),
}));

const WORKFLOW = {
  id: 'w1',
  name: 'Spring reel',
  requiresUserInput: false,
  steps: [
    { id: 's1', agentDefinitionId: 'vt', stepOrder: 1, label: '', stepType: 'VideoAnalyze' },
    { id: 's2', agentDefinitionId: 'ed', stepOrder: 2, label: '', stepType: 'Agent' },
  ],
} as unknown as WorkflowDefinition;

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: mocks.push }), usePathname: () => '/projects/p1/workflows/w1', useSearchParams: () => new URLSearchParams('view=advanced') }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflow: () => ({ data: WORKFLOW, isLoading: false, mutate: vi.fn() }) }));
const { APP } = vi.hoisted(() => ({ APP: { current: undefined as unknown } }));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({ useWorkflowApp: () => ({ data: APP.current, error: undefined, isLoading: false }) }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p1', name: 'Spring launch' } }) }));
vi.mock('@/lib/hooks/use-executions', () => ({ useExecutions: () => ({ data: [], mutate: mocks.mutateExecutions }) }));
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({ data: [{ key: 'fast-social-reel', name: 'Fast social reel', description: 'Punchy', config: { pacing: 'Fast' }, suggestedFormat: 'Vertical' }] }),
  useEditingStyles: () => ({ data: [] }),
}));
vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({ data: [{ id: 'vt', agentType: 'VideoTransform' }, { id: 'ed', agentType: 'VideoStoryEditor' }], isLoading: false }),
}));
vi.mock('@/lib/api/workflows', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/workflows')>()),
  executeWorkflow: mocks.executeWorkflow,
}));
vi.mock('@/components/workflows/FlowchartBuilder', () => ({ FlowchartBuilderWrapper: () => null }));
vi.mock('@/components/workflows/ExecutionHistory', () => ({ ExecutionHistory: () => null }));
// The B7 estimate. Stubbed here because the page fetches it on mount: a real SWR hook would try to
// reach the network in a component test, and every case below is about what the page DOES with the
// figures rather than about fetching them.
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-billing')>();
  // No resolution cap (an unlimited plan): the run's choices are what these cases are about.
  return { ...actual, useWorkflowCostEstimate: vi.fn(), useOutputHeightCap: () => null };
});
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistant: () => ({ opened: false, open: vi.fn() }) }));
vi.mock('@/components/assistant/AssistantPageContext', () => ({ AssistantPageContext: () => null, useAssistantPageContext: () => undefined }));

import { WorkflowAppsFlagProvider } from '@/components/shell/WorkflowAppsFlag';
import WorkflowEditPage from '@/app/(app)/projects/[id]/workflows/[workflowId]/page';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function renderPage(appsOn = false) {
  const value = { id: 'p1', workflowId: 'w1' };
  const params = Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });
  await act(async () => {
    render(<MantineProvider env="test"><WorkflowAppsFlagProvider enabled={appsOn}><WorkflowEditPage params={params} /></WorkflowAppsFlagProvider></MantineProvider>);
  });
}

// Every case starts from an affordable estimate with history behind it; the cases that are about
// the cost override it. Without this the page's own estimate hook returns nothing and the whole
// render throws, which is not what any of these cases is about.
beforeEach(() => {
  estimateMock.mockReturnValue(estimateResult(ESTIMATE));
});

describe('Workflow page run flow with typed app inputs', () => {
  it('asks for the typed inputs and not a free brief, and sends them as brief.inputs', async () => {
    APP.current = {
      workflowId: 'w1', name: 'Clip', hasExplicitManifest: true, costClass: 'Uses credits', source: 'custom',
      manifest: { version: 2, inputs: [{ kind: 'input', id: 'prompt', type: 'longText', label: 'What happens', required: true }] },
    };
    mocks.executeWorkflow.mockResolvedValue({ id: 'e12', status: 'Queued' } as WorkflowExecution);
    await renderPage(true);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(await screen.findByRole('textbox', { name: /What happens/ })).toBeTruthy();
    expect(screen.queryByLabelText(/Your brief/)).toBeNull();
    fireEvent.change(screen.getByRole('textbox', { name: /What happens/ }), { target: { value: 'A barista' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    await waitFor(() => expect(mocks.executeWorkflow).toHaveBeenCalledWith('p1', 'w1', null, false, {}, undefined, undefined, { inputs: { prompt: 'A barista' } }));
    APP.current = undefined;
  });

  it('uses the app\'s typed inputs even when the apps switch is off', async () => {
    APP.current = {
      workflowId: 'w1', name: 'Clip', hasExplicitManifest: true, costClass: 'Uses credits', source: 'custom',
      manifest: { version: 2, inputs: [{ kind: 'input', id: 'prompt', type: 'longText', label: 'What happens' }] },
    };
    await renderPage(false);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(await screen.findByRole('textbox', { name: /What happens/ })).toBeTruthy();
    APP.current = undefined;
  });

  it('keeps the notes box when the app cannot be read', async () => {
    APP.current = undefined;
    await renderPage(false);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(await screen.findByLabelText(/Your brief/)).toBeTruthy();
  });
});

describe('Workflow page run flow', () => {
  it('shows the project name in the breadcrumb and the browser tab', async () => {
    await renderPage();
    expect(screen.getByRole('link', { name: 'Spring launch' }).getAttribute('href')).toBe('/projects/p1');
    await waitFor(() => expect(document.title).toBe('Spring reel · Spring launch · ReelBolt'));
  });

  it('asks for confirmation (with an optional brief) instead of starting on one click', async () => {
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(mocks.executeWorkflow).not.toHaveBeenCalled();
    expect(await screen.findByLabelText(/Your brief/)).toBeTruthy();
    expect(screen.getByText('Watches your footage')).toBeTruthy();
    expect(screen.getByText('Picks the best clips')).toBeTruthy();
  });

  it('sends the brief even though the workflow does not require one, and shows the run at once', async () => {
    const execution = { id: 'e9', status: 'Queued' } as WorkflowExecution;
    mocks.executeWorkflow.mockResolvedValue(execution);
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByLabelText(/Your brief/), { target: { value: '15 s vertical teaser' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    await waitFor(() => expect(mocks.executeWorkflow).toHaveBeenCalledWith('p1', 'w1', null, false, {}, undefined, undefined, { notes: '15 s vertical teaser' }));
    // The new run is inserted into the cached run list immediately…
    expect(mocks.mutateExecutions).toHaveBeenCalled();
    const updater = mocks.mutateExecutions.mock.calls[0][0] as (l: WorkflowExecution[] | undefined) => WorkflowExecution[];
    expect(updater([]).map((e) => e.id)).toEqual(['e9']);
    // …and the page moves to its live progress view.
    expect(mocks.push).toHaveBeenCalledWith('/projects/p1/workflows/w1/executions/e9');
  });

  it('runs in the chosen shape in one click, with no style unless one is picked', async () => {
    mocks.executeWorkflow.mockResolvedValue({ id: 'e10', status: 'Queued' } as WorkflowExecution);
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByText('1:1'));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    await waitFor(() =>
      expect(mocks.executeWorkflow).toHaveBeenCalledWith('p1', 'w1', null, false, {}, undefined, undefined, { format: 'Square' }),
    );
  });

  it('runs at the chosen resolution, and sends none for Auto', async () => {
    mocks.executeWorkflow.mockResolvedValue({ id: 'e11', status: 'Queued' } as WorkflowExecution);
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByText('1080p'));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    await waitFor(() =>
      expect(mocks.executeWorkflow).toHaveBeenCalledWith('p1', 'w1', null, false, {}, undefined, undefined, { resolution: '1080p' }),
    );
  });
});

/**
 * B10c-UI: the run's expected cost, and what the page does when the balance cannot start it. The
 * figures all come from B7's estimate endpoint, so these cases pin what is shown for each shape of
 * that answer — including the two that must show nothing at all.
 */
const estimateMock = vi.mocked(useWorkflowCostEstimate);

/** What the estimate hook hands back; only the fields a case is about vary. */
const estimateResult = (data?: Record<string, unknown>) =>
  ({ data, error: undefined, isLoading: false, mutate: mocks.mutateCostEstimate }) as unknown as ReturnType<
    typeof useWorkflowCostEstimate
  >;

/** The answer from a backend that predates the typical figure: the range alone. */
const OLD_ESTIMATE = {
  billingEnabled: true,
  hasHistory: true,
  lowCredits: 30,
  highCredits: 52,
  requiredCredits: 52,
  balanceCredits: 400,
  affordable: true,
  steps: [],
};

const ESTIMATE = {
  ...OLD_ESTIMATE,
  typicalCredits: 40,
  fixedMaxCredits: 0,
  rangeBasis: 'PastRuns',
  pastRunCount: 6,
  spreadReasons: [],
};

/** The exact 402 body the execute preflight refuses an unaffordable run with (B7). */
const refusal = () =>
  new ApiError('This run needs about 52 credits and the workspace has 12 available.', 402, [], {
    code: 'INSUFFICIENT_CREDITS',
    required: 52,
    balance: 12,
    low: 30,
    high: 52,
    hasHistory: true,
  });

describe('the cost shown before Execute (B10c-UI)', () => {
  it('shows the range next to Run, from the same estimate the dialog shows', async () => {
    estimateMock.mockReturnValue(estimateResult(ESTIMATE));
    await renderPage();

    expect(screen.getByTestId('run-cost-chip').textContent).toBe('≈ 40 credits (usually 30–52)');
  });

  it('adds the generated-video ceiling to the chip as "up to"', async () => {
    estimateMock.mockReturnValue(estimateResult({ ...ESTIMATE, fixedMaxCredits: 320 }));
    await renderPage();

    expect(screen.getByTestId('run-cost-chip').textContent).toBe('≈ 40 credits (usually 30–52) + up to 320');
  });

  it('shows the plain range on the chip for an older backend without a typical figure', async () => {
    estimateMock.mockReturnValue(estimateResult(OLD_ESTIMATE));
    await renderPage();

    expect(screen.getByTestId('run-cost-chip').textContent).toBe('≈ 30–52 credits');
  });

  it('says so on the chip when the balance cannot start the run', async () => {
    estimateMock.mockReturnValue(estimateResult({ ...ESTIMATE, affordable: false, balanceCredits: 12 }));
    await renderPage();

    expect(screen.getByTestId('run-cost-chip').textContent).toBe('≈ 40 credits (usually 30–52), not enough credits yet');
  });

  it('shows the estimate inside the confirmation, before the run starts', async () => {
    estimateMock.mockReturnValue(estimateResult(ESTIMATE));
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    const dialogEstimate = within(await screen.findByTestId('run-cost-estimate'));
    expect(dialogEstimate.getByText(/≈ 40 credits \(usually 30–52\)/)).toBeTruthy();
    // The same figures sit on the chip behind the dialog, from the same single estimate.
    expect(screen.getByTestId('run-cost-chip')).toBeTruthy();
  });

  it('renders no cost chrome at all on a deployment without billing', async () => {
    estimateMock.mockReturnValue(estimateResult({ billingEnabled: false, hasHistory: false, steps: [] }));
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    expect(screen.queryByTestId('run-cost-chip')).toBeNull();
    expect(screen.queryByTestId('run-cost-estimate')).toBeNull();
  });
});

describe('an unaffordable run (402)', () => {
  it('turns the refusal into the shortfall and a way to close it, and re-reads the balance', async () => {
    estimateMock.mockReturnValue(estimateResult(ESTIMATE));
    mocks.executeWorkflow.mockRejectedValue(refusal());
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await screen.findByTestId('insufficient-credits');
    expect(screen.getByText(/52 credits up front/)).toBeTruthy();
    expect(screen.getByText(/12 available/)).toBeTruthy();
    expect(screen.getByText(/40 credits short/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /Top up credits/ }).getAttribute('href')).toBe('/settings/billing');
    // The refusal was read at a balance that has now moved (or has not), so it is not reused as-is.
    expect(mocks.mutateCostEstimate).toHaveBeenCalled();
    // Nothing was queued, so the page stays where it is.
    expect(mocks.push).not.toHaveBeenCalled();
  });

  it('shows the server message verbatim when the refusal carries one, rather than composing its own', async () => {
    const message =
      "This run needs up to 52 credits to start (it usually costs about 40); the workspace has 12 available, so 40 more are needed. Credits that aren't used are returned when the run finishes.";
    estimateMock.mockReturnValue(estimateResult(ESTIMATE));
    mocks.executeWorkflow.mockRejectedValue(
      new ApiError(message, 402, [], {
        code: 'INSUFFICIENT_CREDITS', message, required: 52, balance: 12, missing: 40, low: 30, high: 52, hasHistory: true,
      }),
    );
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await screen.findByTestId('insufficient-credits');
    expect(screen.getByText(/needs up to 52 credits to start \(it usually costs about 40\)/)).toBeTruthy();
    expect(screen.queryByText(/credits up front/)).toBeNull();
  });

  it('clears the refusal on the next attempt, so a stale shortfall is never left on screen', async () => {
    estimateMock.mockReturnValue(estimateResult(ESTIMATE));
    mocks.executeWorkflow.mockRejectedValue(refusal());
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));
    await screen.findByTestId('insufficient-credits');

    mocks.executeWorkflow.mockRejectedValue(new Error('service unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await waitFor(() => expect(screen.queryByTestId('insufficient-credits')).toBeNull());
  });

  it('leaves any other failure as a plain error, with no credits state invented for it', async () => {
    estimateMock.mockReturnValue(estimateResult(ESTIMATE));
    mocks.executeWorkflow.mockRejectedValue(new Error('service unavailable'));
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await waitFor(() => expect(mocks.executeWorkflow).toHaveBeenCalled());
    expect(screen.queryByTestId('insufficient-credits')).toBeNull();
    expect(mocks.mutateCostEstimate).not.toHaveBeenCalled();
  });
});

describe('the accept-the-estimate step', () => {
  const NOT_ACCEPTED = () =>
    new ApiError('Accept an estimate of at least 231 credits to start this build.', 409, [], {
      code: 'ESTIMATE_NOT_ACCEPTED',
      message: 'Accept an estimate of at least 231 credits to start this build.',
      estimate: { low: 12, high: 231 },
      capCredits: 300,
    });

  async function startRun() {
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));
  }

  it('409 NOT_ACCEPTED opens the modal; Confirm resends the same run with the high figure', async () => {
    mocks.executeWorkflow.mockRejectedValueOnce(NOT_ACCEPTED()).mockResolvedValueOnce({ id: 'ex9' });
    await startRun();

    expect(await screen.findByText(/between 12 and 231 credits/)).toBeTruthy();
    expect(mocks.push).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(mocks.executeWorkflow).toHaveBeenCalledTimes(2));
    const [first, second] = mocks.executeWorkflow.mock.calls;
    expect(first).toHaveLength(8);
    expect(second[5]).toBe(231);
    expect([second[0], second[1], second[2], second[3], second[4], second[7]]).toEqual([first[0], first[1], first[2], first[3], first[4], first[7]]);
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith('/projects/p1/workflows/w1/executions/ex9'));
  });

  it('409 OVER_CAP shows the estimate against the cap and no Confirm', async () => {
    mocks.executeWorkflow.mockRejectedValue(
      new ApiError('over', 409, [], {
        code: 'ESTIMATE_OVER_CAP',
        message: 'over',
        estimate: { low: 40, high: 420 },
        capCredits: 300,
      }),
    );
    await startRun();
    expect((await screen.findByTestId('estimate-gate-over-cap')).textContent).toContain('300');
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
    expect(screen.getByRole('link', { name: /Brand & product/ }).getAttribute('href')).toBe('/projects/p1?tab=profile');
  });

  it('503 UNAVAILABLE says to try again shortly', async () => {
    mocks.executeWorkflow.mockRejectedValue(
      new ApiError('unavailable', 503, [], { code: 'ESTIMATE_UNAVAILABLE', message: 'unavailable' }),
    );
    await startRun();
    expect(await screen.findByText(/try again shortly/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
  });

  it('an ordinary run still calls executeWorkflow once, with no figure, and opens the run', async () => {
    mocks.executeWorkflow.mockResolvedValue({ id: 'ex1' });
    await startRun();
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith('/projects/p1/workflows/w1/executions/ex1'));
    expect(mocks.executeWorkflow).toHaveBeenCalledTimes(1);
    expect(mocks.executeWorkflow.mock.calls[0]![5]).toBeUndefined();
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
  });
});
