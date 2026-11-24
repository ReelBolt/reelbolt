'use client';

import { useCallback, useEffect, useRef } from 'react';
import type React from 'react';

export const LONG_PRESS_MS = 450;
export const LONG_PRESS_SLOP_PX = 8;

/**
 * Press-and-hold on a file row or tile (touch and pen only; a mouse keeps its checkboxes). Holding still for
 * {@link LONG_PRESS_MS} calls `onLongPress(id)` with a haptic tick, and the click or context menu that the
 * release then produces is swallowed so it does not also open the preview. Moving more than
 * {@link LONG_PRESS_SLOP_PX} or scrolling (the browser cancels the pointer) abandons the press.
 */
export function useLongPressSelect(enabled: boolean, onLongPress: (id: string) => void) {
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const origin = useRef<{ x: number; y: number } | null>(null);
  const fired = useRef(false);
  const latest = useRef(onLongPress);
  useEffect(() => {
    latest.current = onLongPress;
  }, [onLongPress]);

  const cancel = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    origin.current = null;
  }, []);
  useEffect(() => cancel, [cancel]);

  return useCallback((id: string) => {
    if (!enabled) return {};
    return {
      onPointerDown: (e: React.PointerEvent) => {
        if (e.pointerType === 'mouse' || e.button > 0) return;
        cancel();
        fired.current = false;
        origin.current = { x: e.clientX, y: e.clientY };
        timer.current = setTimeout(() => {
          timer.current = null;
          fired.current = true;
          try { navigator.vibrate?.(15); } catch { /* no haptics */ }
          latest.current(id);
        }, LONG_PRESS_MS);
      },
      onPointerMove: (e: React.PointerEvent) => {
        const o = origin.current;
        if (o && Math.hypot(e.clientX - o.x, e.clientY - o.y) > LONG_PRESS_SLOP_PX) cancel();
      },
      onPointerUp: cancel,
      onPointerCancel: cancel,
      onContextMenu: (e: React.MouseEvent) => { if (timer.current || fired.current) e.preventDefault(); },
      onClickCapture: (e: React.MouseEvent) => {
        if (!fired.current) return;
        fired.current = false;
        e.preventDefault();
        e.stopPropagation();
      },
    };
  }, [enabled, cancel]);
}
