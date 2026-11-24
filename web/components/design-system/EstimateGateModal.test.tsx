// @vitest-environment jsdom
/**
 * The accept-the-estimate step on a generic run / retry. The response bodies are the real
 * ones the server sends (DesignBuildGateResult.ToBody); the real API client functions and the real
 * ApiError run, only `fetch` is stubbed, so what is asserted is the request that goes on the wire.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useState } from 'react';

vi.mock('next/link', () => ({
  default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a>,
}));

import { executeWorkflow } from '@/lib/api/workflows';
import { retryExecution } from '@/lib/api/executions';
import { useEstimateGate } from './EstimateGateModal';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const NOT_ACCEPTED = {
  code: 'ESTIMATE_NOT_ACCEPTED',
  message: 'Accept an estimate of at least 231 credits to start this build.',
  estimate: { low: 12, high: 231 },
  capCredits: 300,
};
const OVER_CAP = {
  code: 'ESTIMATE_OVER_CAP',
  message: 'The AI fallback is estimated at up to 420 credits, above the 300-credit cap.',
  estimate: { low: 40, high: 420 },
  capCredits: 300,
};
const UNAVAILABLE = {
  code: 'ESTIMATE_UNAVAILABLE',
  message: 'The cost estimate is unavailable right now, so the build cannot be priced. Try again shortly.',
};

function reply(status: number, body: unknown) {
  return { ok: status >= 200 && status < 300, status, statusText: 'x', json: () => Promise.resolve(body) };
}

function stubFetch(...replies: Array<ReturnType<typeof reply>>) {
  const fn = vi.fn();
  replies.forEach((r) => fn.mockResolvedValueOnce(r));
  vi.stubGlobal('fetch', fn);
  return fn;
}

function sentBody(fn: ReturnType<typeof vi.fn>, call: number): Record<string, unknown> {
  return JSON.parse((fn.mock.calls[call][1] as RequestInit).body as string) as Record<string, unknown>;
}

/** The wiring both pages use: a failed send is offered to the gate, anything else is the page's own. */
function RunHarness({ kind, onStarted }: { kind: 'run' | 'retry'; onStarted: (id: string) => void }) {
  const gate = useEstimateGate('p1');
  const [other, setOther] = useState<string | null>(null);
  const go = async (accept?: number): Promise<void> => {
    try {
      const exec =
        kind === 'run'
          ? await executeWorkflow('p1', 'w1', 'brief', false, {}, accept)
          : await retryExecution('p1', 'w1', 'e1', false, accept);
      onStarted(exec.id);
    } catch (err) {
      if (!gate.intercept(err, go)) setOther(err instanceof Error ? err.message : 'failed');
    }
  };
  return (
    <MantineProvider>
      <button onClick={() => void go()}>go</button>
      {other && <p data-testid="other">{other}</p>}
      {gate.modal}
    </MantineProvider>
  );
}

describe('request body', () => {
  it('a plain run and retry send exactly what they sent before (no acceptEstimateCredits key)', async () => {
    const fn = stubFetch(reply(200, { id: 'x1' }), reply(200, { id: 'x2' }));
    await executeWorkflow('p1', 'w1', 'brief');
    await retryExecution('p1', 'w1', 'e1');
    expect(sentBody(fn, 0)).toEqual({ userRequest: 'brief', regenerateVideoClips: false });
    expect(sentBody(fn, 1)).toEqual({ regenerateVideoClips: false });
  });

  it('carries the accepted figure when given', async () => {
    const fn = stubFetch(reply(200, { id: 'x1' }), reply(200, { id: 'x2' }));
    await executeWorkflow('p1', 'w1', 'brief', false, {}, 231);
    await retryExecution('p1', 'w1', 'e1', false, 231);
    expect(sentBody(fn, 0).acceptEstimateCredits).toBe(231);
    expect(sentBody(fn, 1).acceptEstimateCredits).toBe(231);
  });
});

describe.each(['run', 'retry'] as const)('%s with the accept-the-estimate step', (kind) => {
  it('409 NOT_ACCEPTED opens the modal; Confirm resends the same request with the high figure', async () => {
    const fn = stubFetch(reply(409, NOT_ACCEPTED), reply(200, { id: 'new1' }));
    const onStarted = vi.fn();
    render(<RunHarness kind={kind} onStarted={onStarted} />);
    fireEvent.click(screen.getByText('go'));

    expect(await screen.findByText('between 12 and 231 credits', { exact: false })).toBeTruthy();
    expect(screen.getByText(/The AI part of this run uses credits/)).toBeTruthy();
    expect(onStarted).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(onStarted).toHaveBeenCalledWith('new1'));

    expect(fn).toHaveBeenCalledTimes(2);
    const first = sentBody(fn, 0);
    const second = sentBody(fn, 1);
    expect(first.acceptEstimateCredits).toBeUndefined();
    expect(second.acceptEstimateCredits).toBe(231);
    expect({ ...second, acceptEstimateCredits: undefined }).toEqual({ ...first, acceptEstimateCredits: undefined });
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
  });

  it('Cancel sends nothing more', async () => {
    const fn = stubFetch(reply(409, NOT_ACCEPTED));
    const onStarted = vi.fn();
    render(<RunHarness kind={kind} onStarted={onStarted} />);
    fireEvent.click(screen.getByText('go'));
    await screen.findByRole('button', { name: 'Confirm' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull());
    expect(fn).toHaveBeenCalledTimes(1);
    expect(onStarted).not.toHaveBeenCalled();
  });

  it('409 OVER_CAP shows the estimate against the cap, no Confirm, and a link to the profile tab', async () => {
    stubFetch(reply(409, OVER_CAP));
    render(<RunHarness kind={kind} onStarted={vi.fn()} />);
    fireEvent.click(screen.getByText('go'));
    const text = await screen.findByTestId('estimate-gate-over-cap');
    expect(text.textContent).toContain('between 40 and 420 credits');
    expect(text.textContent).toContain('300');
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
    const link = screen.getByRole('link', { name: /Brand & product/ });
    expect(link.getAttribute('href')).toBe('/projects/p1?tab=profile');
    expect(screen.getByText(/DESIGN\.md/)).toBeTruthy();
  });

  it('503 UNAVAILABLE says to try again shortly, with no Confirm', async () => {
    stubFetch(reply(503, UNAVAILABLE));
    render(<RunHarness kind={kind} onStarted={vi.fn()} />);
    fireEvent.click(screen.getByText('go'));
    expect(await screen.findByText(/try again shortly/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
  });

  it('any other failure (and a 409 with another code) is left to the page, no modal', async () => {
    stubFetch(reply(409, { code: 'SOMETHING_ELSE', message: 'Nope' }));
    render(<RunHarness kind={kind} onStarted={vi.fn()} />);
    fireEvent.click(screen.getByText('go'));
    expect((await screen.findByTestId('other')).textContent).toBe('Nope');
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
  });

  it('an ordinary run that needs no acceptance never shows the modal', async () => {
    const fn = stubFetch(reply(200, { id: 'plain1' }));
    const onStarted = vi.fn();
    render(<RunHarness kind={kind} onStarted={onStarted} />);
    fireEvent.click(screen.getByText('go'));
    await waitFor(() => expect(onStarted).toHaveBeenCalledWith('plain1'));
    expect(fn).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull();
  });
});
