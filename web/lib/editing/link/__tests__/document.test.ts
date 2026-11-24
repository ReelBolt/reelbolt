/** @vitest-environment node */
/**
 * R08.1 / R08.2 document rules in the TS engine (lockstep with EditTimelineValidator), the
 * companion created by a pool insert / Extract audio, and the multicam program-audio flatten.
 */

import { describe, expect, it } from 'vitest';

import { apply, assertInvariants, EditEngineError } from '../../engine';
import { EditorStore } from '../../store';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import { compileExtractAudio, compilePoolPlacement } from '../../media/pool-insert';
import type { MediaPoolItem } from '../../media/types';
import { expandMulticamAudio, flattenTimeline } from '../../structure/flatten';
import { classifyParity } from '../../preview/parity';
import type { EditTimeline, MulticamClip } from '../../types';
import { groupOf } from '..';

const opaque = { ids: 'opaque' as const };

describe('R08.1 link rules', () => {
  it('accepts a pair, a group of one and unlinked clips', () => {
    expect(() => assertInvariants(doc([track('V1', 'video', [media('v', 0, 10, 0, { linkId: 'g-1' })]), track('A1', 'audio', [media('a', 0, 10, 0, { linkId: 'g-1' })])]), opaque)).not.toThrow();
    expect(() => assertInvariants(doc([track('V1', 'video', [media('v', 0, 10, 0, { linkId: 'solo' })])]), opaque)).not.toThrow();
  });

  it.each([
    ['empty', ''],
    ['bad charset', 'has space'],
    ['too long', 'x'.repeat(65)],
  ])('refuses a %s link id', (_n, linkId) => {
    expect(() => assertInvariants(doc([track('V1', 'video', [media('v', 0, 10, 0, { linkId })])]), opaque)).toThrow(/items\[0\]\.linkId/);
  });

  it('refuses two members on one track and members of different sources, naming the later member', () => {
    const same = doc([track('V1', 'video', [media('a', 0, 10, 0, { linkId: 'g' }), media('b', 10, 20, 0, { linkId: 'g' })])]);
    expect(() => assertInvariants(same, opaque)).toThrow(/^tracks\[0\]\.items\[1\]\.linkId/);
    const other = doc([track('V1', 'video', [media('a', 0, 10, 0, { linkId: 'g' })]), track('A1', 'audio', [media('b', 0, 10, 0, { linkId: 'g', assetRef: assetRef(2) })])]);
    expect(() => assertInvariants(other, opaque)).toThrow(/^tracks\[1\]\.items\[0\]\.linkId/);
  });

  it('keeps link groups per compound sequence', () => {
    const base = doc([track('V1', 'video', [media('a', 0, 10, 0, { linkId: 'g' })])]);
    const withSeq: EditTimeline = { ...base, sequences: [{ id: 's', name: 'S', tracks: [track('SV', 'video', [media('s1', 0, 5, 0, { linkId: 'g' }), media('s2', 5, 10, 0, { linkId: 'g' })])] }] };
    expect(() => assertInvariants(withSeq, opaque)).toThrow(/^sequences\[0\]\.tracks\[0\]\.items\[1\]\.linkId/);
  });

  it('setProperty linkId validates its value, is undoable, and only fits media clips', () => {
    const d = doc([track('V1', 'video', [media('v', 0, 10)])]);
    const set = apply(d, { type: 'setProperty', itemId: 'v', key: 'linkId', value: 'abc-1' });
    expect((set.doc.tracks[0].items[0] as { linkId?: string }).linkId).toBe('abc-1');
    expect(apply(set.doc, set.inverse[0]).doc).toEqual(d);
    expect(() => apply(d, { type: 'setProperty', itemId: 'v', key: 'linkId', value: 'bad id' })).toThrow(EditEngineError);
    expect(() => apply(d, { type: 'setProperty', itemId: 'v', key: 'linkId', value: 5 })).toThrow(EditEngineError);
    expect(apply(set.doc, { type: 'setProperty', itemId: 'v', key: 'linkId', value: null }).doc.tracks[0].items[0]).toMatchObject({ linkId: null });
  });
});

const POOL: MediaPoolItem = {
  mediaRef: 'file:00000000-0000-4000-8000-000000000777',
  kind: 'video',
  fileName: 'a.mp4',
  mimeType: 'video/mp4',
  sizeBytes: 1,
  category: 'userFiles',
  probe: { durationSec: 4, fpsNum: 30, fpsDen: 1, width: 1, height: 1, videoCodec: 'h264', audioCodec: 'aac', container: 'mp4', audioChannels: 2, audioSampleRateHz: 48000, bitrateBps: 1, hasAudio: true },
  bins: [],
  filmstripUrl: '',
} as MediaPoolItem;

