// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditProposalResponse } from '@/lib/editing/sync/api';

const mocks = vi.hoisted(() => ({ estimate: vi.fn(), start: vi.fn() }));
vi.mock('@/lib/editing/api/media-analysis', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/media-analysis')>()),
  estimateMediaAnalysis: mocks.estimate,
  startMediaAnalysis: mocks.start,
}));

import { ProposalPanel } from '../ProposalPanel';
import { ProposalSheet } from '../../phone/ProposalSheet';

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
});
afterEach(cleanup);

const document = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 60)])]);
const common = { projectId: 'p', timelineId: 't', readOnly: false, inOut: { inFrame: null, outFrame: null }, onGhosts: () => {}, onApply: () => {}, estimate: async () => { throw new Error('no quote'); }, availability: async () => ({ analysisAvailable: false, agents: {} }) };
const REF = 'file:00000000-0000-4000-8000-000000000002';

const answered: EditProposalResponse = {
  proposalId: 'p1',
  agent: 'recut',
  summary: 'Cut the talking.',
  groups: [],
  rejected: [],
  did: { cut: 7, cutSeconds: 12.4, remove: 0, trim: 0, mute: 0, other: 0 },
  swapsRefused: 1,
};

describe('recut: what it did and the analysis offer', () => {
  it.each([
    ['panel', (req: () => Promise<EditProposalResponse>) => <ProposalPanel {...common} doc={document()} onSeek={() => {}} request={req} />],
    ['phone sheet', (req: () => Promise<EditProposalResponse>) => <ProposalSheet {...common} doc={document()} onSeek={() => {}} onUndo={() => {}} request={req} />],
  ])('shows the did line and the nothing-muted line in the %s', async (_name, ui) => {
    render(<MantineProvider>{ui(async () => answered)}</MantineProvider>);
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-did').textContent).toBe('What it did: cut 7 spoken passages (12.4 s).'));
    expect(screen.getByTestId('proposal-swaps-refused').textContent).toBe('Skipped changes that did not match your request.');
  });

  it('shows no did or swaps line when the server sent none', async () => {
    render(
      <MantineProvider>
        <ProposalPanel {...common} doc={document()} onSeek={() => {}} request={async () => ({ ...answered, did: undefined, swapsRefused: 0 })} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByText('Cut the talking.')).toBeTruthy());
    expect(screen.queryByTestId('proposal-did')).toBeNull();
    expect(screen.queryByTestId('proposal-swaps-refused')).toBeNull();
  });

  it('keeps the 409 message and offers only the analysis it names, with the price before anything starts', async () => {
    mocks.estimate.mockResolvedValue({ billingEnabled: true, credits: 5, seconds: 30, balanceCredits: 99, affordable: true, cached: false, unavailableReason: null });
    const refused = async (): Promise<EditProposalResponse> => {
      throw new ApiError('x', 409, [], { code: 'PROPOSAL_NEEDS_ANALYSIS', message: 'Analyse this footage first. You were not charged.', analysis: { kinds: ['transcript'], mediaRefs: [REF] } });
    };
    render(
      <MantineProvider>
        <ProposalPanel {...common} doc={document()} onSeek={() => {}} request={refused} />
      </MantineProvider>,
    );
    expect(screen.queryByTestId('analyse-first')).toBeNull();
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-error').textContent).toBe('Analyse this footage first. You were not charged.'));
    fireEvent.click(await screen.findByTestId('analyse-first'));
    await waitFor(() => expect(screen.getByTestId('analyse-first-price').textContent).toContain('5 credits'));
    expect(mocks.estimate).toHaveBeenCalledWith('p', REF, ['transcript']);
    expect(mocks.start).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('analyse-first-confirm'));
    await waitFor(() => expect(mocks.start).toHaveBeenCalledWith('p', REF, ['transcript']));
  });

  it('offers the analysis from a 200 needsAnalysis in the phone sheet too', async () => {
    const partial: EditProposalResponse = { ...answered, needsAnalysis: { kinds: ['transcript'], mediaRefs: [REF] } };
    render(
      <MantineProvider>
        <ProposalSheet {...common} doc={document()} onSeek={() => {}} onUndo={() => {}} request={async () => partial} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('analyse-first')).toBeTruthy());
  });
});
