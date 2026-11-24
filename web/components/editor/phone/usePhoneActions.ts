'use client';

import { useCallback, useMemo } from 'react';
import type { EditorStore } from '@/lib/editing/store';
import type { EditOp, TrimEdge, TrimMode } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import type { UiState } from '@/lib/editing/store';
import { newMarkerId, allMarkerFrames } from '@/lib/editing/markers/markers';
import { addAtPlayhead } from '@/lib/editing/structure/add-at-playhead';
import { nextAfter, previousBefore } from '@/lib/editing/transport';
import { buildMoveInOrderOps, buildSpeedOps, buildTrimStepOps, buildVolumeOps, foldHistorySince } from '@/lib/editing/timeline-edit';
import { groupOf, linkedOpsForSetProperty } from '@/lib/editing/link';
import { newGuid } from '@/lib/editing/ids';
import { refusalSentence } from '@/lib/editing/refusal';
import type { useTimelineEditing } from '../timeline/useTimelineEditing';
import { useRefusalToast } from '../shell/refusal-toast';
import { showUndoSnackbar } from './UndoSnackbar';
import { canSplitAt, clipOfSelection, type PhoneActions } from './phone-actions';
import { setProfileOp } from '@/lib/editing/color/camera-profile-ops';
import { buildAddTitle, buildApplyToAllOps, buildLookOps, buildTitleTemplateOps, buildTitleTextOps, lookStateOf, lookTargetOf, selectedTitleOf } from './look-actions';
import { buildBlendOps, buildChannelOps, buildFadeOps, buildFitFillOps, buildOpacityOps, buildPadOps, buildRotateOps, clipSheetInfoOf } from './clip-sheet-actions';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { refusalSentenceMsg } from '@/lib/editing/labels-msg';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { useMediaPool } from '@/lib/hooks/use-media';
import { hdrKindOfProbe } from '@/lib/editing/media/hdr';
import { mediaRefOf } from '@/lib/editing/preview/scheduler';

export interface PhoneActionsDeps {
  store: EditorStore;
  doc: EditTimeline;
  ui: Pick<UiState, 'selection' | 'playheadFrame' | 'trimMode' | 'snapping' | 'gradeBypass'>;
  readOnly: boolean;
  editing: ReturnType<typeof useTimelineEditing>;
  split: () => void;
  nudge: (deltaFrames: number) => void;
  extractAudio: (itemId: string) => void;
  undo: () => void;
  addMarker: () => void;
  setInOut: (marks: UiState['inOut']) => void;
  selectMode: boolean;
  setSelectMode: (on: boolean) => void;
  /** The source picture size of a clip (from the media probe), for Fit and Fill. */
  sourceSizeFor?: (clip: MediaClip) => { width: number; height: number } | null;
  /** Opens the Media page with the playhead at the frame (the Events sheet's "Add sound here"). */
  openSounds?: (frame: number) => void;
}

/**
 * RM.5: the phone action bar's handlers. Every one calls the desktop's own handler or builder (R03.1-R03.3,
 * linked sound and picture R08.1) so a tap and a shortcut give the same document; destructive ones also
 * show the 6 second Undo (VF9) instead of asking first.
 */
