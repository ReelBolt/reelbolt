import { describe, expect, it } from 'vitest';
import type { AdjustmentClip, GradeStack } from '../../types';
import { doc, media, track } from '../../__tests__/fixtures';
import { withGradePreview, type GradePreview } from '../grade-preview';

const stack = (id: string): GradeStack => ({ enabled: true, nodes: [{ id, name: id, enabled: true, primaries: {}, curves: null, lut: null }] });

const adjustment = (grade: GradeStack | null = null): AdjustmentClip => ({
  id: 'adj1',
  type: 'adjustmentClip',
  name: 'Adjustment',
  recordIn: 0,
  recordOut: 60,
  enabled: true,
  grade,
});

function baseDoc() {
  return doc(
    [
      track('v1', 'video', [media('c1', 0, 60, 0, { grade: null })]),
      track('v2', 'video', [adjustment()]),
    ],
    [],
  );
}

describe('withGradePreview', () => {
  it('returns the same document with no preview', () => {
    const d = baseDoc();
    expect(withGradePreview(d, null)).toBe(d);
  });

  it('returns the same document when the target is missing', () => {
    const d = baseDoc();
    expect(withGradePreview(d, { target: { kind: 'item', id: 'nope' }, stack: stack('n1') })).toBe(d);
    expect(withGradePreview(d, { target: { kind: 'group', id: 'nope' }, stack: stack('n1') })).toBe(d);
  });

  it('replaces a media clip grade without touching the document it came from', () => {
    const d = baseDoc();
    const preview: GradePreview = { target: { kind: 'item', id: 'c1' }, stack: stack('n1') };
    const next = withGradePreview(d, preview);
    expect(next).not.toBe(d);
    const item = next.tracks[0].items[0];
    expect(item.type === 'mediaClip' && item.grade).toBe(preview.stack);
    expect(d.tracks[0].items[0]).toEqual(media('c1', 0, 60, 0, { grade: null }));
    // Untouched objects stay referentially equal.
    expect(next.tracks[1]).toBe(d.tracks[1]);
  });

  it('replaces an adjustment clip grade', () => {
    const d = baseDoc();
    const preview: GradePreview = { target: { kind: 'item', id: 'adj1' }, stack: stack('n2') };
    const next = withGradePreview(d, preview);
    const item = next.tracks[1].items[0];
    expect(item.type === 'adjustmentClip' && item.grade).toBe(preview.stack);
  });

  it('replaces a colour group grade and leaves the other groups alone', () => {
    const d = { ...baseDoc(), colorGroups: [{ id: 'g1', name: 'One', clipIds: ['c1'], grade: null }, { id: 'g2', name: 'Two', clipIds: [] as string[], grade: null }] };
    const preview: GradePreview = { target: { kind: 'group', id: 'g1' }, stack: stack('n3') };
    const next = withGradePreview(d, preview);
    expect(next.colorGroups?.[0].grade).toBe(preview.stack);
    expect(next.colorGroups?.[1]).toBe(d.colorGroups[1]);
  });

  it('returns the same document when the previewed stack is already the grade', () => {
    const grade = stack('n4');
    const d = { ...baseDoc(), colorGroups: [{ id: 'g1', name: 'One', clipIds: ['c1'], grade }] };
    expect(withGradePreview(d, { target: { kind: 'group', id: 'g1' }, stack: grade })).toBe(d);
  });

  it('applies the extra stacks of a preview too (solo mutes the other stack of the chain)', () => {
    const groupGrade = stack('g-n');
    const d = { ...baseDoc(), colorGroups: [{ id: 'g1', name: 'One', clipIds: ['c1'], grade: groupGrade }] };
    const muted = { ...groupGrade, enabled: false };
    const preview: GradePreview = { target: { kind: 'item', id: 'c1' }, stack: stack('n5'), also: [{ target: { kind: 'group', id: 'g1' }, stack: muted }] };
    const next = withGradePreview(d, preview);
    const item = next.tracks[0].items[0];
    expect(item.type === 'mediaClip' && item.grade).toBe(preview.stack);
    expect(next.colorGroups?.[0].grade).toBe(muted);
    // The document the preview was derived from is untouched.
    expect(d.colorGroups[0].grade).toBe(groupGrade);
    expect(d.tracks[0].items[0]).toEqual(media('c1', 0, 60, 0, { grade: null }));
  });

  it('gives back the same derived document for the same document and preview (a preview that stays up)', () => {
    const d = baseDoc();
    const preview: GradePreview = { target: { kind: 'item', id: 'c1' }, stack: stack('n6') };
    const first = withGradePreview(d, preview);
    expect(withGradePreview(d, preview)).toBe(first);
    // Another preview object, or another document, is derived afresh.
    expect(withGradePreview(d, { ...preview })).not.toBe(first);
    expect(withGradePreview({ ...d }, preview)).not.toBe(first);
  });
});
