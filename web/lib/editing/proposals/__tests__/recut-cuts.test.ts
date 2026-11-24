import { describe, expect, it } from 'vitest';
import { assertInvariants, EditEngineError } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';
import type { EditProposalGroupResponse, EditProposalResponse } from '../../sync/api';
import { ghostDiff, planProposal, ProposalNeedsAcknowledgement, programFor, userOwnedTouchedBy } from '../apply-proposal';
import { didLineMsg, swapsRefusedMsg, analysisNeedOf } from '../labels-msg';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import { stableT, type BaseTranslator } from '@/i18n/translator';

const createT = (locale: 'en' | 'it') => {
  const messages = loadMessages(locale);
  const root = createTranslator({ locale, messages }) as unknown as BaseTranslator;
  return stableT(root, root);
};

const V = '11111111-0000-4000-8000-000000000001';
const A = '11111111-0000-4000-8000-000000000002';
const TV = '55555555-0000-4000-8000-000000000001';
const TA = '55555555-0000-4000-8000-000000000002';
const generated = { origin: 'generated' as const, editedByUser: false };

/** Video + linked sound, 20 s at 30 fps, playing the file from 2 s in (sourceIn 60 frames). */
function fixture(provenance: MediaClip['provenance'] = generated): EditTimeline {
  const extra = { sourceIn: 60, linkId: '22222222-0000-4000-8000-000000000001', provenance };
  const base = doc([track(TV, 'video', [media(V, 0, 600, 60, extra)]), track(TA, 'audio', [media(A, 0, 600, 60, extra)])]);
  return { ...base, id: '44444444-0000-4000-8000-000000000001', projectId: '44444444-0000-4000-8000-000000000002' };
}
const cutGroup = (label: string, startSec: number, endSec: number, itemId = V): EditProposalGroupResponse => ({ label, ops: [], touchedItemIds: [itemId], touchesUserOwned: false, reason: null, cuts: [{ itemId, startSec, endSec }] });
const proposal = (groups: EditProposalGroupResponse[]): Pick<EditProposalResponse, 'groups'> => ({ groups });
const spans = (d: EditTimeline, trackId: string) => d.tracks.find((t) => t.id === trackId)!.items.map((i) => [i.recordIn, i.recordOut]);

describe('recut content cuts', () => {
  const two = proposal([cutGroup('Cut 3-4 s', 3, 4), cutGroup('Cut 6-7 s', 6, 7)]);

  it('expands two accepted cut groups into one program with the right remaining spans on both tracks', () => {
    const d = fixture();
    const plan = planProposal(d, two, new Set([0, 1]), new Set([V, A]));
    // source 3-4 s = frames 90-120 = record 30-60; source 6-7 s = record 120-150
    expect(spans(plan.doc, TV)).toEqual([[0, 30], [30, 90], [90, 540]]);
    expect(spans(plan.doc, TA)).toEqual([[0, 30], [30, 90], [90, 540]]);
    expect(plan.ops.length).toBeGreaterThan(0);
    expect(plan.ops.every((op) => op.type === 'blade' || op.type === 'extract' || op.type === 'setProperty')).toBe(true);
    assertInvariants(plan.doc);
  });

  it('partial accept of one cut group removes only its span', () => {
    const plan = planProposal(fixture(), two, new Set([1]), new Set([V, A]));
    expect(spans(plan.doc, TV)).toEqual([[0, 120], [120, 570]]);
    expect(spans(plan.doc, TA)).toEqual([[0, 120], [120, 570]]);
  });

  it('refuses, naming the group, when the cut item is gone', () => {
    const stale = proposal([cutGroup('Cut the intro', 3, 4, '33333333-0000-4000-8000-000000000009')]);
    expect(() => planProposal(fixture(), stale, new Set([0]))).toThrow(EditEngineError);
    expect(() => planProposal(fixture(), stale, new Set([0]))).toThrow(/Cut the intro/);
    expect(ghostDiff(fixture(), stale, new Set([0])).failed).toEqual([0]);
  });

  it('needs the acknowledgement for a cut on a manual clip, partners included', () => {
    const d = fixture(null);
    expect(userOwnedTouchedBy(d, two.groups[0]).sort()).toEqual([A, V].sort());
    expect(() => planProposal(d, two, new Set([0]))).toThrow(ProposalNeedsAcknowledgement);
    expect(() => planProposal(d, two, new Set([0]), new Set([V]))).toThrow(ProposalNeedsAcknowledgement);
    expect(planProposal(d, two, new Set([0]), new Set([V, A])).userOwned.sort()).toEqual([A, V].sort());
  });

  it('puts the cuts before any op group in the program', () => {
    const op = { type: 'setProperty' as const, itemId: V, key: 'enabled' as const, value: true };
    const other = '11111111-0000-4000-8000-000000000077';
    const mixed = proposal([{ label: 'x', ops: [{ ...op, itemId: other }], touchedItemIds: [other], touchesUserOwned: false, reason: null }, cutGroup('Cut', 3, 4)]);
    const op2 = mixed.groups[0].ops[0];
    const program = programFor(mixed, new Set([0, 1]), fixture());
    expect(program[program.length - 1]).toBe(op2);
    expect(program[0].type).toBe('blade');
  });

  it('ghostDiff yields remove and change ghosts for a cut group', () => {
    const { ghosts, failed } = ghostDiff(fixture(), proposal([cutGroup('Cut', 3, 4)]), new Set([0]));
    expect(failed).toEqual([]);
    expect(ghosts.some((g) => g.kind === 'remove')).toBe(true);
    expect(ghosts.some((g) => g.kind === 'change')).toBe(true);
    expect(ghosts.every((g) => g.groupIndex === 0)).toBe(true);
  });
});

