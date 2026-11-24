// @vitest-environment jsdom

/**
 * The page bridge: a page declares where it is and what it has to show, and the globally mounted
 * assistant does the rest.
 *
 * The shell's own `useAssistant()` is delegated to here, with `registerPage` optionally routed
 * through a spy, so the tests can count registrations without reaching inside the provider. The
 * drawer is the real one; only its panel is a double, so a diff "forwarded by the drawer" travels
 * the same path a real proposal does (panel → drawer's once-per-token guard → the page's handler).
 */

import { useCallback, useEffect, useMemo } from 'react';
import type { ReactNode } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import type { AssistantShellValue } from './AssistantShellProvider';
import type { NamedStep } from './diff-model';

const harness = vi.hoisted(() => ({
  /** Armed by one test at a time: routes `registerPage` through a spy, then through the shell. */
  registerPageSpy: null as null | ((registration: unknown) => void),
  DIFF: {
    additions: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Added' }],
    removals: [],
    modifications: [],
    confirmationToken: 'page-context-token',
  } as WorkflowEditDiff,
}));

vi.mock('./AssistantShellProvider', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./AssistantShellProvider')>();
  return {
    ...actual,
    // The real hook's value, so every other assertion (context, page, open) is the real shell's.
    useAssistant: () => {
      const value = actual.useAssistant();
      const shellRegister = value.registerPage;
      const spy = harness.registerPageSpy;
      const registerPage = useMemo(
        () =>
          spy === null
            ? shellRegister
            : (registration: Parameters<typeof shellRegister>[0]) => {
                spy(registration);
                return shellRegister(registration);
              },
        [spy, shellRegister],
      );
      return useMemo(
        () => (registerPage === shellRegister ? value : { ...value, registerPage }),
        [value, registerPage, shellRegister],
      );
    },
  };
});

// The panel is the only double: it forwards a diff the way a real proposal would, and draws the
// page's review node where the real panel docks it.
vi.mock('./AssistantChatPanel', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./AssistantChatPanel')>();
  function RecordingPanel(props: {
    onWorkflowDiff?: (diff: WorkflowEditDiff) => void;
    review?: ReactNode;
  }) {
    return (
      <>
        <button data-testid="panel-proposes" onClick={() => props.onWorkflowDiff?.(harness.DIFF)}>
          propose
        </button>
        <div data-testid="panel-review">{props.review}</div>
      </>
    );
  }
  return { ...actual, AssistantChatPanel: RecordingPanel };
});

import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';
import { AssistantPageContext, useAssistantPageContext } from './AssistantPageContext';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const WORKFLOW_PATH = '../../app/(app)/projects/[id]/workflows/[workflowId]/page.tsx';
const PROJECT_PATH = '../../app/(app)/projects/[id]/page.tsx';
const PICTURE_EDITOR_PATH = '../../app/(app)/editor/thumbnails/[documentId]/page.tsx';
// Resolved per call, not at module scope: Vite rewrites `import.meta.url` outside a test body.
// The base is passed as the only argument — a literal `new URL('...', import.meta.url)` is an asset
// reference Vite rewrites into a server URL, which is not a path this test can read from.
const readSource = (relative: string) =>
  readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
const moduleDir = () => path.dirname(fileURLToPath(new URL(import.meta.url)));
const appDir = () => path.resolve(moduleDir(), '../../app');
const componentsDir = () => path.resolve(moduleDir(), '..');

/** Every `.tsx` under a directory, recursively — the assistant may be mounted in exactly one. */
function tsxFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = `${dir}/${entry.name}`;
    if (entry.isDirectory()) return tsxFiles(path);
    return entry.name.endsWith('.tsx') ? [path] : [];
  });
}

beforeEach(() => {
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
});

afterEach(() => {
  harness.registerPageSpy = null;
  cleanup();
});

/** The real shell, mounted the way `app/(app)/layout.tsx` mounts it. */
function Shell({ children }: { children: ReactNode }) {
  return (
    <MantineProvider env="test">
      <AssistantShellProvider>{children}</AssistantShellProvider>
    </MantineProvider>
  );
}

