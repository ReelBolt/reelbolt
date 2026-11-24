/** @vitest-environment node */
/**
 * T-012 "Place over": the first video track above every picture at that time, a new track on top when none
 * above is free, the corner preset, one batch; and the drop that covers a lower picture.
 */
import { describe, expect, it } from 'vitest';
import { applyAll } from '../../engine';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';
import { buildPlaceOverOps, coversLowerPicture, pipValues, placeOverTrack, PIP_MARGIN, PIP_SCALE } from '../place-over';
import type { MediaPoolItem } from '../types';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`;
const RATE = { num: 30, den: 1 };
const CANVAS = { width: 1920, height: 1080 };
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
const over = (d: EditTimeline, at: number, extra: Partial<Parameters<typeof buildPlaceOverOps>[1]> = {}) => {
  const r = buildPlaceOverOps(d, { item: item(), at, rate: RATE, newId, canvas: CANVAS, aspectPolicy: 'fit', ...extra });
  if (!r.ok) throw new Error(r.reason);
  return r;
};
const clipIn = (d: EditTimeline, id: string): MediaClip => d.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;

describe('placeOverTrack', () => {
  it('is the first free track above the topmost picture, skipping a busy or locked one', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 120)]), track('v2', 'video', [media('b', 0, 120)]), track('v3', 'video', [], { locked: true }), track('v4', 'video', [])]);
    expect(placeOverTrack(d, 10, 70).track?.id).toBe('v4');
    expect(placeOverTrack(d, 10, 70).occupied).toBe(true);
  });

  it('reports nothing occupied on an empty stretch, and no track when everything above is busy', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 50)]), track('v2', 'video', [media('b', 0, 120)])]);
    expect(placeOverTrack(d, 60, 100)).toEqual({ track: null, occupied: true });
    expect(placeOverTrack(d, 130, 190).occupied).toBe(false);
  });
});

describe('buildPlaceOverOps', () => {
  it('puts the clip on the free track above V1 at the playhead with the corner preset, in one batch', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 120)]), track('v2', 'video', []), track('v3', 'video', [])]);
    const r = over(d, 30);
    expect(r.addedTrack).toBe(false);
    expect(r.pip).toBe(true);
    expect(r.trackId).toBe('v2');
    const next = applyAll(d, r.ops);
    const placed = clipIn(next, r.clipId);
    expect([placed.recordIn, placed.recordOut]).toEqual([30, 90]);
    expect(next.tracks[1].items.map((i) => i.id)).toEqual([r.clipId]);
    expect(placed.transform?.scale?.value).toBeCloseTo(PIP_SCALE, 3);
    // Bottom-right, inside the 5% margin: the picture's right and bottom edges sit PIP_MARGIN from the canvas edge.
    const px = placed.transform?.positionX?.value ?? 0;
    const py = placed.transform?.positionY?.value ?? 0;
    expect(px).toBeGreaterThan(0);
    expect(py).toBeGreaterThan(0);
    expect(px + PIP_SCALE / 2 + PIP_MARGIN).toBeCloseTo(0.5, 2);
    expect(r.label).toContain('Place over');
  });

  it('adds a video track on top when none above is free', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 120)]), track('v2', 'video', [media('b', 20, 100)])]);
    const r = over(d, 30);
    expect(r.addedTrack).toBe(true);
    const next = applyAll(d, r.ops);
    expect(next.tracks.filter((t) => t.kind === 'video')).toHaveLength(3);
    const top = next.tracks.filter((t) => t.kind === 'video')[2];
    expect(top.items.map((i) => i.id)).toEqual([r.clipId]);
    expect(top.id).toBe(r.trackId);
    expect(clipIn(next, r.clipId).transform?.scale).toBeDefined();
  });

  it('does not use a free track BELOW the topmost picture', () => {
    const d = doc([track('v1', 'video', []), track('v2', 'video', [media('b', 0, 120)])]);
    const r = over(d, 10);
    expect(r.addedTrack).toBe(true);
    expect(r.trackId).not.toBe('v1');
  });

  it('with no picture at that time it places full-frame (no preset) on a free track', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 20)]), track('v2', 'video', [])]);
    const r = over(d, 200);
    expect(r.pip).toBe(false);
    const placed = clipIn(applyAll(d, r.ops), r.clipId);
    expect(placed.transform).toBeUndefined();
  });

  it('refuses audio', () => {
    const r = buildPlaceOverOps(doc([track('v1', 'video', [media('a', 0, 20)])]), { item: item({ kind: 'audio' }), at: 0, rate: RATE, newId, canvas: CANVAS, aspectPolicy: 'fit' });
    expect(r.ok).toBe(false);
  });

  it('is one undo step through the store', async () => {
    const { EditorStore } = await import('../../store');
    const d = doc([track(TRACKS.v1, 'video', [media('a', 0, 120)]), track(TRACKS.v2, 'video', [])]);
    const store = new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    const r = over(d, 10);
    store.dispatchBatch(r.ops, r.label);
    expect(clipIn(store.getState().doc, r.clipId)).toBeDefined();
    store.undo();
    expect(store.getState().doc.tracks.flatMap((t) => t.items).some((i) => i.id === r.clipId)).toBe(false);
  });
});

describe('coversLowerPicture / pipValues', () => {
  it('is true for a clip on an upper track over a picture, false when alone or on the bottom track', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 60)]), track('v2', 'video', [media('b', 30, 90), media('c', 100, 120)])]);
    expect(coversLowerPicture(d, 'b')).toBe(true);
    expect(coversLowerPicture(d, 'c')).toBe(false);
    expect(coversLowerPicture(d, 'a')).toBe(false);
  });

  it('pipValues is the bottom-right corner at 0.33 for a canvas-shaped source', () => {
    const v = pipValues(CANVAS, { width: 1920, height: 1080 }, 'fit');
    expect(v.scale).toBeCloseTo(0.33, 3);
    expect(v.positionX).toBeCloseTo(0.5 - 0.165 - 0.05, 3);
    expect(v.positionY).toBeCloseTo(0.5 - 0.165 - 0.05, 3);
  });
});
