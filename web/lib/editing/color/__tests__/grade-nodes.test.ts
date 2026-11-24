import { describe, expect, it } from 'vitest';
import type { AdjustmentClip, ColorGroup, GradeNode, GradeStack } from '../../types';
import { MAX_GRADE_NODES } from '../../engine/ops';
import { doc, media, track } from '../../__tests__/fixtures';
import { effectiveGrade } from '../grade';
import {
  canAddNode, chainNodes, cleanNodeName, duplicateNode, duplicateNodeName, gradeChain, isIdentityNode, moveNode, mutedStack, NODE_NAME_MAX, nodeCarries, renameNode, reorderNodes, resetNode, soloStack,
} from '../grade-nodes';

const node = (id: string, extra: Partial<GradeNode> = {}): GradeNode => ({ id, name: id, enabled: true, primaries: {}, curves: null, lut: null, ...extra });
const stackOf = (...ids: string[]): GradeStack => ({ enabled: true, nodes: ids.map((id) => node(id)) });
const ids = (stack: GradeStack | null) => (stack?.nodes ?? []).map((n) => n.id);

describe('cleanNodeName', () => {
  it('trims, collapses whitespace and drops control characters', () => {
    expect(cleanNodeName('  Skin \t tone\n')).toBe('Skin tone');
    expect(cleanNodeName('a\u0000b\u007fc')).toBe('a b c');
  });

  it('returns null for an empty name and cuts a long one', () => {
    expect(cleanNodeName('   ')).toBeNull();
    expect(cleanNodeName('\n\t')).toBeNull();
    expect(Array.from(cleanNodeName('x'.repeat(200)) ?? '')).toHaveLength(NODE_NAME_MAX);
  });
});

describe('renameNode', () => {
  it('renames one node and keeps everything else, including the stack switch', () => {
    const stack: GradeStack = { enabled: false, nodes: [node('a'), node('b', { primaries: { exposure: 1 } })] };
    const next = renameNode(stack, 'b', '  Sky  ');
    expect(next).toEqual({ enabled: false, nodes: [node('a'), node('b', { name: 'Sky', primaries: { exposure: 1 } })] });
    // The input is not mutated.
    expect(stack.nodes?.[1].name).toBe('b');
  });

  it('writes nothing for an empty or unchanged name, or an unknown node', () => {
    const stack = stackOf('a');
    expect(renameNode(stack, 'a', '   ')).toBeNull();
    expect(renameNode(stack, 'a', 'a')).toBeNull();
    expect(renameNode(stack, 'zz', 'New')).toBeNull();
  });
});

describe('reorder', () => {
  it('moves a node to another position, in one stack', () => {
    expect(ids(reorderNodes(stackOf('a', 'b', 'c'), 0, 2))).toEqual(['b', 'c', 'a']);
    expect(ids(reorderNodes(stackOf('a', 'b', 'c'), 2, 0))).toEqual(['c', 'a', 'b']);
    expect(ids(reorderNodes(stackOf('a', 'b', 'c'), 0, 99))).toEqual(['b', 'c', 'a']);
  });

  it('returns null when nothing moves', () => {
    expect(reorderNodes(stackOf('a', 'b'), 1, 1)).toBeNull();
    expect(reorderNodes(stackOf('a', 'b'), 5, 0)).toBeNull();
    expect(reorderNodes(null, 0, 1)).toBeNull();
  });

  it('moves up and down by one and stops at the ends', () => {
    expect(ids(moveNode(stackOf('a', 'b', 'c'), 'b', -1))).toEqual(['b', 'a', 'c']);
    expect(ids(moveNode(stackOf('a', 'b', 'c'), 'b', 1))).toEqual(['a', 'c', 'b']);
    expect(moveNode(stackOf('a', 'b', 'c'), 'a', -1)).toBeNull();
    expect(moveNode(stackOf('a', 'b', 'c'), 'c', 1)).toBeNull();
    expect(moveNode(stackOf('a'), 'nope', 1)).toBeNull();
  });
});

