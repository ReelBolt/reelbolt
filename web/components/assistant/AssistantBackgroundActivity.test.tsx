// @vitest-environment jsdom

/**
 * Background answers: the shell's activity poll, the header badge, the reply toast, the opt-in
 * browser notification and mark-as-read. Real shell + AppShell, stubbed fetch and Notification,
 * fake timers (advanced with the async variant so promises settle).
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { readFileSync } from 'node:fs';
import { AssistantTrigger } from './AssistantTrigger';
import { AssistantReplyToast } from './AssistantReplyToast';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';
import type { AssistantShellValue } from './AssistantShellProvider';
import type { AssistantActivity } from '@/lib/types/assistant';
import { NOTIFY_STORAGE_KEY } from '@/lib/assistant/browser-notify';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { nextActivityDelay } from '@/lib/assistant/activity';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

vi.mock('next/navigation', () => ({
  usePathname: () => '/workflows',
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn() }),
}));

const T1 = '0f0f0f0f-1111-2222-3333-444444444444';
const IDLE: AssistantActivity = { writing: [], unread: [], pendingWatches: 0 };
const WRITING: AssistantActivity = {
  writing: [{ threadId: T1, title: 'Launch teaser', state: 'running', startedAt: 's1', kind: 'reply' }],
  unread: [],
  pendingWatches: 0,
};
const UNREAD: AssistantActivity = {
  writing: [],
  unread: [{ threadId: T1, title: 'Launch teaser', sequence: 4, kind: 'reply', at: 'x' }],
  pendingWatches: 0,
};

let activity: AssistantActivity;
let activityStatus = 200;
let activityCalls = 0;
let readCalls: Array<{ url: string; body: unknown }> = [];
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

function installFetch() {
  activityCalls = 0;
  readCalls = [];
  global.fetch = vi.fn<typeof fetch>((input, init) => {
    const url = String(input);
    if (url.endsWith('/api/v1/assistant/activity')) {
      activityCalls += 1;
      return Promise.resolve(activityStatus === 200 ? json(activity) : json({ error: 'x' }, activityStatus));
    }
    if (url.endsWith('/read')) {
      readCalls.push({ url, body: JSON.parse(typeof init?.body === 'string' ? init.body : '{}') });
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    if (/\/threads\/[^/]+\/turn$/.test(url)) return Promise.resolve(json({ state: 'idle' }));
    if (/\/threads\/[^/]+$/.test(url)) {
      return Promise.resolve(
        json({
          id: T1,
          title: 'Launch teaser',
          projectId: null,
          messages: [
            { id: 'm1', role: 'user', content: 'Hi', sequence: 3, createdAt: '' },
            { id: 'm2', role: 'assistant', content: 'Done.', sequence: 4, createdAt: '' },
          ],
        }),
      );
    }
    return Promise.resolve(json({}));
  }) as unknown as typeof fetch;
}

function setHidden(hidden: boolean) {
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
}

function signIn() {
  const user = { email: 'a@b.c', mustChangePassword: false };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(user))}; path=/`;
}

const captured: { value?: AssistantShellValue } = {};

function mountShell() {
  function Probe() {
    captured.value = useAssistant();
    return null;
  }
  return render(
    <MantineProvider env="test">
      <AssistantShellProvider>
        <Probe />
        <AssistantTrigger />
        <AssistantReplyToast />
      </AssistantShellProvider>
    </MantineProvider>,
  );
}

const advance = (ms: number) =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });

/** Mounts signed in and lets the first (baseline) poll land. */
async function mountSignedIn() {
  signIn();
  mountShell();
  await advance(0);
}

class FakeNotification {
  static permission: NotificationPermission = 'granted';
  static requestPermission = vi.fn(async () => FakeNotification.permission);
  static created: FakeNotification[] = [];
  onclick: (() => void) | null = null;
  close = vi.fn();
  constructor(
    public title: string,
    public options: NotificationOptions,
  ) {
    FakeNotification.created.push(this);
  }
}

beforeEach(() => {
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
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
  activity = IDLE;
  activityStatus = 200;
  installFetch();
  setHidden(false);
  window.localStorage.clear();
  document.cookie = 'reelbolt_user=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT';
  FakeNotification.permission = 'granted';
  FakeNotification.created = [];
  FakeNotification.requestPermission.mockClear();
  vi.stubGlobal('Notification', FakeNotification);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  setHidden(false);
  captured.value = undefined;
});

