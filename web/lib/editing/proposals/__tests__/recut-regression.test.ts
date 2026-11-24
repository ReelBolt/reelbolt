// Regression tests for recut content cuts (from QA probes, recut-follows-request).
import { describe, expect, it } from 'vitest';
import { applyAll, assertInvariants, type EditOp } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';
import type { EditProposalGroupResponse, EditProposalResponse } from '../../sync/api';
import { expandCuts, checkCutCaps, expansionStats, ProposalTooManyCuts, ghostDiff, planProposal, ProposalNeedsAcknowledgement, programFor, userOwnedTouchedBy } from '../apply-proposal';

const g = (n: number) => `11111111-0000-4000-8000-${String(n).padStart(12, '0')}`;
const V = g(1);
const A = g(2);
const W = g(3);
const M = g(4);
const TV = '55555555-0000-4000-8000-000000000001';
const TA = '55555555-0000-4000-8000-000000000002';
const LINK = '22222222-0000-4000-8000-000000000001';
const gen = { origin: 'generated' as const, editedByUser: false };

const wrap = (d: EditTimeline): EditTimeline => ({ ...d, id: '44444444-0000-4000-8000-000000000001', projectId: '44444444-0000-4000-8000-000000000002' });
const clip = (id: string, ri: number, ro: number, si: number, extra: Partial<MediaClip> = {}) => media(id, ri, ro, si, { provenance: gen, ...extra });
const cutGroup = (label: string, cuts: Array<[string, number, number]>): EditProposalGroupResponse => ({ label, ops: [], touchedItemIds: cuts.map((c) => c[0]), touchesUserOwned: false, reason: null, cuts: cuts.map(([itemId, startSec, endSec]) => ({ itemId, startSec, endSec })) });
const opGroup = (label: string, ops: unknown[], ids: string[]): EditProposalGroupResponse => ({ label, ops: ops as never, touchedItemIds: ids, touchesUserOwned: false, reason: null });
const P = (groups: EditProposalGroupResponse[]): Pick<EditProposalResponse, 'groups'> => ({ groups });
const spans = (d: EditTimeline, trackId: string) => d.tracks.find((t) => t.id === trackId)!.items.map((i) => [i.recordIn, i.recordOut]);
const srcSpans = (d: EditTimeline, trackId: string) =>
  d.tracks.find((t) => t.id === trackId)!.items.map((i) => {
    const c = i as MediaClip;
    const r = c.playbackRate ?? 1;
    return [c.sourceIn ?? 0, (c.sourceIn ?? 0) + (c.recordOut - c.recordIn) * r];
  });

describe('QA probe: source-seconds to record mapping', () => {
  it('sourceIn != 0 and playbackRate 2 cut the right record frames', () => {
    // clip at record 100..400 (300 frames), sourceIn 60, rate 2 -> source frames 60..660
    const d = wrap(doc([track(TV, 'video', [clip(V, 100, 400, 60, { playbackRate: 2 })])]));
    // source 4..5 s = frames 120..150 -> record 100 + (60/2)=130 .. 100 + (90/2)=145
    const plan = planProposal(d, P([cutGroup('c', [[V, 4, 5]])]), new Set([0]));
    expect(spans(plan.doc, TV)).toEqual([[100, 130], [130, 385]]);
    expect(srcSpans(plan.doc, TV)).toEqual([[60, 120], [150, 660]]);
    assertInvariants(plan.doc);
  });

  it('a cut partly outside the clip window is clipped to the clip', () => {
    // clip window source 60..360 (2..12 s); cut 0..3 s -> record 0..30
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 60)])]));
    const plan = planProposal(d, P([cutGroup('c', [[V, 0, 3]])]), new Set([0]));
    expect(spans(plan.doc, TV)).toEqual([[0, 270]]);
    expect(srcSpans(plan.doc, TV)).toEqual([[90, 360]]);
  });

  it('a cut wholly outside the clip window is a refusal naming its group', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 60)])]));
    expect(() => planProposal(d, P([cutGroup('Late cut', [[V, 30, 31]])]), new Set([0]))).toThrow(/Late cut/);
  });

  it('a cut on a reversed clip or a freeze frame is a refusal naming the group', () => {
    for (const extra of [{ reverse: true }, { hold: true }]) {
      const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 0, extra)])]));
      expect(() => planProposal(d, P([cutGroup('Rev cut', [[V, 1, 2]])]), new Set([0]))).toThrow(/Rev cut/);
    }
  });
});

