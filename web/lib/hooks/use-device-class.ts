'use client';

/**
 * The app's one device-class hook: 'phone' | 'tablet' | 'desktop' (600 / 1100 px, pointer-aware).
 * It is the editor's SSR-safe store (`lib/editing/use-device-class.ts`: 'desktop' on the server and the
 * hydrating paint, then the live class), re-exported so pages outside the editor import it from `lib/hooks`.
 */
import { useEffect } from 'react';
import { useIsPhoneClass, useIsTouchSurface, useDeviceClass } from '@/lib/editing/use-device-class';
import type { DeviceClass } from '@/lib/editing/device-class';

export { useDeviceClass, useIsPhoneClass, useIsTouchSurface };
export type { DeviceClass };

/** The attribute the app-wide touch rule in `app/global.css` is scoped by (`body[data-app-touch='true']`). */
export const APP_TOUCH_ATTRIBUTE = 'data-app-touch';

/**
 * Marks `<body>` while a finger is the likely pointer (phone, or a coarse-pointer tablet) so `global.css` can
 * raise targets to 44 px and text to 12 px. `enabled` is false on surfaces that size their own controls (the
 * editor). Body, not the shell root, because menus, dialogs and drawers are portalled out of the shell.
 */
export function useAppTouchAttribute(enabled: boolean): boolean {
  const touch = useIsTouchSurface();
  const on = enabled && touch;
  useEffect(() => {
    if (!on) return undefined;
    document.body.setAttribute(APP_TOUCH_ATTRIBUTE, 'true');
    return () => document.body.removeAttribute(APP_TOUCH_ATTRIBUTE);
  }, [on]);
  return on;
}