describe('polling cadence', () => {
  it('never polls when signed out', async () => {
    mountShell();
    await advance(120000);
    expect(activityCalls).toBe(0);
  });

  it('polls every 30 s when idle and closed', async () => {
    await mountSignedIn();
    expect(activityCalls).toBe(1);
    await advance(29000);
    expect(activityCalls).toBe(1);
    await advance(1000);
    expect(activityCalls).toBe(2);
  });

  it('polls every 5 s while something is writing', async () => {
    activity = WRITING;
    await mountSignedIn();
    await advance(5000);
    await advance(5000);
    expect(activityCalls).toBe(3);
  });

  it('polls every 10 s while a run is being waited on, and for a minute after the panel was open', async () => {
    activity = { ...IDLE, pendingWatches: 1 };
    await mountSignedIn();
    await advance(10000);
    expect(activityCalls).toBe(2);
    activity = IDLE;
    await advance(5000);
    const before = activityCalls;
    act(() => captured.value!.open());
    await advance(0);
    act(() => captured.value!.close());
    await advance(5000);
    expect(activityCalls).toBeGreaterThan(before + 1);
  });

  it('pauses while the tab is hidden and polls once when it is visible again', async () => {
    await mountSignedIn();
    setHidden(true);
    await advance(120000);
    expect(activityCalls).toBe(1);
    setHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(activityCalls).toBe(2);
  });

  it('polls at once when the browser comes back online', async () => {
    await mountSignedIn();
    await act(async () => {
      window.dispatchEvent(new Event('online'));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(activityCalls).toBe(2);
  });

  it('backs off after errors instead of retrying quickly', async () => {
    activityStatus = 500;
    await mountSignedIn();
    expect(activityCalls).toBe(1);
    await advance(29000);
    expect(activityCalls).toBe(1);
    await advance(1000);
    expect(activityCalls).toBe(2);
    await advance(59000);
    expect(activityCalls).toBe(2);
    await advance(1000);
    expect(activityCalls).toBe(3);
  });

  it('delay rule: fast when busy, slow otherwise, exponential on failure, capped', () => {
    expect(nextActivityDelay({ activity: WRITING, openedRecently: false, failures: 0 })).toBe(5000);
    expect(nextActivityDelay({ activity: IDLE, openedRecently: false, failures: 0 })).toBe(30000);
    expect(nextActivityDelay({ activity: { ...IDLE, pendingWatches: 1 }, openedRecently: false, failures: 0 })).toBe(10000);
    expect(nextActivityDelay({ activity: { ...IDLE, pendingWatches: 1 }, openedRecently: true, failures: 0 })).toBe(5000);
    expect(nextActivityDelay({ activity: IDLE, openedRecently: true, failures: 0 })).toBe(5000);
    expect(nextActivityDelay({ activity: WRITING, openedRecently: true, failures: 3 })).toBe(120000);
    expect(nextActivityDelay({ activity: IDLE, openedRecently: false, failures: 20 })).toBe(300000);
  });
});

describe('header badge', () => {
  it('combines approvals and unread replies in one count with a clear name', async () => {
    activity = UNREAD;
    await mountSignedIn();
    act(() => captured.value!.setPendingApprovals([{ id: 'a1', title: 'Delete it', kind: 'file' }]));
    const trigger = screen.getByRole('button', { name: 'Open assistant, 1 needs your OK, 1 new reply' });
    expect(trigger.parentElement?.textContent).toContain('2');
  });

  it('shows a writing state in the name and as a marker, and the pulse stops for reduced motion', async () => {
    activity = WRITING;
    await mountSignedIn();
    const trigger = screen.getByRole('button', { name: 'Open assistant, writing a reply' });
    expect(trigger.getAttribute('data-writing')).toBe('true');
    const css = readFileSync(`${process.cwd()}/components/assistant/AssistantTrigger.module.css`, 'utf8');
    expect(css).toMatch(/prefers-reduced-motion: reduce\)\s*{[^}]*\.writing::after\s*{[^}]*animation: none/);
  });

  it('is unchanged with nothing to report', async () => {
    await mountSignedIn();
    expect(screen.getByRole('button', { name: 'Open assistant' }).getAttribute('data-writing')).toBeNull();
  });
});

describe('reply toast', () => {
  it('announces writing, then the reply, and Read opens the thread and marks it read', async () => {
    await mountSignedIn();
    activity = WRITING;
    await advance(30000);
    expect(screen.getByTestId('assistant-reply-toast').textContent).toContain(
      "Assistant is writing a reply in 'Launch teaser'",
    );
    activity = UNREAD;
    await advance(5000);
    const toast = screen.getByTestId('assistant-reply-toast');
    expect(toast.textContent).toContain("Assistant replied in 'Launch teaser'");
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Read' }));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(captured.value!.opened).toBe(true);
    expect(captured.value!.threadId).toBe(T1);
    expect(readCalls[0]).toMatchObject({ url: expect.stringContaining(`/threads/${T1}/read`), body: { throughSequence: 4 } });
    expect(screen.queryByTestId('assistant-reply-toast')).toBeNull();
  });

  it('words a run-triggered turn as picking up now that the run finished', async () => {
    await mountSignedIn();
    activity = { ...WRITING, writing: [{ ...WRITING.writing[0], kind: 'run_wake' }] };
    await advance(30000);
    expect(screen.getByTestId('assistant-reply-toast').textContent).toContain(
      "Assistant is picking up 'Launch teaser' now that your run finished",
    );
  });

  it('hides itself after the countdown, and a dismissed one stays dismissed', async () => {
    await mountSignedIn();
    activity = UNREAD;
    await advance(30000);
    expect(screen.getByTestId('assistant-reply-toast')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByTestId('assistant-reply-toast')).toBeNull();
    await advance(30000);
    await advance(30000);
    expect(screen.queryByTestId('assistant-reply-toast')).toBeNull();
  });

  it('is not shown while the drawer is open on that thread', async () => {
    await mountSignedIn();
    act(() => captured.value!.openThread(T1));
    await advance(0);
    activity = UNREAD;
    await advance(5000);
    expect(screen.queryByTestId('assistant-reply-toast')).toBeNull();
  });

  it('stays silent for a reply this tab is streaming itself', async () => {
    await mountSignedIn();
    act(() => captured.value!.setLocalStreamingThreadId(T1));
    activity = WRITING;
    await advance(30000);
    expect(screen.queryByTestId('assistant-reply-toast')).toBeNull();
  });

  it('does not toast what was already unread when the page loaded', async () => {
    activity = UNREAD;
    await mountSignedIn();
    await advance(30000);
    expect(screen.queryByTestId('assistant-reply-toast')).toBeNull();
  });
});

describe('browser notifications', () => {
  const turnOn = () => window.localStorage.setItem(NOTIFY_STORAGE_KEY, '1');

  async function replyArrives() {
    await mountSignedIn();
    activity = UNREAD;
    await advance(30000);
  }

  it('sends nothing without the switch', async () => {
    await replyArrives();
    expect(FakeNotification.created).toHaveLength(0);
  });

  it('sends nothing without browser permission', async () => {
    turnOn();
    FakeNotification.permission = 'denied';
    await replyArrives();
    expect(FakeNotification.created).toHaveLength(0);
  });

  it('notifies with the thread title only when the drawer is closed, and closes itself', async () => {
    turnOn();
    await replyArrives();
    expect(FakeNotification.created).toHaveLength(1);
    const note = FakeNotification.created[0];
    expect(note.title).toBe('Assistant replied');
    expect(note.options).toMatchObject({ body: 'Launch teaser', tag: T1, requireInteraction: false });
    await advance(10000);
    expect(note.close).toHaveBeenCalled();
  });

  it('stays quiet while the tab is visible and the drawer is open, but not when the tab is hidden', async () => {
    turnOn();
    await mountSignedIn();
    act(() => captured.value!.open());
    activity = UNREAD;
    await advance(5000);
    expect(FakeNotification.created).toHaveLength(0);
    activity = {
      ...UNREAD,
      unread: [{ ...UNREAD.unread[0], sequence: 6 }],
    };
    setHidden(true);
    await act(async () => {
      setHidden(false);
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    setHidden(false);
    setHidden(true);
    // A hidden tab does not poll; the poll that finds the reply happens on return, then the tab is hidden again.
    setHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      setHidden(true);
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(FakeNotification.created).toHaveLength(1);
  });

  it('still notifies after signing out and back in', async () => {
    turnOn();
    await replyArrives();
    expect(FakeNotification.created).toHaveLength(1);
    act(() => {
      document.cookie = 'reelbolt_user=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT';
      notifyAuthChange();
    });
    await advance(0);
    activity = IDLE;
    act(() => {
      signIn();
      notifyAuthChange();
    });
    await advance(0);
    activity = { ...UNREAD, unread: [{ ...UNREAD.unread[0], sequence: 9 }] };
    await advance(30000);
    expect(FakeNotification.created).toHaveLength(2);
  });

  it('says an OK is needed for an approval in a background turn', async () => {
    turnOn();
    await mountSignedIn();
    activity = { ...WRITING, writing: [{ ...WRITING.writing[0], state: 'awaiting_approval' }] };
    await advance(30000);
    expect(FakeNotification.created.map((n) => n.title)).toEqual(['Assistant needs your OK']);
  });

  it('does not notify merely because the assistant started writing', async () => {
    turnOn();
    await mountSignedIn();
    activity = WRITING;
    await advance(30000);
    expect(FakeNotification.created).toHaveLength(0);
  });

  it('clicking it focuses the window and opens the thread', async () => {
    turnOn();
    const focus = vi.spyOn(window, 'focus').mockImplementation(() => {});
    await replyArrives();
    act(() => FakeNotification.created[0].onclick?.());
    expect(focus).toHaveBeenCalled();
    expect(captured.value!.opened).toBe(true);
    expect(captured.value!.threadId).toBe(T1);
  });
});

describe('the notification switch', () => {
  async function openDrawer() {
    await mountSignedIn();
    act(() => captured.value!.open());
    await advance(0);
  }

  it('is off by default and asks nothing on load', async () => {
    await openDrawer();
    const sw = screen.getByLabelText('Notify me when the assistant answers in the background') as HTMLInputElement;
    expect(sw.checked).toBe(false);
    expect(FakeNotification.requestPermission).not.toHaveBeenCalled();
  });

  it('asks for permission from the click and then stays on', async () => {
    await openDrawer();
    await act(async () => {
      fireEvent.click(screen.getByLabelText('Notify me when the assistant answers in the background'));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(FakeNotification.requestPermission).not.toHaveBeenCalled(); // already granted: no second prompt
    expect(window.localStorage.getItem(NOTIFY_STORAGE_KEY)).toBe('1');
  });

  it('prompts when permission is still undecided, and explains a refusal without retrying', async () => {
    FakeNotification.permission = 'default';
    FakeNotification.requestPermission.mockImplementation(async () => {
      FakeNotification.permission = 'denied';
      return 'denied';
    });
    await openDrawer();
    await act(async () => {
      fireEvent.click(screen.getByLabelText('Notify me when the assistant answers in the background'));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(FakeNotification.requestPermission).toHaveBeenCalledTimes(1);
    expect(window.localStorage.getItem(NOTIFY_STORAGE_KEY)).toBeNull();
    expect(screen.getByText(/Notifications are blocked for this site/)).toBeTruthy();
  });

  it('is hidden where the browser has no notifications', async () => {
    vi.stubGlobal('Notification', undefined);
    delete (window as unknown as Record<string, unknown>).Notification;
    await openDrawer();
    expect(screen.queryByLabelText('Notify me when the assistant answers in the background')).toBeNull();
  });
});

describe('marking read', () => {
  it('marks the open thread read once its newest reply is on screen', async () => {
    activity = UNREAD;
    await mountSignedIn();
    act(() => captured.value!.openThread(T1));
    // jsdom 31 settles the thread load a few macrotasks later than 26 did: keep advancing until the mark lands.
    for (let i = 0; i < 6 && readCalls.length === 0; i += 1) await advance(1000);
    expect(readCalls.length).toBeGreaterThanOrEqual(1);
    expect(readCalls[0].url).toContain(`/threads/${T1}/read`);
  });

  it('does not mark a reply read before it is actually displayed', async () => {
    activity = { ...UNREAD, unread: [{ ...UNREAD.unread[0], sequence: 9 }] };
    await mountSignedIn();
    act(() => captured.value!.openThread(T1));
    await advance(3000);
    expect(readCalls).toHaveLength(0);
  });

  it('does not mark anything read while the drawer is closed', async () => {
    activity = UNREAD;
    await mountSignedIn();
    await advance(5000);
    expect(readCalls).toHaveLength(0);
  });
});