describe('QA probe: linked A/V and twins are not double-cut', () => {
  const linked = () => wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 600, 60, { linkId: LINK })])]));

  it('cuts listed on BOTH the video and its linked audio cut each once', () => {
    const plan = planProposal(linked(), P([cutGroup('v', [[V, 3, 4]]), cutGroup('a', [[A, 3, 4]])]), new Set([0, 1]));
    expect(spans(plan.doc, TV)).toEqual([[0, 30], [30, 570]]);
    expect(spans(plan.doc, TA)).toEqual([[0, 30], [30, 570]]);
    assertInvariants(plan.doc);
  });

  it('unlinked exact twins with cuts on both are cut once', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60)]), track(TA, 'audio', [clip(A, 0, 600, 60)])]));
    const plan = planProposal(d, P([cutGroup('v', [[V, 3, 4]]), cutGroup('a', [[A, 3, 4]])]), new Set([0, 1]));
    expect(spans(plan.doc, TV)).toEqual([[0, 30], [30, 570]]);
    expect(spans(plan.doc, TA)).toEqual([[0, 30], [30, 570]]);
  });

  it('a separate clip on the partner track still gets its own cut', () => {
    // TA: camera audio A (linked to V) 0..300, then a different-file clip M 300..600 with its own cut
    const d = wrap(doc([
      track(TV, 'video', [clip(V, 0, 300, 0, { linkId: LINK })]),
      track(TA, 'audio', [clip(A, 0, 300, 0, { linkId: LINK }), media(M, 300, 600, 0, { provenance: gen, assetRef: { kind: 'projectFile', projectFileId: '99999999-0000-4000-8000-000000000009' } as MediaClip['assetRef'] })]),
    ]));
    const plan = planProposal(d, P([cutGroup('v', [[V, 1, 2]]), cutGroup('m', [[M, 1, 2]])]), new Set([0, 1]));
    // M is cut by 30 frames as well as the 30 rippled from the A cut: TA ends at 540
    const end = Math.max(...spans(plan.doc, TA).map((s) => s[1]));
    expect(end).toBe(540);
  });
});

describe('QA probe: acknowledgement', () => {
  it('a generated video with a MANUAL linked partner needs the partner acknowledged', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60, { linkId: LINK })]), track(TA, 'audio', [media(A, 0, 600, 60, { linkId: LINK, provenance: null })])]));
    const pr = P([cutGroup('v', [[V, 3, 4]])]);
    expect(userOwnedTouchedBy(d, pr.groups[0])).toEqual([A]);
    expect(() => planProposal(d, pr, new Set([0]))).toThrow(ProposalNeedsAcknowledgement);
    expect(planProposal(d, pr, new Set([0]), new Set([A])).userOwned).toEqual([A]);
  });

  it('an unlinked manual twin needs acknowledgement', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60)]), track(TA, 'audio', [media(A, 0, 600, 60, { provenance: { origin: 'manual' } as MediaClip['provenance'] })])]));
    const pr = P([cutGroup('v', [[V, 3, 4]])]);
    expect(userOwnedTouchedBy(d, pr.groups[0])).toEqual([A]);
    expect(() => planProposal(d, pr, new Set([0]))).toThrow(ProposalNeedsAcknowledgement);
  });

  it('a cut whose clip is gone still asks for nothing and refuses naming the group', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60)])]));
    const pr = P([cutGroup('Cut ghost', [[g(77), 3, 4]])]);
    expect(userOwnedTouchedBy(d, pr.groups[0])).toEqual([]);
    expect(() => planProposal(d, pr, new Set([0]))).toThrow(/Cut ghost/);
  });
});

