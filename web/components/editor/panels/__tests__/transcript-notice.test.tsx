// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditProposalResponse } from '@/lib/editing/sync/api';
import { NO_KIND, type KindStatus, type MediaAnalysisStatus } from '@/lib/editing/api/media-analysis';

const G = '00000000-0000-4000-8000-0000000000aa';
const REF = `step_result:${G}`;
const mocks = vi.hoisted(() => ({ estimate: vi.fn(), start: vi.fn(), statuses: { data: undefined as unknown, mutate: vi.fn() } }));
vi.mock('@/lib/editing/api/media-analysis', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/media-analysis')>()),
  estimateMediaAnalysis: mocks.estimate,
  startMediaAnalysis: mocks.start,
  useDocumentSourceStatuses: () => mocks.statuses,
}));

import { TranscriptStart } from '../TranscriptStart';
import { ProposalPanel } from '../ProposalPanel';
import { ProposalSheet } from '../../phone/ProposalSheet';

const status = (transcript: Partial<KindStatus>): MediaAnalysisStatus => ({
  mediaRef: REF,
  kinds: { silence: NO_KIND, shots: NO_KIND, motion: NO_KIND, transcript: { ...NO_KIND, ...transcript }, beats: NO_KIND, events: NO_KIND },
});
const stepDoc = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 60, 0, { assetRef: { kind: 'stepResult', stepResultId: G } })])]);
const ui = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  mocks.estimate.mockReset();
  mocks.start.mockReset();
  mocks.statuses.data = undefined;
});
afterEach(cleanup);

describe('TranscriptStart', () => {
  const props = { doc: stepDoc(), projectId: 'p', readOnly: false, emptyText: 'Nothing here' };

  it('not analysed: offers the words with the price, then starts only the transcript', async () => {
    mocks.statuses.data = [status({})];
    mocks.estimate.mockResolvedValue({ billingEnabled: true, credits: 4, seconds: 20, balanceCredits: 50, affordable: true, cached: false, unavailableReason: null });
    ui(<TranscriptStart {...props} />);
    fireEvent.click(screen.getByText('Find the words in this video'));
    await waitFor(() => expect(screen.getByTestId('analyse-first-price').textContent).toContain('4 credits'));
    expect(mocks.estimate).toHaveBeenCalledWith('p', REF, ['transcript']);
    fireEvent.click(screen.getByTestId('analyse-first-confirm'));
    await waitFor(() => expect(mocks.start).toHaveBeenCalledWith('p', REF, ['transcript']));
  });

  it('running: shows progress and no button', () => {
    mocks.statuses.data = [status({ state: 'running' })];
    ui(<TranscriptStart {...props} />);
    expect(screen.getByTestId('transcript-running')).toBeTruthy();
    expect(screen.queryByTestId('analyse-first')).toBeNull();
  });

  it('failed: plain reason and a retry', () => {
    mocks.statuses.data = [status({ state: 'failed', reason: 'No speech found.' })];
    ui(<TranscriptStart {...props} />);
    expect(screen.getByRole('alert').textContent).toBe('No speech found.');
    expect(screen.getByText('Try again')).toBeTruthy();
  });

  it('unreadable status: says so and offers a retry, not "find the words"', () => {
    mocks.statuses.data = [null];
    ui(<TranscriptStart {...props} />);
    expect(screen.getByTestId('transcript-unreadable')).toBeTruthy();
    fireEvent.click(screen.getByText('Try again'));
    expect(mocks.statuses.mutate).toHaveBeenCalled();
  });
});

describe('recut notice and automatic re-ask', () => {
  const answered: EditProposalResponse = { proposalId: 'p1', agent: 'recut', summary: 'Cut the talking.', groups: [], rejected: [] };
  const hint = { kinds: ['transcript'], missingRefs: [REF] };
  const common = { projectId: 'p', timelineId: 't', readOnly: false, inOut: { inFrame: null, outFrame: null }, onGhosts: () => {}, onApply: () => {}, estimate: async () => { throw new Error('no quote'); }, availability: async () => ({ analysisAvailable: true, agents: {}, analysisHint: hint }) };

  it.each([
    ['panel', (req: () => Promise<EditProposalResponse>) => <ProposalPanel {...common} doc={stepDoc()} onSeek={() => {}} request={req} />],
    ['phone sheet', (req: () => Promise<EditProposalResponse>) => <ProposalSheet {...common} doc={stepDoc()} onSeek={() => {}} onUndo={() => {}} request={req} />],
  ])('shows the notice from the availability hint in the %s', async (_n, make) => {
    ui(make(async () => answered));
    const notice = await screen.findByTestId('proposal-needs-analysis');
    expect(notice.textContent).toContain("I can't see what is said in this video yet");
    expect(screen.getByTestId('analyse-first')).toBeTruthy();
  });

  it('asks again by itself once the analysis is ready', async () => {
    mocks.estimate.mockResolvedValue({ billingEnabled: false, credits: null, seconds: null, balanceCredits: null, affordable: true, cached: false, unavailableReason: null });
    mocks.start.mockResolvedValue({ requestId: 'r', mediaRef: REF, states: {} });
    const request = vi.fn(async () => answered);
    const { rerender } = ui(<ProposalPanel {...common} doc={stepDoc()} onSeek={() => {}} request={request} />);
    fireEvent.click(await screen.findByTestId('analyse-first'));
    fireEvent.click(await screen.findByTestId('analyse-first-confirm'));
    await waitFor(() => expect(mocks.start).toHaveBeenCalled());
    expect(request).not.toHaveBeenCalled();
    mocks.statuses.data = [status({ state: 'ready' })];
    rerender(<MantineProvider><ProposalPanel {...common} doc={stepDoc()} onSeek={() => {}} request={request} /></MantineProvider>);
    await waitFor(() => expect(request).toHaveBeenCalledTimes(1));
  });

  it('an unreadable status says it could not check (not "not found") and offers a retry', async () => {
    mocks.estimate.mockResolvedValue({ billingEnabled: false, credits: null, seconds: null, balanceCredits: null, affordable: true, cached: false, unavailableReason: null });
    mocks.start.mockResolvedValue({ requestId: 'r', mediaRef: REF, states: {} });
    const request = vi.fn(async () => answered);
    const { rerender } = ui(<ProposalPanel {...common} doc={stepDoc()} onSeek={() => {}} request={request} />);
    fireEvent.click(await screen.findByTestId('analyse-first'));
    fireEvent.click(await screen.findByTestId('analyse-first-confirm'));
    await waitFor(() => expect(mocks.start).toHaveBeenCalled());
    mocks.statuses.data = [null];
    rerender(<MantineProvider><ProposalPanel {...common} doc={stepDoc()} onSeek={() => {}} request={request} /></MantineProvider>);
    await waitFor(() => expect(screen.getByText('I could not check whether the words were found. Try again.')).toBeTruthy());
    expect(screen.queryByText('The words could not be found. Try again.')).toBeNull();
    fireEvent.click(screen.getByText('Try again'));
    expect(mocks.statuses.mutate).toHaveBeenCalled();
    expect(request).not.toHaveBeenCalled();
  });
});
