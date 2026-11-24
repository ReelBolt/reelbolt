/** @vitest-environment node */
/**
 * Pool inserts (T-022; audit top-10 #5 and #7, ui1 D15, PA-12).
 *
 *   1. A probed source lands at its TRUE length: 12 s at 30 fps is 360 frames, not the 150-frame
 *      placeholder every source got while `clipFromPool` ignored the probe.
 *   2. The source viewer's In/Out marks set the clip's `sourceIn` and length.
 *   3. An unprobed source keeps the placeholder length and says so (`lengthKnown: false`).
 *   4. A video with an audio stream brings its sound: the companion clip on a paired audio track,
 *      in the same op batch, so ONE undo removes picture and sound together.
 *   5. "Extract audio" does the same for a clip already on the timeline.
 *   6. Every id minted is a Guid (the server binds item/track ids as `Guid`).
 */

import { describe, expect, it } from 'vitest';

import { EditorStore } from '../../store';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip, Track } from '../../types';
import { isGuid, newGuid } from '../../ids';
import {
  compileExtractAudio,
  compilePoolPlacement,
  hasAudioStream,
  PAIRED_AUDIO_TRACK_NAME,
  poolClipSpan,
} from '../pool-insert';
import type { MediaPoolItem, MediaProbe } from '../types';

const RATE_30 = { num: 30, den: 1 };
const FILE_ID = '00000000-0000-4000-8000-000000000777';

const probe = (overrides: Partial<MediaProbe> = {}): MediaProbe => ({
  durationSec: 12,
  fpsNum: 30,
  fpsDen: 1,
  width: 1920,
  height: 1080,
  videoCodec: 'h264',
  audioCodec: 'aac',
  container: 'mov,mp4,m4a,3gp,3g2,mj2',
  audioChannels: 2,
  audioSampleRateHz: 48000,
  bitrateBps: 8_000_000,
  ...overrides,
});

const poolItem = (overrides: Partial<MediaPoolItem> = {}): MediaPoolItem => ({
  mediaRef: `file:${FILE_ID}`,
  kind: 'video',
  fileName: 'interview.mp4',
  mimeType: 'video/mp4',
  sizeBytes: 1,
  category: 'userFiles',
  probe: probe(),
  bins: [],
  filmstripUrl: '',
  ...overrides,
});

const store = (d: EditTimeline) => new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });

const findItem = (d: EditTimeline, id: string): { track: Track; item: MediaClip } | null => {
  for (const t of d.tracks) {
    const item = t.items.find((i) => i.id === id);
    if (item) return { track: t, item: item as MediaClip };
  }
  return null;
};

describe('poolClipSpan', () => {
  it('uses the probe duration: a 12 s source at 30 fps is 360 frames', () => {
    expect(poolClipSpan(poolItem(), RATE_30)).toEqual({ sourceIn: 0, durationFrames: 360, lengthKnown: true, fromMarks: false });
  });

  it('converts the duration to the TIMELINE rate (12 s on a 25 fps timeline is 300 frames)', () => {
    expect(poolClipSpan(poolItem(), { num: 25, den: 1 }).durationFrames).toBe(300);
  });

  it('honours In/Out marks, taken in source frames at the probe rate', () => {
    // Source at 60 fps: In 120 (2 s), Out 300 (5 s) → timeline (30 fps) sourceIn 60, 90 frames.
    const item = poolItem({ probe: probe({ fpsNum: 60, fpsDen: 1 }) });
    expect(poolClipSpan(item, RATE_30, { inFrame: 120, outFrame: 300 })).toEqual({ sourceIn: 60, durationFrames: 90, lengthKnown: true, fromMarks: true });
  });

  it('an In mark alone runs to the end of the source', () => {
    expect(poolClipSpan(poolItem(), RATE_30, { inFrame: 60, outFrame: null })).toMatchObject({ sourceIn: 60, durationFrames: 300 });
  });

  it('falls back to 5 s and says the length is unknown when the source was never probed', () => {
    expect(poolClipSpan(poolItem({ probe: null }), RATE_30)).toEqual({ sourceIn: 0, durationFrames: 150, lengthKnown: false, fromMarks: false });
  });

  it('a still is 5 s by design, not an unknown length', () => {
    expect(poolClipSpan(poolItem({ kind: 'image', probe: null }), RATE_30)).toMatchObject({ durationFrames: 150, lengthKnown: true });
  });
});

