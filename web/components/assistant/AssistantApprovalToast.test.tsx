// @vitest-environment jsdom

/**
 * The toast's lifecycle: it names the newest waiting request, hides itself after the one constant,
 * pauses while someone is using it, and never turns a dismissal into a decision.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ASSISTANT_APPROVAL_TOAST_MS } from '@/lib/assistant/approvals';
import {
  AssistantShellProvider,
  useAssistant,
  type AssistantShellValue,
} from './AssistantShellProvider';
import { AssistantApprovalToast } from './AssistantApprovalToast';

// The shell mounts the real drawer once; these tests only care about the toast, and a second chat
// runtime would fetch and re-render for no reason.
vi.mock('./AssistantDrawer', () => ({ AssistantDrawer: () => null }));

vi.mock('next/navigation', () => ({
  usePathname: () => '/workflows',
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn() }),
}));

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const first = { id: 'a1', title: 'Delete the file “notes.txt”', kind: 'file' as const };
const second = { id: 'a2', title: 'Rename the project', kind: 'project' as const };

beforeEach(() => {
  // Only the toast's clock is faked — its countdown banks elapsed time with Date.now — while
  // Mantine's own frames and transitions stay real.
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
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
});

afterEach(() => {
  vi.useRealTimers();
});

function renderToast() {
  const captured: { value?: AssistantShellValue } = {};
  function Probe() {
    captured.value = useAssistant();
    return null;
  }
  const utils = render(
    <MantineProvider>
      <AssistantShellProvider>
        <Probe />
        <AssistantApprovalToast />
      </AssistantShellProvider>
    </MantineProvider>,
  );
  return { captured, ...utils };
}

const setApprovals = (
  captured: { value?: AssistantShellValue },
  approvals: ReadonlyArray<{ id: string; title: string; kind: 'file' | 'project' }>,
) => act(() => captured.value!.setPendingApprovals(approvals));

const toast = () => screen.queryByTestId('assistant-approval-toast');

describe('AssistantApprovalToast', () => {
  it('names the request and announces it in a polite live region, with the controls outside it', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    expect(toast()?.textContent).toContain(first.title);

    const live = screen.getByRole('status');
    // The region persists across toasts; its text is the announcement, and it carries the framing
    // the card uses ("Needs your OK"), not just the bare title.
    expect(live.textContent).toContain('Needs your OK');
    expect(live.getAttribute('aria-live')).toBe('polite');
    expect(live.getAttribute('aria-atomic')).toBe('true');

    // The buttons are reachable but are not part of what gets re-announced.
    const review = within(toast() as HTMLElement).getByRole('button', { name: 'Review' });
    const dismiss = within(toast() as HTMLElement).getByRole('button', { name: 'Dismiss' });
    expect(live.contains(review)).toBe(false);
    expect(live.contains(dismiss)).toBe(false);
  });

  it('shows the dismissal as a draining countdown bar, driven by the one constant', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    const bar = screen.getByTestId('assistant-approval-countdown');
    // Decorative for assistive tech; the live region carries the words.
    expect(bar.getAttribute('aria-hidden')).toBe('true');
    expect(bar.style.getPropertyValue('--toast-ms')).toBe(`${ASSISTANT_APPROVAL_TOAST_MS}ms`);
    expect(bar.getAttribute('data-paused')).toBeNull();
  });

  it('hides itself only after the full constant, and leaves the request pending', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS - 1);
    });
    expect(toast()).not.toBeNull();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(toast()).toBeNull();
    // A toast's timer is not an answer: the request is still waiting for the person.
    expect(captured.value?.pendingApprovals).toEqual([first]);
  });

  it('dismisses immediately, never returns for the same id, and never touches the request', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    fireEvent.click(within(toast() as HTMLElement).getByRole('button', { name: 'Dismiss' }));
    expect(toast()).toBeNull();
    expect(captured.value?.pendingApprovals).toEqual([first]);

    // An equal list is ignored by the shell; a list that changed only in its words still names the
    // same dismissed request and must stay hidden.
    setApprovals(captured, [{ ...first }]);
    expect(toast()).toBeNull();
    setApprovals(captured, [{ ...first, title: 'Delete the file' }]);
    expect(toast()).toBeNull();
  });

  it('shows a new request after an earlier one was dismissed, and says how many more wait', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    fireEvent.click(within(toast() as HTMLElement).getByRole('button', { name: 'Dismiss' }));
    expect(toast()).toBeNull();

    setApprovals(captured, [first, second]);

    const shown = toast() as HTMLElement;
    expect(shown.textContent).toContain(second.title);
    expect(shown.textContent).toContain('and 1 more waiting');
  });

  it('restarts the whole dismissal window when a newer request replaces the toast', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    // Most of the first request's window is spent, then a second request arrives in place.
    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS - 1000);
    });
    setApprovals(captured, [first, second]);
    expect(toast()?.textContent).toContain(second.title);

    // The newer request gets its own full window, not the 1 s the first one had left.
    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS - 1);
    });
    expect(toast()).not.toBeNull();
    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(toast()).toBeNull();
  });

  it('hides when the last request settles', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);
    expect(toast()).not.toBeNull();

    setApprovals(captured, []);
    expect(toast()).toBeNull();
  });

  it('Review reveals the card, hides the toast, and leaves the request alone', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    const token = captured.value!.approvalFocusToken;
    expect(captured.value?.opened).toBe(false);

    fireEvent.click(within(toast() as HTMLElement).getByRole('button', { name: 'Review' }));

    expect(captured.value?.opened).toBe(true);
    expect(captured.value!.approvalFocusToken).toBeGreaterThan(token);
    // The reveal names THIS request, not whichever card happens to be first in the chat.
    expect(captured.value!.approvalFocusId).toBe(first.id);
    expect(toast()).toBeNull();
    expect(captured.value?.pendingApprovals).toEqual([first]);
  });

  it('hides while the drawer is open, and comes back when it closes', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);
    expect(toast()).not.toBeNull();

    // The card is on screen; on a phone the sheet leaves no safe space for a toast.
    act(() => captured.value!.open());
    expect(toast()).toBeNull();
    expect(captured.value?.pendingApprovals).toEqual([first]);

    act(() => captured.value!.close());
    expect(toast()).not.toBeNull();
  });

  it('pauses the countdown while Review has focus, and restarts it when focus leaves', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    const review = within(toast() as HTMLElement).getByRole('button', { name: 'Review' });

    act(() => review.focus());
    expect(screen.getByTestId('assistant-approval-countdown').getAttribute('data-paused')).toBe('true');
    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS);
    });
    expect(toast()).not.toBeNull();

    act(() => review.blur());
    expect(screen.getByTestId('assistant-approval-countdown').getAttribute('data-paused')).toBeNull();
    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS - 1);
    });
    expect(toast()).not.toBeNull();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(toast()).toBeNull();
  });

  it('pauses the countdown while the pointer is over it', () => {
    const { captured } = renderToast();
    setApprovals(captured, [first]);

    const shown = toast() as HTMLElement;
    fireEvent.mouseEnter(shown);
    expect(screen.getByTestId('assistant-approval-countdown').getAttribute('data-paused')).toBe('true');
    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS);
    });
    expect(toast()).not.toBeNull();

    fireEvent.mouseLeave(shown);
    expect(screen.getByTestId('assistant-approval-countdown').getAttribute('data-paused')).toBeNull();
    act(() => {
      vi.advanceTimersByTime(ASSISTANT_APPROVAL_TOAST_MS);
    });
    expect(toast()).toBeNull();
  });
});