describe('QA probe: one batch, applyAll reproduces the plan', () => {
  it('the program re-applied in one applyAll gives the planned document', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 600, 60, { linkId: LINK })])]));
    const pr = P([cutGroup('a', [[V, 3, 4], [V, 10, 11]]), cutGroup('b', [[V, 6, 7]])]);
    const plan = planProposal(d, pr, new Set([0, 1]));
    expect(JSON.stringify(applyAll(d, plan.ops))).toBe(JSON.stringify(plan.doc));
    expect(spans(plan.doc, TV)).toEqual(spans(plan.doc, TA));
    // three cuts of 30 frames
    expect(Math.max(...spans(plan.doc, TV).map((s) => s[1]))).toBe(510);
  });
});

describe('cut V + trim of its linked partner A', () => {
  it('planProposal refuses with the named op group; the trim alone applies', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 600, 60, { linkId: LINK })])]));
    const trimA = opGroup('Trim end of A', [{ type: 'trim', itemId: A, edge: 'out', mode: 'ripple', deltaFrames: -15 }], [A]);
    const pr = P([cutGroup('c', [[V, 3, 4]]), trimA]);
    expect(() => planProposal(d, pr, new Set([0, 1]))).toThrow(/Trim end of A.*already being cut/);
    expect(() => planProposal(d, pr, new Set([1]))).not.toThrow();
  });
});

describe('QA probe: partial accept mixing cut groups with trim/remove groups', () => {
  // V track: V 0..600 (source from 2 s), W 600..900
  const base = () => wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60), clip(W, 600, 900, 0, { assetRef: { kind: 'projectFile', projectFileId: '99999999-0000-4000-8000-000000000008' } as MediaClip['assetRef'] })])]));
  const removeW = opGroup('Remove W', [{ type: 'extract', itemId: W }], [W]);
  const trimTailV = opGroup('Trim end of V by 1 s', [{ type: 'trim', itemId: V, edge: 'out', mode: 'ripple', deltaFrames: -30 }], [V]);
  const trimHeadV = opGroup('Trim start of V by 1 s', [{ type: 'trim', itemId: V, edge: 'in', mode: 'ripple', deltaFrames: 30 }], [V]);

  it('cut + remove of a clip to the right: W still removed after the ripple', () => {
    const pr = P([cutGroup('c1', [[V, 5, 6]]), removeW, cutGroup('c2', [[V, 10, 11]])]);
    const plan = planProposal(base(), pr, new Set([0, 1]));
    expect(spans(plan.doc, TV)).toEqual([[0, 90], [90, 570]]);
  });

  it('an op group on a clip an accepted cut group also cuts is refused, naming the op group', () => {
    const pr = P([cutGroup('c', [[V, 5, 6]]), trimTailV]);
    expect(() => planProposal(base(), pr, new Set([0, 1]))).toThrow(/Trim end of V by 1 s.*already being cut/);
    // accepted alone, each applies
    expect(() => planProposal(base(), pr, new Set([1]))).not.toThrow();
  });

  it('a cut over the clip head + trimHead of the same clip is refused (never an op on a stale id)', () => {
    const pr = P([cutGroup('c', [[V, 2, 3]]), trimHeadV]);
    expect(() => planProposal(base(), pr, new Set([0, 1]))).toThrow(/already being cut/);
  });

  it('credits each op to the group whose span produced it, and a failing second group is named', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60)])]));
    const pr = P([cutGroup('first', [[V, 3, 4]]), cutGroup('second', [[V, 6, 7]])]);
    const { ops, owner } = expandCuts(d, pr.groups, new Set([0, 1]));
    const at = (frame: number) => ops.filter((o) => o.type === 'blade' && (o as { atFrame: number }).atFrame === frame).map((o) => owner.get(o));
    // source 3-4 s = record 30..60 (group 0); source 6-7 s = record 120..150 (group 1)
    expect(at(30)).toEqual([0]);
    expect(at(120)).toEqual([1]);
    expect(ops.filter((o) => o.type === 'extract').map((o) => owner.get(o)).sort()).toEqual([0, 1]);
    const bad = P([cutGroup('first', [[V, 3, 4]]), cutGroup('second', [[V, 40, 41]])]);
    expect(() => planProposal(d, bad, new Set([0, 1]))).toThrow(/second/);
    expect(() => planProposal(d, bad, new Set([0, 1]))).not.toThrow(/first/);
  });

  it('a cut inside the clip window that rounds to zero frames is skipped, not refused', () => {
    // rate 2, sourceIn 60: [2.00, 2.06] s is 1.8 source frames at the clip edge = 0 record frames
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60, { playbackRate: 2 })])]));
    const plan = planProposal(d, P([cutGroup('tiny', [[V, 2, 2.06]]), cutGroup('real', [[V, 5, 6]])]), new Set([0, 1]));
    expect(spans(plan.doc, TV)).toEqual([[0, 45], [45, 585]]);
    expect(planProposal(d, P([cutGroup('tiny', [[V, 2, 2.06]])]), new Set([0])).ops).toEqual([]);
  });

  it('(old) a refusal names the group whose cut produced the op', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60)])]));
    // the second group cuts a span the first group already removed from the clip window
    const pr = P([cutGroup('first', [[V, 3, 4]]), cutGroup('second', [[V, 40, 41]])]);
    expect(() => planProposal(d, pr, new Set([0, 1]))).toThrow(/second/);
  });

  it('only the selected cut group applies when the other cut group is skipped (rightmost-first inside a group)', () => {
    const pr = P([cutGroup('c1', [[V, 3, 4], [V, 12, 13]]), cutGroup('c2', [[V, 7, 8]]), removeW]);
    const plan = planProposal(base(), pr, new Set([0, 2]));
    expect(spans(plan.doc, TV)).toEqual([[0, 30], [30, 270], [270, 540]]);
    const program = programFor(pr, new Set([0, 2]), base());
    expect((program[program.length - 1] as EditOp & { itemId?: string }).itemId).toBe(W);
  });

  it('ghostDiff for a single cut group has no failures and tags its group', () => {
    const pr = P([trimTailV, cutGroup('c', [[V, 5, 6]])]);
    const { ghosts, failed } = ghostDiff(base(), pr, new Set([0, 1]));
    expect(failed).toEqual([]);
    expect(ghosts.filter((x) => x.groupIndex === 1 && x.kind === 'remove').length).toBeGreaterThan(0);
  });
});