describe('hasAudioStream', () => {
  it('reads the probe', () => {
    expect(hasAudioStream(poolItem())).toBe(true);
    expect(hasAudioStream(poolItem({ probe: probe({ audioCodec: null, audioChannels: null }) }))).toBe(false);
    expect(hasAudioStream(poolItem({ probe: probe({ audioCodec: null, audioChannels: 0 }) }))).toBe(false);
    expect(hasAudioStream(poolItem({ probe: null }))).toBeNull();
  });

  it("trusts the server's typed hasAudio over the codec fields (C# MediaProbeResponse.HasAudio, C-323-2)", () => {
    expect(hasAudioStream(poolItem({ probe: probe({ hasAudio: false }) }))).toBe(false);
    expect(hasAudioStream(poolItem({ probe: probe({ hasAudio: true, audioCodec: null, audioChannels: null }) }))).toBe(true);
    // null/absent (an older server, or an artifact that predates it) falls back to the codec fields.
    expect(hasAudioStream(poolItem({ probe: probe({ hasAudio: null }) }))).toBe(true);
    expect(hasAudioStream(poolItem({ probe: probe({ hasAudio: null, audioCodec: null, audioChannels: null }) }))).toBe(false);
  });
});

describe('compilePoolPlacement', () => {
  const base = () => doc([track(TRACKS.v1, 'video', [media('c1', 0, 60)]), track('track-sfx', 'audio', [], { name: 'SFX' })]);

  it('inserts a 12 s source at 360 frames and its sound on a new paired audio track, one undo step', () => {
    const d = base();
    const plan = compilePoolPlacement(d, { item: poolItem(), track: d.tracks[0], where: 'playhead', playheadFrame: 55, rate: RATE_30, newId: newGuid });
    expect(plan).not.toBeNull();
    if (!plan) return;
    expect(plan.ops.map((op) => op.type)).toEqual(['insert', 'addTrack']);
    expect(isGuid(plan.clipId)).toBe(true);
    expect(isGuid(plan.companionId)).toBe(true);

    const s = store(d);
    s.dispatchBatch(plan.ops, plan.label);
    const after = s.getState().doc;
    const picture = findItem(after, plan.clipId);
    const sound = findItem(after, plan.companionId ?? '');
    expect(picture?.track.id).toBe(TRACKS.v1);
    expect(picture?.item.recordIn).toBe(60);
    expect(picture!.item.recordOut - picture!.item.recordIn).toBe(360);
    expect(sound?.track.kind).toBe('audio');
    expect(sound?.track.name).toBe(PAIRED_AUDIO_TRACK_NAME);
    expect(isGuid(sound?.track.id)).toBe(true);
    expect(sound?.item).toMatchObject({ recordIn: 60, recordOut: 420, sourceIn: 0, assetRef: picture?.item.assetRef });
    // The new audio track sits above the existing audio tracks.
    expect(after.tracks.map((t) => t.name)).toEqual([TRACKS.v1, PAIRED_AUDIO_TRACK_NAME, 'SFX']);

    s.undo();
    expect(s.getState().doc).toEqual(d);
  });

  it('reuses the paired track and ripples it with the picture so later sound stays in sync', () => {
    const d = doc([
      track(TRACKS.v1, 'video', [media('c1', 0, 60), media('c2', 60, 120)]),
      track('track-cam', 'audio', [media('s1', 0, 60), media('s2', 60, 120)], { name: PAIRED_AUDIO_TRACK_NAME }),
    ]);
    const plan = compilePoolPlacement(d, { item: poolItem(), track: d.tracks[0], where: 'playhead', playheadFrame: 58, rate: RATE_30, newId: newGuid });
    expect(plan?.ops.map((op) => op.type)).toEqual(['insert', 'insert']);
    const s = store(d);
    s.dispatchBatch(plan!.ops, plan!.label);
    const after = s.getState().doc;
    expect(findItem(after, 'c2')?.item.recordIn).toBe(420);
    expect(findItem(after, 's2')?.item.recordIn).toBe(420);
    expect(findItem(after, plan!.companionId!)?.track.id).toBe('track-cam');
  });

  it('Add to end appends the picture and places the sound at the same frame', () => {
    const d = base();
    const plan = compilePoolPlacement(d, { item: poolItem(), track: d.tracks[0], where: 'end', playheadFrame: 0, rate: RATE_30, newId: newGuid });
    const s = store(d);
    s.dispatchBatch(plan!.ops, plan!.label);
    const after = s.getState().doc;
    expect(findItem(after, plan!.clipId)?.item).toMatchObject({ recordIn: 60, recordOut: 420 });
    expect(findItem(after, plan!.companionId!)?.item).toMatchObject({ recordIn: 60, recordOut: 420 });
  });

  it('the marked range becomes the clip on both tracks', () => {
    const d = base();
    const plan = compilePoolPlacement(d, { item: poolItem(), track: d.tracks[0], where: 'end', playheadFrame: 0, rate: RATE_30, newId: newGuid, marks: { inFrame: 30, outFrame: 90 } });
    const s = store(d);
    s.dispatchBatch(plan!.ops, plan!.label);
    const after = s.getState().doc;
    expect(findItem(after, plan!.clipId)?.item).toMatchObject({ recordIn: 60, recordOut: 120, sourceIn: 30, name: 'interview [30-90]' });
    expect(findItem(after, plan!.companionId!)?.item).toMatchObject({ recordIn: 60, recordOut: 120, sourceIn: 30 });
  });

  it('a silent video, an unprobed video and an audio source add no companion', () => {
    const d = doc([track(TRACKS.v1, 'video', []), track(TRACKS.a1, 'audio', [])]);
    const silent = compilePoolPlacement(d, { item: poolItem({ probe: probe({ audioCodec: null, audioChannels: 0 }) }), track: d.tracks[0], where: 'end', playheadFrame: 0, rate: RATE_30, newId: newGuid });
    expect(silent?.ops.map((op) => op.type)).toEqual(['append']);
    const unprobed = compilePoolPlacement(d, { item: poolItem({ probe: null }), track: d.tracks[0], where: 'end', playheadFrame: 0, rate: RATE_30, newId: newGuid });
    expect(unprobed?.ops.map((op) => op.type)).toEqual(['append']);
    expect(unprobed?.span.lengthKnown).toBe(false);
    const audio = compilePoolPlacement(d, { item: poolItem({ kind: 'audio', probe: probe({ videoCodec: null, fpsNum: null, fpsDen: null }) }), track: d.tracks[1], where: 'end', playheadFrame: 0, rate: RATE_30, newId: newGuid });
    expect(audio?.ops.map((op) => op.type)).toEqual(['append']);
    expect(audio?.companionId).toBeNull();
  });
});

