// @vitest-environment jsdom

/**
 * The assistant's chrome and its context wiring: the drawer's shape per breakpoint, the
 * once-per-token guard, the prop-less drawer reading the shell, and the conversation runtime
 * surviving a close/reopen.
 *
 * Mantine's `Drawer` is replaced by a recording double that — like the real one — unmounts its
 * children while closed; that is the behaviour the runtime-survives-close invariant is about.
 * The tests assert the props the drawer is handed, never Mantine's generated class names.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import type { NamedStep } from './diff-model';

const harness = vi.hoisted(() => {
  const diff = (confirmationToken: string): WorkflowEditDiff => ({
    additions: [],
    removals: [],
    modifications: [],
    confirmationToken,
  });
  return {
    drawerCalls: [] as Array<Record<string, unknown>>,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    panelCalls: [] as Array<{ onWorkflowDiff?: (d: any) => void; review?: unknown }>,
    runtimeConfigs: [] as Array<{ api: string; body: () => Promise<unknown> }>,
    runtimeInstances: [] as unknown[],
    runtimeProviderMounts: 0,
    renderRealPanel: false,
    DIFF_A: diff('token-a'),
    DIFF_B: diff('token-b'),
  };
});

vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();
  function RecordingDrawer(props: {
    opened: boolean;
    title?: ReactNode;
    children?: ReactNode;
  }) {
    harness.drawerCalls.push(props as unknown as Record<string, unknown>);
    // The real Drawer mounts neither its content nor its title while closed.
    if (!props.opened) return null;
    return (
      <div data-testid="drawer">
        {props.title}
        {props.children}
      </div>
    );
  }
  return { ...actual, Drawer: RecordingDrawer };
});

vi.mock('@assistant-ui/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@assistant-ui/react')>();
  const React = await import('react');
  function RecordingRuntimeProvider(props: { runtime?: unknown; children?: ReactNode }) {
    // An effect with no dependencies runs once per MOUNT — a re-render of the same element must
    // not be counted, since the invariant is about the subtree surviving, not about render count.
    React.useEffect(() => {
      harness.runtimeProviderMounts += 1;
    }, []);
    harness.runtimeInstances.push(props.runtime);
    return (
      <actual.AssistantRuntimeProvider runtime={props.runtime as never}>
        <div data-testid="runtime-provider">{props.children}</div>
      </actual.AssistantRuntimeProvider>
    );
  }
  return { ...actual, AssistantRuntimeProvider: RecordingRuntimeProvider };
});

vi.mock('@assistant-ui/react-data-stream', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@assistant-ui/react-data-stream')>();
  return {
    ...actual,
    useDataStreamRuntime: function useRecordingDataStreamRuntime(
      options: Parameters<typeof actual.useDataStreamRuntime>[0],
    ) {
      harness.runtimeConfigs.push(options as unknown as { api: string; body: () => Promise<unknown> });
      return actual.useDataStreamRuntime(options);
    },
  };
});

vi.mock('./AssistantChatPanel', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./AssistantChatPanel')>();
  function RecordingChatPanel(props: {
    onWorkflowDiff?: (d: WorkflowEditDiff) => void;
    review?: ReactNode;
  }) {
    harness.panelCalls.push(props);
    return (
      <>
        <button data-testid="emit-token-a" onClick={() => props.onWorkflowDiff?.(harness.DIFF_A)} />
        <button data-testid="emit-token-b" onClick={() => props.onWorkflowDiff?.(harness.DIFF_B)} />
        <div data-testid="panel-review">{props.review}</div>
        {harness.renderRealPanel ? <actual.AssistantChatPanel {...props} /> : null}
      </>
    );
  }
  return { ...actual, AssistantChatPanel: RecordingChatPanel };
});

// Imported after the mocks (vi.mock is hoisted anyway; this keeps the intent obvious).
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';
import { AssistantDrawerShell } from './AssistantDrawer.shell';
import type { AssistantDrawerProps } from './AssistantDrawer';
import { AssistantProvider } from './AssistantProvider';
import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

/** Which side of `(max-width: 48em)` the viewport sits on for the next render. */
let phoneViewport = false;

