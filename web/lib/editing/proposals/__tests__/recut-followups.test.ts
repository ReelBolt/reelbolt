// Regression tests for the recut follow-ups: linked partners, blades since launch, reversed twins, ghost seeding, copy.
import { describe, expect, it } from 'vitest';
import { applyAll, assertInvariants } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';
import type { EditProposalGroupResponse, EditProposalResponse } from '../../sync/api';
import { expandCuts, ghostDiff, planProposal } from '../apply-proposal';
import { didLineMsg, swapsRefusedMsg } from '../labels-msg';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import { stableT, type BaseTranslator } from '@/i18n/translator';
import en from '@/messages/en/editor-ai-tools.json';
import it_ from '@/messages/it/editor-ai-tools.json';

const g = (n: number) => `11111111-0000-4000-8000-${String(n).padStart(12, '0')}`;
const V = g(1);
const A = g(2);
const W = g(3);
const TV = '55555555-0000-4000-8000-000000000001';
const TA = '55555555-0000-4000-8000-000000000002';
const LINK = '22222222-0000-4000-8000-000000000001';
const gen = { origin: 'generated' as const, editedByUser: false };
const otherFile = (n: number) => ({ kind: 'projectFile', projectFileId: `99999999-0000-4000-8000-00000000000${n}` }) as MediaClip['assetRef'];

const wrap = (d: EditTimeline): EditTimeline => ({ ...d, id: '44444444-0000-4000-8000-000000000001', projectId: '44444444-0000-4000-8000-000000000002' });
const clip = (id: string, ri: number, ro: number, si: number, extra: Partial<MediaClip> = {}) => media(id, ri, ro, si, { provenance: gen, ...extra });
const cutGroup = (label: string, cuts: Array<[string, number, number]>): EditProposalGroupResponse => ({ label, ops: [], touchedItemIds: cuts.map((c) => c[0]), touchesUserOwned: false, reason: null, cuts: cuts.map(([itemId, startSec, endSec]) => ({ itemId, startSec, endSec })) });
const opGroup = (label: string, ops: unknown[], ids: string[]): EditProposalGroupResponse => ({ label, ops: ops as never, touchedItemIds: ids, touchesUserOwned: false, reason: null });
const P = (groups: EditProposalGroupResponse[]): Pick<EditProposalResponse, 'groups'> => ({ groups });
const spans = (d: EditTimeline, trackId: string) => d.tracks.find((t) => t.id === trackId)!.items.map((i) => [i.recordIn, i.recordOut]);
const end = (d: EditTimeline, trackId: string) => Math.max(...spans(d, trackId).map((s) => s[1]));

describe('reversed and hold clips', () => {
  it('P1 a reversed UNLINKED twin of the cut clip is not cut at the same record frames (its source runs backwards)', () => {
    // V plays source 0..20 s forwards; A (same file, same window) plays it reversed: record 30..60 on A is source ~18..19 s, not 1..2 s.
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 0)]), track(TA, 'audio', [clip(A, 0, 600, 0, { reverse: true })])]));
    let plan: ReturnType<typeof planProposal> | null = null;
    try {
      plan = planProposal(d, P([cutGroup('c', [[V, 1, 2]])]), new Set([0]));
    } catch {
      plan = null; // a refusal is acceptable
    }
    if (plan) expect(spans(plan.doc, TA)).toEqual([[0, 600]]);
  });

  it('P2 ghostDiff of a cut on a hold clip reports the group as failed, with no cut ghost', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 0, { hold: true })])]));
    const diff = ghostDiff(d, P([cutGroup('h', [[V, 1, 2]])]), new Set([0]));
    expect(diff.failed).toEqual([0]);
    expect(diff.ghosts).toEqual([]);
  });

  it('P3 a reversed clip on its own does not block a cut on another clip in a different group', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 0), clip(W, 300, 600, 0, { reverse: true, assetRef: otherFile(8) })])]));
    expect(() => planProposal(d, P([cutGroup('ok', [[V, 1, 2]]), cutGroup('rev', [[W, 1, 2]])]), new Set([0, 1]))).toThrow(/“rev”/);
    expect(end(planProposal(d, P([cutGroup('ok', [[V, 1, 2]]), cutGroup('rev', [[W, 1, 2]])]), new Set([0])).doc, TV)).toBe(570);
  });
});

