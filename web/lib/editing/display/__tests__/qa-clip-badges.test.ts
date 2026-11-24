import { describe, expect, it } from 'vitest';
import { apply, type EditOp } from '@/lib/editing/engine';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { clipBadges, layoutBadges } from '../clip-badges';
import { classifyItem } from '@/lib/editing/preview/parity';
import { badgeParity } from '../clip-badges';
import { registerJobMark, clearJobMark, getExternalJobMarks } from '@/lib/editing/background/job-marks';
import { jobsHolding } from '@/lib/editing/background/edit-guard';

const applyAll = (d: EditTimeline, ops: readonly EditOp[]) => {
  const inverses: EditOp[][] = [];
  let next = d;
  for (const op of ops) { const r = apply(next, op); next = r.doc; inverses.push(r.inverse); }
  let back = next;
  for (const inv of inverses.reverse()) for (const op of inv) back = apply(back, op).doc;
  return { next, back };
};

describe('QA: undo of every badge remove/toggle restores the clip', () => {
  const cases: [string, Partial<MediaClip>][] = [
    ['motion', { transform: { scale: { value: 1.2 } } }],
    ['crop', { crop: { left: 0.1 } }],
    ['speed 2x', { playbackRate: 2 }],
    ['speed 0.5x of', { playbackRate: 0.5, retimeQuality: 'opticalFlow' }],
    ['reverse', { reverse: true }], ['hold', { hold: true }],
    ['colour', { grade: { enabled: true, nodes: [{ id: 'n1', name: 'n', enabled: true } as never] }, inputProfile: 'appleLog' }],
    ['mask', { mask: { kind: 'rect' } as never }],
    ['keyer', { keyer: { color: '#00ff00' } as never }],
    ['effects', { effects: [{ type: 'blur', amount: 0.5 }] }],
    ['stabilize', { stabilize: true }],
    ['reframe', { reframe: { x: 0.5 } as never }],
    ['cornerPin', { cornerPin: { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } } as never }],
  ];
  it.each(cases)('%s', (_n, extra) => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, extra)])]);
    for (const b of clipBadges(d.tracks[0]!.items[0]!, d)) {
      for (const ops of [b.remove, ...(b.toggle ? [b.toggle] : [])]) {
        const { back } = applyAll(d, ops);
        expect(back.tracks[0]!.items[0]).toEqual(d.tracks[0]!.items[0]);
      }
    }
  });
});

describe('QA: speed remove next to a neighbour', () => {
  it('2x clip followed by a clip: removing speed (clip doubles) - what happens to the neighbour', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 2 }), media('c2', 60, 120, 500)])]);
    const b = clipBadges(d.tracks[0]!.items[0]!, d).find((x) => x.kind === 'speed')!;
    let outcome: string;
    try {
      const { next } = applyAll(d, b.remove);
      outcome = JSON.stringify(next.tracks[0]!.items.map((i) => [i.id, i.recordIn, i.recordOut]));
    } catch (e) {
      outcome = `refused: ${(e as Error).message}`;
    }
    console.log('SPEED-REMOVE-NEIGHBOUR', outcome);
    expect(outcome).toBeDefined();
  });
});

describe('QA: colour toggle state with a camera profile', () => {
  it('after "turn off" the badge must read as off (else the menu offers "Turn off" again and re-enables)', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { grade: { enabled: true, nodes: [{ id: 'n1', name: 'n', enabled: true } as never] }, inputProfile: 'appleLog' })])]);
    const b = clipBadges(d.tracks[0]!.items[0]!, d).find((x) => x.kind === 'colour')!;
    const off = applyAll(d, b.toggle!).next;
    const after = clipBadges(off.tracks[0]!.items[0]!, off).find((x) => x.kind === 'colour')!;
    expect((off.tracks[0]!.items[0] as MediaClip).grade?.enabled).toBe(false);
    expect(after.enabled).toBe(false);
  });
});

describe('QA: motion badge remove also clears opacity', () => {
  it('a clip whose only transform is 50% opacity shows no Motion badge', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { transform: { opacity: { value: 0.5 } } })])]);
    expect(clipBadges(d.tracks[0]!.items[0]!, d).map((b) => b.kind)).toEqual([]);
  });
  it('Remove motion keeps the opacity', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { transform: { scale: { value: 1.3 }, rotation: { value: 5 }, opacity: { value: 0.5 } } })])]);
    const b = clipBadges(d.tracks[0]!.items[0]!, d)[0]!;
    expect(b.kind).toBe('motion');
    const { next } = applyAll(d, b.remove);
    expect((next.tracks[0]!.items[0] as MediaClip).transform).toEqual({ opacity: { value: 0.5 } });
  });
});

describe('QA: parity dots', () => {
  it('crop-only clip: crop badge carries the transform finding', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { crop: { left: 0.1 } })])]);
    const item = d.tracks[0]!.items[0]!;
    const f = classifyItem(item, d).findings;
    const b = clipBadges(item, d)[0]!;
    expect(badgeParity(b, f).parity).toBe('approximate');
  });
  it('speed+mask+keyer: each badge gets its own class', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 2, mask: { kind: 'rect' } as never, keyer: { color: '#0f0' } as never })])]);
    const item = d.tracks[0]!.items[0]!;
    const f = classifyItem(item, d).findings;
    const got = Object.fromEntries(clipBadges(item, d).map((b) => [b.kind, badgeParity(b, f).parity]));
    expect(got).toEqual({ speed: 'approximate', mask: 'approximate', keyer: 'faithful' });
  });
});

describe('QA: badge row on the small (40px) track preset', () => {
  it('bodyH = 40 - 8 = 32: a +N chip in the label band; the 20 px collapsed track shows nothing', () => {
    expect(layoutBadges(['crop'], 0, 300, 4, 32)).toEqual([expect.objectContaining({ kind: 'more', count: 1 })]);
    expect(layoutBadges(['crop'], 0, 300, 4, 12)).toEqual([]);
    expect(layoutBadges(['crop'], 0, 300, 4, 56).length).toBe(1);
  });
});

describe('QA: follow job vs its own result write', () => {
  it('the follow result (setProperty transform on the same clip) is not held by the follow mark; a user edit still is', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60)])]);
    registerJobMark({ id: 'follow:c1', jobKind: 'follow', clipIds: ['c1'], state: 'running', label: 'Following' });
    try {
      const write = { type: 'setProperty', itemId: 'c1', key: 'transform', value: { scale: { value: 1.1 } } } as EditOp;
      expect(jobsHolding([write], d, [], getExternalJobMarks(), new Set(), 'follow:c1').marks.map((m) => m.id)).toEqual([]);
      expect(jobsHolding([write], d, [], getExternalJobMarks(), new Set()).marks.map((m) => m.id)).toEqual(['follow:c1']);
    } finally {
      clearJobMark('follow:c1');
    }
  });
});

describe('QA: speed remove ripple across tracks', () => {
  it('other tracks after the clip', () => {
    const d = doc([
      track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 2 }), media('c2', 60, 120, 500)]),
      track('a1', 'audio', [media('m1', 0, 60), media('m2', 60, 120, 300)]),
    ]);
    const b = clipBadges(d.tracks[0]!.items[0]!, d).find((x) => x.kind === 'speed')!;
    const { next } = applyAll(d, b.remove);
    console.log('SPEED-REMOVE-TRACKS', JSON.stringify(next.tracks.map((t) => t.items.map((i) => [i.id, i.recordIn, i.recordOut]))));
  });
});
