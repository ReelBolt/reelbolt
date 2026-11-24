import { useCallback, useEffect, useRef, useMemo } from 'react';
import { type EditOp, apply as applyOp } from '@/lib/editing/engine';
import { firstDifferenceFrame } from '@/lib/editing/audition';
import { effectivePlayRange, timelineEndFrame } from '@/lib/editing/transport';
import { snapFrame, snapCandidates, snapThresholdFrames, snapSourceAt } from '@/lib/editing/selection';
import { useEventsLaneController } from '../timeline/useEventsLaneController';
import { useBeatGridOverlay } from '@/lib/editing/beats/use-beat-grid-overlay';
import { useCutToBeatFeeds } from '@/lib/editing/beats/use-cut-to-beat-feeds';
import type { EditDispatchCtx } from './useEditDispatch';

/** Seek, speed, play ranges, the audition, the beat grid, the Events lane and snapping. */
export function useTransport(ctx: EditDispatchCtx) {
  const { store, endFrame, auditionRef, showRefusal, setAudition, applyBatch, auditionState, audition, doc, ui, showBeats, beatsOpen, readOnly } = ctx;
  // ── transport ─────────────────────────────────────────────────────────────
  const seek = useCallback(
    (frame: number) => store.setUi({ playheadFrame: Math.max(0, Math.min(Math.max(0, endFrame - 1), Math.trunc(frame))) }),
    [store, endFrame],
  );
  // Anything that plays the timeline (the transport, Cut's review, an audition, play in to out) returns to Record first:
  // the Source monitor is not the timeline, and the timeline's playhead never advances behind it.
  const setSpeed = useCallback((speed: number) => store.setUi(speed !== 0 && store.getState().ui.viewerMode === 'source' ? { viewerMode: 'record', playbackSpeed: speed } : { playbackSpeed: speed }), [store]);
  useEffect(
    () =>
      store.subscribe(() => {
        const current = store.getState().ui;
        if (current.viewerMode === 'source' && current.playbackSpeed !== 0) store.setUi({ playbackSpeed: 0 });
      }),
    [store],
  );
  // PA-7: Play in to out holds until playback stops; Loop wraps inside the viewer (play-range.ts).
  const inToOut = useRef(false);
  useEffect(
    () =>
      store.subscribe(() => {
        if (store.getState().ui.playbackSpeed === 0) inToOut.current = false;
      }),
    [store],
  );
  const playInToOut = useCallback(() => {
    const current = store.getState().ui;
    if (current.inOut.inFrame === null) return false;
    inToOut.current = true;
    store.setUi({ playheadFrame: Math.min(current.inOut.inFrame, current.inOut.outFrame ?? current.inOut.inFrame), playbackSpeed: 1, ...(current.viewerMode === 'source' ? { viewerMode: 'record' as const } : {}) });
    return true;
  }, [store]);
  const getPlayRange = useCallback(() => {
    const current = store.getState();
    return effectivePlayRange(current.ui.inOut, { loop: current.ui.loop, inToOut: inToOut.current, endFrame: timelineEndFrame(auditionRef.current ? auditionRef.current.preview : current.doc) });
  }, [store, auditionRef]);
  const startAudition = useCallback(
    (ops: readonly EditOp[], label: string) => {
      const base = store.getState().doc;
      let preview = base;
      try {
        for (const op of ops) preview = applyOp(preview, op).doc;
      } catch (error) {
        showRefusal(error, base);
        return;
      }
      const fps = base.settings.rate.den === 0 ? 30 : base.settings.rate.num / base.settings.rate.den;
      const from = Math.max(0, firstDifferenceFrame(base, preview) - Math.round(2 * fps));
      setAudition({ base, preview, ops: [...ops], label });
      store.setUi({ playheadFrame: from, playbackSpeed: 1, ...(store.getState().ui.viewerMode === 'source' ? { viewerMode: 'record' as const } : {}) });
    },
    [store, setAudition],
  );
  const cancelAudition = useCallback(() => {
    setAudition(null);
    store.setUi({ playbackSpeed: 0 });
  }, [store, setAudition]);
  const applyAudition = useCallback(() => {
    const current = auditionRef.current;
    if (!current) return;
    setAudition(null);
    store.setUi({ playbackSpeed: 0 });
    applyBatch(current.ops, current.label);
  }, [store, applyBatch, auditionRef, setAudition]);
  const lostAudition = auditionState !== null && audition === null;
  useEffect(() => {
    if (lostAudition) store.setUi({ playbackSpeed: 0 });
  }, [lostAudition, store]);
  const onEnded = useCallback(() => {
    store.setUi({ playbackSpeed: 0 });
  }, [store]);

  const beatOverlay = useBeatGridOverlay(store.getState().projectId, doc, ui.selection);
  const beatFrames = useMemo(() => (showBeats ? beatOverlay.beats.map((b) => b.frame) : undefined), [showBeats, beatOverlay.beats]);
  const beatGrid = useMemo(() => (showBeats && beatOverlay.beats.length > 0 ? beatOverlay.beats : undefined), [showBeats, beatOverlay.beats]);
  const beatFeeds = useCutToBeatFeeds(store.getState().projectId, doc, beatsOpen);

  // The Events lane: ticks, snap targets and "Act on this event". "Add sound here" goes through a ref because the
  // workspace switcher is defined further down.
  const openSoundsRef = useRef<(frame: number) => void>(() => {});
  const openSounds = useCallback((frame: number) => openSoundsRef.current(frame), []);
  const isTrackLocked = useCallback(
    (trackId: string) => {
      const t = store.getState().doc.tracks.find((x) => x.id === trackId);
      return t ? store.effectiveTrackFlags(t).locked : false;
    },
    [store],
  );
  const eventsLane = useEventsLaneController({
    store,
    doc,
    projectId: store.getState().projectId ?? '',
    timelineId: store.getState().timelineId ?? '',
    readOnly,
    apply: applyBatch,
    openSounds,
    isLocked: isTrackLocked,
  });
  const snapExtra = useMemo(() => (beatFrames || eventsLane.snapFrames ? [...(beatFrames ?? []), ...(eventsLane.snapFrames ?? [])] : undefined), [beatFrames, eventsLane.snapFrames]);

  const snap = useCallback(
    (frame: number, excludeItemIds?: readonly string[]) =>
      snapFrame(
        frame,
        snapCandidates(doc, { playheadFrame: store.getState().ui.playheadFrame, inFrame: ui.inOut.inFrame, outFrame: ui.inOut.outFrame, excludeItemIds, extraFrames: snapExtra }),
        snapThresholdFrames(ui.pixelsPerFrame),
        ui.snapping,
      ).frame,
    [doc, store, ui.inOut, ui.pixelsPerFrame, ui.snapping, snapExtra],
  );

  const snapSource = useCallback(
    (frame: number, excludeItemIds?: readonly string[]) =>
      snapSourceAt(doc, { playheadFrame: store.getState().ui.playheadFrame, inFrame: ui.inOut.inFrame, outFrame: ui.inOut.outFrame, excludeItemIds }, frame),
    [doc, store, ui.inOut],
  );

  return { seek, setSpeed, playInToOut, getPlayRange, startAudition, cancelAudition, applyAudition, onEnded, beatOverlay, beatGrid, beatFeeds, openSoundsRef, openSounds, eventsLane, snap, snapSource };
}

export type TransportCtx = EditDispatchCtx & ReturnType<typeof useTransport>;