describe('duplicateNode', () => {
  it('places a deep copy right after the original under a new id and name', () => {
    const stack: GradeStack = { enabled: true, nodes: [node('a', { primaries: { gain: { r: 0.2 } } }), node('b')] };
    const next = duplicateNode(stack, 'a', 'a2', 'Copy');
    expect(ids(next)).toEqual(['a', 'a2', 'b']);
    expect(next?.nodes?.[1]).toEqual(node('a2', { name: 'Copy', primaries: { gain: { r: 0.2 } } }));
    expect(next?.nodes?.[1].primaries).not.toBe(stack.nodes?.[0].primaries);
  });

  it('refuses when the stack is full or the node is unknown', () => {
    const full = stackOf(...Array.from({ length: MAX_GRADE_NODES }, (_, i) => `n${i}`));
    expect(canAddNode(full)).toBe(false);
    expect(duplicateNode(full, 'n0', 'x', 'Copy')).toBeNull();
    expect(duplicateNode(stackOf('a'), 'zz', 'x', 'Copy')).toBeNull();
    expect(canAddNode(null)).toBe(true);
  });

  it('names a copy without any language in it', () => {
    expect(duplicateNodeName('Correction 1', 3)).toBe('Correction 4');
    expect(duplicateNodeName(undefined, 1)).toBe('Correction 2');
    expect(duplicateNodeName('Skin', 2)).toBe('Skin 2');
    expect(duplicateNodeName('Skin 2', 2)).toBe('Skin 3');
    expect(Array.from(duplicateNodeName('x'.repeat(NODE_NAME_MAX), 1)).length).toBeLessThanOrEqual(NODE_NAME_MAX);
  });
});

describe('resetNode', () => {
  it('returns the node to identity and keeps its id, name and switch', () => {
    const rich = node('a', {
      name: 'Sky',
      enabled: false,
      primaries: { exposure: 1 },
      curves: { y: [{ x: 0, y: 0 }, { x: 1, y: 0.8 }] },
      lut: { file: { kind: 'projectFile', projectFileId: 'f1' }, intensity: 1 },
      qualifier: { hueCenter: 20, hueWidth: 20 },
      window: { shape: { kind: 'ellipse' } },
    });
    const next = resetNode({ enabled: true, nodes: [rich, node('b')] }, 'a');
    expect(next?.nodes?.[0]).toEqual({ id: 'a', name: 'Sky', enabled: false, primaries: {}, curves: null, lut: null });
    expect(next?.nodes?.[1]).toEqual(node('b'));
    expect(isIdentityNode(next!.nodes![0])).toBe(true);
  });

  it('writes nothing when the node already changes nothing', () => {
    expect(resetNode(stackOf('a'), 'a')).toBeNull();
    expect(resetNode(stackOf('a'), 'zz')).toBeNull();
  });
});

describe('nodeCarries', () => {
  it('says what a correction holds', () => {
    expect(nodeCarries(node('a'))).toEqual({ primaries: false, curves: false, lut: false, qualifier: false, window: false });
    expect(nodeCarries(node('a', { primaries: { saturation: 1.2 }, window: { shape: {} } }))).toEqual({ primaries: true, curves: false, lut: false, qualifier: false, window: true });
    expect(nodeCarries(node('a', { curves: { r: [{ x: 0, y: 0 }, { x: 1, y: 1 }] }, lut: { file: { kind: 'projectFile', projectFileId: 'f' } }, qualifier: { hueCenter: 1, hueWidth: 2 } }))).toMatchObject({ curves: true, lut: true, qualifier: true });
  });
});

describe('soloStack', () => {
  it('switches the stack on with only the soloed node enabled, without touching the input', () => {
    const stack: GradeStack = { enabled: false, nodes: [node('a'), node('b', { enabled: false }), node('c')] };
    const solo = soloStack(stack, 'b');
    expect(solo?.enabled).toBe(true);
    expect(solo?.nodes?.map((n) => n.enabled)).toEqual([false, true, false]);
    expect(stack.enabled).toBe(false);
    expect(stack.nodes?.map((n) => n.enabled)).toEqual([true, false, true]);
  });

  it('is null for a node that is not in the stack', () => {
    expect(soloStack(stackOf('a'), 'zz')).toBeNull();
    expect(soloStack(null, 'a')).toBeNull();
  });

  it('mutes the other stack of the chain', () => {
    expect(mutedStack(stackOf('a'))).toEqual({ enabled: false, nodes: [node('a')] });
  });
});

