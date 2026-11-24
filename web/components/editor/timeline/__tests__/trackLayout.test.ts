import { describe, expect, it } from 'vitest';
import {
  DEFAULT_TIMELINE_VIEW, LANE_BACKGROUNDS, TRACK_HEIGHT_PRESETS, TRACK_SWATCHES, addIndex, cleanTrackName, dropIndexAtY, moveTarget, nearestHeightPreset,
  nextTrackName, parseTimelineView, removeConfirmation,
} from '../trackLayout';

function lum(hex: string): number {
  const ch = (c: number) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  };
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b);
}
const contrast = (a: string, b: string): number => {
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

describe('R03.5 track palette', () => {
  it('every swatch holds 3:1 against every lane background (non-text indicator)', () => {
    for (const sw of TRACK_SWATCHES) {
      expect(sw.hex).toMatch(/^#[0-9a-f]{6}$/);
      for (const bg of LANE_BACKGROUNDS) expect(contrast(sw.hex, bg), `${sw.name} on ${bg}`).toBeGreaterThanOrEqual(3);
    }
  });
  it('swatches are unique and named', () => {
    expect(new Set(TRACK_SWATCHES.map((s) => s.hex)).size).toBe(TRACK_SWATCHES.length);
    for (const sw of TRACK_SWATCHES) expect(sw.name.length).toBeGreaterThan(2);
  });
});

describe('R03.5 layout math', () => {
  it('height presets are increasing and the nearest preset is found', () => {
    expect(TRACK_HEIGHT_PRESETS.small).toBeLessThan(TRACK_HEIGHT_PRESETS.medium);
    expect(TRACK_HEIGHT_PRESETS.medium).toBeLessThan(TRACK_HEIGHT_PRESETS.large);
    expect(nearestHeightPreset(40)).toBe('small');
    expect(nearestHeightPreset(60)).toBe('medium');
    expect(nearestHeightPreset(120)).toBe('large');
  });
  it('moveTarget stops at the ends', () => {
    expect(moveTarget(0, 3, 'up')).toBeNull();
    expect(moveTarget(2, 3, 'down')).toBeNull();
    expect(moveTarget(1, 3, 'up')).toBe(0);
    expect(moveTarget(1, 3, 'down')).toBe(2);
  });
  it('dropIndexAtY maps a y to the row under it and clamps', () => {
    expect(dropIndexAtY([40, 64, 96], 10)).toBe(0);
    expect(dropIndexAtY([40, 64, 96], 40)).toBe(1);
    expect(dropIndexAtY([40, 64, 96], 103)).toBe(1);
    expect(dropIndexAtY([40, 64, 96], 104)).toBe(2);
    expect(dropIndexAtY([40, 64, 96], -20)).toBe(0);
    expect(dropIndexAtY([40, 64, 96], 9999)).toBe(2);
    expect(dropIndexAtY([], 5)).toBe(0);
  });
  it('addIndex puts a track directly above or below', () => {
    expect(addIndex(2, 'above')).toBe(2);
    expect(addIndex(2, 'below')).toBe(3);
  });
  it('nextTrackName skips names in use', () => {
    const t = (kind: 'video' | 'audio', name: string) => ({ kind, name });
    expect(nextTrackName([t('video', 'V1'), t('audio', 'A1')], 'video')).toBe('V2');
    expect(nextTrackName([t('video', 'V2')], 'video')).toBe('V3');
    expect(nextTrackName([], 'audio')).toBe('A1');
  });
  it('cleanTrackName trims, collapses spaces, refuses empty and caps length', () => {
    expect(cleanTrackName('  Main   cam ')).toBe('Main cam');
    expect(cleanTrackName('   ')).toBeNull();
    expect(cleanTrackName('x'.repeat(100))).toHaveLength(60);
  });
  it('removeConfirmation is null for an empty track and plain for a populated one', () => {
    expect(removeConfirmation({ name: 'V1', items: [] })).toBeNull();
    expect(removeConfirmation({ name: 'V1', items: [{} as never] })).toBe('V1 has 1 clip. Removing the track removes it too. You can undo this.');
    expect(removeConfirmation({ name: 'V1', items: [{} as never, {} as never] })).toContain('2 clips');
  });
  it('view toggles parse defensively', () => {
    expect(parseTimelineView(null)).toEqual(DEFAULT_TIMELINE_VIEW);
    expect(parseTimelineView('not json')).toEqual(DEFAULT_TIMELINE_VIEW);
    expect(parseTimelineView('{"waveforms":false,"names":"x"}')).toEqual({ waveforms: false, thumbnails: true, names: true });
  });
});
