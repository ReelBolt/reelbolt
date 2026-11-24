/** @vitest-environment node */
/**
 * R08.1 linked clips: group reading, link-aware op batches (one undo step each), the J/L-cut helper
 * and the migration. Every batch is dispatched through the real store and undone to prove the
 * "one history entry, exact restore" contract.
 */

import fc from 'fast-check';
import { describe, expect, it } from 'vitest';

import { assertInvariants, EditEngineError, type EditOp } from '../../engine';
import { EditorStore } from '../../store';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';
import {
  expandSelection,
  groupOf,
  isLinked,
  jlCutOps,
  linkedOpsForBlade,
  linkedOpsForDelete,
  linkedOpsForEnable,
  linkedOpsForMove,
  linkedOpsForNudge,
  linkedOpsForRetime,
  linkedOpsForSetProperty,
  linkedOpsForTrim,
  linkOps,
  migrateToLinkedAudio,
  unlinkOps,
} from '..';

const store = (d: EditTimeline) => new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
const get = (d: EditTimeline, id: string): MediaClip => {
  for (const t of d.tracks) {
    const hit = t.items.find((i) => i.id === id);
    if (hit) return hit as MediaClip;
  }
  throw new Error(`no ${id}`);
};

/** V1: v1, v2 (contiguous); A1: a1, a2 with the same spans. Pairs are linked L1 / L2. */
function linkedDoc(): EditTimeline {
  return doc([
    track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' }), media('v2', 100, 250, 0, { linkId: 'L2' }), media('v3', 250, 300)]),
    track('A1', 'audio', [media('a1', 0, 100, 0, { linkId: 'L1' }), media('a2', 100, 250, 0, { linkId: 'L2' }), media('a3', 250, 300)]),
  ]);
}

/** Dispatches `ops` as one batch, asserts the document stays valid, then undoes and expects the original back. */
function roundTrip(d: EditTimeline, ops: EditOp[]): EditTimeline {
  const s = store(d);
  s.dispatchBatch(ops, 'test');
  const after = s.getState().doc;
  assertInvariants(after, { ids: 'opaque' });
  expect(s.getState().history?.length ?? s.getState().cursor).toBeGreaterThan(0);
  s.undo();
  expect(s.getState().doc).toEqual(d);
  return after;
}

describe('R08.1 groups', () => {
  it('reads a group and expands a selection to its partners', () => {
    const d = linkedDoc();
    expect(groupOf(d, 'v1').map((m) => m.item.id)).toEqual(['v1', 'a1']);
    expect(groupOf(d, 'v3').map((m) => m.item.id)).toEqual(['v3']);
    expect(isLinked(d, 'a2')).toBe(true);
    expect(isLinked(d, 'a3')).toBe(false);
    expect(expandSelection(d, ['a2', 'v3'])).toEqual(['a2', 'v3', 'v2']);
    expect(expandSelection(d, ['nope'])).toEqual([]);
  });
});

