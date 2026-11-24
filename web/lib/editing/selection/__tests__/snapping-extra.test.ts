import { describe, expect, it } from 'vitest';

import { snapCandidates } from '../snapping';
import { clip, timeline, track } from '../../beats/__tests__/fixtures';

describe('snapCandidates extraFrames', () => {
  const doc = timeline([track('v1', 'video', [clip('a', 10, 50)])]);
  const ctx = { playheadFrame: 20, inFrame: null, outFrame: null };
  it('is unchanged without extras', () => {
    expect(snapCandidates(doc, ctx)).toEqual([0, 10, 20, 50]);
  });
  it('unions extras, sorted and unique', () => {
    expect(snapCandidates(doc, { ...ctx, extraFrames: [50, 33, 33, 5] })).toEqual([0, 5, 10, 20, 33, 50]);
  });
});
