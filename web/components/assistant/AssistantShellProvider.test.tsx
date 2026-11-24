// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { AppShell } from '@/components/shell/AppShell';
import {
  AssistantShellProvider,
  useAssistant,
  type AssistantPageRegistration,
  type AssistantShellValue,
} from './AssistantShellProvider';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

// NavLinks reads the app-router path, which is absent under Vitest; without this the shell throws
// on `pathname.startsWith`. Same stub shape the assistant's other shell-level tests use.
vi.mock('next/navigation', () => ({
  usePathname: () => '/workflows',
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn() }),
}));

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
});

afterEach(() => {
  cleanup();
});

/** The real shell, mounted the way `app/(app)/layout.tsx` mounts it. */
function renderShell(captured?: { value?: AssistantShellValue }) {
  function Probe() {
    captured!.value = useAssistant();
    return null;
  }
  return render(
    <MantineProvider>
      <AssistantShellProvider>
        {captured ? <Probe /> : null}
        <AppShell>
          <div>page body</div>
        </AppShell>
      </AssistantShellProvider>
    </MantineProvider>,
  );
}

function renderProbe() {
  const captured: { value?: AssistantShellValue; renders: number } = { renders: 0 };
  function Probe() {
    captured.value = useAssistant();
    captured.renders += 1;
    return null;
  }
  const utils = render(
    <MantineProvider>
      <AssistantShellProvider>
        <Probe />
      </AssistantShellProvider>
    </MantineProvider>,
  );
  return { captured, ...utils };
}

const readSource = (relative: string) =>
  readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');

