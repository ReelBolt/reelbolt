// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AUTO_MODE_WARNING, AutoModeConfirmModal } from './AutoModeConfirmModal';

beforeEach(() => {
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

afterEach(() => cleanup());

function mount(props: Partial<React.ComponentProps<typeof AutoModeConfirmModal>> = {}) {
  const onConfirm = vi.fn();
  const onCancel = vi.fn();
  render(
    <MantineProvider env="test">
      <AutoModeConfirmModal opened onConfirm={onConfirm} onCancel={onCancel} {...props} />
    </MantineProvider>,
  );
  return { onConfirm, onCancel };
}

describe('AutoModeConfirmModal', () => {
  it('says what auto mode includes, in the words the person is agreeing to', () => {
    mount();

    const dialog = screen.getByRole('dialog', { name: 'Let the assistant act without asking?' });
    expect(dialog.textContent).toContain(AUTO_MODE_WARNING);
    expect(AUTO_MODE_WARNING).toMatch(/deleting things — without asking you first. It still asks when an action is expected to cost more than the cost limit/);
    expect(AUTO_MODE_WARNING).toMatch(/still see what it did in the chat/);
    expect(AUTO_MODE_WARNING).toMatch(/switch back at any time/);
  });

  it('puts focus on the safe answer, not the one that removes the checks', async () => {
    mount();

    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Keep asking' })));
  });

  it('confirms only through "Turn on auto mode"', async () => {
    const { onConfirm, onCancel } = mount();

    await userEvent.setup().click(screen.getByRole('button', { name: 'Turn on auto mode' }));

    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onCancel).not.toHaveBeenCalled();
  });

  it.each([
    ['the safe button', async () => userEvent.setup().click(screen.getByRole('button', { name: 'Keep asking' }))],
    ['Escape', async () => userEvent.setup().keyboard('{Escape}')],
    ['the close button', async () => userEvent.setup().click(screen.getByRole('button', { name: 'Close and keep asking' }))],
  ])('%s keeps asking', async (_label, act) => {
    const { onConfirm, onCancel } = mount();

    await act();

    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('renders nothing while closed', () => {
    mount({ opened: false });

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
