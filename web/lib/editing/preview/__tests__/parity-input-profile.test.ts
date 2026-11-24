import { describe, expect, it } from 'vitest';
import { classifyItem, CAMERA_PLUS_LUT_HINT } from '../parity';
import { HDR_PREVIEW_REASON } from '../../media/hdr';
import { CAMERA_PROFILE_IDS } from '../../color/camera-profiles';
import { media } from '../../__tests__/fixtures';

const base = () => media('c1', 0, 100) as never;
const withProfile = (extra: Record<string, unknown> = {}) => ({ ...(base() as object), inputProfile: CAMERA_PROFILE_IDS[0], ...extra }) as never;
const lutGrade = { enabled: true, nodes: [{ id: 'n', enabled: true, lut: { fileId: 'l' } }] };

describe('parity of a clip with a camera conversion', () => {
  it('is faithful: the preview converts through the render\'s table, tetrahedrally like lut3d', () => {
    const r = classifyItem(withProfile());
    expect(r.parity).toBe('faithful');
    expect(r.findings).toHaveLength(0);
  });
  it('adds the double-conversion hint when a grade node carries a LUT too', () => {
    const r = classifyItem(withProfile({ grade: lutGrade }));
    expect(r.parity).toBe('approximate');
    expect(r.findings[0]?.feature).toBe('inputProfile');
    expect(r.reason).toContain(CAMERA_PLUS_LUT_HINT);
  });
  it('HDR footage ignores the profile and keeps the HDR reason', () => {
    const r = classifyItem(withProfile(), undefined, { hdrKindFor: () => 'hlg' as never });
    expect(r.reason).toBe(HDR_PREVIEW_REASON);
  });
  it('a clip without a profile is unchanged', () => {
    expect(classifyItem(base()).parity).toBe('faithful');
  });
});