describe('AssistantShellProvider', () => {
  it('openWithPrompt opens the drawer, queues the prompt once and exposes extra context', () => {
    const { captured } = renderProbe();
    act(() => captured.value!.openWithPrompt('Explain this field', { highlightedField: 'minScore' }));
    expect(captured.value!.opened).toBe(true);
    expect(captured.value!.pendingPrompt).toMatchObject({ prompt: 'Explain this field', send: false });
    expect(captured.value!.extraContext).toEqual({ highlightedField: 'minScore' });
    const id = captured.value!.pendingPrompt!.id;
    act(() => captured.value!.consumePrompt(id));
    expect(captured.value!.pendingPrompt).toBeNull();
  });

  it('A1: useAssistant() inside the provider returns exactly the documented shell value', () => {
    const { captured } = renderProbe();
    const value = captured.value;

    expect(value).toBeDefined();
    expect(Object.keys(value as object).sort()).toEqual(
      [
        'opened',
        'open',
        'close',
        'toggle',
        'threadId',
        'setThreadId',
        'fillThreadIdIfEmpty',
        'selectionToken',
        'context',
        'setContext',
        'page',
        'pageInfo',
        'extraContext',
        'openWithPrompt',
        'pendingPrompt',
        'threadsVersion',
        'bumpThreads',
        'consumePrompt',
        'registerPage',
        'pendingApprovals',
        'setPendingApprovals',
        'approvalFocusToken',
        'approvalFocusId',
        'revealPendingApproval',
        'activity',
        'activityEvents',
        'openThread',
        'markThreadRead',
        'setLocalStreamingThreadId',
      ].sort(),
    );
    expect(typeof value?.open).toBe('function');
    expect(typeof value?.close).toBe('function');
    expect(typeof value?.toggle).toBe('function');
    expect(typeof value?.setThreadId).toBe('function');
    expect(typeof value?.fillThreadIdIfEmpty).toBe('function');
    expect(typeof value?.selectionToken).toBe('function');
    expect(typeof value?.setContext).toBe('function');
    expect(typeof value?.registerPage).toBe('function');
    expect(typeof value?.setPendingApprovals).toBe('function');
    expect(typeof value?.revealPendingApproval).toBe('function');
    expect(value?.pendingApprovals).toEqual([]);
    expect(value?.approvalFocusToken).toBe(0);
  });

  it('A1: the exposed value actually drives the shell state', () => {
    const { captured } = renderProbe();

    expect(captured.value?.opened).toBe(false);
    expect(captured.value?.threadId).toBeNull();
    expect(captured.value?.context).toEqual({});

    act(() => captured.value?.open());
    expect(captured.value?.opened).toBe(true);

    act(() => captured.value?.close());
    expect(captured.value?.opened).toBe(false);

    act(() => captured.value?.toggle());
    expect(captured.value?.opened).toBe(true);

    act(() => captured.value?.setThreadId('thread-1'));
    expect(captured.value?.threadId).toBe('thread-1');

    // The token is a claim counter, not the selection: a claim advances it, the fallback fill does
    // not. Counting the fallback as well would let a visit-create that landed first invalidate a
    // "New chat" the user had already clicked.
    const afterClaim = captured.value!.selectionToken();
    expect(afterClaim).toBeGreaterThan(0);

    act(() => captured.value?.fillThreadIdIfEmpty('thread-2'));
    expect(captured.value?.threadId).toBe('thread-1');
    expect(captured.value?.selectionToken()).toBe(afterClaim);

    act(() => captured.value?.setThreadId('thread-2'));
    expect(captured.value?.threadId).toBe('thread-2');
    expect(captured.value?.selectionToken()).toBeGreaterThan(afterClaim);

    act(() => captured.value?.setContext({ projectId: 'p1', workflowId: 'w1' }));
    expect(captured.value?.context).toEqual({ projectId: 'p1', workflowId: 'w1' });
  });

  it('A1: pending approvals publish, ignore an identical list, and reveal the drawer on demand', () => {
    const { captured } = renderProbe();
    const first = [{ id: 'a1', title: 'Delete notes.txt', kind: 'file' as const }];

    act(() => captured.value!.setPendingApprovals(first));
    expect(captured.value?.pendingApprovals).toBe(first);
    const setter = captured.value!.setPendingApprovals;

    // Same requests, same words: the published array keeps its identity, so a consumer that writes
    // it back from an effect cannot loop.
    act(() => captured.value!.setPendingApprovals([{ id: 'a1', title: 'Delete notes.txt', kind: 'workflow' }]));
    expect(captured.value?.pendingApprovals).toBe(first);
    expect(captured.value!.setPendingApprovals).toBe(setter);

    const token = captured.value!.approvalFocusToken;
    act(() => captured.value!.revealPendingApproval('a1'));
    expect(captured.value?.opened).toBe(true);
    expect(captured.value!.approvalFocusToken).toBeGreaterThan(token);
    // The id travels with the request, so the reveal can land on the card the toast named.
    expect(captured.value!.approvalFocusId).toBe('a1');
  });

  it('A1: useAssistant() outside the provider throws an Error naming AssistantShellProvider', () => {
    function Outside() {
      useAssistant();
      return null;
    }
    // React logs the render error to console.error before it surfaces; keep the run output clean.
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      expect(() =>
        render(
          <MantineProvider>
            <Outside />
          </MantineProvider>,
        ),
      ).toThrow(/AssistantShellProvider/);
    } finally {
      consoleError.mockRestore();
    }
  });

  it('A6: registerPage stores the registration and notifies subscribers', () => {
    const { captured } = renderProbe();
    const registration: AssistantPageRegistration = {
      currentSteps: [{ label: 'Analyze', stepType: 'VideoAnalyze' }],
    };
    const rendersBefore = captured.renders;

    act(() => {
      captured.value?.registerPage(registration);
    });

    expect(captured.value?.page).toBe(registration);
    expect(captured.renders).toBeGreaterThan(rendersBefore);
  });

  it('A6: the disposer restores the previous value', () => {
    const { captured } = renderProbe();
    const first: AssistantPageRegistration = { currentSteps: [{ label: 'First' }] };
    let dispose = () => {};

    act(() => {
      dispose = captured.value!.registerPage(first);
    });
    expect(captured.value?.page).toBe(first);

    act(() => dispose());

    expect(captured.value?.page).toEqual({});
    expect(captured.value?.page).not.toBe(first);
  });

  it('A6: a disposer run after a newer registration does not clobber it, and is idempotent', () => {
    const { captured } = renderProbe();
    const older: AssistantPageRegistration = { currentSteps: [{ label: 'Older' }] };
    const newer: AssistantPageRegistration = { currentSteps: [{ label: 'Newer' }] };
    let disposeOlder = () => {};

    act(() => {
      disposeOlder = captured.value!.registerPage(older);
    });
    act(() => {
      captured.value!.registerPage(newer);
    });
    expect(captured.value?.page).toBe(newer);

    act(() => disposeOlder());
    expect(captured.value?.page).toBe(newer);

    act(() => disposeOlder());
    expect(captured.value?.page).toBe(newer);
  });
});