describe('cut caps and one expansion per accept', () => {
  const many = (n: number, label = 'many'): EditProposalGroupResponse => cutGroup(label, Array.from({ length: n }, (_, i): [string, number, number] => [V, i * 2 + 0.5, i * 2 + 1]));
  const long = (frames: number) => wrap(doc([track(TV, 'video', [clip(V, 0, frames, 0, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, frames, 0, { linkId: LINK })])]));

  it('refuses a group over 200 cuts and an accept over 300 in total, by name', () => {
    const d = long(1300);
    expect(() => planProposal(d, P([many(201, 'Too big')]), new Set([0]))).toThrow(/Too big.*too many cuts/);
    expect(() => planProposal(d, P([many(200, 'a'), many(101, 'b')]), new Set([0, 1]))).toThrow(ProposalTooManyCuts);
    expect(() => checkCutCaps(P([many(200), many(100)]).groups, new Set([0, 1]))).not.toThrow();
    expect(ghostDiff(d, P([many(201)]), new Set([0])).failed).toEqual([0]);
  });

  it('a 200-span group with a linked partner plans in reasonable time, expanding once per accept', () => {
    const d = long(200 * 60 + 600);
    const pr = P([many(200)]);
    const before = expansionStats.computed;
    const t0 = performance.now();
    const plan = planProposal(d, pr, new Set([0]), new Set([]));
    userOwnedTouchedBy(d, pr.groups[0]);
    ghostDiff(d, pr, new Set([0]));
    const ms = performance.now() - t0;
    expect(expansionStats.computed - before).toBe(1);
    expect(ms).toBeLessThan(8000);
    expect(plan.doc.tracks[0].items.length).toBeGreaterThan(200);
  });
});

