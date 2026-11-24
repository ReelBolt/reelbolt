'use client';

import { useSidePanelsView } from './useSidePanelsView';
import { useViewerZoneView } from './useViewerZoneView';
import { useTimelineZoneView } from './useTimelineZoneView';
import { usePagesView } from './usePagesView';
import { useChromeView } from './useChromeView';
import { useSimpleView } from './useSimpleView';
import { useEditorShellView } from './useEditorShellView';
import type { ShellStateCtx } from '../useShellState';

/**
 * The workspace's markup, built from the controller. View hooks that return elements (not components), called in a
 * fixed order. The last one picks the surface: the phone's, the Simple shell's, or the full editor's.
 */
export function useEditorView(input: ShellStateCtx) {
  const c1 = { ...input, ...useSidePanelsView(input) };
  const c2 = { ...c1, ...useViewerZoneView(c1) };
  const c3 = { ...c2, ...useTimelineZoneView(c2) };
  const c4 = { ...c3, ...usePagesView(c3) };
  const c5 = { ...c4, ...useChromeView(c4) };
  const c6 = { ...c5, ...useSimpleView(c5) };
  return useEditorShellView(c6);
}
