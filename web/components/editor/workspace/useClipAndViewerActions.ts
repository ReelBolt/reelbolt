import { useCallback, useState, useRef, useEffect } from 'react';
import { notifications } from '@mantine/notifications';
import { type EditorTool, type UiState } from '@/lib/editing/store';
import { type PropertyKey, type PropertyValue, type EditOp } from '@/lib/editing/engine';
import { linkedOpsForSetProperty } from '@/lib/editing/link';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { subscribeToPageNavigation } from '@/lib/editing/page-url';
import { framesToTimecode } from '@/lib/editing/time';
import type { EditTimeline } from '@/lib/editing/types';
import { enterViewerTool, maskRedrawFor } from '@/lib/editing/viewer-transform';
import { type FlushResult } from '../deliver/DeliverWorkspace';
import { saveFailureMsg } from '../deliver/deliver-model';
import { type GalleryStill } from '../color/ColorWorkspace';
import type { FrameStats } from '@/lib/editing/color/grade';
import { findComposeItem } from '@/lib/editing/compose/entries';
import { sampleCanvas, computeScopes } from '@/lib/editing/color/scopes';
import { programPictureCanvas } from '@/lib/editing/stills/stills';
import { useT } from '@/i18n';
import type { TimelineCallbacksCtx } from './useTimelineCallbacks';

/** Saving before a render, restoring a version, setting a clip property, the viewer's tools, stills and scope statistics. */
export function useClipAndViewerActions(ctx: TimelineCallbacksCtx) {
  const { sync, store, readOnly, doc, linkOptions, applyBatch, canvasRef } = ctx;
  const tx = useT('editor.editorWorkspace');
  // R06.6: Deliver saves first, so a render always contains what is on screen.
  const flushForRender = useCallback(async (): Promise<FlushResult> => {
    await sync.flush();
    const state = sync.getState();
    const current = store.getState();
    if (!current.dirty && state.status === 'saved') return { ok: true, revision: current.revision };
    const failure = saveFailureMsg(state);
    return { ok: false, reason: typeof failure === 'string' ? failure : tx.msg(failure) };
  }, [sync, store, tx]);

  const onRestore = useCallback(
    (document: EditTimeline, fromRevision: number) => {
      if (readOnly) return;
      // A restore is an ordinary PATCH: the store takes the restored document as its new state
      // (dirty), the sync client sends it against the CURRENT revision with a `restore` marker.
      store.replaceDocument({ ...document, id: doc.id, projectId: doc.projectId }, store.getState().revision, { markDirty: true });
      sync.attachOps([{ type: 'restore', revision: fromRevision }]);
      void sync.flush();
      notifications.show({ color: 'blue', title: tx('restored'), message: tx('versionRestoredYourEarlier') });
    },
    [store, sync, doc.id, doc.projectId, readOnly, tx],
  );

  const onSetProperty = useCallback(
    (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => {
      // R08.1: group-wide properties (enabled) reach the linked partners in the same undo step.
      const ops = store.getState().ui.editSeparately ? [{ type: 'setProperty', itemId, key, value } as EditOp] : linkedOpsForSetProperty(store.getState().doc, itemId, key, value, linkOptions());
      applyBatch(ops, label ?? `Set ${key}`);
    },
    [store, applyBatch, linkOptions],
  );
  /** §7.3: a mask/reframe Add from the Inspector or Compose enters the shape tool (pauses, focuses the picture). */
  const activateViewerTool = useCallback((tool: 'mask' | 'reframe' | 'pin') => enterViewerTool(store, tool), [store]);
  /** Compose's "Draw on the picture" / "Redraw": enters the mask tool, with Redraw armed when a mask exists. */
  const drawMask = useCallback(
    (redraw: boolean) => {
      const s = store.getState();
      const item = findComposeItem(s.doc, s.ui.selection);
      maskRedrawFor(store).set(redraw && item ? item.id : null);
      enterViewerTool(store, 'mask');
    },
    [store],
  );
  /** Compose's list (§12 rule 3): a shape entry that is on enters its tool; any other entry returns the viewer to Select. */
  const composeViewerTool = useCallback((tool: 'select' | 'mask' | 'reframe' | 'pin', options?: { focusPicture?: boolean }) => (tool === 'select' ? store.setUi({ viewerTool: 'select' }) : enterViewerTool(store, tool, options)), [store]);

  // P7: the stills gallery and the reference wipe. Stills are preview captures held in memory for
  // the session (data URLs); the wipe draws the reference over the viewer, clipped at `wipe`.
  const [gallery, setGallery] = useState<GalleryStill[]>([]);
  const [referenceId, setReferenceId] = useState<string | null>(null);
  const [wipe, setWipe] = useState(0.5);
  const getCanvas = useCallback(() => canvasRef.current, [canvasRef]);
  const captureStill = useCallback(
    async (label: string) => {
      const canvas = programPictureCanvas(canvasRef.current);
      if (!canvas) {
        notifications.show({ color: 'yellow', title: tx('noPictureToGrab'), message: tx('thePreviewHasNo2') });
        return;
      }
      let dataUrl: string;
      try {
        dataUrl = canvas.toDataURL('image/png');
      } catch {
        notifications.show({ color: 'yellow', title: tx('noPictureToGrab2'), message: tx('thePreviewCanvasCould2') });
        return;
      }
      const buffer = sampleCanvas(canvas, 192);
      const stats = buffer ? computeScopes(buffer, { columns: 32, levels: 64, vectorSize: 16 }).stats : null;
      const state = store.getState();
      const still: GalleryStill = { id: newMarkerId(), dataUrl, label: `${label} · ${framesToTimecode(state.ui.playheadFrame, state.doc.settings.rate)}`, stats };
      setGallery((g) => [...g, still]);
      setReferenceId(still.id);
    },
    [store, tx, canvasRef],
  );
  const reference = gallery.find((s) => s.id === referenceId) ?? null;
  // §8.2: the scopes sample the page whether or not they are collapsed; "Match to reference" reads this.
  const scopeStatsRef = useRef<FrameStats | null>(null);

  useEffect(
    () =>
      subscribeToPageNavigation(store, {
        target: window,
        href: () => window.location.href,
        storage: window.localStorage,
      }),
    [store],
  );
  // A timeline tool leaves any picture tool (§7.3): its play-time persistence ends, the paused auto-show stays.
  const setTool = (tool: EditorTool) => store.setUi({ tool, viewerTool: 'select' });
  const setUi = (patch: Partial<UiState>) => store.setUi(patch);

  return { flushForRender, onRestore, onSetProperty, activateViewerTool, drawMask, composeViewerTool, gallery, setGallery, referenceId, setReferenceId, wipe, setWipe, getCanvas, captureStill, reference, scopeStatsRef, setTool, setUi };
}

export type ClipAndViewerActionsCtx = TimelineCallbacksCtx & ReturnType<typeof useClipAndViewerActions>;
