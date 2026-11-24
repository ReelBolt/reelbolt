'use client';

import { useCallback, useMemo, useState } from 'react';
import { notifications } from '@mantine/notifications';
import type { EditorStore } from '@/lib/editing/store';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { newGuid } from '@/lib/editing/ids';
import { mediaRefOf } from '@/lib/editing/preview';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import {
  buildCloseAllGapsOps,
  buildDeleteGapOps,
  buildDeleteOps,
  buildDuplicateOps,
  fitToFillAvailability,
  fitToFillConfirmLine,
  joinAvailability,
  replaceAvailability,
  buildLiftOps,
  buildMoveToTrackOps,
  buildNumericTrimOps,
  buildPasteOps,
  buildToggleEnabledOps,
  copyClips,
  gapStillExists,
  interiorGapCount,
  readClipClipboard,
  selectFromPlayhead,
  writeClipClipboard,
  type NumericField,
  type TimelineGap,
} from '@/lib/editing/timeline-edit';
import { isLinked, linkedOpsForDelete, linkedOpsForTrim, linkOps, unlinkOps } from '@/lib/editing/link';
import { freezeBlocker, freezeFrameOps } from '@/lib/editing/structure/freeze-frame';
import { enterViewerTool } from '@/lib/editing/viewer-transform/frame-handles';
import type { ClipMenuItem } from './ClipContextMenu';
import { showRecordFirst } from '@/lib/editing/commands';
import type { ClipBadgeKind } from '@/lib/editing/display/clip-badges';
import { adjustmentsMenuItems } from './badgeMenu';
import { DEFAULT_REFRAME_POINT, DEFAULT_SHAPE_MASK } from '../panels/MaskReframeFields';
import { useRefusalToast } from '../shell/refusal-toast';
import { useT } from '@/i18n';
import type { InOutMarks } from '@/lib/editing/store/editor-store';
import type { PlaceMode } from '@/lib/editing/media/place';
import { placeChoiceLabelMsg, PLACE_MODES } from '../place/placeChoices';

export interface TimelineEditingDeps {
  store: EditorStore;
  readOnly: boolean;
  doc: EditTimeline;
  pool: readonly MediaPoolItem[] | undefined;
  split: () => void;
  openInspector: (focusSpeed: boolean) => void;
  openReplace: (itemId: string) => void;
  /** The live display chord of Paste, for the "copied" toast; undefined when it is unbound. Read when it is needed. */
  getPasteChord?: () => string | undefined;
  /** In and out marks of the timeline (the workspace's one setter). */
  setMarks?: (marks: InOutMarks) => void;
  /** Adds a timeline marker at a frame. */
  addMarkerAt?: (frame: number) => void;
  /** The loaded source's placement, or null when no source is loaded. Read when the menu is built. */
  getPlace?: () => ((mode: PlaceMode) => void) | null;
  /** Selects the clip and opens the section of one of its adjustments (the "Adjustments" part of the menu). */
  revealAdjustment?: (itemId: string, kind: ClipBadgeKind) => void;
}

/** Where a menu was opened: on a clip, on empty lane space (a gap is part of it), or on the ruler and marker lane. */
export type MenuArea = 'clip' | 'lane' | 'ruler';
export interface MenuTarget {
  area: MenuArea;
  /** The timeline frame under the pointer (the playhead for the keyboard path). */
  frame: number;
}
const CLIP_TARGET: MenuTarget = { area: 'clip', frame: 0 };

/** Menu entries that do not change the timeline, or (placements) are how Source writes forward: they leave the viewer on Source. */
const KEEPS_SOURCE: ReadonlySet<string> = new Set([
  'copy', 'speed', 'properties', 'edit-separately', 'select-forward', 'select-backward', 'select-forward-all', 'select-backward-all',
  'point-mark-in', 'point-mark-out', 'point-clear-marks', 'point-insert', 'point-overwrite', 'point-append', 'point-placeOnTop',
]);

function recordFirstItems(store: EditorStore, items: ClipMenuItem[]): ClipMenuItem[] {
  return items.map((item) => (KEEPS_SOURCE.has(item.id) ? item : { ...item, run: () => { showRecordFirst(store); item.run(); } }));
}

