/**
 * The playback and marks machinery of ONE source, shared by the Media pool's source viewer and the
 * editor's Source monitor so the two cannot drift: derivative rows (one fetch per clip), the file that
 * plays (the proxy when it exists, else the original), the playhead in the unit the marks use, and the
 * marks themselves. Marking issues no request; nothing here writes to any document.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { derivativeContentUrl, fetchMediaDerivatives, originalContentUrl } from '@/lib/editing/api/media';
import {
  NO_MARKS,
  clearMarks,
  framesFromSeconds,
  resolveMarkDomain,
  setIn,
  setOut,
  type MarkDomain,
  type Marks,
} from '@/lib/editing/media/in-out';
import { pickProxyRow, type MediaDerivativeRow, type MediaPoolItem, type MediaRef } from '@/lib/editing/media/types';
import { retryWhilePending } from '@/lib/outputs/output-check';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';

/** Which file the viewer plays. Chosen automatically (R05.5): the proxy when one exists, else the original. */
export type PlaybackSource = 'original' | 'proxy';

/** The proxy row to play (the largest when a source has several), or `null` - presence is the whole state machine. */
export function findProxyRow(rows: MediaDerivativeRow[] | null): MediaDerivativeRow | null {
  return pickProxyRow(rows);
}

interface DerivativesEntry {
  mediaRef: MediaRef;
  rows: MediaDerivativeRow[] | null;
  error: string | null;
}

/**
 * The frame-reporting surface of a media element, accessed structurally. These members are not
 * available in every environment, so each is guarded with `typeof === 'function'` at its use.
 */
interface FrameReporter {
  requestVideoFrameCallback?: (
    callback: (now: number, metadata: { presentedFrames?: number }) => void,
  ) => number;
  cancelVideoFrameCallback?: (handle: number) => void;
  getVideoPlaybackQuality?: () => { totalVideoFrames?: number };
}

const reporterOf = (element: HTMLVideoElement | null): FrameReporter | null =>
  element ? (element as unknown as FrameReporter) : null;

