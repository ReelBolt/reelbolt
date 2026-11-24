import type { EditTimelineSummary } from '@/lib/editing/sync';

/** R01.1: how the hub's timeline list is ordered. */
export type TimelineSort = 'edited' | 'name' | 'created';

export const TIMELINE_SORT_OPTIONS: { value: TimelineSort }[] = [
  { value: 'edited' },
  { value: 'name' },
  { value: 'created' },
];

export const DEFAULT_TIMELINE_SORT: TimelineSort = 'edited';

/** The browser-local key the chosen order is remembered under. */
export const TIMELINE_SORT_STORAGE_KEY = 'reelbolt:editor:hub:timeline-sort';

export function isTimelineSort(value: unknown): value is TimelineSort {
  return value === 'edited' || value === 'name' || value === 'created';
}

/** The remembered order, or the default when nothing (valid) is stored or storage is unavailable. */
export function loadTimelineSort(): TimelineSort {
  if (typeof window === 'undefined') return DEFAULT_TIMELINE_SORT;
  try {
    const stored = window.localStorage.getItem(TIMELINE_SORT_STORAGE_KEY);
    return isTimelineSort(stored) ? stored : DEFAULT_TIMELINE_SORT;
  } catch {
    return DEFAULT_TIMELINE_SORT;
  }
}

export function saveTimelineSort(sort: TimelineSort): void {
  try {
    window.localStorage.setItem(TIMELINE_SORT_STORAGE_KEY, sort);
  } catch {
    // Private mode or a full store: the order still applies for this visit.
  }
}

/** Name contains the search text, case-insensitive; an empty search keeps everything. */
export function filterTimelines<T extends Pick<EditTimelineSummary, 'name'>>(timelines: readonly T[], query: string): T[] {
  const needle = query.trim().toLowerCase();
  if (needle === '') return [...timelines];
  return timelines.filter((t) => t.name.toLowerCase().includes(needle));
}

/** A new array in the chosen order; recent first for the date orders, A to Z for names. */
export function sortTimelines<T extends Pick<EditTimelineSummary, 'name' | 'updatedAt' | 'createdAt' | 'id'>>(timelines: readonly T[], sort: TimelineSort): T[] {
  const byId = (a: T, b: T) => a.id.localeCompare(b.id);
  const copy = [...timelines];
  switch (sort) {
    case 'name':
      return copy.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true }) || byId(a, b));
    case 'created':
      return copy.sort((a, b) => b.createdAt.localeCompare(a.createdAt) || byId(a, b));
    default:
      return copy.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt) || byId(a, b));
  }
}

const DAY_MS = 86_400_000;

const days = (n: number): string => `${n} ${n === 1 ? 'day' : 'days'}`;

/** "Deleted 3 days ago - removed for good in 27 days" for a row of the Recently deleted list. */
export function trashSentence(deletedAt: string, purgesAt: string, now: number = Date.now()): string {
  const deleted = Math.max(0, Math.floor((now - Date.parse(deletedAt)) / DAY_MS));
  const left = Math.max(0, Math.ceil((Date.parse(purgesAt) - now) / DAY_MS));
  const when = deleted === 0 ? 'Deleted today' : `Deleted ${days(deleted)} ago`;
  return `${when} - removed for good in ${days(left)}`;
}

/** The slice of a workflow render the hub list and the new-timeline select need. */
export interface RenderRow {
  stepResultId: string;
  workflowExecutionId: string;
  createdAt: string;
  isFinal?: boolean;
}

export interface RenderRun<T extends RenderRow> {
  runId: string;
  rows: T[];
}

/**
 * Renders grouped under their run (runs in first-seen order, rows newest first). With intermediates hidden each run keeps
 * its final render; a run without one (not Passed, or its final render has no timeline) keeps its latest row so it is never hidden.
 */
export function groupRenderRuns<T extends RenderRow>(rows: T[], showIntermediate: boolean): RenderRun<T>[] {
  const runs = new Map<string, T[]>();
  for (const r of rows) {
    const list = runs.get(r.workflowExecutionId);
    if (list) list.push(r);
    else runs.set(r.workflowExecutionId, [r]);
  }
  const newestFirst = (a: T, b: T) => Date.parse(b.createdAt) - Date.parse(a.createdAt);
  return [...runs].map(([runId, list]) => {
    const sorted = [...list].sort(newestFirst);
    if (showIntermediate) return { runId, rows: sorted };
    const final = sorted.find((r) => r.isFinal);
    return { runId, rows: [final ?? sorted[0]] };
  });
}
