// @vitest-environment jsdom
/**
 * The workflow route (spec 4.3 and 4.5): with the app view on it is the app run page and the graph
 * builder sits behind `?view=advanced`; with `WORKFLOW_APPS_ENABLED` off it is today's builder page,
 * unchanged, whatever the query says.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { WorkflowAppsFlagProvider } from '@/components/shell/WorkflowAppsFlag';
import type { WorkflowDefinition } from '@/lib/types/workflow';

const env = vi.hoisted(() => ({ view: null as string | null }));

const WORKFLOW = { id: 'w1', name: 'Spring reel', requiresUserInput: false, steps: [] } as unknown as WorkflowDefinition;

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn() }),
  usePathname: () => '/projects/p1/workflows/w1',
  useSearchParams: () => new URLSearchParams(env.view ? `view=${env.view}` : ''),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflow: () => ({ data: WORKFLOW, isLoading: false, mutate: vi.fn() }) }));
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

async function renderRoute(enabled: boolean | null) {
  const value = { id: 'p1', workflowId: 'w1' };
  const params = Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });
  const page = <WorkflowRoute params={params} />;
  await act(async () => {
    render(<MantineProvider env="test">{enabled === null ? page : <WorkflowAppsFlagProvider enabled={enabled}>{page}</WorkflowAppsFlagProvider>}</MantineProvider>);
  });
}

describe('workflow route', () => {
  it('is the app page by default', async () => {
    env.view = null;
    await renderRoute(true);
    expect(screen.getByText('The app page')).toBeTruthy();
    expect(screen.queryByText('The step builder')).toBeNull();
  });

  it('mounts the existing builder behind ?view=advanced, with a way back to the app', async () => {
    env.view = 'advanced';
    await renderRoute(true);
    expect(screen.getByText('The step builder')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to the app' }).getAttribute('href')).toBe('/projects/p1/workflows/w1');
  });

  it('with the flag off it is today\'s builder page, whatever the query says', async () => {
    env.view = null;
    await renderRoute(false);
    expect(screen.getByText('The step builder')).toBeTruthy();
    expect(screen.queryByText('The app page')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Back to the app' })).toBeNull();
    cleanup();
    env.view = 'advanced';
    await renderRoute(false);
    expect(screen.queryByRole('link', { name: 'Back to the app' })).toBeNull();
  });
});
