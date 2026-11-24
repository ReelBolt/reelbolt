/** @vitest-environment node */
// QA repro (log-footage-color T-020, fixed by T-024). A multicam built from log clips must keep
// their camera conversion once flattened: each angle carries its source clip's inputProfile and
// every run the flattener emits carries the angle's.
import { describe, expect, it } from 'vitest';
import { doc, media, track, TRACKS } from './fixtures';
import { apply } from '../engine/apply';
import { compileCreateMulticam } from '../structure/multicam';
import { flattenTimeline } from '../structure/flatten';
import type { EditTimeline, MediaClip } from '../types';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n + 9000).padStart(12, '0')}`;

describe('QA repro: multicam keeps the camera conversion (plan P3 "flattener copy for compound/multicam")', () => {
  it('creating a multicam from two Apple Log clips still plays them converted after flattening', () => {
    let d: EditTimeline = doc([
      track(TRACKS.v1, 'video', [media('a', 0, 60, 0, { inputProfile: 'appleLog' })]),
      track(TRACKS.v2, 'video', [media('b', 0, 60, 0, { inputProfile: 'appleLog' })]),
    ]);
    const ops = compileCreateMulticam(d, ['a', 'b'], { multicamId: newId(), angleIds: [newId(), newId()] });
    for (const op of ops) d = apply(d, op).doc;
    const runs = flattenTimeline(d).tracks.flatMap((t) => t.items).filter((i): i is MediaClip => i.type === 'mediaClip');
    expect(runs.length).toBeGreaterThan(0);
    expect(runs.map((r) => r.inputProfile ?? null)).toEqual(runs.map(() => 'appleLog'));
  });
});
