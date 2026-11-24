// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditOp } from '../../engine';
import type { CompoundClip, EditTimeline } from '../../types';
import { anchorForClips } from '../anchor';
import { createEditorStore } from '../../store';
import { entriesAffectedBy, jobsHolding, marksAffectedBy } from '../edit-guard';
import {
  clearJobMark, getExternalJobMarks, marksByClip, marksFromEntries, onReviewRequest, registerJobMark, requestReview, resetJobMarks, subscribeJobMarks, updateJobMark, type JobMark,
} from '../job-marks';
import { heldKeysFor, JOB_READS, SOURCE_KEYS } from '../job-protection';
import { resetResultsStores, type BackgroundEntry } from '../results-store';

const d: EditTimeline = doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0), media('c2', 60, 120, 0)])]);
const entry = (over: Partial<BackgroundEntry> = {}): BackgroundEntry => ({
  id: 'r1', tool: 'objectEffect', requestId: 'r1', anchor: anchorForClips(d, ['c1'], { startSec: 0, endSec: null }), intent: {}, state: 'running', createdAt: '2026-01-01T00:00:00Z', ...over,
});
const label = (tool: string) => `job:${tool}`;
const setKey = (itemId: string, key: string): EditOp => ({ type: 'setProperty', itemId, key, value: undefined }) as unknown as EditOp;

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
  resetJobMarks();
});

describe('job mark registry', () => {
  it('registers, updates and clears a mark, telling subscribers each time', () => {
    const seen = vi.fn();
    const off = subscribeJobMarks(seen);
    const mark: JobMark = { id: 'follow:c1', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'Following' };
    registerJobMark(mark);
    expect(getExternalJobMarks()).toEqual([mark]);
    updateJobMark('follow:c1', { progress: 0.5 });
    expect(getExternalJobMarks()[0]!.progress).toBe(0.5);
    clearJobMark('follow:c1');
    expect(getExternalJobMarks()).toEqual([]);
    expect(seen).toHaveBeenCalledTimes(3);
    clearJobMark('follow:c1');
    expect(seen).toHaveBeenCalledTimes(3);
    off();
  });
  it('registering the same id again replaces it', () => {
    registerJobMark({ id: 'a', jobKind: 'enhance', clipIds: ['c1'], state: 'running', label: 'x' });
    registerJobMark({ id: 'a', jobKind: 'enhance', clipIds: ['c2'], state: 'running', label: 'x' });
    expect(getExternalJobMarks()).toHaveLength(1);
    expect(getExternalJobMarks()[0]!.clipIds).toEqual(['c2']);
  });
  it('a review request reaches its listener', () => {
    const got = vi.fn();
    const off = onReviewRequest(got);
    requestReview('r1');
    expect(got).toHaveBeenCalledWith('r1');
    off();
    requestReview('r2');
    expect(got).toHaveBeenCalledTimes(1);
  });
});

describe('marks from results-store entries', () => {
  it('running, needs-review and failed become marks; applied and discarded do not', () => {
    const marks = marksFromEntries(
      [entry(), entry({ id: 'r2', state: 'needsReview', review: { applicable: ['c1'], reasons: [], lostCount: 0 } }), entry({ id: 'r3', state: 'failed', failure: { code: null, message: 'No credits' } }), entry({ id: 'r4', state: 'applied' }), entry({ id: 'r5', state: 'discarded' })],
      d,
      label,
    );
    expect(marks.map((m) => [m.id, m.state])).toEqual([['r1', 'running'], ['r2', 'needsReview'], ['r3', 'failed']]);
    expect(marks[0]!.label).toBe('job:objectEffect');
    expect(marks[2]!.message).toBe('No credits');
  });
  it('carries the entry progress when the job reports one', () => {
    expect(marksFromEntries([entry({ progress: 0.4 })], d, label)[0]!.progress).toBe(0.4);
    expect(marksFromEntries([entry()], d, label)[0]!.progress).toBeUndefined();
  });
  it('an entry whose clips are gone makes no mark', () => {
    const gone = entry({ anchor: { ...entry().anchor, assetKey: 'file:00000000-0000-4000-8000-0000000000ff', clipIds: ['missing'], assetClipIdsAtLaunch: [] } });
    expect(marksFromEntries([gone], d, label)).toEqual([]);
  });
});

