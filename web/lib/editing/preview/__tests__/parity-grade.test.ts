import { describe, expect, it } from 'vitest';
import { classifyParity } from '../parity';
import { GRADE_NODE_BOUND, GRADE_NODE_MAX } from '../../color/preview-grade';
import { doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, GradeNode } from '../../types';

const withNodes = (nodes: GradeNode[]): EditTimeline => {
  const d = doc([track('v1', 'video', [media('c1', 0, 60)])]);
  return { ...d, colorGroups: [{ id: 'g1', name: 'Group', clipIds: ['c1'], grade: { enabled: true, nodes } }] };
};
const node = (i: number): GradeNode => ({ id: `n${i}`, name: `n${i}`, enabled: true, primaries: { lift: { r: 0.1 }, tint: 0.3, hue: 10, saturation: 1.2, temperature: 0.2, exposure: 0.3 } }) as GradeNode;
const parityOf = (d: EditTimeline) => classifyParity(d).items.find((i) => i.itemId === 'c1')!.parity;

describe('primaries parity', () => {
  it('enabled primaries nodes (every ported field) are faithful', () => expect(parityOf(withNodes([node(1), node(2)]))).toBe('faithful'));
  it('more nodes than the single-draw shader holds run in the grade pass: still faithful', () => {
    expect(parityOf(withNodes(Array.from({ length: GRADE_NODE_BOUND + 1 }, (_, i) => node(i))))).toBe('faithful');
    expect(parityOf(withNodes(Array.from({ length: GRADE_NODE_MAX }, (_, i) => node(i))))).toBe('faithful');
  });
  it('more than 16 nodes fold the rest into one approximate step', () => {
    expect(parityOf(withNodes(Array.from({ length: GRADE_NODE_MAX + 1 }, (_, i) => node(i))))).toBe('approximate');
  });
  it('curves and secondaries are faithful', () => {
    const curves = { id: 'c', curves: { y: [{ x: 0.5, y: 0.6 }] } } as GradeNode;
    const secondary = { ...node(1), qualifier: { hueCenter: 20, hueWidth: 30 }, window: { shape: { kind: 'rect' } } } as GradeNode;
    expect(parityOf(withNodes([curves, secondary]))).toBe('faithful');
  });
});
