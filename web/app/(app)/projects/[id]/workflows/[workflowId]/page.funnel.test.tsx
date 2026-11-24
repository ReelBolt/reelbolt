// @vitest-environment jsdom
/**
 * The `alpha.first_run` call-site (alpha-launch T-018). What this protects: the funnel event is
 * emitted from the REAL transition — the API accepted the run — and not on a page load.
 * `emitFunnelEvent` enforces once-per-user and the consent gate (see
 * `web/lib/telemetry/funnel.test.ts`); this test proves the page calls it at all, on the transition.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { userKeyFromEmail } from '@/lib/telemetry';
import type { WorkflowDefinition, WorkflowExecution } from '@/lib/types/workflow';

const mocks = vi.hoisted(() => ({
  emitFunnelEvent: vi.fn(),
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

const ESTIMATE = {
  billingEnabled: false,
  hasHistory: false,
  lowCredits: 0,
  highCredits: 0,
  requiredCredits: 0,
  balanceCredits: 0,
  affordable: true,
  steps: [],
};

vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  emitFunnelEvent: mocks.emitFunnelEvent,
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: mocks.push }), usePathname: () => '/projects/p1/workflows/w1', useSearchParams: () => new URLSearchParams('view=advanced') }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'Ada@Example.com' } }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflow: () => ({ data: WORKFLOW, isLoading: false, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p1', name: 'Spring launch' } }) }));
vi.mock('@/lib/hooks/use-executions', () => ({ useExecutions: () => ({ data: [], mutate: mocks.mutateExecutions }) }));
vi.mock('@/lib/hooks/use-styles', () => ({ useStylePresets: () => ({ data: [] }), useEditingStyles: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({
    data: [
      { id: 'vt', agentType: 'VideoTransform' },
      { id: 'ed', agentType: 'VideoStoryEditor' },
    ],
    isLoading: false,
  }),
}));
vi.mock('@/lib/api/workflows', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/workflows')>()),
  executeWorkflow: mocks.executeWorkflow,
}));
vi.mock('@/components/workflows/FlowchartBuilder', () => ({ FlowchartBuilderWrapper: () => null }));
vi.mock('@/components/workflows/ExecutionHistory', () => ({ ExecutionHistory: () => null }));
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-billing')>();
  return {
    ...actual,
    useWorkflowCostEstimate: () => ({ data: ESTIMATE, error: undefined, isLoading: false, mutate: mocks.mutateCostEstimate }),
    useOutputHeightCap: () => null,
  };
});
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistant: () => ({ opened: false, open: vi.fn() }) }));
vi.mock('@/components/assistant/AssistantPageContext', () => ({
  AssistantPageContext: () => null,
  useAssistantPageContext: () => undefined,
}));

import WorkflowEditPage from './page';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
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

beforeEach(() => {
  mocks.emitFunnelEvent.mockClear();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function renderPage() {
  const value = { id: 'p1', workflowId: 'w1' };
  const params = Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });
  await act(async () => {
    render(
      <MantineProvider env="test">
        <WorkflowEditPage params={params} />
      </MantineProvider>,
    );
  });
}

describe('Workflow page funnel call-site', () => {
  it('does not emit anything merely because the page rendered', async () => {
    await renderPage();
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
  });

  it('emits alpha.first_run once the API accepted the run', async () => {
    mocks.executeWorkflow.mockResolvedValue({ id: 'e9', status: 'Queued' } as WorkflowExecution);
    await renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await waitFor(() => expect(mocks.executeWorkflow).toHaveBeenCalledWith('p1', 'w1', null, false, {}, undefined, undefined, {}));
    await waitFor(() =>
      expect(mocks.emitFunnelEvent).toHaveBeenCalledWith('alpha.first_run', userKeyFromEmail('Ada@Example.com')),
    );
  });

  it('emits nothing when the run was refused', async () => {
    mocks.executeWorkflow.mockRejectedValue(new Error('nope'));
    await renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await waitFor(() => expect(mocks.executeWorkflow).toHaveBeenCalled());
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
  });
});
