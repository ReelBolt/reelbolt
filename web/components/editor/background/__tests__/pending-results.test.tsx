// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store';
import { PendingResults } from '../PendingResults';
import { clipOf, installDomStubs, launchOn, makeStore, STEP } from './helpers';

const server = vi.hoisted(() => ({ resolve: vi.fn() }));
vi.mock('@/lib/editing/background/object-effect', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/background/object-effect')>()), resolveObjectOnServer: server.resolve }));

const mount = (store: EditorStore) => render(<MantineProvider><PendingResults store={store} projectId="p" /></MantineProvider>);
const review = (applicable: string[], reasons: Array<'targetGone' | 'outsideCoverage' | 'assetChanged' | 'limitReached'>) => ({ applicable, reasons, lostCount: 1 });
const intent = { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0, analysisStepResultId: STEP, trackIds: ['g1'] };

beforeEach(() => {
  installDomStubs();
  window.localStorage.clear();
  resetResultsStores();
  setActiveTimeline('t');
  server.resolve.mockReset().mockResolvedValue(undefined);
});
afterEach(cleanup);

describe('PendingResults', () => {
  it('shows nothing when no result waits', () => {
    const store = makeStore();
    launchOn(store, { state: 'running' });
    mount(store);
    expect(screen.queryByTestId('pending-results-chip')).toBeNull();
  });
  it('the chip counts results and the list says what changed in plain words (read live from the document)', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review: review([], ['targetGone']) });
    act(() => store.dispatch({ type: 'lift', itemId: 'x' } as never));
    mount(store);
    expect(screen.getByTestId('pending-results-chip').textContent).toBe('1 result needs your OK');
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    expect((await screen.findByTestId('pending-reason-targetGone')).textContent).toBe('The clip it was made for was deleted.');
    expect(screen.getByTestId('pending-results-list').textContent).toContain('costs nothing');
  });
  it('"Apply to what is left" is disabled with a reason when nothing is applicable, and comes back when the delete is undone', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review: review([], ['targetGone']) });
    act(() => store.dispatch({ type: 'lift', itemId: 'x' } as never));
    mount(store);
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    expect((await screen.findByTestId('pending-apply-r1') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('pending-apply-reason').textContent).toContain('no clip left');
    act(() => {
      store.undo();
    });
    await waitFor(() => expect((screen.getByTestId('pending-apply-r1') as HTMLButtonElement).disabled).toBe(false));
  });
  it('"Apply to what is left" applies to the current clips as one undo step and tells the server', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review: review(['x'], ['outsideCoverage']) });
    mount(store);
    const before = store.getState().cursor;
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    fireEvent.click(await screen.findByTestId('pending-apply-r1'));
    expect(store.getState().cursor).toBe(before + 1);
    expect(clipOf(store, 'x').regionEffects).toHaveLength(1);
    expect(getResultsStore('t').getEntries()[0].state).toBe('applied');
    await waitFor(() => expect(server.resolve).toHaveBeenCalledWith('p', expect.stringContaining('file:'), 'r1', 'applied', 1));
    await waitFor(() => expect(screen.queryByTestId('pending-results-chip')).toBeNull());
  });
  it('"Discard" resolves it, writes nothing and tells the server', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review: review(['x'], ['outsideCoverage']) });
    mount(store);
    const before = store.getState().cursor;
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    fireEvent.click(await screen.findByTestId('pending-discard-r1'));
    expect(store.getState().cursor).toBe(before);
    expect(getResultsStore('t').getEntries()[0].state).toBe('discarded');
    expect(server.resolve).toHaveBeenCalledWith('p', expect.any(String), 'r1', 'discarded', 1);
    act(() => undefined);
  });
});
