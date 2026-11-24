import { describe, expect, it } from 'vitest';
import { classifyItem, classifyParity, parityAtFrame, type StabilizeLookup } from '../parity';
import { STABILIZE_FAILED_REASON, STABILIZE_READY_REASON, stabilizePreparingReason } from '../stabilize-proxy';
import { doc, media, track } from '../../__tests__/fixtures';
import type { DspSupport } from '../../audio/dsp-preview';

const dsp: DspSupport = { nodes: true, worklet: true };
const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { stabilize: true })])]);
const item = d.tracks[0]!.items[0]!;

describe('stabilise parity', () => {
  it('without a proxy lookup it stays render-required (unchanged)', () => {
    const r = classifyItem(item, d, { dsp });
    expect(r.parity).toBe('renderRequired');
    expect(r.findings.map((f) => f.feature)).toEqual(['stabilize']);
    expect(r.reason).toBe('Stabilisation is a two-pass render effect; the preview shows the unstabilised picture.');
  });
  it('while the proxy is being made: render required, with the percent', () => {
    const lookup: StabilizeLookup = () => ({ state: 'preparing', percent: 42 });
    const r = classifyItem(item, d, { dsp, stabilizeFor: lookup });
    expect(r.parity).toBe('renderRequired');
    expect(r.reason).toBe(stabilizePreparingReason(42));
    expect(r.reason).toBe('Stabilised preview is being prepared (42%).');
  });
  it('a failed proxy: render required, says the preview is not available and the export still stabilises', () => {
    const r = classifyItem(item, d, { dsp, stabilizeFor: () => ({ state: 'failed' }) });
    expect(r.parity).toBe('renderRequired');
    expect(r.reason).toBe(STABILIZE_FAILED_REASON);
    expect(r.reason).not.toContain('being prepared');
  });
  it('percent unknown: the plain sentence', () => {
    const r = classifyItem(item, d, { dsp, stabilizeFor: () => ({ state: 'preparing', percent: null }) });
    expect(r.reason).toBe('Stabilised preview is being prepared.');
  });
  it('when ready: approximate, the whole-source sentence', () => {
    const r = classifyItem(item, d, { dsp, stabilizeFor: () => ({ state: 'ready' }) });
    expect(r.parity).toBe('approximate');
    expect(r.reason).toBe(STABILIZE_READY_REASON);
  });
  it('a lookup with no answer for the clip keeps the old rule; a clip without stabilise is untouched', () => {
    expect(classifyItem(item, d, { dsp, stabilizeFor: () => null }).parity).toBe('renderRequired');
    const plain = doc([track('v1', 'video', [media('c2', 0, 60)])]);
    expect(classifyItem(plain.tracks[0]!.items[0]!, plain, { dsp, stabilizeFor: () => ({ state: 'ready' }) }).parity).toBe('faithful');
  });
  it('the summary functions thread the lookup and count the clip as approximate', () => {
    const lookup: StabilizeLookup = () => ({ state: 'ready' });
    const all = classifyParity(d, { dsp, stabilizeFor: lookup });
    expect(all.renderRequiredCount).toBe(0);
    expect(all.approximateCount).toBe(1);
    const at = parityAtFrame(d, 10, { dsp, stabilizeFor: lookup });
    expect(at.worst).toBe('approximate');
  });
});
