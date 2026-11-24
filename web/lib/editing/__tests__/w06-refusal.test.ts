/**
 * W0.6 (d3 N1/N3, d1 N8): every "Edit refused" toast goes through one mapper from the validator's
 * errors to plain sentences — no op names, no raw JSON, no "[object Object]", and names instead of ids.
 */

import { describe, expect, it } from 'vitest';
import { apply, EditEngineError, type EditOp } from '../engine';
import { doc, media, track } from './fixtures';
import { refusalSentence } from '../refusal';
import type { EditTimeline } from '../types';

const TRACK_ID = '5f0c2b1e-0d7a-4c55-9a51-1b2c3d4e5f60';
const CLIP_ID = '9a8b7c6d-1111-4222-8333-444455556666';

const fixture = (locked = false): EditTimeline => {
  const d = doc([track(TRACK_ID, 'video', [media(CLIP_ID, 0, 60)], { locked })]);
  d.tracks[0].name = 'Interviews';
  (d.tracks[0].items[0] as { name: string }).name = 'Opening shot';
  return d;
};

function refusal(d: EditTimeline, op: EditOp): unknown {
  try {
    apply(d, op);
  } catch (error) {
    return error;
  }
  throw new Error('expected the engine to refuse');
}

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
const plain = (s: string) => {
  expect(s).not.toMatch(GUID);
  expect(s).not.toMatch(/[{}[\]]|object Object|setProperty|setTrackProperty|nudge:/);
  expect(s).toMatch(/^[A-Z“]/);
  expect(s).toMatch(/\.$/);
};

describe('W0.6 refusal sentences', () => {
  it('a locked track is named, not shown as its id (d1 N8)', () => {
    const s = refusalSentence(refusal(fixture(true), { type: 'nudge', itemId: CLIP_ID, deltaFrames: 1 }), fixture(true));
    expect(s).toContain('“Interviews”');
    expect(s).toMatch(/locked/);
    plain(s);
  });

  it('an out-of-range transform value names the field and its range, without the raw transform (d3 N1)', () => {
    const s = refusalSentence(refusal(fixture(), { type: 'setProperty', itemId: CLIP_ID, key: 'transform', value: { scale: { value: 99, keys: null } } }), fixture());
    expect(s).toBe('Scale must be more than 0 and at most 8.');
    const o = refusalSentence(refusal(fixture(), { type: 'setProperty', itemId: CLIP_ID, key: 'transform', value: { opacity: { value: 2, keys: null } } }), fixture());
    expect(o).toBe('Opacity must be between 0 and 1.');
  });

  it('clip gain out of range', () => {
    const s = refusalSentence(refusal(fixture(), { type: 'setProperty', itemId: CLIP_ID, key: 'gainDb', value: { value: 50, keys: null } }), fixture());
    expect(s).toBe('Clip gain must be between -60 and 20 dB.');
  });

  it('a bad track setting does not print "[object Object]" (d3 N3)', () => {
    const op = { type: 'setTrackProperty', trackId: TRACK_ID, key: 'eq', value: { bands: 'nope' } } as unknown as EditOp;
    const s = refusalSentence(refusal(fixture(), op), fixture(), op);
    plain(s);
    expect(s).toBe('That EQ setting is not allowed on “Interviews”.');
  });

  it('a missing clip, and an unknown id, read as plain sentences', () => {
    const s = refusalSentence(refusal(fixture(), { type: 'lift', itemId: 'f00dfeed-0000-4000-8000-000000000000' }), fixture());
    expect(s).toBe('That clip is no longer on the timeline.');
    plain(refusalSentence(new EditEngineError(`the destination overlaps '${CLIP_ID}', found {"a":1}`, 'move'), fixture()));
    expect(refusalSentence(new EditEngineError(`the destination overlaps '${CLIP_ID}'`, 'move'), fixture())).toContain('“Opening shot”');
  });

  it('non-engine errors still read as a sentence', () => {
    expect(refusalSentence('boom', null)).toBe('Boom.');
    expect(refusalSentence(new Error('network down'), null)).toBe('Network down.');
  });
});
