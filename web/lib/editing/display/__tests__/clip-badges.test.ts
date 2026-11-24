import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { apply } from '@/lib/editing/engine';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import { classifyItem } from '@/lib/editing/preview/parity';
import type { ChromaKeyer, ClipEffect, EditTimeline, MediaClip, ShapeMask } from '@/lib/editing/types';
import {
  BADGE_MORE_WIDTH, BADGE_PHONE_SECTION, BADGE_SECTION, CLIP_BADGE_ORDER, BADGE_SIZE, badgeAt, badgeParity, clipBadgePaints, clipBadges, layoutBadges, looksFollowed, type ClipBadgeKind,
} from '../clip-badges';

const docWith = (extra: Partial<MediaClip>, length = 120): EditTimeline => doc([track('v1', 'video', [media('c1', 0, length, 0, extra)])]);
const badgesOf = (extra: Partial<MediaClip>, length = 120) => {
  const d = docWith(extra, length);
  return { d, badges: clipBadges(d.tracks[0]!.items[0]!, d) };
};
const kindsOf = (extra: Partial<MediaClip>) => badgesOf(extra).badges.map((b) => b.kind);

const keyer = { color: '#00ff00' } as unknown as ChromaKeyer;
const mask = { kind: 'rect' } as unknown as ShapeMask;
const fx = (enabled?: boolean): ClipEffect[] => [{ type: 'blur', amount: 0.5, ...(enabled === undefined ? {} : { enabled }) }];
const linearKeys = (values: number[]) => ({ value: values[0]!, keys: values.map((v, i) => ({ frame: i * 10, value: v, interp: 'linear' as const })) });

describe('clipBadges: one badge per kind of adjustment', () => {
  it('a plain clip has none', () => expect(kindsOf({})).toEqual([]));
  it('only media clips carry badges', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60)])]);
    expect(clipBadges({ ...d.tracks[0]!.items[0]!, type: 'titleClip' } as never, d)).toEqual([]);
  });
  it('motion, crop, speed, mask, keyer, effects, stabilise, reframe, corner pin', () => {
    expect(kindsOf({ transform: { scale: { value: 1.2 } } })).toEqual(['motion']);
    expect(kindsOf({ crop: { left: 0.1 } })).toEqual(['crop']);
    expect(kindsOf({ crop: { left: 0 } })).toEqual([]);
    expect(kindsOf({ playbackRate: 2 })).toEqual(['speed']);
    expect(kindsOf({ reverse: true })).toEqual(['speed']);
    expect(kindsOf({ hold: true })).toEqual(['speed']);
    expect(kindsOf({ mask })).toEqual(['mask']);
    expect(kindsOf({ keyer })).toEqual(['keyer']);
    expect(kindsOf({ effects: fx() })).toEqual(['effects']);
    expect(kindsOf({ stabilize: true })).toEqual(['stabilize']);
    expect(kindsOf({ reframe: { x: 0.5 } })).toEqual(['reframe']);
    expect(kindsOf({ cornerPin: { tl: { x: 0, y: 0 }, tr: { x: 1, y: 0 }, br: { x: 1, y: 1 }, bl: { x: 0, y: 1 } } as never })).toEqual(['cornerPin']);
  });
  it('a camera profile and a grade are colour', () => {
    expect(kindsOf({ inputProfile: 'appleLog' })).toEqual(['camera']);
    expect(kindsOf({ grade: { enabled: true, nodes: [{ id: 'n1', name: 'n', enabled: true } as never] } })).toEqual(['colour']);
  });
  it('the transform a follow writes is "follow", any other keyed motion is "motion"', () => {
    const followed = { positionX: linearKeys([0, 0.1]), positionY: linearKeys([0, 0.2]), scale: linearKeys([1, 1.1]) };
    expect(looksFollowed(followed)).toBe(true);
    expect(kindsOf({ transform: followed })).toEqual(['follow']);
    expect(looksFollowed({ positionX: linearKeys([0, 0.1]) })).toBe(false);
    expect(kindsOf({ transform: { positionX: linearKeys([0, 0.1]) } })).toEqual(['motion']);
  });
  it('badges come in a stable order', () => {
    expect(kindsOf({ keyer, mask, playbackRate: 2, crop: { left: 0.1 } })).toEqual(['crop', 'speed', 'mask', 'keyer']);
  });
  it('a switched-off effect is a hollow (disabled) badge', () => {
    expect(badgesOf({ effects: fx(false) }).badges[0]!.enabled).toBe(false);
    expect(badgesOf({ effects: fx(true) }).badges[0]!.enabled).toBe(true);
  });
});

