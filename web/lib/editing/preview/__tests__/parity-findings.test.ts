import { describe, expect, it } from 'vitest';
import { classifyItem, classifyParity, OPTICAL_FLOW_REASON } from '../parity';
import { parityEntries } from '../honesty';
import { REASON_TABLES } from '../../reason-tables';
import { doc, media, track } from '../../__tests__/fixtures';
import type { ChromaKeyer, ClipMask } from '../../types';
import type { DspSupport } from '../../audio/dsp-preview';

const keyer = { color: '#00ff00' } as unknown as ChromaKeyer;
const shapeMask = { kind: 'shape' } as unknown as ClipMask;

const busy = () => media('c1', 0, 60, 0, { playbackRate: 2, mask: shapeMask, keyer });
const dsp: DspSupport = { nodes: true, worklet: true };

describe('a stored null blendMode is no blend', () => {
  it('a picture clip with blendMode null is previewed as rendered, not with the blend-mode sentence', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { blendMode: null } as never)])]);
    const r = classifyItem(d.tracks[0]!.items[0]!, d, { dsp });
    expect(r.parity).toBe('faithful');
    expect(r.reason).toMatch(/previewed as rendered/i);
  });
});

describe('every finding per clip', () => {
  it('a clip with speed + mask + keyer reports two findings (the keyer is faithful), worst = approximate', () => {
    const d = doc([track('v1', 'video', [busy()])]);
    const r = classifyItem(d.tracks[0]!.items[0]!, d, { dsp });
    expect(r.findings.map((f) => f.feature)).toEqual(['speed', 'shapeMask']);
    expect(r.parity).toBe('approximate');
    expect(r.reason).toBe(r.findings[0]!.reason);
  });

  it('the order is stable across calls', () => {
    const d = doc([track('v1', 'video', [busy()])]);
    const a = classifyParity(d, { dsp }).items[0]!.findings.map((f) => f.feature);
    const b = classifyParity(d, { dsp }).items[0]!.findings.map((f) => f.feature);
    expect(a).toEqual(b);
  });

  it('a single-finding clip is unchanged', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { keyer })])]);
    const item = classifyParity(d, { dsp }).items[0]!;
    // The chroma key runs in the live grade pass: faithful.
    expect(item.parity).toBe('faithful');
    expect(item.findings).toHaveLength(0);
  });

  it('a faithful clip has no findings', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60)])]);
    const item = classifyParity(d, { dsp }).items[0]!;
    expect(item.parity).toBe('faithful');
    expect(item.findings).toEqual([]);
  });

  it('a clip counts once in the summary', () => {
    const d = doc([track('v1', 'video', [busy()])]);
    const s = classifyParity(d, { dsp });
    expect(s.renderRequiredCount).toBe(0);
    expect(s.approximateCount).toBe(1);
  });

  it('the badge list has one line per finding, all jumping to the clip', () => {
    const d = doc([track('v1', 'video', [busy()])]);
    const lines = parityEntries(classifyParity(d, { dsp }).items);
    expect(lines).toHaveLength(2);
    expect(lines.every((l) => l.itemId === 'c1')).toBe(true);
  });
});

describe('optical flow retime marker', () => {
  it('adds an approximate retime finding next to the speed change', () => {
    const d = doc([track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 0.5, retimeQuality: 'opticalFlow' })])]);
    const item = classifyParity(d, { dsp }).items[0]!;
    expect(item.findings.map((f) => f.feature)).toEqual(['speed', 'retime']);
    expect(item.findings[1]!.reason).toBe(OPTICAL_FLOW_REASON);
    expect(item.parity).toBe('approximate');
  });

  it('frame quality or no speed change adds nothing', () => {
    const a = classifyParity(doc([track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 0.5, retimeQuality: 'frame' })])]), { dsp }).items[0]!;
    const b = classifyParity(doc([track('v1', 'video', [media('c1', 0, 60, 0, { retimeQuality: 'opticalFlow' })])]), { dsp }).items[0]!;
    expect(a.findings.map((f) => f.feature)).toEqual(['speed']);
    expect(b.findings).toEqual([]);
  });

  it('the new reason is in the Italian reason table', () => {
    expect(REASON_TABLES.parity.some((e) => e[0] === OPTICAL_FLOW_REASON)).toBe(true);
  });
});
