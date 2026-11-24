import { useCallback, useMemo, useState, useEffect, useRef } from 'react';
import { orientedSourceSize } from '../phone/clip-sheet-actions';
import { selectItem } from '@/lib/editing/selection';
import { mediaRefOf, mediaRefsIn, ReadinessStore, stabilizedSourcesIn, prepareSources, pictureSourceRefs, planPlayback, type ProxyStatus } from '@/lib/editing/preview';
import type { MediaClip } from '@/lib/editing/types';
import { useMediaPool } from '@/lib/hooks/use-media';
import { requestMediaDerivatives } from '@/lib/editing/api/media';
import { StabilizeJobs } from '@/lib/editing/background/stabilize-job';
import { useRequestTimelineDerivatives, useMediaDerivativeProgress, poolStageStoresProbe } from '@/lib/hooks/use-media-derivatives';
import { type HdrKindLookup, hdrKindOfProbe } from '@/lib/editing/media/hdr';
import { useClipArt, useTimelineView } from '../timeline/useTimelineArt';
import { createWorkspaceProxyResolver, wantDocumentProfiles, wantDocumentStabilized, buildSessionHooks } from '../viewer/session-wiring';
import { useViewingLutSpec } from '../viewer/useViewingLutSpec';
import { useGradeLuts } from '../viewer/useGradeLuts';
import type { MediaProbe } from '@/lib/editing/media/types';
import { useT, useLocale } from '@/i18n';
import { autoPlanText } from '@/lib/editing/preview/labels-msg';
import type { PreviewDocumentCtx } from './usePreviewDocument';

/**
 * Where the preview's pictures come from: proxy lookup, readiness, stabilised copies, the playback plan for this
 * device, camera conversion, viewing and grade LUTs.
 */
