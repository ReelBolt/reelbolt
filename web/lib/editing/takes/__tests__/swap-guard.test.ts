import { describe, expect, it } from 'vitest';

import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { readShots } from '../../cut/cut';
import { buildTakeStacks, readDuplicateGroups, swapTakeRefusal } from '../take-stacks';

const FPS = 30;
const artifact = {
  shots: [
    { id: 's0', startSec: 0, endSec: 10, visual: { duplicateGroupId: 'k0' } },
    { id: 's1', startSec: 20, endSec: 26, visual: { duplicateGroupId: 'k0', isBestTake: true } },
    { id: 's2', startSec: 30, endSec: 45, visual: { duplicateGroupId: 'k0' } },
  ],
  duplicateGroups: [{ id: 'k0', shotIds: ['s0', 's1', 's2'], bestShotId: 's1' }],
};

describe('"Use this take" guard (d4 N5)', () => {
  it('refuses an alternate shorter than the clip with a plain sentence, accepts one that fits', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('c1', 0, 300, 0)])]);
    const [stack] = buildTakeStacks(d, TRACKS.v1, readShots(artifact), readDuplicateGroups(artifact), FPS);
    const short = stack.alternates.find((a) => a.shot.id === 's1')!;
    const long = stack.alternates.find((a) => a.shot.id === 's2')!;
    const refusal = swapTakeRefusal(d, 'c1', short.shot, FPS);
    expect(refusal).toMatch(/^This take is 6\.0 s long but the clip needs 10\.0 s/);
    expect(refusal).not.toMatch(/s1|c1/);
    expect(swapTakeRefusal(d, 'c1', long.shot, FPS)).toBeNull();
  });
});
