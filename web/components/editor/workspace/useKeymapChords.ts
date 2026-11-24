import { useCallback, useMemo, useState } from 'react';
import { type WorkspaceId } from '@/lib/editing/store';
import { linkedOpsForNudge } from '@/lib/editing/link';
import { sanitizeOverrides, buildKeymap, liveChords, type KeyAction, displayChord, ariaChord } from '@/lib/editing/keymap';
import { useEditorPreferences } from '@/lib/hooks/use-editor-p9';
import { IS_MAC } from './helpers';
import type { TimelineEditsCtx } from './useTimelineEdits';

/** The user's keymap and the chords shown in the interface. */
export function useKeymapChords(ctx: TimelineEditsCtx) {
  const { store, readOnly, linkOptions, showRefusal } = ctx;
  // ── keyboard ──────────────────────────────────────────────────────────────
  const nudgeSelection = useCallback(
    (deltaFrames: number) => {
      const s = store.getState();
      if (s.ui.selection.length === 0 || readOnly) return;
      // R08.1: the selection already holds linked partners; linked ops move each once, direction of travel first.
      const ops = s.ui.editSeparately
        ? s.ui.selection.map((id) => ({ type: 'nudge' as const, itemId: id, deltaFrames })).sort((x, y) => {
            const ri = (id: string) => s.doc.tracks.flatMap((t) => t.items).find((i) => i.id === id)?.recordIn ?? 0;
            return deltaFrames > 0 ? ri(y.itemId) - ri(x.itemId) : ri(x.itemId) - ri(y.itemId);
          })
        : linkedOpsForNudge(s.doc, s.ui.selection, deltaFrames, linkOptions());
      try {
        store.dispatchBatch(ops, `Nudge ${deltaFrames > 0 ? '+' : ''}${deltaFrames}f`);
      } catch (error) {
        showRefusal(error, s.doc);
      }
    },
    [store, readOnly, linkOptions],
  );

  // P9: the keymap — NLE-standard defaults plus the user's overrides from their preferences.
  const { data: preferences, mutate: mutatePreferences } = useEditorPreferences();
  const keymapOverrides = useMemo(() => sanitizeOverrides(preferences?.keymap), [preferences]);
  const keymap = useMemo(() => buildKeymap(keymapOverrides), [keymapOverrides]);
  /** The chords that really run (a default another action's remap took is dropped): every control advertises these. */
  const runningChords = useMemo(() => liveChords(keymapOverrides), [keymapOverrides]);
  const chords = runningChords;
  /** T-007: the first live chord of every action, for the transport's tooltips and aria. */
  const firstChords = useMemo<Partial<Record<KeyAction, string>>>(
    () => Object.fromEntries(Object.entries(chords).map(([id, list]) => [id, list[0]])) as Partial<Record<KeyAction, string>>,
    [chords],
  );
  const liveChordLabel = (label: string, action: KeyAction): string => {
    const chord = chords[action]?.[0];
    return chord ? `${label} (${displayChord(chord, IS_MAC)})` : label;
  };
  const liveAria = (action: KeyAction): string | undefined => {
    const chord = chords[action]?.[0];
    return chord ? ariaChord(chord, IS_MAC) : undefined;
  };
  /** T-007: the page actions' live chords, keyed by workspace, for the switcher and its tooltips. */
  const pageChords = useMemo<Partial<Record<WorkspaceId, string>>>(
    () => ({ media: chords.pageMedia[0], cut: chords.pageCut[0], edit: chords.pageEdit[0], compose: chords.pageCompose[0], color: chords.pageColour[0], audio: chords.pageAudio[0], deliver: chords.pageDeliver[0] }),
    [chords],
  );
  const [keymapOpen, setKeymapOpen] = useState(false);
  // T-007: `?` opens the read-only cheatsheet; Mod+K opens the command palette.
  const [cheatsheetOpen, setCheatsheetOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);

  return { nudgeSelection, preferences, mutatePreferences, keymapOverrides, keymap, runningChords, chords, firstChords, liveChordLabel, liveAria, pageChords, keymapOpen, setKeymapOpen, cheatsheetOpen, setCheatsheetOpen, paletteOpen, setPaletteOpen };
}

export type KeymapChordsCtx = TimelineEditsCtx & ReturnType<typeof useKeymapChords>;