const readSource = (relative: string) =>
  readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');

/** Opening the drawer now opens a conversation server-side, so `fetch` is stubbed in EVERY test
 * here: no test in this file may depend on the network, or on the backend being up. */
function stubThreadEndpoints() {
  const THREAD_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = String(input);
      const collection = url.endsWith('/api/v1/assistant/threads');
      const body = collection
        ? init?.method === 'POST'
          ? { id: THREAD_ID }
          : []
        : { id: THREAD_ID, title: 'New chat', projectId: null, messages: [] };
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      );
    }),
  );
}

const STEPS: readonly NamedStep[] = [{ label: 'Analyze', stepType: 'VideoAnalyze' }];
const pageHandler = vi.fn();

/** A page that registers what it has, the way a real page does. */
function PageProbe({ autoOpen = true }: { autoOpen?: boolean }) {
  const { open, toggle, registerPage } = useAssistant();
  useEffect(
    () =>
      registerPage({
        review: <span>review tray</span>,
        onWorkflowDiff: pageHandler,
        currentSteps: STEPS,
      }),
    [registerPage],
  );
  useEffect(() => {
    if (autoOpen) open();
  }, [autoOpen, open]);
  return (
    <div>
      page body
      <button onClick={toggle}>toggle assistant</button>
    </div>
  );
}