describe('linked partner already cut while its track has other cuts', () => {
  it('P4 L-cut: a cut on the linked audio TAIL (past the video end) still applies after the video cut bladed the audio', () => {
    // V 0..300, linked A 0..400 (audio runs 100 frames past the picture). Cut V 1..2 s and A's tail 11..12 s (record 330..360).
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 0, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 400, 0, { linkId: LINK })])]));
    const plan = planProposal(d, P([cutGroup('v', [[V, 1, 2]]), cutGroup('tail', [[A, 11, 12]])]), new Set([0, 1]));
    assertInvariants(plan.doc);
    expect(end(plan.doc, TV)).toBe(270);
    expect(end(plan.doc, TA)).toBe(340); // 400 - 30 (rippled with V) - 30 (its own tail cut)
  });

  it('P5 L-cut tail cut alone applies', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 0, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 400, 0, { linkId: LINK })])]));
    const plan = planProposal(d, P([cutGroup('tail', [[A, 11, 12]])]), new Set([0]));
    expect(end(plan.doc, TA)).toBe(370);
    expect(end(plan.doc, TV)).toBe(300);
  });

  it('P6 a cut on the linked partner at a DIFFERENT time than the video cut is not dropped', () => {
    // Speech only on the audio side at 8..9 s (e.g. server lists A separately); V is cut at 1..2 s.
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 0, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 600, 0, { linkId: LINK })])]));
    const plan = planProposal(d, P([cutGroup('v', [[V, 1, 2]]), cutGroup('a', [[A, 8, 9]])]), new Set([0, 1]));
    expect(end(plan.doc, TA)).toBe(540);
  });
});

describe('op groups after a ripple cut', () => {
  const base = () => wrap(doc([track(TV, 'video', [clip(V, 0, 600, 0), clip(W, 600, 900, 0, { assetRef: otherFile(8) })])]));

  it('P7 remove (extract) of the clip being cut is refused naming the op group', () => {
    const pr = P([cutGroup('c', [[V, 1, 2]]), opGroup('Remove V', [{ type: 'extract', itemId: V }], [V])]);
    expect(() => planProposal(base(), pr, new Set([0, 1]))).toThrow(/Remove V.*already being cut/);
  });

  it('P8 mute of the linked partner of a cut clip is refused (stale id), not applied to the left piece only', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 0, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 600, 0, { linkId: LINK })])]));
    const pr = P([cutGroup('c', [[V, 1, 2]]), opGroup('Mute A', [{ type: 'setProperty', itemId: A, path: 'enabled', value: false }], [A])]);
    expect(() => planProposal(d, pr, new Set([0, 1]))).toThrow(/Mute A.*already being cut/);
  });

  it('P9 a delta trim of an uncut clip to the right on the same track lands after the ripple', () => {
    const pr = P([cutGroup('c', [[V, 1, 2]]), opGroup('Trim W', [{ type: 'trim', itemId: W, edge: 'out', mode: 'ripple', deltaFrames: -30 }], [W])]);
    const plan = planProposal(base(), pr, new Set([0, 1]));
    expect(end(plan.doc, TV)).toBe(840);
    expect(JSON.stringify(applyAll(base(), plan.ops))).toBe(JSON.stringify(plan.doc));
  });
});