describe('AssistantTrigger in AppShell', () => {
  it('A2: the trigger names itself, reflects aria-expanded, and toggles on press', async () => {
    const user = userEvent.setup();
    renderShell();

    const open = screen.getByRole('button', { name: 'Open assistant' });
    expect(open.getAttribute('aria-expanded')).toBe('false');

    await user.click(open);

    const close = screen.getByRole('button', { name: 'Close assistant' });
    expect(close.getAttribute('aria-expanded')).toBe('true');
    expect(screen.queryByRole('button', { name: 'Open assistant' })).toBeNull();

    await user.click(close);

    const reopened = screen.getByRole('button', { name: 'Open assistant' });
    expect(reopened.getAttribute('aria-expanded')).toBe('false');
  });

  it('A6: the drawer close control and the header trigger never share an accessible name', async () => {
    const user = userEvent.setup();
    renderShell();

    await user.click(screen.getByRole('button', { name: 'Open assistant' }));

    // Both controls are on screen at once. The trigger flips its own name to 'Close assistant',
    // so the drawer's close button must answer to something else — otherwise `getByRole` (and a
    // screen-reader user) cannot tell them apart.
    const trigger = screen.getByRole('button', { name: 'Close assistant' });
    expect(trigger.getAttribute('aria-expanded')).toBe('true');

    // Mantine mounts the drawer's body into a portal on the frame after `opened` flips, so the
    // panel's own close control is not queryable synchronously after the click — asserting it
    // right away reads an empty drawer root on a slow frame and fails the file intermittently.
    // `findByRole` polls until the control lands; every assertion here is unchanged.
    const panelClose = await screen.findByRole('button', { name: 'Close assistant panel' });
    expect(panelClose).not.toBe(trigger);

    // Exactly those two, each with its own distinct name.
    expect(screen.getAllByRole('button', { name: /^close assistant/i })).toHaveLength(2);
  });

  it('A2: at zero pending approvals the trigger keeps its exact names and shows no number', () => {
    renderShell();

    const trigger = screen.getByRole('button', { name: 'Open assistant' });
    expect(trigger.parentElement?.textContent).not.toMatch(/\d/);
  });

  it('A2: one pending approval is named in the singular and counted on the badge', () => {
    const captured: { value?: AssistantShellValue } = {};
    renderShell(captured);

    act(() => {
      captured.value!.setPendingApprovals([{ id: 'a1', title: 'Delete notes.txt', kind: 'file' }]);
    });

    const trigger = screen.getByRole('button', { name: 'Open assistant, 1 approval waiting' });
    // The Indicator renders the count beside the button, inside its own wrapper.
    expect(trigger.parentElement?.textContent).toContain('1');
  });

  it('A2: two pending approvals use the plural, and pressing the badge opens on the card', async () => {
    const user = userEvent.setup();
    const captured: { value?: AssistantShellValue } = {};
    renderShell(captured);

    act(() => {
      captured.value!.setPendingApprovals([
        { id: 'a1', title: 'Delete notes.txt', kind: 'file' },
        { id: 'a2', title: 'Rename the project', kind: 'project' },
      ]);
    });

    const trigger = screen.getByRole('button', { name: 'Open assistant, 2 approvals waiting' });
    expect(trigger.parentElement?.textContent).toContain('2');

    const token = captured.value!.approvalFocusToken;
    await user.click(trigger);

    expect(captured.value?.opened).toBe(true);
    expect(captured.value!.approvalFocusToken).toBeGreaterThan(token);
    // Open, the button can no longer close the drawer, and it must not claim to.
    expect(
      screen.getByRole('button', { name: 'Show assistant, 2 approvals waiting' }).getAttribute('aria-expanded'),
    ).toBe('true');
  });

  it('A3: a page that renders no PageHeader still reaches exactly one trigger, inside the header', () => {
    // The body below is a bare <div>: no PageHeader, no registration — the 15-of-22-page case.
    const { container } = renderShell();

    const triggers = screen.getAllByRole('button', { name: 'Open assistant' });
    expect(triggers).toHaveLength(1);

    const header = container.querySelector('header');
    expect(header).not.toBeNull();
    expect(header!.contains(triggers[0])).toBe(true);

    // AppShell's own Title is the only heading a siteless page renders; PageHeader would add an h2.
    expect(container.querySelector('h2')).toBeNull();
  });

  it('A4: the header keeps the Burger first and the assistant trigger beside it', () => {
    const { container } = renderShell();
    const header = container.querySelector('header')!;

    // The theme toggle also matches /toggle/i, so narrow the regex hit to the Mantine Burger
    // itself via its own root class rather than picking an arbitrary match.
    const toggleNamed = within(header).getAllByRole('button', { name: /navigation|menu|toggle/i });
    const burger = toggleNamed.find((element) => element.classList.contains('mantine-Burger-root'));
    const trigger = within(header).getByRole('button', { name: 'Open assistant' });

    expect(burger).toBeDefined();
    expect(burger).not.toBe(trigger);
    expect(burger!.compareDocumentPosition(trigger) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

describe('app/(app)/layout.tsx', () => {
  const layoutPath = '../../app/(app)/layout.tsx';

  it('A5: wraps AppShell in AssistantShellProvider and names no drawer or data-stream provider', () => {
    const source = readSource(layoutPath);

    expect(source).toContain('AssistantShellProvider');
    expect(source.indexOf('<AssistantShellProvider>')).toBeGreaterThan(-1);
    // `<AppShell` rather than `<AppShell>`: the shell now takes the video-editor flag as a prop
    // (T-014), so the opening tag carries an attribute. What this pins is the nesting order, which
    // is unchanged.
    expect(source.indexOf('<AssistantShellProvider>')).toBeLessThan(source.indexOf('<AppShell'));

    expect(source).not.toContain('AssistantDrawer');
    expect(source).not.toMatch(/AssistantProvider/);
  });

  it('A5: none of this ticket\'s files imports the data-stream AssistantProvider', () => {
    const sources = {
      'AssistantShellProvider.tsx': readSource('./AssistantShellProvider.tsx'),
      'AssistantTrigger.tsx': readSource('./AssistantTrigger.tsx'),
      'layout.tsx': readSource(layoutPath),
      'AppShell.tsx': readSource('../../components/shell/AppShell.tsx'),
    };

    for (const [name, source] of Object.entries(sources)) {
      expect(source, `${name} must not reference AssistantProvider`).not.toMatch(/AssistantProvider/);
      expect(source, `${name} must not import @assistant-ui`).not.toContain('@assistant-ui');
    }
  });
});
