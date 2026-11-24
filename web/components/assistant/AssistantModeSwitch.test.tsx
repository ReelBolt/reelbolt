// @vitest-environment jsdom

/**
 * The "Ask before changes" switch in the drawer's header, the line under the assistant's name that
 * follows it, and the confirmation in front of auto mode. Real Mantine drawer and modal; only
 * `fetch` and the toast are replaced.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

const notify = vi.hoisted(() => vi.fn());
vi.mock('@mantine/notifications', () => ({ notifications: { show: notify } }));

import { AssistantProvider } from './AssistantProvider';
import { AssistantDrawerShell } from './AssistantDrawer.shell';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

let stored: 'ask' | 'auto';
let puts: unknown[];
let putStatus: number;
/** While set, the preference read waits — the window before the server has answered. */
let holdGet: Promise<void> | null;

function installFetch() {
  global.fetch = vi.fn<typeof fetch>(async (input, init) => {
    const url = String(input);
    const json = (body: unknown, status = 200) =>
      new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
    if (url.endsWith('/api/v1/assistant/preferences')) {
      if (init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)) as { approvalMode: 'ask' | 'auto' };
        puts.push(body);
        if (putStatus !== 200) return json({ message: 'no' }, putStatus);
        stored = body.approvalMode;
        return json({ approvalMode: stored });
      }
      if (holdGet) await holdGet;
      return json({ approvalMode: stored });
    }
    return json([]);
  }) as unknown as typeof fetch;
}

function mount(onClose: () => void = () => {}) {
  return render(
    <MantineProvider env="test">
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <AssistantProvider context={{}}>
          <AssistantDrawerShell opened onClose={onClose} />
        </AssistantProvider>
      </SWRConfig>
    </MantineProvider>,
  );
}

// The drawer is itself a dialog, so the confirmation is found by its own name.
const CONFIRM = 'Let the assistant act without asking?';
const confirmation = () => screen.queryByRole('dialog', { name: CONFIRM });
const toggle = () => screen.getByRole('switch', { name: 'Ask before changes' }) as HTMLInputElement;
const user = () => userEvent.setup();

beforeEach(() => {
  stored = 'ask';
  puts = [];
  putStatus = 200;
  holdGet = null;
  notify.mockReset();
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
});

describe('the Ask before changes switch', () => {
  it('is on by default, and the line under the name says the assistant asks', async () => {
    mount();

    await waitFor(() => expect(toggle().disabled).toBe(false));
    expect(toggle().checked).toBe(true);
    expect(screen.getByText('Asks before it changes anything.')).toBeTruthy();
    // The old, static claim is gone: it was not true once there was an auto mode.
    expect(screen.queryByText(/You approve every change/)).toBeNull();
    expect(screen.queryByText(/Proposes edits/)).toBeNull();
  });

  it('cannot be flipped before it is known what it would be flipping', async () => {
    let release: () => void = () => {};
    holdGet = new Promise<void>((resolve) => { release = resolve; });
    mount();

    expect(toggle().disabled).toBe(true);

    release();
    await waitFor(() => expect(toggle().disabled).toBe(false));
  });

  it('shows auto mode, in words, when the server is in it', async () => {
    stored = 'auto';
    mount();

    await waitFor(() => expect(toggle().checked).toBe(false));
    expect(screen.getByText('Auto mode: changes happen without asking, except costly ones.')).toBeTruthy();
    expect(screen.getByText('Auto mode')).toBeTruthy();
  });

  it('turning it off asks first, in plain words, and saves nothing until the person agrees', async () => {
    mount();
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());

    const dialog = await screen.findByRole('dialog', { name: CONFIRM });
    expect(dialog.textContent).toContain(
      "In auto mode the assistant makes changes straight away — including deleting things — without asking you first. It still asks when an action is expected to cost more than the cost limit shown under the switch (or when its cost can't be estimated); the limit applies to each action, not to their total. You'll still see what it did in the chat. You can switch back at any time.",
    );
    expect(screen.getByRole('button', { name: 'Turn on auto mode' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Keep asking' })).toBeTruthy();
    expect(puts).toEqual([]);
    // Still on, under the dialog: the switch only moves once the person has said yes.
    expect(toggle().checked).toBe(true);
  });

  it('"Turn on auto mode" saves it, switches off, and the line follows', async () => {
    mount();
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());
    await user().click(await screen.findByRole('button', { name: 'Turn on auto mode' }));

    await waitFor(() => expect(puts).toEqual([{ approvalMode: 'auto' }]));
    await waitFor(() => expect(toggle().checked).toBe(false));
    expect(screen.getByText('Auto mode: changes happen without asking, except costly ones.')).toBeTruthy();
    await waitFor(() => expect(confirmation()).toBeNull());
  });

  it('"Keep asking" leaves everything as it was', async () => {
    mount();
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());
    await user().click(await screen.findByRole('button', { name: 'Keep asking' }));

    await waitFor(() => expect(confirmation()).toBeNull());
    expect(puts).toEqual([]);
    expect(toggle().checked).toBe(true);
    expect(screen.getByText('Asks before it changes anything.')).toBeTruthy();
  });

  it('Escape is the safe answer too', async () => {
    mount();
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());
    await screen.findByRole('dialog', { name: CONFIRM });
    await user().keyboard('{Escape}');

    await waitFor(() => expect(confirmation()).toBeNull());
    expect(puts).toEqual([]);
    expect(toggle().checked).toBe(true);
  });

  it('Escape in the dialog closes the dialog only — the assistant stays open — and the next one closes the assistant', async () => {
    const onClose = vi.fn();
    mount(onClose);
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());
    await screen.findByRole('dialog', { name: CONFIRM });
    await user().keyboard('{Escape}');

    await waitFor(() => expect(confirmation()).toBeNull());
    expect(onClose).not.toHaveBeenCalled();

    // With the dialog gone, Escape is the drawer's again.
    await user().keyboard('{Escape}');
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });

  it('turning it back on is immediate: no dialog, one save', async () => {
    stored = 'auto';
    mount();
    await waitFor(() => expect(toggle().checked).toBe(false));

    await user().click(toggle());

    await waitFor(() => expect(puts).toEqual([{ approvalMode: 'ask' }]));
    expect(confirmation()).toBeNull();
    await waitFor(() => expect(toggle().checked).toBe(true));
    expect(screen.getByText('Asks before it changes anything.')).toBeTruthy();
  });

  it('puts the switch back and says so calmly when the save fails', async () => {
    putStatus = 500;
    mount();
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());
    await user().click(await screen.findByRole('button', { name: 'Turn on auto mode' }));

    await waitFor(() => expect(notify).toHaveBeenCalledTimes(1));
    expect((notify.mock.calls[0][0] as { title: string }).title).toBe("Couldn't change that setting");
    await waitFor(() => expect(toggle().checked).toBe(true));
    expect(screen.getByText('Asks before it changes anything.')).toBeTruthy();
  });

  it('keeps the dialog\'s safe button as the one that has focus', async () => {
    mount();
    await waitFor(() => expect(toggle().disabled).toBe(false));

    await user().click(toggle());
    const keep = await screen.findByRole('button', { name: 'Keep asking' });

    await waitFor(() => expect(document.activeElement).toBe(keep));
  });
});
