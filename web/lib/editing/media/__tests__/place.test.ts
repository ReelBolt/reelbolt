/** @vitest-environment node */
/**
 * R03.6 buildPlaceOps: each mode lands where it says; the op batch is one undo step; no mode but
 * overwrite ever creates an overlap (and overwrite never leaves one either).
 */
import { describe, expect, it } from 'vitest';
import { applyAll } from '../../engine';
import { EditorStore } from '../../store';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditTimeline, Track } from '../../types';
import { buildPlaceOps, landedAtLine, trackDisplayName, type PlaceMode } from '../place';
import type { MediaPoolItem } from '../types';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`;
const RATE = { num: 30, den: 1 };
const item = (over: Partial<MediaPoolItem> = {}): MediaPoolItem => ({
  mediaRef: 'file:00000000-0000-4000-8000-000000000777',
  kind: 'video',
  fileName: 'shot.mp4',
  mimeType: 'video/mp4',
  sizeBytes: 1,
  category: 'userFiles',
  probe: { durationSec: 2, fpsNum: 30, fpsDen: 1, width: 1920, height: 1080, videoCodec: 'h264', audioCodec: null, container: 'mp4', audioChannels: null, audioSampleRateHz: null, bitrateBps: 1 },
  bins: [],
  filmstripUrl: '',
  ...over,
});
const base = (): EditTimeline => doc([track(TRACKS.v1, 'video', [media('a', 0, 60), media('b', 90, 150)]), track(TRACKS.v2, 'video', []), track(TRACKS.a1, 'audio', [])]);
const run = (d: EditTimeline, ops: Parameters<typeof applyAll>[1]) => applyAll(d, ops);
const overlaps = (t: Track) => t.items.some((x, i) => t.items.some((y, j) => i !== j && x.recordIn < y.recordOut && y.recordIn < x.recordOut));
const place = (d: EditTimeline, mode: PlaceMode, at: number, extra: Partial<Parameters<typeof buildPlaceOps>[1]> = {}) => {
  const r = buildPlaceOps(d, { item: item(), mode, at, trackId: TRACKS.v1, rate: RATE, newId, ...extra });
  if (!r.ok) throw new Error(r.reason);
  return r;
};

describe('buildPlaceOps', () => {
  it('append lands after the last clip', () => {
    const r = place(base(), 'append', 5);
    const next = run(base(), r.ops);
    const added = next.tracks[0].items.find((i) => i.id === r.clipId)!;
    expect([added.recordIn, added.recordOut]).toEqual([150, 210]);
    expect(r.landedAt).toEqual({ trackName: 'track-v1', frame: 150, tc: '00:00:05:00' });
  });

  it('insert snaps to the nearest cut and ripples what follows', () => {
    const r = place(base(), 'insert', 85);
    const next = run(base(), r.ops);
    expect(r.landedAt.frame).toBe(90);
    const t = next.tracks[0];
    expect(t.items.map((i) => [i.id, i.recordIn])).toEqual([['a', 0], [r.clipId, 90], ['b', 150]]);
    expect(overlaps(t)).toBe(false);
  });

  it('overwrite lands exactly at the playhead and cuts away what is under it', () => {
    const r = place(base(), 'overwrite', 40);
    const next = run(base(), r.ops);
    const t = next.tracks[0];
    const placed = t.items.find((i) => i.id === r.clipId)!;
    expect([placed.recordIn, placed.recordOut]).toEqual([40, 100]);
    expect(t.items.find((i) => i.id === 'a')).toMatchObject({ recordIn: 0, recordOut: 40 });
    expect(t.items.find((i) => i.recordIn === 100)).toMatchObject({ recordOut: 150, sourceIn: 10 });
    expect(overlaps(t)).toBe(false);
  });

  it('overwrite inside one clip splits it around the new clip', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('a', 0, 300)])]);
    const r = place(d, 'overwrite', 100);
    const t = run(d, r.ops).tracks[0];
    expect(t.items.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 100], [100, 160], [160, 300]]);
    expect(overlaps(t)).toBe(false);
  });

  it('place on top uses the topmost free track at the frame, else adds a track', () => {
    const free = place(base(), 'placeOnTop', 10);
    expect(free.landedAt.trackName).toBe('track-v2');
    const busy = doc([track(TRACKS.v1, 'video', [media('a', 0, 300)])]);
    const added = place(busy, 'placeOnTop', 10);
    expect(added.ops[0].type).toBe('addTrack');
    expect(run(busy, added.ops).tracks.filter((t) => t.kind === 'video')).toHaveLength(2);
  });

  it('three-point edit: the In/Out marks set the source range, the playhead sets where', () => {
    const r = place(base(), 'overwrite', 200, { marks: { inFrame: 30, outFrame: 45 } });
    const clip = run(base(), r.ops).tracks[0].items.find((i) => i.id === r.clipId)!;
    expect(clip).toMatchObject({ recordIn: 200, recordOut: 215, sourceIn: 30 });
  });

  it('a video with sound brings a linked companion on the paired audio track', () => {
    const withAudio = item({ probe: { ...item().probe!, audioCodec: 'aac', audioChannels: 2 } });
    const r = place(base(), 'overwrite', 200, { item: withAudio });
    const next = run(base(), r.ops);
    const audio = next.tracks.filter((t) => t.kind === 'audio').flatMap((t) => t.items);
    expect(audio).toHaveLength(1);
    expect(audio[0]).toMatchObject({ recordIn: 200, recordOut: 260 });
    const pic = next.tracks[0].items.find((i) => i.id === r.clipId) as { linkId?: string };
    expect((audio[0] as { linkId?: string }).linkId).toBe(pic.linkId);
  });

  it('with no usable track says so in plain words', () => {
    const d = doc([track(TRACKS.a1, 'audio', [])]);
    expect(buildPlaceOps(d, { item: item(), mode: 'insert', at: 0, rate: RATE, newId })).toMatchObject({ ok: false, reason: expect.stringMatching(/video track/) });
  });

  it('is one undo step', () => {
    const d = base();
    const store = new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    const r = place(d, 'overwrite', 40);
    store.dispatchBatch(r.ops, r.label);
    expect(store.undo()).toBe(true);
    expect(store.getState().doc).toEqual(d);
  });

  it('property: insert, append and place on top never overlap on any track', () => {
    for (const mode of ['insert', 'append', 'placeOnTop'] as const) {
      for (let at = 0; at <= 200; at += 7) {
        const d = base();
        const next = run(d, place(d, mode, at).ops);
        expect(next.tracks.some(overlaps), `${mode} at ${at}`).toBe(false);
      }
    }
  });

  it('property: overwrite leaves no overlap either, at any frame', () => {
    for (let at = 0; at <= 200; at += 3) {
      const d = base();
      const next = run(d, place(d, 'overwrite', at).ops);
      expect(next.tracks.some(overlaps), `at ${at}`).toBe(false);
    }
  });
});

describe('labels', () => {
  it('names tracks in plain language', () => {
    expect(trackDisplayName({ name: 'V1', kind: 'video' })).toBe('Video 1');
    expect(trackDisplayName({ name: 'A2', kind: 'audio' })).toBe('Audio 2');
    expect(trackDisplayName({ name: 'Camera audio', kind: 'audio' })).toBe('Camera audio');
    expect(landedAtLine({ trackName: 'Video 1', frame: 0, tc: '00:00:12:04' })).toBe('Placed on Video 1 at 00:00:12:04');
  });
});

