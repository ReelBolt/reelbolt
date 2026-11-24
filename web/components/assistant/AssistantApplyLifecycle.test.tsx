// @vitest-environment jsdom

/**
 * Regression tests for the assistant proposal lifecycle on the real workflow edit page:
 * a proposal that was applied, discarded or superseded must never come back when the drawer
 * remounts its messages, a 409 on apply must clear the stale preview, and a double click must
 * send one request. Drives real `data-workflowDiff` SSE chunks through the real
 * AssistantDrawer + WorkflowDiffExtractor; only the network (`fetch`, `applyWorkflow`) and the
 * page's heavy, unrelated children are mocked.
 */

import { Suspense } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { WorkflowAssistantApplyResponse, WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import type { WorkflowDefinition } from '@/lib/types/workflow';
import { ApiError } from '@/lib/api/client';
import { announceAssistantActionDone, resetAnnouncedAssistantActions } from '@/lib/assistant/events';
import type { AssistantAction } from '@/lib/types/assistant';

const mocks = vi.hoisted(() => ({
  applyWorkflow: vi.fn(),
  notify: vi.fn(),
  mutate: vi.fn(),
  push: vi.fn(),
}));

vi.mock('@/lib/api/workflow-assistant', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/workflow-assistant')>()),
  applyWorkflow: mocks.applyWorkflow,
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: mocks.notify } }));
vi.mock('next/navigation', () => {
  const router = { push: mocks.push };
  return { useRouter: () => router, usePathname: () => '/projects/p1/workflows/w1', useSearchParams: () => new URLSearchParams('view=advanced') };
});
vi.mock('@/lib/hooks/use-workflows', () => ({
  useWorkflow: () => ({ data: WORKFLOW, isLoading: false, mutate: mocks.mutate }),
}));
// Renders the step labels it is handed, so a test can see which steps the page drew, and lets a
// test type into the canvas (add a step the server has never heard of).
vi.mock('@/components/workflows/FlowchartBuilder', () => ({
  FlowchartBuilderWrapper: ({
    steps,
    onChange,
    pendingAssistantDiff,
  }: {
    steps: Array<{ label: string }>;
    onChange: (next: Array<{ label: string }>) => void;
    pendingAssistantDiff?: { confirmationToken: string };
  }) => (
    <div>
      <ul data-testid="flowchart-steps">
        {steps.map((s, i) => (
          <li key={i}>{s.label}</li>
        ))}
      </ul>
      <span data-testid="canvas-diff">{pendingAssistantDiff?.confirmationToken ?? 'none'}</span>
      <button type="button" onClick={() => onChange([...steps, { label: 'Local edit' }])}>
        edit the canvas
      </button>
    </div>
  ),
}));
// The page also reads the project (breadcrumb/tab title), its runs and the agent list (run
// confirmation). Answered here so those SWR fetches never consume a queued chat reply.
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p', name: 'My project' } }) }));
vi.mock('@/lib/hooks/use-executions', () => ({ useExecutions: () => ({ data: [], mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => ({ data: [], isLoading: false }) }));
vi.mock('@/components/workflows/ExecutionHistory', () => ({ ExecutionHistory: () => null }));
vi.mock('@/components/workflow-apps/RunWithInputsModal', () => ({ RunWithInputsModal: () => null }));
// B10c-UI: the page reads B7's cost estimate for the next run on mount. Answered here like the
// other page hooks — this file's `fetch` is a QUEUE of canned chat replies, and a live estimate
// request would consume one of them and starve the conversation under test.
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-billing')>();
  return {
    ...actual,
    useWorkflowCostEstimate: () => ({ data: undefined, error: undefined, isLoading: false, mutate: vi.fn() }),
  };
});

// Imported after the mocks are registered (vi.mock is hoisted anyway; this keeps intent obvious).
import WorkflowEditPage from '@/app/(app)/projects/[id]/workflows/[workflowId]/page';
import { AssistantShellProvider } from './AssistantShellProvider';

const PROJECT_ID = '12345678-1234-1234-1234-123456789012';
const WORKFLOW_ID = '87654321-4321-4321-4321-210987654321';
const THREAD_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const OTHER_WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';
const NEW_WORKFLOW_ID = '99999999-8888-7777-6666-555555555555';

const WORKFLOW = {
  id: WORKFLOW_ID,
  projectId: PROJECT_ID,
  name: 'My workflow',
  requiresUserInput: false,
  steps: [],
} as unknown as WorkflowDefinition;

