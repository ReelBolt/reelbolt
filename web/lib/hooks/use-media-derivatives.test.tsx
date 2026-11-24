// @vitest-environment jsdom
/**
 * T-022 (audit top-10 #7, d2 N2/N4): the timeline asks for its sources' derivatives ONCE per open,
 * and the media pane hears the pipeline's own progress on the Go API's global SSE feed instead of
 * waiting for a "Check again" click.
 */
import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('@/lib/editing/api/media', async (orig) => ({
  ...(await orig<typeof import('@/lib/editing/api/media')>()),
  requestMediaDerivatives: vi.fn().mockResolvedValue({}),
}));

import { requestMediaDerivatives } from '@/lib/editing/api/media';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import {
  MEDIA_DERIVATIVE_PROGRESS_EVENT,
  parseMediaDerivativeProgress,
  poolStageStoresProbe,
  progressSentence,
  REQUESTED_BY_TIMELINE_OPEN,
  timelineMediaRefs,
  useMediaDerivativeProgress,
  useRequestTimelineDerivatives,
  type MediaDerivativeProgressEvent,
} from './use-media-derivatives';

const PROJECT = '11111111-1111-4111-8111-111111111111';
const FILE_A = '00000000-0000-4000-8000-00000000000a';
const FILE_B = '00000000-0000-4000-8000-00000000000b';
const STEP = '00000000-0000-4000-8000-0000000000c1';

const clipOf = (id: string, recordIn: number, recordOut: number, assetRef: MediaClip['assetRef']): MediaClip => ({
  ...(media(id, recordIn, recordOut) as MediaClip),
  assetRef,
});

const timeline = (): EditTimeline =>
  doc([
    track(TRACKS.v1, 'video', [
      clipOf('c1', 0, 30, { kind: 'projectFile', projectFileId: FILE_A }),
      clipOf('c2', 30, 60, { kind: 'stepResult', stepResultId: STEP }),
      clipOf('c3', 60, 90, { kind: 'projectFile', projectFileId: FILE_A }),
    ]),
    track(TRACKS.a1, 'audio', [clipOf('a1', 0, 30, { kind: 'projectFile', projectFileId: FILE_B })]),
  ]);

const frame = (data: Record<string, unknown>, projectId: string = PROJECT) =>
  JSON.stringify({ type: MEDIA_DERIVATIVE_PROGRESS_EVENT, projectId, timestamp: '2026-10-13T00:00:00Z', data: { projectId, ...data } });

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  url: string;
  closed = false;
  listeners = new Map<string, ((event: Event) => void)[]>();
  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, handler: (event: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), handler]);
  }
  removeEventListener(type: string, handler: (event: Event) => void) {
    this.listeners.set(type, (this.listeners.get(type) ?? []).filter((h) => h !== handler));
  }
  close() {
    this.closed = true;
  }
  emit(type: string, data: string) {
    for (const handler of this.listeners.get(type) ?? []) handler(new MessageEvent(type, { data }));
  }
}

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  vi.mocked(requestMediaDerivatives).mockClear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('timelineMediaRefs', () => {
  it('lists every distinct source the clips point at, files and step results', () => {
    expect(timelineMediaRefs(timeline()).sort()).toEqual([`file:${FILE_A}`, `file:${FILE_B}`, `step_result:${STEP}`].sort());
  });
});

