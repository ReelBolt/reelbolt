/**
 * P3b: `MediaClip.inputProfile` in the web document model (mirror of T-008): `setProperty` sets it
 * as one undoable step, an unknown/empty/non-string id is refused, and the flattener keeps it
 * through a compound. The shared contract corpus (`engine/__tests__/contract.test.ts`) covers the
 * accepted/refused fixture documents; this file covers the op and the flattener.
 */

import { describe, expect, it, vi } from 'vitest';

import type { EditTimeline, MediaClip } from '../types';
import { doc, media, track } from './fixtures';

// Fixtures name tracks/clips `track-v1`/`c1`; the engine's default invariant check wants Guid ids.
// Only that shape is relaxed — every structural rule still runs.
vi.mock('../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../engine')>();
  return { ...engine, assertInvariants: (d: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(d, { ids: 'opaque' }) };
});

import { apply, EditEngineError, SET_PROPERTY_ALLOWLIST } from '../engine';
import { flattenTimeline } from '../structure/flatten';

const clip = (extra: Partial<MediaClip> = {}): MediaClip => media('c1', 0, 30, 0, extra);
const oneClip = (extra: Partial<MediaClip> = {}): EditTimeline => doc([track('v1', 'video', [clip(extra)])]);

describe('inputProfile', () => {
  it('is settable on a media clip and nowhere else', () => {
    expect(SET_PROPERTY_ALLOWLIST.mediaClip).toContain('inputProfile');
    expect(SET_PROPERTY_ALLOWLIST.titleClip).not.toContain('inputProfile');
  });

  it('sets a known profile as one undo step; undo restores the absent field', () => {
    const start = oneClip();
    const set = apply(start, { type: 'setProperty', itemId: 'c1', key: 'inputProfile', value: 'sonySLog3' });
    expect((set.doc.tracks[0].items[0] as MediaClip).inputProfile).toBe('sonySLog3');
    // The start document is untouched and did not gain the key.
    expect(Object.hasOwn(start.tracks[0].items[0], 'inputProfile')).toBe(false);

    let undone = set.doc;
    for (const op of set.inverse) undone = apply(undone, op).doc;
    expect(Object.hasOwn(undone.tracks[0].items[0], 'inputProfile')).toBe(false);
    expect(undone).toStrictEqual(start);
  });

  it('accepts null (no input transform)', () => {
    const set = apply(oneClip({ inputProfile: 'arriLogC4' }), { type: 'setProperty', itemId: 'c1', key: 'inputProfile', value: null });
    expect((set.doc.tracks[0].items[0] as MediaClip).inputProfile).toBeNull();
  });

  it.each(['notAProfile', '', 'AppleLog', 7 as unknown as string])('refuses the invalid profile %o', (value) => {
    expect(() => apply(oneClip(), { type: 'setProperty', itemId: 'c1', key: 'inputProfile', value })).toThrow(EditEngineError);
  });

  it('is kept through a compound in the flattener, and an absent one stays absent', () => {
    const d: EditTimeline = {
      ...doc([
        track('v1', 'video', [{ id: 'cc', type: 'compoundClip', name: 'Nest', recordIn: 100, recordOut: 140, sequenceId: 'q', sourceIn: 10, enabled: true }]),
      ]),
      sequences: [{ id: 'q', name: 'Q', tracks: [track('qt', 'video', [media('n1', 0, 20, 5, { inputProfile: 'fujiFLog2' }), media('n2', 20, 60, 0)])] }],
    };
    const flat = flattenTimeline(d);
    // The compound leaves the host track empty and puts the expanded track right after it.
    const [withProfile, withoutProfile] = flat.tracks[1].items as MediaClip[];
    expect(withProfile.inputProfile).toBe('fujiFLog2');
    expect(Object.hasOwn(withoutProfile, 'inputProfile')).toBe(false);
  });
});