describe('what it did', () => {
  const line = (locale: 'en' | 'it', did: EditProposalResponse['did']) => {
    const m = didLineMsg(did);
    return m ? createT(locale).msg(m) : null;
  };
  it('words the cut count and length in English with plurals', () => {
    expect(line('en', { cut: 7, cutSeconds: 12.4, remove: 0, trim: 0, mute: 0, other: 0 })).toBe('What it did: cut 7 spoken passages (12.4 s).');
    expect(line('en', { cut: 1, cutSeconds: 2, remove: 0, trim: 1, mute: 2, other: 0 })).toBe('What it did: cut 1 spoken passage (2.0 s), trimmed 1 clip, muted 2 clips.');
    expect(line('en', { cut: 0, cutSeconds: 0, remove: 0, trim: 0, mute: 0, other: 0 })).toBeNull();
  });
  it('words it in Italian', () => {
    expect(line('it', { cut: 7, cutSeconds: 12.4, remove: 1, trim: 0, mute: 0, other: 0 })).toBe('Cosa ha fatto: tagliati 7 passaggi parlati (12,4 s), 1 clip rimossa.');
  });
  it('words one part per kind of cut, with the locale number format', () => {
    const did = { cut: 0, cutSeconds: 0, remove: 0, trim: 0, mute: 0, other: 0, cuts: [
      { kind: 'quiet' as const, spans: 3, seconds: 4.1 }, { kind: 'fillers' as const, spans: 5, seconds: 1.2 }, { kind: 'lines' as const, spans: 2, seconds: 6 }, { kind: 'keep' as const, spans: 3, seconds: 20.5 } ] };
    expect(line('en', did)).toBe('What it did: cut 3 silences (4.1 s), cut 5 filler words (1.2 s), cut 2 lines (6.0 s), kept only the chosen lines, cutting 3 stretches (20.5 s).');
    expect(line('it', did)).toContain('(4,1 s)');
  });
  it('says so only when swaps were refused', () => {
    expect(swapsRefusedMsg({ swapsRefused: 0 })).toBeNull();
    expect(createT('en').msg(swapsRefusedMsg({ swapsRefused: 2 })!)).toBe('Skipped changes that did not match your request.');
    expect(createT('it').msg(swapsRefusedMsg({ swapsRefused: 2 })!)).toContain('saltate');
  });
  it('reads the analysis need, dropping unknown kinds and malformed refs', () => {
    expect(analysisNeedOf({ kinds: ['transcript', 'bogus'], mediaRefs: ['file:3f2b8c1e-0a41-4c55-9d4e-1b7f6a9c2d11', 'nope'] })).toEqual({ kinds: ['transcript'], mediaRefs: ['file:3f2b8c1e-0a41-4c55-9d4e-1b7f6a9c2d11'] });
    expect(analysisNeedOf(null)).toBeNull();
  });
});