export function usePhoneActions(deps: PhoneActionsDeps): PhoneActions {
  const tx = useT('editor.phone.usePhoneActions');
  const te = useT('editor-timeline.eventsLane');
  const { showRefusalMessage } = useRefusalToast();
  const { store, doc, ui, readOnly, editing, split, nudge, extractAudio, undo, addMarker, setInOut, selectMode, setSelectMode, sourceSizeFor, openSounds } = deps;

  const isLocked = useCallback(
    (trackId: string) => {
      const t = store.getState().doc.tracks.find((x) => x.id === trackId);
      return t ? store.effectiveTrackFlags(t).locked : false;
    },
    [store],
  );
  const linkOptions = useCallback(() => ({ isLocked, separately: store.getState().ui.editSeparately }), [isLocked, store]);

  /** Runs a destructive handler and, when it changed the document, offers Undo for 6 seconds. */
  const withUndo = useCallback(
    (message: (count: number) => string, run: () => void) => {
      const before = store.getState().cursor;
      const count = store.getState().ui.selection.length;
      run();
      if (store.getState().cursor > before) showUndoSnackbar({ message: message(count), onUndo: undo });
    },
    [store, undo],
  );

  const dispatchOps = useCallback(
    (ops: readonly EditOp[], label: string): string | null => {
      if (readOnly) return tx('thisTimelineCannotBe');
      try {
        store.dispatchBatch(ops, label);
        return null;
      } catch (error) {
        return tx.msg(refusalSentenceMsg(refusalSentence(error, store.getState().doc)));
      }
    },
    [store, readOnly, tx],
  );

  const clip = useMemo(() => clipOfSelection(doc, ui.selection), [doc, ui.selection]);
  const canSplit = useMemo(() => canSplitAt(doc, ui.selection, ui.playheadFrame), [doc, ui.selection, ui.playheadFrame]);

  // RM.10: the Look and fix sheet. The target is the selected picture clip, else the one under the playhead.
  const lookTarget = useMemo(() => lookTargetOf(doc, ui.selection, ui.playheadFrame), [doc, ui.selection, ui.playheadFrame]);
  const lookState = useMemo(() => lookStateOf(doc, lookTarget), [doc, lookTarget]);
  const titleClip = useMemo(() => selectedTitleOf(doc, ui.selection), [doc, ui.selection]);
  // The Look sheet is phone-only: read the pool there (SWR shares EditorWorkspace's entry) to hide the camera-profile
  // row on HDR, where the render ignores a profile. Off the phone the hook is idle, so desktop renders no request.
  const phone = useDeviceClass() === 'phone';
  const { data: mediaPool } = useMediaPool(phone ? store.getState().projectId : null);
  const lookTargetHdr = useMemo(() => {
    if (!lookTarget) return false;
    const ref = mediaRefOf(lookTarget.assetRef);
    return hdrKindOfProbe(ref ? mediaPool?.find((p) => p.mediaRef === ref)?.probe : null) !== null;
  }, [lookTarget, mediaPool]);

  // R07.7: the clip sheet acts on the picture half of a linked pair, else on the clip itself.
  const sheetClip = useMemo<MediaClip | null>(() => {
    if (!clip || clip.item.type !== 'mediaClip') return null;
    const picture = groupOf(doc, clip.item.id).find((m) => m.track.kind === 'video' && m.item.type === 'mediaClip');
    return (picture?.item as MediaClip | undefined) ?? (clip.item as MediaClip);
  }, [doc, clip]);
  const sheetInfo = useMemo(() => (sheetClip ? clipSheetInfoOf(doc, sheetClip, ui.playheadFrame) : null), [doc, sheetClip, ui.playheadFrame]);

  const run = useCallback(
    (r: { ok: true; ops: EditOp[]; label: string } | { ok: false; reason: string }): string | null => (r.ok ? dispatchOps(r.ops, r.label) : r.reason),
    [dispatchOps],
  );

  return useMemo<PhoneActions>(
    () => ({
      readOnly,
      rate: doc.settings.rate,
      selection: ui.selection,
      clip,
      canSplit,
      selectMode,
      setSelectMode,
      clearSelection: () => store.setUi({ selection: [] }),
      snapping: ui.snapping,
      trimMode: ui.trimMode,
      setTrimMode: (mode: TrimMode) => store.setUi({ trimMode: mode }),
      addText: () => {
        if (readOnly) return;
        const s = store.getState();
        const { ops, itemId } = addAtPlayhead(s.doc, 'title', s.ui.playheadFrame, newMarkerId);
        if (editing.apply(ops, tx('addTitle')) && store.getState().doc.tracks.some((t) => t.items.some((i) => i.id === itemId))) store.setUi({ selection: [itemId] });
      },
      addMarker,
      split,
      remove: () => withUndo((n) => (n === 1 ? tx('clipDeleted') : tx('clipsDeleted', { n })), editing.deleteSelection),
      duplicate: editing.duplicate,
      lift: () => withUndo((n) => (n === 1 ? tx('clipLiftedOutGap') : tx('clipsLiftedOutGaps', { n })), editing.liftSelection),
      nudge,
      moveInOrder: (direction) => {
        const s = store.getState();
        const r = buildMoveInOrderOps(s.doc, s.ui.selection, direction, isLocked);
        if (!r.ok) return showRefusalMessage(r.reason);
        const problem = dispatchOps(r.ops, direction < 0 ? tx('moveEarlier') : tx('moveLater'));
        if (problem) showRefusalMessage(problem);
      },
      extractAudio: () => clip && extractAudio(clip.item.id),
      link: editing.linkSelected,
      unlink: editing.unlinkSelected,
      freezeFrame: editing.freezeFrame,
      copy: editing.copy,
      cut: editing.cut,
      paste: editing.paste,
      previousMarker: () => {
        const s = store.getState();
        const frame = previousBefore(allMarkerFrames(s.doc), s.ui.playheadFrame);
        if (frame === null) return showRefusalMessage(tx('thereIsNoMarker'));
        store.setUi({ playheadFrame: frame });
      },
      nextMarker: () => {
        const s = store.getState();
        const frame = nextAfter(allMarkerFrames(s.doc), s.ui.playheadFrame);
        if (frame === null) return showRefusalMessage(tx('thereIsNoMarker2'));
        store.setUi({ playheadFrame: frame });
      },
      clearInOut: () => setInOut({ inFrame: null, outFrame: null }),
      toggleSnapping: () => store.setUi({ snapping: !store.getState().ui.snapping }),
      trimStep: (edge: TrimEdge, deltaFrames: number) => {
        const s = store.getState();
        const id = s.ui.selection[0];
        if (!id) return tx('selectAClipFirst');
        const r = buildTrimStepOps(s.doc, id, edge, s.ui.trimMode, deltaFrames, linkOptions());
        if (!r.ok) return r.reason;
        return dispatchOps(r.ops, r.label);
      },
      trimNumeric: (field, frames) => {
        const s = store.getState();
        const id = s.ui.selection[0];
        if (!id) return tx('selectAClipFirst2');
        return editing.applyNumericTrim(id, field, frames);
      },
      beginTrim: () => store.getState().cursor,
      endTrim: (start: number) => {
        try {
          foldHistorySince(store, start, tx('trimClip'));
        } catch (error) {
          showRefusalMessage(tx.msg(refusalSentenceMsg(refusalSentence(error, store.getState().doc))));
        }
      },
      setSpeed: (speed: number) => {
        const s = store.getState();
        const id = s.ui.selection[0];
        if (!id) return tx('selectAClipFirst3');
        const r = buildSpeedOps(s.doc, id, speed, linkOptions());
        if (!r.ok) return r.reason;
        const problem = dispatchOps(r.ops, r.label);
        if (!problem && r.fadesShrunk) showRefusalMessage(tx('theFadesWereLonger'));
        return problem;
      },
      setVolume: (percent: number) => {
        const s = store.getState();
        const id = s.ui.selection[0];
        if (!id) return tx('selectAClipFirst4');
        // A picture clip's sound lives on its paired sound clip: set that one.
        const own = s.doc.tracks.flatMap((t) => t.items.map((item) => ({ item, kind: t.kind }))).find((x) => x.item.id === id);
        const target = own?.kind === 'audio' ? id : (groupOf(s.doc, id).find((m) => m.track.kind === 'audio')?.item.id ?? id);
        const r = buildVolumeOps(s.doc, target, percent, linkOptions());
        if (!r.ok) return r.reason;
        return dispatchOps(r.ops, r.label);
      },
      events: {
        store,
        doc,
        projectId: store.getState().projectId ?? '',
        timelineId: store.getState().timelineId ?? '',
        readOnly,
        apply: (ops, label) => {
          const refused = dispatchOps(ops, label);
          if (refused !== null) notifications.show({ color: 'yellow', title: te('nothingChanged'), message: refused });
          return refused === null;
        },
        openSounds,
        isLocked,
      },
      look: {
        state: lookState,
        hdr: lookTargetHdr,
        title: titleClip,
        bypass: ui.gradeBypass,
        setBypass: (on) => store.setUi({ gradeBypass: on }),
        change: (change) => {
          if (!lookTarget) return tx('thereIsNoPicture');
          return run(buildLookOps(lookTarget, change, newGuid));
        },
        applyToAll: () => {
          if (!lookTarget) return tx('thereIsNoPicture2');
          const r = buildApplyToAllOps(store.getState().doc, lookTarget, newGuid);
          if (!r.ok) return r.reason;
          const problem = dispatchOps(r.ops, tx('applyLookToAll'));
          if (!problem) showUndoSnackbar({ message: tx('lookAppliedToOther', { count2: r.count }), onUndo: undo });
          return problem;
        },
        setProfile: (profile) => {
          if (!lookTarget) return tx('thereIsNoPicture');
          return dispatchOps([setProfileOp(lookTarget.id, profile)], profile ? tx('cameraProfile') : tx('cameraProfileOff'));
        },
        addTitle: (text, template) => {
          const s = store.getState();
          const r = buildAddTitle(s.doc, text, template, s.ui.playheadFrame, newGuid);
          if (!r.ok) return r.reason;
          const problem = dispatchOps(r.ops, r.label);
          if (!problem) store.setUi({ selection: [r.itemId] });
          return problem;
        },
        setTitleText: (text) => (titleClip ? run(buildTitleTextOps(titleClip, text)) : tx('selectATitleFirst')),
        setTitleTemplate: (template) => (titleClip ? run(buildTitleTemplateOps(titleClip, template)) : tx('selectATitleFirst2')),
      },
      clipSheet: {
        info: sheetInfo,
        fitFill: (kind) => (sheetInfo ? run(buildFitFillOps(store.getState().doc, sheetInfo, kind, sourceSizeFor?.(sheetInfo.clip) ?? null, store.getState().ui.playheadFrame)) : tx('selectAClipFirst5')),
        rotate: () => (sheetInfo ? run(buildRotateOps(sheetInfo)) : tx('selectAClipFirst5')),
        pad: (col, row) => (sheetInfo ? run(buildPadOps(store.getState().doc, sheetInfo, col, row, sourceSizeFor?.(sheetInfo.clip) ?? null, store.getState().ui.playheadFrame)) : tx('selectAClipFirst6')),
        setChannel: (channel, percent) => (sheetInfo ? run(buildChannelOps(sheetInfo, channel, percent, store.getState().ui.playheadFrame)) : tx('selectAClipFirst7')),
        setOpacity: (percent) => (sheetInfo ? run(buildOpacityOps(sheetInfo, percent)) : tx('selectAClipFirst8')),
        setBlendMode: (mode) => (sheetInfo ? run(buildBlendOps(sheetInfo, mode)) : tx('selectAClipFirst8')),
        setFade: (which, seconds) => {
          if (!sheetInfo) return tx('selectAClipFirst9');
          const r = buildFadeOps(sheetInfo, which, seconds, store.getState().doc.settings.rate);
          if (!r.ok) return r.reason;
          const op = r.ops[0] as Extract<EditOp, { type: 'setProperty' }>;
          const ops = linkedOpsForSetProperty(store.getState().doc, op.itemId, op.key, op.value, linkOptions());
          return dispatchOps(ops, r.label);
        },
        objects: {
          projectId: store.getState().projectId ?? null,
          setProperty: (key, value, label) => (sheetInfo ? dispatchOps([{ type: 'setProperty', itemId: sheetInfo.clip.id, key, value }], label ?? tx('objectEffectLabel')) : tx('selectAClipFirst5')),
        },
      },
    }),
    [lookState, lookTargetHdr, lookTarget, titleClip, sheetInfo, sourceSizeFor, run, ui.gradeBypass, readOnly, doc.settings.rate, ui.selection, ui.snapping, ui.trimMode, clip, canSplit, selectMode, setSelectMode, store, editing, addMarker, split, withUndo, nudge, isLocked, dispatchOps, extractAudio, setInOut, linkOptions, tx, te, openSounds, showRefusalMessage],
  );
}
