// @vitest-environment jsdom

/**
 * The shell's affordance (T-019): reachable from the shell, and it offers the page the user is on.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const getThread = vi.fn();
vi.mock('@/lib/api/assistant', () => ({ getThread: (id: string) => getThread(id) }));
vi.mock('@/lib/api/feedback', () => ({ submitFeedback: vi.fn() }));
vi.mock('next/navigation', () => ({ usePathname: () => '/projects/abc' }));
const assistant = { threadId: null as string | null };
vi.mock('@/components/assistant/AssistantShellProvider', () => ({
  useAssistant: () => assistant
}));

import { FeedbackLauncher } from './FeedbackLauncher';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({
      matches: false,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => false
    })
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const renderLauncher = () =>
  render(
    <MantineProvider>
      <FeedbackLauncher />
    </MantineProvider>
  );

describe('FeedbackLauncher', () => {
  it('is a reachable action in the shell', () => {
    renderLauncher();
    expect(screen.getByRole('button', { name: 'Flag a bug or send feedback' })).toBeTruthy();
  });

  it('keeps the flag icon and the accessible name on a phone, hiding only the wide label', () => {
    renderLauncher();

    const button = screen.getByRole('button', { name: 'Flag a bug or send feedback' });
    expect(button.querySelector('svg')).toBeTruthy();
    // The label is the only wide item in the header's right group that was not already responsive;
    // it is hidden below `sm` (Mantine's visibleFrom adds this class) so the row fits a ~400px phone.
    expect(screen.getByText('Feedback').className).toMatch(/visible-from/);
  });

  it('opens the dialog offering the page the user is on', () => {
    assistant.threadId = null;
    renderLauncher();

    fireEvent.click(screen.getByRole('button', { name: 'Flag a bug or send feedback' }));

    const payload = JSON.parse(screen.getByTestId('feedback-payload').textContent ?? '{}');
    expect(payload.context.page).toBe('/projects/abc');
  });

  it('offers the last assistant turns when the shell has a thread', async () => {
    assistant.threadId = 't1';
    getThread.mockResolvedValue({
      id: 't1',
      title: 't',
      projectId: null,
      messages: [{ id: 'm', role: 'assistant', content: 'hello', sequence: 1, createdAt: '' }]
    });
    renderLauncher();

    fireEvent.click(screen.getByRole('button', { name: 'Flag a bug or send feedback' }));

    await screen.findByRole('checkbox', { name: /assistant turns/i });
    const payload = JSON.parse(screen.getByTestId('feedback-payload').textContent ?? '{}');
    expect(payload.context.assistantTurns).toEqual([{ role: 'assistant', content: 'hello' }]);
  });
});