/** What the apply endpoint really returns: a summary, never a WorkflowDefinition. */
const APPLY_SUMMARY: WorkflowAssistantApplyResponse = {
  workflowId: WORKFLOW_ID,
  name: 'My workflow',
  stepCount: 1,
};

/** The workflow the server hands back when the page refreshes after an apply. */
const FRESH_WORKFLOW = {
  ...WORKFLOW,
  steps: [
    {
      id: 'step-1',
      workflowDefinitionId: WORKFLOW_ID,
      agentDefinitionId: 'agent-1',
      stepOrder: 1,
      label: 'Fresh step from server',
      stepType: 'Agent',
    },
  ],
} as unknown as WorkflowDefinition;

type Notice = { title: string; message: string; color?: string };
const notices = () => mocks.notify.mock.calls.map((c) => c[0] as Notice);

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

function makeDiff(token: string, over: Partial<WorkflowEditDiff> = {}): WorkflowEditDiff {
  return {
    additions: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: `Added for ${token}` }],
    removals: [],
    modifications: [],
    confirmationToken: token,
    proposedSteps: [],
    ...over,
  };
}

let replyCount = 0;
function sseWithDiff(text: string, diff: WorkflowEditDiff | null, frames: unknown[] = []): string {
  replyCount += 1;
  return [
    `data: {"type":"start","messageId":"msg_${replyCount}"}`,
    '',
    // Frames the reply carries before its text, in the order the server sent them.
    ...frames.flatMap((data) => [`data: ${JSON.stringify({ type: 'data-assistantAction', data })}`, '']),
    `data: {"type":"text-start","id":"txt_${replyCount}"}`,
    '',
    `data: ${JSON.stringify({ type: 'text-delta', id: `txt_${replyCount}`, delta: text, textDelta: text })}`,
    '',
    `data: {"type":"text-end","id":"txt_${replyCount}"}`,
    '',
    ...(diff ? [`data: ${JSON.stringify({ type: 'data-workflowDiff', data: diff })}`, ''] : []),
    'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
    '',
    'data: [DONE]',
    '',
    '',
  ].join('\n');
}

/** Each chat request answers with the next queued reply. */
function queueReplies(replies: Array<{ text: string; diff: WorkflowEditDiff | null; frames?: unknown[] }>) {
  const bodies = replies.map((r) => sseWithDiff(r.text, r.diff, r.frames));
  const json = (payload: unknown) =>
    Promise.resolve(
      new Response(JSON.stringify(payload), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
  global.fetch = vi.fn<typeof fetch>((input, init) => {
    const url = String(input);
    // Thread management is not the chat stream. Opening the drawer now opens a conversation, so
    // these are answered here rather than allowed to eat a queued chat reply.
    if (url.endsWith('/api/v1/assistant/threads')) {
      if (init?.method === 'POST') return json({ id: THREAD_ID });
      return json([]);
    }
    // The turn-state probe a reopened thread makes (T-023): answered as an IDLE thread, so the client
    // does not re-attach — and, like the thread reads above, it must not eat a queued chat reply.
    if (/\/api\/v1\/assistant\/threads\/[^/]+\/turn$/.test(url)) return json({ state: 'idle' });
    if (/\/api\/v1\/assistant\/threads\/[^/]+$/.test(url)) {
      return json({ id: THREAD_ID, title: 'New chat', projectId: null, messages: [] });
    }
    // The page reads the workflow's app page (app-inputs switch); not a chat reply either.
    if (/\/api\/v1\/projects\/[^/]+\/workflows\/[^/]+\/app$/.test(url)) return json({});
    // The drawer reads the person's ask/auto setting when it opens; that is not a chat reply either.
    if (url.endsWith('/api/v1/assistant/preferences')) return json({ approvalMode: 'ask' });
    const body = bodies.shift();
    if (body === undefined) return Promise.reject(new Error('unexpected chat request'));
    return Promise.resolve(
      new Response(body, {
        status: 200,
        headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
      }),
    );
  }) as unknown as typeof fetch;
}

async function renderPage() {
  const value = { id: PROJECT_ID, workflowId: WORKFLOW_ID };
  // A pre-settled thenable: React's `use()` reads it synchronously instead of suspending.
  const params = Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });
  render(
    <MantineProvider env="test">
      {/* The assistant is mounted once for the whole platform, above the router — so the page is
          rendered inside the shell, exactly as `app/(app)/layout.tsx` renders it, and the drawer
          under test is the shell's own. */}
      <AssistantShellProvider>
        <Suspense fallback={null}>
          <WorkflowEditPage params={params} />
        </Suspense>
      </AssistantShellProvider>
    </MantineProvider>,
  );
  await screen.findByRole('button', { name: /^assistant/i });
}

