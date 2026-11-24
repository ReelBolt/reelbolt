/**
 * Accepting a large recut (hundreds of cuts) must not clone the document once per op.
 * Target: 300 cuts well under 1 s. The bound asserted here is forgiving (CI machines vary) and is
 * paired with a ratio against the per-op clone path, which is what actually proves the fix.
 */

import { describe, expect, it } from 'vitest';

import { apply, applyBatch, type EditOp } from '../index';
import { compileRippleCut } from '../../transcript/transcript';
import { doc, media, title, caption, track } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';

const CUTS = 300;

function realistic(): EditTimeline {
  const link = { linkId: 'link-1' };
  const titles = Array.from({ length: 40 }, (_, i) => title(`ti${i}`, i * 1000, i * 1000 + 300));
  const captions = Array.from({ length: 120 }, (_, i) => caption(`ca${i}`, i * 330, i * 330 + 300));
  return doc([
    track('v1', 'video', [media('big-v', 0, 60000, 0, link)]),
    track('v2', 'video', titles),
    track('a1', 'audio', [media('big-a', 0, 60000, 0, link)]),
    track('a2', 'audio', [media('music', 0, 60000, 0)]),
    track('c1', 'video', captions),
  ]);
}

function cutProgram(start: EditTimeline): EditOp[] {
  let n = 0;
  const spans = Array.from({ length: CUTS }, (_, i) => ({ startFrame: 100 + i * 150, endFrame: 130 + i * 150 }));
  return compileRippleCut(start, { trackId: 'v1', spans, newItemId: () => `piece-${n++}` }).ops;
}

describe('applyBatch performance', () => {
  it('applies a 300-cut recut on one working copy, much faster than a clone per op', () => {
    const start = realistic();
    const ops = cutProgram(start);
    expect(ops.length).toBeGreaterThanOrEqual(CUTS * 2);

    const t0 = performance.now();
    const batched = applyBatch(start, ops);
    const batchMs = performance.now() - t0;

    const t1 = performance.now();
    let current = start;
    for (const op of ops) current = apply(current, op).doc;
    const perOpMs = performance.now() - t1;

    expect(batched.doc).toEqual(current);
    process.stdout.write(`recut ${CUTS} cuts (${ops.length} ops): batch ${batchMs.toFixed(0)} ms, per-op clone ${perOpMs.toFixed(0)} ms\n`);
    expect(batchMs).toBeLessThan(1500);
    expect(batchMs).toBeLessThan(perOpMs / 2);
  }, 30_000);
});
