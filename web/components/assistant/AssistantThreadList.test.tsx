// @vitest-environment jsdom

/**
 * The assistant's server-persisted conversation history.
 *
 * Three things are pinned here, in one file because they are one feature:
 *  - the LIST (`AssistantThreadList`): expand, select, rename, delete-with-confirmation, and the
 *    loading/empty/error states, all against a stubbed `fetch`;
 *  - the RESTORE (`AssistantProvider`): persisted turns arrive as the runtime's `initialMessages`,
 *    and a failed load is reported without taking the chat down with it;
 *  - the LIFECYCLE (`AssistantDrawer`): opening the assistant with no thread opens exactly one.
 *
 * `fetch` is stubbed in every test: nothing here may need the backend running, or merged.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { StrictMode, useEffect, useRef } from 'react';
import type { ReactNode } from 'react';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { AssistantThreadDetail, AssistantThreadSummary } from '@/lib/types/assistant';

const harness = vi.hoisted(() => ({
  configs: [] as Array<{
    api: string;
    protocol: string;
    body: () => Promise<unknown>;
    initialMessages?: readonly unknown[];
  }>,
  runtimes: [] as unknown[],
}));

vi.mock('@assistant-ui/react-data-stream', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@assistant-ui/react-data-stream')>();
  return {
    ...actual,
    useDataStreamRuntime: function useRecordingDataStreamRuntime(
      options: Parameters<typeof actual.useDataStreamRuntime>[0],
    ) {
      const runtime = actual.useDataStreamRuntime(options);
      harness.configs.push(options as unknown as (typeof harness.configs)[number]);
      harness.runtimes.push(runtime);
      return runtime;
    },
  };
});

// Imported after the mock is registered (vi.mock is hoisted anyway; this keeps the intent obvious).
import { AssistantProvider } from './AssistantProvider';
import { AssistantThreadList } from './AssistantThreadList';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';

const THREADS_URL = '/api/v1/assistant/threads';

const PROJECT_ID = '12345678-1234-1234-1234-123456789012';

const OLDER: AssistantThreadSummary = {
  id: 'thread-older',
  title: 'Older chat',
  projectId: null,
  updatedAt: '2024-01-01T10:00:00Z',
};
const NEWER: AssistantThreadSummary = {
  id: 'thread-newer',
  title: 'Newer chat',
  projectId: PROJECT_ID,
  updatedAt: '2024-03-01T10:00:00Z',
};

interface RecordedCall {
  method: string;
  url: string;
  body: string | null;
}

/** The stub's state. Every test resets it; nothing here reaches the network. */
const server = {
  threads: [] as AssistantThreadSummary[],
  details: new Map<string, AssistantThreadDetail>(),
  failThreads: false,
  failDetail: false,
  /** When set, list requests wait for it — the only way to observe the loading state. */
  holdThreads: null as Promise<void> | null,
  releaseThreads: null as (() => void) | null,
  calls: [] as RecordedCall[],
  created: 0,
};

const json = (payload: unknown) =>
  new Response(JSON.stringify(payload), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });

const empty = () => new Response(null, { status: 204 });

function installFetch() {
  const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    server.calls.push({
      method,
      url,
      body: typeof init?.body === 'string' ? init.body : null,
    });

    if (url === THREADS_URL && method === 'GET') {
      if (server.holdThreads) await server.holdThreads;
      if (server.failThreads) return new Response('nope', { status: 500 });
      return json(server.threads);
    }

    if (url === THREADS_URL && method === 'POST') {
      server.created += 1;
      const id = `thread-created-${server.created}`;
      server.details.set(id, { id, title: 'New chat', projectId: null, messages: [] });
      return json({ id });
    }

    const detail = /^\/api\/v1\/assistant\/threads\/(.+)$/.exec(url);
    if (detail) {
      const id = detail[1];
      if (method === 'GET') {
        if (server.failDetail) return new Response('boom', { status: 503 });
        return json(
          server.details.get(id) ?? { id, title: 'New chat', projectId: null, messages: [] },
        );
      }
      // The stub is stateful, like the server: a rename or a delete is visible to the list's next
      // refresh, which is what makes the refresh path testable at all.
      if (method === 'PATCH') {
        const title = (JSON.parse(String(init?.body)) as { title: string }).title;
        server.threads = server.threads.map((thread) =>
          thread.id === id ? { ...thread, title } : thread,
        );
        return empty();
      }
      if (method === 'DELETE') {
        server.threads = server.threads.filter((thread) => thread.id !== id);
        return empty();
      }
    }

    return new Response('unexpected request', { status: 500 });
  });

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const callsMatching = (method: string, url: string) =>
  server.calls.filter((call) => call.method === method && call.url === url);

