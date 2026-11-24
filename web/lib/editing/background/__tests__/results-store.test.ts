// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { assetRef, doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { anchorForClips } from '../anchor';
import { BACKGROUND_CAP, backgroundKey, getResultsStore, resetResultsStores, ResultsStore, type BackgroundEntry } from '../results-store';

const d = doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0), media('o', 60, 120, 0, { assetRef: assetRef(2) })])]);
const entry = (id: string, over: Partial<BackgroundEntry> = {}): BackgroundEntry => ({
  id, tool: 'objectEffect', requestId: id, anchor: anchorForClips(d, ['x'], { startSec: 0, endSec: null }), intent: { prompt: 'face' }, state: 'running', createdAt: '2026-01-01T00:00:00Z', ...over,
});

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('results store', () => {
  it('persists to the per-user, per-timeline key and survives a reload', () => {
    const a = getResultsStore('tl1');
    a.launch(entry('r1'));
    const key = Object.keys(window.localStorage).find((k) => k.startsWith('reelbolt.editor.bg.'));
    expect(key).toMatch(/^reelbolt\.editor\.bg\..+\.tl1$/);
    resetResultsStores(); // a reload forgets every in-memory store
    expect(getResultsStore('tl1').getEntries().map((e) => e.id)).toEqual(['r1']);
    expect(getResultsStore('tl2').getEntries()).toEqual([]);
  });
  it('update, resolve and subscribe', () => {
    const s = getResultsStore('tl1');
    const seen = vi.fn();
    s.subscribe(seen);
    s.launch(entry('r1'));
    s.update('r1', { state: 'needsReview', review: { applicable: ['x'], reasons: [], lostCount: 0 } });
    expect(s.getEntries()[0].state).toBe('needsReview');
    s.resolve('r1', 'applied');
    expect(s.getEntries()[0].state).toBe('applied');
    expect(s.getEntries()[0].review).toBeUndefined();
    expect(seen).toHaveBeenCalledTimes(3);
  });
  it('launching the same id twice keeps one entry', () => {
    const s = getResultsStore('tl1');
    s.launch(entry('r1'));
    s.launch(entry('r1'));
    expect(s.getEntries()).toHaveLength(1);
  });
  it('caps at 50, dropping finished entries before open ones', () => {
    const s = new ResultsStore('tl', backgroundKey('u', 'tl'), null);
    s.launch(entry('keep', { state: 'running' }));
    for (let i = 0; i < BACKGROUND_CAP + 5; i++) s.launch(entry(`done-${i}`, { state: 'applied' }));
    expect(s.getEntries()).toHaveLength(BACKGROUND_CAP);
    expect(s.getEntries().some((e) => e.id === 'keep')).toBe(true);
    expect(s.getEntries().some((e) => e.id === 'done-0')).toBe(false);
  });
  it('ignores a corrupt saved value', () => {
    window.localStorage.setItem(backgroundKey('u', 'tl'), '{nope');
    expect(new ResultsStore('tl', backgroundKey('u', 'tl'), window.localStorage).getEntries()).toEqual([]);
  });
  it('busyClipIds: targets of running entries, rebased (a split piece is busy too), not finished ones', () => {
    const s = getResultsStore('tl1');
    s.launch(entry('r1'));
    s.launch(entry('r2', { state: 'applied' }));
    const split = doc([track(TRACKS.v1, 'video', [media('x', 0, 30, 0), media('y', 30, 60, 30)])]);
    expect([...s.busyClipIds(split)].sort()).toEqual(['x', 'y']);
    expect([...s.busyClipIds(doc([]))]).toEqual([]);
  });
});