describe('R08.1 link-aware batches', () => {
  it('moves both clips of a pair by the same delta, in one undo step', () => {
    const d = doc([
      track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' })]),
      track('V2', 'video', []),
      track('A1', 'audio', [media('a1', 0, 100, 0, { linkId: 'L1' })]),
    ]);
    const ops = linkedOpsForMove(d, [{ itemId: 'v1', toTrackId: 'V2', toRecordIn: 40 }]);
    expect(ops).toHaveLength(2);
    const after = roundTrip(d, ops);
    expect(get(after, 'v1').recordIn).toBe(40);
    expect(get(after, 'a1').recordIn).toBe(40);
    expect(after.tracks[1].items.map((i) => i.id)).toEqual(['v1']);
    expect(after.tracks[2].items.map((i) => i.id)).toEqual(['a1']);
  });

  it('does not move a partner twice when both are in the move list, and skips a locked partner track', () => {
    const d = linkedDoc();
    const both = linkedOpsForMove(d, [
      { itemId: 'v3', toTrackId: 'V1', toRecordIn: 260 },
      { itemId: 'a3', toTrackId: 'A1', toRecordIn: 260 },
    ]);
    expect(both).toHaveLength(2);
    const locked = linkedOpsForMove(d, [{ itemId: 'v2', toTrackId: 'V1', toRecordIn: 110 }], { isLocked: (id) => id === 'A1' });
    expect(locked.map((o) => (o as { itemId: string }).itemId)).toEqual(['v2']);
  });

  it('nudges a pair and orders right-moves right-most first', () => {
    const d = linkedDoc();
    const ops = linkedOpsForNudge(d, ['v1', 'v2'], 5);
    expect(ops).toHaveLength(4);
    expect(ops.map((o) => (o as { itemId: string }).itemId).slice(0, 2).sort()).toEqual(['a2', 'v2']);
    const after = roundTrip(doc([
      track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' }), media('v2', 100, 250, 0, { linkId: 'L2' })]),
      track('A1', 'audio', [media('a1', 0, 100, 0, { linkId: 'L1' }), media('a2', 100, 250, 0, { linkId: 'L2' })]),
    ]), linkedOpsForNudge(d, ['v1', 'v2'], 5));
    expect(get(after, 'a1').recordIn).toBe(5);
    expect(get(after, 'v2').recordOut).toBe(255);
  });

  it('trims the same edge of both, to the delta every member can take', () => {
    const d = doc([
      track('V1', 'video', [media('v1', 0, 100, 20, { linkId: 'L1' })]),
      track('A1', 'audio', [media('a1', 0, 100, 5, { linkId: 'L1' })]),
    ]);
    // Head trim earlier by 15: the picture could, the sound has only 5 frames of handle -> both take 5.
    const ops = linkedOpsForTrim(d, 'v1', 'in', 'ripple', -15);
    expect(ops.map((o) => (o as { deltaFrames: number }).deltaFrames)).toEqual([-5, -5]);
    const after = roundTrip(d, ops);
    expect(get(after, 'v1').sourceIn).toBe(15);
    expect(get(after, 'a1').sourceIn).toBe(0);
    expect(get(after, 'v1').recordOut).toBe(get(after, 'a1').recordOut);
  });

  it('ripple-trims the out edge of a pair and shifts the followers on both tracks', () => {
    const d = linkedDoc();
    const after = roundTrip(d, linkedOpsForTrim(d, 'a1', 'out', 'ripple', -10));
    expect(get(after, 'v2').recordIn).toBe(90);
    expect(get(after, 'a2').recordIn).toBe(90);
  });

  it('refuses to trim a pair when one member cannot move at all', () => {
    const d = doc([
      track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' })]),
      track('A1', 'audio', [media('a1', 0, 100, 0, { linkId: 'L1' })]),
    ]);
    expect(linkedOpsForTrim(d, 'v1', 'in', 'ripple', -10)).toEqual([]);
  });

  it('blades a pair; the right halves get a fresh shared link and undo restores everything', () => {
    const d = linkedDoc();
    const plan = linkedOpsForBlade(d, 'v1', 40, 'v1-right');
    expect(Object.keys(plan.rightHalves).sort()).toEqual(['a1', 'v1']);
    const after = roundTrip(d, plan.ops);
    const left = get(after, 'v1');
    const right = get(after, 'v1-right');
    expect(get(after, 'a1').linkId).toBe('L1');
    expect(left.linkId).toBe('L1');
    expect(right.linkId).toBeTruthy();
    expect(right.linkId).not.toBe('L1');
    expect(get(after, plan.rightHalves.a1).linkId).toBe(right.linkId);
    expect(groupOf(after, 'v1-right')).toHaveLength(2);
  });

  it('blades only the members that straddle the frame, and unlinks a lone right half', () => {
    const d = doc([
      track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' })]),
      track('A1', 'audio', [media('a1', 0, 30, 0, { linkId: 'L1' })]),
    ]);
    const plan = linkedOpsForBlade(d, 'v1', 60, 'v1-right');
    expect(Object.keys(plan.rightHalves)).toEqual(['v1']);
    const after = roundTrip(d, plan.ops);
    expect(get(after, 'v1-right').linkId).toBeNull();
    expect(get(after, 'v1').linkId).toBe('L1');
  });

  it('blades an unlinked clip with a plain blade', () => {
    const d = linkedDoc();
    const plan = linkedOpsForBlade(d, 'v3', 280, 'v3-right');
    expect(plan.ops).toEqual([{ type: 'blade', itemId: 'v3', atFrame: 280, newItemId: 'v3-right' }]);
  });

  it('ripple-deletes a pair and closes the gap on both tracks; lift leaves the gaps', () => {
    const d = linkedDoc();
    const ripple = linkedOpsForDelete(d, ['v1'], 'ripple');
    expect(ripple.itemCount).toBe(2);
    const after = roundTrip(d, ripple.ops);
    expect(get(after, 'v2').recordIn).toBe(0);
    expect(get(after, 'a2').recordIn).toBe(0);
    expect(get(after, 'v3').recordIn).toBe(150);
    const lift = linkedOpsForDelete(d, ['a1'], 'lift');
    expect(lift.ops.map((o) => o.type)).toEqual(['lift', 'lift']);
    const lifted = roundTrip(d, lift.ops);
    expect(lifted.tracks[0].items.map((i) => i.id)).toEqual(['v2', 'v3']);
    expect(get(lifted, 'v2').recordIn).toBe(100);
  });

  it('leaves a partner on a locked track alone and counts it', () => {
    const d = linkedDoc();
    const plan = linkedOpsForDelete(d, ['v1'], 'lift', (id) => id === 'A1');
    expect(plan.skippedLocked).toBe(1);
    expect(plan.ops).toEqual([{ type: 'lift', itemId: 'v1' }]);
  });

  it('retimes a pair to the same speed and proportional length', () => {
    const d = linkedDoc();
    const ops = linkedOpsForRetime(d, 'v1', 2, 50);
    expect(ops).toHaveLength(2);
    const after = roundTrip(d, ops);
    expect(get(after, 'v1').playbackRate).toBe(2);
    expect(get(after, 'a1').recordOut).toBe(50);
    expect(get(after, 'a2').recordIn).toBe(50);
  });

  it('enables/disables a pair together but sets per-clip properties on one clip', () => {
    const d = linkedDoc();
    const off = linkedOpsForEnable(d, 'v1', false);
    expect(off).toHaveLength(2);
    const after = roundTrip(d, off);
    expect(get(after, 'a1').enabled).toBe(false);
    expect(linkedOpsForSetProperty(d, 'v1', 'gainDb', { value: -3 })).toHaveLength(1);
  });

  it('links clips into a new group, refuses invalid groups, and unlinks', () => {
    const d = linkedDoc();
    const ops = linkOps(d, ['v3', 'a3']);
    expect(ops).toHaveLength(2);
    const after = roundTrip(d, ops);
    expect(isLinked(after, 'v3')).toBe(true);
    expect(linkOps(d, ['v1', 'v2'])).toEqual([]); // same track
    expect(linkOps(d, ['v1'])).toEqual([]);
    const other = doc([track('V1', 'video', [media('x', 0, 10)]), track('A1', 'audio', [media('y', 0, 10, 0, { assetRef: assetRef(9) })])]);
    expect(linkOps(other, ['x', 'y'])).toEqual([]); // different source
    const unlinked = roundTrip(d, unlinkOps(d, ['v1']));
    expect(isLinked(unlinked, 'a1')).toBe(false);
    const one = unlinkOps(d, ['v1'], true);
    expect(one).toHaveLength(1);
    expect(unlinkOps(d, ['v3'])).toEqual([]);
  });
});

