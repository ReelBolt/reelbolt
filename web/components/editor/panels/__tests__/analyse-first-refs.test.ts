import { describe, expect, it } from 'vitest';
import { mediaRefsInRange } from '../AnalyseFirst';
import { analysisBaseForTest } from '@/lib/editing/api/media-analysis';
import type { EditTimeline } from '@/lib/editing/types';

const doc = (ids: string[]): EditTimeline =>
  ({
    tracks: [
      {
        items: ids.map((id, i) => ({ type: 'mediaClip', recordIn: i * 10, recordOut: i * 10 + 10, assetRef: { kind: 'projectFile', projectFileId: id } })),
      },
    ],
  }) as unknown as EditTimeline;

describe('analysis media refs', () => {
  it('keeps only UUID-shaped file ids', () => {
    const good = '3f2b8c1e-0a41-4c55-9d4e-1b7f6a9c2d10';
    const refs = mediaRefsInRange(doc([good, '../../admin', 'a/b', '']), null);
    expect(refs).toEqual([`file:${good}`]);
  });
  it('encodes path segments in the analysis URL', () => {
    expect(analysisBaseForTest('p1', 'file:../x?y')).toBe('/api/v1/projects/p1/media/file%3A..%2Fx%3Fy/analysis');
  });
});
