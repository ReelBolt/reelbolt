// @vitest-environment jsdom
/** The workflow page: a failed load says what happened and offers Retry / a way back. */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { fireEvent } from '@testing-library/react';
import { ApiError } from '@/lib/api/client';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { WorkflowAppsFlagProvider } from '@/components/shell/WorkflowAppsFlag';
import type { WorkflowDefinition } from '@/lib/types/workflow';

const env = vi.hoisted(() => ({ view: null as string | null, workflow: undefined as unknown, error: undefined as unknown, mutate: (() => undefined) as () => unknown }));

const WORKFLOW = { id: 'w1', name: 'Spring reel', requiresUserInput: false, steps: [] } as unknown as WorkflowDefinition;

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn() }),
  usePathname: () => '/projects/p1/workflows/w1',
  useSearchParams: () => new URLSearchParams(env.view ? `view=${env.view}` : ''),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflow: () => ({ data: env.workflow, error: env.error, isLoading: false, mutate: env.mutate }) }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p1', name: 'Spring launch' } }) }));
vi.mock('@/lib/hooks/use-executions', () => ({ useExecutions: () => ({ data: [], mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-styles', () => ({ useStylePresets: () => ({ data: [] }), useEditingStyles: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => ({ data: [], isLoading: false }) }));
vi.mock('@/components/workflows/FlowchartBuilder', () => ({ FlowchartBuilderWrapper: () => <div>The step builder</div> }));
vi.mock('@/components/workflows/ExecutionHistory', () => ({ ExecutionHistory: () => null }));
vi.mock('@/components/workflow-apps/AppRunPage', () => ({ AppRunPage: () => <div>The app page</div> }));
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

import WorkflowRoute from './page';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
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
afterEach(cleanup);

async function renderRoute() {
  const value = { id: 'p1', workflowId: 'w1' };
  const params = Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });
  await act(async () => {
    render(<MantineProvider env="test"><WorkflowAppsFlagProvider enabled={false}><WorkflowRoute params={params} /></WorkflowAppsFlagProvider></MantineProvider>);
  });
}

describe('workflow page failures', () => {
  it('500 and network: plain sentence, Retry re-reads, way back', async () => {
    for (const err of [new ApiError('raw boom', 500), new TypeError('Failed to fetch')]) {
      const mutate = vi.fn();
      Object.assign(env, { workflow: undefined, error: err, mutate });
      await renderRoute();
      expect(screen.getByTestId('failure-card')).toBeTruthy();
      expect(screen.queryByText('Workflow not found')).toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
      expect(mutate).toHaveBeenCalledTimes(1);
      expect(screen.getByRole('link', { name: 'Back to the dashboard' })).toBeTruthy();
      cleanup();
    }
  });
  it('404 says not found; 403 does not; neither offers Retry', async () => {
    for (const status of [404, 403]) {
      Object.assign(env, { workflow: undefined, error: new ApiError('x', status), mutate: vi.fn() });
      await renderRoute();
      expect(screen.getByTestId('failure-card').getAttribute('data-kind')).toBe(status === 404 ? 'notFound' : 'forbidden');
      expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
      expect(screen.getByRole('link', { name: 'Back to the dashboard' })).toBeTruthy();
      cleanup();
    }
  });
  it('success: no failure card', async () => {
    Object.assign(env, { workflow: WORKFLOW, error: undefined, mutate: vi.fn() });
    await renderRoute();
    expect(screen.queryByTestId('failure-card')).toBeNull();
  });
});
