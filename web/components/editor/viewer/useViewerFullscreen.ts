'use client';

import { useCallback, useMemo, useState, useSyncExternalStore } from 'react';

const subscribeFullscreen = (onChange: () => void) => {
  document.addEventListener('fullscreenchange', onChange);
  return () => document.removeEventListener('fullscreenchange', onChange);
};
const subscribeNever = () => () => undefined;
const fullscreenElement = (): Element | null => document.fullscreenElement;

export interface ViewerFullscreen {
  /** Put this on the element that should fill the screen (the picture and its transport). */
  attach: (element: HTMLDivElement | null) => void;
  /** The element is on the whole screen right now. */
  active: boolean;
  /** This browser can put an element on the whole screen (an iPhone's Safari cannot). */
  supported: boolean;
  toggle: () => void;
  /** Leaves full screen when it is on; safe to call any time (before a dialog that lives outside the viewer opens). */
  exit: () => void;
  /**
   * A Mantine theme fragment for the viewer's subtree: while full screen, tooltips and menus mount INSIDE the element,
   * because the browser shows nothing outside it. Empty otherwise.
   */
  portalTheme: { components?: { Portal: { defaultProps: { target: HTMLElement } } } };
}

/**
 * The viewer on the whole screen (the Fullscreen API on one element). View state only: nothing is written anywhere.
 * Escape leaves it (the browser's own rule), as does the same button or chord.
 */
export function useViewerFullscreen(): ViewerFullscreen {
  const [element, setElement] = useState<HTMLDivElement | null>(null);
  const current = useSyncExternalStore(subscribeFullscreen, fullscreenElement, () => null);
  const active = element !== null && current === element;
  const supported = useSyncExternalStore(
    subscribeNever,
    () => document.fullscreenEnabled === true,
    () => false,
  );
  const exit = useCallback(() => {
    if (!element || typeof document === 'undefined' || document.fullscreenElement !== element) return;
    void document.exitFullscreen?.().catch(() => undefined);
  }, [element]);
  const toggle = useCallback(() => {
    if (!element || typeof document === 'undefined' || document.fullscreenEnabled !== true) return;
    if (document.fullscreenElement === element) void document.exitFullscreen().catch(() => undefined);
    else void element.requestFullscreen({ navigationUI: 'hide' }).catch(() => undefined);
  }, [element]);
  const portalTheme = useMemo(() => (active && element ? { components: { Portal: { defaultProps: { target: element } } } } : {}), [active, element]);
  return { attach: setElement, active, supported, toggle, exit, portalTheme };
}
