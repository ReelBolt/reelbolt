import { describe, expect, it } from 'vitest';
import { assertInvariants } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import type { TranscriptWord } from '../../transcript/transcript';
import { compileKeepTake, findRepeatedTakes, sentenceSimilarity } from '../repeated-takes';

let n = 0;
const fresh = () => `00000000-0000-4000-8000-${String(++n + 9000).padStart(12, '0')}`;

const say = (text: string, startSec: number, prefix: string, estimated = false): TranscriptWord[] => {
  const tokens = text.split(' ');
  return tokens.map((t, i) => ({ id: `${prefix}${i}`, text: t, startSec: startSec + i * 0.3, endSec: startSec + i * 0.3 + 0.25, sourceIndex: 0, segmentId: null, estimated }));
};

// Take 1 (with an um and a rough word), take 2 clean, an unrelated line between.
const WORDS = [
  ...say('The quick brown um fox jumps over the lazy dog.', 1, 'a', false),
  ...say('Something completely different happens here.', 6, 'b'),
  ...say('The quick brown fox jumps over the lazy dog.', 10, 'c'),
];

describe('R09.9 repeated takes', () => {
  it('groups the repeated line and suggests the clean take as best', () => {
    const groups = findRepeatedTakes(WORDS);
    expect(groups).toHaveLength(1);
    expect(groups[0].takes).toHaveLength(2);
    const best = groups[0].takes.find((t) => t.id === groups[0].bestId);
    expect(best?.startSec).toBeCloseTo(10);
    expect(best?.stumbles).toBe(0);
    expect(groups[0].takes[0].stumbles).toBe(1);
  });

  it('does not group different lines', () => {
    expect(findRepeatedTakes(WORDS.filter((w) => !w.id.startsWith('c')))).toEqual([]);
    expect(sentenceSimilarity(['a', 'b', 'c'], ['x', 'y', 'z'])).toBe(0);
  });

  it('sets the other takes aside on a switched-off track and cuts the linked sound with them', () => {
    const picture = media('11111111-0000-4000-8000-000000000001', 0, 450, 0, { linkId: 'L1' });
    const sound = media('11111111-0000-4000-8000-000000000002', 0, 450, 0, { linkId: 'L1' });
    const d = doc([track('t-v', 'video', [picture]), track('t-a', 'audio', [sound])]);
    const group = findRepeatedTakes(WORDS)[0];
    const plan = compileKeepTake(d, { trackId: 't-v', groups: [{ group, keepId: group.bestId }], fps: 30, newItemId: fresh });
    assertInvariants(plan.preview, { ids: 'opaque' });
    expect(plan.cuts).toBeGreaterThan(0);
    expect(plan.linkedCuts).toBeGreaterThan(0);
    const holding = plan.preview.tracks.filter((t) => t.name.startsWith('Other takes'));
    expect(holding.map((t) => t.kind).sort()).toEqual(['audio', 'video']);
    expect(holding.every((t) => t.enabled === false && t.items.length > 0)).toBe(true);
    // The kept timeline is shorter by the set-aside take, picture and sound alike.
    const ends = plan.preview.tracks.filter((t) => !t.name.startsWith('Other takes')).map((t) => t.items.reduce((e, i) => Math.max(e, i.recordOut), 0));
    expect(ends[0]).toBe(ends[1]);
    expect(ends[0]).toBeLessThan(450);
  });
});
