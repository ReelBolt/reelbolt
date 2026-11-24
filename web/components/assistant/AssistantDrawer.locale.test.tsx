// @vitest-environment jsdom

/** A language switch re-renders every `t`; it must not pull focus back into the docked panel. */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { IntlProvider } from '@/i18n/provider';
import { loadMessages } from '@/i18n/messages';
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

function Host() {
  const [locale, setLocale] = useState('en');
  return (
    <MantineProvider>
      <IntlProvider locale={locale} messages={loadMessages(locale)}>
        <button onClick={() => setLocale('it')}>switch</button>
        <AssistantDrawerShell opened onClose={() => {}} />
      </IntlProvider>
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

describe('docked assistant panel and language switch', () => {
  it('does not move focus again when the locale changes', async () => {
    render(<Host />);
    await frame();
    const composer = screen.getByLabelText('composer');
    composer.focus();
    expect(document.activeElement).toBe(composer);
    fireEvent.click(screen.getByText('switch'));
    await frame();
    expect(document.activeElement).toBe(composer);
  });
});
