import { describe, expect, it } from 'vitest';
import { foldOf, nextFold, type FoldLevel, type NeededWidths } from '../useToolbarFold';

/** Runs the fold to rest over a bar whose content needs `widths[level]`. */
function settle(widths: readonly number[], available: number, start: { level: FoldLevel; known: NeededWidths } = { level: 0, known: [] }) {
  let state = start;
  const seen: FoldLevel[] = [state.level];
  for (let i = 0; i < 8; i++) {
    state = nextFold(state.level, widths[state.level], available, state.known);
    seen.push(state.level);
  }
  return { ...state, seen };
}

describe('toolbar fold', () => {
  const WIDTHS = [1656, 1450, 1347, 1227, 1000];

  it('folds the trim control first, then the steppers', () => {
    expect(foldOf(0)).toEqual({ trim: false, steppers: false, zoomSlider: false, labels: false });
    expect(foldOf(1)).toEqual({ trim: true, steppers: false, zoomSlider: false, labels: false });
    expect(foldOf(2)).toEqual({ trim: true, steppers: true, zoomSlider: false, labels: false });
    expect(foldOf(3)).toEqual({ trim: true, steppers: true, zoomSlider: true, labels: false });
    expect(foldOf(4)).toEqual({ trim: true, steppers: true, zoomSlider: true, labels: true });
    expect(settle(WIDTHS, 1700).level).toBe(0);
    expect(settle(WIDTHS, 1500).level).toBe(1);
    expect(settle(WIDTHS, 1280).seen.slice(0, 4)).toEqual([0, 1, 2, 3]);
    // With the Events menu and the text buttons the bar needs 1474 px with everything but the words folded.
    const WIDE = [1903, 1697, 1594, 1474, 1230];
    expect(settle(WIDE, 1280).seen.slice(0, 5)).toEqual([0, 1, 2, 3, 4]);
    expect(settle(WIDE, 1280).level, 'the words on the text buttons fold last, so 1280 fits').toBe(4);
    expect(settle(WIDE, 1440).level, '1440 needs the words folded too').toBe(4);
  });

  it('stays at the last level when even that overflows (the bar scrolls, with a cue)', () => {
    expect(settle(WIDTHS, 900).level).toBe(4);
    expect(settle(WIDTHS, 900).known[4]).toBe(1000);
  });

  it('unfolds when room returns, one level at a time, and rests', () => {
    const folded = settle(WIDTHS, 1280);
    const wider = settle(WIDTHS, 1800, folded);
    expect(wider.seen.slice(0, 4)).toEqual([3, 2, 1, 0]);
    expect(new Set(wider.seen.slice(-3))).toEqual(new Set([0]));
    expect(new Set(settle(WIDTHS, 1450).seen.slice(-3)), 'an exact fit does not oscillate').toEqual(new Set([1]));
  });

  it('a button appearing shifts what the wider levels are known to need', () => {
    const folded = settle(WIDTHS, 1500);
    expect(folded.level).toBe(1);
    const grown = nextFold(1, 1550, 1500, folded.known);
    expect(grown.level).toBe(2);
    expect(grown.known[0]).toBe(1756);
  });
});
