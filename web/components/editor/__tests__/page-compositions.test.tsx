// @vitest-environment jsdom
/**
 * T-003 page compositions, through the real EditorWorkspace and store: Media is a page in the
 * shared frame (bins | pool | viewer+metadata, timeline hidden until Show timeline) while the Edit
 * tab still renders the same pool in its narrow container; Cut's overview is a page band; Audio's
 * mixer stays left with buses and the voice-over recorder on the right; Deliver is a two-column
 * page with no centre and no timeline.
 *
 * The heavy leaves (the pool, the canvas, the viewer) are stubs; the frame, its zones and the
 * wiring from the page to them are the real thing.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { EditorWorkspace } from '../EditorWorkspace';
import { createEditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import type { SaveState, SyncClient } from '@/lib/editing/sync';

vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useDeviceClass: () => 'desktop',
  useRetireFullViewOnPhone: () => undefined,
}));
vi.mock('@/lib/hooks/use-media', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useMediaPool: () => ({ data: [] }),
}));
vi.mock('@/lib/hooks/use-media-derivatives', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useRequestTimelineDerivatives: () => undefined,
}));
vi.mock('../timeline/TimelineCanvas', () => ({ TimelineCanvas: () => null }));
vi.mock('../viewer', async (importOriginal) => ({ ...(await importOriginal<object>()), ProgramViewer: () => null }));
vi.mock('../hub/MediaView', () => ({
  MediaView: ({ zoneTargets }: { zoneTargets?: unknown }) => (
    <div data-testid="media-view" data-zone-targets={zoneTargets ? 'yes' : 'no'} />
  ),
}));
vi.mock('../cut/CutWorkspace', () => ({
  CutWorkspace: () => <div data-testid="cut-tools" />,
  CutOverview: () => <div data-testid="cut-overview-stub" />,
}));
vi.mock('../deliver/DeliverWorkspace', () => ({
  DeliverWorkspace: ({ zoneTargets }: { zoneTargets?: unknown }) => (
    <div data-testid="deliver-stub" data-zones={zoneTargets ? 'yes' : 'no'} />
  ),
}));
vi.mock('../audio/MixerPanel', () => ({ MixerPanel: () => <div data-testid="mixer-stub" /> }));
vi.mock('../audio/BusesPanel', () => ({ BusesPanel: () => <div data-testid="buses-stub" /> }));
vi.mock('../audio/VoRecorderPanel', () => ({ VoRecorderPanel: () => <div data-testid="vo-stub" /> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
globalThis.fetch = vi.fn(() => new Promise<Response>(() => {})) as unknown as typeof fetch;

const saved: SaveState = { status: 'saved', revision: 1, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };
const fakeSync = () =>
  ({ getState: () => saved, subscribe: () => () => {}, attachOps: vi.fn(), flush: vi.fn(), getQueuedEntry: () => null, discardQueue: vi.fn() }) as unknown as SyncClient;

beforeEach(() => {
  window.localStorage.clear();
  // These tests are about the full editor: say so, or a desktop with no stored choice waits for the saved preferences.
  window.localStorage.setItem('reelbolt:editor:shell', 'full');
});
afterEach(cleanup);

function mount(workspace: 'media' | 'cut' | 'edit' | 'audio' | 'deliver') {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
  store.setUi({ workspace });
  render(
    <MantineProvider>
      <EditorWorkspace store={store} sync={fakeSync()} onReload={() => {}} />
    </MantineProvider>,
  );
  return store;
}

describe('T-003 Media is a page in the frame', () => {
  it('renders bins, pool and viewer+metadata into the frame’s zones, with the timeline hidden until asked', async () => {
    mount('media');
    expect(screen.getByTestId('panel-layout').getAttribute('data-page')).toBe('media');
    expect(screen.getByTestId('media-bins-zone')).not.toBeNull();
    expect(screen.getByTestId('media-pool-zone')).not.toBeNull();
    expect(screen.getByTestId('media-detail-zone')).not.toBeNull();
    await waitFor(() => expect(screen.getByTestId('media-view').getAttribute('data-zone-targets')).toBe('yes'));

    expect(screen.queryByTestId('panel-timeline'), 'the timeline starts hidden').toBeNull();
    const toggle = screen.getByTestId('media-timeline-toggle');
    expect(toggle.textContent).toBe('Show timeline');
    fireEvent.click(toggle);
    expect(screen.getByTestId('panel-timeline')).not.toBeNull();
    await waitFor(() => expect(screen.getByTestId('media-timeline-toggle').textContent).toBe('Hide timeline'));
    fireEvent.click(screen.getByTestId('media-timeline-toggle'));
    expect(screen.queryByTestId('panel-timeline')).toBeNull();
  });

  it('keeps the Edit tab’s Media pool in its narrow container, without frame zones', async () => {
    mount('edit');
    expect(screen.getByTestId('panel-layout').getAttribute('data-page')).toBe('edit');
    const view = screen.getByTestId('media-view');
    expect(view.getAttribute('data-zone-targets')).toBe('no');
    // The Edit page owns panel tabs: the pool is a tab's content, not a frame zone.
    expect(screen.queryByTestId('media-bins-zone')).toBeNull();
    expect(screen.queryByTestId('media-pool-zone')).toBeNull();
  });
});

describe('T-003 Cut, Audio and Deliver pages', () => {
  it('puts the overview in its own band and keeps the tools on the left', () => {
    mount('cut');
    expect(screen.getByTestId('panel-layout').getAttribute('data-page')).toBe('cut');
    expect(screen.getByTestId('panel-overview').contains(screen.getByTestId('cut-overview-stub'))).toBe(true);
    expect(screen.getByTestId('panel-left').contains(screen.getByTestId('cut-tools'))).toBe(true);
    expect(screen.getByTestId('panel-timeline')).not.toBeNull();
  });

  it('splits Audio: the mixer on the left, the buses and voice-over on the right', () => {
    mount('audio');
    expect(screen.getByTestId('panel-layout').getAttribute('data-page')).toBe('audio');
    expect(screen.getByTestId('panel-left').contains(screen.getByTestId('mixer-stub'))).toBe(true);
    const right = screen.getByTestId('panel-right');
    expect(right.contains(screen.getByTestId('buses-stub'))).toBe(true);
    expect(right.contains(screen.getByTestId('vo-stub'))).toBe(true);
  });

  it('renders Deliver as two columns in the frame, with no centre and no timeline', async () => {
    mount('deliver');
    expect(screen.getByTestId('deliver-fullpane')).not.toBeNull();
    expect(screen.getByTestId('deliver-workspace')).not.toBeNull();
    expect(screen.getByTestId('panel-layout').getAttribute('data-page')).toBe('deliver');
    expect(screen.getByTestId('deliver-controls-zone')).not.toBeNull();
    expect(screen.getByTestId('deliver-result-zone')).not.toBeNull();
    await waitFor(() => expect(screen.getByTestId('deliver-stub').getAttribute('data-zones')).toBe('yes'));
    expect(screen.queryByTestId('panel-viewer'), 'no centre zone').toBeNull();
    expect(screen.queryByTestId('panel-timeline'), 'no timeline').toBeNull();
  });
});