/**
 * A probe that records the shell's value, plus the holder it records into. The value is recorded in
 * an effect rather than during render — a render-time write to a variable outside the component is
 * exactly what React's compiler rules forbid, and this probe re-renders on any change to the shell's
 * value (context, page, opened), so the holder always ends up holding the latest one.
 */
function shellProbe() {
  const captured: { value?: AssistantShellValue } = {};
  function Probe() {
    const value = useAssistant();
    useEffect(() => {
      captured.value = value;
    }, [value]);
    return null;
  }
  return { captured, Probe };
}

describe('AssistantPageContext', () => {
  it('A1: renders nothing at all', () => {
    render(
      <Shell>
        <div data-testid="host">
          <AssistantPageContext projectId="p" workflowId="w" />
        </div>
      </Shell>,
    );

    const host = screen.getByTestId('host');
    expect(host.childNodes).toHaveLength(0);
    expect(host.innerHTML).toBe('');
  });

  it('A2: publishes the page context, drops omitted fields, and clears it on unmount', () => {
    const { captured, Probe } = shellProbe();
    const { rerender } = render(
      <Shell>
        <Probe />
        <AssistantPageContext projectId="p" workflowId="w" />
      </Shell>,
    );
    expect(captured.value?.context).toEqual({ projectId: 'p', workflowId: 'w' });

    rerender(
      <Shell>
        <Probe />
        <AssistantPageContext projectId="p" />
      </Shell>,
    );
    expect(captured.value?.context).toEqual({ projectId: 'p' });
    expect(Object.keys(captured.value?.context ?? {})).toEqual(['projectId']);

    rerender(
      <Shell>
        <Probe />
      </Shell>,
    );
    expect(captured.value?.context).toEqual({});
  });

  it('A3: registers the page handlers, forwards a drawer diff to them, and disposes on unmount', async () => {
    const user = userEvent.setup();
    const spy = vi.fn();
    harness.registerPageSpy = spy;
    const { captured, Probe } = shellProbe();
    const onWorkflowDiff = vi.fn();
    const steps: NamedStep[] = [{ label: 'Analyse', stepType: 'Agent' }];
    const review = <span data-testid="page-review-tray">1 change waiting for review</span>;

    function Page() {
      const { open } = useAssistant();
      useAssistantPageContext({ onWorkflowDiff, currentSteps: steps, review });
      return <button onClick={open}>open assistant</button>;
    }

    const { rerender } = render(
      <Shell>
        <Probe />
        <Page />
      </Shell>,
    );

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.mock.calls[0][0]).toEqual({ onWorkflowDiff, currentSteps: steps, review });
    expect(captured.value?.page.onWorkflowDiff).toBe(onWorkflowDiff);
    expect(captured.value?.page.currentSteps).toBe(steps);
    expect(captured.value?.page.review).toBe(review);

    // What the drawer is handed is what the chrome draws, and a diff it forwards lands on the page.
    await user.click(screen.getByRole('button', { name: 'open assistant' }));
    await user.click(await screen.findByTestId('panel-proposes'));
    expect(onWorkflowDiff).toHaveBeenCalledWith(harness.DIFF);
    expect(within(screen.getByTestId('panel-review')).getByText('1 change waiting for review')).toBeTruthy();

    rerender(
      <Shell>
        <Probe />
      </Shell>,
    );
    expect(captured.value?.page).toEqual({});
  });

  it('A4: ten re-renders of the host register at most four times — no register/re-render loop', () => {
    const spy = vi.fn();
    harness.registerPageSpy = spy;
    const onWorkflowDiff = vi.fn();
    const steps: NamedStep[] = [{ label: 'Analyse', stepType: 'Agent' }];
    const review = <span>tray</span>;

    function Host({ tick }: { tick: number }) {
      // The page's own wiring: callbacks are memoized and the review node is one element, exactly
      // as a page must hand them over — a fresh one per render is the loop this test exists for.
      const handleDiff = useCallback(() => {}, []);
      const handleReview = useMemo(() => review, []);
      useAssistantPageContext({ onWorkflowDiff: handleDiff, currentSteps: steps, review: handleReview });
      return <span data-testid="tick">{tick}</span>;
    }

    const { rerender } = render(
      <Shell>
        <Host tick={0} />
      </Shell>,
    );
    for (let tick = 1; tick <= 10; tick += 1) {
      rerender(
        <Shell>
          <Host tick={tick} />
        </Shell>,
      );
    }

    expect(screen.getByTestId('tick').textContent).toBe('10');
    expect(spy).toHaveBeenCalled();
    expect(spy.mock.calls.length).toBeLessThanOrEqual(4);
    expect(onWorkflowDiff).not.toHaveBeenCalled();
  });
});

