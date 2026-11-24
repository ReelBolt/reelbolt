/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { allMarkerFrames, allMarkerRows, isRationaleMarkerId, rationaleMarkerRows, userMarkerRows } from '../markers';
import { buildEditIndex, filterEditIndex, sortEditIndex } from '../../edit-index/rows';
import { standardDoc } from '../../__tests__/fixtures';
import fc from 'fast-check';
import { doc, media, track } from '../../__tests__/fixtures';

const d = standardDoc();

describe('markers', () => {
  it('derives rationale markers from real provenance only — never a fabricated label', () => {
    const rows = rationaleMarkerRows(d);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      kind: 'rationale',
      frame: 0,
      name: 'c1',
      note: 'Opening shot keeps the hook.',
      itemId: 'c1',
      agentName: 'VideoStoryEditor',
      providerName: 'vLLM',
    });
    expect(isRationaleMarkerId(rows[0].id)).toBe(true);
    expect(userMarkerRows(d).map((r) => r.name)).toEqual(['Hook', 'Act two']);
  });

  it('merges user and rationale markers by frame, user first on ties', () => {
    const rows = allMarkerRows(d);
    expect(rows.map((r) => `${r.frame}:${r.kind}`)).toEqual(['0:rationale', '45:user', '150:user']);
    expect(allMarkerFrames(d)).toEqual([0, 45, 150]);
  });
});

describe('edit index', () => {
  it('projects every item exactly once in document order', () => {
    const rows = buildEditIndex(d);
    expect(rows.map((r) => r.itemId)).toEqual(['c1', 'c2', 'c3', 't1', 'a1', 'a2']);
    expect(rows[0]).toMatchObject({ index: 1, trackName: 'track-v1', itemType: 'mediaClip', recordIn: 0, recordOut: 60, durationFrames: 60, sourceIn: 0, hasProvenance: true });
    expect(rows[3].sourceIn).toBeNull();
  });

  it('row count always equals item count (property)', () => {
    fc.assert(
      fc.property(fc.array(fc.integer({ min: 1, max: 20 }), { minLength: 1, maxLength: 6 }), (durations) => {
        let cursor = 0;
        const items = durations.map((dur, i) => {
          const item = media(`i${i}`, cursor, cursor + dur);
          cursor += dur + (i % 2);
          return item;
        });
        const d2 = doc([track('t', 'video', items), track('u', 'audio', items.map((it) => ({ ...it, id: `${it.id}-a` })))]);
        expect(buildEditIndex(d2)).toHaveLength(items.length * 2);
      }),
    );
  });

  it('sorts and filters without dropping rows it should keep', () => {
    const rows = buildEditIndex(d);
    expect(sortEditIndex(rows, 'durationFrames', true)[0].itemId).toBe('a1');
    expect(sortEditIndex(rows, 'name').map((r) => r.name)).toEqual(['a1', 'a2', 'c1', 'c2', 'c3', 't1']);
    expect(filterEditIndex(rows, 'hook').map((r) => r.itemId)).toEqual(['c1']);
    expect(filterEditIndex(rows, 'title').map((r) => r.itemId)).toEqual(['t1']);
    expect(filterEditIndex(rows, '')).toHaveLength(6);
  });
});