export function useSourcePlayback(
  projectId: string,
  item: MediaPoolItem | null,
  derivativesReloadKey: number,
  onMarksChange?: (marks: Marks) => void,
) {
  const t = useT('editor-media.viewer');
  const mediaRef = item ? item.mediaRef : null;

  // ───────────────────────────────────────────────────────────────────────────
  // Derivatives — ONE fetch per clip, plus one more per explicit reload request.
  // The entry carries the mediaRef it belongs to, so a new selection can never
  // render the previous clip's rows (and no reset effect is needed, which would
  // be a setState during an effect).
  // ───────────────────────────────────────────────────────────────────────────
  const [entry, setEntry] = useState<DerivativesEntry | null>(null);

  useEffect(() => {
    if (!mediaRef) return undefined;

    let cancelled = false;
    // A source still being checked answers "pending": keep the "getting ready" state and ask again on the back-off.
    retryWhilePending(() => fetchMediaDerivatives(projectId, mediaRef), { isCancelled: () => cancelled })
      .then((rows) => {
        if (!cancelled) setEntry({ mediaRef, rows, error: null });
      })
      .catch((error: unknown) => {
        if (!cancelled) setEntry({ mediaRef, rows: null, error: humanizeErrorT(t, error, t('requestFailed')) });
      });

    return () => {
      cancelled = true;
    };
  }, [projectId, mediaRef, derivativesReloadKey, t]);

  const current = entry && entry.mediaRef === mediaRef ? entry : null;
  const rows = current ? current.rows : null;
  const rowsError = current ? current.error : null;

  // ───────────────────────────────────────────────────────────────────────────
  // View state: the playback position, the marks, the subclip preview.
  // ───────────────────────────────────────────────────────────────────────────
  // The marks carry the clip they belong to, so another clip never shows these (no reset effect needed).
  const [marksEntry, setMarksEntry] = useState<{ mediaRef: MediaRef | null; marks: Marks }>({ mediaRef: null, marks: NO_MARKS });
  const marks = marksEntry.mediaRef === mediaRef ? marksEntry.marks : NO_MARKS;
  const setMarks = useCallback((next: Marks) => setMarksEntry({ mediaRef, marks: next }), [mediaRef]);
  const [playheadFrame, setPlayheadFrame] = useState<number | null>(null);
  const [decodedTotalFrames, setDecodedTotalFrames] = useState<number | null>(null);

  const videoRef = useRef<HTMLVideoElement | null>(null);
  // The element changes when a source that was still being prepared loads (CheckedVideo remounts it), and on every Source visit.
  const [videoEl, setVideoEl] = useState<HTMLVideoElement | null>(null);
  const attachVideo = useCallback((node: HTMLVideoElement | null) => {
    videoRef.current = node;
    setVideoEl(node);
  }, []);
  /** The last decoded frame index the element reported, when it can report one at all. */
  const decodedFrameRef = useRef<number | null>(null);

  const domain: MarkDomain = useMemo(
    () => resolveMarkDomain(item ? item.probe : null, decodedTotalFrames),
    [item, decodedTotalFrames],
  );

  const proxyRow = findProxyRow(rows);
  const proxyUrl = proxyRow ? derivativeContentUrl(projectId, proxyRow) : null;
  const originalUrl = item ? originalContentUrl(projectId, item.mediaRef) : null;

  // ONE viewer, no switch (R05.5): the best file that can play. A proxy is light and seeks well, so it wins
  // when it exists; until then (or for a source with none) the original plays. Both are Range-aware.
  const effectiveSource: PlaybackSource | null = proxyUrl ? 'proxy' : originalUrl ? 'original' : null;

  const videoUrl =
    effectiveSource === 'proxy' ? proxyUrl : effectiveSource === 'original' ? originalUrl : null;

  // ───────────────────────────────────────────────────────────────────────────
  // The playhead, in whichever unit this source's marks are in.
  // ───────────────────────────────────────────────────────────────────────────
  const readPlayheadFrame = useCallback((): number | null => {
    if (domain.kind === 'unavailable') return null;
    if (domain.kind === 'decoded') return decodedFrameRef.current;

    const element = videoRef.current;
    if (!element) return null;
    return framesFromSeconds(element.currentTime, domain.fps);
  }, [domain]);

  const refreshPlayhead = useCallback(() => {
    setPlayheadFrame(readPlayheadFrame());
  }, [readPlayheadFrame]);

  // Decoded frames are the ONLY honest bound and cursor when the probe has no rate, so they are
  // read from the element itself — never estimated from the duration.
  useEffect(() => {
    const element = reporterOf(videoEl);
    if (!element || typeof element.requestVideoFrameCallback !== 'function') return undefined;

    let cancelled = false;
    let handle = 0;

    const onFrame = (_now: number, metadata: { presentedFrames?: number }) => {
      if (cancelled) return;
      const presented = metadata ? metadata.presentedFrames : undefined;
      if (typeof presented === 'number' && Number.isFinite(presented)) {
        decodedFrameRef.current = presented;
        setPlayheadFrame(presented);
      }
      handle = element.requestVideoFrameCallback?.(onFrame) ?? 0;
    };

    handle = element.requestVideoFrameCallback(onFrame);
    return () => {
      cancelled = true;
      if (handle) element.cancelVideoFrameCallback?.(handle);
    };
  }, [videoUrl, videoEl]);

  const handleLoadedMetadata = useCallback(() => {
    const element = reporterOf(videoRef.current);
    const quality =
      element && typeof element.getVideoPlaybackQuality === 'function'
        ? element.getVideoPlaybackQuality()
        : null;
    const total = quality ? quality.totalVideoFrames : undefined;
    if (typeof total === 'number' && Number.isFinite(total) && total > 0) {
      setDecodedTotalFrames(total);
    }
    refreshPlayhead();
  }, [refreshPlayhead]);

  // ───────────────────────────────────────────────────────────────────────────
  // Marking. These read the element at click time, so a mark is always taken at
  // the position the user is looking at — and they issue nothing.
  // ───────────────────────────────────────────────────────────────────────────
  const applyMark = useCallback(
    (which: 'in' | 'out') => {
      const frame = readPlayheadFrame();
      if (frame === null) return;
      const next = which === 'in' ? setIn(marks, frame, domain) : setOut(marks, frame, domain);
      setMarks(next);
      onMarksChange?.(next);
    },
    [domain, readPlayheadFrame, marks, onMarksChange, setMarks],
  );

  const handleClearMarks = useCallback(() => {
    const next = clearMarks();
    setMarks(next);
    onMarksChange?.(next);
  }, [onMarksChange, setMarks]);


  return {
    rows,
    rowsError,
    marks,
    setMarks,
    playheadFrame,
    domain: domain as MarkDomain,
    videoRef,
    attachVideo,
    videoUrl,
    effectiveSource,
    originalUrl,
    refreshPlayhead,
    handleLoadedMetadata,
    readPlayheadFrame,
    applyMark,
    handleClearMarks,
  };
}