describe('pages that contribute context', () => {
  // The files route is a redirect stub: it forwards to the project detail page, which registers
  // the same projectId, so it declares nothing itself and is deliberately not scanned for here.
  it('A6: exactly the three real registrars — project detail, workflow editor and the picture editor', () => {
    expect(readSource(PROJECT_PATH)).toContain('<AssistantPageContext projectId={id} />');
    expect(readSource(WORKFLOW_PATH)).toContain(
      '<AssistantPageContext projectId={projectId} workflowId={workflowId} />',
    );
    // The picture editor is the one page whose project id is in the QUERY rather than the path, so
    // it states both ids instead of leaving them to be re-derived from a route that cannot see it.
    expect(readSource(PICTURE_EDITOR_PATH)).toContain(
      '<AssistantPageContext projectId={projectId} imageDocumentId={documentId} />',
    );

    // Test files are not registrars: a page's own test names the component it mocks.
    const registrars = tsxFiles(appDir())
      .filter((path) => !path.endsWith('.test.tsx'))
      .filter((path) => readFileSync(path, 'utf8').includes('AssistantPageContext'))
      .map((path) => path.slice(appDir().length + 1))
      .sort();

    expect(registrars).toEqual([
      '(app)/editor/thumbnails/[documentId]/page.tsx',
      '(app)/projects/[id]/page.tsx',
      '(app)/projects/[id]/workflows/[workflowId]/page.tsx',
    ]);
  });

  it('A5: the workflow page registers through the bridge, not by hand, and keeps its own tray', () => {
    const source = readSource(WORKFLOW_PATH);

    expect(source).toContain('useAssistantPageContext({ onWorkflowDiff: handleWorkflowDiff, currentSteps: steps, review })');
    expect(source).not.toMatch(/registerPage/);
    expect(source).not.toMatch(/setContext/);

    // The page's own proposal lifecycle is untouched.
    expect(source).toContain('const [pendingDiff, setPendingDiff] = useState<WorkflowEditDiff | null>(null)');
    expect(source).toContain('const applyingRef = useRef(false)');
    expect(source).toContain('const handleApplyDiff = useCallback(');
    expect(source).toContain('err.status === 409');
    expect(source).toContain("t('outOfDateTitle')");

    // ...and its header button opens the one global assistant.
    expect(source).toMatch(/const \{ opened: assistantOpened, open: openAssistant \} = useAssistant\(\)/);
    expect(source).toContain('onClick={openAssistant}');
  });
});

describe('one assistant only', () => {
  it('A7: a single AssistantProvider import site, and no page imports the drawer', () => {
    const production = [...tsxFiles(appDir()), ...tsxFiles(componentsDir())].filter(
      (path) => !path.endsWith('.test.tsx'),
    );
    const relative = (path: string) => path.replace(`${componentsDir()}/`, 'components/');

    const importSites = production
      .filter((path) => /import\s*\{[^}]*\bAssistantProvider\b[^}]*\}\s*from/.test(readFileSync(path, 'utf8')))
      .map(relative);

    expect(importSites).toEqual(['components/assistant/AssistantDrawer.tsx']);

    const appDrawerImporters = tsxFiles(appDir()).filter((path) =>
      readFileSync(path, 'utf8').includes('AssistantDrawer'),
    );

    expect(appDrawerImporters).toEqual([]);
  });
});
