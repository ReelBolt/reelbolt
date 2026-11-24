/** @vitest-environment node */
/** R12.1 hand-off counts and R12.2 relink: one undo step, the hole stays the hole, the marker goes. */
import { describe, expect, it } from 'vitest';
import { applyAll } from '../../engine';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';
import type { MediaPoolItem } from '../../media/types';
import { handOffCounts, handOffLines, mediaSentence } from '../hand-off';
import { buildRelinkOps, offlineMarkers, offlineMessage } from '../relink';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`;
const RATE = { num: 30, den: 1 };
const item = (durationSec: number): MediaPoolItem => ({
  mediaRef: 'file:00000000-0000-4000-8000-000000000777', kind: 'video', fileName: 'shot.mp4', mimeType: 'video/mp4', sizeBytes: 1, category: 'userFiles',
  probe: { durationSec, fpsNum: 30, fpsDen: 1, width: 1920, height: 1080, videoCodec: 'h264', audioCodec: null, container: 'mp4', audioChannels: null, audioSampleRateHz: null, bitrateBps: null } as MediaPoolItem['probe'],
  bins: [], filmstripUrl: '',
});
const withHole = (): EditTimeline => ({
  ...doc([track(TRACKS.v1, 'video', [media('a', 0, 60), media('b', 150, 210)]), track(TRACKS.a1, 'audio', [])]),
  markers: [{ id: 'm1', frame: 60, name: 'Offline: clip.mov', color: 'red', note: 'Media missing.', durationFrames: 90 }, { id: 'm2', frame: 5, name: 'Intro' }],
});

describe('hand-off', () => {
  it('counts what each format would carry', () => {
    const d = withHole();
    const c = handOffCounts(d);
    expect(c).toMatchObject({ videoClips: 2, audioClips: 0, captions: 0, markers: 2 });
    expect(handOffLines('edl', c)).toEqual(['2 video cuts']);
    expect(handOffLines('srt', c)).toEqual([]);
    expect(handOffLines('otio', c)).toEqual(['2 video clips', '2 markers']);
  });
  it('says what media the file names', () => {
    expect(mediaSentence('none')).toMatch(/names no media/);
    expect(mediaSentence('reelbolt-url')).toMatch(/link/);
  });
});

describe('offline markers and relink', () => {
  it('lists only the Offline markers, in order, in plain words', () => {
    const list = offlineMarkers(withHole());
    expect(list).toEqual([{ markerId: 'm1', name: 'clip.mov', frame: 60, durationFrames: 90 }]);
    expect(offlineMessage(list)).toMatch(/^1 clip could not be found in this project: clip\.mov/);
  });
  it('places the file in the hole, cut to its length, and removes the marker, as one batch', () => {
    const d = withHole();
    const r = buildRelinkOps(d, { marker: d.markers[0], item: item(10), trackId: TRACKS.v1, rate: RATE, newId });
    if (!r.ok) throw new Error(r.reason);
    const next = applyAll(d, r.ops);
    const placed = next.tracks[0].items.find((i) => i.id === r.place.clipId)!;
    expect([placed.recordIn, placed.recordOut]).toEqual([60, 150]);
    expect(next.tracks[0].items.map((i) => i.id)).toContain('b');
    expect(next.markers.map((m) => m.id)).toEqual(['m2']);
  });
  it('a shorter file leaves the rest of the hole alone', () => {
    const d = withHole();
    const r = buildRelinkOps(d, { marker: d.markers[0], item: item(1), trackId: TRACKS.v1, rate: RATE, newId });
    if (!r.ok) throw new Error(r.reason);
    const placed = applyAll(d, r.ops).tracks[0].items.find((i) => i.id === r.place.clipId)!;
    expect(placed.recordOut - placed.recordIn).toBe(30);
  });
});