/**
 * A Mantine control's rendered size in px. Read from the CSS variable the component actually gets
 * (`size={44}` renders as `--ai-size: calc(2.75rem * ...)`), so this measures the real target
 * rather than trusting a prop name.
 */
function controlSizePx(element: HTMLElement): number {
  const raw =
    element.style.getPropertyValue('--ai-size') ||
    element.style.height ||
    element.style.getPropertyValue('--button-height');
  const rem = /([\d.]+)rem/.exec(raw);
  if (rem) return Number(rem[1]) * 16;
  const px = /([\d.]+)px/.exec(raw);
  return px ? Number(px[1]) : 0;
}

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

/** Every thread id the shell has ever held, recorded on change. */
let selections: Array<string | null> = [];

function ThreadProbe({ autoOpen = false }: { autoOpen?: boolean }) {
  const { threadId, setThreadId, open, toggle } = useAssistant();
  const previous = useRef<string | null | undefined>(undefined);

  useEffect(() => {
    if (previous.current === threadId) return;
    previous.current = threadId;
    selections.push(threadId);
  }, [threadId]);

  useEffect(() => {
    if (autoOpen) open();
  }, [autoOpen, open]);

  return (
    <>
      <span data-testid="active-thread">{threadId ?? 'none'}</span>
      <button onClick={toggle}>toggle assistant</button>
      {/* Opens a thread without going through the list, so a test can inspect the list of an
          ALREADY-open conversation instead of one that is being switched under it. */}
      <button onClick={() => setThreadId(NEWER.id)}>select a thread</button>
    </>
  );
}

function Wrapper({ children, strict = false }: { children: ReactNode; strict?: boolean }) {
  const tree = (
    <MantineProvider env="test">
      <AssistantShellProvider>{children}</AssistantShellProvider>
    </MantineProvider>
  );
  return strict ? <StrictMode>{tree}</StrictMode> : tree;
}

/** The list on its own, inside the shell that owns the selected thread. */
function renderList() {
  return render(
    <Wrapper>
      <ThreadProbe />
      <AssistantThreadList />
    </Wrapper>,
  );
}

const user = () => userEvent.setup();

async function expandHistory() {
  await user().click(screen.getByRole('button', { name: 'History' }));
  // The panel is a Collapse: it mounts on expand, so this waits for it rather than assuming it.
  return screen.findByRole('region', { name: 'Conversation history' });
}

