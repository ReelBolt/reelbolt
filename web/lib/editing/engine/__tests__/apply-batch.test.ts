/**
 * The batch path (one working copy for a whole program) must be indistinguishable from folding `apply`
 * op by op: same document, same inverse programs in the same order, same failure, input untouched.
 */

import { describe, expect, it } from 'vitest';

import { apply, applyAll, applyBatch, ApplySession, assertInvariants, EditEngineError, type EditOp } from '../index';
import { doc, media, title, track } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';

const opaque = (d: EditTimeline): void => assertInvariants(d, { ids: 'opaque' });

const transition = { id: 'x1', type: 'transition' as const, name: 'Dissolve', transitionType: 'dissolve', easing: 'linear', enabled: true, provenance: null };

function base(): EditTimeline {
  return doc([
    track('v1', 'video', [media('c1', 0, 100, 0), media('c2', 100, 200, 100), media('c3', 200, 300, 200), media('c4', 300, 400, 300)]),
    track('a1', 'audio', [media('s1', 0, 200, 0), media('s2', 200, 400, 200)]),
    track('t1', 'video', [title('ti1', 50, 150)]),
  ]);
}

const mixed: EditOp[] = [
  { type: 'blade', itemId: 'c1', atFrame: 40, newItemId: 'c1b' },
  { type: 'trim', itemId: 'c2', edge: 'out', mode: 'ripple', deltaFrames: -10 },
  { type: 'setProperty', itemId: 'c3', key: 'enabled', value: false },
  { type: 'move', itemId: 'ti1', toTrackId: 't1', toRecordIn: 60 },
  { type: 'extract', itemId: 'c1b' },
  { type: 'blade', itemId: 's1', atFrame: 80, newItemId: 's1b' },
  { type: 'lift', itemId: 's1b' },
];

const withTransitions: EditOp[] = [
  { type: 'addTransition', afterItemId: 'c1', transition, framesBefore: 2, framesAfter: 2 },
  { type: 'blade', itemId: 'c2', atFrame: 150, newItemId: 'c2b' },
  { type: 'blade', itemId: 'c1', atFrame: 50, newItemId: 'c1b' },
  { type: 'extract', itemId: 'c3' },
  { type: 'setProperty', itemId: 'c4', key: 'name', value: 'Renamed' },
];

function fold(start: EditTimeline, ops: readonly EditOp[]): { doc: EditTimeline; inverses: EditOp[][] } {
  let current = start;
  const inverses: EditOp[][] = [];
  for (const op of ops) {
    const r = apply(current, op);
    current = r.doc;
    inverses.push(r.inverse);
  }
  return { doc: current, inverses };
}

describe('applyBatch', () => {
  for (const [name, ops] of [['a mixed program', mixed], ['a program with transitions near the cuts', withTransitions]] as const) {
    it(`matches folding apply for ${name}: document and every inverse`, () => {
      const start = base();
      const before = structuredClone(start);
      const expected = fold(start, ops);
      const got = applyBatch(start, ops);
      expect(got.doc).toEqual(expected.doc);
      expect(got.inverses).toEqual(expected.inverses);
      expect(applyAll(start, ops)).toEqual(expected.doc);
      expect(start).toEqual(before);
      opaque(got.doc);
    });

    it(`undoes ${name} by replaying the inverses back to front`, () => {
      const start = base();
      const got = applyBatch(start, ops);
      let back = got.doc;
      for (let i = got.inverses.length - 1; i >= 0; i--) back = applyAll(back, got.inverses[i], { inverse: true });
      expect(back).toEqual(start);
    });
  }

  it('keeps the inverse programs from aliasing the working copy', () => {
    const start = base();
    const session = new ApplySession(start);
    const inverse = session.apply({ type: 'blade', itemId: 'c1', atFrame: 40, newItemId: 'c1b' });
    const frozen = structuredClone(inverse);
    session.apply({ type: 'trim', itemId: 'c1b', edge: 'out', mode: 'ripple', deltaFrames: -5 });
    session.apply({ type: 'extract', itemId: 'c2' });
    expect(inverse).toEqual(frozen);
  });

  it('is all or nothing: op k failing throws the same error as folding, and the input is untouched', () => {
    const start = base();
    const before = structuredClone(start);
    const bad: EditOp[] = [mixed[0], mixed[1], { type: 'blade', itemId: 'no-such-clip', atFrame: 10, newItemId: 'zz' }, mixed[2]];
    let folded: unknown;
    try {
      fold(start, bad);
    } catch (error) {
      folded = error;
    }
    expect(folded).toBeInstanceOf(EditEngineError);
    expect(() => applyBatch(start, bad)).toThrow((folded as Error).message);
    expect(start).toEqual(before);
    const session = new ApplySession(start);
    expect(() => session.apply(bad[2])).toThrow(EditEngineError);
    expect(() => session.apply(bad[0])).toThrow(/discarded/);
  });

  it('returns the very document for an empty program', () => {
    const start = base();
    expect(applyAll(start, [])).toBe(start);
    expect(applyBatch(start, []).inverses).toEqual([]);
  });
});
