import { describe, expect, it } from 'vitest';

import type { EditTimeline, MediaClip, Track } from '../../types';
import { compileRemoveSilence, mapSourceSilenceToRecord } from '../remove-silence';

const clip = (id: string, recordIn: number, recordOut: number, extra: Partial<MediaClip> = {}): MediaClip => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  provenance: null,
  assetRef: { kind: 'projectFile', projectFileId: `file-${id}` },
  sourceIn: 0,
  playbackRate: 1,
  enabled: true,
  ...extra,
});

const track = (id: string, kind: 'video' | 'audio', items: MediaClip[]): Track => ({ id, kind, name: id, height: 64, locked: false, enabled: true, mute: false, solo: false, items });
const timeline = (tracks: Track[]): EditTimeline => ({
  schemaVersion: 2,
  id: 'tl',
  projectId: 'p',
  name: 'tl',
  settings: { width: 320, height: 180, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
  tracks,
  markers: [],
  inOut: null,
  seed: null,
});
let n = 0;
const fresh = () => `s${++n}`;
const spans = (doc: EditTimeline, id = 'v1') => doc.tracks.find((t) => t.id === id)?.items.map((i) => [i.recordIn, i.recordOut]);

describe('remove silence at the edges of a clip (no pad sliver)', () => {
  it('a silent tail is removed exactly, keeping the pad only on the speech side', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100)])]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 70, endFrame: 100 }], padFrames: 2, newItemId: fresh });
    expect(spans(plan.preview)).toEqual([[0, 72]]);
    expect(plan.removedFrames).toBe(28);
  });

  it('a silent head is removed exactly', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100)])]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 0, endFrame: 30 }], padFrames: 2, newItemId: fresh });
    expect(spans(plan.preview)).toEqual([[0, 72]]);
    expect(plan.removedFrames).toBe(28);
  });

  it('a wholly silent clip is removed whole', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 50), clip('b', 50, 100)])]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 0, endFrame: 50 }], padFrames: 2, newItemId: fresh });
    expect(plan.preview.tracks[0].items.map((i) => i.id)).toEqual(['b']);
    expect(spans(plan.preview)).toEqual([[0, 50]]);
  });

  it('a silence across the cut between two clips removes it without padding the join', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 50), clip('b', 50, 100)])]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 40, endFrame: 60 }], padFrames: 2, newItemId: fresh });
    expect(plan.removedFrames).toBe(16);
  });

  it('adjacent silences merge and a silent tail still ends exactly', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100)])]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 60, endFrame: 80 }, { startFrame: 80, endFrame: 100 }], padFrames: 2, newItemId: fresh });
    expect(spans(plan.preview)).toEqual([[0, 62]]);
  });

  it('linked picture and sound are cut by the same frames', () => {
    const doc = timeline([track('v1', 'video', [clip('v', 0, 100, { linkId: 'L' })]), track('a1', 'audio', [clip('au', 0, 100, { linkId: 'L' })])]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 70, endFrame: 100 }], padFrames: 2, newItemId: fresh });
    expect(spans(plan.preview, 'v1')).toEqual([[0, 72]]);
    expect(spans(plan.preview, 'a1')).toEqual([[0, 72]]);
  });

  it('a silence that reaches the clip start or end to within a frame covers it', () => {
    const c = clip('a', 0, 100);
    expect(mapSourceSilenceToRecord(c, { id: 'g1', startSec: 2, endSec: 99.5 / 30 }, 30)).toEqual({ startFrame: 60, endFrame: 100 });
    expect(mapSourceSilenceToRecord(c, { id: 'g2', startSec: 0.5 / 30, endSec: 1 }, 30)).toEqual({ startFrame: 0, endFrame: 30 });
  });
});
