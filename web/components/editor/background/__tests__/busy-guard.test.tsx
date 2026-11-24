// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { resetResultsStores } from '@/lib/editing/background/results-store';
import { getResultsStore } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store';
import { useBusyEditGuard } from '../useBusyEditGuard';
import { clipOf, installDomStubs, launchOn, makeStore } from './helpers';

const stop = vi.hoisted(() => ({ fn: vi.fn() }));
vi.mock('@/lib/editing/background/object-effect', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/background/object-effect')>()), stopObjectJob: stop.fn }));

function Harness({ store, onRefusal }: { store: EditorStore; onRefusal: (e: unknown) => void }) {
  return <>{useBusyEditGuard({ store, projectId: 'p', timelineId: 't', onRefusal })}</>;
}
const mount = (store: EditorStore, onRefusal = vi.fn()) => render(<MantineProvider><Harness store={store} onRefusal={onRefusal} /></MantineProvider>);
const trimX = () => ({ type: 'trim', itemId: 'x', edge: 'out', newFrame: 30, mode: 'ripple' }) as never;

beforeEach(() => {
  installDomStubs();
  window.localStorage.clear();
  resetResultsStores();
  stop.fn.mockReset().mockResolvedValue({});
});
afterEach(cleanup);

describe('edit guard', () => {
  it('opens on a split of a busy clip, holds the edit, and "Go on" re-dispatches the same ops', async () => {
    const store = makeStore();
    launchOn(store);
    mount(store);
    const before = store.getState().cursor;
    act(() => store.dispatchBatch([{ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'y' }], 'Split'));
    expect(await screen.findByTestId('busy-edit-modal')).toBeTruthy();
    expect(screen.getByText('Magic blur is still working on this clip')).toBeTruthy();
    expect(screen.getByText(/the result will follow your edit/)).toBeTruthy();
    expect(store.getState().cursor).toBe(before);
    fireEvent.click(screen.getByTestId('busy-go-on'));
    expect(store.getState().cursor).toBe(before + 1);
    expect(clipOf(store, 'y')).toBeTruthy();
    // remembered for this session: the next edit does not ask again
    act(() => store.dispatchBatch([{ type: 'blade', itemId: 'y', atFrame: 45, newItemId: 'z' }], 'Split'));
    expect(store.getState().cursor).toBe(before + 2);
  });
  it('opens on trim and on delete (lift) of a busy clip', async () => {
    const store = makeStore();
    launchOn(store);
    mount(store);
    act(() => store.dispatch(trimX()));
    expect(await screen.findByTestId('busy-edit-modal')).toBeTruthy();
    fireEvent.click(screen.getByTestId('busy-back'));
    await waitFor(() => expect(screen.queryByTestId('busy-edit-modal')).toBeNull());
    act(() => store.dispatch({ type: 'lift', itemId: 'x' } as never));
    expect(await screen.findByTestId('busy-edit-modal')).toBeTruthy();
    expect(clipOf(store, 'x')).toBeTruthy();
  });
  it('does not open for another clip, for the job\'s own write, or for undo', () => {
    const store = makeStore();
    launchOn(store);
    mount(store);
    const before = store.getState().cursor;
    act(() => store.dispatch({ type: 'lift', itemId: 'o' } as never));
    act(() => store.dispatch({ type: 'setProperty', itemId: 'x', key: 'regionEffects', value: null }));
    expect(store.getState().cursor).toBe(before + 2);
    expect(screen.queryByTestId('busy-edit-modal')).toBeNull();
    act(() => void store.undo());
    expect(store.getState().cursor).toBe(before + 1);
    expect(screen.queryByTestId('busy-edit-modal')).toBeNull();
  });
  it('a finished job no longer asks', () => {
    const store = makeStore();
    launchOn(store, { state: 'applied' });
    mount(store);
    const before = store.getState().cursor;
    act(() => store.dispatch({ type: 'lift', itemId: 'x' } as never));
    expect(store.getState().cursor).toBe(before + 1);
  });
  it('offers "Stop the job" only when the workflow is known, then stops it and goes on', async () => {
    const store = makeStore();
    launchOn(store, { intent: { prompt: 'face', workflowId: 'w1' } });
    mount(store);
    act(() => store.dispatch({ type: 'lift', itemId: 'x' } as never));
    fireEvent.click(await screen.findByTestId('busy-stop'));
    await waitFor(() => expect(stop.fn).toHaveBeenCalledWith('p', 'w1', 'e1'));
    await waitFor(() => expect(getResultsStore('t').getEntries()[0].state).toBe('discarded'));
  });
  it('says the job cannot be stopped from here when the workflow is unknown', async () => {
    const store = makeStore();
    launchOn(store);
    mount(store);
    act(() => store.dispatch({ type: 'lift', itemId: 'x' } as never));
    expect(await screen.findByTestId('busy-no-stop')).toBeTruthy();
    expect(screen.queryByTestId('busy-stop')).toBeNull();
  });
});
