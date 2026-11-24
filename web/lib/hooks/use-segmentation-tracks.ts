'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import useSWR from 'swr';
import { fetcher } from '../api/client';
import { MASK_PREVIEW_MAX_TRIES, MASK_PREVIEW_POLL_MS, maskPreviewUrl, type SegmentationTrack, type SegmentationTracksLookup } from '../editing/preview/segmentation-tracks';

type TracksById = Record<string, SegmentationTrack[]>;

/**
 * The tracked boxes of the analyses the document's segmentation masks name, for the live preview.
 * Cached by SWR per (project, set of analyses); no request at all when the document has none. An
 * analysis the API refuses (the one 404 for foreign/missing) is simply absent from the lookup, and
 * the parity badge then says the preview has no box for it.
 */
export function useSegmentationTracks(projectId: string, analysisIds: readonly string[]): { lookup: SegmentationTracksLookup; version: number } {
  const key = projectId && analysisIds.length > 0 ? (['segmentation-tracks', projectId, analysisIds.join(',')] as const) : null;
  const { data, mutate } = useSWR<TracksById>(
    key,
    async ([, project, ids]: readonly [string, string, string]) => {
      const out: TracksById = {};
      await Promise.all(
        ids.split(',').map(async (id) => {
          try {
            out[id] = await fetcher<SegmentationTrack[]>(`/api/v1/projects/${project}/step-results/${id}/segmentation-tracks`);
          } catch {
            // refused or unreachable: no preview for this analysis
          }
        }),
      );
      return out;
    },
    { revalidateOnFocus: false },
  );
  // A pending mask preview is asked for again until it is ready (the first request starts its production); after
  // MASK_PREVIEW_MAX_TRIES the track stays on boxes. Ready or none tracks are never requested here.
  const tries = useRef(new Map<string, number>());
  // Bumped after each round that did not finish the previews: a 202 changes no data, so the effect would never run again.
  const [round, setRound] = useState(0);
  useEffect(() => {
    if (!data || !projectId) return;
    const pending: string[] = [];
    for (const [analysis, tracks] of Object.entries(data)) for (const t of tracks) if (t.maskPreview?.state === 'pending' && (tries.current.get(`${analysis}/${t.trackId}`) ?? 0) < MASK_PREVIEW_MAX_TRIES) pending.push(`${analysis}/${t.trackId}`);
    if (pending.length === 0) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      void Promise.all(
        pending.map(async (key) => {
          tries.current.set(key, (tries.current.get(key) ?? 0) + 1);
          const [analysis, trackId] = key.split('/');
          try {
            const res = await fetch(maskPreviewUrl(projectId, analysis, trackId), { credentials: 'include', headers: { Range: 'bytes=0-0' } });
            // 404 = the preview can never exist (none): stop asking; the track stays on boxes.
            if (res.status === 404) tries.current.set(key, MASK_PREVIEW_MAX_TRIES);
            return res.status === 200 || res.status === 206;
          } catch {
            return false;
          }
        }),
      ).then((ready) => {
        if (cancelled) return;
        if (ready.some(Boolean)) void mutate();
        else setRound((r) => r + 1);
      });
    }, MASK_PREVIEW_POLL_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [data, projectId, mutate, round]);
  const lookup = useCallback<SegmentationTracksLookup>((id) => data?.[id], [data]);
  return { lookup, version: data ? Object.keys(data).length + 1 : 0 };
}