describe('R08.1 J/L cut', () => {
  const jl = () =>
    doc([
      track('V1', 'video', [media('v1', 100, 250, 0, { linkId: 'L1' })]),
      track('A1', 'audio', [media('a0', 0, 80), media('a1', 100, 250, 20, { linkId: 'L1' }), media('a2', 250, 300)]),
    ]);

  it('extends the sound earlier than the picture (J cut) without moving its neighbours or breaking the link', () => {
    const d = jl();
    const after = roundTrip(d, jlCutOps(d, 'a1', 'in', -10));
    expect(get(after, 'a1')).toMatchObject({ recordIn: 90, recordOut: 250, sourceIn: 10, linkId: 'L1' });
    expect(get(after, 'v1').recordIn).toBe(100);
    expect(get(after, 'a2').recordIn).toBe(250);
  });

  it('shortens the sound head later (L cut) and keeps followers where they were', () => {
    const d = jl();
    const after = roundTrip(d, jlCutOps(d, 'a1', 'in', 15));
    expect(get(after, 'a1')).toMatchObject({ recordIn: 115, recordOut: 250, sourceIn: 35 });
    expect(get(after, 'a2').recordIn).toBe(250);
  });

  it('extends the tail into free space, and refuses (empty) when a neighbour is in the way', () => {
    const free = doc([
      track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' })]),
      track('A1', 'audio', [media('a1', 0, 100, 0, { linkId: 'L1' }), media('a2', 140, 200)]),
    ]);
    const after = roundTrip(free, jlCutOps(free, 'a1', 'out', 20));
    expect(get(after, 'a1').recordOut).toBe(120);
    expect(get(after, 'a2').recordIn).toBe(140);
    expect(jlCutOps(jl(), 'a1', 'out', 20)).toEqual([]);
  });
});

