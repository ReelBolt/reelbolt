import { describe, expect, it } from 'vitest';
import { layerTransformAt } from '@/lib/editing/preview/scheduler';
import type { MediaClip } from '@/lib/editing/types';
import { scaleYOf, transformAt } from '..';

const clip = { transform: { scale: { value: 2 }, scaleY: { value: 0 } } } as unknown as MediaClip;

describe('a stored scaleY of 0 means no value (uniform)', () => {
  it('the viewer scales uniformly', () => {
    expect(layerTransformAt(clip, 0).scaleY).toBe(2);
  });
  it('the handles read it as uniform too', () => {
    const v = transformAt(clip, 0);
    expect(v.scaleY).toBeUndefined();
    expect(scaleYOf({ positionX: 0, positionY: 0, scale: 2, scaleY: 0, rotation: 0 })).toBe(2);
  });
});