describe('refusal attribution across groups', () => {
  const d = () => wrap(doc([track(TV, 'video', [clip(V, 0, 600, 0)]), track(TA, 'audio', [clip(A, 0, 600, 0, { assetRef: otherFile(7) })])]));

  it('P10 the third group (clip gone) is named, the first two are not', () => {
    const pr = P([cutGroup('alpha', [[V, 1, 2]]), cutGroup('beta', [[A, 1, 2]]), cutGroup('gamma', [[g(99), 1, 2]])]);
    let message = '';
    try {
      planProposal(d(), pr, new Set([0, 1, 2]));
    } catch (e) {
      message = (e as Error).message;
    }
    expect(message).toMatch(/gamma/);
    expect(message).not.toMatch(/alpha|beta/);
  });

  it('P11 a stale span in the second group on another track is named (not the first)', () => {
    const pr = P([cutGroup('alpha', [[V, 1, 2]]), cutGroup('beta', [[A, 50, 51]])]);
    expect(() => planProposal(d(), pr, new Set([0, 1]))).toThrow(/“beta”.*no longer in the clip/);
  });

  it('P12 overlapping spans from two groups merge; each blade is credited to a group that asked for that span', () => {
    const pr = P([cutGroup('alpha', [[V, 1, 2]]), cutGroup('beta', [[V, 1.5, 3]])]);
    const { ops, owner } = expandCuts(d(), pr.groups, new Set([0, 1]));
    const blades = ops.filter((o) => o.type === 'blade') as Array<{ atFrame: number }>;
    // merged 30..90: blade at 90 belongs to beta only, blade at 30 to alpha
    const at = (f: number) => blades.filter((b) => b.atFrame === f).map((b) => owner.get(b as never));
    expect(at(30)).toEqual([0]);
    expect(at(90)).toEqual([1]);
  });
});

const createT = (locale: 'en' | 'it') => {
  const messages = loadMessages(locale);
  const root = createTranslator({ locale, messages }) as unknown as BaseTranslator;
  return stableT(root, root);
};
const did = (kind: 'speech' | 'quiet' | 'fillers', spans: number, seconds: number) => ({ cut: spans, cutSeconds: seconds, remove: 0, trim: 0, mute: 0, other: 0, cuts: [{ kind, spans, seconds }] });

describe('"what it did" line', () => {
  it('P13 en names speech / silence / fillers', () => {
    const t = createT('en');
    expect(t.msg(didLineMsg(did('speech', 2, 3))!)).toMatch(/spoken passages/);
    expect(t.msg(didLineMsg(did('quiet', 2, 3))!)).toMatch(/silences/);
    expect(t.msg(didLineMsg(did('quiet', 2, 3))!)).not.toMatch(/spoken/);
    expect(t.msg(didLineMsg(did('fillers', 1, 0.4))!)).toMatch(/filler word \(0\.4 s\)/);
  });

  it('P14 it names parlato / pause / riempimento, decimal comma and grouping', () => {
    const t = createT('it');
    expect(t.msg(didLineMsg(did('speech', 2, 3))!)).toMatch(/passaggi parlati \(3,0 s\)/);
    expect(t.msg(didLineMsg(did('quiet', 2, 12.44))!)).toMatch(/pause \(12,4 s\)/);
    expect(t.msg(didLineMsg(did('fillers', 1, 0.4))!)).toMatch(/parola di riempimento \(0,4 s\)/);
    expect(t.msg(didLineMsg(did('quiet', 2, 1234.56))!)).toMatch(/1234,6 s/) // it CLDR: no grouping below 5 digits;
  });

  it('P15 en/it key parity of editor-ai-tools.json (recursive)', () => {
    const keys = (o: unknown, p = ''): string[] => (typeof o === 'object' && o !== null ? Object.entries(o).flatMap(([k, v]) => keys(v, `${p}${k}.`)) : [p]);
    expect(keys(it_).sort()).toEqual(keys(en).sort());
  });

  it('P16 the refused-swap line does not claim "nothing was muted" when what was refused was a remove/trim (data cannot tell)', () => {
    // swapsRefused counts mute/remove/trim (api.ts doc); the line always says "Nothing was muted".
    const t = createT('en');
    expect(t.msg(swapsRefusedMsg({ swapsRefused: 1 })!)).not.toMatch(/muted/);
  });

  it('P17 multi-kind line does not capitalise each later part', () => {
    const t = createT('en');
    const m = didLineMsg({ cut: 0, cutSeconds: 0, remove: 0, trim: 0, mute: 0, other: 0, cuts: [{ kind: 'quiet', spans: 2, seconds: 1 }, { kind: 'fillers', spans: 3, seconds: 1 }] })!;
    expect(t.msg(m)).not.toMatch(/, Cut/);
  });
});