describe('toggle and remove are catalogue ops that apply cleanly', () => {
  const cases: [string, Partial<MediaClip>][] = [
    ['motion', { transform: { scale: { value: 1.2 } } }],
    ['crop', { crop: { left: 0.1 } }],
    ['speed', { playbackRate: 2 }],
    ['speed reverse', { reverse: true }],
    ['speed hold', { hold: true }],
    ['speed optical flow', { playbackRate: 0.5, retimeQuality: 'opticalFlow' }],
    ['colour', { grade: { enabled: true, nodes: [{ id: 'n1', name: 'n', enabled: true } as never] }, inputProfile: 'appleLog' }],
    ['mask', { mask }],
    ['keyer', { keyer }],
    ['effects', { effects: fx() }],
    ['stabilize', { stabilize: true }],
    ['reframe', { reframe: { x: 0.5 } }],
  ];
  it.each(cases)('%s: remove applies and clears the badge', (_name, extra) => {
    const { d, badges } = badgesOf(extra);
    expect(badges.length).toBeGreaterThan(0);
    for (const badge of badges) {
      expect(badge.remove.length).toBeGreaterThan(0);
      let next = d;
      for (const op of badge.remove) next = apply(next, op).doc;
      expect(clipBadges(next.tracks[0]!.items[0]!, next).map((b) => b.kind)).not.toContain(badge.kind);
    }
  });
  it('speed remove keeps the same footage: 2x over 60 frames becomes 1x over 120', () => {
    const { d, badges } = badgesOf({ playbackRate: 2 }, 60);
    let next = d;
    for (const op of badges[0]!.remove) next = apply(next, op).doc;
    const clip = next.tracks[0]!.items[0] as MediaClip;
    expect(clip.playbackRate ?? 1).toBe(1);
    expect(clip.recordOut - clip.recordIn).toBe(120);
  });
  it('effects: turn off then on again', () => {
    const { d, badges } = badgesOf({ effects: fx() });
    const off = apply(d, badges[0]!.toggle![0]!).doc;
    const offBadge = clipBadges(off.tracks[0]!.items[0]!, off)[0]!;
    expect(offBadge.enabled).toBe(false);
    const on = apply(off, offBadge.toggle![0]!).doc;
    expect(clipBadges(on.tracks[0]!.items[0]!, on)[0]!.enabled).toBe(true);
  });
  it('colour: turning the clip grade off keeps the badge, hollow', () => {
    const { d, badges } = badgesOf({ grade: { enabled: true, nodes: [{ id: 'n1', name: 'n', enabled: true } as never] } });
    const off = apply(d, badges[0]!.toggle![0]!).doc;
    expect(off.tracks[0]!.items[0]).toMatchObject({ grade: { enabled: false } });
  });
  it('kinds without an on/off state offer no toggle', () => {
    expect(badgesOf({ keyer }).badges[0]!.toggle).toBeNull();
    expect(badgesOf({ crop: { left: 0.1 } }).badges[0]!.toggle).toBeNull();
  });
});

describe('parity per badge (joined by feature)', () => {
  const findings = [
    { feature: 'speed' as const, parity: 'approximate' as const, reason: 'slow' },
    { feature: 'retime' as const, parity: 'approximate' as const, reason: 'blend' },
    { feature: 'keyer' as const, parity: 'renderRequired' as const, reason: 'key' },
  ];
  it('takes the worst finding of its own features only', () => {
    const { badges } = badgesOf({ playbackRate: 2, keyer });
    const speed = badges.find((b) => b.kind === 'speed')!;
    const key = badges.find((b) => b.kind === 'keyer')!;
    expect(badgeParity(speed, findings)).toEqual({ parity: 'approximate', reasons: ['slow', 'blend'] });
    expect(badgeParity(key, findings)).toEqual({ parity: 'renderRequired', reasons: ['key'] });
    expect(badgeParity({ features: ['crop' as never] }, findings).parity).toBe('faithful');
  });
  it('clipBadgePaints carries kind, enabled and parity', () => {
    const d = docWith({ keyer });
    expect(clipBadgePaints(d.tracks[0]!.items[0]!, d, findings)).toEqual([{ kind: 'keyer', enabled: true, parity: 'renderRequired' }]);
  });
});

