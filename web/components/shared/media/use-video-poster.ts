'use client';

import { useEffect, useRef, useState, type RefObject } from 'react';
import useSWR from 'swr';
import { fetcher } from '@/lib/api/client';
import { filmstripSheetUrl, mediaFilmstripKey } from '@/lib/editing/api/media';
import { filmstripCellBackground, resolveFilmstripThumb } from '@/lib/editing/media/filmstrip';
import type { MediaFilmstripResponse } from '@/lib/editing/media/types';

export interface VideoPoster {
  url: string;
  backgroundSize: string;
  backgroundPosition: string;
}

/** True once `ref` is near the viewport (always true where IntersectionObserver is missing, e.g. tests). */
export function useNearViewport(): [RefObject<HTMLDivElement | null>, boolean] {
  const ref = useRef<HTMLDivElement | null>(null);
  const [near, setNear] = useState(() => typeof IntersectionObserver === 'undefined');
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof IntersectionObserver === 'undefined') return undefined;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setNear(true);
          observer.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, near];
}

/**
 * The first-frame poster of a video, taken from its filmstrip derivative (a small sprite sheet), never
 * from the video file itself. `null` while loading, when the source has no pool row, or when no
 * filmstrip exists yet: the caller then shows a video icon.
 */
export function useVideoPoster(projectId: string, mediaRef: string | null, enabled: boolean): VideoPoster | null {
  const { data } = useSWR<MediaFilmstripResponse>(
    enabled && mediaRef ? mediaFilmstripKey(projectId, mediaRef) : null,
    fetcher,
    { shouldRetryOnError: false },
  );
  const cell = resolveFilmstripThumb(data?.filmstrip, 0, filmstripSheetUrl);
  if (!cell || !cell.sheetUrl) return null;
  return { url: cell.sheetUrl, ...filmstripCellBackground(cell) };
}
