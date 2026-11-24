import { describe, expect, it } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { PaintUi } from '../../timeline/paint';
import {
  HANDLE_HIT_PX,
  STRIP_HEADER_PX,
  STRIP_LANE_MIN,
  SUMMARY_LANE_HEIGHT,
  adjacentClip,
  centreScroll,
  describeSelection,
  handleRects,
  panPlayhead,
  stripClips,
  stripDoc,
  stripFrameToX,
  stripHitTest,
  stripLanes,
  stripViewport,
  stripXToFrame,
} from '../strip-geometry';

const d = doc([
  track('v1', 'video', [media('c1', 0, 120, 0, { name: 'Intro.mp4', linkId: 'L1' }), media('c2', 150, 270, 0, { name: 'Beach.mp4', linkId: 'L2' })], { height: 40 }),
  track('a1', 'audio', [media('s1', 0, 120, 0, { linkId: 'L1' }), media('s2', 150, 270, 0, { linkId: 'L2' })]),
  track('v2', 'video', [media('o1', 30, 90)]),
  track('a2', 'audio', [media('m1', 0, 270)]),
]);
const ui = (selection: string[] = []): PaintUi => ({
  playheadFrame: 0,
  selection: new Set(selection),
  inFrame: null,
  outFrame: null,
  trackFlags: () => ({ locked: false, enabled: true, mute: false, solo: false, collapsed: false }),
  parityOf: () => 'exact' as never,
  rationaleFrames: [],
});

describe('strip lanes', () => {
  const lanes = stripLanes(d);
  it('edits the first video track and its linked audio directly; the rest are summary lanes', () => {
    expect(lanes.primaryId).toBe('v1');
    expect([...lanes.fullIds].sort()).toEqual(['a1', 'v1']);
    expect([...lanes.summaryIds].sort()).toEqual(['a2', 'v2']);
  });
  it('gives direct lanes at least 56 px and summary lanes a thin height', () => {
    const sd = stripDoc(d, lanes);
    expect(sd.tracks.find((t) => t.id === 'v1')!.height).toBeGreaterThanOrEqual(STRIP_LANE_MIN);
    expect(sd.tracks.find((t) => t.id === 'a1')!.height).toBeGreaterThanOrEqual(STRIP_LANE_MIN);
    expect(sd.tracks.find((t) => t.id === 'v2')!.height).toBe(SUMMARY_LANE_HEIGHT);
    expect(d.tracks[0].height).toBe(40);
  });
  it('an empty timeline has no primary lane', () => {
    expect(stripLanes(doc([])).primaryId).toBeNull();
  });
});

describe('centre-locked geometry', () => {
  it('keeps the playhead at the centre and round-trips x and frame', () => {
    expect(stripFrameToX(100, 100, 400, 2)).toBe(200);
    expect(stripFrameToX(110, 100, 400, 2)).toBe(220);
    expect(stripXToFrame(220, 100, 400, 2)).toBe(110);
    expect(centreScroll(0, 400, 2)).toBe(-100);
    expect(stripViewport(50, 300, 100, 3, 2).scrollFrame).toBe(0);
  });
  it('panning moves the playhead against the finger and clamps to the timeline', () => {
    expect(panPlayhead(100, 20, 2, 270)).toBe(90);
    expect(panPlayhead(100, -20, 2, 270)).toBe(110);
    expect(panPlayhead(5, 200, 2, 270)).toBe(0);
    expect(panPlayhead(260, -200, 2, 270)).toBe(270);
  });
});

describe('hit testing', () => {
  const lanes = stripLanes(d);
  const sd = stripDoc(d, lanes);
  const view = stripViewport(60, 400, 300, 2, 1);
  it('finds the clip under a tap in a direct lane and nothing in a gap or the ruler', () => {
    const x = (f: number) => stripFrameToX(f, 60, 400, 2);
    const y = STRIP_HEADER_PX + 10;
    expect(stripHitTest(sd, view, ui(), lanes, x(50), y)?.item.id).toBe('c1');
    expect(stripHitTest(sd, view, ui(), lanes, x(130), y)).toBeNull();
    expect(stripHitTest(sd, view, ui(), lanes, x(50), 10)).toBeNull();
  });
  it('forgives a near miss below a thin summary lane', () => {
    const x = stripFrameToX(60, 60, 400, 2);
    const summaryTop = STRIP_HEADER_PX + 2 * STRIP_LANE_MIN;
    expect(stripHitTest(sd, view, ui(), lanes, x, summaryTop + SUMMARY_LANE_HEIGHT / 2)?.item.id).toBe('o1');
  });
});

describe('text alternative and neighbours', () => {
  const lanes = stripLanes(d);
  it('lists the selected clip', () => {
    expect(describeSelection(d, lanes, ['c2'])).toBe('Clip 2 of 2, Beach.mp4, 0:05 to 0:09');
    expect(describeSelection(d, lanes, [])).toBe('No clip selected, 2 clips');
    expect(describeSelection(d, lanes, ['o1'])).toContain('on ');
  });
  it('steps to the previous and next clip, and stops at the ends', () => {
    const clips = stripClips(d, lanes);
    expect(adjacentClip(clips, 'c1', 0, 1)?.id).toBe('c2');
    expect(adjacentClip(clips, 'c1', 0, -1)).toBeNull();
    expect(adjacentClip(clips, 'c2', 0, 1)).toBeNull();
    expect(adjacentClip(clips, null, 130, 1)?.id).toBe('c2');
    expect(adjacentClip(clips, null, 130, -1)?.id).toBe('c1');
  });
});

describe('trim handles', () => {
  it('paint 10 to 12 px with a 44 px finger area centred on the edge', () => {
    const r = handleRects(100, 'in');
    expect(r.paintW).toBeGreaterThanOrEqual(10);
    expect(r.paintW).toBeLessThanOrEqual(12);
    expect(r.hitW).toBe(HANDLE_HIT_PX);
    expect(r.hitX + r.hitW / 2).toBe(100);
    expect(handleRects(100, 'out').paintX).toBe(100 - r.paintW);
  });
});
