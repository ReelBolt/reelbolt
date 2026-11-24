import { describe, expect, it } from 'vitest';
import fc from 'fast-check';

import { apply, applyAll, assertInvariants as assertEngineInvariants, type EditOp } from '../../engine';
import type { EditTimeline, MediaClip, Track } from '../../types';
import { compileRemoveSilence, mapSourceSilenceToRecord, normalizeSpans } from '../remove-silence';

/** These fixtures name things `v1`/`track-video`: every invariant except the Guid id shape (pinned in `apply.contract.test.ts`). */
const assertInvariants = (doc: Parameters<typeof assertEngineInvariants>[0]): void => assertEngineInvariants(doc, { ids: 'opaque' });

const clip = (id: string, recordIn: number, recordOut: number, sourceIn = 0, playbackRate = 1): MediaClip => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  provenance: null,
  assetRef: { kind: 'projectFile', projectFileId: `file-${id}` },
  sourceIn,
  playbackRate,
  enabled: true,
});

const timeline = (items: MediaClip[]): EditTimeline => {
  const track: Track = { id: 'v1', kind: 'video', name: 'V1', height: 64, locked: false, enabled: true, mute: false, solo: false, items };
  return {
    schemaVersion: 2,
    id: 'tl',
    projectId: 'p',
    name: 'tl',
    settings: { width: 320, height: 180, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
    tracks: [track],
    markers: [],
    inOut: null,
    seed: null,
  };
};

let counter = 0;
const freshId = () => `n${++counter}`;

describe('compileRemoveSilence', () => {
  it('cuts the middle of a clip out with two blades and one ripple extract, closing the gap', () => {
    const doc = timeline([clip('a', 0, 100), clip('b', 100, 150)]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 40, endFrame: 60 }], newItemId: freshId });

    expect(plan.cuts).toBe(1);
    expect(plan.removedFrames).toBe(20);
    expect(plan.ops.map((o) => o.type)).toEqual(['blade', 'blade', 'extract']);
    const items = plan.preview.tracks[0].items;
    expect(items.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 40], [40, 80], [80, 130]]);
    // The surviving tail keeps reading from where the removed silence ended.
    expect((items[1] as MediaClip).sourceIn).toBe(60);
    assertInvariants(plan.preview);
  });

  it('removes a whole clip the span covers, with no blade at all', () => {
    const doc = timeline([clip('a', 0, 10), clip('b', 10, 20), clip('c', 20, 30)]);
    const plan = compileRemoveSilence(doc, { trackId: 'v1', spans: [{ startFrame: 10, endFrame: 20 }], newItemId: freshId });
    expect(plan.ops.map((o) => o.type)).toEqual(['extract']);
    expect(plan.preview.tracks[0].items.map((i) => i.id)).toEqual(['a', 'c']);
    expect(plan.preview.tracks[0].items[1].recordIn).toBe(10);
  });

  it('respects padding and the minimum length, and skips spans off the clips', () => {
    const doc = timeline([clip('a', 0, 100)]);
    const plan = compileRemoveSilence(doc, {
      trackId: 'v1',
      spans: [
        { startFrame: 40, endFrame: 60 }, // 20 frames, padded to 14
        { startFrame: 70, endFrame: 73 }, // 3 frames, too short after padding
        { startFrame: 200, endFrame: 260 }, // off the clip
      ],
      padFrames: 3,
      minFrames: 5,
      newItemId: freshId,
    });
    expect(plan.cuts).toBe(1);
    expect(plan.removedFrames).toBe(14);
    expect(plan.preview.tracks[0].items.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 43], [43, 86]]);
  });

  it('is one undo step: the whole program undoes back to the original through the engine inverses', () => {
    const doc = timeline([clip('a', 0, 100), clip('b', 100, 160, 5, 1)]);
    const plan = compileRemoveSilence(doc, {
      trackId: 'v1',
      spans: [{ startFrame: 10, endFrame: 20 }, { startFrame: 90, endFrame: 120 }],
      newItemId: freshId,
    });
    expect(plan.cuts).toBe(3);
    let current = doc;
    const inverses: EditOp[][] = [];
    for (const op of plan.ops) {
      const result = apply(current, op);
      current = result.doc;
      inverses.push(result.inverse);
    }
    expect(current).toStrictEqual(plan.preview);
    let undone = current;
    for (let i = inverses.length - 1; i >= 0; i--) undone = applyAll(undone, inverses[i]);
    expect(undone).toStrictEqual(doc);
  });

  it('never mutates the input and never produces an invalid intermediate document (property)', () => {
    fc.assert(
      fc.property(
        fc.array(fc.record({ startFrame: fc.integer({ min: 0, max: 200 }), length: fc.integer({ min: 1, max: 40 }) }), { maxLength: 6 }),
        fc.integer({ min: 0, max: 4 }),
        (raw, pad) => {
          const doc = timeline([clip('a', 0, 80), clip('b', 80, 140, 10), clip('c', 150, 200)]);
          const before = structuredClone(doc);
          const plan = compileRemoveSilence(doc, {
            trackId: 'v1',
            spans: raw.map((r) => ({ startFrame: r.startFrame, endFrame: r.startFrame + r.length })),
            padFrames: pad,
            newItemId: freshId,
          });
          expect(doc).toStrictEqual(before);
          let current = doc;
          for (const op of plan.ops) {
            current = apply(current, op).doc;
            assertInvariants(current);
          }
          expect(current).toStrictEqual(plan.preview);
          const total = (d: EditTimeline) => d.tracks[0].items.reduce((n, i) => n + (i.recordOut - i.recordIn), 0);
          expect(total(before) - total(plan.preview)).toBe(plan.removedFrames);
        },
      ),
    );
  });
});

describe('mapSourceSilenceToRecord', () => {
  it('maps source seconds through sourceIn and playbackRate onto record frames, clipped to the clip', () => {
    // Clip plays source frames [60, 120) at 2x over record [10, 40).
    const c = clip('a', 10, 40, 60, 2);
    expect(mapSourceSilenceToRecord(c, { id: 'g1', startSec: 3, endSec: 3.5 }, 30)).toEqual({ startFrame: 25, endFrame: 32 });
    // Entirely before the clip's source range.
    expect(mapSourceSilenceToRecord(c, { id: 'g2', startSec: 0, endSec: 1 }, 30)).toBeNull();
    // Overhanging the end is clipped to the clip's record end.
    expect(mapSourceSilenceToRecord(c, { id: 'g3', startSec: 3.8, endSec: 9 }, 30)).toEqual({ startFrame: 37, endFrame: 40 });
  });
});

describe('normalizeSpans', () => {
  it('merges overlaps, drops empties and negatives, and sorts', () => {
    expect(normalizeSpans([{ startFrame: 10, endFrame: 20 }, { startFrame: 15, endFrame: 25 }, { startFrame: 5, endFrame: 5 }, { startFrame: -1, endFrame: 3 }, { startFrame: 30, endFrame: 31 }]))
      .toEqual([{ startFrame: 10, endFrame: 25 }, { startFrame: 30, endFrame: 31 }]);
  });
});

describe('mapSourceSilenceToRecord: reversed and held clips', () => {
  it('maps no span onto a reversed clip or a freeze frame', () => {
    for (const extra of [{ reverse: true }, { hold: true }]) {
      const c: MediaClip = { ...clip('c', 0, 300), ...extra };
      expect(mapSourceSilenceToRecord(c, { id: 'g', startSec: 1, endSec: 2 }, 30)).toBeNull();
    }
  });
});
