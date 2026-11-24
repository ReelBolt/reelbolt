import { describe, expect, it } from 'vitest';
import { apply, EditEngineError } from '../../engine';
import { caption, doc, media, title, track } from '../../__tests__/fixtures';
import { captionTracks, deliveryOf, deliveryOps } from '../delivery';

const CAP = '31111111-0000-4000-8000-000000000001';
const TITLE = '31111111-0000-4000-8000-000000000002';

describe('R09.7 caption delivery', () => {
  const d = doc([track('t-v', 'video', [media('21111111-0000-4000-8000-000000000001', 0, 300)]), track('t-s', 'video', [caption(CAP, 0, 60, 'hi')])]);

  it('knows which tracks hold captions and how they are delivered', () => {
    expect(captionTracks(d)).toEqual([{ id: 't-s', name: 't-s', enabled: true }]);
    expect(deliveryOf(captionTracks(d))).toBe('burn');
  });

  it('switching to separate files disables only the caption tracks; both and burn enable them again', () => {
    const off = deliveryOps(captionTracks(d), 'sidecar');
    expect(off).toEqual([{ type: 'setTrackProperty', trackId: 't-s', key: 'enabled', value: false }]);
    const next = off.reduce((acc, op) => apply(acc, op).doc, d);
    expect(deliveryOf(captionTracks(next))).toBe('sidecar');
    expect(deliveryOps(captionTracks(next), 'both')).toEqual([{ type: 'setTrackProperty', trackId: 't-s', key: 'enabled', value: true }]);
    expect(deliveryOps(captionTracks(d), 'burn')).toEqual([]);
  });
});

describe('R09.6 look and position through setProperty', () => {
  const d = doc([track('t-s', 'video', [caption(CAP, 0, 60, 'hi'), title(TITLE, 100, 160, 'T')])]);

  it('sets and clears a caption look and a position', () => {
    const withLook = apply(apply(d, { type: 'setProperty', itemId: CAP, key: 'look', value: 'boxed' }).doc, { type: 'setProperty', itemId: CAP, key: 'position', value: 'topLeft' }).doc;
    expect(withLook.tracks[0].items[0]).toMatchObject({ look: 'boxed', position: 'topLeft' });
    const cleared = apply(withLook, { type: 'setProperty', itemId: CAP, key: 'look', value: null }).doc;
    expect((cleared.tracks[0].items[0] as { look?: string | null }).look ?? null).toBeNull();
  });

  it('refuses a look outside the catalogue and a position off the pad', () => {
    expect(() => apply(d, { type: 'setProperty', itemId: CAP, key: 'look', value: 'neon' })).toThrow(EditEngineError);
    expect(() => apply(d, { type: 'setProperty', itemId: CAP, key: 'position', value: 'nowhere' })).toThrow(EditEngineError);
  });

  it('puts a title on the pad but a title has no look', () => {
    const moved = apply(d, { type: 'setProperty', itemId: TITLE, key: 'position', value: 'bottomRight' }).doc;
    expect(moved.tracks[0].items[1]).toMatchObject({ position: 'bottomRight' });
    expect(() => apply(d, { type: 'setProperty', itemId: TITLE, key: 'look', value: 'bold' })).toThrow(EditEngineError);
  });
});
