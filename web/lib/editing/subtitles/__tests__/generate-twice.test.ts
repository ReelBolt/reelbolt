import { describe, expect, it } from 'vitest';

import { apply } from '../../engine';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { compileGenerateCaptions, listCaptions } from '../subtitles';
import type { EditTimeline } from '../../types';

const cues = [{ startFrame: 0, endFrame: 60, text: 'So this is it.' }, { startFrame: 61, endFrame: 120, text: 'Next thought' }];

const run = (d: EditTimeline, trackId: string | null, prefix: string) => {
  let n = 0;
  const plan = compileGenerateCaptions(d, { cues, trackId, trackName: 'Subtitles', newItemId: () => `${prefix}${++n}`, newTrackId: () => `${prefix}-track` });
  let out = d;
  for (const op of plan.ops) out = apply(out, op).doc;
  return { plan, out };
};

describe('generate captions twice (d4 N2)', () => {
  it('reuses the existing captions track instead of adding a second identical one', () => {
    const start = doc([track(TRACKS.v1, 'video', [media('c1', 0, 300, 0)])]);
    const first = run(start, null, 'a');
    expect(first.plan.inserted).toBe(2);
    expect(first.out.tracks).toHaveLength(2);

    const second = run(first.out, null, 'b');
    expect(second.out.tracks).toHaveLength(first.out.tracks.length);
    expect(second.plan.trackId).toBe('a-track');
    expect(second.plan.inserted).toBe(0);
    expect(second.plan.skipped).toBe(2);
    expect(listCaptions(second.out)).toHaveLength(2);
  });

  it('does not reuse a track that holds pictures, even when it is named Subtitles', () => {
    const start = doc([track(TRACKS.v1, 'video', [media('c1', 0, 300, 0)]), track(TRACKS.v2, 'video', [media('c2', 0, 30, 0)], { name: 'Subtitles' })]);
    const { plan, out } = run(start, null, 'a');
    expect(plan.trackId).toBe('a-track');
    expect(out.tracks).toHaveLength(3);
    expect(plan.inserted).toBe(2);
  });
});
