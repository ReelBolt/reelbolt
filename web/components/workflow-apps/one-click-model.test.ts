import { describe, expect, it } from 'vitest';
import type { AppManifest } from '@/lib/types/workflow';
import { decideOneClick, reviewBlockDetail, type OneClickInput } from './one-click-model';

const clip = (id: string) => ({ id, originalFileName: `${id}.mp4`, mimeType: 'video/mp4', category: 'userFiles' });
const manifest: AppManifest = { version: 1, inputs: [{ kind: 'files', role: 'video' }, { kind: 'notes' }] };

const base = (over: Partial<OneClickInput> = {}): OneClickInput => ({
  manifest,
  requiresUserInput: false,
  mediaEgress: null,
  files: [clip('a'), clip('b')],
  findings: [],
  estimate: { billingEnabled: true, affordable: true, highCredits: 20, balanceCredits: 100 },
  costClass: 'Some',
  ...over,
});

describe('decideOneClick', () => {
  it('runs in one click and fills the footage slot with the uploaded clips', () => {
    const d = decideOneClick(base());
    expect(d.mode).toBe('oneClick');
    if (d.mode === 'oneClick') expect(d.brief.sourceFiles).toEqual([{ role: 'Video', fileIds: ['a', 'b'] }]);
  });

  it('reviews when notes are required', () => {
    expect(decideOneClick(base({ requiresUserInput: true }))).toEqual({ mode: 'review', reason: 'notes' });
  });

  it('reviews when there is no footage for a required slot or more clips than the slot takes', () => {
    expect(decideOneClick(base({ files: [] }))).toEqual({ mode: 'review', reason: 'footage' });
    const capped: AppManifest = { version: 1, inputs: [{ kind: 'files', role: 'video', max: 1 }] };
    expect(decideOneClick(base({ manifest: capped }))).toEqual({ mode: 'review', reason: 'footage' });
    expect(decideOneClick(base({ manifest: capped, files: [clip('a')] })).mode).toBe('oneClick');
  });

  it('reviews when the app has choices of its own or an avatar', () => {
    const own: AppManifest = { version: 2, inputs: [{ kind: 'files', role: 'video' }, { kind: 'input', id: 'topic', type: 'shortText' }] };
    expect(decideOneClick(base({ manifest: own }))).toEqual({ mode: 'review', reason: 'inputs' });
    const avatar: AppManifest = { version: 1, inputs: [{ kind: 'files', role: 'video' }, { kind: 'brief', field: 'avatar' }] };
    expect(decideOneClick(base({ manifest: avatar }))).toEqual({ mode: 'review', reason: 'inputs' });
  });

  it('never consents for the person: a needed permission means review', () => {
    const egress = { inputId: 'allowMediaEgress', required: true, alreadyAllowed: false, steps: [] };
    expect(decideOneClick(base({ mediaEgress: egress, findings: null }))).toEqual({ mode: 'review', reason: 'consent' });
    const flagged = [{ ruleId: 'WL015', severity: 'error' as const }];
    expect(decideOneClick(base({ mediaEgress: { ...egress, required: false }, findings: flagged }))).toEqual({ mode: 'review', reason: 'consent' });
    expect(decideOneClick(base({ mediaEgress: { ...egress, alreadyAllowed: true } })).mode).toBe('oneClick');
  });

  it('reviews on a lint error, an unanswered check, a missing estimate and a shortfall', () => {
    expect(decideOneClick(base({ findings: [{ ruleId: 'WL001', severity: 'error' }] }))).toEqual({ mode: 'review', reason: 'check' });
    expect(decideOneClick(base({ findings: [{ ruleId: 'WL002', severity: 'warning' }] })).mode).toBe('oneClick');
    expect(decideOneClick(base({ findings: null }))).toEqual({ mode: 'review', reason: 'check' });
    expect(decideOneClick(base({ estimate: null }))).toEqual({ mode: 'review', reason: 'estimate' });
    expect(decideOneClick(base({ estimate: { billingEnabled: true, affordable: false, highCredits: 20, balanceCredits: 1 } }))).toEqual({ mode: 'review', reason: 'shortfall' });
    expect(decideOneClick(base({ estimate: { billingEnabled: false, affordable: false, highCredits: 20 } })).mode).toBe('oneClick');
  });

  it('reviews a run the run page would confirm first (50 credits or more at most)', () => {
    const big = { billingEnabled: true, affordable: true, highCredits: 40, fixedMaxCredits: 10, balanceCredits: 100 };
    expect(decideOneClick(base({ estimate: big }))).toEqual({ mode: 'review', reason: 'cost' });
    expect(decideOneClick(base({ estimate: { ...big, fixedMaxCredits: 9 } })).mode).toBe('oneClick');
    expect(decideOneClick(base({ estimate: big, costClass: 'No credits' })).mode).toBe('oneClick');
  });

  it('reviews a billed run that has no balance to show in the price line', () => {
    const noBalance = { billingEnabled: true, affordable: true, highCredits: 20, balanceCredits: null };
    expect(decideOneClick(base({ estimate: noBalance }))).toEqual({ mode: 'review', reason: 'estimate' });
  });

  it('reviews a required permission that is not allowed even when the check is silent about it', () => {
    const egress = { inputId: 'allowMediaEgress', required: true, alreadyAllowed: false, steps: [] };
    expect(decideOneClick(base({ mediaEgress: egress, findings: [] }))).toEqual({ mode: 'review', reason: 'consent' });
  });

  it('sends the suggested style only when it is a preset that exists', () => {
    const suggested: AppManifest = { ...manifest, suggestedStylePreset: 'punchy' };
    const withPreset = decideOneClick(base({ manifest: suggested, stylePresetKeys: ['punchy'] }));
    const without = decideOneClick(base({ manifest: suggested, stylePresetKeys: ['calm'] }));
    if (withPreset.mode === 'oneClick') expect(withPreset.brief.stylePreset).toBe('punchy');
    else throw new Error('expected one click');
    if (without.mode === 'oneClick') expect(without.brief.stylePreset).toBeUndefined();
    else throw new Error('expected one click');
  });

  it('maps every review reason to a closed block token', () => {
    expect(reviewBlockDetail('notes')).toBe('notes_required');
    expect(reviewBlockDetail('shortfall')).toBe('credits');
    expect(reviewBlockDetail('cost')).toBe('estimate_gate');
    expect(reviewBlockDetail('consent')).toBe('readiness');
  });
});
