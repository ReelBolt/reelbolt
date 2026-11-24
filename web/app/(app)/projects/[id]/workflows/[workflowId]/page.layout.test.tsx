// @vitest-environment jsdom
/**
 * The builder's save and run-history affordances (workflow-builder-and-approval-ux T-001).
 *
 * What this protects: Save lives in the header dock so it is reachable without scrolling past the
 * canvas, it is honest about unsaved changes (disabled until an edit, disabled again after a save),
 * and run history keeps its source position after the form — which is what lets the desktop grid
 * promote it into a rail without reordering the document for mobile.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowDefinition } from '@/lib/types/workflow';

const mocks = vi.hoisted(() => ({
  updateWorkflow: vi.fn(),
  executeWorkflow: vi.fn(),
  mutateWorkflow: vi.fn(),
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

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: mocks.push }), usePathname: () => '/projects/p1/workflows/w1', useSearchParams: () => new URLSearchParams('view=advanced') }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'Ada@Example.com' } }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({
  useWorkflow: () => ({ data: WORKFLOW, isLoading: false, mutate: mocks.mutateWorkflow }),
}));
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
  updateWorkflow: mocks.updateWorkflow,
}));
vi.mock('@/components/workflows/FlowchartBuilder', () => ({ FlowchartBuilderWrapper: () => null }));
vi.mock('@/components/workflows/ExecutionHistory', () => ({
  ExecutionHistory: ({ compact }: { compact?: boolean }) => (
    <div data-testid="execution-history" data-compact={compact ? 'true' : 'false'} />
  ),
}));
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

import { announceAssistantActionDone, resetAnnouncedAssistantActions } from '@/lib/assistant/events';
import type { AssistantAction } from '@/lib/types/assistant';
import WorkflowEditPage from './page';

/** jsdom reports every element as 0px tall; the page's rail offset needs a real measurement. */
const originalOffsetHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'offsetHeight');

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
  mocks.updateWorkflow.mockResolvedValue(WORKFLOW);
  mocks.mutateWorkflow.mockResolvedValue(WORKFLOW);
  resetAnnouncedAssistantActions();
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
    configurable: true,
    get(this: HTMLElement) {
      return this.dataset.testid === 'workflow-page-header' ? 116 : 0;
    },
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  if (originalOffsetHeight) Object.defineProperty(HTMLElement.prototype, 'offsetHeight', originalOffsetHeight);
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

describe('Workflow page layout', () => {
  it('puts Save in the header, ahead of the form it submits', async () => {
    await renderPage();

    const header = screen.getByTestId('workflow-page-header');
    const save = within(header).getByRole('button', { name: 'Save Changes' });
    const form = document.getElementById('workflow-edit-form');

    expect(form).toBeTruthy();
    expect(header.contains(form)).toBe(false);
    // The header is earlier in the document than the form, so the button is reachable without
    // scrolling past the canvas (which is what moving it out of the bottom group means).
    expect(header.compareDocumentPosition(form!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // It submits that form from outside it.
    expect(save.getAttribute('type')).toBe('submit');
    expect(save.getAttribute('form')).toBe('workflow-edit-form');
  });

  it('is disabled until the draft differs from what was last saved', async () => {
    await renderPage();

    const save = screen.getByRole('button', { name: 'Save Changes' });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(screen.getByLabelText('Workflow Name'), { target: { value: 'Summer reel' } });
    expect((save as HTMLButtonElement).disabled).toBe(false);
  });

  it('saves the edited name and the steps, then is disabled again', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Workflow Name'), { target: { value: 'Summer reel' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }));

    await waitFor(() => expect(mocks.updateWorkflow).toHaveBeenCalledTimes(1));
    expect(mocks.updateWorkflow).toHaveBeenCalledWith(
      'p1',
      'w1',
      expect.objectContaining({
        name: 'Summer reel',
        requiresUserInput: false,
        steps: [
          expect.objectContaining({ agentDefinitionId: 'vt', stepOrder: 1 }),
          expect.objectContaining({ agentDefinitionId: 'ed', stepOrder: 2 }),
        ],
      }),
    );
    // The save re-baselines the draft, so the button reads clean again.
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Save Changes' }) as HTMLButtonElement).disabled).toBe(true),
    );
  });

  it('keeps run history in an aside after the form', async () => {
    await renderPage();

    const form = document.getElementById('workflow-edit-form');
    const history = screen.getByTestId('workflow-run-history');

    expect(history.tagName).toBe('ASIDE');
    expect(history.getAttribute('aria-label')).toBe('Run history');
    expect(form!.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // The narrow rail gets the stacked card; the wide row does not fit 380px.
    expect(screen.getByTestId('execution-history').getAttribute('data-compact')).toBe('true');
  });

  it('stays dirty when the save fails, so the edit is never silently dropped', async () => {
    mocks.updateWorkflow.mockRejectedValueOnce(new Error('service unavailable'));
    await renderPage();

    fireEvent.change(screen.getByLabelText('Workflow Name'), { target: { value: 'Summer reel' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }));

    await waitFor(() => expect(mocks.updateWorkflow).toHaveBeenCalledTimes(1));
    // `loading={saving}` also disables the button, so the assertion has to wait for the rejected
    // save's `finally` to settle; a wrongly moved baseline would stay disabled and time out.
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Save Changes' }) as HTMLButtonElement).disabled).toBe(false),
    );
    expect((screen.getByLabelText('Workflow Name') as HTMLInputElement).value).toBe('Summer reel');
  });

  it('takes a server adoption as the new clean baseline, name and brief switch included', async () => {
    const adopted = { ...WORKFLOW, name: 'Autumn reel', requiresUserInput: true } as typeof WORKFLOW;
    mocks.mutateWorkflow.mockResolvedValue(adopted);
    await renderPage();

    // Typing makes the page dirty; the assistant then changes the same workflow.
    fireEvent.change(screen.getByLabelText('Workflow Name'), { target: { value: 'Summer reel' } });
    await act(async () => {
      announceAssistantActionDone({
        id: 'adopt-1',
        state: 'done',
        mode: 'ask',
        kind: 'workflow',
        risk: 'normal',
        title: 'Changed the workflow',
        changes: [],
        refresh: { workflowId: 'w1' },
      } as AssistantAction);
    });

    await waitFor(() => expect((screen.getByLabelText('Workflow Name') as HTMLInputElement).value).toBe('Autumn reel'));
    // The switch is adopted with the name, so the baseline matches what the page now shows.
    expect((screen.getByLabelText(/Require a brief/) as HTMLInputElement).checked).toBe(true);
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Save Changes' }) as HTMLButtonElement).disabled).toBe(true),
    );
  });

  it('hands the rail the dock height it measured', async () => {
    await renderPage();

    expect(screen.getByTestId('workflow-layout').style.getPropertyValue('--workflow-dock-height')).toBe('116px');
  });
});
