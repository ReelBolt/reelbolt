'use client';

/**
 * The drag handle between two media pool panes (R05.8). It is a real separator: focusable, with
 * its size announced, and operable by keyboard (arrows resize, Shift for bigger steps, Home and End
 * jump to the limits, Enter hides the pane). Dragging works with mouse, pen and touch.
 */

import React, { useRef } from 'react';
import { POOL_PANE_LIMITS, widthAfterKey, type PoolPaneId } from '@/lib/editing/media/pool-panes';
import classes from './media-pool.module.css';

export interface PaneResizerProps {
  pane: PoolPaneId;
  /** Current pane width in px. */
  width: number;
  label: string;
  onResize: (width: number) => void;
  onCollapse: () => void;
}

export function PaneResizer({ pane, width, label, onResize, onCollapse }: PaneResizerProps) {
  const drag = useRef<{ startX: number; startWidth: number } | null>(null);
  const limits = POOL_PANE_LIMITS[pane];
  // The bins pane grows when its right edge moves right; the details pane when its left edge moves left.
  const direction = pane === 'bins' ? 1 : -1;

  return (
    <div
      role="separator"
      aria-orientation="vertical"
      aria-label={label}
      aria-valuemin={limits.min}
      aria-valuemax={limits.max}
      aria-valuenow={width}
      tabIndex={0}
      className={`${classes.resizer} ${pane === 'bins' ? classes.resizerBins : classes.resizerDetail}`}
      data-testid={`pool-resizer-${pane}`}
      onPointerDown={(event) => {
        event.currentTarget.setPointerCapture(event.pointerId);
        drag.current = { startX: event.clientX, startWidth: width };
      }}
      onPointerMove={(event) => {
        if (!drag.current) return;
        onResize(drag.current.startWidth + (event.clientX - drag.current.startX) * direction);
      }}
      onPointerUp={() => {
        drag.current = null;
      }}
      onPointerCancel={() => {
        drag.current = null;
      }}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          onCollapse();
          return;
        }
        const next = widthAfterKey(pane, width, event.key, event.shiftKey);
        if (next === null) return;
        event.preventDefault();
        onResize(next);
      }}
      onDoubleClick={onCollapse}
    />
  );
}