describe('R08.1 companion creation', () => {
  let n = 0;
  const newId = (): string => `id-${++n}`;
  const empty = () => doc([track('V1', 'video', []), track('A9', 'audio', [])]);

  it('a pool insert with sound makes a linked pair in one batch, one undo', () => {
    const d = empty();
    const plan = compilePoolPlacement(d, { item: POOL, track: d.tracks[0], where: 'end', playheadFrame: 0, rate: { num: 30, den: 1 }, newId })!;
    expect(plan.linkId).not.toBeNull();
    const s = new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    s.dispatchBatch(plan.ops, 'Insert');
    const after = s.getState().doc;
    assertInvariants(after, opaque);
    const group = groupOf(after, plan.clipId);
    expect(group.map((m) => m.track.kind).sort()).toEqual(['audio', 'video']);
    expect(group.every((m) => (m.item as { linkId?: string }).linkId === plan.linkId)).toBe(true);
    s.undo();
    expect(s.getState().doc).toEqual(d);
  });

  it('no audio stream: no companion and no link', () => {
    const d = empty();
    const silent = { ...POOL, probe: { ...POOL.probe!, hasAudio: false } } as MediaPoolItem;
    const plan = compilePoolPlacement(d, { item: silent, track: d.tracks[0], where: 'end', playheadFrame: 0, rate: { num: 30, den: 1 }, newId })!;
    expect(plan.companionId).toBeNull();
    expect(plan.linkId).toBeNull();
    expect(plan.ops).toHaveLength(1);
    expect((plan.ops[0] as { item: { linkId?: string } }).item.linkId).toBeUndefined();
  });

  it('Extract audio links the sound to the picture (new group, or the picture\'s own)', () => {
    const d = doc([track('V1', 'video', [media('v', 0, 30)]), track('A9', 'audio', [])]);
    const plan = compileExtractAudio(d, 'v', newId);
    if (typeof plan === 'string') throw new Error(plan);
    const s = new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    s.dispatchBatch(plan.ops, 'Extract');
    expect(groupOf(s.getState().doc, 'v')).toHaveLength(2);
    assertInvariants(s.getState().doc, opaque);
    const linked = doc([track('V1', 'video', [media('v', 0, 30, 0, { linkId: 'keep' })]), track('A9', 'audio', [])]);
    const again = compileExtractAudio(linked, 'v', newId);
    if (typeof again === 'string') throw new Error(again);
    expect(again.ops.some((o) => o.type === 'setProperty')).toBe(false);
    expect(JSON.stringify(again.ops[0])).toContain('"linkId":"keep"');
  });
});

describe('R08.2 multicam audioSource', () => {
  const mc = (extra: Partial<MulticamClip> = {}): MulticamClip => ({
    id: 'mc',
    type: 'multicamClip',
    name: 'Show',
    recordIn: 10,
    recordOut: 110,
    angles: [
      { id: 'a1', name: 'Wide', assetRef: assetRef(1), offsetFrames: 3 },
      { id: 'a2', name: 'Close', assetRef: assetRef(2), offsetFrames: 7 },
    ],
    activeAngleId: 'a2',
    cuts: [{ atFrame: 60, angleId: 'a1' }],
    sourceIn: 5,
    ...extra,
  });
  const withMc = (clip: MulticamClip) => doc([track('V1', 'video', [clip], { mute: true })]);

  it('program (default) is the first angle\'s sound across the whole clip, on a derived track after the host', () => {
    const flat = flattenTimeline(withMc(mc()));
    expect(flat.tracks.map((t) => t.kind)).toEqual(['video', 'audio']);
    const sound = flat.tracks[1];
    expect(sound).toMatchObject({ id: 'mc/audio', name: 'Show / audio', mute: true });
    expect(sound.items).toHaveLength(1);
    expect(sound.items[0]).toMatchObject({ id: 'mc/audio0', recordIn: 10, recordOut: 110, sourceIn: 8, assetRef: assetRef(1) });
    expect(flat.tracks[0].items).toHaveLength(2); // the picture still cuts between angles
  });

  it('an angle id plays that angle; none is silent; a disabled clip is silent', () => {
    expect(expandMulticamAudio(mc({ audioSource: 'a2' }))[0]).toMatchObject({ assetRef: assetRef(2), sourceIn: 12 });
    expect(expandMulticamAudio(mc({ audioSource: 'none' }))).toEqual([]);
    expect(flattenTimeline(withMc(mc({ audioSource: 'none' }))).tracks).toHaveLength(1);
    expect(expandMulticamAudio(mc({ enabled: false }))).toEqual([]);
  });

  it('setProperty audioSource accepts program/none/an angle and the engine refuses an unknown angle', () => {
    const d = withMc(mc());
    for (const value of ['program', 'none', 'a1']) expect(() => apply(d, { type: 'setProperty', itemId: 'mc', key: 'audioSource', value })).not.toThrow();
    expect(() => apply(d, { type: 'setProperty', itemId: 'mc', key: 'audioSource', value: 'zzz' })).toThrow(/audioSource/);
    const set = apply(d, { type: 'setProperty', itemId: 'mc', key: 'audioSource', value: 'none' });
    expect(apply(set.doc, set.inverse[0]).doc).toEqual(d);
  });

  it('on an audio host track an explicit source replaces the angle runs; the preview stays faithful', () => {
    const onAudio = doc([track('A1', 'audio', [mc({ audioSource: 'a1' })])]);
    const flat = flattenTimeline(onAudio);
    expect(flat.tracks).toHaveLength(1);
    expect(flat.tracks[0].items).toHaveLength(1);
    expect(classifyParity(withMc(mc())).worst ?? 'faithful').toBe('faithful');
  });
});
