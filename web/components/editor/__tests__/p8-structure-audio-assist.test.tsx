// @vitest-environment jsdom
/**
 * P8 UI against a real store: the Assist panel asks for a proposal (injected transport), paints
 * its ghost diff (the ghosts the canvas receives), refuses to change a user-owned clip until it is
 * acknowledged, and accepts PARTIALLY as one undo step; the Structure panel makes a compound and a
 * multicam and cuts an angle; the Buses panel adds a bus and routes a track; the VO recorder says why
 * it is unavailable and, with an injected recorder, places a take at the playhead.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import { assertInvariants, type EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip, MulticamClip } from '@/lib/editing/types';
import type { EditProposalResponse } from '@/lib/editing/sync/api';
import type { ProposalGhost } from '@/lib/editing/proposals/apply-proposal';
import { doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { ProposalPanel } from '../panels/ProposalPanel';
import { StructurePanel } from '../panels/StructurePanel';
import { BusesPanel } from '../audio/BusesPanel';
import { VoRecorderPanel } from '../audio/VoRecorderPanel';

// These fixtures name things `c1`/`timeline-1`, and the production code under test checks documents
// with the strict engine invariants. Only the Guid id shape is relaxed here (it is pinned in
// `engine/__tests__/apply.contract.test.ts`); every structural invariant still runs.
vi.mock('@/lib/editing/engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('@/lib/editing/engine')>();
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(doc, { ids: 'opaque' }) };
});

// R02.4: accepting a proposal first stores a "before an AI suggestion" version; the API is stubbed here.
const versionCalls = vi.hoisted(() => vi.fn().mockResolvedValue({}));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  createEditTimelineVersion: (...args: unknown[]) => versionCalls(...args),
}));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
beforeEach(() => window.localStorage.setItem('rb.editor.advancedAudio', '1'));
afterEach(cleanup);

const generated = (id: string, recordIn: number, recordOut: number): MediaClip => ({ ...media(id, recordIn, recordOut, 0), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' } });

function storeWith(document: EditTimeline) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  let n = 0;
  return {
    store,
    onOp: (op: EditOp, label?: string) => store.dispatch(op, label),
    onApply: (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label),
    newId: () => `new-${++n}`,
  };
}

describe('ProposalPanel — ghost diff and partial accept', () => {
  const document = () => doc([track(TRACKS.v1, 'video', [generated('gen', 0, 60), media('mine', 60, 120, 0), title('t', 120, 150)])]);
  const response: EditProposalResponse = {
    proposalId: 'pr1',
    agent: 'recut',
    summary: 'Tighten the opening.',
    rejected: ["action 3: 'i9' is not an offered item"],
    groups: [
      { label: 'Trim end of gen by 0.5 s', ops: [{ type: 'trim', itemId: 'gen', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['gen'], touchesUserOwned: false, reason: 'the pause drags' },
      { label: 'Remove mine', ops: [{ type: 'extract', itemId: 'mine' }], touchedItemIds: ['mine'], touchesUserOwned: true, reason: 'repeats the intro' },
    ],
  };

  it('shows the ghost diff, refuses an unacknowledged user clip, then accepts one group as one undo step', async () => {
    const s = storeWith(document());
    const ghosts: Array<{ ghosts: ProposalGhost[] } | null> = [];
    const request = vi.fn(async () => response);
    const props = () => ({ doc: s.store.getState().doc, projectId: 'p', timelineId: 't', readOnly: false, inOut: { inFrame: null, outFrame: null }, onGhosts: (g: { ghosts: ProposalGhost[] } | null) => ghosts.push(g), onApply: s.onApply, onSeek: () => {}, request });
    const view = wrap(<ProposalPanel {...props()} />);
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-group-0')).toBeTruthy());
    expect(request).toHaveBeenCalledWith('p', 't', { agent: 'recut', range: null, brief: null });
    expect(screen.getByText(/could not be used/)).toBeTruthy();

    // The ghost diff the timeline paints: the trim as a change, the removal hatched and flagged as yours.
    const last = ghosts.filter(Boolean).at(-1)!;
    expect(last.ghosts.find((g) => g.itemId === 'gen')).toMatchObject({ kind: 'change', recordOut: 45 });
    expect(last.ghosts.find((g) => g.itemId === 'mine' && g.groupIndex === 1)).toMatchObject({ kind: 'remove', userOwned: true });
    expect(screen.getByTestId('proposal-user-owned-1')).toBeTruthy();

    // Accepting both without acknowledging the user's clip is refused, and nothing changes.
    fireEvent.click(screen.getByTestId('proposal-accept-selected'));
    expect(screen.getByTestId('proposal-error').textContent).toMatch(/you made yourself/);
    expect(s.store.getState().history).toHaveLength(0);

    // Partial accept: untick the removal, accept the trim only.
    fireEvent.click(screen.getByTestId('proposal-group-check-1'));
    fireEvent.click(screen.getByTestId('proposal-accept-selected'));
    await waitFor(() => expect(s.store.getState().history).toHaveLength(1));
    expect(versionCalls).toHaveBeenCalledWith('p', 't', { cause: 'beforeProposal' });
    const after = s.store.getState();
    expect(after.history).toHaveLength(1);
    expect(after.doc.tracks[0].items.map((i) => [i.id, i.recordIn, i.recordOut])).toEqual([
      ['gen', 0, 45],
      ['mine', 45, 105],
      ['t', 105, 135],
    ]);
    assertInvariants(after.doc);
    // The rejected group stays reviewable; undo restores the start exactly.
    view.rerender(<MantineProvider><ProposalPanel {...props()} /></MantineProvider>);
    expect(screen.getByTestId('proposal-group-0').textContent).toMatch(/Remove mine/);
    act(() => s.store.undo());
    expect(s.store.getState().doc).toStrictEqual(document());
  });

  it('accepts a user-owned change once it is acknowledged', async () => {
    const s = storeWith(document());
    wrap(<ProposalPanel doc={s.store.getState().doc} projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: 0, outFrame: 90 }} onGhosts={() => {}} onApply={s.onApply} onSeek={() => {}} request={async () => ({ ...response, groups: [response.groups[1]] })} />);
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-ack-mine')).toBeTruthy());
    fireEvent.click(screen.getByTestId('proposal-ack-mine'));
    fireEvent.click(screen.getByTestId('proposal-accept-selected'));
    await waitFor(() => expect(s.store.getState().doc.tracks[0].items.map((i) => i.id)).toEqual(['gen', 't']));
  });

  it('says plainly when no chat provider is configured', async () => {
    const { ApiError } = await import('@/lib/api/client');
    wrap(<ProposalPanel doc={document()} projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: null, outFrame: null }} onGhosts={() => {}} onApply={() => {}} onSeek={() => {}} request={async () => { throw new ApiError('no provider', 503); }} />);
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-error').textContent).toMatch(/No chat provider/));
  });
});

describe('StructurePanel', () => {
  it('makes a compound from the selection and decomposes it again', () => {
    const s = storeWith(doc([track(TRACKS.v1, 'video', [media('a', 0, 30), media('b', 30, 60, 100)])]));
    const props = () => ({ doc: s.store.getState().doc, readOnly: false, playheadFrame: 0, playbackSpeed: 0, newId: s.newId, onOp: s.onOp, onApply: s.onApply, onSelect: () => {} });
    const view = wrap(<StructurePanel {...props()} selection={['a', 'b']} />);
    fireEvent.click(screen.getByTestId('compound-create'));
    const created = s.store.getState().doc;
    expect(created.tracks[0].items.map((i) => i.type)).toEqual(['compoundClip']);
    expect(created.sequences).toHaveLength(1);
    view.rerender(<MantineProvider><StructurePanel {...props()} selection={[]} /></MantineProvider>);
    const compoundId = created.tracks[0].items[0].id;
    fireEvent.click(screen.getByTestId(`compound-decompose-${compoundId}`));
    const back = s.store.getState().doc;
    expect(back.sequences ?? []).toEqual([]);
    expect(back.tracks[1].items.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 30], [30, 60]]);
    expect(s.store.getState().history).toHaveLength(2);
  });

  it('makes a multicam, syncs its angles from decoded audio and switches the angle under the playhead', async () => {
    const s = storeWith(doc([track(TRACKS.v1, 'video', [media('camA', 0, 90)]), track(TRACKS.v2, 'video', [media('camB', 0, 90)])]));
    // An envelope with one spike; angle B hears it 0.5 s later → +15 frames at 30 fps.
    const spike = (at: number, seed: number) => {
      let x = seed;
      return Float32Array.from({ length: 400 }, (_, i) => {
        x = (x * 1103515245 + 12345) % 2147483648;
        return i === at ? 1 : 0.02 * (x / 2147483648);
      });
    };
    const decode = vi.fn(async (clip: MediaClip) => (clip.id === 'camA' ? spike(100, 1) : spike(150, 9)));
    const props = (selection: string[]) => ({ doc: s.store.getState().doc, readOnly: false, playheadFrame: 30, playbackSpeed: 0, newId: s.newId, onOp: s.onOp, onApply: s.onApply, onSelect: () => {}, selection, decodeEnvelope: decode });
    const view = wrap(<StructurePanel {...props(['camA', 'camB'])} />);
    fireEvent.click(screen.getByTestId('multicam-sync'));
    await waitFor(() => expect((screen.getByTestId('multicam-offset-camB') as HTMLInputElement).value).toBe('15'));
    fireEvent.click(screen.getByTestId('multicam-create'));
    const mc = s.store.getState().doc.tracks[0].items[0] as MulticamClip;
    expect(mc.type).toBe('multicamClip');
    expect(mc.angles.map((a) => a.offsetFrames)).toEqual([0, 15]);
    view.rerender(<MantineProvider><StructurePanel {...props([mc.id])} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('multicam-angle-2'));
    expect((s.store.getState().doc.tracks[0].items[0] as MulticamClip).activeAngleId).toBe(mc.angles[1].id);
  });
});

describe('BusesPanel', () => {
  it('adds a bus, routes a track to it and turns on a limiter — each one op', () => {
    const s = storeWith(doc([track(TRACKS.a1, 'audio', [media('m', 0, 30)])]));
    const props = () => ({ doc: s.store.getState().doc, readOnly: false, playheadFrame: 0, playbackSpeed: 0, newId: s.newId, onOp: s.onOp, onApply: s.onApply });
    const view = wrap(<BusesPanel {...props()} />);
    fireEvent.change(screen.getByTestId('bus-new-name'), { target: { value: 'Dialogue' } });
    fireEvent.click(screen.getByTestId('bus-add'));
    expect(s.store.getState().doc.buses).toEqual([{ id: 'new-1', name: 'Dialogue', pan: 0, outputBusId: null }]);
    view.rerender(<MantineProvider><BusesPanel {...props()} /></MantineProvider>);
    fireEvent.click(screen.getByTestId(`track-${TRACKS.a1}-limiter`));
    expect(s.store.getState().doc.tracks[0].dynamics).toEqual({ limiter: { ceilingDb: -1 } });
    expect(s.store.getState().history.map((h) => h.label)).toEqual(['Add bus Dialogue', `Toggle limiter (${TRACKS.a1})`]);
  });
});

describe('VoRecorderPanel', () => {
  it('states why recording is unavailable instead of offering a dead button', () => {
    wrap(<VoRecorderPanel doc={doc([])} projectId="p" readOnly={false} playheadFrame={0} newId={() => 'x'} onApply={() => {}} support={{ ok: false, reason: 'This browser cannot record audio (no MediaRecorder).' }} />);
    expect(screen.getByTestId('vo-unavailable').textContent).toMatch(/no MediaRecorder/);
    expect(screen.queryByTestId('vo-record')).toBeNull();
  });

  it('records, uploads and places the take at the frame where recording started', async () => {
    const s = storeWith(doc([track(TRACKS.v1, 'video', [media('v', 0, 300)])]));
    const take = { stop: vi.fn(async () => ({ blob: new Blob(['x'], { type: 'audio/webm' }), durationSeconds: 2 })), cancel: vi.fn() };
    const upload = vi.fn(async () => ({ id: '00000000-0000-4000-8000-000000000123' }));
    wrap(<VoRecorderPanel doc={s.store.getState().doc} projectId="p" readOnly={false} playheadFrame={45} newId={s.newId} onApply={s.onApply} support={{ ok: true, mimeType: 'audio/webm;codecs=opus' }} start={async () => take} upload={upload} countInMs={0} />);
    fireEvent.click(screen.getByTestId('vo-record'));
    await waitFor(() => expect(screen.getByTestId('vo-stop')).toBeTruthy());
    fireEvent.click(screen.getByTestId('vo-stop'));
    await waitFor(() => expect(s.store.getState().doc.tracks).toHaveLength(2));
    const vo = s.store.getState().doc.tracks[1];
    expect(vo.kind).toBe('audio');
    expect(vo.items[0]).toMatchObject({ recordIn: 45, recordOut: 105, assetRef: { kind: 'projectFile', projectFileId: '00000000-0000-4000-8000-000000000123' } });
    const uploaded = (upload.mock.calls[0] as unknown[])[1] as File;
    expect(uploaded).toBeInstanceOf(File);
    expect(uploaded.type, 'the upload carries the bare media type, not the recorder parameter').toBe('audio/webm');
    expect(uploaded.name).toMatch(/\.webm$/);
  });
});

describe('ProposalPanel - estimate, allowance and per-change skip (T-022)', () => {
  const resp: EditProposalResponse = {
    proposalId: 'pr2', agent: 'recut', summary: 'Two changes.', rejected: [],
    disclosure: { itemCount: 2, rangeSeconds: 5, soundFileCount: 0, briefChars: 0, transcriptSeconds: 0, summary: 'Sent to the model: 2 clips.' },
    groups: [
      { label: 'Trim A', ops: [{ type: 'trim', itemId: 'gen', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['gen'], touchesUserOwned: false, reason: null },
      { label: 'Remove B', ops: [{ type: 'extract', itemId: 'gen2' }], touchedItemIds: ['gen2'], touchesUserOwned: false, reason: null },
    ],
  };
  it('shows price and allowance before asking, and skips one change', async () => {
    const document = doc([track(TRACKS.v1, 'video', [generated('gen', 0, 60), generated('gen2', 60, 120)])]);
    const estimate = vi.fn(async () => ({ billingEnabled: true, credits: 2, affordable: true, used: 8, limit: 20, disclosure: { itemCount: 2, rangeSeconds: 5, soundFileCount: 0, briefChars: 0, transcriptSeconds: 0, summary: 'Sent to the model: 2 clips.' } }));
    wrap(<ProposalPanel doc={document} projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: null, outFrame: null }} onGhosts={() => {}} onApply={() => {}} onSeek={() => {}} request={async () => resp} estimate={estimate} />);
    await waitFor(() => expect(screen.getByTestId('proposal-estimate').textContent).toMatch(/about 2 credits/));
    expect(screen.getByTestId('proposal-allowance').textContent).toMatch(/12 of 20/);
    expect(screen.getByTestId('proposal-disclosure').textContent).toMatch(/Sent to the model/);
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(screen.getByTestId('proposal-group-1')).toBeTruthy());
    fireEvent.click(screen.getByTestId('proposal-skip-0'));
    expect(screen.queryByTestId('proposal-group-1')).toBeNull();
    expect(screen.getByTestId('proposal-group-0').textContent).toMatch(/Remove B/);
  });
});
