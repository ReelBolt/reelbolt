// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import {
  DEFAULT_TIMELINE_SORT,
  filterTimelines,
  loadTimelineSort,
  saveTimelineSort,
  sortTimelines,
  trashSentence,
} from '../timeline-list';

const t = (id: string, name: string, createdAt: string, updatedAt: string) => ({ id, name, createdAt, updatedAt });
const rows = [
  t('a', 'Beach trip', '2026-01-01T00:00:00Z', '2026-03-01T00:00:00Z'),
  t('b', 'alpha cut', '2026-02-01T00:00:00Z', '2026-01-15T00:00:00Z'),
  t('c', 'Zebra', '2026-03-01T00:00:00Z', '2026-02-01T00:00:00Z'),
];

describe('R01.1 timeline list', () => {
  it('filters by name, case-insensitive, instantly', () => {
    expect(filterTimelines(rows, 'ALPHA').map((r) => r.id)).toEqual(['b']);
    expect(filterTimelines(rows, ' e ').map((r) => r.id)).toEqual(['a', 'c']);
    expect(filterTimelines(rows, '')).toHaveLength(3);
    expect(filterTimelines(rows, 'nope')).toEqual([]);
  });
  it('sorts by recently edited, name and created', () => {
    expect(sortTimelines(rows, 'edited').map((r) => r.id)).toEqual(['a', 'c', 'b']);
    expect(sortTimelines(rows, 'name').map((r) => r.id)).toEqual(['b', 'a', 'c']);
    expect(sortTimelines(rows, 'created').map((r) => r.id)).toEqual(['c', 'b', 'a']);
  });
  it('does not mutate its input', () => {
    const before = rows.map((r) => r.id);
    sortTimelines(rows, 'name');
    expect(rows.map((r) => r.id)).toEqual(before);
  });
});

describe('R01.1 remembered sort', () => {
  beforeEach(() => window.localStorage.clear());
  it('defaults, round-trips and ignores junk', () => {
    expect(loadTimelineSort()).toBe(DEFAULT_TIMELINE_SORT);
    saveTimelineSort('name');
    expect(loadTimelineSort()).toBe('name');
    window.localStorage.setItem('reelbolt:editor:hub:timeline-sort', 'sideways');
    expect(loadTimelineSort()).toBe(DEFAULT_TIMELINE_SORT);
  });
});

describe('R01.1 trash sentence', () => {
  const now = Date.parse('2026-10-24T12:00:00Z');
  it('says how long ago and how long is left', () => {
    expect(trashSentence('2026-10-21T12:00:00Z', '2026-11-20T12:00:00Z', now)).toBe('Deleted 3 days ago - removed for good in 27 days');
    expect(trashSentence('2026-10-24T08:00:00Z', '2026-11-23T08:00:00Z', now)).toBe('Deleted today - removed for good in 30 days');
    expect(trashSentence('2026-10-23T11:00:00Z', '2026-11-22T11:00:00Z', now)).toBe('Deleted 1 day ago - removed for good in 29 days');
  });
});

import { groupRenderRuns } from '../timeline-list';

describe('groupRenderRuns', () => {
  const row = (id: string, run: string, createdAt: string, isFinal = false) => ({ stepResultId: id, workflowExecutionId: run, createdAt, isFinal });
  const rows = [
    row('a2', 'A', '2026-01-02T00:00:00Z', true),
    row('a1', 'A', '2026-01-01T00:00:00Z'),
    row('b2', 'B', '2026-01-04T00:00:00Z'),
    row('b1', 'B', '2026-01-03T00:00:00Z'),
  ];
  it('keeps only the final render per run, and the latest row of a run with no final', () => {
    const ids = groupRenderRuns(rows, false).flatMap((g) => g.rows.map((r) => r.stepResultId));
    expect(ids).toEqual(['a2', 'b2']);
  });
  it('shows every row grouped under its run when intermediates are on', () => {
    const g = groupRenderRuns(rows, true);
    expect(g.map((x) => x.rows.map((r) => r.stepResultId))).toEqual([['a2', 'a1'], ['b2', 'b1']]);
  });
});
