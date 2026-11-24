/** @vitest-environment node */
/**
 * PA-18 and PA-13: the parity badge never says "faithful" about a picture the viewer is not
 * showing as rendered, and a clip the viewer cannot draw says why instead of showing black.
 */

import { describe, expect, it } from 'vitest';
import { classifyItem, classifyParity, parityAtFrame } from '../parity';
import { notReadyClipsAt, previewBadge, viewerNotice } from '../honesty';
import type { ProxyStatus } from '../proxy-urls';
import { doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';

const oneClip = (): EditTimeline => doc([track('v1', 'video', [media('c1', 0, 60)]), track('a1', 'audio', [media('a1', 0, 60)])]);
const flags = () => ({ locked: false, enabled: true, mute: false, solo: false });
const proxy: ProxyStatus = { state: 'proxy', url: '/p' };

describe('parity reads the colour group a clip belongs to', () => {
  it('a clip graded through a group with a LUT is faithful; approximate while its file loads', () => {
    const d: EditTimeline = {
      ...oneClip(),
      colorGroups: [{ id: 'g1', name: 'Group', clipIds: ['c1'], grade: { enabled: true, nodes: [{ id: 'n1', lut: { file: { kind: 'projectFile', projectFileId: 'lut' } } }] } }],
    };
    expect(classifyParity(d).items.find((i) => i.itemId === 'c1')!.parity).toBe('faithful');
    expect(parityAtFrame(d, 10).worst).toBe('faithful');
    const clip = d.tracks[0]!.items[0]!;
    expect(classifyItem(clip, d, { gradeLutStatus: () => 'loading' }).parity).toBe('approximate');
    expect(classifyItem(clip, d, { gradeLutStatus: () => 'ready' }).parity).toBe('faithful');
    const failed = classifyItem(clip, d, { gradeLutStatus: () => 'failed' });
    expect(failed.parity).toBe('approximate');
    expect(failed.reason).toBe('The LUT file could not be loaded, so the preview shows the clip without it.');
  });

  it('a group secondary is faithful (the live pass applies it); a bypassed group stack does not count', () => {
    const secondary: EditTimeline = {
      ...oneClip(),
      colorGroups: [{ id: 'g1', name: 'Group', clipIds: ['c1'], grade: { nodes: [{ id: 'n1', qualifier: { hueCenter: 20, hueWidth: 30 } }] } }],
    };
    const bypassed: EditTimeline = {
      ...oneClip(),
      colorGroups: [{ id: 'g1', name: 'Group', clipIds: ['c1'], grade: { enabled: false, nodes: [{ id: 'n1', lut: { file: { kind: 'projectFile', projectFileId: 'lut' } } }] } }],
    };
    expect(parityAtFrame(secondary, 10).worst).toBe('faithful');
    expect(parityAtFrame(bypassed, 10).worst).toBe('faithful');
  });
});

describe('previewBadge', () => {
  const d = oneClip();
  const parity = parityAtFrame(d, 10);

  it('reads faithful only when the live session drew every layer from a proxy', () => {
    const badge = previewBadge(parity, { sessionKind: 'live', sessionReason: null, missing: [], notReady: [] });
    expect(badge.kind).toBe('faithful');
    expect(badge.label).toBe('Preview faithful');
  });

  it('reads "not ready" on a clip with no proxy', () => {
    const notReady = notReadyClipsAt(d, 10, flags, () => ({ state: 'original', url: '/o' }));
    expect(notReady.map((n) => n.itemId)).toEqual(['c1']);
    const badge = previewBadge(parity, { sessionKind: 'live', sessionReason: null, missing: [], notReady });
    expect(badge.kind).toBe('notReady');
    expect(badge.label).toMatch(/not ready/i);
    expect(badge.reasons.join(' ')).toMatch(/proxy/i);
  });

  it('audio clips never make the picture "not ready" (audio has no proxy by design)', () => {
    const notReady = notReadyClipsAt(d, 10, flags, (clip: MediaClip) => (clip.id === 'a1' ? { state: 'original', url: '/o' } : proxy));
    expect(notReady).toEqual([]);
  });

  it('is never faithful with a missing frame', () => {
    const badge = previewBadge(parity, { sessionKind: 'live', sessionReason: null, missing: [{ itemId: 'c1', trackId: 'v1', cause: 'noFrame' }], notReady: [] });
    expect(badge.kind).not.toBe('faithful');
  });

  it('is never faithful in the single-clip fallback, and names the reason', () => {
    const badge = previewBadge(parity, { sessionKind: 'fallback', sessionReason: 'Single clip only.', missing: [], notReady: [] });
    expect(badge.kind).toBe('approximate');
    expect(badge.reasons).toContain('Single clip only.');
  });

  it('is not ready when there is no session at all', () => {
    const badge = previewBadge(parity, { sessionKind: 'unavailable', sessionReason: 'No WebGL2.', missing: [], notReady: [] });
    expect(badge.kind).toBe('notReady');
  });

  it('keeps render-required visible beside the parity reasons', () => {
    const masked = doc([track('v1', 'video', [media('c1', 0, 60, 0, { stabilize: true })])]);
    const badge = previewBadge(parityAtFrame(masked, 10), { sessionKind: 'live', sessionReason: null, missing: [], notReady: [] });
    expect(badge.kind).toBe('renderRequired');
  });

  describe('R04.2 the list of reasons', () => {
    const live = { sessionKind: 'live', sessionReason: null, missing: [], notReady: [] } as const;

    it('a slow-motion clip is listed by its name with its id, and the badge is approximate', () => {
      const slow = doc([track('v1', 'video', [media('c1', 0, 60, 0, { name: 'Interview', playbackRate: 0.5 })])]);
      const badge = previewBadge(parityAtFrame(slow, 10), live);
      expect(badge.kind).toBe('approximate');
      expect(badge.entries).toHaveLength(1);
      expect(badge.entries[0].itemId).toBe('c1');
      expect(badge.entries[0].text).toMatch(/^Clip "Interview": Slow motion/);
      expect(badge.entries[0].text).toMatch(/final render is exact/);
    });

    it('a faithful picture on a live session has no reasons', () => {
      const badge = previewBadge(parityAtFrame(oneClip(), 10), live);
      expect(badge).toMatchObject({ kind: 'faithful', label: 'Preview faithful', entries: [], reasons: [] });
    });

    it('the simple preview is labelled and never faithful, even for plain footage', () => {
      const badge = previewBadge(parityAtFrame(oneClip(), 10), { ...live, sessionKind: 'fallback', sessionReason: 'One clip at a time.' });
      expect(badge.kind).toBe('approximate');
      expect(badge.label).toBe('Simple preview');
      expect(badge.entries[0].text).toMatch(/^Simple preview: One clip at a time\. Your edits are still exact in the final render\./);
    });

    it('software rendering (reduced quality) is never faithful', () => {
      const badge = previewBadge(parityAtFrame(oneClip(), 10), { ...live, reducedQuality: 'This browser draws with software graphics.' });
      expect(badge.kind).toBe('approximate');
      expect(badge.entries.map((e) => e.text)).toContain('Reduced quality: This browser draws with software graphics.');
    });

    it('a clip with a missing frame is listed with its id; an unavailable session says so', () => {
      const missing = previewBadge(parityAtFrame(oneClip(), 10), { ...live, missing: [{ itemId: 'c1', trackId: 'v1', cause: 'noFrame' }] });
      expect(missing.kind).toBe('notReady');
      expect(missing.entries[0]).toMatchObject({ itemId: 'c1', text: 'Clip "c1": A layer has no picture at this frame yet.' });
      const none = previewBadge(parityAtFrame(oneClip(), 10), { ...live, sessionKind: 'unavailable', sessionReason: 'No WebGL2.' });
      expect(none.entries[0]).toMatchObject({ text: 'No WebGL2.' });
    });

    it('lists no GUIDs or op names', () => {
      const text = previewBadge(parityAtFrame(doc([track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 2 })])]), 10), live).entries.map((e) => e.text).join(' ');
      expect(text).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}|playbackRate|retime/i);
    });
  });
});

