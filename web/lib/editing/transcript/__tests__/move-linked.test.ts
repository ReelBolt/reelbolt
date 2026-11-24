import { describe, expect, it } from 'vitest';
import { assertInvariants } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import { compileMoveSpan } from '../transcript';

let n = 0;
const fresh = () => `00000000-0000-4000-8000-${String(++n + 6000).padStart(12, '0')}`;

describe('compileMoveSpan with linked clips', () => {
  it('leaves a valid document: moved pieces are unlinked, not left as a broken group', () => {
    const d = doc([
      track('t-v', 'video', [media('11111111-0000-4000-8000-000000000001', 0, 300, 0, { linkId: 'L1' })]),
      track('t-a', 'audio', [media('11111111-0000-4000-8000-000000000002', 0, 300, 0, { linkId: 'L1' })]),
    ]);
    const plan = compileMoveSpan(d, { trackId: 't-v', from: { startFrame: 200, endFrame: 300 }, toFrame: 0, newItemId: fresh });
    expect(plan.refusal).toBeNull();
    assertInvariants(plan.preview, { ids: 'opaque' });
    const ids = plan.preview.tracks[0].items.map((i) => (i.type === 'mediaClip' ? i.linkId ?? null : null));
    expect(ids.every((l) => l === null)).toBe(true);
  });
});
