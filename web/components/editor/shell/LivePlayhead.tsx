'use client';

import type { ComponentType, ReactNode } from 'react';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';

/** How often (seconds) a panel that follows the playhead refreshes while playing. */
export const PANEL_PLAYHEAD_QUANTUM_SEC = 0.25;
/** The refresh ceiling for leaves that only paint the playhead. */
export const PAINT_MAX_HZ = 30;

/**
 * Re-renders ONLY its own subtree on every playhead tick. The editor shell reads a UI state whose playhead
 * is frozen while playing (`lib/editing/store/coarse-ui.ts`), so what paints per frame — the timeline canvas,
 * the transport and status timecodes, the program viewer — is rendered through this leaf (`PAINT_MAX_HZ` for the
 * ones that only paint).
 */
export function LivePlayhead({ store, maxHz, children }: { store: EditorStore; maxHz?: number; children: (playheadFrame: number) => ReactNode }) {
  // `maxHz`: a leaf that only paints a number or a line (timeline playhead, timecodes) updates at most that often
  // while playing; a 60 fps timeline then costs these leaves 30 renders a second, not 60. Exact when paused.
  const playheadFrame = useEditorStore(store, (s) => (maxHz ? leafPlayheadFrame(s, 1 / maxHz) : s.ui.playheadFrame));
  return <>{children(playheadFrame)}</>;
}

/**
 * A panel that shows the playhead but not frame by frame: `withLivePlayhead(Panel)` takes a `store` instead of
 * `playheadFrame` and refreshes `PANEL_PLAYHEAD_QUANTUM_SEC` apart while playing (exact when paused), re-rendering
 * the panel alone, never the shell.
 */
export function withLivePlayhead<P extends { playheadFrame?: number }>(Component: ComponentType<P>) {
  function WithLivePlayhead(props: Omit<P, 'playheadFrame'> & { store: EditorStore }) {
    const { store, ...rest } = props;
    const playheadFrame = useEditorStore(store, (s) => leafPlayheadFrame(s, PANEL_PLAYHEAD_QUANTUM_SEC));
    return <Component {...(rest as unknown as P)} playheadFrame={playheadFrame} />;
  }
  WithLivePlayhead.displayName = `WithLivePlayhead(${Component.displayName ?? Component.name ?? 'Component'})`;
  return WithLivePlayhead;
}
