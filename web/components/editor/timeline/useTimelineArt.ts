'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { ClipArtStore } from './clipArtStore';
import { DEFAULT_TIMELINE_VIEW, TIMELINE_VIEW_STORAGE_KEY, parseTimelineView, type TimelineView } from './trackLayout';

/** One clip-art cache per project; `version` changes whenever a thumbnail or waveform lands, so the canvas repaints. */
export function useClipArt(projectId: string | null | undefined): { art: ClipArtStore | undefined; version: number } {
  const art = useMemo(() => (projectId ? new ClipArtStore(projectId) : undefined), [projectId]);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    if (!art) return;
    return art.subscribe(() => setVersion(art.getVersion()));
  }, [art]);
  return { art, version };
}

/** The labelled view-only toggles (waveforms / thumbnails / names), remembered per browser, never in the document. */
export function useTimelineView(): [TimelineView, (patch: Partial<TimelineView>) => void] {
  const [view, setView] = useState<TimelineView>(() => {
    if (typeof window === 'undefined') return DEFAULT_TIMELINE_VIEW;
    try {
      return parseTimelineView(window.localStorage.getItem(TIMELINE_VIEW_STORAGE_KEY));
    } catch {
      return DEFAULT_TIMELINE_VIEW; // storage blocked
    }
  });
  const update = useCallback((patch: Partial<TimelineView>) => {
    setView((prev) => {
      const next = { ...prev, ...patch };
      try {
        window.localStorage.setItem(TIMELINE_VIEW_STORAGE_KEY, JSON.stringify(next));
      } catch {
        /* storage blocked: the toggle still works for this session */
      }
      return next;
    });
  }, []);
  return [view, update];
}
