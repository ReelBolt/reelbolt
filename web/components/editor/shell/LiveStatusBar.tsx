'use client';

import { useSyncExternalStore } from 'react';
import type { PlaybackStats } from '@/lib/editing/preview';
import type { ValueStore } from '@/lib/editing/preview/throttled-stats';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { framesToTimecode } from '@/lib/editing/time';
import type { Rational } from '@/lib/editing/types';
import { PAINT_MAX_HZ } from './LivePlayhead';
import { StatusBar, type StatusBarProps } from './StatusBar';

/**
 * The status bar with its two fast-moving inputs — the exact playhead timecode and the playback counter —
 * read here instead of in the editor shell, so playing re-renders this leaf and not the whole workspace.
 */
export function LiveStatusBar({
  editorStore,
  statsStore,
  rate,
  ...props
}: Omit<StatusBarProps, 'stats' | 'timecode'> & { editorStore: EditorStore; statsStore: ValueStore<PlaybackStats>; rate: Rational }) {
  const playheadFrame = useEditorStore(editorStore, (s) => leafPlayheadFrame(s, 1 / PAINT_MAX_HZ));
  const stats = useSyncExternalStore(statsStore.subscribe, statsStore.get, statsStore.get);
  return <StatusBar {...props} stats={stats} timecode={framesToTimecode(playheadFrame, rate)} />;
}
