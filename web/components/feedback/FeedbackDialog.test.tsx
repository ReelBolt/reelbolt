// @vitest-environment jsdom

/**
 * T-019's web half. The dialog must show the user exactly what will be attached, let them remove
 * any of it, and send the assembled payload — and nothing else.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const submitFeedback = vi.fn();
vi.mock('@/lib/api/feedback', () => ({
  submitFeedback: (payload: unknown) => submitFeedback(payload)
}));

const collect = vi.fn();
vi.mock('@/lib/diagnostics/shell-diagnostics', () => ({
  collectShellDiagnostics: (env: unknown) => collect(env)
}));

import { FeedbackDialog } from './FeedbackDialog';
import type { FeedbackCandidates } from '@/lib/feedback/context';

const PROJECT = '11111111-1111-4111-8111-111111111111';
const EXECUTION = '22222222-2222-4222-8222-222222222222';

const candidates: FeedbackCandidates = {
  page: '/projects/abc',
  projectId: PROJECT,
  executionId: EXECUTION,
  assistantTurns: [{ role: 'user', content: 'render it' }]
};

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

beforeEach(() => submitFeedback.mockReset().mockResolvedValue({ id: 'r1', createdAt: 'now' }));
afterEach(cleanup);

function renderDialog(overrides: Partial<FeedbackCandidates> = {}) {
  render(
    <MantineProvider>
      <FeedbackDialog candidates={{ ...candidates, ...overrides }} onClose={() => {}} />
    </MantineProvider>
  );
}

function previewed() {
  return JSON.parse(screen.getByTestId('feedback-payload').textContent ?? '{}');
}

function typeMessage(text: string) {
  fireEvent.change(screen.getByTestId('feedback-message'), { target: { value: text } });
}

describe('FeedbackDialog', () => {
  it('previews exactly the context that will be attached', () => {
    renderDialog();

    const payload = previewed();
    expect(payload.context.page).toBe('/projects/abc');
    expect(payload.context.projectId).toBe(PROJECT);
    expect(payload.context.executionId).toBe(EXECUTION);
    expect(payload.context.assistantTurns).toEqual([{ role: 'user', content: 'render it' }]);
  });

  it('lets the user remove a field, and the preview and the sent body both drop it', async () => {
    renderDialog();
    typeMessage('it broke');

    fireEvent.click(screen.getByRole('checkbox', { name: /project id/i }));
    expect(previewed().context).not.toHaveProperty('projectId');

    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(submitFeedback).toHaveBeenCalledTimes(1));
    const sent = submitFeedback.mock.calls[0][0];
    expect(sent).toEqual({
      kind: 'bug',
      message: 'it broke',
      context: {
        page: '/projects/abc',
        executionId: EXECUTION,
        assistantTurns: [{ role: 'user', content: 'render it' }]
      }
    });
    expect(sent.context).not.toHaveProperty('projectId');
  });

  it('sends nothing when the user removes every attachment', async () => {
    renderDialog();
    typeMessage('just words');

    for (const name of [/page/i, /project id/i, /execution/i, /assistant turns/i]) {
      fireEvent.click(screen.getByRole('checkbox', { name }));
    }
    expect(previewed().context).toEqual({});

    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(submitFeedback).toHaveBeenCalledTimes(1));
    expect(submitFeedback.mock.calls[0][0].context).toEqual({});
  });

  it('cannot send an empty message', () => {
    renderDialog();
    expect(screen.getByRole('button', { name: 'Send' })).toHaveProperty('disabled', true);
    typeMessage('   ');
    expect(screen.getByRole('button', { name: 'Send' })).toHaveProperty('disabled', true);
    typeMessage('ok');
    expect(screen.getByRole('button', { name: 'Send' })).toHaveProperty('disabled', false);
  });

  it('says so when there is nothing to attach', () => {
    renderDialog({ page: undefined, projectId: undefined, executionId: undefined, assistantTurns: undefined });
    expect(screen.getByTestId('feedback-no-context')).toBeTruthy();
  });

  it('still submits, without diagnostics, when the collector throws', async () => {
    collect.mockRejectedValue(new Error('boom'));
    renderDialog();
    typeMessage('it broke');
    fireEvent.click(screen.getByTestId('feedback-share-diagnostics'));
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(submitFeedback).toHaveBeenCalledTimes(1));
    expect(submitFeedback.mock.calls[0][0].context).not.toHaveProperty('diagnostics');
  });
});
