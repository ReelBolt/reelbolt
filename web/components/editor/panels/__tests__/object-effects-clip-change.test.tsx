// @vitest-environment jsdom
/**
 * The section is not keyed by clip in InspectorPanel, and unmounts when the clip is deselected. A run started on clip
 * c1 belongs to the editor, not to the panel: selecting another clip never shows c1's progress on c2, never applies
 * it to c2, and never drops it.
 */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MediaClip } from '@/lib/editing/types';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { ObjectEffectsSection } from '../ObjectEffectsSection';

const api = vi.hoisted(() => ({ estimateObject: vi.fn(), startObject: vi.fn() }));
vi.mock('@/lib/editing/api/objects', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/objects')>()),
  estimateObject: api.estimateObject,
  startObject: api.startObject,
  useObjectList: () => ({ data: undefined }),
  useObjectConsent: () => ({ data: undefined }),
}));
global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };

const clip = (id: string, fileId = 'f1'): MediaClip =>
  ({ id, type: 'mediaClip', recordIn: 0, recordOut: 100, sourceIn: 0, assetRef: { kind: 'projectFile', projectFileId: fileId } }) as unknown as MediaClip;

beforeEach(() => {
  api.estimateObject.mockReset().mockResolvedValue({ billingEnabled: true, credits: 1, frames: 1, seconds: 1, balanceCredits: 9, affordable: true, cached: false, consented: true, unavailableReason: null });
  api.startObject.mockReset().mockResolvedValue({ requestId: 'r1', executionId: 'e1', status: 'queued' });
  window.localStorage.clear();
  resetResultsStores();
  setActiveTimeline('t1');
});
afterEach(cleanup);

async function startOn(c: MediaClip) {
  const onSetProperty = vi.fn();
  const view = (x: MediaClip) => (
    <MantineProvider>
      <ObjectEffectsSection clip={x} projectId="p1" readOnly={false} onSetProperty={onSetProperty} />
    </MantineProvider>
  );
  const utils = render(view(c));
  fireEvent.change(screen.getByTestId('object-prompt'), { target: { value: 'face' } });
  await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByTestId('object-run'));
  await waitFor(() => expect(api.startObject).toHaveBeenCalled());
  await screen.findByTestId('object-progress');
  return { onSetProperty, rerender: (x: MediaClip) => utils.rerender(view(x)), unmount: utils.unmount };
}

describe('clip change while a search runs', () => {
  it('does not show the run on another clip (another file), and keeps it in the editor', async () => {
    const { rerender } = await startOn(clip('c1', 'f1'));
    rerender(clip('c2', 'f2'));
    expect(screen.queryByTestId('object-progress')).toBeNull();
    expect(getResultsStore('t1').getEntries()[0]).toMatchObject({ state: 'running', anchor: { clipIds: ['c1'] } });
  });
  it('does not show c1\'s run on c2 (same source file) and never writes to c2', async () => {
    const { onSetProperty, rerender } = await startOn(clip('c1', 'f1'));
    rerender(clip('c2', 'f1'));
    expect(screen.queryByTestId('object-progress')).toBeNull();
    expect(onSetProperty.mock.calls.filter((call) => call[0] === 'c2')).toHaveLength(0);
  });
  it('an unmounted section (clip deselected) drops nothing: the entry is still running and shows again on reselect', async () => {
    const first = await startOn(clip('c1', 'f1'));
    first.unmount();
    expect(getResultsStore('t1').getEntries()[0].state).toBe('running');
    render(
      <MantineProvider>
        <ObjectEffectsSection clip={clip('c1', 'f1')} projectId="p1" readOnly={false} onSetProperty={vi.fn()} />
      </MantineProvider>,
    );
    expect(await screen.findByTestId('object-progress')).toBeTruthy();
  });
});