describe('viewerNotice (PA-13)', () => {
  const d = oneClip();
  it('says "Preparing preview…" while the lookup is pending', () => {
    expect(viewerNotice(d, [{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' }], () => ({ state: 'pending' }))).toBe('Preparing preview…');
  });

  it('names the failure in plain words', () => {
    const unavailable = viewerNotice(d, [{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' }], () => ({ state: 'unavailable', reason: 'This source has no proxy yet.' }));
    expect(unavailable).toBe('This source has no proxy yet.');
    const failed = viewerNotice(d, [{ itemId: 'c1', trackId: 'v1', cause: 'openFailed' }], () => ({ state: 'original', url: '/o' }));
    expect(failed).toMatch(/could not be opened/);
    expect(failed).not.toMatch(/hover|beta|[0-9a-f]{8}-[0-9a-f]{4}/i);
  });

  it('says nothing when every layer was drawn', () => {
    expect(viewerNotice(d, [], () => proxy)).toBeNull();
  });
});

describe('previewBadge: graphics honesty', () => {
  const parity = () => parityAtFrame(oneClip(), 10);
  it('a lost graphics context is "Preview not ready", never faithful, and clears with the loss', () => {
    const lost = previewBadge(parity(), { sessionKind: 'live', sessionReason: null, missing: [], notReady: [], contextLoss: 'The graphics context was lost. Reconnecting…' });
    expect(lost.kind).toBe('notReady');
    expect(lost.reasons).toContain('The graphics context was lost. Reconnecting…');
    const back = previewBadge(parity(), { sessionKind: 'live', sessionReason: null, missing: [], notReady: [], contextLoss: null });
    expect(back.kind).toBe('faithful');
  });
  it('the Simple preview with a grade says it shows the primaries approximately', () => {
    const badge = previewBadge(parity(), { sessionKind: 'fallback', sessionReason: 'x', missing: [], notReady: [], simpleGrade: true });
    expect(badge.label).toBe('Simple preview');
    expect(badge.reasons).toContain("The simple preview shows the grade's primaries approximately.");
    const none = previewBadge(parity(), { sessionKind: 'fallback', sessionReason: 'x', missing: [], notReady: [] });
    expect(none.reasons).not.toContain("The simple preview shows the grade's primaries approximately.");
  });

  it('every badge line and label also carries a message that exists in both catalogues (IT/EN)', async () => {
    const { ok } = await import('../../__tests__/helpers/catalog');
    const cases = [
      { sessionKind: 'fallback' as const, sessionReason: null, missing: [], notReady: [], simpleGrade: true },
      { sessionKind: 'live' as const, sessionReason: null, missing: [], notReady: [], reducedQuality: 'x', contextLoss: 'The graphics context was lost. Reconnecting…' },
      { sessionKind: 'unavailable' as const, sessionReason: null, missing: [], notReady: [] },
    ];
    for (const c of cases) {
      const badge = previewBadge(parity(), c);
      ok(badge.labelMsg);
      expect(badge.entries.length).toBeGreaterThan(0);
      for (const e of badge.entries) ok(e.msg);
    }
    ok(previewBadge(parity(), { sessionKind: 'live', sessionReason: null, missing: [], notReady: [] }).labelMsg);
  });
});
