/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { createEditorStore } from '../../store';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { buildPlaceOps } from '../../media/place';
import { compilePoolPlacement } from '../../media/pool-insert';
import type { MediaPoolItem, MediaProbe } from '../../media/types';
import type { MediaClip } from '../../types';
import { autoProfileOf, wasAutoApplied, withDetectedProfile } from '../auto-profile';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n + 5000).padStart(12, '0')}`;
const PROBE: MediaProbe = { durationSec: 2, fpsNum: 30, fpsDen: 1, width: 1920, height: 1080, videoCodec: 'h264', audioCodec: null, container: 'mp4', audioChannels: null, audioSampleRateHz: null, bitrateBps: 1 };
const item = (probe: Partial<MediaProbe>, kind: MediaPoolItem['kind'] = 'video'): MediaPoolItem => ({
  mediaRef: 'file:00000000-0000-4000-8000-000000000777',
  kind,
  fileName: 'shot.mov',
  mimeType: 'video/quicktime',
  sizeBytes: 1,
  category: 'userFiles',
  probe: { ...PROBE, ...probe },
  bins: [],
  filmstripUrl: '',
});
const high = { cameraProfileHint: { profile: 'appleLog', confidence: 'high' as const, reason: 'transfer' } };
const base = () => doc([track(TRACKS.v1, 'video', [media('existing', 0, 60, 0, { inputProfile: 'sonySLog3' })]), track(TRACKS.a1, 'audio', [])]);
const place = (it: MediaPoolItem) => {
  const r = buildPlaceOps(base(), { item: it, mode: 'append', at: 0, trackId: TRACKS.v1, rate: { num: 30, den: 1 }, newId });
  if (!r.ok) throw new Error(r.reason);
  return r;
};
const lastOp = (ops: { type: string }[]) => ops[ops.length - 1] as { type: string; key?: string; value?: unknown; itemId?: string };

describe('auto-apply of a detected camera profile on add', () => {
  it('a high hint appends the conversion to the SAME op batch', () => {
    const r = place(item(high));
    expect(lastOp(r.ops)).toMatchObject({ type: 'setProperty', itemId: r.clipId, key: 'inputProfile', value: 'appleLog' });
    expect(wasAutoApplied(r.clipId)).toBe(true);
  });

  it('a medium hint never applies', () => {
    const r = place(item({ cameraProfileHint: { profile: 'appleLog', confidence: 'medium', reason: 'x' } }));
    expect(r.ops.some((o) => o.type === 'setProperty')).toBe(false);
    expect(wasAutoApplied(r.clipId)).toBe(false);
  });

  it('HDR footage, a null profile, images and audio never apply', () => {
    expect(autoProfileOf({ ...PROBE, ...high, hdr: 'hlg' })).toBeNull();
    expect(autoProfileOf({ ...PROBE, cameraProfileHint: { profile: null, confidence: 'high', reason: 'x' } })).toBeNull();
    expect(autoProfileOf({ ...PROBE, ...high }, 'image')).toBeNull();
    expect(autoProfileOf({ ...PROBE, ...high }, 'audio')).toBeNull();
    expect(withDetectedProfile([], null, 'c')).toEqual([]);
  });

  it('add + conversion is one undo step, and a clip that already has a profile is untouched', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: base(), revision: 1 });
    const r = place(item(high));
    store.dispatchBatch(r.ops, r.label);
    const clips = () => store.getState().doc.tracks.flatMap((t) => t.items).filter((i): i is MediaClip => i.type === 'mediaClip');
    expect(clips().find((c) => c.id === r.clipId)?.inputProfile).toBe('appleLog');
    expect(clips().find((c) => c.id === 'existing')?.inputProfile).toBe('sonySLog3');
    expect(store.undo()).toBe(true);
    expect(clips().map((c) => c.id)).toEqual(['existing']);
    expect(store.canUndo()).toBe(false);
  });

  it('the Cut page placement (compilePoolPlacement) applies it in the same batch too', () => {
    const d = base();
    const p = compilePoolPlacement(d, { item: item(high), track: d.tracks[0], where: 'end', playheadFrame: 0, rate: { num: 30, den: 1 }, newId });
    expect(lastOp(p!.ops)).toMatchObject({ type: 'setProperty', itemId: p!.clipId, key: 'inputProfile', value: 'appleLog' });
  });
});