function renderShellProvider(children: ReactNode = <PageProbe />) {
  return render(
    <MantineProvider env="test">
      <AssistantShellProvider>{children}</AssistantShellProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  harness.drawerCalls.length = 0;
  harness.panelCalls.length = 0;
  harness.runtimeConfigs.length = 0;
  harness.runtimeInstances.length = 0;
  harness.runtimeProviderMounts = 0;
  harness.renderRealPanel = false;
  pageHandler.mockClear();
  phoneViewport = false;
  resetTestViewport();

  global.ResizeObserver = MockResizeObserver;
  stubThreadEndpoints();
  global.matchMedia = ((query: string) => ({
    matches: phoneViewport,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  })) as unknown as typeof window.matchMedia;
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const lastDrawer = () => harness.drawerCalls[harness.drawerCalls.length - 1];

describe('AssistantDrawer / AssistantDrawerShell', () => {
  it('A1: AssistantDrawer takes no props and reads everything from useAssistant()', () => {
    // A type-level pin: the exported props type admits only the empty object, so no page can
    // hand the platform's drawer a project, a diff or an open state.
    const noProps: AssistantDrawerProps = {};
    expect(noProps).toEqual({});
    // @ts-expect-error — AssistantDrawer accepts no props at all
    const withOpened: AssistantDrawerProps = { opened: true };
    expect(withOpened).toBeDefined();

    renderShellProvider();

    // One drawer, rendered by the shell itself (no page passed it anything), and it is OPEN —
    // driven by `useAssistant().open()` from the probe, not by a prop.
    expect(screen.getByTestId('drawer')).toBeDefined();
    expect(lastDrawer().opened).toBe(true);
  });

  it('A2: desktop renders a right drawer at 560px with no padding', () => {
    phoneViewport = false;
    renderShellProvider();

    expect(lastDrawer().position).toBe('right');
    expect(lastDrawer().size).toBe(560);
    expect(lastDrawer().padding).toBe(0);
  });

  it('A2: on a phone it renders a full-height bottom sheet', () => {
    phoneViewport = true;
    setTestViewport('phone');
    renderShellProvider();

    expect(lastDrawer().position).toBe('bottom');
    expect(lastDrawer().size).toBe('100dvh');
    expect(lastDrawer().padding).toBe(0);
  });

  it('A3: closing and reopening never remounts the runtime provider, which stays the ancestor', async () => {
    const user = userEvent.setup();
    renderShellProvider(<PageProbe autoOpen={false} />);

    const toggle = () => screen.getByRole('button', { name: 'toggle assistant' });
    await user.click(toggle());

    const providerEl = screen.getByTestId('runtime-provider');
    const drawerEl = screen.getByTestId('drawer');
    expect(harness.runtimeProviderMounts).toBe(1);
    expect(providerEl.contains(drawerEl)).toBe(true);
    expect(drawerEl.contains(providerEl)).toBe(false);

    await user.click(toggle());
    expect(screen.queryByTestId('drawer')).toBeNull();

    await user.click(toggle());
    expect(screen.getByTestId('drawer')).toBeDefined();

    // The provider itself: same DOM node, mounted exactly once across the whole cycle.
    expect(screen.getByTestId('runtime-provider')).toBe(providerEl);
    expect(harness.runtimeProviderMounts).toBe(1);
  });

  it('A4: the same confirmationToken is forwarded once, a new one is forwarded again', async () => {
    const user = userEvent.setup();
    const forwarded: WorkflowEditDiff[] = [];
    render(
      <MantineProvider env="test">
        <AssistantDrawerShell
          opened
          onClose={() => {}}
          onWorkflowDiff={(diff) => forwarded.push(diff)}
        />
      </MantineProvider>,
    );

    await user.click(screen.getByTestId('emit-token-a'));
    await user.click(screen.getByTestId('emit-token-a'));
    expect(forwarded.map((d) => d.confirmationToken)).toEqual(['token-a']);

    await user.click(screen.getByTestId('emit-token-b'));
    await user.click(screen.getByTestId('emit-token-b'));
    expect(forwarded.map((d) => d.confirmationToken)).toEqual(['token-a', 'token-b']);
  });

  it('A5: the drawer hands the page registration to the panel and keeps its title', async () => {
    const user = userEvent.setup();
    renderShellProvider();

    // The panel's diff callback is the shell's guarded wrapper, so assert it FORWARDS to the
    // page's own handler rather than comparing function identity.
    await user.click(screen.getByTestId('emit-token-a'));
    expect(pageHandler).toHaveBeenCalledWith(harness.DIFF_A);
    expect(screen.getByTestId('panel-review').textContent).toContain('review tray');

    const drawer = screen.getByTestId('drawer');
    expect(drawer.textContent).toContain('Assistant');
    expect(within(drawer).getByText('Assistant')).toBeDefined();
    // The sparkle mark is decorative and carries no name of its own.
    const mark = drawer.querySelector('span[aria-hidden]');
    expect(mark?.querySelector('svg')).not.toBeNull();
    // Named distinctly from AssistantTrigger's open-state name ('Close assistant'), so the two
    // controls never collide in the accessibility tree.
    expect(lastDrawer().closeButtonProps).toEqual({ 'aria-label': 'Close assistant panel' });
  });

  it('A6: the shell provider mounts the drawer once, unconditionally, and still renders children', () => {
    const source = readSource('./AssistantShellProvider.tsx');
    expect(source.match(/<AssistantDrawer\b/g) ?? []).toHaveLength(1);
    // Comments below explain the rule; the rule itself is asserted against the code only.
    const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
    expect(code).not.toMatch(/opened\s*&&/);
    expect(source).toContain('{children}');

    // And at runtime: the drawer is handed `opened: false` — it exists while closed, which is
    // what keeps the conversation alive between openings.
    renderShellProvider(<div>page body</div>);
    expect(lastDrawer().opened).toBe(false);
    expect(screen.getByText('page body')).toBeDefined();
  });

  it('A9: a new page context does NOT recreate the runtime, and the body reads the live one', async () => {
    const { rerender } = render(
      <AssistantProvider context={{ projectId: 'project-1' }}>
        <div>child</div>
      </AssistantProvider>,
    );
    const firstConfig = harness.runtimeConfigs[0];
    const firstRuntime = harness.runtimeInstances[0];

    rerender(
      <AssistantProvider context={{ projectId: 'project-2', workflowId: 'workflow-2' }}>
        <div>child</div>
      </AssistantProvider>,
    );

    // Same config object, same runtime instance: `useDataStreamRuntime` never saw a reset signal.
    expect(harness.runtimeConfigs.every((config) => config === firstConfig)).toBe(true);
    expect(harness.runtimeInstances.at(-1)).toBe(firstRuntime);

    // The body is a STABLE function that reads the context at send time, so the turn that goes
    // out carries the page the user is actually on.
    await expect(firstConfig.body()).resolves.toEqual({
      projectId: 'project-2',
      workflowId: 'workflow-2',
    });
  });

  it('A9: absent context fields are omitted rather than sent empty', async () => {
    render(
      <AssistantProvider context={{}}>
        <div>child</div>
      </AssistantProvider>,
    );

    expect(harness.runtimeConfigs[0].api).toBe('/api/v1/assistant/chat');
    const body = await harness.runtimeConfigs[0].body();
    // Asserted on the SERIALIZED body, because that string is what goes on the wire. `threadId` is
    // deliberately an OWN key of the returned object holding `undefined` — that is what displaces
    // the data-stream adapter's `__LOCALID_…` placeholder before it reaches the wire (see
    // `AssistantRequestBody` in AssistantProvider.tsx) — and `JSON.stringify` drops an `undefined`
    // value, so the turn still goes out carrying no context at all. Asserting on the object's own
    // keys would now be asserting on the override mechanism rather than on the body it produces.
    expect(JSON.stringify(body)).toBe('{}');
  });

  it('A10: a project-less page renders the panel and composer, and sends nothing', async () => {
    harness.renderRealPanel = true;
    const fetchSpy = vi.fn((input: RequestInfo | URL) =>
      Promise.reject(new Error(`no network in this test: ${String(input)}`)),
    );
    vi.stubGlobal('fetch', fetchSpy);

    renderShellProvider();

    const drawer = await screen.findByTestId('drawer');
    expect(
      await within(drawer).findByRole('textbox', { name: 'Message the workflow assistant' }),
    ).toBeDefined();
    expect(within(drawer).getByTestId('assistant-thread')).toBeDefined();
    // Opening the assistant does open a conversation (that is the drawer's own lifecycle), so the
    // invariant here is the one this test is named for: no TURN is ever sent from a page that has
    // nothing to ask yet.
    expect(fetchSpy.mock.calls.filter(([url]) => String(url).includes('/assistant/chat'))).toEqual(
      [],
    );
  });
});

describe('AssistantDrawer.tsx source', () => {
  it('A1: exports a prop-less AssistantDrawer whose runtime provider wraps the shell', () => {
    const source = readSource('./AssistantDrawer.tsx');

    expect(source).toMatch(/export function AssistantDrawer\(\s*\)/);
    expect(source).toContain('useAssistant()');
    // `<AssistantProvider>` opens before `<AssistantDrawerShell`, so the runtime is the shell's
    // ancestor and never its descendant.
    expect(source.indexOf('<AssistantProvider')).toBeLessThan(
      source.indexOf('<AssistantDrawerShell'),
    );
  });
});

describe('assistant mode bar layout', () => {
  // jsdom does no layout, so this pins the rules that make the bar wrap rather than measuring it.
  it('wraps, gives the cost-limit note its own full-width line, and fixes no height', () => {
    const css = readSource('./AssistantDrawer.module.css');
    const bar = /\.modeBar\s*{([^}]*)}/.exec(css)?.[1] ?? '';
    const limit = /\.modeLimit\s*{([^}]*)}/.exec(css)?.[1] ?? '';
    expect(bar).toMatch(/flex-wrap:\s*wrap/);
    expect(bar).toMatch(/min-height:\s*44px/);
    expect(bar).not.toMatch(/(^|[^-])height:/);
    expect(limit).toMatch(/flex:\s*1 1 100%/);
    expect(limit).toMatch(/min-width:\s*0/);
    expect(limit).toMatch(/overflow-wrap:\s*anywhere/);
    expect(limit).not.toMatch(/(^|[^-])height:/);
  });

  it('renders the cost-limit note as its own element with the limit class', () => {
    const source = readSource('./AssistantDrawer.shell.tsx');
    expect(source).toMatch(
      /<span className=\{`\$\{classes\.modeNote\} \$\{classes\.modeLimit\}`\} data-testid="auto-cost-limit">/,
    );
  });
});
