import { describe, expect, it } from 'vitest';
import { assertInvariants } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import { compileRemoveSilence } from '../remove-silence';

let n = 0;
const fresh = () => `00000000-0000-4000-8000-${String(++n + 5000).padStart(12, '0')}`;

describe('R09.2 linked-track apply', () => {
  const picture = media('11111111-0000-4000-8000-000000000001', 0, 300, 0, { linkId: 'L1' });
  const sound = media('11111111-0000-4000-8000-000000000002', 0, 300, 0, { linkId: 'L1' });
  const other = media('11111111-0000-4000-8000-000000000003', 0, 300, 0);

  it('cuts the linked sound together with the picture so both stay the same length', () => {
    const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [sound])]);
    const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh });
    expect(plan.cuts).toBe(1);
    expect(plan.linkedCuts).toBe(1);
    const lengths = plan.preview.tracks.map((t) => t.items.reduce((e, i) => Math.max(e, i.recordOut), 0));
    expect(lengths).toEqual([270, 270]);
    assertInvariants(plan.preview, { ids: 'opaque' });
  });

  it('leaves an unlinked clip on another track alone', () => {
    const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [other])]);
    const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh });
    expect(plan.linkedCuts).toBe(0);
    expect(plan.preview.tracks[1].items).toHaveLength(1);
    expect(plan.preview.tracks[1].items[0].recordOut).toBe(300);
  });

  it('can be switched off', () => {
    const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [sound])]);
    const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh, includeLinked: false });
    expect(plan.linkedCuts).toBe(0);
    expect(plan.preview.tracks[1].items[0].recordOut).toBe(300);
  });
});

describe('a picture and its sound that were never linked (a timeline made from a render)', () => {
  const picture = media('11111111-0000-4000-8000-000000000001', 0, 300, 0);
  const twin = media('11111111-0000-4000-8000-000000000002', 0, 300, 0);
  const stranger = media('11111111-0000-4000-8000-000000000003', 0, 300, 0, { assetRef: { kind: 'projectFile', projectFileId: 'another-file', stepResultId: null } });
  const shorter = media('11111111-0000-4000-8000-000000000004', 0, 200, 0);

  it('cuts the exact twin on another track with the picture, so the sound stays in step', () => {
    const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [twin])]);
    const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh });
    expect(plan.linkedCuts).toBe(1);
    expect(plan.preview.tracks.map((t) => t.items.map((i) => [i.recordIn, i.recordOut]))).toEqual([[[0, 100], [100, 270]], [[0, 100], [100, 270]]]);
    assertInvariants(plan.preview, { ids: 'opaque' });
  });

  it('leaves a clip of another source or another span alone', () => {
    for (const other of [stranger, shorter]) {
      const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [other])]);
      const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh });
      expect(plan.linkedCuts).toBe(0);
      expect(plan.preview.tracks[1].items).toHaveLength(1);
    }
  });

  it('is not done when linking is switched off', () => {
    const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [twin])]);
    expect(compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh, includeLinked: false }).linkedCuts).toBe(0);
  });
});

describe('linked pieces after a cut', () => {
  it('re-pairs the halves that still line up and leaves the doc valid', () => {
    const a = media('11111111-0000-4000-8000-000000000001', 0, 300, 0, { linkId: 'L1' });
    const b = media('11111111-0000-4000-8000-000000000002', 0, 300, 0, { linkId: 'L1' });
    const d = doc([track('t-v', 'video', [a]), track('t-a', 'audio', [b])]);
    const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh });
    assertInvariants(plan.preview, { ids: 'opaque' });
    const ids = plan.preview.tracks.map((t) => t.items.map((i) => (i.type === 'mediaClip' ? i.linkId : null)));
    expect(ids[0]).toEqual(ids[1]);
    expect(new Set(ids[0]).size).toBe(2);
  });

  it('unlinks pieces whose partner was not cut', () => {
    const a = media('11111111-0000-4000-8000-000000000001', 0, 300, 0, { linkId: 'L1' });
    const b = media('11111111-0000-4000-8000-000000000002', 0, 300, 0, { linkId: 'L1' });
    const d = doc([track('t-v', 'video', [a]), track('t-a', 'audio', [b])]);
    const plan = compileRemoveSilence(d, { trackId: 't-v', spans: [{ startFrame: 100, endFrame: 130 }], newItemId: fresh, includeLinked: false });
    assertInvariants(plan.preview, { ids: 'opaque' });
    const links = plan.preview.tracks.flatMap((t) => t.items.map((i) => (i.type === 'mediaClip' ? i.linkId ?? null : null)));
    expect(links.every((l) => l === null)).toBe(true);
  });
});