describe('compileExtractAudio', () => {
  it('puts the clip sound on the paired track at the same span and source offset, one undo step', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('c1', 30, 90, 12)])]);
    const plan = compileExtractAudio(d, 'c1', newGuid);
    expect(typeof plan).not.toBe('string');
    if (typeof plan === 'string') return;
    const s = store(d);
    s.dispatchBatch(plan.ops, 'Extract audio');
    const sound = findItem(s.getState().doc, plan.companionId);
    expect(sound?.track).toMatchObject({ kind: 'audio', name: PAIRED_AUDIO_TRACK_NAME });
    expect(sound?.item).toMatchObject({ recordIn: 30, recordOut: 90, sourceIn: 12, playbackRate: 1 });
    s.undo();
    expect(s.getState().doc).toEqual(d);
  });

  it('opens a second paired track rather than overwriting sound already there', () => {
    const d = doc([
      track(TRACKS.v1, 'video', [media('c1', 30, 90)]),
      track('track-cam', 'audio', [media('s1', 0, 60)], { name: PAIRED_AUDIO_TRACK_NAME }),
    ]);
    const plan = compileExtractAudio(d, 'c1', newGuid);
    if (typeof plan === 'string') throw new Error(plan);
    expect(plan.ops[0]).toMatchObject({ type: 'addTrack', track: { name: `${PAIRED_AUDIO_TRACK_NAME} 2` } });
  });

  it('refuses what has no sound to give', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('h', 0, 30, 0, { hold: true })]), track(TRACKS.a1, 'audio', [media('a', 0, 30)])]);
    expect(compileExtractAudio(d, 'h', newGuid)).toMatch(/freeze frame/);
    expect(compileExtractAudio(d, 'a', newGuid)).toMatch(/already on an audio track/);
  });
});

describe('newGuid', () => {
  it('mints a v4 Guid even without crypto.randomUUID', () => {
    const original = globalThis.crypto.randomUUID;
    Object.defineProperty(globalThis.crypto, 'randomUUID', { value: undefined, configurable: true });
    try {
      const id = newGuid();
      expect(isGuid(id)).toBe(true);
      expect(id[14]).toBe('4');
    } finally {
      Object.defineProperty(globalThis.crypto, 'randomUUID', { value: original, configurable: true });
    }
  });
});