describe('useRequestTimelineDerivatives', () => {
  function Harness({ timelineId, d }: { timelineId: string; d: EditTimeline }) {
    useRequestTimelineDerivatives(PROJECT, timelineId, d);
    return null;
  }
  const flush = () => act(async () => { await new Promise((r) => setTimeout(r, 0)); });

  it('asks once per source when the timeline opens, and not again on re-render', async () => {
    const d = timeline();
    const view = render(<Harness timelineId="t1" d={d} />);
    await flush();
    view.rerender(<Harness timelineId="t1" d={{ ...d }} />);
    await flush();
    const calls = vi.mocked(requestMediaDerivatives).mock.calls;
    expect(calls).toHaveLength(3);
    expect(calls.map(([projectId]) => projectId)).toEqual([PROJECT, PROJECT, PROJECT]);
    expect(calls.map(([, body]) => body.mediaRef).sort()).toEqual([`file:${FILE_A}`, `file:${FILE_B}`, `step_result:${STEP}`].sort());
    expect(calls.every(([, body]) => body.requestedBy === REQUESTED_BY_TIMELINE_OPEN)).toBe(true);
  });

  it('asks for a source added later exactly once, and again for every source when another timeline opens', async () => {
    const d = timeline();
    const view = render(<Harness timelineId="t1" d={d} />);
    await flush();
    const FILE_C = '00000000-0000-4000-8000-00000000000c';
    const grown: EditTimeline = { ...d, tracks: [{ ...d.tracks[0], items: [...d.tracks[0].items, clipOf('c4', 90, 120, { kind: 'projectFile', projectFileId: FILE_C })] }, d.tracks[1]] };
    view.rerender(<Harness timelineId="t1" d={grown} />);
    await flush();
    expect(vi.mocked(requestMediaDerivatives)).toHaveBeenCalledTimes(4);
    expect(vi.mocked(requestMediaDerivatives).mock.calls[3][1].mediaRef).toBe(`file:${FILE_C}`);
    view.rerender(<Harness timelineId="t2" d={grown} />);
    await flush();
    expect(vi.mocked(requestMediaDerivatives)).toHaveBeenCalledTimes(8);
  });

  it('a refused request is swallowed and does not stop the others', async () => {
    vi.mocked(requestMediaDerivatives).mockRejectedValueOnce(new Error('boom'));
    render(<Harness timelineId="t1" d={timeline()} />);
    await flush();
    expect(vi.mocked(requestMediaDerivatives)).toHaveBeenCalledTimes(3);
  });
});

describe('parseMediaDerivativeProgress', () => {
  it('reads the relay frame for this project', () => {
    expect(parseMediaDerivativeProgress(frame({ sourceProjectFileId: FILE_A, stage: 'Proxy', percentComplete: 40 }), PROJECT)).toEqual({
      mediaRef: `file:${FILE_A}`,
      stage: 'Proxy',
      percentComplete: 40,
    });
  });

  it('ignores another project, an unknown stage and garbage', () => {
    const other = '22222222-2222-4222-8222-222222222222';
    expect(parseMediaDerivativeProgress(frame({ stage: 'Done' }, other), PROJECT)).toBeNull();
    expect(parseMediaDerivativeProgress(frame({ stage: 'Exploded' }), PROJECT)).toBeNull();
    expect(parseMediaDerivativeProgress('not json', PROJECT)).toBeNull();
  });

  it('a render (no file id) is reported with a null mediaRef', () => {
    expect(parseMediaDerivativeProgress(frame({ stage: 'Done' }), PROJECT)?.mediaRef).toBeNull();
  });

  it('says where the preparation is in plain words', () => {
    expect(progressSentence({ stage: 'Proxy', percentComplete: 40 })).toBe('Preparing this file: making the playback copy (40%).');
    expect(progressSentence({ stage: 'Done', percentComplete: null })).toBe('This file is prepared.');
  });
});

describe('useMediaDerivativeProgress', () => {
  it('listens on the global feed and hands over only this project\'s frames', () => {
    const seen: MediaDerivativeProgressEvent[] = [];
    function Harness() {
      useMediaDerivativeProgress(PROJECT, (event) => seen.push(event));
      return null;
    }
    const view = render(<Harness />);
    expect(FakeEventSource.instances).toHaveLength(1);
    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/v1/workflows/events');
    act(() => {
      source.emit(MEDIA_DERIVATIVE_PROGRESS_EVENT, frame({ sourceProjectFileId: FILE_A, stage: 'Filmstrip' }));
      source.emit(MEDIA_DERIVATIVE_PROGRESS_EVENT, frame({ stage: 'Done' }, '22222222-2222-4222-8222-222222222222'));
    });
    expect(seen).toEqual([{ mediaRef: `file:${FILE_A}`, stage: 'Filmstrip', percentComplete: null }]);
    view.unmount();
    expect(source.closed).toBe(true);
  });
});

describe('poolStageStoresProbe (the editor re-reads the media pool once the ingest probe is stored)', () => {
  it('is false while the run is still queued, probing or failed, and true once the probe is behind it', () => {
    expect(['Queued', 'Probing', 'Failed', 'Skipped'].map((s) => poolStageStoresProbe(s as MediaDerivativeProgressEvent['stage']))).toEqual([false, false, false, false]);
    expect(['Proxy', 'Filmstrip', 'Peaks', 'KeyframeIndex', 'Done'].every((s) => poolStageStoresProbe(s as MediaDerivativeProgressEvent['stage']))).toBe(true);
  });
});
