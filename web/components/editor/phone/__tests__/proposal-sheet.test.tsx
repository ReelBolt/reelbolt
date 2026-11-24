// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep } from '@/lib/editing/store';
import { doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import type { EditProposalResponse } from '@/lib/editing/sync/api';
import type { ProposalGhost } from '@/lib/editing/proposals/apply-proposal';
import { ProposalSheet } from '../ProposalSheet';
import { UndoSnackbar, resetUndoSnackbar } from '../UndoSnackbar';

vi.mock('@/lib/editing/engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('@/lib/editing/engine')>();
  return { ...engine, assertInvariants: (d: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(d, { ids: 'opaque' }) };
});
const versionCalls = vi.hoisted(() => vi.fn().mockResolvedValue({}));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  createEditTimelineVersion: (...args: unknown[]) => versionCalls(...args),
}));

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
});
afterEach(() => {
  cleanup();
  resetUndoSnackbar();
});

const generated = (id: string, recordIn: number, recordOut: number): MediaClip => ({ ...media(id, recordIn, recordOut, 0), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' } });
const document = (): EditTimeline => doc([track(TRACKS.v1, 'video', [generated('gen', 0, 60), media('mine', 60, 120, 0), title('t', 120, 150)])]);
const response: EditProposalResponse = {
  proposalId: 'pr1',
  agent: 'recut',
  summary: 'Tighten the opening.',
  rejected: [],
  groups: [
    { label: 'Trim end of gen', ops: [{ type: 'trim', itemId: 'gen', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['gen'], touchesUserOwned: false, reason: 'the pause drags' },
    { label: 'Remove mine', ops: [{ type: 'extract', itemId: 'mine' }], touchedItemIds: ['mine'], touchesUserOwned: true, reason: 'repeats the intro' },
  ],
};

function setup(extra: { request?: () => Promise<EditProposalResponse> } = {}) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document(), revision: 1 });
  const ghosts: Array<{ ghosts: ProposalGhost[] } | null> = [];
  const onSeek = vi.fn();
  const onApplySpy = vi.fn((ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label));
  const view = render(
    <MantineProvider>
      <UndoSnackbar />
      <ProposalSheet doc={store.getState().doc} projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: null, outFrame: null }} onGhosts={(g) => ghosts.push(g)} onApply={onApplySpy} onSeek={onSeek} onUndo={() => undoStep(store)} request={extra.request ?? (async () => response)} estimate={async () => { throw new Error('no quote'); }} />
    </MantineProvider>,
  );
  return { store, ghosts, onSeek, onApplySpy, view };
}

describe('RM.13 ProposalSheet', () => {
  it('lists groups, seeks and highlights only the tapped group ghost', async () => {
    const s = setup();
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-group-0')).toBeTruthy());
    const all = s.ghosts.filter(Boolean).at(-1)!;
    expect(new Set(all.ghosts.map((g) => g.groupIndex))).toEqual(new Set([0, 1]));
    fireEvent.click(screen.getByTestId('proposal-group-focus-1'));
    expect(s.onSeek).toHaveBeenCalledWith(60);
    await waitFor(() => expect(new Set(s.ghosts.filter(Boolean).at(-1)!.ghosts.map((g) => g.groupIndex))).toEqual(new Set([1])));
    expect(screen.getByTestId('proposal-group-1').getAttribute('data-focused')).toBe('true');
  });

  it('keeps Accept disabled until the user-owned tick inside the group is ticked, then applies as one undo step', async () => {
    const s = setup();
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-ack-mine')).toBeTruthy());
    const accept = screen.getByTestId('proposal-accept-selected') as HTMLButtonElement;
    expect(accept.disabled).toBe(true);
    expect(screen.getByTestId('proposal-group-1').contains(screen.getByTestId('proposal-ack-mine'))).toBe(true);
    fireEvent.click(screen.getByTestId('proposal-ack-mine'));
    expect(accept.disabled).toBe(false);
    fireEvent.click(accept);
    await waitFor(() => expect(s.store.getState().history).toHaveLength(1));
    expect(versionCalls).toHaveBeenCalledWith('p', 't', { cause: 'beforeProposal' });
    expect(s.store.getState().doc.tracks[0].items.map((i) => i.id)).toEqual(['gen', 't']);
    await waitFor(() => expect(screen.getByTestId('undo-snackbar').textContent).toMatch(/^2 changes applied/));
    fireEvent.click(screen.getByTestId('undo-snackbar-undo'));
    expect(s.store.getState().doc).toStrictEqual(document());
  });

  it('a double tap on Accept applies the groups once', async () => {
    let release: () => void = () => {};
    versionCalls.mockImplementationOnce(() => new Promise((resolve) => { release = () => resolve({}); }));
    const s = setup();
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-ack-mine')).toBeTruthy());
    fireEvent.click(screen.getByTestId('proposal-ack-mine'));
    const accept = screen.getByTestId('proposal-accept-selected') as HTMLButtonElement;
    fireEvent.click(accept);
    fireEvent.click(accept);
    await act(async () => { release(); });
    await waitFor(() => expect(s.store.getState().history).toHaveLength(1));
    expect(s.onApplySpy).toHaveBeenCalledTimes(1);
  });

  it('Reject leaves the document unchanged', async () => {
    const s = setup();
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-reject')).toBeTruthy());
    fireEvent.click(screen.getByTestId('proposal-reject'));
    expect(s.onApplySpy).not.toHaveBeenCalled();
    expect(s.store.getState().doc).toStrictEqual(document());
    expect(screen.queryByTestId('proposal-group-0')).toBeNull();
  });

  it('says plainly when the hourly limit is reached', async () => {
    const { ApiError } = await import('@/lib/api/client');
    setup({ request: async () => { throw new ApiError('limit', 429, [], { code: 'PROPOSAL_RATE_LIMITED', retryAfterSeconds: 600 }); } });
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-error').textContent).toMatch(/limit of suggestions.*10 minutes/));
  });
});
