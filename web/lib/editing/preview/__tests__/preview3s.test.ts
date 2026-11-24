import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { buttonLabel, offersPreview3s, pollDelayMs, previewReducer, priceFromEstimate, type PreviewState } from '../preview3s';
import { createPreview3sStore } from '../preview3s-store';
import type { Preview3sApi } from '../../api/preview3s';

const est = (o: Partial<Parameters<typeof priceFromEstimate>[0]>) => ({ billingEnabled: true, affordable: true, credits: 2, ...o });

describe('R10.4 price words', () => {
  it('says included when billing is off or the price is zero', () => {
    expect(priceFromEstimate(est({ billingEnabled: false }))).toEqual({ kind: 'ready', label: 'Included', affordable: true });
    expect(priceFromEstimate(est({ credits: 0 }))).toMatchObject({ label: 'Included' });
  });
  it('says about N credits and puts it in the button', () => {
    const p = priceFromEstimate(est({ credits: 2 }));
    expect(p).toMatchObject({ kind: 'ready', label: 'About 2 credits' });
    expect(buttonLabel(p)).toBe('Preview 3 s (about 2 credits)');
    expect(buttonLabel({ kind: 'unknown' })).toBe('Preview 3 s');
  });
  it('says what is missing when the balance is short', () => {
    expect(priceFromEstimate(est({ affordable: false, credits: 5, balanceCredits: 3 }))).toEqual({ kind: 'refused', reason: 'You need 2 more credits for this preview.' });
  });
  it('is offered only for render-only or approximate features', () => {
    expect(offersPreview3s('renderRequired')).toBe(true);
    expect(offersPreview3s('approximate')).toBe(true);
    expect(offersPreview3s('faithful')).toBe(false);
  });
  it('backs off to four seconds', () => {
    expect(pollDelayMs(0)).toBe(1500);
    expect(pollDelayMs(50)).toBe(4000);
  });
});

describe('R10.4 state machine', () => {
  const run = (events: Parameters<typeof previewReducer>[1][]): PreviewState => events.reduce(previewReducer, { phase: 'idle' } as PreviewState);
  it('goes idle, starting, rendering, ready, idle', () => {
    expect(run([{ type: 'start' }, { type: 'started', jobId: 'j' }, { type: 'status', status: 'Running', ready: false }])).toMatchObject({ phase: 'rendering', statusWord: 'Rendering your preview' });
    expect(run([{ type: 'start' }, { type: 'started', jobId: 'j' }, { type: 'status', status: 'Passed', ready: true }])).toEqual({ phase: 'ready', jobId: 'j' });
    expect(run([{ type: 'start' }, { type: 'close' }])).toEqual({ phase: 'idle' });
  });
  it('fails with the server sentence, and an expired file is a failure', () => {
    expect(run([{ type: 'start' }, { type: 'started', jobId: 'j' }, { type: 'status', status: 'Failed', ready: false, errorMessage: 'Nope.' }])).toEqual({ phase: 'failed', message: 'Nope.' });
    expect(run([{ type: 'start' }, { type: 'started', jobId: 'j' }, { type: 'status', status: 'Passed', ready: false, errorMessage: 'This preview has expired. Make a new one.' }])).toMatchObject({ phase: 'failed' });
  });
  it('maps 402 and 429', () => {
    expect(run([{ type: 'start' }, { type: 'error', status: 402, code: 'INSUFFICIENT_CREDITS', message: 'm' }])).toEqual({ phase: 'refused', message: 'm' });
    expect(run([{ type: 'start' }, { type: 'error', status: 429, code: 'PREVIEW_RATE_LIMITED', message: 'm' }])).toEqual({ phase: 'limited', message: 'm' });
  });
});

describe('R10.4 store', () => {
  const sleep = () => Promise.resolve();
  const okEstimate = { billingEnabled: false, affordable: true };
  it('polls until ready and stops after close', async () => {
    let calls = 0;
    const api: Preview3sApi = {
      estimate: async () => okEstimate,
      start: async () => ({ executionId: 'job1' }),
      status: async () => ({ status: ++calls < 2 ? 'Running' : 'Passed', ready: calls >= 2 }),
    };
    const store = createPreview3sStore(api, sleep);
    await store.start('p', 't', 10);
    expect(store.getState()).toEqual({ phase: 'ready', jobId: 'job1' });
    expect(calls).toBe(2);
    store.close();
    expect(store.getState()).toEqual({ phase: 'idle' });
  });
  it('a 402 on start is refused and polls nothing', async () => {
    let polled = false;
    const api: Preview3sApi = {
      estimate: async () => okEstimate,
      start: async () => {
        throw new ApiError('You need 2 more credits.', 402, [], { code: 'INSUFFICIENT_CREDITS' });
      },
      status: async () => {
        polled = true;
        return { status: 'Running', ready: false };
      },
    };
    const store = createPreview3sStore(api, sleep);
    await store.start('p', 't', 0);
    expect(store.getState().phase).toBe('refused');
    expect(polled).toBe(false);
  });
});
