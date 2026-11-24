/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { fromFractions, isFractions, toFractions } from '../layout-fractions';
import { describePreset, uniqueCopyName } from '../preset-manage';
import { keymapFromJson, keymapToJson } from '../../../components/editor/prefs/KeymapEditor';

describe('layout fractions', () => {
  it('round-trips through the same window and scales to another', () => {
    const f = toFractions({ left: 300, right: 300, timeline: 320 }, { width: 1500, height: 800 });
    expect(f).toEqual({ left: 0.2, right: 0.2, timeline: 0.4 });
    expect(fromFractions(f, { width: 1500, height: 800 })).toEqual({ left: 300, right: 300, timeline: 320 });
    expect(fromFractions(f, { width: 1000, height: 600 })).toEqual({ left: 200, right: 200, timeline: 240 });
  });
  it('never squeezes the viewer or a panel to nothing', () => {
    const s = fromFractions({ left: 0.9, right: 0.9, timeline: 0.99 }, { width: 800, height: 600 });
    expect(s.left + s.right).toBeLessThanOrEqual(800 - 400 + 160);
    expect(s.timeline).toBeLessThanOrEqual(440);
    expect(isFractions({ left: 0.2, right: 0.2, timeline: 0.4 })).toBe(true);
    expect(isFractions({ left: 300, right: 1, timeline: 1 })).toBe(false);
  });
});

describe('preset management', () => {
  it('names copies without clashing', () => {
    expect(uniqueCopyName('Warm', ['Warm'])).toBe('Warm copy');
    expect(uniqueCopyName('Warm copy', ['Warm', 'Warm copy'])).toBe('Warm copy 2');
  });
  it('previews in plain words', () => {
    expect(describePreset('mastering', { targetLufs: -14, truePeakDbtp: -1 })).toBe('Sets loudness -14 LUFS, peaks no higher than -1 dB.');
    expect(describePreset('effect', { effects: [{ type: 'blur', amount: 4 }] })).toBe('Adds blur 4.');
  });
});

describe('keymap file', () => {
  it('round-trips and rejects other files', () => {
    expect(keymapFromJson(keymapToJson({ undo: 'Mod+KeyU' }))).toEqual({ undo: 'Mod+KeyU' });
    expect(keymapFromJson('{"a":1}')).toBeNull();
    expect(keymapFromJson('nope')).toBeNull();
    expect(keymapFromJson(keymapToJson({ notAnAction: 'KeyX' }))).toEqual({});
  });
});
