import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { isReconcilable, newLayerIds, reconciledText } from './reconcile';

describe('reconcile after a failed run', () => {
  it('treats a 424, a gateway answer and a dropped connection as worth a re-read', () => {
    expect(isReconcilable(new ApiError('x', 424))).toBe(true);
    expect(isReconcilable(new ApiError('x', 504))).toBe(true);
    expect(isReconcilable(new TypeError('Failed to fetch'))).toBe(true);
    expect(isReconcilable(new ApiError('x', 402))).toBe(false);
    expect(isReconcilable(new ApiError('x', 422))).toBe(false);
  });
  it('finds only the layers that were not there before', () => {
    expect(newLayerIds(['a', 'b'], [{ id: 'a' }, { id: 'b' }, { id: 'late' }])).toEqual(['late']);
    expect(newLayerIds(['a'], [{ id: 'a' }])).toEqual([]);
  });
  it('says plainly whether it finished', () => {
    expect(reconciledText('Upscale', 1)).toContain('charged once');
    expect(reconciledText('Upscale', 0)).toContain('nothing was added');
  });
});
