import { describe, expect, it } from 'vitest';
import { tabletTwoPane } from '../PanelLayout';
import { LAYOUT_MINIMUMS } from '@/lib/editing/layout/editor-layout';

const base = { dock: true, narrow: true, touch: true, wideEnough: true, hasRight: true };
describe('tabletTwoPane', () => {
  it('applies on a touch tablet from 768 to 1100 px', () => expect(tabletTwoPane(base)).toBe(true));
  it('never on a desktop (fine pointer) window', () => expect(tabletTwoPane({ ...base, touch: false })).toBe(false));
  it('not under 768 px, above 1100 px, without a right panel or when not asked for', () => {
    expect(tabletTwoPane({ ...base, wideEnough: false })).toBe(false);
    expect(tabletTwoPane({ ...base, narrow: false })).toBe(false);
    expect(tabletTwoPane({ ...base, hasRight: false })).toBe(false);
    expect(tabletTwoPane({ ...base, dock: false })).toBe(false);
  });
});

import { twoPaneWidths } from '../PanelLayout';
describe('twoPaneWidths', () => {
  const stored = { left: 300, right: 320 };
  it('uses the compact widths under 1000 px', () => {
    expect(twoPaneWidths(820, stored)).toEqual({ left: 240, right: 220 });
    expect(twoPaneWidths(999, stored)).toEqual({ left: 240, right: 220 });
  });
  it('uses the stored sizes from 1000 px, Inspector capped at 300', () => {
    expect(twoPaneWidths(1000, stored)).toEqual({ left: 300, right: 300 });
    expect(twoPaneWidths(1100, { left: 340, right: 250 })).toEqual({ left: 340, right: 250 });
  });
  it('leaves a viewer of about 303 px at 820', () => {
    const w = twoPaneWidths(820, stored);
    expect(820 - 56 - w.left - w.right - 1).toBeGreaterThanOrEqual(300);
  });
});

describe('the geometry table agrees with the touch two-pane minimums', () => {
  it('uses the 240 px side minimum under 1000 px', () => {
    expect(twoPaneWidths(820, { left: 280, right: 420 }).left).toBe(LAYOUT_MINIMUMS.side);
    expect(LAYOUT_MINIMUMS.side).toBe(240);
  });
});
