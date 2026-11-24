// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { runImageOperation, segmentSubjects } from '@/lib/api/image-documents';
import { requestEditProposal } from '@/lib/editing/sync/api';
import { insufficientCreditsSentence, notifyInsufficientCredits } from './insufficientCreditsNotification';

/**
 * A picture editor AI tool or a timeline AI proposal refused with 402 `INSUFFICIENT_CREDITS` gets a
 * plain notification with the server's figures and a link to add credits, instead of a generic error.
 * The refusal is produced by the real API functions against a mocked fetch.
 */

const PROJECT = '12345678-1234-1234-1234-123456789012';
const DOC = '04d17aa1-84fe-4694-9b86-db1bf2d3997d';

function respond(status: number, body: unknown) {
  global.fetch = vi.fn<typeof fetch>(() =>
    Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })),
  ) as unknown as typeof fetch;
}

const REFUSAL = { code: 'INSUFFICIENT_CREDITS', message: 'Not enough credits.', required: 12, balance: 3 };

async function refusalOf(call: () => Promise<unknown>): Promise<unknown> {
  try {
    await call();
  } catch (error) {
    return error;
  }
  throw new Error('expected the call to be refused');
}

function renderNotifications() {
  render(
    <MantineProvider>
      <Notifications />
    </MantineProvider>,
  );
}

describe('notifyInsufficientCredits', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
    renderNotifications();
  });

  afterEach(() => {
    act(() => notifications.clean());
    cleanup();
    vi.restoreAllMocks();
  });

  it('turns an editor operation 402 into the credits notification with a billing link', async () => {
    respond(402, REFUSAL);
    const error = await refusalOf(() => runImageOperation(PROJECT, DOC, { operation: 'upscale', layerId: 'l-1' } as never));

    let handled = false;
    act(() => { handled = notifyInsufficientCredits(error); });

    expect(handled).toBe(true);
    expect(await screen.findByText('Not enough credits for this. It needs about 12 credits and you have 3.')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Add credits' }).getAttribute('href')).toBe('/settings/billing');
  });

  it('handles a subject cut-out 402 the same way', async () => {
    respond(402, REFUSAL);
    const error = await refusalOf(() => segmentSubjects(PROJECT, DOC, { prompt: 'dog' } as never));

    let handled = false;
    act(() => { handled = notifyInsufficientCredits(error); });

    expect(handled).toBe(true);
    expect(await screen.findByText(/It needs about 12 credits and you have 3\./)).toBeTruthy();
  });

  it('turns a timeline proposal 402 into the credits notification', async () => {
    respond(402, { ...REFUSAL, required: 5, balance: 0.5 });
    const error = await refusalOf(() => requestEditProposal(PROJECT, 'tl-1', { agent: 'recut' }));

    let handled = false;
    act(() => { handled = notifyInsufficientCredits(error); });

    expect(handled).toBe(true);
    expect(await screen.findByText('Not enough credits for this. It needs about 5 credits and you have 0.5.')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Add credits' }).getAttribute('href')).toBe('/settings/billing');
  });

  it('leaves every other failure to the caller and shows nothing', async () => {
    respond(402, { code: 'PLAN_LIMIT', message: 'Bring your own provider.' });
    const planLimit = await refusalOf(() => requestEditProposal(PROJECT, 'tl-1', { agent: 'recut' }));
    respond(503, { code: 'no_chat_provider' });
    const noProvider = await refusalOf(() => requestEditProposal(PROJECT, 'tl-1', { agent: 'recut' }));

    let results: boolean[] = [];
    act(() => { results = [notifyInsufficientCredits(planLimit), notifyInsufficientCredits(noProvider), notifyInsufficientCredits(new Error('x'))]; });

    expect(results).toEqual([false, false, false]);
    expect(screen.queryByText(/Not enough credits/)).toBeNull();
  });

  it('words the sentence with the figures it is given', () => {
    expect(insufficientCreditsSentence({ required: 1200, balance: 0 })).toBe(
      'Not enough credits for this. It needs about 1,200 credits and you have 0.',
    );
  });
});
