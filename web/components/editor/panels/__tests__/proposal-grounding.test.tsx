// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditProposalResponse, ProposalAvailability, ProposalGrounding } from '@/lib/editing/sync/api';
import { groundingLine, proposalErrorSentence, unavailableHelpers } from '@/lib/editing/proposals/assist-text';
import { analyseFirstPriceLine, mediaRefsInRange } from '../AnalyseFirst';
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
});
afterEach(cleanup);

const g = (level: ProposalGrounding['level'], clipsWithFacts: number, clipsTotal: number, note = 'server note'): ProposalGrounding => ({ level, clipsWithFacts, clipsTotal, note });

describe('R11.2 text helpers', () => {
  it('words the grounding line, and trusts the server for length only', () => {
    expect(groundingLine(g('transcriptAndShots', 5, 6))).toBe('Used the spoken words and shots of 5 of 6 clips');
    expect(groundingLine(g('transcriptOnly', 1, 1))).toBe('Used the spoken words of 1 of 1 clip');
    expect(groundingLine(g('shotsOnly', 2, 3))).toBe('Used the shots of 2 of 3 clips');
    expect(groundingLine(g('namesOnly', 0, 2, 'Cutting by length only: analyse this footage first for a smarter cut.'))).toBe('Cutting by length only: analyse this footage first for a smarter cut.');
    expect(groundingLine(null)).toBeNull();
  });
  it('turns a 409 PROPOSAL_NEEDS_ANALYSIS into the server sentence', () => {
    const e = new ApiError('x', 409, [], { code: 'PROPOSAL_NEEDS_ANALYSIS', message: 'Analyse this footage first. You were not charged.' });
    expect(proposalErrorSentence(e)).toBe('Analyse this footage first. You were not charged.');
  });
  it('lists unavailable helpers with their reason', () => {
    const a: ProposalAvailability = { analysisAvailable: true, agents: { recut: { available: true, grounding: null, reason: null }, grade: { available: false, grounding: null, reason: 'Analyse first.' } } };
    expect(unavailableHelpers(a)).toEqual([{ agent: 'grade', reason: 'Analyse first.' }]);
  });
  it('finds files in range and prices the analysis', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('a', 0, 30), media('b', 60, 90, 0, { assetRef: { kind: 'projectFile', projectFileId: '3f2b8c1e-0a41-4c55-9d4e-1b7f6a9c2d11' } })])]);
    expect(mediaRefsInRange(d, null)).toHaveLength(2);
    expect(mediaRefsInRange(d, { inFrame: 0, outFrame: 30 })).toHaveLength(1);
    const e = { billingEnabled: true, credits: 3, seconds: 10, balanceCredits: 50, affordable: true, cached: false, unavailableReason: null };
    expect(analyseFirstPriceLine([e, e])).toBe('Analysing 2 files costs about 6 credits.');
  });
});

const availability: ProposalAvailability = {
  analysisAvailable: true,
  agents: {
    recut: { available: true, grounding: g('namesOnly', 0, 1, 'Cutting by length only: analyse this footage first for a smarter cut.'), reason: null },
    grade: { available: false, grounding: null, reason: 'Analyse this footage first so the colourist can see its colours and light.' },
    review: { available: false, grounding: null, reason: 'Analyse this footage first so the reviewer has the words and shots to go on.' },
  },
};
const document = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 60)])]);
const common = { projectId: 'p', timelineId: 't', readOnly: false, inOut: { inFrame: null, outFrame: null }, onGhosts: () => {}, onApply: () => {}, estimate: async () => { throw new Error('no quote'); }, availability: async () => availability };

describe('R11.2 ProposalPanel availability and grounding', () => {
  it('shows why Grade and Review are off, offers Analyse first with a price, and the grounding line', async () => {
    const estimateAnalysis = vi.fn().mockResolvedValue({ billingEnabled: true, credits: 4, seconds: 20, balanceCredits: 99, affordable: true, cached: false, unavailableReason: null });
    render(
      <MantineProvider>
        <ProposalPanel {...common} doc={document()} onSeek={() => {}} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('proposal-unavailable-grade')).toBeTruthy());
    expect(screen.getByTestId('proposal-unavailable-grade').textContent).toContain('colourist');
    expect(screen.getByTestId('proposal-unavailable-review').textContent).toContain('reviewer');
    expect(screen.getByTestId('proposal-grounding').textContent).toContain('Cutting by length only');
    expect(screen.getByTestId('analyse-first')).toBeTruthy();
    void estimateAnalysis;
  });

  it('shows the grounding line from the response and the 409 message when the ask is refused', async () => {
    const response: EditProposalResponse = { proposalId: 'p1', agent: 'recut', summary: 'Tighten.', rejected: [], groups: [], grounding: g('transcriptAndShots', 5, 6), analysisAvailable: true };
    const view = render(
      <MantineProvider>
        <ProposalPanel {...common} doc={document()} onSeek={() => {}} request={async () => response} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('proposal-grounding')).toBeTruthy());
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-grounding').textContent).toBe('Used the spoken words and shots of 5 of 6 clips'));
    view.unmount();

    render(
      <MantineProvider>
        <ProposalPanel {...common} doc={document()} onSeek={() => {}} request={async () => { throw new ApiError('x', 409, [], { code: 'PROPOSAL_NEEDS_ANALYSIS', message: 'Analyse this footage first. You were not charged.' }); }} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-error').textContent).toBe('Analyse this footage first. You were not charged.'));
  });
});

describe('R11.2 phone sheet', () => {
  it('shows the same availability and grounding states and adds no Analyse first control', async () => {
    render(
      <MantineProvider>
        <ProposalSheet {...common} doc={document()} onSeek={() => {}} onUndo={() => {}} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('proposal-unavailable-grade')).toBeTruthy());
    expect(screen.getByTestId('proposal-grounding').textContent).toContain('Cutting by length only');
    expect(screen.queryByTestId('analyse-first')).toBeNull();
  });
});