const user = () => userEvent.setup();

async function openDrawer() {
  await user().click(screen.getByRole('button', { name: /^assistant/i }));
  return screen.findByTestId('assistant-thread');
}

async function closeDrawer() {
  // The panel's own close control, named distinctly from the trigger's open-state 'Close assistant'.
  await user().click(screen.getByRole('button', { name: 'Close assistant panel' }));
  await waitFor(() => expect(screen.queryByTestId('assistant-thread')).toBeNull());
}

async function ask(prompt: string, expectedReply: string) {
  const input = screen.getByRole('textbox', { name: 'Message the workflow assistant' });
  await user().type(input, prompt);
  await user().click(screen.getByRole('button', { name: 'Send' }));
  const thread = screen.getByTestId('assistant-thread');
  await within(thread).findByText(expectedReply, {}, { timeout: 10000 });
  await screen.findByTestId('apply-button');
}

/** Close and reopen the drawer, waiting until every past message has remounted. */
async function closeAndReopen(lastReply: string) {
  await closeDrawer();
  const thread = await openDrawer();
  await within(thread).findByText(lastReply, {}, { timeout: 10000 });
}

describe('Assistant proposal lifecycle on the workflow page', () => {
  beforeEach(() => {
    replyCount = 0;
    global.ResizeObserver = MockResizeObserver;
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
    Element.prototype.scrollTo = vi.fn();
    mocks.applyWorkflow.mockReset();
    mocks.notify.mockReset();
    mocks.mutate.mockReset();
    mocks.mutate.mockResolvedValue(FRESH_WORKFLOW);
    mocks.push.mockReset();
    resetAnnouncedAssistantActions();
  });

  afterEach(() => {
    cleanup();
  });

  it('an applied proposal stays gone after the drawer is closed and reopened', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.applyWorkflow.mockResolvedValue(APPLY_SUMMARY);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    await closeAndReopen('Proposal A ready.');
    expect(screen.queryByTestId('apply-button')).toBeNull();
    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
    expect(mocks.applyWorkflow.mock.calls[0][4]).toBe('token-A');
  }, 30000);

  it('a discarded proposal, and the older one it superseded, never come back', async () => {
    queueReplies([
      { text: 'Proposal A ready.', diff: makeDiff('token-A') },
      { text: 'Proposal B ready.', diff: makeDiff('token-B') },
    ]);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    await ask('Actually, a different step', 'Proposal B ready.');

    await user().click(screen.getByTestId('discard-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    await closeAndReopen('Proposal B ready.');
    expect(screen.queryByTestId('apply-button')).toBeNull();
    expect(screen.queryByTestId('discard-button')).toBeNull();
  }, 30000);

  it('an applied proposal does not let the older, superseded one resurface', async () => {
    queueReplies([
      { text: 'Proposal A ready.', diff: makeDiff('token-A') },
      { text: 'Proposal B ready.', diff: makeDiff('token-B') },
    ]);
    mocks.applyWorkflow.mockResolvedValue(APPLY_SUMMARY);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    await ask('Actually, a different step', 'Proposal B ready.');

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    await closeAndReopen('Proposal B ready.');
    expect(screen.queryByTestId('apply-button')).toBeNull();
    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
    expect(mocks.applyWorkflow.mock.calls[0][4]).toBe('token-B');
  }, 30000);

  it('an unapplied pending proposal still shows after close/reopen and applies with its own token', async () => {
    queueReplies([
      { text: 'Proposal A ready.', diff: makeDiff('token-A') },
      { text: 'Proposal B ready.', diff: makeDiff('token-B') },
    ]);
    mocks.applyWorkflow.mockResolvedValue(APPLY_SUMMARY);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    await ask('Actually, a different step', 'Proposal B ready.');

    await closeAndReopen('Proposal B ready.');
    await user().click(await screen.findByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());
    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
    expect(mocks.applyWorkflow.mock.calls[0][4]).toBe('token-B');
  }, 30000);

  it('a 409 on apply clears the stale preview and explains it in plain words', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.applyWorkflow.mockRejectedValue(new ApiError('ConfirmationTokenMismatch: stale hash 9f2c', 409));
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(mocks.notify).toHaveBeenCalledTimes(1);
    const shown = mocks.notify.mock.calls[0][0] as { title: string; message: string };
    expect(shown.title).toBe('Proposal out of date');
    expect(shown.message).toMatch(/ask the assistant again/i);
    expect(shown.message).not.toMatch(/ConfirmationTokenMismatch|9f2c/);

    await closeAndReopen('Proposal A ready.');
    expect(screen.queryByTestId('apply-button')).toBeNull();
    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
  }, 30000);

  it('two rapid Apply clicks send exactly one request, with the button busy throughout', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    let resolveApply: (summary: WorkflowAssistantApplyResponse) => void = () => {};
    mocks.applyWorkflow.mockImplementation(
      () =>
        new Promise<WorkflowAssistantApplyResponse>((resolve) => {
          resolveApply = resolve;
        }),
    );
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');

    const apply = screen.getByTestId('apply-button');
    // Both clicks land before React re-renders, so a state-only guard would let both through.
    act(() => {
      apply.click();
      apply.click();
    });

    await waitFor(() => expect((screen.getByTestId('apply-button') as HTMLButtonElement).disabled).toBe(true));
    expect(screen.getByTestId('apply-button').hasAttribute('data-loading')).toBe(true);
    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);

    await act(async () => {
      resolveApply(APPLY_SUMMARY);
    });
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());
    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
  }, 30000);

  it('a successful Apply shows Applied, clears the preview and redraws steps from the refreshed workflow', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.applyWorkflow.mockResolvedValue(APPLY_SUMMARY);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    expect(screen.queryByText('Fresh step from server')).toBeNull();

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
    expect(mocks.mutate).toHaveBeenCalled();
    expect(notices().filter((n) => n.color === 'red')).toEqual([]);
    expect(notices()).toContainEqual(expect.objectContaining({ title: 'Applied', color: 'green' }));
    expect(within(screen.getByTestId('flowchart-steps')).getByText('Fresh step from server')).toBeTruthy();
  }, 30000);

  it.each([
    ['fails', () => mocks.mutate.mockRejectedValue(new Error('network down'))],
    ['returns no data', () => mocks.mutate.mockResolvedValue(undefined)],
  ])('when the refresh after a successful Apply %s, the preview still clears with no red error', async (_label, arrange) => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.applyWorkflow.mockResolvedValue(APPLY_SUMMARY);
    arrange();
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
    expect(notices().filter((n) => n.color === 'red' || n.title === 'Error')).toEqual([]);
    expect(notices().length).toBeGreaterThan(0);

    await closeAndReopen('Proposal A ready.');
    expect(screen.queryByTestId('apply-button')).toBeNull();
  }, 30000);

  // --- The "proposal is old" bug --------------------------------------------------------------
  // The token covers the workflow's CURRENT steps, so once the assistant had applied a proposal
  // itself, the tray's copy of it could never verify — although every change in it was already in.

  it('pressing Apply after the assistant already applied the same changes is a success, not "out of date"', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    // What the server now answers for a token that no longer verifies when the steps are already in.
    mocks.applyWorkflow.mockResolvedValue({ ...APPLY_SUMMARY, alreadyApplied: true });
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(notices()).toEqual([
      { title: 'Already applied', message: 'Those changes were already in your workflow.', color: 'blue' },
    ]);
    // The canvas is brought up to what the server holds, so the person sees the changes that are in.
    expect(mocks.mutate).toHaveBeenCalled();
    expect(within(screen.getByTestId('flowchart-steps')).getByText('Fresh step from server')).toBeTruthy();
  }, 30000);

  it('applies a proposal to the workflow it names, not to the one on screen', async () => {
    queueReplies([
      { text: 'Proposal A ready.', diff: makeDiff('token-A', { workflowId: OTHER_WORKFLOW_ID, name: 'Launch teaser' }) },
    ]);
    mocks.applyWorkflow.mockResolvedValue({ workflowId: OTHER_WORKFLOW_ID, name: 'Launch teaser', stepCount: 1 });
    await renderPage();
    await openDrawer();
    await ask('Add a step to the teaser', 'Proposal A ready.');

    // The tray says whose it is, and the canvas is not decorated with another workflow's ghost steps.
    expect(screen.getByTestId('canvas-diff').textContent).toBe('none');
    expect(screen.getByText(/Changes “Launch teaser”/)).toBeTruthy();

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1);
    const [project, workflowId, name, , token] = mocks.applyWorkflow.mock.calls[0];
    expect([project, workflowId, name, token]).toEqual([PROJECT_ID, OTHER_WORKFLOW_ID, 'Launch teaser', 'token-A']);
    // The page's own workflow was not touched, so it is not refetched or redrawn.
    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(screen.queryByText('Fresh step from server')).toBeNull();
    expect(notices()).toEqual([
      { title: 'Applied', message: 'Assistant changes were applied to “Launch teaser”.', color: 'green' },
    ]);
  }, 30000);

  it('creates a new workflow for a proposal that names none, and opens it', async () => {
    queueReplies([
      { text: 'Proposal A ready.', diff: makeDiff('token-A', { workflowId: null, name: 'Brand new' }) },
    ]);
    mocks.applyWorkflow.mockResolvedValue({ workflowId: NEW_WORKFLOW_ID, name: 'Brand new', stepCount: 1 });
    await renderPage();
    await openDrawer();
    await ask('Make me a workflow', 'Proposal A ready.');

    expect(screen.getByText(/Creates a new workflow “Brand new”/)).toBeTruthy();

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    // No workflow id in the request: that is what asks the server to create one.
    const [project, workflowId, name] = mocks.applyWorkflow.mock.calls[0];
    expect([project, workflowId, name]).toEqual([PROJECT_ID, undefined, 'Brand new']);
    expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}/workflows/${NEW_WORKFLOW_ID}`);
    expect(notices()).toEqual([{ title: 'Created', message: '“Brand new” was created.', color: 'green' }]);
  }, 30000);

  it('names a proposal that brought no name "New workflow" rather than sending a nameless create', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A', { workflowId: null }) }]);
    mocks.applyWorkflow.mockResolvedValue({ workflowId: NEW_WORKFLOW_ID, name: 'New workflow', stepCount: 1 });
    await renderPage();
    await openDrawer();
    await ask('Make me a workflow', 'Proposal A ready.');

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(mocks.applyWorkflow.mock.calls[0][2]).toBe('New workflow');
  }, 30000);

  it('a real conflict shows the workflow as it really is, and says nothing was applied', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.applyWorkflow.mockRejectedValue(new ApiError('The workflow was modified since this diff was computed.', 409));
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    expect(screen.queryByText('Fresh step from server')).toBeNull();

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    // Refetched, and — because the server moved on without the page — the canvas follows it.
    expect(mocks.mutate).toHaveBeenCalled();
    await waitFor(() => expect(within(screen.getByTestId('flowchart-steps')).getByText('Fresh step from server')).toBeTruthy());
    expect(notices()).toEqual([
      {
        title: 'Proposal out of date',
        message:
          'This workflow changed after the assistant proposed these edits, so nothing was applied. Ask the assistant again for a fresh proposal.',
        color: 'orange',
      },
    ]);
  }, 30000);

  it('a real conflict does not throw away what is being typed when the server had not moved', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.applyWorkflow.mockRejectedValue(new ApiError('conflict', 409));
    // The refetch finds exactly what the page already holds.
    mocks.mutate.mockResolvedValue(WORKFLOW);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    await user().click(screen.getByRole('button', { name: 'edit the canvas' }));
    expect(within(screen.getByTestId('flowchart-steps')).getByText('Local edit')).toBeTruthy();

    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());

    expect(mocks.mutate).toHaveBeenCalled();
    expect(within(screen.getByTestId('flowchart-steps')).getByText('Local edit')).toBeTruthy();
  }, 30000);

  // --- A finished assistant action reaches the page --------------------------------------------

  const doneAction = (over: Partial<AssistantAction> = {}): AssistantAction => ({
    id: 'act-1',
    state: 'done',
    mode: 'ask',
    kind: 'workflow',
    risk: 'normal',
    title: 'Add a review step',
    changes: [],
    refresh: { projectId: PROJECT_ID, workflowId: WORKFLOW_ID },
    ...over,
  });

  it('drops the waiting proposal and redraws the canvas when the assistant finishes a change to this workflow', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    expect(screen.queryByText('Fresh step from server')).toBeNull();

    act(() => {
      announceAssistantActionDone(doneAction());
    });

    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());
    await waitFor(() => expect(within(screen.getByTestId('flowchart-steps')).getByText('Fresh step from server')).toBeTruthy());
    // Nothing was applied from the tray, and nothing told the person it was out of date.
    expect(mocks.applyWorkflow).not.toHaveBeenCalled();
    expect(notices()).toEqual([]);
  }, 30000);

  it('keeps what is being typed when the assistant finished something that left this workflow as it was', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    mocks.mutate.mockResolvedValue(WORKFLOW);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');
    await user().click(screen.getByRole('button', { name: 'edit the canvas' }));

    act(() => {
      // Starting a run names the workflow to refresh, but does not change its steps.
      announceAssistantActionDone(doneAction({ id: 'act-run', kind: 'run', title: 'Run the workflow' }));
    });

    await waitFor(() => expect(mocks.mutate).toHaveBeenCalled());
    expect(within(screen.getByTestId('flowchart-steps')).getByText('Local edit')).toBeTruthy();
    // A run is not an edit: the proposal waiting in the tray is still good.
    expect(screen.getByTestId('apply-button')).toBeTruthy();
  }, 30000);

  it('leaves the tray and the canvas alone when the finished action is about another workflow', async () => {
    queueReplies([{ text: 'Proposal A ready.', diff: makeDiff('token-A') }]);
    await renderPage();
    await openDrawer();
    await ask('Add a step', 'Proposal A ready.');

    act(() => {
      announceAssistantActionDone(doneAction({ id: 'act-other', refresh: { projectId: PROJECT_ID, workflowId: OTHER_WORKFLOW_ID } }));
    });

    expect(screen.getByTestId('apply-button')).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();
  }, 30000);

  it('a finished change to the workflow a proposal was FOR consumes that proposal, even from another page', async () => {
    queueReplies([
      { text: 'Proposal A ready.', diff: makeDiff('token-A', { workflowId: OTHER_WORKFLOW_ID, name: 'Launch teaser' }) },
    ]);
    await renderPage();
    await openDrawer();
    await ask('Add a step to the teaser', 'Proposal A ready.');

    act(() => {
      announceAssistantActionDone(doneAction({ id: 'act-teaser', refresh: { projectId: PROJECT_ID, workflowId: OTHER_WORKFLOW_ID } }));
    });

    await waitFor(() => expect(screen.queryByTestId('apply-button')).toBeNull());
    // Not this page's workflow, so this page's canvas is not refetched on its account.
    expect(mocks.mutate).not.toHaveBeenCalled();
  }, 30000);

  // --- The same, through the real chat stream ---------------------------------------------------

  const DONE_FRAME = {
    id: 'act-applied',
    state: 'done',
    mode: 'ask',
    kind: 'workflow',
    risk: 'normal',
    title: 'Add a review step to “My workflow”',
    outcome: 'Added a review step.',
    refresh: { projectId: PROJECT_ID, workflowId: WORKFLOW_ID },
  };

  it('when the assistant applies its own proposal, the canvas shows it and no stale proposal is left to press', async () => {
    // What the server now sends: the finished action, and NO workflowDiff for a proposal it applied.
    queueReplies([{ text: 'Added the review step.', diff: null, frames: [DONE_FRAME] }]);
    await renderPage();
    await openDrawer();
    const input = screen.getByRole('textbox', { name: 'Message the workflow assistant' });
    await user().type(input, 'Add a review step');
    await user().click(screen.getByRole('button', { name: 'Send' }));
    await within(screen.getByTestId('assistant-thread')).findByText('Added the review step.', {}, { timeout: 10000 });

    await waitFor(() => expect(within(screen.getByTestId('flowchart-steps')).getByText('Fresh step from server')).toBeTruthy());
    expect(screen.queryByTestId('apply-button')).toBeNull();
    expect(notices()).toEqual([]);
  }, 30000);

  it('a proposal made AFTER an apply in the same turn is still shown, not swallowed by the apply', async () => {
    queueReplies([
      { text: 'Added one, and here is another.', diff: makeDiff('token-NEXT'), frames: [DONE_FRAME] },
    ]);
    mocks.applyWorkflow.mockResolvedValue(APPLY_SUMMARY);
    await renderPage();
    await openDrawer();
    await ask('Add a review step, then propose a second', 'Added one, and here is another.');

    // The finished action reached the page first (the canvas caught up) and the proposal then arrived.
    await waitFor(() => expect(within(screen.getByTestId('flowchart-steps')).getByText('Fresh step from server')).toBeTruthy());
    await user().click(screen.getByTestId('apply-button'));
    await waitFor(() => expect(mocks.applyWorkflow).toHaveBeenCalledTimes(1));
    expect(mocks.applyWorkflow.mock.calls[0][4]).toBe('token-NEXT');
  }, 30000);
});
