// @vitest-environment jsdom
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { signInAs } from '@/lib/activity/__tests__/helpers';
import type { EditProposalResponse, ProposalAvailability } from '@/lib/editing/sync/api';
import { resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { useBackgroundActivityBridge } from '../../background/useBackgroundActivityBridge';
import { makeStore } from '../../background/__tests__/helpers';
import { ProposalPanel } from '../ProposalPanel';

vi.mock('next/navigation', () => ({ usePathname: () => '/editor/t', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

const editor = makeStore();
let ctx: ActivityContextValue;
function Probe() {
  useBackgroundActivityBridge({ store: editor, projectId: 'p', timelineId: 't' });
  const value = useActivity();
  useEffect(() => {
    ctx = value;
  });
  return null;
}
const availability: ProposalAvailability = { analysisAvailable: true, agents: { recut: { available: true, grounding: null, reason: null } } };

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [], serverTime: '', activeCount: 0 }), { status: 200 })));
  window.localStorage.clear();
  resetResultsStores();
  setActiveTimeline('t');
  signInAs();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('proposal request in the activity model', () => {
  it('reaches the tray through the bridge: running while pending (status in the panel), then needs your OK', async () => {
    let finish: (r: EditProposalResponse) => void = () => undefined;
    const request = () => new Promise<EditProposalResponse>((resolve) => (finish = resolve));
    render(
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <ActivityProvider>
          <Probe />
          <MantineProvider>
            <ProposalPanel projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: null, outFrame: null }} onGhosts={() => {}} onApply={() => {}} estimate={async () => { throw new Error('no quote'); }} availability={async () => availability} doc={doc([track(TRACKS.v1, 'video', [media('a', 0, 60)])])} onSeek={() => {}} request={request} />
          </MantineProvider>
        </ActivityProvider>
      </SWRConfig>,
    );
    await waitFor(() => expect(screen.getByTestId('proposal-ask')).toBeTruthy());
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(ctx.jobs.map((j) => [j.type, j.state])).toEqual([['proposal', 'running']]));
    expect(screen.getByTestId('inline-job-status')).toBeTruthy();
    await act(async () => finish({ proposalId: 'p1', agent: 'recut', summary: 's', rejected: [], groups: [], grounding: null, analysisAvailable: true }));
    await waitFor(() => expect(ctx.jobs.map((j) => [j.type, j.state])).toEqual([['proposal', 'needsReview']]));
  });
});
