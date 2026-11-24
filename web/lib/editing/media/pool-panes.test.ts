import { describe, expect, it } from 'vitest';
import {
  DEFAULT_POOL_PANES,
  POOL_PANE_LIMITS,
  clampPaneWidth,
  parsePoolPanes,
  poolColumns,
  widthAfterKey,
} from './pool-panes';

describe('R05.8 pool panes', () => {
  it('clamps widths to the limits and falls back for junk', () => {
    expect(clampPaneWidth('bins', 10)).toBe(POOL_PANE_LIMITS.bins.min);
    expect(clampPaneWidth('bins', 9999)).toBe(POOL_PANE_LIMITS.bins.max);
    expect(clampPaneWidth('detail', Number.NaN)).toBe(POOL_PANE_LIMITS.detail.default);
  });

  it('round-trips a stored layout and survives corrupt storage', () => {
    const stored = JSON.stringify({ binsWidth: 300, detailWidth: 400, binsCollapsed: true, detailCollapsed: false });
    expect(parsePoolPanes(stored)).toEqual({ binsWidth: 300, detailWidth: 400, binsCollapsed: true, detailCollapsed: false });
    expect(parsePoolPanes('{not json')).toEqual(DEFAULT_POOL_PANES);
    expect(parsePoolPanes(null)).toEqual(DEFAULT_POOL_PANES);
    expect(parsePoolPanes(JSON.stringify({ binsWidth: 1 })).binsWidth).toBe(POOL_PANE_LIMITS.bins.min);
  });

  it('keys move each separator the way its pane grows', () => {
    expect(widthAfterKey('bins', 260, 'ArrowRight', false)).toBe(276);
    expect(widthAfterKey('bins', 260, 'ArrowLeft', true)).toBe(POOL_PANE_LIMITS.bins.min);
    expect(widthAfterKey('detail', 320, 'ArrowLeft', false)).toBe(336);
    expect(widthAfterKey('detail', 320, 'ArrowRight', false)).toBe(304);
    expect(widthAfterKey('detail', 320, 'End', false)).toBe(POOL_PANE_LIMITS.detail.max);
    expect(widthAfterKey('bins', 260, 'a', false)).toBeNull();
  });

  it('drops a collapsed pane from the grid columns', () => {
    expect(poolColumns(DEFAULT_POOL_PANES).three).toBe('260px minmax(0, 1fr) 320px');
    expect(poolColumns({ ...DEFAULT_POOL_PANES, binsCollapsed: true }).three).toBe('minmax(0, 1fr) 320px');
    expect(poolColumns({ ...DEFAULT_POOL_PANES, detailCollapsed: true }).two).toBe('260px minmax(0, 1fr)');
  });
});
