import { describe, expect, it } from 'vitest';
import fixture from '../__fixtures__/grade-parity.json';
import { applyPreviewGradeNodes, GRADE_NODE_BOUND, previewGradeNodesOf, TINT_SUMS_WITH_GAMMA_GREEN } from '../preview-grade';
import type { GradeStack } from '../../types';

type Case = { name: string; stack: GradeStack; out: number[][] };
const cases = fixture.cases as unknown as Case[];
const patches = fixture.patches as number[][];

/** Max |CPU - ffmpeg| per case, in 8-bit steps. */
function maxDiff(c: Case): number {
  const nodes = previewGradeNodesOf([c.stack]);
  let m = 0;
  patches.forEach((p, i) => {
    const got = applyPreviewGradeNodes(nodes, [p[0] / 255, p[1] / 255, p[2] / 255]);
    for (let k = 0; k < 3; k++) m = Math.max(m, Math.abs(got[k] * 255 - c.out[i][k]));
  });
  return m;
}

describe('viewer grade = export maths (ffmpeg fixture)', () => {
  it('uses the merged tint + gamma green', () => expect(TINT_SUMS_WITH_GAMMA_GREEN).toBe(true));
  it.each(cases.map((c) => [c.name, maxDiff(c).toFixed(2), c] as const))('%s: max %s/255 vs ffmpeg, within 3', (_n, _d, c) => {
    expect(maxDiff(c)).toBeLessThanOrEqual(3);
  });

  it('12 nodes differ from "first 8 only": the rest fold into one last stage', () => {
    const stack: GradeStack = { enabled: true, nodes: Array.from({ length: 12 }, (_, i) => ({ id: `n${i}`, enabled: true, primaries: { exposure: 0.1, saturation: 1.05 } })) };
    const nodes = previewGradeNodesOf([stack]);
    expect(nodes).toHaveLength(12);
    const all = applyPreviewGradeNodes(nodes, [0.3, 0.4, 0.5]);
    const first8 = applyPreviewGradeNodes(nodes.slice(0, GRADE_NODE_BOUND), [0.3, 0.4, 0.5]);
    expect(Math.abs(all[0] - first8[0]) + Math.abs(all[1] - first8[1])).toBeGreaterThan(0.02);
  });
});