export function usePreviewSources(ctx: PreviewDocumentCtx) {
  const { store, doc, previewDoc, getPreviewDoc, ui, deviceFacts, simpleChoice, sessionRef } = ctx;
  const tx = useT('editor.editorWorkspace');
  const tStab = useT('editor-background');
  // ── proxies: the media pool's derivative rows, read once per asset ─────────
  // The resolver keeps every answer across effect re-runs (PA-1); its version is the viewer's cue
  // to re-present the paused frame when a URL arrives (PA-6).
  const { data: pool, mutate: mutatePool } = useMediaPool(store.getState().projectId);
  const sourceSizeFor = useCallback(
    (clip: MediaClip) => {
      const ref = mediaRefOf(clip.assetRef);
      const probe = ref ? pool?.find((p) => p.mediaRef === ref)?.probe : null;
      const w = probe?.width ?? 0;
      const h = probe?.height ?? 0;
      return w > 0 && h > 0 ? orientedSourceSize({ width: w, height: h }, clip.sourceRotation) : null;
    },
    [pool],
  );
  const sourceWidthFor = useCallback((clip: MediaClip) => sourceSizeFor(clip)?.width ?? null, [sourceSizeFor]);
  // R10.7: a clip whose source is HDR (the ingest probe says hlg / pq) previews approximately.
  const hdrKindFor = useCallback<HdrKindLookup>(
    (clip) => {
      const ref = mediaRefOf(clip.assetRef);
      return ref ? hdrKindOfProbe(pool?.find((p) => p.mediaRef === ref)?.probe) : null;
    },
    [pool],
  );
  // Opening a timeline asks for the derivatives of every source it uses (once per source per open).
  useRequestTimelineDerivatives(store.getState().projectId, store.getState().timelineId, doc);
  const { art: clipArt, version: artVersion } = useClipArt(store.getState().projectId);
  const [timelineView, setTimelineView] = useTimelineView();
  const proxyResolver = useMemo(() => createWorkspaceProxyResolver(store.getState().projectId), [store]);
  const [proxyVersion, setProxyVersion] = useState(0);
  useEffect(() => proxyResolver.subscribe(() => setProxyVersion((v) => v + 1)), [proxyResolver]);
  useEffect(() => {
    proxyResolver.request(mediaRefsIn(previewDoc));
  }, [previewDoc, proxyResolver]);
  // Clips with a camera profile: ask once for the converted proxy of each (source, profile); the plain proxy plays meanwhile.
  useEffect(() => {
    wantDocumentProfiles(proxyResolver, previewDoc, (mediaRef) => hdrKindOfProbe(pool?.find((p) => p.mediaRef === mediaRef)?.probe) !== null);
  }, [previewDoc, proxyResolver, pool]);
  // Clips with stabilisation on: ask once for the stabilised proxy of each source; the unstabilised picture plays meanwhile.
  useEffect(() => {
    wantDocumentStabilized(proxyResolver, previewDoc);
  }, [previewDoc, proxyResolver]);
  // The pool changed (a source was added, derivatives were prepared): ask again for every source
  // that has no proxy yet, so a proxy built while the editor is open is picked up.
  const seenPool = useRef(pool);
  useEffect(() => {
    if (seenPool.current === pool) return;
    seenPool.current = pool;
    proxyResolver.request(mediaRefsIn(getPreviewDoc()), { refresh: true });
  }, [pool, proxyResolver, getPreviewDoc]);

  // ── R04.1 preview readiness: the pipeline's progress per source, kept OUT of React state so a progress
  // event never re-renders the shell (the viewer subscribes to the store itself).
  const readiness = useMemo(() => new ReadinessStore(), []);
  const stabilizeJobs = useMemo(() => new StabilizeJobs({ label: '', failedMessage: '', retry: () => undefined }), []);
  useEffect(() => {
    stabilizeJobs.setDeps({ label: tStab('stabilizeLabel'), failedMessage: tStab('stabilizeFailed'), retry: (ref) => proxyResolver.retryStabilized(ref) });
  }, [stabilizeJobs, tStab, proxyResolver]);
  useEffect(() => {
    stabilizeJobs.sync(stabilizedSourcesIn(previewDoc), (ref) => proxyResolver.stabilizedUrlFor(ref) !== null);
  }, [stabilizeJobs, previewDoc, proxyResolver, proxyVersion]);
  useEffect(() => () => stabilizeJobs.sync(new Map(), () => true), [stabilizeJobs]);
  const poolRefreshTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useMediaDerivativeProgress(store.getState().projectId, (event) => {
    // The stabilised proxy's own progress feeds its job mark, not the plain proxy's readiness.
    const stabilizeEvent = stabilizeJobs.apply(event, (ref) => proxyResolver.status(ref).state === 'proxy');
    if (event.stage !== 'Stabilize') readiness.apply({ mediaRef: event.mediaRef, stage: event.stage, percentComplete: event.percentComplete });
    // The proxy exists once its stage is behind us: look it up again so the viewer switches to it.
    if (event.mediaRef && event.stage !== 'Queued' && event.stage !== 'Probing' && event.stage !== 'Failed' && event.stage !== 'Stabilize') proxyResolver.request([event.mediaRef], { refresh: true });
    if (stabilizeEvent && event.mediaRef && (event.stage === 'Done' || event.stage === 'Skipped')) proxyResolver.request([event.mediaRef], { refresh: true });
    // The ingest probe lands after the upload row finished: read the pool again once it is behind us, or the Media pane keeps
    // saying "not analysed yet" (marks off, sound unknown) for a source that was just uploaded.
    if (poolStageStoresProbe(event.stage)) {
      if (poolRefreshTimer.current) clearTimeout(poolRefreshTimer.current);
      poolRefreshTimer.current = setTimeout(() => void mutatePool?.(), 600);
    }
  });
  useEffect(() => () => { if (poolRefreshTimer.current) clearTimeout(poolRefreshTimer.current); }, []);
  const retryPreview = useCallback(
    (refs: string[]) => {
      void prepareSources(
        readiness,
        {
          request: (mediaRef) => requestMediaDerivatives(store.getState().projectId, { mediaRef, requestedBy: 'preview-retry' }),
          refresh: (mediaRefs) => proxyResolver.request(mediaRefs, { refresh: true }),
        },
        refs,
      );
    },
    [readiness, proxyResolver, store],
  );
  const prepareAllPreview = useCallback(() => retryPreview(pictureSourceRefs(getPreviewDoc())), [retryPreview, getPreviewDoc]);

  // R04.2: a badge line about a clip: select it, bring it into view.
  const jumpToClip = useCallback(
    (itemId: string) => {
      const current = store.getState().doc;
      for (const track of current.tracks) {
        const item = track.items.find((i) => i.id === itemId);
        if (!item) continue;
        store.setUi({ selection: selectItem(current, itemId), playheadFrame: item.recordIn, scrollFrame: Math.max(0, item.recordIn - 15) });
        return;
      }
    },
    [store],
  );

  // R04.5: what the viewer plays with - the user's pick, or for Auto what this device can carry.
  const playbackPlan = useMemo(() => planPlayback(ui.playbackResolution, deviceFacts), [ui.playbackResolution, deviceFacts]);
  const locale = useLocale();
  const autoReasonText = useMemo(() => autoPlanText(playbackPlan, (m) => tx.msg(m), locale), [playbackPlan, tx, locale]);
  const forceFallback = simpleChoice ?? playbackPlan.simple;

  const sessionHooks = useMemo(() => buildSessionHooks(proxyResolver, hdrKindFor), [proxyResolver, hdrKindFor]);
  const proxyUrlFor = sessionHooks.proxyUrlFor;
  const inputStageFor = sessionHooks.inputStageFor;
  const probeFor = useCallback(
    (clip: MediaClip): MediaProbe | null => {
      const ref = mediaRefOf(clip.assetRef);
      return ref ? pool?.find((p) => p.mediaRef === ref)?.probe ?? null : null;
    },
    [pool],
  );
  const viewingLut = useViewingLutSpec(store.getState().projectId, store.getState().timelineId);
  const pictureUrlFor = sessionHooks.pictureUrlFor;
  // Grade-node LUTs (.cube): a file that finishes loading re-presents the frame.
  const gradeLuts = useGradeLuts(store.getState().projectId, useCallback(() => sessionRef.current?.invalidate(), [sessionRef]));
  const { gradeLutFor, gradeLutStatus } = gradeLuts;
  const proxyStatusFor = useCallback((clip: MediaClip): ProxyStatus => proxyResolver.status(mediaRefOf(clip.assetRef)), [proxyResolver]);

  return { pool, sourceSizeFor, sourceWidthFor, hdrKindFor, clipArt, artVersion, timelineView, setTimelineView, proxyResolver, proxyVersion, readiness, stabilizeJobs, retryPreview, prepareAllPreview, jumpToClip, playbackPlan, autoReasonText, forceFallback, proxyUrlFor, inputStageFor, probeFor, viewingLut, pictureUrlFor, gradeLutFor, gradeLutStatus, proxyStatusFor };
}

export type PreviewSourcesCtx = PreviewDocumentCtx & ReturnType<typeof usePreviewSources>;
