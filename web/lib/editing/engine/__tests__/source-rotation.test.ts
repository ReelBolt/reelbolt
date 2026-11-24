import { describe, expect, it } from 'vitest';

import { standardDoc } from '../../__tests__/fixtures';
import type { MediaClip } from '../../types';
import { apply, applyAll, EditEngineError } from '../apply';
import { SET_PROPERTY_ALLOWLIST, type EditOp } from '../ops';
import { buildRotateOps, nextSourceRotation, orientedSourceSize, clipSheetInfoOf } from '../../../../components/editor/phone/clip-sheet-actions';

const set = (value: unknown): EditOp => ({ type: 'setProperty', itemId: 'c1', key: 'sourceRotation', value } as EditOp);
const clipOf = (d: ReturnType<typeof standardDoc>): MediaClip => d.tracks.flatMap((t) => t.items).find((i) => i.id === 'c1') as MediaClip;

describe('sourceRotation via setProperty', () => {
  it('is allow-listed on a media clip', () => {
    expect(SET_PROPERTY_ALLOWLIST.mediaClip).toContain('sourceRotation');
  });
  it('sets 90, and undo restores the document', () => {
    const base = standardDoc();
    const r = apply(base, set(90));
    expect(clipOf(r.doc).sourceRotation).toBe(90);
    expect(clipOf(applyAll(r.doc, r.inverse, { inverse: true })).sourceRotation).toBeUndefined();
  });
  it('clears with undefined', () => {
    const r = apply(apply(standardDoc(), set(270)).doc, set(undefined));
    expect(clipOf(r.doc).sourceRotation).toBeUndefined();
  });
  it.each([45, '90', -90, 360, true])('refuses %j', (bad) => {
    expect(() => apply(standardDoc(), set(bad))).toThrow(EditEngineError);
  });
});

describe('phone rotate action', () => {
  it('cycles 0 -> 90 -> 180 -> 270 -> 0', () => {
    expect([undefined, 90, 180, 270, 0].map((r) => nextSourceRotation(r))).toEqual([90, 180, 270, 0, 90]);
  });
  it('emits one setProperty, clearing at 270', () => {
    let d = standardDoc();
    const seen: unknown[] = [];
    for (let i = 0; i < 4; i++) {
      const r = buildRotateOps(clipSheetInfoOf(d, clipOf(d), 0));
      if (!r.ok) throw new Error(r.reason);
      expect(r.ops).toHaveLength(1);
      d = apply(d, r.ops[0]!).doc;
      seen.push(clipOf(d).sourceRotation);
    }
    expect(seen).toEqual([90, 180, 270, undefined]);
  });
  it('swaps the source size at 90 and 270 only', () => {
    expect(orientedSourceSize({ width: 1920, height: 1080 }, 90)).toEqual({ width: 1080, height: 1920 });
    expect(orientedSourceSize({ width: 1920, height: 1080 }, 180)).toEqual({ width: 1920, height: 1080 });
    expect(orientedSourceSize(null, 90)).toBeNull();
  });
});
