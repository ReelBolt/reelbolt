// @vitest-environment jsdom
/**
 * The App | Advanced control on the workflow page (docs/design/workflow-apps.md 4.5, 4.6): the radiogroup
 * on a desktop viewport, the overflow entry on a phone, and the refusal that names an orphaned input.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowAppResponse, WorkflowDefinition } from '@/lib/types/workflow';
import { ApiError } from '@/lib/api/client';

const mocks = vi.hoisted(() => ({
  updateWorkflow: vi.fn(),
  mutateWorkflow: vi.fn(),
  push: vi.fn(),
  desktop: true,
}));

const WORKFLOW = {
  id: 'w1', name: 'Spring reel', requiresUserInput: false,
  steps: [
    { id: 's1', agentDefinitionId: 'vt', stepOrder: 1, label: '', stepType: 'VideoAnalyze' },
    { id: 's2', agentDefinitionId: 'ed', stepOrder: 2, label: '', stepType: 'Agent' },
  ],
} as unknown as WorkflowDefinition;

const APP: WorkflowAppResponse = {
  workflowId: 'w1', name: 'Spring reel', hasExplicitManifest: true, costClass: 'No credits', source: 'custom',
  manifest: { version: 1, title: 'Spring reel', inputs: [{ kind: 'brief', field: 'voice', label: 'Narration' }] },
};

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: mocks.push }),
  usePathname: () => '/projects/p1/workflows/w1',
  useSearchParams: () => new URLSearchParams(window.location.search),
}));
vi.mock('@/components/workflow-apps/AppRunPage', () => ({ AppRunPage: () => <div data-testid="app-run-page" /> }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({
  useWorkflow: () => ({ data: WORKFLOW, isLoading: false, mutate: mocks.mutateWorkflow }),
}));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p1', name: 'Spring launch' } }) }));
vi.mock('@/lib/hooks/use-executions', () => ({ useExecutions: () => ({ data: [], mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-styles', () => ({ useStylePresets: () => ({ data: [] }), useEditingStyles: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({ data: [{ id: 'vt', agentType: 'VideoTransform' }, { id: 'ed', agentType: 'VideoStoryEditor' }], isLoading: false }),
}));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({ useWorkflowApp: () => ({ data: APP, error: undefined, isLoading: false }) }));
vi.mock('@/lib/workflow-apps/readable-fields', () => ({ readableBriefFields: vi.fn().mockResolvedValue([]) }));
vi.mock('@/lib/api/workflows', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/workflows')>()),
  updateWorkflow: mocks.updateWorkflow,
}));
vi.mock('@/components/workflows/FlowchartBuilder', () => ({
  FlowchartBuilderWrapper: () => <div data-testid="builder" />,
}));
vi.mock('@/components/workflows/ExecutionHistory', () => ({ ExecutionHistory: () => <div /> }));
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/hooks/use-billing')>()),
  useWorkflowCostEstimate: () => ({ data: undefined, error: undefined, isLoading: false, mutate: vi.fn() }),
  useOutputHeightCap: () => null,
}));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistant: () => ({ opened: false, open: vi.fn() }) }));
vi.mock('@/components/assistant/AssistantPageContext', () => ({
  AssistantPageContext: () => null,
  useAssistantPageContext: () => undefined,
}));

import WorkflowEditPage from './page';
import { WorkflowAppsFlagProvider } from '@/components/shell/WorkflowAppsFlag';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: mocks.desktop && query.includes('min-width'),
      media: query, onchange: null,
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

beforeEach(() => {
  mocks.desktop = true;
  window.localStorage.clear();
  window.history.replaceState(null, '', '/projects/p1/workflows/w1');
  mocks.updateWorkflow.mockResolvedValue(WORKFLOW);
  mocks.mutateWorkflow.mockResolvedValue(WORKFLOW);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

async function renderPage(enabled = true) {
  const value = { id: 'p1', workflowId: 'w1' };
  const params = Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });
  await act(async () => {
    render(<MantineProvider env="test"><WorkflowAppsFlagProvider enabled={enabled}><WorkflowEditPage params={params} /></WorkflowAppsFlagProvider></MantineProvider>);
  });
}

describe('workflow page view control', () => {
  it('opens on the app with no ?view and nothing remembered, and syncs ?view= from the radiogroup', async () => {
    await renderPage();
    expect(screen.getByRole('radiogroup', { name: 'Workflow view' })).toBeTruthy();
    expect(screen.getByTestId('app-run-page')).toBeTruthy();
    expect(screen.queryByTestId('builder')).toBeNull();

    fireEvent.click(screen.getByRole('radio', { name: 'Advanced' }));
    expect(window.location.search).toBe('?view=advanced');
    expect(screen.getByTestId('builder')).toBeTruthy();
    expect(screen.queryByTestId('app-run-page')).toBeNull();

    fireEvent.click(screen.getByRole('radio', { name: 'App' }));
    expect(window.location.search).toBe('?view=app');
    expect(screen.getByTestId('app-run-page')).toBeTruthy();
  });

  it('opens in the remembered view when the URL names none', async () => {
    const { workflowViewStorageKey } = await import('@/lib/workflow-apps/view-mode');
    const { userKeyFromEmail } = await import('@/lib/telemetry');
    window.localStorage.setItem(workflowViewStorageKey(userKeyFromEmail('ada@example.com')), 'advanced');
    await renderPage();
    expect(screen.getByTestId('builder')).toBeTruthy();
  });

  it('hides the control on a phone, opens on the app and states the current view', async () => {
    mocks.desktop = false;
    await renderPage();
    expect(screen.queryByRole('radiogroup')).toBeNull();
    expect(screen.getByTestId('app-run-page')).toBeTruthy();
    expect(screen.getByTestId('phone-view-state').textContent).toBe('Showing the app');
  });

  it('blocks a save that would orphan an exposed input and names it', async () => {
    window.history.replaceState(null, '', '/projects/p1/workflows/w1?view=advanced');
    mocks.updateWorkflow.mockRejectedValue(new ApiError(
      'The input "Narration" is not read by any step of this workflow, so it would be shown and ignored.',
      400,
      ['The input "Narration" is not read by any step of this workflow, so it would be shown and ignored.'],
      { code: 'APP_MANIFEST_INVALID' },
    ));
    await renderPage();
    fireEvent.change(screen.getByLabelText('Workflow Name'), { target: { value: 'Summer reel' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Save Changes' })); });

    // The builder (the only editor now) puts the refusal next to the input it names.
    const alerts = await screen.findAllByText(/"Narration" is not read by any step/);
    expect(alerts.length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByTestId('app-inputs-panel')).toBeTruthy());
  });

  it('shows no toggle and the builder when the app view is off', async () => {
    await renderPage(false);
    expect(screen.queryByRole('radiogroup')).toBeNull();
    expect(screen.getByTestId('builder')).toBeTruthy();
  });
});
