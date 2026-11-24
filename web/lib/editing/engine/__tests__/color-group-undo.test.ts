/**
 * T-112 (QA-P1-20 residual): a colour group's restore must survive the undo stack.
 *
 * Undo replays each step's inverse program NEWEST-first. For the sequence [group a clip, remove the
 * clip, remove the group] that replays the group's restore BEFORE the inverse that brings the clip
 * back, so the restored `setColorGroup` names a clip the document does not currently hold as a media
 * clip. The engine tolerates a group outliving its clips (`assertColorGroups`), so its own restore
 * must not be refused by the write-time membership rule — while a fresh edit that NAMES a clip which
 * does not exist still is.
 *
 * Every case here fails on the pre-fix engine: the restore throws
 * `setColorGroup: colorGroups[0].clipIds[0] '<id>' is not a media clip of this timeline`.
 */

import { describe, expect, it } from 'vitest';
import { apply, EditEngineError } from '../index';
import type { EditTimeline, ColorGroup } from '../../types';
import type { EditOp } from '../index';
import { doc, media, title, track, TRACKS } from '../../__tests__/fixtures';

/** Two media clips on one track — the group names the first, the second keeps the track non-empty. */
const twoClips = (): EditTimeline =>
  doc([track(TRACKS.v1, 'video', [media('c1', 0, 60), media('c2', 60, 120)])]);

const group = (clipIds: string[] = ['c1']): ColorGroup => ({ id: 'g1', name: 'G', clipIds, grade: null });

/** Replays a stack of inverse programs the way `undo()` does — newest entry first. */
function replayInverses(doc0: EditTimeline, programs: readonly (readonly EditOp[])[]): EditTimeline {
  let current = doc0;
  for (let step = programs.length - 1; step >= 0; step--) {
    // Replay as an INVERSE program: that is what grants T-112's restore exemption (never the op).
    for (const op of programs[step]) current = apply(current, op, { inverse: true }).doc;
  }
  return current;
}

describe('T-112: undoing [group a clip, remove the clip, remove the group] restores the document', () => {
  it('a lifted member: the group restore runs before the clip is back and still applies', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });
    const removed = apply(lifted.doc, { type: 'setColorGroup', groupId: 'g1', group: undefined });

    const undone = replayInverses(removed.doc, [added.inverse, lifted.inverse, removed.inverse]);
    expect(undone).toStrictEqual(start);
  });

  it('a replaced member (mediaClip -> titleClip, the exact seed-36 shape)', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const overwritten = apply(added.doc, { type: 'overwrite', itemId: 'c1', item: title('c1', 0, 60) });
    const removed = apply(overwritten.doc, { type: 'setColorGroup', groupId: 'g1', group: undefined });

    const undone = replayInverses(removed.doc, [added.inverse, overwritten.inverse, removed.inverse]);
    expect(undone).toStrictEqual(start);
  });

  it('a replace that keeps a dangling member round-trips through its own inverse', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });
    // Re-setting the now-dangling group (a rename/regrade) is a legal edit…
    const regraded = apply(lifted.doc, { type: 'setColorGroup', groupId: 'g1', group: { ...group(), name: 'G2' } });
    expect(regraded.doc.colorGroups?.[0]).toMatchObject({ name: 'G2', clipIds: ['c1'] });

    // …and its inverse restores the pre-edit group, dangling member and all.
    const undone = replayInverses(regraded.doc, [regraded.inverse]);
    expect(undone).toStrictEqual(lifted.doc);
  });
});

describe('T-112: the write-time membership rule still refuses a member that does not exist', () => {
  it('a fresh set naming a clip that is not in the document is refused', () => {
    const start = twoClips();
    expect(() => apply(start, { type: 'setColorGroup', groupId: 'g1', group: group(['nope']) })).toThrow(EditEngineError);
    expect(() => apply(start, { type: 'setColorGroup', groupId: 'g1', group: group(['nope']) })).toThrow(/not a media clip/);
  });

  it('a fresh replace may keep a dangling member but not add a new one', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });

    expect(() =>
      apply(lifted.doc, { type: 'setColorGroup', groupId: 'g1', group: { ...group(), clipIds: ['c1', 'nope'] } }),
    ).toThrow(/not a media clip/);
  });

  it('a group that already dangles does not block an unrelated edit', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });

    const second = apply(lifted.doc, { type: 'setColorGroup', groupId: 'g2', group: { id: 'g2', name: 'H', clipIds: ['c2'], grade: null } });
    expect(second.doc.colorGroups?.map((g) => g.id)).toEqual(['g1', 'g2']);
  });
});

describe('T-112: the restore exemption comes from the apply path, never from the op', () => {
  it('an inverse program may restore a dangling group; the same op applied forward is refused', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });
    const danglingRemoval = apply(lifted.doc, { type: 'setColorGroup', groupId: 'g1', group: undefined });

    // The inverse op carries nothing special: no field a caller could forge.
    expect(danglingRemoval.inverse[0]).not.toHaveProperty('restore');
    expect(danglingRemoval.inverse[0]).toMatchObject({ type: 'setColorGroup', groupId: 'g1' });

    // Replayed as an inverse program, it is honoured (the clip comes back afterwards).
    const restored = replayInverses(lifted.doc, [[...danglingRemoval.inverse]]);
    expect(restored.colorGroups?.map((g) => g.id)).toEqual(['g1']);

    // Applied FORWARD — what a wire op, a proposal or a queued op does — the same op is refused:
    // the group it re-sets names a clip the document no longer holds, and `previous` is undefined.
    const forged = danglingRemoval.inverse[0] as EditOp;
    expect(() => apply(danglingRemoval.doc, forged)).toThrow(EditEngineError);
  });

  it('a caller cannot forge the exemption by decorating the op', () => {
    const start = twoClips();
    const added = apply(start, { type: 'setColorGroup', groupId: 'g1', group: group() });
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });
    const removed = apply(lifted.doc, { type: 'setColorGroup', groupId: 'g1', group: undefined });

    const forged = { type: 'setColorGroup', groupId: 'g1', group: group(), restore: true } as unknown as EditOp;

    expect(() => apply(removed.doc, forged)).toThrow(EditEngineError);
  });
});
