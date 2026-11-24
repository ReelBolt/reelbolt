// @vitest-environment jsdom
/**
 * P6 UI against a real store: deleting words in the transcript panel is a real ripple cut on the
 * timeline (the wave's browser-assertion property, here at the component level); fillers are
 * individually rejectable; captions generate onto a new track; the Cut page smart-inserts a pool
 * source at the nearest edit point; a take swap is one replace; the annotation editor sends only
 * what changed; a smart bin is created through the bins client with its filter.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useState } from 'react';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { MediaBin, MediaPoolItem } from '@/lib/editing/media/types';
import { TranscriptPanel } from '../panels/TranscriptPanel';
import { SubtitlesPanel } from '../panels/SubtitlesPanel';
import { TakeStackPanel } from '../panels/TakeStackPanel';
import { CutWorkspace } from '../cut/CutWorkspace';
import { AnnotationEditor, annotationRequest } from '../media/AnnotationEditor';
import { BinTree } from '../media/BinTree';
import { CLIP_DRAG_MIME } from '@/lib/editing/media/bins';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const ARTIFACT = {
  shots: [
    { id: 's0', startSec: 0, endSec: 10, visual: { motionMean: 0.01, motionClass: 'Static', duplicateGroupId: 'k0', isBestTake: false } },
    { id: 's1', startSec: 20, endSec: 26, visual: { motionMean: 0.02, motionClass: 'Static', duplicateGroupId: 'k0', isBestTake: true } },
  ],
  duplicateGroups: [{ id: 'k0', shotIds: ['s0', 's1'], bestShotId: 's1', meanSimilarity: 0.93 }],
  segments: [{ id: 't0', startSec: 0, endSec: 5, text: 'so um the the quick brown fox.' }],
  words: [
    { id: 'w0', startSec: 0.0, endSec: 0.5, text: 'so' },
    { id: 'w1', startSec: 0.5, endSec: 1.0, text: 'um' },
    { id: 'w2', startSec: 1.0, endSec: 1.5, text: 'the' },
    { id: 'w3', startSec: 1.6, endSec: 2.0, text: 'the' },
    { id: 'w4', startSec: 2.0, endSec: 3.0, text: 'quick' },
    { id: 'w5', startSec: 3.0, endSec: 4.0, text: 'brown' },
    { id: 'w6', startSec: 4.0, endSec: 5.0, text: 'fox.' },
  ],
};

const hooks = vi.hoisted(() => ({
  artifact: { data: undefined as unknown, isLoading: false, error: undefined as unknown },
  pool: { data: [] as MediaPoolItem[], error: undefined as unknown, isLoading: false, mutate: vi.fn() },
  api: { createBin: vi.fn(), fetchBinItems: vi.fn() },
}));
// The panels read the analysis through the resolver; here it hands back the fixture analysis as a
// legacy single-source document (no `sources`), which the one-file fixture timeline accepts.
vi.mock('@/lib/editing/api/analysis', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/analysis')>();
  return {
    ...actual,
    useTimelineAnalysis: () => ({ ...hooks.artifact, data: hooks.artifact.data === undefined ? undefined : { stepResultId: 'analysis', artifact: hooks.artifact.data, sources: [] } }),
  };
});
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => hooks.pool, useMediaBins: () => ({ data: [] }), useMediaAnnotations: () => ({ data: [] }) }));
vi.mock('@/lib/editing/api/media', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/media')>();
  return { ...actual, ...hooks.api };
});
// The overview strip paints to a canvas, which jsdom has no 2D context for.
vi.mock('../timeline/TimelineCanvas', () => ({ TimelineCanvas: (props: { pixelsPerFrame: number }) => <div data-testid="overview-canvas" data-ppf={props.pixelsPerFrame} /> }));

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
afterEach(() => {
  cleanup();
  hooks.api.createBin.mockReset();
  hooks.api.fetchBinItems.mockReset();
});

const interviewDoc = () => doc([track(TRACKS.v1, 'video', [media('c1', 0, 300, 0), media('c2', 300, 480, 600)])]);

function storeWith(document = interviewDoc()) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  const onApply = (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label);
  const onOp = (op: EditOp, label?: string) => store.dispatch(op, label);
  const onSetProperty = (itemId: string, key: PropertyKey, value: PropertyValue) => store.dispatch({ type: 'setProperty', itemId, key, value });
  let n = 0;
  const newId = () => `new-${++n}`;
  return { store, onApply, onOp, onSetProperty, newId };
}

describe('TranscriptPanel', () => {
  it('deleting a word range ripple-cuts exactly those frames in one undo step', () => {
    hooks.artifact.data = ARTIFACT;
    const { store, onApply, newId } = storeWith();
    const seek = vi.fn();
    wrap(<TranscriptPanel doc={store.getState().doc} projectId="p" seedStepResultId="seed" readOnly={false} newItemId={newId} onApply={onApply} onSeek={seek} />);

    fireEvent.click(screen.getByTestId('transcript-word-w2'));
    expect(seek).toHaveBeenCalledWith(30);
    fireEvent.click(screen.getByTestId('transcript-word-w4'), { shiftKey: true });
    expect(screen.getByTestId('transcript-delete').textContent).toContain('(60 f)');
    fireEvent.click(screen.getByTestId('transcript-delete'));

    const items = store.getState().doc.tracks[0].items as MediaClip[];
    expect(items.map((c) => [c.recordIn, c.recordOut, c.sourceIn])).toEqual([[0, 30, 0], [30, 240, 90], [240, 420, 600]]);
    expect(store.getState().history).toHaveLength(1);
    expect(store.getState().history[0].label).toMatch(/Delete 3 words/);
    store.undo();
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).recordOut).toBe(300);
  });

  it('lists fillers as rejectable checkboxes and cuts only the accepted ones', () => {
    hooks.artifact.data = ARTIFACT;
    const { store, onApply, newId } = storeWith();
    wrap(<TranscriptPanel doc={store.getState().doc} projectId="p" seedStepResultId="seed" readOnly={false} newItemId={newId} onApply={onApply} onSeek={() => {}} />);
    fireEvent.click(screen.getByTestId('transcript-fillers-toggle'));
    expect(screen.getByTestId('transcript-filler-filler:w1')).toBeDefined();
    expect(screen.getByTestId('transcript-filler-repeat:w2')).toBeDefined();
    // Reject the stutter; only "um" (frames 15..30) is cut.
    fireEvent.click(screen.getByTestId('transcript-filler-repeat:w2'));
    expect(screen.getByTestId('transcript-fillers-apply').textContent).toContain('Remove 1 of 2');
    fireEvent.click(screen.getByTestId('transcript-fillers-apply'));
    const items = store.getState().doc.tracks[0].items as MediaClip[];
    expect(items.map((c) => [c.recordIn, c.recordOut, c.sourceIn])).toEqual([[0, 15, 0], [15, 285, 30], [285, 465, 600]]);
  });

  it('says there is nothing to read without a seed', () => {
    hooks.artifact.data = undefined;
    const { store, onApply, newId } = storeWith();
    wrap(<TranscriptPanel doc={store.getState().doc} projectId="p" seedStepResultId={null} readOnly={false} newItemId={newId} onApply={onApply} onSeek={() => {}} />);
    expect(screen.getByText(/No transcript to read/)).toBeDefined();
  });
});

describe('SubtitlesPanel', () => {
  it('generates captions from the transcript onto a new track and links the sidecars', () => {
    hooks.artifact.data = ARTIFACT;
    const { store, onApply, onSetProperty, newId } = storeWith();
    wrap(<SubtitlesPanel doc={store.getState().doc} projectId="p" timelineId="t" seedStepResultId="seed" readOnly={false} playheadFrame={0} newItemId={newId} newTrackId={() => 'subs'} onApply={onApply} onSetProperty={onSetProperty} onSeek={() => {}} />);
    expect(screen.getByTestId('subtitles-preview-count').textContent).toMatch(/cue.* from 7 words/);
    fireEvent.click(screen.getByTestId('subtitles-generate'));
    const state = store.getState();
    expect(state.doc.tracks.map((t) => t.id)).toEqual([TRACKS.v1, 'subs']);
    const captions = state.doc.tracks[1].items;
    expect(captions.length).toBeGreaterThanOrEqual(1);
    expect(captions[0].type).toBe('caption');
    expect(captions[0].recordIn).toBe(0);
    expect(state.history).toHaveLength(1);
    expect(screen.getByTestId('subtitles-srt').getAttribute('href')).toBe('/api/v1/projects/p/edit-timelines/t/export?format=srt');
    expect(screen.getByTestId('subtitles-vtt').getAttribute('href')).toBe('/api/v1/projects/p/edit-timelines/t/export?format=vtt');
  });
});

describe('CutWorkspace', () => {
  it('smart-inserts a pool source at the edit point nearest the playhead', () => {
    hooks.artifact.data = ARTIFACT;
    hooks.pool.data = [
      { mediaRef: 'file:00000000-0000-4000-8000-000000000777', kind: 'video', fileName: 'drone.mp4', mimeType: 'video/mp4', sizeBytes: 1, category: 'userFiles', probe: { durationSec: 2, fpsNum: 30, fpsDen: 1, width: 1920, height: 1080, videoCodec: null, audioCodec: null, container: null, audioChannels: null, audioSampleRateHz: null, bitrateBps: null }, bins: [], filmstripUrl: '' } as unknown as MediaPoolItem,
    ];
    const { store, onApply, onOp, newId } = storeWith();
    const seek = vi.fn();
    const select = vi.fn();
    wrap(
      <CutWorkspace
        doc={store.getState().doc}
        projectId="p"
        seedStepResultId="seed"
        readOnly={false}
        playheadFrame={290}
        playbackSpeed={0}
        selection={[]}
        trackId={null}
        trackFlags={() => ({ enabled: true, locked: false, mute: false, solo: false, collapsed: false })}
        parityOf={() => 'faithful'}
        newItemId={newId}
        onSeek={seek}
        onSelect={select}
        onSetSpeed={() => {}}
        onOp={onOp}
        onApply={onApply}
      />,
    );
    // T-003: the overview strip is its own page band (CutOverview); this component keeps the tools.
    fireEvent.click(screen.getByTestId('cut-insert-file:00000000-0000-4000-8000-000000000777'));
    const items = store.getState().doc.tracks[0].items;
    expect(items.map((i) => [i.name, i.recordIn, i.recordOut])).toEqual([['c1', 0, 300], ['drone.mp4', 300, 360], ['c2', 360, 540]]);
    expect((items[1] as MediaClip).assetRef).toEqual({ kind: 'projectFile', projectFileId: '00000000-0000-4000-8000-000000000777' });
    expect(seek).toHaveBeenCalledWith(300);
    expect(select).toHaveBeenCalledWith('new-1');
    // The boring-shot list names the long still shot the analysis measured, and removes it as a ripple cut.
    expect(screen.getByTestId('cut-boring-s0')).toBeDefined();
  });
});

describe('TakeStackPanel', () => {
  it('swaps a clip to an alternate take with one replace that keeps the slot', () => {
    hooks.artifact.data = ARTIFACT;
    // A 5 s clip, so the 6 s alternate s1 covers it.
    const { store, onOp } = storeWith(doc([track(TRACKS.v1, 'video', [media('c1', 0, 150, 0), media('c2', 150, 330, 600)])]));
    wrap(<TakeStackPanel doc={store.getState().doc} projectId="p" seedStepResultId="seed" readOnly={false} onOp={onOp} onSeek={() => {}} onSelect={() => {}} />);
    fireEvent.click(screen.getByTestId('take-use-c1-s1'));
    const clip = store.getState().doc.tracks[0].items[0] as MediaClip;
    expect([clip.recordIn, clip.recordOut, clip.sourceIn]).toEqual([0, 150, 600]);
    expect(store.getState().history[0].label).toBe('Use take s1');
  });

  it('refuses an alternate shorter than the clip with a plain sentence and changes nothing (d4 N5)', () => {
    hooks.artifact.data = ARTIFACT;
    const { store, onOp } = storeWith();
    wrap(<TakeStackPanel doc={store.getState().doc} projectId="p" seedStepResultId="seed" readOnly={false} onOp={onOp} onSeek={() => {}} onSelect={() => {}} />);
    fireEvent.click(screen.getByTestId('take-use-c1-s1'));
    expect(screen.getByTestId('take-refusal').textContent).toContain('This take is 6.0 s long but the clip needs 10.0 s');
    expect(store.getState().history).toHaveLength(0);
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).sourceIn).toBe(0);
  });
});

describe('AnnotationEditor', () => {
  it('sends only the changed fields, normalising keywords', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    wrap(<AnnotationEditor annotation={{ mediaRef: 'file:x', keywords: ['interview'], note: 'n', colorLabel: 'red', favourite: false, flag: false, updatedAt: '2026-10-11T00:00:00Z' }} onSave={onSave} />);
    fireEvent.click(screen.getByTestId('annotation-favourite'));
    // R05.8: no Save button; a switch saves on change.
    expect(screen.queryByTestId('annotation-save')).toBeNull();
    await waitFor(() => expect(onSave).toHaveBeenCalledWith({ favourite: true }));
    await waitFor(() => expect(screen.getByTestId('annotation-message').textContent).toBe('Saved.'));
    expect(onSave).toHaveBeenCalledTimes(1);
    expect(annotationRequest(null, { keywords: [' B-Roll', 'b-roll', ''], note: '', colorLabel: null, favourite: false, flag: true })).toEqual({ keywords: ['b-roll'], flag: true });
    expect(annotationRequest(null, { keywords: [], note: '', colorLabel: null, favourite: false, flag: false })).toBeNull();
  });
});

describe('R05.8 AnnotationEditor saves on change', () => {
  const stored = { mediaRef: 'file:x', keywords: [], note: '', colorLabel: null, favourite: false, flag: false, updatedAt: '2026-10-11T00:00:00Z' };

  it('writes a typed note once, after the pause, as a single request', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    wrap(<AnnotationEditor annotation={stored} onSave={onSave} />);
    const note = screen.getByTestId('annotation-note');
    fireEvent.change(note, { target: { value: 'ch' } });
    fireEvent.change(note, { target: { value: 'chosen take' } });
    expect(onSave).not.toHaveBeenCalled();
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1), { timeout: 3000 });
    expect(onSave).toHaveBeenCalledWith({ note: 'chosen take' });
  });

  it('shows plain error text with Try again, and does not retry on its own', async () => {
    const onSave = vi.fn().mockRejectedValueOnce(new Error('Network down')).mockResolvedValue(undefined);
    wrap(<AnnotationEditor annotation={stored} onSave={onSave} />);
    fireEvent.click(screen.getByTestId('annotation-flag'));
    await waitFor(() => expect(screen.getByTestId('annotation-retry')).toBeTruthy());
    expect(screen.getByTestId('annotation-message').textContent ?? '').not.toBe('');
    await new Promise((r) => setTimeout(r, 200));
    expect(onSave).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('annotation-retry'));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(2));
  });
});

describe('BinTree smart bins', () => {
  function Harness({ initial }: { initial: MediaBin[] }) {
    const [bins, setBins] = useState<MediaBin[]>(initial);
    const [selected, setSelected] = useState<string | null>(null);
    return <BinTree projectId="p" bins={bins} onBinsChange={setBins} selectedBinId={selected} onSelect={setSelected} countsByBinId={{}} />;
  }

  it('creates a smart bin with its filter and marks it in the tree', async () => {
    hooks.api.createBin.mockResolvedValue({ id: 'smart-1', name: 'Favourite video', parentBinId: null, sortOrder: 1, itemCount: 0, smartFilter: { kinds: ['video'], favourite: true }, isSmart: true } satisfies MediaBin);
    hooks.api.fetchBinItems.mockResolvedValue([]);
    wrap(<Harness initial={[]} />);
    fireEvent.click(screen.getByTestId('smart-bin-new'));
    fireEvent.change(await screen.findByTestId('smart-bin-name-input'), { target: { value: 'Favourite video' } });
    fireEvent.change(screen.getByTestId('smart-bin-name-contains'), { target: { value: 'cam' } });
    await act(async () => {
      fireEvent.click(screen.getByTestId('smart-bin-save'));
    });
    expect(hooks.api.createBin).toHaveBeenCalledWith('p', { name: 'Favourite video', smartFilter: { nameContains: 'cam' } });
    await waitFor(() => expect(screen.getByTestId('bin-smart-smart-1')).toBeDefined());
  });

  it('refuses a drop on a smart bin without issuing a call', () => {
    wrap(<Harness initial={[{ id: 'smart-1', name: 'Smart', parentBinId: null, sortOrder: 1, itemCount: 0, smartFilter: { favourite: true }, isSmart: true }]} />);
    const target = screen.getByTestId('bin-drop-target-smart-1');
    const store = new Map<string, string>([[CLIP_DRAG_MIME, 'file:00000000-0000-4000-8000-000000000001']]);
    fireEvent.drop(target, { dataTransfer: { getData: (k: string) => store.get(k) ?? '', types: [...store.keys()], setData: () => {} } });
    expect(screen.getByTestId('bin-message').textContent).toMatch(/smart bin/);
  });
});