describe('layout', () => {
  const kinds: ClipBadgeKind[] = ['crop', 'speed', 'mask'];
  it('is right-aligned inside the clip, one chip per badge', () => {
    const rects = layoutBadges(kinds, 100, 300, 10, 56);
    expect(rects).toHaveLength(3);
    expect(rects.map((r) => r.kind)).toEqual(kinds);
    const last = rects[2]!;
    expect(last.x + last.w).toBeLessThanOrEqual(400);
    expect(last.x + last.w).toBeGreaterThan(390);
    expect(rects[0]!.y).toBeGreaterThanOrEqual(10 + 18);
  });
  it('a narrow clip collapses to one +N badge', () => {
    const rects = layoutBadges(kinds, 0, 40, 10, 56);
    expect(rects).toHaveLength(1);
    expect(rects[0]).toMatchObject({ kind: 'more', count: 3, w: BADGE_MORE_WIDTH });
  });
  it('a clip too narrow even for +N, or a body too short for a row, shows nothing', () => {
    expect(layoutBadges(kinds, 0, 20, 10, 56)).toEqual([]);
    expect(layoutBadges(kinds, 0, 300, 10, 12)).toEqual([]);
    expect(layoutBadges([], 0, 300, 10, 56)).toEqual([]);
  });
  it('hit testing finds the chip under a point', () => {
    const rects = layoutBadges(kinds, 0, 300, 10, 56);
    const first = rects[0]!;
    expect(badgeAt(rects, first.x + 1, first.y + 1)?.kind).toBe('crop');
    expect(badgeAt(rects, first.x - 5, first.y + 1)).toBeNull();
    expect(BADGE_SIZE).toBe(14);
  });
});

describe('every badge opens a real section', () => {
  // The panel file that carries each anchor id; the test reads the source so an id cannot be renamed away silently.
  const FILE: Record<string, string> = {
    'inspector-section-transform': 'components/editor/panels/InspectorPanel.tsx',
    'inspector-section-crop': 'components/editor/panels/InspectorPanel.tsx',
    'follow-section': 'components/editor/panels/FollowSection.tsx',
    'inspector-speed': 'components/editor/panels/clip-depth/ClipControls.tsx',
    'inspector-section-colour': 'components/editor/color/ColorWorkspace.tsx',
    'inspector-section-camera': 'components/editor/color/ColorWorkspace.tsx',
    'inspector-section-mask': 'components/editor/compose/ComposeProperties.tsx',
    'inspector-section-reframe': 'components/editor/compose/ComposeProperties.tsx',
    'inspector-section-keyer': 'components/editor/compose/ComposeProperties.tsx',
    'inspector-section-effects': 'components/editor/compose/ComposeProperties.tsx',
    'inspector-section-stabilize': 'components/editor/compose/ComposeProperties.tsx',
    'inspector-section-corner-pin': 'components/editor/compose/ComposeProperties.tsx',
    'object-effects-section': 'components/editor/panels/ObjectEffectsSection.tsx',
  };
  it.each(CLIP_BADGE_ORDER.map((k) => [k]))('%s has a workspace, an anchor that exists in a panel, and a phone section', (kind) => {
    const section = BADGE_SECTION[kind];
    expect(section.anchor).toBeTruthy();
    const file = FILE[section.anchor];
    expect(file, `no panel file known for ${section.anchor}`).toBeTruthy();
    // Resolved from this test file (web/lib/editing/display/__tests__), so the working directory does not matter.
    const source = readFileSync(path.resolve(__dirname, '../../../..', file!), 'utf8');
    // `inspector-section-${entry}` covers mask and reframe in ComposeProperties.
    const needle = section.anchor === 'inspector-section-mask' || section.anchor === 'inspector-section-reframe' ? 'inspector-section-${entry}' : section.anchor;
    expect(source).toContain(needle);
    expect(['edit', 'compose', 'color']).toContain(section.workspace);
    if (section.workspace === 'compose') expect(section.composeEntry).toBeTruthy();
    expect(['picture', 'speed', 'objects', null]).toContain(BADGE_PHONE_SECTION[kind]);
  });
});

