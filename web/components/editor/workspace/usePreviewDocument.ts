import { useMemo, useRef, useCallback, useEffect, useState } from 'react';
import type { EditTimeline, Track } from '@/lib/editing/types';
import { draftFor, type TransformDraftState, frameDraftFor, windowTargetFor, type FrameDraftState, applyDraft, applyFrameDraft } from '@/lib/editing/viewer-transform';
import { flattenTimeline } from '@/lib/editing/structure/flatten';
import type { ProposalGhost } from '@/lib/editing/proposals/apply-proposal';
import { useEditTimelineRenders, latestRenderForRevision } from '@/lib/hooks/use-edit-timeline-renders';
import type { WorkspaceCoreCtx } from './useWorkspaceCore';

/**
 * The documents the viewer and the timeline show: the flattened preview document (compound and multicam clips
 * resolved, live drafts applied), the timeline's own view of it, and the render queue of this timeline.
 */
export function usePreviewDocument(ctx: WorkspaceCoreCtx) {
  const { store, shownDoc, auditionRef, sessionRef, doc, dirty, revision } = ctx;
  // ── P8: the preview plays the FLATTENED document (compound and multicam clips expanded by the
  // renderer's own rules), so nothing in the compositor or the audio graph knows nesting exists.
  const previewDoc = useMemo(() => flattenTimeline(shownDoc), [shownDoc]);
  const flatCache = useRef<{ source: EditTimeline | null; flat: EditTimeline | null }>({ source: null, flat: null });
  const transformDraft = useMemo(() => draftFor(store), [store]);
  const draftCache = useRef<{ flat: EditTimeline | null; draft: TransformDraftState; out: EditTimeline | null }>({ flat: null, draft: null, out: null });
  const frameDraft = useMemo(() => frameDraftFor(store), [store]);
  const onWindowTarget = useCallback((target: { itemId: string; nodeId: string } | null) => windowTargetFor(store).set(target), [store]);
  const frameDraftCache = useRef<{ base: EditTimeline | null; draft: FrameDraftState; out: EditTimeline | null }>({ base: null, draft: null, out: null });
  const getPreviewDoc = useCallback(() => {
    const current = auditionRef.current ? auditionRef.current.preview : store.getState().doc;
    if (flatCache.current.source !== current) flatCache.current = { source: current, flat: flattenTimeline(current) };
    const flat = flatCache.current.flat as EditTimeline;
    // R07.2: a viewer drag shows in the preview before it is written (one batch of ops on release).
    const draft = transformDraft.get();
    let shown = flat;
    if (draft) {
      const cached = draftCache.current;
      if (cached.flat === flat && cached.draft === draft && cached.out) shown = cached.out;
      else {
        shown = applyDraft(flat, draft, store.getState().ui.playheadFrame);
        draftCache.current = { flat, draft, out: shown };
      }
    }
    // R10.3: a mask / reframe / power-window handle drag shows the same way.
    const fd = frameDraft.get();
    if (!fd) return shown;
    const fc = frameDraftCache.current;
    if (fc.base === shown && fc.draft === fd && fc.out) return fc.out;
    const out = applyFrameDraft(shown, fd);
    frameDraftCache.current = { base: shown, draft: fd, out };
    return out;
  }, [store, transformDraft, frameDraft, auditionRef]);
  // The paused picture follows the drag: re-present on the next animation frame (at most once per frame).
  useEffect(() => {
    let raf = 0;
    const unsubscribeFrame = frameDraft.subscribe(() => {
      if (raf) return;
      raf = requestAnimationFrame(() => {
        raf = 0;
        sessionRef.current?.invalidate();
      });
    });
    const unsubscribe = transformDraft.subscribe(() => {
      if (raf) return;
      raf = requestAnimationFrame(() => {
        raf = 0;
        sessionRef.current?.invalidate();
      });
    });
    return () => {
      unsubscribe();
      unsubscribeFrame();
      if (raf) cancelAnimationFrame(raf);
    };
  }, [transformDraft, frameDraft, sessionRef]);

  // ── P8: an AI-assist proposal's ghost diff, painted over the timeline while it is reviewed. New
  // lanes the proposal would create are shown as empty placeholder tracks at the bottom.
  const [ghostState, setGhostState] = useState<{ ghosts: ProposalGhost[]; newTracks: { id: string; name: string; kind: 'video' | 'audio' }[] } | null>(null);
  const timelineDoc = useMemo(() => {
    if (!ghostState || ghostState.newTracks.length === 0) return doc;
    const placeholders: Track[] = ghostState.newTracks
      .filter((t) => !doc.tracks.some((d) => d.id === t.id))
      .map((t) => ({ id: t.id, kind: t.kind, name: `${t.name} (proposed)`, height: 48, locked: true, enabled: true, mute: false, solo: false, items: [] }));
    return { ...doc, tracks: [...doc.tracks, ...placeholders] };
  }, [doc, ghostState]);

  // ── renders (P4): the queue, and the cache picture of the render that matches this revision ──
  const { data: renders, isLoading: rendersLoading, mutate: mutateRenders } = useEditTimelineRenders(store.getState().projectId, store.getState().timelineId);
  const refreshRenders = useCallback(() => {
    void mutateRenders();
  }, [mutateRenders]);
  // Only a finished render of the revision on screen describes these frames; unsaved edits or an
  // older render would paint cache state for a document that no longer exists.
  const latestRender = useMemo(() => (dirty ? null : latestRenderForRevision(renders, revision)), [renders, revision, dirty]);
  const renderRegions = useMemo(() => latestRender?.regions?.list ?? undefined, [latestRender]);
  const renderCache = latestRender?.regions ? { total: latestRender.regions.total, cached: latestRender.regions.cached } : null;

  return { previewDoc, onWindowTarget, getPreviewDoc, ghostState, setGhostState, timelineDoc, renders, rendersLoading, refreshRenders, latestRender, renderRegions, renderCache };
}

export type PreviewDocumentCtx = WorkspaceCoreCtx & ReturnType<typeof usePreviewDocument>;
