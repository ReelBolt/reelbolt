/**
 * Media pool pane geometry (R05.8): how wide the bins and details panes are and whether each is
 * collapsed.
 *
 * T-003: the Media page now renders its bins, pool and viewer+metadata in the shared frame's zones,
 * and those widths live in the one layout record (`lib/editing/layout/editor-layout.ts`). The legacy
 * per-device key below is read ONCE by `migratePoolPanes` and deleted; nothing here reads or writes
 * it any more. The helpers remain for the pool's in-memory default widths (the hub and the Edit tab).
 */

export const POOL_PANES_STORAGE_KEY = 'reelforge.editor.poolPanes.v1';

export interface PoolPaneState {
  binsWidth: number;
  detailWidth: number;
  binsCollapsed: boolean;
  detailCollapsed: boolean;
}

export type PoolPaneId = 'bins' | 'detail';

export const POOL_PANE_LIMITS: Record<PoolPaneId, { min: number; max: number; default: number }> = {
  bins: { min: 200, max: 480, default: 260 },
  detail: { min: 260, max: 560, default: 320 },
};

/** Pixels moved by one arrow key; Shift moves five times as far. */
export const POOL_PANE_KEY_STEP = 16;

export const DEFAULT_POOL_PANES: PoolPaneState = {
  binsWidth: POOL_PANE_LIMITS.bins.default,
  detailWidth: POOL_PANE_LIMITS.detail.default,
  binsCollapsed: false,
  detailCollapsed: false,
};

export function clampPaneWidth(pane: PoolPaneId, width: number): number {
  const { min, max, default: fallback } = POOL_PANE_LIMITS[pane];
  if (!Number.isFinite(width)) return fallback;
  return Math.min(max, Math.max(min, Math.round(width)));
}

export function parsePoolPanes(raw: string | null): PoolPaneState {
  if (!raw) return DEFAULT_POOL_PANES;
  try {
    const parsed = JSON.parse(raw) as Partial<PoolPaneState> | null;
    if (!parsed || typeof parsed !== 'object') return DEFAULT_POOL_PANES;
    return {
      binsWidth: typeof parsed.binsWidth === 'number' ? clampPaneWidth('bins', parsed.binsWidth) : DEFAULT_POOL_PANES.binsWidth,
      detailWidth: typeof parsed.detailWidth === 'number' ? clampPaneWidth('detail', parsed.detailWidth) : DEFAULT_POOL_PANES.detailWidth,
      binsCollapsed: parsed.binsCollapsed === true,
      detailCollapsed: parsed.detailCollapsed === true,
    };
  } catch {
    return DEFAULT_POOL_PANES;
  }
}

/**
 * The next width for a key press on a pane's separator, or `null` for a key it does not handle.
 * `side` says which way the pane grows when the separator moves right: the bins pane sits left of
 * the separator (right grows it), the details pane sits right of it (left grows it).
 */
export function widthAfterKey(pane: PoolPaneId, width: number, key: string, shift: boolean): number | null {
  const step = POOL_PANE_KEY_STEP * (shift ? 5 : 1);
  const grow = pane === 'bins' ? 'ArrowRight' : 'ArrowLeft';
  const shrink = pane === 'bins' ? 'ArrowLeft' : 'ArrowRight';
  if (key === grow) return clampPaneWidth(pane, width + step);
  if (key === shrink) return clampPaneWidth(pane, width - step);
  if (key === 'Home') return POOL_PANE_LIMITS[pane].min;
  if (key === 'End') return POOL_PANE_LIMITS[pane].max;
  return null;
}

/** The two grid-template-columns strings the stylesheet picks between by container width. */
export function poolColumns(state: PoolPaneState): { two: string; three: string } {
  const bins = state.binsCollapsed ? null : `${state.binsWidth}px`;
  const detail = state.detailCollapsed ? null : `${state.detailWidth}px`;
  return {
    two: [bins, 'minmax(0, 1fr)'].filter(Boolean).join(' '),
    three: [bins, 'minmax(0, 1fr)', detail].filter(Boolean).join(' '),
  };
}