describe('colour and camera conversion are separate', () => {
  const graded = { grade: { enabled: true, nodes: [{ id: 'n1', name: 'n', enabled: true } as never] }, inputProfile: 'appleLog' as const };
  it('two badges; Turn off on colour reads as off and leaves the camera badge alone', () => {
    const { d, badges } = badgesOf(graded);
    expect(badges.map((b) => b.kind)).toEqual(['colour', 'camera']);
    const off = apply(d, badges[0]!.toggle![0]!).doc;
    const after = clipBadges(off.tracks[0]!.items[0]!, off);
    expect(after.find((b) => b.kind === 'colour')!.enabled).toBe(false);
    expect(after.find((b) => b.kind === 'camera')!.enabled).toBe(true);
    expect((off.tracks[0]!.items[0] as MediaClip).inputProfile).toBe('appleLog');
  });
  it('Remove colour keeps the camera profile; Remove camera keeps the grade', () => {
    const { d, badges } = badgesOf(graded);
    let a = d;
    for (const op of badges.find((b) => b.kind === 'colour')!.remove) a = apply(a, op).doc;
    expect((a.tracks[0]!.items[0] as MediaClip).grade).toBeUndefined();
    expect((a.tracks[0]!.items[0] as MediaClip).inputProfile).toBe('appleLog');
    let b2 = d;
    for (const op of badges.find((b) => b.kind === 'camera')!.remove) b2 = apply(b2, op).doc;
    expect((b2.tracks[0]!.items[0] as MediaClip).inputProfile).toBeUndefined();
    expect((b2.tracks[0]!.items[0] as MediaClip).grade).toBeDefined();
  });
});

describe('motion is position, size and rotation only', () => {
  it('opacity alone is no badge; remove keeps the opacity', () => {
    expect(kindsOf({ transform: { opacity: { value: 0.4 } } })).toEqual([]);
    const { d, badges } = badgesOf({ transform: { scale: { value: 1.2 }, opacity: { value: 0.4 } } });
    let next = d;
    for (const op of badges[0]!.remove) next = apply(next, op).doc;
    expect((next.tracks[0]!.items[0] as MediaClip).transform).toEqual({ opacity: { value: 0.4 } });
  });
  it('removing a follow keeps rotation and opacity', () => {
    const followed = { positionX: linearKeys([0, 0.1]), positionY: linearKeys([0, 0.2]), scale: linearKeys([1, 1.1]), rotation: { value: 3 }, opacity: { value: 0.9 } };
    const { d, badges } = badgesOf({ transform: followed });
    expect(badges[0]!.kind).toBe('follow');
    let next = d;
    for (const op of badges[0]!.remove) next = apply(next, op).doc;
    expect((next.tracks[0]!.items[0] as MediaClip).transform).toEqual({ rotation: { value: 3 }, opacity: { value: 0.9 } });
  });
});

describe('speed remove is the inspector reset', () => {
  it('one retime back to 1x keeping the footage, fades shrunk first when they no longer fit', () => {
    const { d, badges } = badgesOf({ playbackRate: 0.5, fadeInFrames: 30, fadeOutFrames: 30 }, 100);
    const ops = badges[0]!.remove;
    expect(ops.filter((o) => o.type === 'retime')).toHaveLength(1);
    expect(ops[ops.length - 1]).toMatchObject({ type: 'retime', playbackRate: 1, recordOut: 50 });
    expect(badges[0]!.removeKey).toBe('resetSpeed');
    let next = d;
    for (const op of ops) next = apply(next, op).doc;
    const clip = next.tracks[0]!.items[0] as MediaClip;
    expect(clip.recordOut).toBe(50);
    expect((clip.fadeInFrames ?? 0) + (clip.fadeOutFrames ?? 0)).toBeLessThanOrEqual(50);
  });
  it('reverse alone has the plain Remove', () => {
    expect(badgesOf({ reverse: true }).badges[0]!.removeKey).toBeUndefined();
  });
});

describe('the crop badge answers only for the crop finding', () => {
  it('a keyframed transform plus a crop: separate reasons', () => {
    const { d } = badgesOf({ crop: { left: 0.1 }, transform: { scale: linearKeys([1, 1.2]) } });
    const found = classifyItem(d.tracks[0]!.items[0]!, d).findings;
    const crop = clipBadges(d.tracks[0]!.items[0]!, d).find((b) => b.kind === 'crop')!;
    expect(badgeParity(crop, found).reasons).toEqual(['Transform/crop preview uses GPU bilinear sampling; the render uses swscale.']);
    const motion = clipBadges(d.tracks[0]!.items[0]!, d).find((b) => b.kind === 'motion')!;
    expect(badgeParity(motion, found).reasons[0]).toMatch(/Keyframed transform/);
  });
});
