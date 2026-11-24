/**
 * R05.2 / R05.6: browsing the pool - search, sort, filters - and the smart-bin rule in plain words.
 * Pure functions only; the components feed them the pool, the annotations and the user's choices.
 *
 * `matchesSmartFilter` mirrors `MediaSmartFilter.Matches` (inference/src/ReelBolt.Shared/Editing/MediaSmartFilter.cs):
 * every clause ANDs, keywords must ALL be present (exact, lower-case), name is a case-insensitive
 * substring, a missing annotation counts as no keywords / no label / not favourite / not flagged.
 */

import type { MediaAnnotation, MediaKind, MediaPoolItem, MediaSmartFilter } from './types';

export type PoolSort = 'added' | 'newest' | 'name' | 'duration' | 'size';
export type PoolStateFilter = 'all' | 'starred' | 'flagged' | 'unanalysed';

export const POOL_SORT_LABELS: Record<PoolSort, string> = {
  added: 'Order added',
  newest: 'Newest first',
  name: 'Name (A to Z)',
  duration: 'Longest first',
  size: 'Largest first',
};

export const POOL_STATE_LABELS: Record<PoolStateFilter, string> = {
  all: 'Everything',
  starred: 'Starred',
  flagged: 'Flagged',
  unanalysed: 'Not analysed yet',
};

export interface PoolBrowseOptions {
  query: string;
  sort: PoolSort;
  /** Empty = every kind. */
  kinds: readonly MediaKind[];
  state: PoolStateFilter;
}

export const DEFAULT_BROWSE: PoolBrowseOptions = { query: '', sort: 'added', kinds: [], state: 'all' };

export type AnnotationIndex = ReadonlyMap<string, MediaAnnotation>;

export function indexAnnotations(annotations: readonly MediaAnnotation[] | undefined | null): AnnotationIndex {
  const map = new Map<string, MediaAnnotation>();
  for (const a of annotations ?? []) map.set(a.mediaRef, a);
  return map;
}

/** True when any control differs from the defaults (the "Clear filters" button shows then). */
export function isBrowsing(options: PoolBrowseOptions): boolean {
  return options.query.trim() !== '' || options.kinds.length > 0 || options.state !== 'all';
}

const durationOf = (item: MediaPoolItem): number => item.probe?.durationSec ?? -1;

/**
 * Filters and sorts. `added` keeps the server's order; `newest` reverses it (the pool arrives oldest
 * first); every other sort is stable on ties by name.
 */
export function browsePool(
  items: readonly MediaPoolItem[],
  annotations: AnnotationIndex,
  options: PoolBrowseOptions,
): MediaPoolItem[] {
  const query = options.query.trim().toLowerCase();
  const kinds = new Set(options.kinds);
  const filtered = items.filter((item) => {
    if (kinds.size > 0 && !kinds.has(item.kind)) return false;
    if (query !== '') {
      const annotation = annotations.get(item.mediaRef);
      const haystack = [item.fileName, ...(annotation?.keywords ?? [])].join('\n').toLowerCase();
      if (!haystack.includes(query)) return false;
    }
    const annotation = annotations.get(item.mediaRef);
    if (options.state === 'starred' && !annotation?.favourite) return false;
    if (options.state === 'flagged' && !annotation?.flag) return false;
    if (options.state === 'unanalysed' && item.probe !== null) return false;
    return true;
  });
  const byName = (a: MediaPoolItem, b: MediaPoolItem) => a.fileName.localeCompare(b.fileName, undefined, { sensitivity: 'base', numeric: true });
  switch (options.sort) {
    case 'added':
      return filtered;
    case 'newest':
      return filtered.reverse();
    case 'name':
      return filtered.sort(byName);
    case 'duration':
      return filtered.sort((a, b) => durationOf(b) - durationOf(a) || byName(a, b));
    case 'size':
      return filtered.sort((a, b) => b.sizeBytes - a.sizeBytes || byName(a, b));
  }
}