beforeEach(() => {
  harness.configs.length = 0;
  harness.runtimes.length = 0;
  selections = [];
  server.threads = [];
  server.details = new Map();
  server.failThreads = false;
  server.failDetail = false;
  server.holdThreads = null;
  server.releaseThreads = null;
  server.calls = [];
  server.created = 0;

  global.ResizeObserver = MockResizeObserver;
  global.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  })) as unknown as typeof window.matchMedia;
  Element.prototype.scrollTo = vi.fn();

  installFetch();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AssistantThreadList', () => {
  it('A5: the History panel is collapsed until asked, then loads the caller’s threads newest first', async () => {
    server.threads = [OLDER, NEWER];
    renderList();

    expect(screen.queryByRole('region', { name: 'Conversation history' })).toBeNull();
    expect(callsMatching('GET', THREADS_URL)).toHaveLength(0);

    const panel = await expandHistory();

    await waitFor(() => {
      expect(within(panel).getByRole('button', { name: 'Open conversation Newer chat' })).toBeDefined();
    });
    expect(callsMatching('GET', THREADS_URL)).toHaveLength(1);
    expect(within(panel).getByRole('button', { name: 'Open conversation Older chat' })).toBeDefined();

    // Most recently updated first, whatever order the server answered in.
    const text = panel.textContent ?? '';
    expect(text.indexOf('Newer chat')).toBeLessThan(text.indexOf('Older chat'));
  });

  it('names a conversation the way the person would, never by an id that ended up in its title', async () => {
    const GUID = '9f8e7d6c-5b4a-4938-8271-60594a3b2c1d';
    server.threads = [
      { ...OLDER, id: 'thread-id-title', title: `I attached the file "intro.mp4" (project file id ${GUID})` },
      { ...NEWER, id: 'thread-only-id', title: GUID },
    ];
    renderList();

    const panel = await expandHistory();

    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Open conversation New chat' })).toBeDefined());
    expect(panel.textContent).not.toContain(GUID);
    expect(panel.textContent).toContain('intro.mp4');
    expect(panel.textContent).not.toMatch(/project file id/);
  });

  it('A5: selecting a past thread selects it through the shell, not through local state', async () => {
    server.threads = [OLDER, NEWER];
    renderList();
    const panel = await expandHistory();

    expect(screen.getByTestId('active-thread').textContent).toBe('none');

    await user().click(within(panel).getByRole('button', { name: 'Open conversation Older chat' }));

    expect(screen.getByTestId('active-thread').textContent).toBe(OLDER.id);
  });

  it('A5: rename PATCHes { title } and shows the new title', async () => {
    server.threads = [OLDER];
    renderList();
    const panel = await expandHistory();

    await user().click(
      within(panel).getByRole('button', { name: 'Rename conversation Older chat' }),
    );

    const input = within(panel).getByRole('textbox', {
      name: 'New title for conversation Older chat',
    });
    await user().clear(input);
    await user().type(input, 'Renamed chat');
    await user().click(
      within(panel).getByRole('button', { name: 'Save new title for conversation Older chat' }),
    );

    await waitFor(() => {
      expect(callsMatching('PATCH', `${THREADS_URL}/${OLDER.id}`)).toHaveLength(1);
    });
    expect(callsMatching('PATCH', `${THREADS_URL}/${OLDER.id}`)[0].body).toBe(
      JSON.stringify({ title: 'Renamed chat' }),
    );
    expect(
      await within(panel).findByRole('button', { name: 'Open conversation Renamed chat' }),
    ).toBeDefined();
  });

  it('A5: delete asks for confirmation first, and only the confirmation sends DELETE', async () => {
    server.threads = [OLDER];
    renderList();
    const panel = await expandHistory();

    await user().click(
      within(panel).getByRole('button', { name: 'Delete conversation Older chat' }),
    );

    expect(
      within(panel).getByRole('group', { name: 'Confirm deleting conversation Older chat' }),
    ).toBeDefined();
    expect(callsMatching('DELETE', `${THREADS_URL}/${OLDER.id}`)).toHaveLength(0);

    await user().click(
      within(panel).getByRole('button', { name: 'Cancel delete conversation Older chat' }),
    );
    expect(callsMatching('DELETE', `${THREADS_URL}/${OLDER.id}`)).toHaveLength(0);

    await user().click(
      within(panel).getByRole('button', { name: 'Delete conversation Older chat' }),
    );
    await user().click(
      within(panel).getByRole('button', { name: 'Confirm delete conversation Older chat' }),
    );

    await waitFor(() => {
      expect(callsMatching('DELETE', `${THREADS_URL}/${OLDER.id}`)).toHaveLength(1);
    });
  });

  it('A5: New chat creates a thread and opens it', async () => {
    renderList();

    await user().click(screen.getByRole('button', { name: 'New chat' }));

    await waitFor(() => {
      expect(callsMatching('POST', THREADS_URL)).toHaveLength(1);
    });
    await waitFor(() => {
      expect(screen.getByTestId('active-thread').textContent).toBe('thread-created-1');
    });
  });

  it('A5: the loading state is announced while the list is still on its way', async () => {
    server.threads = [OLDER];
    server.holdThreads = new Promise<void>((resolve) => {
      server.releaseThreads = resolve;
    });
    renderList();

    await user().click(screen.getByRole('button', { name: 'History' }));

    const loading = await screen.findByRole('status');
    expect(loading.textContent).toContain('Loading conversations');

    server.releaseThreads?.();
    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: 'Open conversation Older chat' }),
      ).toBeDefined();
    });
  });

  it('A5: the empty state says so rather than showing nothing', async () => {
    server.threads = [];
    renderList();

    const panel = await expandHistory();

    await waitFor(() => {
      expect(within(panel).getByRole('status').textContent).toContain('No conversations yet');
    });
  });

  it('A5: the error state is announced and offers a retry that works', async () => {
    server.failThreads = true;
    renderList();
    const panel = await expandHistory();

    const alert = await within(panel).findByRole('alert');
    expect(alert.textContent).toContain('Conversations unavailable');

    server.failThreads = false;
    server.threads = [NEWER];
    await user().click(within(alert).getByRole('button', { name: 'Try again' }));

    expect(
      await within(panel).findByRole('button', { name: 'Open conversation Newer chat' }),
    ).toBeDefined();
  });

  it('A7: named region, marked current thread, distinct names, 44px-class targets', async () => {
    server.threads = [OLDER, NEWER];
    server.details.set(NEWER.id, {
      id: NEWER.id,
      title: NEWER.title,
      projectId: null,
      messages: [],
    });
    renderList();

    // Open a conversation FIRST, and wait for the runtime that comes with it: switching threads
    // re-keys the runtime (and the whole drawer subtree) by design, so asserting against a list
    // that is being remounted under the assertion would prove nothing.
    const mountsBefore = harness.runtimes.length;
    await user().click(screen.getByRole('button', { name: 'select a thread' }));
    await waitFor(() => {
      expect(harness.runtimes.length).toBeGreaterThan(mountsBefore);
    });

    const panel = await expandHistory();
    expect(panel.getAttribute('aria-label')).toBe('Conversation history');

    const openButtons = [
      within(panel).getByRole('button', { name: 'Open conversation Newer chat' }),
      within(panel).getByRole('button', { name: 'Open conversation Older chat' }),
    ];
    expect(openButtons[0].getAttribute('aria-current')).toBe('true');
    expect(openButtons[1].getAttribute('aria-current')).toBeNull();

    // Every control of a row answers to a name of its own — no two controls share one.
    const names = [
      'Open conversation Newer chat',
      'Rename conversation Newer chat',
      'Delete conversation Newer chat',
      'Open conversation Older chat',
      'Rename conversation Older chat',
      'Delete conversation Older chat',
    ];
    const controls = names.map((name) => within(panel).getByRole('button', { name }));
    expect(new Set(names).size).toBe(names.length);

    // The History toggle lives in the bar above the panel, so it is looked up on the screen.
    for (const control of [...controls, screen.getByRole('button', { name: 'History' })]) {
      expect(controlSizePx(control)).toBeGreaterThanOrEqual(44);
    }
  });
});