describe('R08.1 migration', () => {
  const probes = new Map<string, boolean | null>([[assetRef(1).projectFileId, true], [assetRef(2).projectFileId, false]]);

  it('links existing same-asset same-span pairs and adds missing companions; idempotent', () => {
    const d = doc([
      track('V1', 'video', [media('v1', 0, 100), media('v2', 100, 200, 0, { assetRef: assetRef(2) }), media('v3', 200, 260, 0, { assetRef: assetRef(3) })]),
      track('A1', 'audio', [media('a1', 0, 100)]),
    ]);
    let n = 0;
    const plan = migrateToLinkedAudio(d, probes, () => `id-${++n}`);
    expect(plan).toMatchObject({ linked: 1, added: 0 });
    expect(plan.summary).toBe('Linked 1 clip.'.replace('clip.', 'clip.'));
    const withAudio = doc([track('V1', 'video', [media('v1', 0, 100), media('v4', 100, 150, 5)]), track('A1', 'audio', [media('a1', 0, 100)])]);
    const s = store(withAudio);
    const both = migrateToLinkedAudio(withAudio, probes, () => `g-${++n}`);
    expect(both).toMatchObject({ linked: 1, added: 1 });
    expect(both.summary).toBe('Linked 1 clip, added sound for 1.');
    s.dispatchBatch(both.ops, 'Link');
    const migrated = s.getState().doc;
    assertInvariants(migrated, { ids: 'opaque' });
    expect(groupOf(migrated, 'v1').map((m) => m.item.id).sort()).toEqual(['a1', 'v1']);
    expect(groupOf(migrated, 'v4')).toHaveLength(2);
    const again = migrateToLinkedAudio(migrated, probes, () => `h-${++n}`);
    expect(again.ops).toEqual([]);
    expect(again.summary).toBe('Nothing to link.');
    s.undo();
    expect(s.getState().doc).toEqual(withAudio);
  });

  it('adds no companion when the source has no audio or the probe is unknown', () => {
    const d = doc([track('V1', 'video', [media('v2', 0, 100, 0, { assetRef: assetRef(2) }), media('v3', 100, 160, 0, { assetRef: assetRef(3) })])]);
    expect(migrateToLinkedAudio(d, probes).ops).toEqual([]);
  });

  it('creates a Camera audio track when none exists, and two companions share it when they fit', () => {
    const d = doc([track('V1', 'video', [media('v1', 0, 100), media('v4', 100, 150, 5)])]);
    const plan = migrateToLinkedAudio(d, probes);
    const s = store(d);
    s.dispatchBatch(plan.ops, 'Link');
    const after = s.getState().doc;
    assertInvariants(after, { ids: 'opaque' });
    expect(after.tracks.filter((t) => t.kind === 'audio')).toHaveLength(1);
    expect(after.tracks.find((t) => t.kind === 'audio')?.items).toHaveLength(2);
    expect(plan.added).toBe(2);
  });
});

describe('R08.1 round-trip property', () => {
  it('any link-aware batch on a random run of linked pairs is valid and undoes exactly', () => {
    const build = (lengths: number[]): EditTimeline => {
      let at = 0;
      const v: MediaClip[] = [];
      const a: MediaClip[] = [];
      lengths.forEach((len, i) => {
        v.push(media(`v${i}`, at, at + len, 30, { linkId: `L${i}` }));
        a.push(media(`a${i}`, at, at + len, 30, { linkId: `L${i}` }));
        at += len;
      });
      return doc([track('V1', 'video', v), track('A1', 'audio', a)]);
    };
    fc.assert(
      fc.property(fc.array(fc.integer({ min: 20, max: 90 }), { minLength: 1, maxLength: 5 }), fc.nat(4), fc.integer({ min: 1, max: 18 }), fc.integer({ min: 0, max: 4 }), (lengths, pick, amount, kind) => {
        const d = build(lengths);
        const id = `v${pick % lengths.length}`;
        const target = get(d, id);
        let ops: EditOp[];
        switch (kind) {
          case 0: ops = linkedOpsForNudge(d, [id], amount); break;
          case 1: ops = linkedOpsForTrim(d, id, 'out', 'ripple', -amount); break;
          case 2: ops = linkedOpsForBlade(d, id, target.recordIn + amount, 'fresh-right').ops; break;
          case 3: ops = linkedOpsForDelete(d, [id], 'ripple').ops; break;
          default: ops = linkedOpsForRetime(d, id, 2, target.recordIn + Math.max(1, Math.round((target.recordOut - target.recordIn) / 2))); break;
        }
        const s = store(d);
        try {
          s.dispatchBatch(ops, 'p');
        } catch (error) {
          // A refusal is fine (e.g. nudging past a neighbour) but it must leave the document untouched.
          expect(error).toBeInstanceOf(EditEngineError);
          expect(s.getState().doc).toEqual(d);
          return;
        }
        assertInvariants(s.getState().doc, { ids: 'opaque' });
        if (ops.length > 0) {
          s.undo();
          expect(s.getState().doc).toEqual(d);
        }
      }),
      { numRuns: 120 },
    );
  });
});
