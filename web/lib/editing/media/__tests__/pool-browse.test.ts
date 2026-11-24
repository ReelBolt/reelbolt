import { describe, expect, it } from 'vitest';
import {
  browsePool, cleanRule, countSmartMatches, DEFAULT_BROWSE, describeRule, indexAnnotations, isBrowsing,
  isEmptySmartRule, matchesSmartFilter, moveTargets,
} from '../pool-browse';
import type { MediaAnnotation, MediaPoolItem } from '../types';

const item = (id: string, over: Partial<MediaPoolItem> = {}): MediaPoolItem => ({
  mediaRef: `file:${id}`, kind: 'video', fileName: `${id}.mp4`, mimeType: 'video/mp4', sizeBytes: 100, category: 'userFiles',
  probe: { durationSec: 10 } as MediaPoolItem['probe'], bins: [], filmstripUrl: '', ...over,
});
const ann = (id: string, over: Partial<MediaAnnotation> = {}): MediaAnnotation => ({
  mediaRef: `file:${id}`, keywords: [], note: null, colorLabel: null, favourite: false, flag: false, updatedAt: '', ...over,
});

const pool = [
  item('beach', { sizeBytes: 500, probe: { durationSec: 30 } as MediaPoolItem['probe'] }),
  item('city', { kind: 'image', sizeBytes: 900, probe: null }),
  item('Take 10', { kind: 'audio', sizeBytes: 50 }),
  item('take 2', { sizeBytes: 70, probe: { durationSec: 5 } as MediaPoolItem['probe'] }),
];
const annotations = indexAnnotations([ann('beach', { favourite: true, keywords: ['sun', 'sea'] }), ann('take 2', { flag: true, colorLabel: 'red' })]);
const ids = (rows: MediaPoolItem[]) => rows.map((r) => r.mediaRef.slice(5));

describe('browsePool', () => {
  it('search matches file name and keywords, case-insensitively', () => {
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, query: 'TAKE', sort: 'name' }))).toEqual(['take 2', 'Take 10'].sort((a, b) => a.localeCompare(b, undefined, { numeric: true })));
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, query: 'sea' }))).toEqual(['beach']);
  });
  it('filters by kind and by state', () => {
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, kinds: ['image', 'audio'], sort: 'name' }))).toEqual(['city', 'Take 10']);
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, state: 'starred' }))).toEqual(['beach']);
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, state: 'flagged' }))).toEqual(['take 2']);
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, state: 'unanalysed' }))).toEqual(['city']);
  });
  it('sorts by name (natural), duration and size; newest reverses the arrival order', () => {
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, sort: 'name' }))).toEqual(['beach', 'city', 'take 2', 'Take 10']);
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, sort: 'duration' }))[0]).toBe('beach');
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, sort: 'size' }))).toEqual(['city', 'beach', 'take 2', 'Take 10']);
    expect(ids(browsePool(pool, annotations, { ...DEFAULT_BROWSE, sort: 'newest' }))).toEqual(['take 2', 'Take 10', 'city', 'beach']);
    expect(ids(browsePool(pool, annotations, DEFAULT_BROWSE))).toEqual(['beach', 'city', 'Take 10', 'take 2']);
  });
  it('does not mutate the input and reports browsing state', () => {
    const copy = [...pool];
    browsePool(pool, annotations, { ...DEFAULT_BROWSE, sort: 'size' });
    expect(pool).toEqual(copy);
    expect(isBrowsing(DEFAULT_BROWSE)).toBe(false);
    expect(isBrowsing({ ...DEFAULT_BROWSE, query: ' x ' })).toBe(true);
    expect(isBrowsing({ ...DEFAULT_BROWSE, sort: 'size' })).toBe(false);
  });
});

describe('smart rules', () => {
  it('ANDs every clause like the server', () => {
    const a = annotations.get('file:beach');
    expect(matchesSmartFilter({ keywords: ['sun', 'sea'], favourite: true }, pool[0], a)).toBe(true);
    expect(matchesSmartFilter({ keywords: ['sun', 'moon'] }, pool[0], a)).toBe(false);
    expect(matchesSmartFilter({ kinds: ['audio'] }, pool[0], a)).toBe(false);
    expect(matchesSmartFilter({ nameContains: 'BEA' }, pool[0], a)).toBe(true);
    expect(matchesSmartFilter({ colorLabel: 'red' }, pool[3], annotations.get('file:take 2'))).toBe(true);
  });
  it('treats a missing annotation as plain', () => {
    expect(matchesSmartFilter({ favourite: false }, pool[1], undefined)).toBe(true);
    expect(matchesSmartFilter({ favourite: true }, pool[1], undefined)).toBe(false);
    expect(matchesSmartFilter({ keywords: ['x'] }, pool[1], undefined)).toBe(false);
  });
  it('counts matches', () => {
    expect(countSmartMatches({ kinds: ['video'] }, pool, annotations)).toBe(2);
    expect(countSmartMatches({ flag: true }, pool, annotations)).toBe(1);
  });
  it('detects an empty rule and cleans empty clauses', () => {
    expect(isEmptySmartRule({})).toBe(true);
    expect(isEmptySmartRule({ keywords: [], kinds: null, nameContains: '  ' })).toBe(true);
    expect(isEmptySmartRule({ flag: false })).toBe(false);
    expect(cleanRule({ keywords: [' Sun '], kinds: [], nameContains: ' x ', flag: false, favourite: null })).toEqual({ keywords: ['sun'], nameContains: 'x', flag: false });
  });
  it('describes the rule in plain words', () => {
    expect(describeRule({})).toBe('Everything in the pool');
    expect(describeRule({ kinds: ['video', 'audio'], favourite: true, keywords: ['sun', 'sea'], nameContains: 'take' })).toBe('Video or audio, starred, tagged sun and sea, name has "take"');
  });
  it('never offers a smart bin (or an excluded bin) as a target', () => {
    const bins = [{ id: 'a' }, { id: 'b', isSmart: true }, { id: 'c', smartFilter: {} }, { id: 'd' }];
    expect(moveTargets(bins, new Set(['d'])).map((b) => b.id)).toEqual(['a']);
  });
});
