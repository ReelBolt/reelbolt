import { useCallback, useMemo, useState, useRef } from 'react';
import { notifications } from '@mantine/notifications';
import { undoStep, redoStep } from '@/lib/editing/store';
import { type EditOp } from '@/lib/editing/engine';
import { type LinkOptions, locate, linkIdOf, linkedOpsForBlade } from '@/lib/editing/link';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { mediaRefOf } from '@/lib/editing/preview';
import { framesToTimecode } from '@/lib/editing/time';
import { type PlaceMode } from '@/lib/editing/media/place';
import { type TrimDragContext, type TrimDragRequest, planTrimDrag } from '@/lib/editing/trim-readout';
import { useTimelineEditing } from '../timeline/useTimelineEditing';
import { useToolbarFold } from '../timeline/useToolbarFold';
import { gapAt, buildDeleteGapOps, buildCloseAllGapsOps } from '@/lib/editing/timeline-edit/gap-objects';
import { showRecordFirst } from '@/lib/editing/commands';
import { useT } from '@/i18n';
import type { TransportCtx } from './useTransport';

/** The edits a timeline offers: markers, undo and redo, gaps, links, trims, the blade, and the clip menu's actions. */
export function useTimelineEdits(ctx: TransportCtx) {
  const { store, dispatch, readOnly, showRefusal, pool, isPhone, applyBatch, ui, doc, rate, setRightTab, setMarks, endFrame, revealAdjustment, isTablet } = ctx;
  const tx = useT('editor.editorWorkspace');
  const tm = useT('editor-timeline.menu');
  const addMarker = useCallback(
    (name: string, frame?: number) => {
      dispatch({ type: 'addMarker', marker: { id: newMarkerId(), frame: frame ?? store.getState().ui.playheadFrame, name, color: null, note: null, durationFrames: null } }, tx('addMarker', { name }));
    },
    [dispatch, store, tx],
  );

  // Undo and redo are on screen (a phone has no Ctrl+Z) and say so when a step cannot be taken back.
  const undo = useCallback(() => {
    const result = undoStep(store);
    if (result.status === 'refused') notifications.show({ color: 'red', title: result.title, message: result.message });
  }, [store]);
  const redo = useCallback(() => {
    const result = redoStep(store);
    if (result.status === 'refused') notifications.show({ color: 'red', title: result.title, message: result.message });
  }, [store]);

  // Closing a gap: later clips on that track shift left (one undo step). Lift leaves gaps; this closes them.
  const closeGapBefore = useCallback(
    (trackId: string, atFrame: number) => {
      if (readOnly) return;
      try {
        const doc = store.getState().doc;
        const gap = gapAt(doc, trackId, atFrame);
        const isLocked = (id: string) => {
          const t = doc.tracks.find((x) => x.id === id);
          return t ? store.effectiveTrackFlags(t).locked : false;
        };
        // A camera sound beside its picture closes the same gap with it, so the pair stays in step.
        if (gap) store.dispatchBatch(buildDeleteGapOps(gap, doc, { isLocked }), tx('closeGap'));
        else store.dispatch({ type: 'deleteGap', trackId, atFrame }, tx('closeGap2'));
      } catch (error) {
        showRefusal(error, store.getState().doc);
      }
    },
    [store, readOnly, tx],
  );
  const closeTrackGaps = useCallback(
    (trackId: string) => {
      if (readOnly) return;
      try {
        const doc = store.getState().doc;
        const isLocked = (id: string) => {
          const t = doc.tracks.find((x) => x.id === id);
          return t ? store.effectiveTrackFlags(t).locked : false;
        };
        const ops = buildCloseAllGapsOps(doc, trackId, { isLocked });
        if (ops.length > 0) store.dispatchBatch(ops, tx('closeAllGaps2'));
        else store.dispatch({ type: 'closeGaps', trackId }, tx('closeAllGaps3'));
      } catch (error) {
        showRefusal(error, store.getState().doc);
      }
    },
    [store, readOnly, tx],
  );

  // R08.1: link options for every edit (partners on a locked track are left alone). "Edit sound separately" bypasses linking.
  const linkOptions = useCallback(
    (): LinkOptions => ({
      isLocked: (trackId: string) => {
        const t = store.getState().doc.tracks.find((x) => x.id === trackId);
        return t ? store.effectiveTrackFlags(t).locked : false;
      },
    }),
    [store],
  );
  // One plan for a trim drag: the canvas previews it and the release dispatches it. The source lengths are the media pool's probes, the
  // ones the server validates against; a source without one is not clamped, as the server does not check it either. The phone keeps its own limits.
  const trimContext = useMemo<TrimDragContext>(() => {
    const seconds = new Map((pool ?? []).map((p) => [p.mediaRef, p.probe?.durationSec ?? null]));
    return {
      linkOptions: linkOptions(),
      sourceFramesFor: isPhone
        ? undefined
        : (clip) => {
            const ref = mediaRefOf(clip.assetRef);
            const known = ref ? seconds.get(ref) : null;
            const { num, den } = store.getState().doc.settings.rate;
            return known && known > 0 && den !== 0 ? Math.ceil((known * num) / den) : null;
          },
    };
  }, [pool, linkOptions, isPhone, store]);
  const planTrim = useCallback(
    (request: TrimDragRequest) => {
      const state = store.getState();
      return planTrimDrag(state.doc, { ...request, separately: request.separately === true || state.ui.editSeparately }, trimContext);
    },
    [store, trimContext],
  );
  const commitTrim = useCallback(
    (itemId: string, edge: 'in' | 'out', deltaFrames: number, separately?: boolean) => {
      const { doc: d, ui: current } = store.getState();
      const plan = planTrim({ itemId, edge, mode: current.trimMode, deltaFrames, separately });
      const apart = (separately === true || current.editSeparately) && current.trimMode === 'ripple' && (() => { const found = locate(d, itemId); return found !== null && linkIdOf(found.item) !== null; })();
      applyBatch(plan.ops, apart ? tx('trimSoundSeparately') : `Trim ${edge} (${current.trimMode})`);
    },
    [store, planTrim, applyBatch, tx],
  );
  const bladeAtPlayhead = useCallback(() => {
    const frame = store.getState().ui.playheadFrame;
    const targets = ui.selection.length > 0 ? new Set(ui.selection) : null;
    const ops: EditOp[] = [];
    // R08.1: cutting a linked clip cuts its partners too; a partner already cut with its group is not cut twice.
    const cut = new Set<string>();
    const linkOpts = linkOptions();
    for (const track of doc.tracks) {
      if (!targets && !ui.targetedTrackIds.includes(track.id)) continue;
      for (const item of track.items) {
        if (targets && !targets.has(item.id)) continue;
        if (cut.has(item.id)) continue;
        if (item.recordIn < frame && frame < item.recordOut) {
          if (store.getState().ui.editSeparately) {
            ops.push({ type: 'blade', itemId: item.id, atFrame: frame, newItemId: newMarkerId() });
            continue;
          }
          const blade = linkedOpsForBlade(doc, item.id, frame, newMarkerId(), linkOpts);
          for (const id of Object.keys(blade.rightHalves)) cut.add(id);
          ops.push(...blade.ops);
        }
      }
    }
    if (ops.length === 0) {
      notifications.show({
        color: 'yellow',
        title: tx('nothingToSplitHere'),
        message: targets
          ? tx('moveThePlayheadInside')
          : tx('moveThePlayheadInside2'),
      });
      return;
    }
    try {
      store.dispatchBatch(ops, tx('bladeAt', { v1: framesToTimecode(frame, rate) }));
    } catch (error) {
      showRefusal(error, store.getState().doc);
    }
  }, [store, doc, ui.selection, ui.targetedTrackIds, rate, linkOptions, tx]);

  // R03.1/R03.2/R03.3: every timeline editing action (delete, lift, gaps, clipboard, menu actions) in one place.
  const [replaceFor, setReplaceFor] = useState<string | null>(null);
  const openInspector = useCallback((focusSpeed: boolean) => {
    setRightTab('inspector');
    if (focusSpeed) window.setTimeout(() => document.querySelector<HTMLElement>('[data-testid="inspector-speed"]')?.focus(), 50);
  }, [setRightTab]);
    // T-018: the menu reads the live paste chord and the loaded source's placement when it needs them (both are set up further down).
  const pasteChordRef = useRef<string | undefined>(undefined);
  const placeRef = useRef<((mode: PlaceMode) => void) | null>(null);
  const editing = useTimelineEditing({
    store,
    readOnly,
    doc,
    pool,
    split: bladeAtPlayhead,
    openInspector,
    openReplace: setReplaceFor,
    getPasteChord: () => pasteChordRef.current,
    setMarks,
    addMarkerAt: (frame) => addMarker('Marker', Math.max(0, Math.min(Math.trunc(frame), Math.max(0, endFrame - 1)))),
    getPlace: () => placeRef.current,
    revealAdjustment,
  });
  const pointMenuLabel = editing.menuTarget.area === 'clip' ? undefined : tm('pointActions');
  // RM.5 Select mode: while on, a tap on a clip adds it to (or takes it from) the selection, like shift-click on a desktop.
  const [selectMode, setSelectMode] = useState(false);
  const extractSelection = editing.deleteSelection;
  // A toolbar writer run while Source shows returns to Record first, so the user sees what changed. Undo stays as it is.
  const recordFirst = useCallback(
    <A extends unknown[]>(run: (...args: A) => void) =>
      (...args: A): void => {
        showRecordFirst(store);
        run(...args);
      },
    [store],
  );
  const { ref: toolbarRef, fold: toolbarFold } = useToolbarFold(!isPhone && !isTablet);
  const liftSelection = editing.liftSelection;

  return { addMarker, undo, redo, closeGapBefore, closeTrackGaps, linkOptions, planTrim, commitTrim, bladeAtPlayhead, replaceFor, setReplaceFor, openInspector, pasteChordRef, placeRef, editing, pointMenuLabel, selectMode, setSelectMode, extractSelection, recordFirst, toolbarRef, toolbarFold, liftSelection };
}

export type TimelineEditsCtx = TransportCtx & ReturnType<typeof useTimelineEdits>;
