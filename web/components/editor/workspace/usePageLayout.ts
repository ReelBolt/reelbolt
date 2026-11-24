import { useRef, useState, useCallback, useEffect } from 'react';
import { type WorkspaceId } from '@/lib/editing/store';
import { workspaceToPage, changeEditorPage } from '@/lib/editing/page-url';
import { resetComposeToolOnPageChange } from '@/lib/editing/compose/tool-lifecycle';
import { defaultPagePixels, migratePoolPanes } from '@/lib/editing/layout/editor-layout';
import { type SavedLayout } from '@/lib/editing/api/editor-p9';
import { savePreferencesSafely, restorePreviousPreferences } from '@/lib/editing/prefs/safe-save';
import type { Sizes as PanelSizes } from '../shell/PanelLayout';
import { useT } from '@/i18n';
import type { KeymapChordsCtx } from './useKeymapChords';

/** Panel sizes, saved layouts and preferences, and switching pages. */
export function usePageLayout(ctx: KeymapChordsCtx) {
  const { store, preferences, keymapOverrides, mutatePreferences, ui, openSoundsRef } = ctx;
  const tx = useT('editor.editorWorkspace');
  // The one geometry table is the starting point (Edit's 300/320/320), not a second set of numbers:
  // this is what "Save current" reads before the frame reports its first measured sizes on mount.
  const panelSizes = useRef<PanelSizes>(defaultPagePixels('edit'));
  const [appliedSizes, setAppliedSizes] = useState<PanelSizes | null>(null);
  const panelBounds = useRef({ width: 0, height: 0 });
  const [appliedFractions, setAppliedFractions] = useState<PanelSizes | null>(null);
  const [layoutReset, setLayoutReset] = useState(0);
  // T-003: the legacy pool-panes key is read ONCE at editor boot, mapped into the Media page's
  // fractions and deleted. The pool itself never reads it again (MediaPool keeps its panes in
  // memory for the hub/Edit tab; the Media page's widths are the frame's fractions).
  useState(() => {
    if (typeof window === 'undefined') return null;
    migratePoolPanes(window.localStorage, { width: window.innerWidth, height: window.innerHeight });
    return null;
  });
  const onPanelSizes = useCallback((sizes: PanelSizes, bounds: { width: number; height: number }) => {
    panelSizes.current = sizes;
    panelBounds.current = bounds;
  }, []);
  const savePreferences = useCallback(
    async (patch: { keymap?: Record<string, string>; layouts?: SavedLayout[] }) => {
      // W0.6 (UI2-08): a PUT replaces both halves, so saving before the stored preferences loaded
      // would wipe the half this save does not carry. Failures are thrown to the caller, which keeps
      // its dialog open with the reason.
      if (!preferences) throw new Error(tx('yourSavedPreferencesHave'));
      const next = { keymap: patch.keymap ?? keymapOverrides, layouts: patch.layouts ?? preferences.layouts ?? [] };
      const saved = await savePreferencesSafely(preferences, next);
      await mutatePreferences(saved, { revalidate: false });
    },
    [keymapOverrides, preferences, mutatePreferences, tx],
  );
  const restorePreferences = useCallback(async () => {
    const restored = await restorePreviousPreferences();
    if (!restored) throw new Error(tx('thereAreNoEarlier'));
    await mutatePreferences(restored, { revalidate: false });
  }, [mutatePreferences, tx]);

  // T-001: a page change moves UI state and pushes one history entry. The URL write is deliberate:
  // the timeline page does not read `page` for data, so a router navigation here would only cost an
  // RSC request and a remount. Back/forward is read back into the store by the popstate listener.
  // A compose picture tool (mask, reframe, pin) never survives a page change; one subscription, one rule.
  useEffect(() => resetComposeToolOnPageChange(store), [store]);
  const editorPage = workspaceToPage(ui.workspace);
  const setWorkspace = useCallback(
    (workspace: WorkspaceId) =>
      changeEditorPage(store, workspaceToPage(workspace), {
        history: window.history,
        href: window.location.href,
        storage: window.localStorage,
      }),
    [store],
  );
  // "Add sound here": the playhead is already on the event; the Media page is where a sound is picked and placed.
  useEffect(() => {
    openSoundsRef.current = (frame: number) => {
      store.setUi({ playheadFrame: frame, playbackSpeed: 0 });
      setWorkspace('media');
    };
  }, [store, setWorkspace, openSoundsRef]);

  return { panelSizes, appliedSizes, setAppliedSizes, panelBounds, appliedFractions, setAppliedFractions, layoutReset, setLayoutReset, onPanelSizes, savePreferences, restorePreferences, editorPage, setWorkspace };
}

export type PageLayoutCtx = KeymapChordsCtx & ReturnType<typeof usePageLayout>;