describe('one mark per clip', () => {
  const mark = (over: Partial<JobMark>): JobMark => ({ id: 'm', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'L', ...over });
  it('running beats needs-review beats failed', () => {
    const map = marksByClip([mark({ id: 'a', state: 'failed' }), mark({ id: 'b', state: 'needsReview' }), mark({ id: 'c', state: 'running', progress: 2 })], d);
    expect(map.get('c1')).toMatchObject({ state: 'running', markId: 'c', progress: 1 });
    expect(map.get('c2')).toBeUndefined();
  });
  it('a compound shows the mark of a clip inside it', () => {
    const compound = { id: 'cmp', type: 'compoundClip', trackId: TRACKS.v1, recordIn: 0, recordOut: 60, sequenceId: 'seq' } as unknown as CompoundClip;
    const dd: EditTimeline = { ...doc([track(TRACKS.v1, 'video', [compound])]), sequences: [{ id: 'seq', name: 'S', tracks: [track('00000000-0000-4000-8000-0000000000b1', 'video', [media('inner', 0, 60, 0)])] }] };
    expect(marksByClip([mark({ clipIds: ['inner'] })], dd).get('cmp')?.state).toBe('running');
  });
});

describe('which edits each job kind holds', () => {
  it('every kind holds the source keys', () => {
    for (const kind of [...Object.keys(JOB_READS).filter((k) => k !== 'stabilize'), 'somethingNew']) for (const key of SOURCE_KEYS) expect(heldKeysFor(kind).has(key)).toBe(true);
  });
  it.each([
    ['objectEffect', 'stabilize', true],
    ['objectEffect', 'transform', false],
    ['objectEffect', 'grade', false],
    ['audioCleanup', 'crop', false],
    ['proposal', 'transform', false],
    ['follow', 'crop', true],
    ['follow', 'reframe', true],
    ['follow', 'transform', true],
    ['follow', 'grade', false],
    ['follow', 'effects', false],
    ['stabilize', 'transform', false],
    ['stabilize', 'assetRef', true],
    ['stabilize', 'stabilize', true],
    ['stabilize', 'playbackRate', false],
    ['stabilize', 'grade', false],
    ['enhance', 'crop', false],
    ['unknownKind', 'crop', false],
    ['toString', 'crop', false],
    ['constructor', 'transform', false],
  ] as const)('%s holds %s: %s', (kind, key, held) => {
    expect(heldKeysFor(kind).has(key)).toBe(held);
  });
  it('a running follow holds a crop edit on its clip; a running magic blur does not', () => {
    const follow: JobMark = { id: 'f', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'F' };
    expect(marksAffectedBy([setKey('c1', 'crop')], d, [follow], new Set())).toHaveLength(1);
    expect(marksAffectedBy([setKey('c2', 'crop')], d, [follow], new Set())).toEqual([]);
    expect(entriesAffectedBy([setKey('c1', 'crop')], d, [entry()], new Set())).toEqual([]);
    expect(entriesAffectedBy([setKey('c1', 'playbackRate')], d, [entry()], new Set())).toHaveLength(1);
  });
  it('"go on" is remembered per job, and only running jobs hold', () => {
    const follow: JobMark = { id: 'f', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'F' };
    expect(marksAffectedBy([setKey('c1', 'transform')], d, [follow], new Set(['f']))).toEqual([]);
    expect(marksAffectedBy([setKey('c1', 'transform')], d, [{ ...follow, state: 'needsReview' }], new Set())).toEqual([]);
  });
  it('a delete or trim is held by every kind', () => {
    const follow: JobMark = { id: 'f', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'F' };
    expect(marksAffectedBy([{ type: 'lift', itemId: 'c1' } as EditOp], d, [follow], new Set())).toHaveLength(1);
  });
});

describe("a job's own result is never held by its own mark", () => {
  const follow: JobMark = { id: 'follow:c1', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'F' };
  const write = { type: 'setProperty', itemId: 'c1', key: 'transform', value: { scale: { value: 1.2 } } } as unknown as EditOp;
  const makeGuarded = () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    registerJobMark(follow);
    store.setEditGuard((ops, doc, _label, exempt) => {
      const held = jobsHolding(ops, doc, [], getExternalJobMarks(), new Set(), exempt);
      return held.entries.length === 0 && held.marks.length === 0;
    });
    return store;
  };
  it('the follow result lands with the exemption', () => {
    const store = makeGuarded();
    expect(store.dispatchBatch([write], 'Follow', { exemptJobId: 'follow:c1' })).toBe(true);
    expect((store.getState().doc.tracks[0]!.items[0] as { transform?: unknown }).transform).toBeTruthy();
  });
  it('the same write from the user (no exemption) is held, and nothing changed', () => {
    const store = makeGuarded();
    expect(store.dispatchBatch([write], 'Edit')).toBe(false);
    expect((store.getState().doc.tracks[0]!.items[0] as { transform?: unknown }).transform).toBeUndefined();
  });
  it("another job's exemption does not wave the follow mark through", () => {
    const store = makeGuarded();
    expect(store.dispatchBatch([write], 'Edit', { exemptJobId: 'someone-else' })).toBe(false);
  });
  it('dispatch reports whether it landed', () => {
    const store = makeGuarded();
    expect(store.dispatch(write as never, 'x', { exemptJobId: 'follow:c1' })).toBe(true);
  });
});