// Moved from QA's delta probe. The helpers below mirror the ones used above.
describe('sub-frame cuts the server may emit (MinCutSec 0.05 s) are not whole-accept refusals', () => {
  // rate 2 clip, sourceIn 60 (2 s); the server clips a spoken run that straddles the window start to [2.00, 2.06]
  const fast = () => wrap(doc([track(TV, 'video', [clip(V, 0, 300, 60, { playbackRate: 2 })])]));

  it('head-edge 0.06 s cut on a 2x clip, together with a real cut, still applies', () => {
    const pr = P([cutGroup('Cut spoken passages', [[V, 2.0, 2.06], [V, 5, 6]])]);
    expect(() => planProposal(fast(), pr, new Set([0]))).not.toThrow();
  });

  it('tail-edge 0.06 s cut on a 2x clip still applies', () => {
    // window end: 60 + 300*2 = 660 frames = 22 s
    const pr = P([cutGroup('Cut spoken passages', [[V, 5, 6], [V, 21.94, 22.0]])]);
    expect(() => planProposal(fast(), pr, new Set([0]))).not.toThrow();
  });

  it('control: the same 0.06 s head-edge cut on a 1x clip applies', () => {
    const d = wrap(doc([track(TV, 'video', [clip(V, 0, 300, 60)])]));
    expect(() => planProposal(d, P([cutGroup('c', [[V, 2.0, 2.06], [V, 5, 6]])]), new Set([0]))).not.toThrow();
  });
});

describe('memoised expansion', () => {
  const linked = () => wrap(doc([track(TV, 'video', [clip(V, 0, 600, 60, { linkId: LINK })]), track(TA, 'audio', [clip(A, 0, 600, 60, { linkId: LINK })])]));

  it('plan.ops replayed on the original doc equal plan.doc (cut-only and after ghostDiff warmed the memo)', () => {
    const d = linked();
    const pr = P([cutGroup('a', [[V, 3, 4]]), cutGroup('b', [[V, 6, 7]])]);
    ghostDiff(d, pr, new Set([0, 1]));
    const plan = planProposal(d, pr, new Set([0, 1]));
    expect(JSON.stringify(applyAll(d, plan.ops))).toBe(JSON.stringify(plan.doc));
    expect(spans(plan.doc, TV)).toEqual([[0, 30], [30, 90], [90, 540]]);
    expect(spans(plan.doc, TA)).toEqual(spans(plan.doc, TV));
  });

  it('subset accepts on the same doc are not served a superset expansion', () => {
    const d = linked();
    const pr = P([cutGroup('a', [[V, 3, 4]]), cutGroup('b', [[V, 6, 7]])]);
    planProposal(d, pr, new Set([0, 1]));
    expect(spans(planProposal(d, pr, new Set([1])).doc, TV)).toEqual([[0, 120], [120, 570]]);
    expect(spans(planProposal(d, pr, new Set([0])).doc, TV)).toEqual([[0, 30], [30, 570]]);
  });

  it('two plans of the same accept are deep-equal, and the first is untouched by the second', () => {
    const d = linked();
    const pr = P([cutGroup('a', [[V, 3, 4]])]);
    const first = planProposal(d, pr, new Set([0]));
    const snapshot = JSON.stringify(first.doc);
    const second = planProposal(d, pr, new Set([0]));
    expect(second.doc).toEqual(first.doc);
    expect(JSON.stringify(first.doc)).toBe(snapshot);
    // a fresh document (no memo hit) expands to the same spans
    expect(spans(planProposal(linked(), pr, new Set([0])).doc, TV)).toEqual(spans(first.doc, TV));
  });

  it('the input doc is not mutated by planning', () => {
    const d = linked();
    const before = JSON.stringify(d);
    planProposal(d, P([cutGroup('a', [[V, 3, 4]])]), new Set([0]));
    expect(JSON.stringify(d)).toBe(before);
  });
});
