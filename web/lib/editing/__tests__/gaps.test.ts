import { describe, expect, it } from 'vitest';
import { apply } from '../engine';
import { gapInfoFor } from '../gaps';
import type { EditTimeline, Item } from '../types';

const clip = (id: string, recordIn: number, recordOut: number): Item =>
  ({ type: 'mediaClip', id, name: id, recordIn, recordOut, assetRef: { kind: 'projectFile', projectFileId: 'f', stepResultId: null }, sourceIn: 0, playbackRate: 1, enabled: true }) as unknown as Item;

const doc = (items: Item[]): EditTimeline =>
  ({
    schemaVersion: 2,
    id: 'tl',
    projectId: 'p',
    name: 't',
    settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 } },
    tracks: [{ id: 'v1', kind: 'video', name: 'V1', color: null, height: 64, locked: false, enabled: true, mute: false, solo: false, busId: null, items }],
    markers: [],
  }) as unknown as EditTimeline;

describe('gapInfoFor', () => {
  it('finds the gap before the selected clip and counts the interior gaps of its track', () => {
    const d = doc([clip('a', 0, 60), clip('b', 90, 150), clip('c', 180, 240)]);
    expect(gapInfoFor(d, ['b'])).toEqual({ trackId: 'v1', trackName: 'V1', beforeAtFrame: 60, beforeLength: 30, interiorGaps: 2 });
    expect(gapInfoFor(d, ['a'])).toMatchObject({ beforeAtFrame: null, beforeLength: 0, interiorGaps: 2 });
  });

  it('counts a leading gap as the gap before a first clip but not as an interior gap', () => {
    const d = doc([clip('a', 30, 60), clip('b', 60, 90)]);
    expect(gapInfoFor(d, ['a'])).toMatchObject({ beforeAtFrame: 0, beforeLength: 30, interiorGaps: 0 });
  });

  it('returns null for an empty or unknown selection', () => {
    expect(gapInfoFor(doc([clip('a', 0, 10)]), [])).toBeNull();
    expect(gapInfoFor(doc([clip('a', 0, 10)]), ['zzz'])).toBeNull();
  });

  it('the frames it names are exactly what deleteGap and closeGaps accept', () => {
    const d = doc([clip('a', 0, 60), clip('b', 90, 150), clip('c', 180, 240)]);
    const info = gapInfoFor(d, ['b'])!;
    const one = apply(d, { type: 'deleteGap', trackId: info.trackId, atFrame: info.beforeAtFrame! }).doc;
    expect(one.tracks[0].items.map((i) => [i.id, i.recordIn])).toEqual([['a', 0], ['b', 60], ['c', 150]]);
    const all = apply(d, { type: 'closeGaps', trackId: info.trackId }).doc;
    expect(all.tracks[0].items.map((i) => [i.id, i.recordIn])).toEqual([['a', 0], ['b', 60], ['c', 120]]);
  });
});
