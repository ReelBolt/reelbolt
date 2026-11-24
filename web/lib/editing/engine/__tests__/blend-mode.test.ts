/** @vitest-environment node */
/** T-012: `blendMode` through the engine - allowlisted on media clips only, closed words, video tracks only, undoable. */
import { describe, expect, it } from 'vitest';
import { apply, assertInvariants, BLEND_MODES, EditEngineError, SET_PROPERTY_ALLOWLIST } from '../index';
import type { EditTimeline, MediaClip } from '../../types';

const clip = (id: string): MediaClip =>
  ({ id, type: 'mediaClip', name: id, recordIn: 0, recordOut: 100, assetRef: { kind: 'projectFile', projectFileId: '7d3c2b1a-0f9e-4d8c-b7a6-5f4e3d2c1b01' }, sourceIn: 0, playbackRate: 1, enabled: true }) as MediaClip;
const CLIP = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeee01';
const doc = (kind: 'video' | 'audio' = 'video'): EditTimeline =>
  ({
    schemaVersion: 2, id: '0e4d2c1b-7a6f-4e3d-8c2b-1a0f9e8d7c61', projectId: '5b0f6c1e-2a3d-4c8e-9f10-1a2b3c4d5e60', name: 'T', markers: [],
    settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
    tracks: [{ id: '11111111-2222-4333-8444-555555555501', kind, name: 'T', color: null, height: 64, locked: false, enabled: true, mute: false, solo: false, busId: null, items: [clip(CLIP)] }],
  }) as unknown as EditTimeline;
const set = (value: unknown) => ({ type: 'setProperty', itemId: CLIP, key: 'blendMode', value }) as never;

describe('blendMode engine contract', () => {
  it('is allowlisted on mediaClip only; the words are the contract catalogue', () => {
    expect(SET_PROPERTY_ALLOWLIST.mediaClip).toContain('blendMode');
    expect(SET_PROPERTY_ALLOWLIST.titleClip).not.toContain('blendMode');
    expect(BLEND_MODES).toEqual(['normal', 'multiply', 'screen', 'overlay', 'softLight', 'hardLight', 'darken', 'lighten', 'add', 'difference']);
  });

  it('sets every word on a video track, undoes through the inverse, and clears with undefined', () => {
    for (const word of BLEND_MODES) {
      const { doc: next, inverse } = apply(doc(), set(word));
      expect((next.tracks[0].items[0] as MediaClip).blendMode).toBe(word);
      assertInvariants(next, { ids: 'guid' });
      const back = inverse.reduce((d, op) => apply(d, op, { inverse: true }).doc, next);
      expect((back.tracks[0].items[0] as MediaClip).blendMode).toBeUndefined();
    }
    const set1 = apply(doc(), set('multiply')).doc;
    expect((apply(set1, set(undefined)).doc.tracks[0].items[0] as MediaClip).blendMode).toBeUndefined();
  });

  it('reads a null blendMode (a stored document may carry one) as no blend', () => {
    const d = doc();
    (d.tracks[0].items[0] as unknown as { blendMode: null }).blendMode = null;
    expect(() => assertInvariants(d, { ids: 'guid' })).not.toThrow();
    expect(() => apply(d, set('multiply'))).not.toThrow();
    const audio = doc('audio');
    (audio.tracks[0].items[0] as unknown as { blendMode: null }).blendMode = null;
    expect(() => assertInvariants(audio, { ids: 'guid' })).not.toThrow();
  });

  it('refuses an unknown or wrong-case word', () => {
    expect(() => apply(doc(), set('Multiply'))).toThrow(EditEngineError);
    expect(() => apply(doc(), set('sparkle'))).toThrow(EditEngineError);
    expect(() => apply(doc(), set(3))).toThrow(EditEngineError);
  });

  it('refuses a non-normal blend on an audio track, accepts normal there', () => {
    const audio = doc('audio');
    expect(() => apply(audio, set('multiply'))).toThrow(/video track/);
    const ok = { ...audio, tracks: [{ ...audio.tracks[0], items: [{ ...clip(CLIP), blendMode: 'normal' }] }] } as unknown as EditTimeline;
    expect(() => assertInvariants(ok, { ids: 'guid' })).not.toThrow();
    const bad = { ...audio, tracks: [{ ...audio.tracks[0], items: [{ ...clip(CLIP), blendMode: 'add' }] }] } as unknown as EditTimeline;
    expect(() => assertInvariants(bad, { ids: 'guid' })).toThrow(/tracks\[0\]\.items\[0\]\.blendMode/);
  });
});
