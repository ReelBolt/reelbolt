// @vitest-environment jsdom

/**
 * Stop while a permission card is open: Stop only aborts this screen's stream, the server keeps the card and
 * the turn, so the screen declines the card for the person — once — and never reconnects to the stopped turn.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';

const state = vi.hoisted(() => ({
  messages: [] as unknown[],
  isRunning: false,
}));
const respond = vi.hoisted(() => vi.fn(() => Promise.resolve()));

vi.mock('@assistant-ui/react', () => ({
  useAuiState: (selector: (s: unknown) => unknown) =>
    selector({ thread: { messages: state.messages, isRunning: state.isRunning } }),
}));
vi.mock('@/lib/api/assistant', () => ({ respondToApproval: respond }));

import { AssistantResumeOnReturn } from './AssistantResumeOnReturn';

const reply = (status: { type: string; reason?: string }, actionState: string) => ({
  id: 'r1',
  role: 'assistant',
  status,
  metadata: {
    unstable_data: [
      {
        name: 'assistantAction',
        data: { id: 'card-1', state: actionState, mode: 'ask', kind: 'file', risk: 'destructive', title: 'Delete it' },
      },
    ],
  },
});

afterEach(() => {
  cleanup();
  respond.mockClear();
});

describe('AssistantResumeOnReturn after Stop', () => {
  it('declines the card the person left open, once, and does not reload', async () => {
    const reload = vi.fn();
    state.messages = [reply({ type: 'incomplete', reason: 'cancelled' }, 'pending')];

    const { rerender } = render(<AssistantResumeOnReturn onReload={reload} />);
    rerender(<AssistantResumeOnReturn onReload={reload} />);
    window.dispatchEvent(new Event('pageshow'));
    await new Promise((resolve) => setTimeout(resolve, 1200));

    expect(respond).toHaveBeenCalledTimes(1);
    expect(respond).toHaveBeenCalledWith('card-1', 'deny');
    expect(reload).not.toHaveBeenCalled();
  });

  it('leaves a card alone when the reply was not stopped by the person', () => {
    state.messages = [reply({ type: 'complete' }, 'pending')];
    render(<AssistantResumeOnReturn onReload={vi.fn()} />);

    expect(respond).not.toHaveBeenCalled();
  });
});
