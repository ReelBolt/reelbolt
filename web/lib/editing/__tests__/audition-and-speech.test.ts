import { describe, expect, it } from 'vitest';
import { apply } from '../engine';
import { firstDifferenceFrame } from '../audition';
import { longestQuietStretch, speechAwareShots, type BoringShot } from '../cut/cut';
import { doc, media, track } from './fixtures';

describe('R09.3 audition', () => {
  it('finds where a cut first changes the timeline', () => {
    const d = doc([track('t', 'video', [media('11111111-0000-4000-8000-000000000001', 0, 300)])]);
    const cut = apply(apply(d, { type: 'blade', itemId: '11111111-0000-4000-8000-000000000001', atFrame: 100, newItemId: '11111111-0000-4000-8000-000000000009' }).doc, { type: 'extract', itemId: '11111111-0000-4000-8000-000000000009' }).doc;
    expect(firstDifferenceFrame(d, cut)).toBe(100);
    expect(firstDifferenceFrame(d, d)).toBe(0);
  });
});

describe('R09.8 speech-aware boring shots', () => {
  const shot = (id: string, startSec: number, endSec: number): BoringShot => ({ id, startSec, endSec, sourceIndex: 0, motionMean: 0.01, motionClass: 'still', duplicateGroupId: null, isBestTake: false, durationSec: endSec - startSec, score: 1, record: { startFrame: 0, endFrame: 1, clipId: 'c' } });

  it('finds the longest stretch away from the words', () => {
    expect(longestQuietStretch(0, 10, [{ startSec: 1, endSec: 2 }], 0.25)).toEqual({ startSec: 2.25, endSec: 10 });
    expect(longestQuietStretch(0, 10, [{ startSec: 0, endSec: 10 }])).toBeNull();
  });

  it('offers the whole shot when nobody talks, trims when someone does, blocks when it is all speech', () => {
    const words = [{ startSec: 10, endSec: 10.5, sourceIndex: 0 }, { startSec: 20, endSec: 29.5, sourceIndex: 0 }];
    const out = speechAwareShots([shot('s0', 0, 8), shot('s1', 9, 19), shot('s2', 20, 30)], words);
    expect(out.map((s) => s.speech)).toEqual(['none', 'trimmed', 'blocked']);
    expect(out[1].cutSec?.startSec).toBeCloseTo(10.75);
    expect(out[1].keptSpeechSec).toBeGreaterThan(0);
    expect(out[2].cutSec).toBeNull();
  });

  it('is unchecked without a transcript', () => {
    expect(speechAwareShots([shot('s0', 0, 8)], null)[0].speech).toBe('unchecked');
  });
});
