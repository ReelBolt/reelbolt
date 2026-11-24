// @vitest-environment jsdom

/** C3: the docked (>=1024px) non-modal panel, with the REAL Mantine Drawer. */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useState } from 'react';

vi.mock('./AssistantChatPanel', () => ({ AssistantChatPanel: () => <input aria-label="composer" /> }));
vi.mock('@/lib/editing/use-device-class', () => ({ useIsPhoneClass: () => false }));
vi.mock('@/components/editor/phone/use-keyboard-inset', () => ({ useKeyboardInset: () => 0 }));
vi.mock('./AssistantProvider', () => ({ useAssistantHistory: () => ({ status: 'ready', error: null, reload: vi.fn() }) }));
vi.mock('@/lib/hooks/use-assistant-preferences', () => ({
  useAssistantPreferences: () => ({ mode: 'ask', loaded: true, error: undefined, setMode: vi.fn(), autoApproveCostLimitCredits: undefined }),
}));
vi.mock('@/lib/assistant/browser-notify', () => ({
  enableNotifications: vi.fn(), getNotifySwitch: () => false, isNotificationSupported: () => false,
  notificationPermission: () => 'default', setNotifySwitch: vi.fn(), subscribeNotifySwitch: () => () => {},
}));

import { AssistantDrawerShell } from './AssistantDrawer.shell';

function Host({ onClose }: { onClose: () => void }) {
  const [opened, setOpened] = useState(false);
  const [n, setN] = useState(0);
  return (
    <MantineProvider>
      <button onClick={() => setOpened(true)}>trigger</button>
      <button onClick={() => setN(n + 1)}>rerender</button>
      <AssistantDrawerShell opened={opened} onClose={() => { onClose(); setOpened(false); }} />
    </MantineProvider>
  );
}

const frame = () => act(async () => { await new Promise((r) => setTimeout(r, 50)); });
const dialog = () => document.querySelector('[role="dialog"]') as HTMLElement;

beforeEach(() => {
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  window.matchMedia = ((q: string) => ({
    matches: q.includes('64em'), media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

describe('docked assistant panel', () => {
  it('is not aria-modal (sticky across rerender), takes focus on open, gives it back on close', async () => {
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    const trigger = screen.getByText('trigger');
    trigger.focus();
    fireEvent.click(trigger);
    await frame();
    expect(dialog().getAttribute('aria-modal')).toBe('false');
    expect(dialog().contains(document.activeElement)).toBe(true);
    fireEvent.click(screen.getByText('rerender'));
    await frame();
    expect(dialog().getAttribute('aria-modal')).toBe('false');
    expect(screen.queryByRole('presentation')).toBeNull();
    fireEvent.click(screen.getByLabelText('Close assistant panel'));
    await frame();
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(trigger);
  });

  it('Escape during IME composition does not close it', async () => {
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    fireEvent.click(screen.getByText('trigger'));
    await frame();
    fireEvent.keyDown(screen.getByLabelText('composer'), { key: 'Escape', isComposing: true });
    expect(onClose).not.toHaveBeenCalled();
  });

  async function openPanel(onClose: () => void) {
    render(<Host onClose={onClose} />);
    fireEvent.click(screen.getByText('trigger'));
    await frame();
  }

  it('Escape with focus inside the panel closes it', async () => {
    const onClose = vi.fn();
    await openPanel(onClose);
    screen.getByLabelText('composer').focus();
    fireEvent.keyDown(screen.getByLabelText('composer'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('Escape with focus outside the panel does not close it', async () => {
    const onClose = vi.fn();
    await openPanel(onClose);
    screen.getByText('rerender').focus();
    fireEvent.keyDown(document.body, { key: 'Escape' });
    fireEvent.keyDown(screen.getByText('rerender'), { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('Escape does not close it while a menu or popover is open inside', async () => {
    const onClose = vi.fn();
    await openPanel(onClose);
    const menu = document.createElement('div');
    menu.setAttribute('role', 'menu');
    document.body.appendChild(menu);
    screen.getByLabelText('composer').focus();
    fireEvent.keyDown(screen.getByLabelText('composer'), { key: 'Escape' });
    menu.remove();
    expect(onClose).not.toHaveBeenCalled();
  });
});
