import { useMemo, useCallback, useEffect, useRef } from 'react';
import { newGuid } from '@/lib/editing/ids';
import { useSourceMonitor } from '../viewer/SourceMonitor';
import { eventToChord, shortcutBlocked, isHelpKey, type KeyAction } from '@/lib/editing/keymap';
import { VIEWER_SWITCH_PAGES, type CommandContext, commandUnavailable, PLACE_ACTIONS, runCommand } from '@/lib/editing/commands';
import { type ComposeCommandEnv, COMPOSE_ACTIONS } from '@/lib/editing/compose/commands';
import { DEFAULT_ADJUSTMENT_NAME, DEFAULT_ADJUSTMENTS_TRACK_NAME } from '../shell/default-names';
import { readClipClipboard } from '@/lib/editing/timeline-edit';
import { useT } from '@/i18n';
import { selectedClipMenuAnchor } from './helpers';
import type { ShellChoiceCtx } from './useShellChoice';

/** The Source monitor, the command context every shortcut and the palette run with, and the one keydown listener. */
export function useCommands(ctx: ShellChoiceCtx) {
  const { store, isPhone, ui, pool, readOnly, endFrame, undo, redo, addMarker, liftSelection, extractSelection, nudgeSelection, bladeAtPlayhead, playInToOut, setMarks, editing, exitFullscreen, setWorkspace, setCheatsheetOpen, setPaletteOpen, toggleFullscreen, eventsLane, placeRef, keymap, simple, setSimplePlace, openFullForAction, pendingFullActionRef } = ctx;
  const tx = useT('editor.editorWorkspace');
  // ── the Source monitor (Cut and Edit): the viewer's second program ─────────────────────────────
  // View state only: `viewerMode` / `viewerSource` live in the store's UI half, never in the document.
  // The Simple shell has no Source monitor: one viewer, always the timeline.
  const viewerSwitchOn = !isPhone && !simple && VIEWER_SWITCH_PAGES.has(ui.workspace);
  const sourceShowing = viewerSwitchOn && ui.viewerMode === 'source';
  const sourceItem = useMemo(() => pool?.find((p) => p.mediaRef === ui.viewerSource) ?? null, [pool, ui.viewerSource]);
  const monitor = useSourceMonitor(store.getState().projectId, sourceItem, sourceShowing);
  const openSource = useCallback(
    (mediaRef: string) => {
      if (!viewerSwitchOn) return;
      // The timeline stops first: while Source shows, the preview session does no work.
      store.setUi({ viewerSource: mediaRef, viewerMode: 'source', playbackSpeed: 0 });
    },
    [store, viewerSwitchOn],
  );
  // Entering Source stops the timeline's playback; a page without the switch (or the phone) returns to Record;
  // a picture tool (mask, reframe, pin, window, transform) belongs to the timeline's picture, so it returns to Record too.
  useEffect(() => {
    if (sourceShowing && store.getState().ui.playbackSpeed !== 0) store.setUi({ playbackSpeed: 0 });
  }, [sourceShowing, store]);
  useEffect(() => {
    if (ui.viewerMode === 'source' && (!viewerSwitchOn || ui.viewerTool !== 'select')) store.setUi({ viewerMode: 'record' });
  }, [ui.viewerMode, ui.viewerTool, viewerSwitchOn, store]);

  // T-014: what the Compose actions need that only the workspace knows (the undo-step names are translated here).
  const composeEnv = useMemo<ComposeCommandEnv>(
    () => ({
      readOnly,
      phone: isPhone,
      newId: newGuid,
      labels: {
        maskShape: tx('composeMaskShape'),
        maskInvert: tx('composeMaskInvert'),
        removeMask: tx('composeRemoveMask'),
        removeReframe: tx('composeRemoveReframe'),
        removeKeyer: tx('composeRemoveKeyer'),
        removePin: tx('composeRemovePin'),
        addAdjustment: tx('composeAddAdjustment'),
      },
      adjustmentNames: { clip: DEFAULT_ADJUSTMENT_NAME, track: DEFAULT_ADJUSTMENTS_TRACK_NAME },
    }),
    [readOnly, isPhone, tx],
  );

  // T-012: the "Place over" chord runs whatever the placement code below registers (it is declared after this context).
  const placeOverHandlerRef = useRef<() => void>(() => undefined);
  // T-007: one command context; the keydown listener and the palette both run `runCommand` with it.
  const commandContext = useCallback(
    (event?: KeyboardEvent): CommandContext => ({
      store,
      endFrame,
      undo,
      redo,
      addMarker,
      liftSelection,
      extractSelection,
      nudgeSelection,
      bladeAtPlayhead,
      playInToOut,
      setMarks,
      editing,
      // A page switch or a dialog lives outside the viewer: full screen is left first, or it would open unseen.
      setWorkspace: (workspace) => {
        exitFullscreen();
        setWorkspace(workspace);
      },
      openCheatsheet: () => {
        exitFullscreen();
        setCheatsheetOpen(true);
      },
      openPalette: () => {
        exitFullscreen();
        setPaletteOpen(true);
      },
      toggleFullscreen,
      placeOver: () => placeOverHandlerRef.current(),
      events: { toggleLane: eventsLane.toggleLane, toggleSnap: eventsLane.toggleSnap, actOnNearest: eventsLane.actOnNearest },
      compose: composeEnv,
      source: sourceShowing ? monitor.transport : undefined,
      place: viewerSwitchOn && sourceItem ? (mode) => placeRef.current?.(mode) : undefined,
      simple: simple ? { setPlace: setSimplePlace, openFull: openFullForAction } : undefined,
      event,
    }),
    [store, composeEnv, sourceShowing, viewerSwitchOn, sourceItem, monitor.transport, eventsLane.toggleLane, eventsLane.toggleSnap, eventsLane.actOnNearest, endFrame, undo, redo, addMarker, liftSelection, extractSelection, nudgeSelection, bladeAtPlayhead, playInToOut, setMarks, editing, setWorkspace, exitFullscreen, toggleFullscreen, setCheatsheetOpen, setPaletteOpen, placeRef, simple, setSimplePlace, openFullForAction],
  );

  // An action the Simple shell handed to the full editor runs here, once the full editor is what shows.
  useEffect(() => {
    if (simple) return;
    const id = pendingFullActionRef.current;
    if (!id) return;
    pendingFullActionRef.current = null;
    runCommand(id, commandContext());
  }, [simple, commandContext, pendingFullActionRef]);

  // T-007: one keydown listener over the one command catalogue (the palette calls the same `runCommand`).
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      // W0.6 (UI2-09): never behind or inside a dialog, on a slider/menu/text field, or Space/Enter on a button.
      const chord = eventToChord(event);
      if (shortcutBlocked(event.target, chord)) return;
      // C14: "?" opens the read-only cheatsheet, as on most web apps.
      if (isHelpKey(event)) {
        event.preventDefault();
        setCheatsheetOpen(true);
        return;
      }
      // T-018: the keyboard's context-menu key (or Shift+F10, the same request) opens the clip menu on the selected clip.
      // A saved user binding on Shift+F10 wins over the built-in request.
      if (event.key === 'ContextMenu' || (chord === 'Shift+F10' && !keymap.has(chord))) {
        const where = selectedClipMenuAnchor(store);
        if (where && !isPhone) {
          event.preventDefault();
          editing.openMenu(where.x, where.y);
        }
        return;
      }
      const action = keymap.get(chord);
      if (!action) return;
      // The Source | Record switch exists on Cut and Edit only; elsewhere (and on the phone) its chord does nothing.
      if (action === 'toggleViewerMode' && !viewerSwitchOn) return;
      // T-014: a Compose chord that cannot run changes nothing (the palette row says why), and the key keeps its default.
      if (COMPOSE_ACTIONS.has(action) && commandUnavailable(action, { store, canUndo: false, canRedo: false, hasClipboard: false, readOnly, phone: isPhone }) !== null) return;
      if (COMPOSE_ACTIONS.has(action)) event.preventDefault();
      // T-018: a placement chord with no source loaded (or on the phone, or read-only) changes nothing and keeps the key's default.
      if (action in PLACE_ACTIONS && commandUnavailable(action, { store, canUndo: false, canRedo: false, hasClipboard: false, readOnly, phone: isPhone, hasSource: viewerSwitchOn && sourceItem !== null }) !== null) return;
      // Holding a placement chord (or the Source | Record chord) places, or flips, once: key auto-repeat does nothing.
      if (event.repeat && (action in PLACE_ACTIONS || action === 'placeOver' || action === 'toggleViewerMode')) {
        event.preventDefault();
        return;
      }
      runCommand(action, commandContext(event));
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [keymap, commandContext, store, readOnly, isPhone, viewerSwitchOn, sourceItem, editing, setCheatsheetOpen]);

  // T-007: the palette reads availability live from the store and runs through the same `runCommand`.
  const paletteUnavailable = useCallback(
    (id: KeyAction) => commandUnavailable(id, { store, canUndo: store.canUndo(), canRedo: store.canRedo(), hasClipboard: readClipClipboard() !== null, readOnly, phone: isPhone, hasSource: viewerSwitchOn && sourceItem !== null, simple }),
    [store, readOnly, isPhone, viewerSwitchOn, sourceItem, simple],
  );
  const runPaletteCommand = useCallback(
    (id: KeyAction) => {
      setPaletteOpen(false);
      runCommand(id, commandContext());
    },
    [commandContext, setPaletteOpen],
  );

  return { viewerSwitchOn, sourceShowing, sourceItem, monitor, openSource, placeOverHandlerRef, paletteUnavailable, runPaletteCommand };
}

export type CommandsCtx = ShellChoiceCtx & ReturnType<typeof useCommands>;
