// @vitest-environment jsdom

/** C4: the phone settings popover, with the REAL Popover and the REAL confirm dialog. */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';

const h = vi.hoisted(() => ({ setMode: vi.fn(async () => true), mode: 'ask' as 'ask' | 'auto', perm: 'default' as string }));

vi.mock('@/lib/hooks/use-assistant-preferences', () => ({
  useAssistantPreferences: () => ({ mode: h.mode, loaded: true, error: undefined, setMode: h.setMode, autoApproveCostLimitCredits: undefined }),
}));
vi.mock('@/lib/assistant/browser-notify', () => ({
  enableNotifications: vi.fn(async () => 'denied'),
  getNotifySwitch: () => false,
  isNotificationSupported: () => true,
  notificationPermission: () => h.perm,
  setNotifySwitch: vi.fn(),
  subscribeNotifySwitch: () => () => {},
}));

import { AssistantPhoneSettings } from './AssistantDrawer.shell';

function ui() {
  return (
    <MantineProvider>
      <AssistantPhoneSettings />
    </MantineProvider>
  );
}

beforeEach(() => {
  h.setMode.mockClear();
  h.mode = 'ask';
  h.perm = 'default';
  window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

describe('AssistantPhoneSettings', () => {
  it('keeps the switches behind one settings button', async () => {
    render(ui());
    expect(screen.queryByLabelText('Ask before changes')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Assistant settings' }));
    expect(await screen.findByLabelText('Ask before changes')).toBeTruthy();
  });

  it('turning auto mode on still needs the confirmation, and a slow press confirms exactly once', async () => {
    render(ui());
    await userEvent.click(screen.getByRole('button', { name: 'Assistant settings' }));
    await userEvent.click(await screen.findByLabelText('Ask before changes'));
    expect(h.setMode).not.toHaveBeenCalled();
    const confirm = await screen.findByRole('button', { name: 'Turn on auto mode' });
    // A real press: pointer down, a pause, then release.
    fireEvent.pointerDown(confirm);
    fireEvent.mouseDown(confirm);
    await act(async () => {
      await new Promise((r) => setTimeout(r, 400));
    });
    fireEvent.pointerUp(confirm);
    fireEvent.mouseUp(confirm);
    fireEvent.click(confirm);
    expect(h.setMode).toHaveBeenCalledTimes(1);
    expect(h.setMode).toHaveBeenCalledWith('auto');
  });

  it('cancelling the confirmation keeps asking', async () => {
    render(ui());
    await userEvent.click(screen.getByRole('button', { name: 'Assistant settings' }));
    await userEvent.click(await screen.findByLabelText('Ask before changes'));
    await userEvent.click(await screen.findByRole('button', { name: 'Keep asking' }));
    expect(h.setMode).not.toHaveBeenCalled();
  });

  it('shows on the strip whether auto mode is on', () => {
    h.mode = 'auto';
    render(ui());
    expect(screen.getByText('Auto mode is on')).toBeTruthy();
  });

  it('does not offer Notify me when notifications are already blocked', async () => {
    h.perm = 'denied';
    render(ui());
    await userEvent.click(screen.getByRole('button', { name: 'Assistant settings' }));
    await screen.findByLabelText('Ask before changes');
    expect(screen.queryByLabelText(/Notify me/)).toBeNull();
  });
});