describe('AssistantProvider history restore', () => {
  const detail: AssistantThreadDetail = {
    id: 'thread-restored',
    title: 'Restored',
    projectId: null,
    messages: [
      { id: 'm1', role: 'user', content: 'Where is the render step?', sequence: 1, createdAt: '' },
      { id: 'm2', role: 'assistant', content: 'At the end.', sequence: 2, createdAt: '' },
    ],
  };

  it('A4: persisted turns arrive as the runtime’s initial messages, as text parts', async () => {
    server.details.set(detail.id, detail);

    render(
      <MantineProvider env="test">
        <AssistantProvider threadId={detail.id} context={{}}>
          <span>child</span>
        </AssistantProvider>
      </MantineProvider>,
    );

    await waitFor(() => {
      expect(harness.configs.at(-1)?.initialMessages).toHaveLength(2);
    });

    expect(harness.configs.at(-1)?.initialMessages).toEqual([
      { id: 'm1', role: 'user', content: [{ type: 'text', text: 'Where is the render step?' }], metadata: { custom: { sequence: 1 } } },
      { id: 'm2', role: 'assistant', content: [{ type: 'text', text: 'At the end.' }], metadata: { custom: { sequence: 2 } } },
    ]);
    expect(harness.configs.at(-1)?.api).toBe('/api/v1/assistant/chat');
  });

  it('A3: a new page context keeps the same runtime, a new thread does not', async () => {
    // Real GUIDs, because that is the only shape a server thread id can have: the endpoint binds
    // `threadId` as a `Guid`, and `AssistantProvider` omits the key for anything that is not one
    // rather than putting an unbindable value on the wire.
    const threadA = '11111111-1111-4111-8111-111111111111';
    const threadB = '22222222-2222-4222-8222-222222222222';
    server.details.set(threadA, { ...detail, id: threadA });
    server.details.set(threadB, { ...detail, id: threadB, messages: [] });

    const { rerender } = render(
      <MantineProvider env="test">
        <AssistantProvider threadId={threadA} context={{ projectId: PROJECT_ID }}>
          <span>child</span>
        </AssistantProvider>
      </MantineProvider>,
    );

    await waitFor(() => {
      expect(harness.configs.at(-1)?.initialMessages).toHaveLength(2);
    });
    const settledConfig = harness.configs.at(-1);
    const settledRuntime = harness.runtimes.at(-1);
    const callsBefore = harness.configs.length;

    // A navigation: the bytes the turn will carry change, the runtime does not.
    rerender(
      <MantineProvider env="test">
        <AssistantProvider threadId={threadA} context={{ workflowId: 'workflow-2' }}>
          <span>child</span>
        </AssistantProvider>
      </MantineProvider>,
    );
    expect(harness.configs.slice(callsBefore).every((config) => config === settledConfig)).toBe(
      true,
    );
    expect(harness.runtimes.at(-1)).toBe(settledRuntime);
    await expect(settledConfig?.body()).resolves.toEqual({
      threadId: threadA,
      workflowId: 'workflow-2',
    });

    // Another thread: a different conversation, so a newly constructed runtime.
    rerender(
      <MantineProvider env="test">
        <AssistantProvider threadId={threadB} context={{ workflowId: 'workflow-2' }}>
          <span>child</span>
        </AssistantProvider>
      </MantineProvider>,
    );
    await waitFor(() => {
      expect(harness.runtimes.at(-1)).not.toBe(settledRuntime);
    });
    expect(harness.configs.at(-1)).not.toBe(settledConfig);
    await expect(harness.configs.at(-1)?.body()).resolves.toEqual({
      threadId: threadB,
      workflowId: 'workflow-2',
    });
  });

  it('A4: a failed load reports itself inline and leaves the composer usable', async () => {
    server.failDetail = true;

    render(
      <Wrapper>
        <ThreadProbe autoOpen />
      </Wrapper>,
    );

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Could not load this conversation');

    // The chat is still there, and so is its composer: the failure is a notice, not a wall.
    expect(await screen.findByTestId('assistant-thread')).toBeDefined();
    expect(
      screen.getByRole('textbox', { name: 'Message the workflow assistant' }),
    ).toBeDefined();
  });
});