/**
 * R03.1/R03.2/R03.3: every timeline editing action as one small function over the store, shared by
 * the keyboard, the right-click menu, the "..." button and the buttons. Each builds its ops with the
 * pure builders in `lib/editing/timeline-edit` and applies them as ONE undo step.
 */
export function useTimelineEditing({ store, readOnly, doc, pool, split, openInspector, openReplace, getPasteChord, setMarks, addMarkerAt, getPlace, revealAdjustment }: TimelineEditingDeps) {
  const t = useT('editor-timeline.edit');
  const tp = useT('editor-misc.place');
  const tBadges = useT('editor-timeline.badges');
  const { showRefusal, showRefusalMessage } = useRefusalToast();
  const [selectedGap, setSelectedGapState] = useState<TimelineGap | null>(null);
  const [anchor, setAnchor] = useState<{ x: number; y: number } | null>(null);
  const [menuTarget, setMenuTarget] = useState<MenuTarget>(CLIP_TARGET);

  // A gap that an edit closed or resized is no longer selected.
  const liveGap = selectedGap && gapStillExists(doc, selectedGap) ? selectedGap : null;
  const setSelectedGap = useCallback((gap: TimelineGap | null) => setSelectedGapState(gap), []);

  const isLocked = useCallback(
    (trackId: string) => {
      const track = store.getState().doc.tracks.find((x) => x.id === trackId);
      return track ? store.effectiveTrackFlags(track).locked : false;
    },
    [store],
  );

  const guard = useCallback((): boolean => {
    if (readOnly) {
      notifications.show({ color: 'orange', title: t('readOnlyTitle'), message: t('readOnlyMessage') });
      return false;
    }
    return true;
  }, [readOnly, t]);

  const apply = useCallback(
    (ops: readonly EditOp[], label: string): boolean => {
      if (ops.length === 0 || !guard()) return false;
      try {
        store.dispatchBatch(ops, label);
        return true;
      } catch (error) {
        showRefusal(error, store.getState().doc);
        return false;
      }
    },
    [store, guard, showRefusal],
  );

  // ── delete / lift / gaps ───────────────────────────────────────────────────
  const deleteSelection = useCallback(() => {
    const s = store.getState();
    if (s.ui.selection.length === 0) {
      if (liveGap && apply(buildDeleteGapOps(liveGap, s.doc, { isLocked }), t('closeGap'))) setSelectedGapState(null);
      return;
    }
    const plan = s.ui.editSeparately ? buildDeleteOps(s.doc, s.ui.selection, isLocked) : linkedOpsForDelete(s.doc, s.ui.selection, 'ripple', isLocked);
    if (apply(plan.ops, t('deleteLabel', { count: plan.itemCount }))) {
      store.setUi({ selection: [] });
      if (plan.skippedLocked > 0) {
        notifications.show({ color: 'yellow', title: t('pairedLeftTitle'), message: t('pairedLeftMessage') });
      }
    }
  }, [store, apply, isLocked, liveGap, t]);

  const liftSelection = useCallback(() => {
    const s = store.getState();
    const ops = s.ui.editSeparately ? buildLiftOps(s.doc, s.ui.selection) : linkedOpsForDelete(s.doc, s.ui.selection, 'lift', isLocked).ops;
    if (apply(ops, t('liftLabel', { count: ops.length }))) store.setUi({ selection: [] });
  }, [store, apply, isLocked, t]);

  const closeAllGaps = useCallback(
    (trackId: string) => {
      const ops = buildCloseAllGapsOps(store.getState().doc, trackId, { isLocked });
      if (ops.length === 0) {
        notifications.show({ color: 'blue', title: t('noGapsTitle'), message: t('noGapsMessage') });
        return;
      }
      apply(ops, t('closeAllGaps'));
    },
    [store, apply, isLocked, t],
  );

  // ── link / unlink (R08.1) ──────────────────────────────────────────────────
  const linkSelected = useCallback(() => {
    const s = store.getState();
    const ops = linkOps(s.doc, s.ui.selection);
    if (ops.length === 0) return showRefusalMessage(t('linkRefusal'));
    apply(ops, t('linkLabel'));
  }, [store, apply, t]);
  const unlinkSelected = useCallback(() => {
    const s = store.getState();
    const ops = unlinkOps(s.doc, s.ui.selection);
    if (ops.length === 0) return showRefusalMessage(t('notLinked'));
    // Unlinking leaves the clips selected; with the group gone each is now its own selection.
    if (apply(ops, t('unlinkLabel'))) store.setUi({ selection: s.ui.selection.slice(0, 1) });
  }, [store, apply, t]);
  const toggleSeparately = useCallback(() => {
    store.setUi((ui) => ({ editSeparately: !ui.editSeparately }));
  }, [store]);

  // ── clipboard ──────────────────────────────────────────────────────────────
  const copy = useCallback((): boolean => {
    const s = store.getState();
    const payload = copyClips(s.doc, s.ui.selection);
    if (!payload) {
      notifications.show({ color: 'yellow', title: t('nothingToCopyTitle'), message: t('selectClipFirst') });
      return false;
    }
    writeClipClipboard(payload);
    notifications.show({ color: 'blue', title: t('copiedTitle', { count: payload.entries.length }), message: getPasteChord?.() ? t('copiedMessage', { count: payload.entries.length, shortcut: getPasteChord() }) : t('copiedMessageNoChord', { count: payload.entries.length }) });
    return true;
  }, [store, getPasteChord, t]);

  const cut = useCallback(() => {
    const s = store.getState();
    const payload = copyClips(s.doc, s.ui.selection);
    if (!payload || !guard()) return;
    writeClipClipboard(payload);
    deleteSelection();
  }, [store, guard, deleteSelection]);

  const pasteTrackId = useCallback((): string | null => {
    const s = store.getState();
    const selectedTrack = s.ui.selection.length > 0 ? s.doc.tracks.find((t) => t.items.some((i) => i.id === s.ui.selection[0])) : undefined;
    if (selectedTrack) return selectedTrack.id;
    return s.ui.targetedTrackIds.find((id) => s.doc.tracks.some((t) => t.id === id)) ?? s.doc.tracks[0]?.id ?? null;
  }, [store]);

  const paste = useCallback(() => {
    const payload = readClipClipboard();
    if (!payload) {
      notifications.show({ color: 'yellow', title: t('nothingToPasteTitle'), message: t('nothingToPasteMessage') });
      return;
    }
    const s = store.getState();
    const trackId = pasteTrackId();
    if (!trackId) return showRefusalMessage(t('addTrackFirst'));
    const result = buildPasteOps(s.doc, payload, { trackId, atFrame: s.ui.playheadFrame }, newGuid, isLocked);
    if (!result.ok) return showRefusalMessage(result.reason);
    if (apply(result.ops, t('pasteLabel', { count: payload.entries.length }))) store.setUi({ selection: result.newItemIds });
  }, [store, apply, isLocked, pasteTrackId, t]);

  const duplicate = useCallback(() => {
    const s = store.getState();
    const result = buildDuplicateOps(s.doc, s.ui.selection, newGuid, isLocked);
    if (!result.ok) return showRefusalMessage(result.reason);
    if (apply(result.ops, t('duplicateLabel'))) store.setUi({ selection: result.newItemIds });
  }, [store, apply, isLocked, t]);

  // ── single-purpose clip actions ────────────────────────────────────────────
  const run = useCallback(
    (result: { ok: true; ops: EditOp[] } | { ok: false; reason: string }, label: string) => {
      if (!result.ok) return showRefusalMessage(result.reason);
      apply(result.ops, label);
    },
    [apply],
  );
  const moveToTrack = useCallback(
    (direction: -1 | 1) => {
      const s = store.getState();
      run(buildMoveToTrackOps(s.doc, s.ui.selection, direction, isLocked), direction < 0 ? t('moveUpLabel') : t('moveDownLabel'));
    },
    [store, run, isLocked, t],
  );
  const join = useCallback(() => {
    const s = store.getState();
    if (s.ui.selection.length !== 1) return showRefusalMessage(t('joinSelectOne'));
    run(joinAvailability(s.doc, s.ui.selection[0]), t('joinLabel'));
  }, [store, run, t]);
  const fitToFill = useCallback(() => {
    const s = store.getState();
    if (s.ui.selection.length !== 1) return showRefusalMessage(t('fitSelectOne'));
    const plan = fitToFillAvailability(s.doc, s.ui.selection[0]);
    if (!plan.ok) return showRefusalMessage(plan.reason);
    if (apply(plan.ops, t('fitLabel'))) notifications.show({ color: 'blue', title: t('fitTitle'), message: fitToFillConfirmLine(plan) });
  }, [store, run, apply, t]);
  const toggleEnabled = useCallback(() => {
    const s = store.getState();
    const result = buildToggleEnabledOps(s.doc, s.ui.selection);
    if (!result.ok) return showRefusalMessage(result.reason);
    apply(result.ops, result.enabling ? t('enableClip') : t('disableClip'));
  }, [store, apply, t]);
  const selectScope = useCallback(
    (scope: 'forward' | 'backward', allTracks: boolean) => {
      const s = store.getState();
      const trackId = allTracks ? null : (pasteTrackId() ?? null);
      store.setUi({ selection: selectFromPlayhead(s.doc, s.ui.playheadFrame, scope, trackId) });
    },
    [store, pasteTrackId],
  );
  const stepPlayhead = useCallback(
    (delta: number) => {
      const s = store.getState();
      store.setUi({ playheadFrame: Math.max(0, s.ui.playheadFrame + delta), playbackSpeed: 0 });
    },
    [store],
  );

  // ── numeric trim (inspector) ───────────────────────────────────────────────
  const sourceFramesOf = useCallback(
    (item: MediaClip): number | null => {
      const ref = mediaRefOf(item.assetRef);
      const source = ref ? pool?.find((p) => p.mediaRef === ref) : undefined;
      const seconds = source?.probe?.durationSec;
      if (!source || typeof seconds !== 'number' || !(seconds > 0) || source.kind === 'image') return null;
      const rate = store.getState().doc.settings.rate;
      return Math.floor((seconds * rate.num) / Math.max(1, rate.den));
    },
    [pool, store],
  );
  /** Returns a plain sentence when the entry was refused, else null (and the document is changed). */
  const applyNumericTrim = useCallback(
    (itemId: string, field: NumericField, value: number): string | null => {
      if (readOnly) return t('readOnlyMessage');
      const d = store.getState().doc;
      const item = d.tracks.flatMap((t) => t.items).find((i) => i.id === itemId);
      const result = buildNumericTrimOps(d, itemId, field, value, item?.type === 'mediaClip' ? sourceFramesOf(item as MediaClip) : null);
      if (!result.ok) return result.reason;
      let ops = result.ops;
      // R08.1: a typed in / out / duration reaches the linked partners as the same trim, as one undo step.
      const only = ops.length === 1 ? ops[0] : null;
      if (only && only.type === 'trim' && !store.getState().ui.editSeparately && isLinked(d, itemId)) {
        const linked = linkedOpsForTrim(d, itemId, only.edge, only.mode, only.deltaFrames, { isLocked });
        if (linked.length === 0) return t('linkedCannotFollow');
        ops = linked;
      }
      try {
        store.dispatchBatch(ops, result.label);
        return null;
      } catch (error) {
        // eslint-disable-next-line no-restricted-syntax -- a local store refusal sentence, not an API error
        return error instanceof Error ? error.message : t('notPossible');
      }
    },
    [store, readOnly, sourceFramesOf, isLocked, t],
  );

  /** R07.5: split at the playhead and hold that frame for two seconds (one undo step, silent). */
  const freezeFrame = useCallback(() => {
    const s = store.getState();
    const target = s.ui.selection.length === 1 ? s.ui.selection[0] : null;
    const locked = (trackId: string) => {
      const tr = s.doc.tracks.find((x) => x.id === trackId);
      return tr ? store.effectiveTrackFlags(tr).locked : false;
    };
    const why = freezeBlocker(s.doc, target, s.ui.playheadFrame, locked);
    if (why || !target) {
      notifications.show({ color: 'yellow', title: t('freezeBlockedTitle'), message: why ?? t('selectVideoFirst') });
      return;
    }
    const plan = freezeFrameOps(s.doc, target, s.ui.playheadFrame, newGuid, locked);
    if (plan && apply(plan.ops, t('freezeLabel'))) store.setUi({ selection: [plan.holdId] });
  }, [store, apply, t]);

  // ── the one action list (menu, "..." button) ───────────────────────────────
  const items = useCallback(
    (): ClipMenuItem[] => {
      const s = store.getState();
      const sel = s.ui.selection;
      const none = sel.length === 0 ? t('selectClipFirst') : undefined;
      const one = sel.length === 1 ? undefined : t('selectOneClip');
      const playhead = s.ui.playheadFrame;
      const canSplit = sel.some((id) => {
        const item = s.doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === id);
        return item ? item.recordIn < playhead && playhead < item.recordOut : false;
      });
      const hasClipboard = readClipClipboard() !== null;
      const locked = readOnly ? t('readOnlyMessage') : undefined;
      const track = s.doc.tracks.find((x) => x.id === pasteTrackId());
      const only = sel.length === 1 ? sel[0] : null;
      const joinReason = only === null ? undefined : (() => { const r = joinAvailability(s.doc, only); return r.ok ? undefined : r.reason; })();
      const fitReason = only === null ? undefined : (() => { const r = fitToFillAvailability(s.doc, only); return r.ok ? undefined : r.reason; })();
      const replaceReason = only === null ? undefined : (() => { const r = replaceAvailability(s.doc, only); return r.ok ? undefined : r.reason; })();
      const gapsHere = track ? interiorGapCount(s.doc, track.id) : 0;
      // §7.2: Add mask / Add reframe belong to exactly one media clip, never a multi-selection or another kind.
      const maskClip = (): MediaClip | null => {
        if (sel.length !== 1) return null;
        const found = s.doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === sel[0]);
        return found && found.type === 'mediaClip' ? (found as MediaClip) : null;
      };
      const maskWhy = locked ?? (sel.length === 0 ? t('selectClipFirst') : sel.length > 1 ? t('selectOneClip') : maskClip() ? undefined : t('maskNeedsMediaClip'));
      const addMask = (kind: 'rect' | 'ellipse') => {
        const clip = maskClip();
        if (!clip) return;
        const label = kind === 'ellipse' ? t('items.addEllipseMask') : t('items.addMask');
        if (!apply([{ type: 'setProperty', itemId: clip.id, key: 'mask', value: { ...DEFAULT_SHAPE_MASK, kind } }], label)) return;
        store.setUi({ selection: [clip.id] });
        enterViewerTool(store, 'mask');
        openInspector(false);
      };
      const addReframe = () => {
        const clip = maskClip();
        if (!clip) return;
        if (!apply([{ type: 'setProperty', itemId: clip.id, key: 'reframe', value: { ...DEFAULT_REFRAME_POINT } }], t('items.addReframe'))) return;
        store.setUi({ selection: [clip.id] });
        enterViewerTool(store, 'reframe');
        openInspector(false);
      };
      // Every adjustment the one selected clip carries, with open / turn off / remove (the same actions as its badges).
      const only1 = sel.length === 1 ? s.doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === sel[0]) : undefined;
      const adjustments: ClipMenuItem[] = only1 ? adjustmentsMenuItems(only1, s.doc, { t: tBadges, readOnly, open: (id, kind) => revealAdjustment?.(id, kind), apply: (ops, label) => void apply(ops, label) }) : [];
      return recordFirstItems(store, [
        { id: 'split', label: t('items.split'), action: 'bladeAtPlayhead', unavailable: locked ?? none ?? (canSplit ? undefined : t('splitReason')), run: split },
        { id: 'freeze-frame', label: t('items.freezeFrame', { seconds: 2 }), unavailable: locked ?? freezeBlocker(s.doc, sel.length === 1 ? sel[0] : null, playhead) ?? undefined, run: freezeFrame },
        { id: 'delete', label: t('items.delete'), action: 'deleteSelection', unavailable: locked ?? (none && !liveGap ? none : undefined), danger: true, run: deleteSelection },
        { id: 'lift', label: t('items.lift'), action: 'liftSelection', unavailable: locked ?? none, run: liftSelection },
        { id: 'copy', label: t('items.copy'), action: 'copyClips', unavailable: none, dividerBefore: true, run: () => void copy() },
        { id: 'cut', label: t('items.cut'), action: 'cutClips', unavailable: locked ?? none, run: cut },
        { id: 'paste', label: t('items.paste'), action: 'pasteClips', unavailable: locked ?? (hasClipboard ? undefined : t('copyFirst')), run: paste },
        { id: 'duplicate', label: t('items.duplicate'), action: 'duplicateClips', unavailable: locked ?? none, run: duplicate },
        { id: 'fit-to-fill', sub: 'arrange', label: t('items.fitToFill'), unavailable: locked ?? none ?? one ?? fitReason, dividerBefore: true, run: fitToFill },
        { id: 'replace', sub: 'arrange', label: t('items.replace'), unavailable: locked ?? none ?? one ?? replaceReason, run: () => openReplace(sel[0]) },
        { id: 'join', sub: 'arrange', label: t('items.join'), unavailable: locked ?? none ?? one ?? joinReason, run: join },
        { id: 'move-up', sub: 'arrange', label: t('items.moveUp'), action: 'moveTrackUp', unavailable: locked ?? none, run: () => moveToTrack(-1) },
        { id: 'move-down', sub: 'arrange', label: t('items.moveDown'), action: 'moveTrackDown', unavailable: locked ?? none, run: () => moveToTrack(1) },
        { id: 'speed', sub: 'arrange', label: t('items.speed'), unavailable: none ?? one, run: () => openInspector(true) },
        { id: 'toggle-enabled', sub: 'arrange', label: t('items.toggleEnabled'), unavailable: locked ?? none, run: toggleEnabled },
        { id: 'link', sub: 'link', label: t('items.link'), action: 'linkClips', unavailable: locked ?? none ?? (linkOps(s.doc, sel).length > 0 ? undefined : t('linkReason')), dividerBefore: true, run: linkSelected },
        { id: 'unlink', sub: 'link', label: t('items.unlink'), action: 'unlinkClips', unavailable: locked ?? none ?? (sel.some((id) => isLinked(s.doc, id)) ? undefined : t('notLinked')), run: unlinkSelected },
        { id: 'edit-separately', sub: 'link', label: s.ui.editSeparately ? t('items.editTogether') : t('items.editSeparately'), run: toggleSeparately },
        { id: 'select-forward', sub: 'select', label: t('items.selectForward'), action: 'selectForward', dividerBefore: true, run: () => selectScope('forward', false) },
        { id: 'select-backward', sub: 'select', label: t('items.selectBackward'), action: 'selectBackward', run: () => selectScope('backward', false) },
        { id: 'select-forward-all', sub: 'select', label: t('items.selectForwardAll'), run: () => selectScope('forward', true) },
        { id: 'select-backward-all', sub: 'select', label: t('items.selectBackwardAll'), run: () => selectScope('backward', true) },
        { id: 'close-gaps', sub: 'select', label: track ? t('items.closeGapsOn', { track: track.name }) : t('items.closeGaps'), unavailable: locked ?? (gapsHere > 0 ? undefined : t('noGapsHere')), run: () => track && closeAllGaps(track.id) },
        { id: 'add-mask', sub: 'picture', label: t('items.addMask'), unavailable: maskWhy, dividerBefore: true, run: () => addMask('rect') },
        { id: 'add-ellipse-mask', sub: 'picture', label: t('items.addEllipseMask'), unavailable: maskWhy, run: () => addMask('ellipse') },
        { id: 'add-reframe', sub: 'picture', label: t('items.addReframe'), unavailable: maskWhy, run: addReframe },
        { id: 'blur-object', sub: 'picture', label: t('items.blurObject'), unavailable: maskWhy, run: () => { openInspector(false); window.setTimeout(() => { const el = document.getElementById('object-effects-section'); el?.scrollIntoView?.({ block: 'center' }); el?.querySelector<HTMLElement>('input')?.focus(); }, 80); } },
        { id: 'properties', label: t('items.properties'), unavailable: none, dividerBefore: true, run: () => openInspector(false) },
        ...adjustments,
      ]);
    },
    [store, readOnly, linkSelected, unlinkSelected, toggleSeparately, split, freezeFrame, deleteSelection, liftSelection, copy, cut, paste, duplicate, fitToFill, join, moveToTrack, toggleEnabled, selectScope, closeAllGaps, openInspector, openReplace, pasteTrackId, liveGap, apply, t, tBadges, revealAdjustment],
  );

  /** T-018: the menu for empty lane space, a gap and the ruler: what can be done at the frame under the pointer. */
  const pointItems = useCallback(
    (): ClipMenuItem[] => {
      const s = store.getState();
      const frame = menuTarget.frame;
      const locked = readOnly ? t('readOnlyMessage') : undefined;
      const at = (action: () => void) => () => {
        store.setUi({ playheadFrame: frame });
        action();
      };
      const out: ClipMenuItem[] = [];
      const place = getPlace?.() ?? null;
      if (menuTarget.area === 'lane') {
        out.push({ id: 'point-paste', label: t('points.pasteHere'), action: 'pasteClips', unavailable: locked ?? (readClipClipboard() !== null ? undefined : t('copyFirst')), run: () => {
          // A paste that placed nothing leaves the playhead where it was.
          const before = store.getState();
          store.setUi({ playheadFrame: frame });
          paste();
          if (store.getState().doc === before.doc) store.setUi({ playheadFrame: before.ui.playheadFrame });
        } });
        if (liveGap) {
          out.push({ id: 'point-close-gap', label: t('points.closeThisGap'), unavailable: locked, run: () => { if (apply(buildDeleteGapOps(liveGap, store.getState().doc, { isLocked }), t('closeGap'))) setSelectedGapState(null); } });
        }
      }
      const marked = s.ui.inOut.inFrame !== null || s.ui.inOut.outFrame !== null;
      out.push({ id: 'point-mark-in', label: t('points.markInHere'), dividerBefore: out.length > 0, run: () => setMarks?.({ ...store.getState().ui.inOut, inFrame: frame }) });
      out.push({ id: 'point-mark-out', label: t('points.markOutHere'), run: () => setMarks?.({ ...store.getState().ui.inOut, outFrame: frame + 1 }) });
      if (marked) out.push({ id: 'point-clear-marks', label: t('points.clearMarks'), action: 'clearInOut', run: () => setMarks?.({ inFrame: null, outFrame: null }) });
      out.push({ id: 'point-add-marker', label: t('points.addMarkerHere'), unavailable: locked, run: () => addMarkerAt?.(frame) });
      if (place) {
        const ACTION_OF = { insert: 'placeInsert', overwrite: 'placeOverwrite', append: 'placeAppend', placeOnTop: 'placeOnTop' } as const;
        PLACE_MODES.forEach(({ mode }, i) => out.push({ id: `point-${mode}`, label: tp.msg(placeChoiceLabelMsg(mode)), action: ACTION_OF[mode], dividerBefore: i === 0, unavailable: locked, run: at(() => place(mode)) }));
      }
      return recordFirstItems(store, out);
    },
    [store, menuTarget, readOnly, liveGap, paste, apply, isLocked, setMarks, addMarkerAt, getPlace, tp, t],
  );

  return useMemo(
    () => ({
      selectedGap: liveGap,
      setSelectedGap,
      anchor,
      openMenu: (x: number, y: number, target?: MenuTarget) => {
        setMenuTarget(target ?? CLIP_TARGET);
        setAnchor({ x, y });
      },
      menuTarget,
      pointItems,
      closeMenu: () => setAnchor(null),
      items,
      freezeFrame,
      deleteSelection,
      liftSelection,
      closeAllGaps,
      copy,
      cut,
      paste,
      duplicate,
      moveToTrack,
      linkSelected,
      unlinkSelected,
      toggleSeparately,
      stepPlayhead,
      applyNumericTrim,
      apply,
    }),
    [liveGap, setSelectedGap, anchor, menuTarget, pointItems, items, freezeFrame, deleteSelection, liftSelection, closeAllGaps, copy, cut, paste, duplicate, moveToTrack, linkSelected, unlinkSelected, toggleSeparately, stepPlayhead, applyNumericTrim, apply],
  );
}