describe('gradeChain', () => {
  const group = (grade: GradeStack | null, clipIds = ['c1']): ColorGroup => ({ id: 'g1', name: 'Day', clipIds, grade });
  const withGroups = (clipGrade: GradeStack | null, groups: ColorGroup[]) => ({ ...doc([track('v1', 'video', [media('c1', 0, 60, 0, { grade: clipGrade }), media('c2', 60, 120, 0)])]), colorGroups: groups });

  it('lists the group stack before the clip stack, in the order of effectiveGrade', () => {
    const d = withGroups(stackOf('c-a', 'c-b'), [group(stackOf('g-a', 'g-b'))]);
    const clip = d.tracks[0].items[0];
    if (clip.type !== 'mediaClip') throw new Error('fixture');
    const chain = gradeChain(d, clip, null);
    expect(chain.map((s) => s.owner.kind)).toEqual(['group', 'clip']);
    const expected = effectiveGrade(d, clip).flatMap((s) => s.nodes ?? []).map((n) => n.id);
    expect(chainNodes(chain).map((n) => n.node.id)).toEqual(expected);
    expect(expected).toEqual(['g-a', 'g-b', 'c-a', 'c-b']);
    expect(chainNodes(chain).map((n) => n.key)).toEqual(['group:g1:g-a', 'group:g1:g-b', 'clip:c1:c-a', 'clip:c1:c-b']);
  });

  it('keeps the same chain when the clip’s own group is the target', () => {
    const g = group(stackOf('g-a'));
    const d = withGroups(stackOf('c-a'), [g]);
    const clip = d.tracks[0].items[0];
    if (clip.type !== 'mediaClip') throw new Error('fixture');
    expect(chainNodes(gradeChain(d, clip, g)).map((n) => n.node.id)).toEqual(['g-a', 'c-a']);
  });

  it('shows a group alone when it is the target and the selected clip is not a member (or nothing is selected)', () => {
    const other: ColorGroup = { id: 'g2', name: 'Night', clipIds: ['c2'], grade: stackOf('n-a') };
    const d = withGroups(stackOf('c-a'), [group(stackOf('g-a')), other]);
    const clip = d.tracks[0].items[0];
    if (clip.type !== 'mediaClip') throw new Error('fixture');
    expect(chainNodes(gradeChain(d, clip, other)).map((n) => n.key)).toEqual(['group:g2:n-a']);
    expect(chainNodes(gradeChain(d, null, other)).map((n) => n.key)).toEqual(['group:g2:n-a']);
    expect(gradeChain(d, null, null)).toEqual([]);
  });

  it('marks a node off when it is disabled or its stack is bypassed', () => {
    const d = withGroups({ enabled: true, nodes: [node('c-a', { enabled: false }), node('c-b')] }, [group({ enabled: false, nodes: [node('g-a')] })]);
    const clip = d.tracks[0].items[0];
    if (clip.type !== 'mediaClip') throw new Error('fixture');
    const chain = gradeChain(d, clip, null);
    expect(chain[0].stackEnabled).toBe(false);
    expect(chainNodes(chain).map((n) => n.enabled)).toEqual([false, false, true]);
  });

  it('gives an adjustment clip only its own stack', () => {
    const adjustment: AdjustmentClip = { id: 'adj', type: 'adjustmentClip', name: 'Adjustment', recordIn: 0, recordOut: 30, enabled: true, grade: stackOf('x') };
    const d = { ...doc([track('v1', 'video', [adjustment])]), colorGroups: [group(stackOf('g-a'), ['adj'])] };
    expect(gradeChain(d, adjustment, null).map((s) => s.owner.kind)).toEqual(['clip']);
  });
});