export function matchesSmartFilter(
  filter: MediaSmartFilter,
  item: Pick<MediaPoolItem, 'kind' | 'fileName'>,
  annotation: MediaAnnotation | null | undefined,
): boolean {
  if (filter.kinds && filter.kinds.length > 0 && !filter.kinds.includes(item.kind)) return false;
  const fragment = filter.nameContains?.trim();
  if (fragment && !item.fileName.toLowerCase().includes(fragment.toLowerCase())) return false;
  if (filter.keywords && filter.keywords.length > 0) {
    const have = annotation?.keywords ?? [];
    if (!filter.keywords.every((k) => have.includes(k.trim().toLowerCase()))) return false;
  }
  if (filter.colorLabel && annotation?.colorLabel !== filter.colorLabel) return false;
  if (filter.favourite !== null && filter.favourite !== undefined && (annotation?.favourite ?? false) !== filter.favourite) return false;
  if (filter.flag !== null && filter.flag !== undefined && (annotation?.flag ?? false) !== filter.flag) return false;
  return true;
}

export function countSmartMatches(
  filter: MediaSmartFilter,
  items: readonly MediaPoolItem[],
  annotations: AnnotationIndex,
): number {
  let n = 0;
  for (const item of items) if (matchesSmartFilter(filter, item, annotations.get(item.mediaRef))) n++;
  return n;
}

export function isEmptySmartRule(filter: MediaSmartFilter): boolean {
  return (
    (filter.keywords ?? []).length === 0 &&
    (filter.kinds ?? []).length === 0 &&
    (filter.colorLabel ?? null) === null &&
    (filter.favourite ?? null) === null &&
    (filter.flag ?? null) === null &&
    (filter.nameContains ?? '').trim().length === 0
  );
}

const KIND_WORD: Record<MediaKind, string> = { video: 'video', audio: 'audio', image: 'pictures' };

/** The rule as one plain sentence, e.g. "Video or audio, starred, tagged beach and sunset, name has \"take\"". */
export function describeRule(filter: MediaSmartFilter): string {
  if (isEmptySmartRule(filter)) return 'Everything in the pool';
  const parts: string[] = [];
  if (filter.kinds && filter.kinds.length > 0) parts.push(filter.kinds.map((k) => KIND_WORD[k]).join(' or '));
  if (filter.favourite === true) parts.push('starred');
  if (filter.favourite === false) parts.push('not starred');
  if (filter.flag === true) parts.push('flagged');
  if (filter.flag === false) parts.push('not flagged');
  if (filter.colorLabel) parts.push(`${filter.colorLabel} colour`);
  if (filter.keywords && filter.keywords.length > 0) parts.push(`tagged ${filter.keywords.join(' and ')}`);
  if (filter.nameContains && filter.nameContains.trim()) parts.push(`name has "${filter.nameContains.trim()}"`);
  const text = parts.join(', ');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** What the API gets: empty clauses dropped, so a saved rule only holds what the user set. */
export function cleanRule(filter: MediaSmartFilter): MediaSmartFilter {
  const out: MediaSmartFilter = {};
  if (filter.keywords && filter.keywords.length > 0) out.keywords = filter.keywords.map((k) => k.trim().toLowerCase()).filter(Boolean);
  if (filter.kinds && filter.kinds.length > 0) out.kinds = [...filter.kinds];
  if (filter.colorLabel) out.colorLabel = filter.colorLabel;
  if (filter.favourite !== null && filter.favourite !== undefined) out.favourite = filter.favourite;
  if (filter.flag !== null && filter.flag !== undefined) out.flag = filter.flag;
  if (filter.nameContains && filter.nameContains.trim()) out.nameContains = filter.nameContains.trim();
  return out;
}

/** The bins a clip may be moved/added to: ordinary bins only, never a smart bin. */
export function moveTargets<T extends { id: string; isSmart?: boolean; smartFilter?: unknown }>(
  bins: readonly T[],
  excludeIds: ReadonlySet<string> = new Set(),
): T[] {
  return bins.filter((b) => !excludeIds.has(b.id) && !(b.isSmart || b.smartFilter));
}