describe('AssistantDrawer thread lifecycle', () => {
  it('A6: opening with no thread opens exactly one, and selects it once', async () => {
    render(
      <Wrapper>
        <ThreadProbe autoOpen />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('active-thread').textContent).toBe('thread-created-1');
    });

    expect(callsMatching('POST', THREADS_URL)).toHaveLength(1);
    expect(selections).toEqual([null, 'thread-created-1']);
  });

  it('A6: reopening with a thread already selected opens none', async () => {
    render(
      <Wrapper>
        <ThreadProbe autoOpen />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('active-thread').textContent).toBe('thread-created-1');
    });

    await user().click(screen.getByRole('button', { name: 'Close assistant panel' }));
    await user().click(screen.getByRole('button', { name: 'toggle assistant' }));

    await screen.findByTestId('assistant-thread');
    expect(callsMatching('POST', THREADS_URL)).toHaveLength(1);
  });

  it('A6: a StrictMode remount still opens exactly one thread', async () => {
    render(
      <Wrapper strict>
        <ThreadProbe autoOpen />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('active-thread').textContent).toBe('thread-created-1');
    });

    // In StrictMode React runs every effect twice on mount; the ref guard, not the dependency
    // array, is what keeps the second run from opening a second thread.
    expect(callsMatching('POST', THREADS_URL)).toHaveLength(1);
    expect(server.created).toBe(1);
  });
});
