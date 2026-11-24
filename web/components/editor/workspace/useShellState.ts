import { useCallback, useState } from 'react';
import type { PlaceChoice } from '../place/placeChoices';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { usePhoneActions } from '../phone/usePhoneActions';
import { type OfflineHint } from '../interchange/OfflineRelink';
import { type ImportSheetModel, peekImportResult } from '@/lib/editing/interchange/handoff-ui';
import type { WorkspacePanelsCtx } from './useWorkspacePanels';

/**
 * State that belongs to the shell rather than to a domain: the frame zones the Media and Deliver pages render into,
 * the import result sheet, and the phone's actions.
 */
export function useShellState(ctx: WorkspacePanelsCtx) {
  const { store, placeChosen, poolMarksRef, timelineId, doc, ui, readOnly, editing, bladeAtPlayhead, nudgeSelection, extractAudio, undo, addMarker, setMarks, selectMode, setSelectMode, sourceSizeFor, openSounds } = ctx;
  // The pool's Place menu puts the source (with the source viewer's marks) on the timeline.
  const placePoolSource = useCallback(
    (item: MediaPoolItem, mode: PlaceChoice) => placeChosen(item, mode, poolMarksRef.current.get(item.mediaRef) ?? null),
    [placeChosen, poolMarksRef],
  );

  // T-003: the Media page's frame targets. The pool renders its bins and viewer+metadata panes into
  // these zones (portals), so the one pool component keeps its state and the frame owns the widths.
  const [mediaBinsTarget, setMediaBinsTarget] = useState<HTMLDivElement | null>(null);
  const [mediaDetailTarget, setMediaDetailTarget] = useState<HTMLDivElement | null>(null);
  const [mediaTimelineOpen, setMediaTimelineOpen] = useState(false);

  // T-003: Deliver in the shared frame — settings 420 | result (rest), no centre, no timeline.
  const [deliverControlsTarget, setDeliverControlsTarget] = useState<HTMLDivElement | null>(null);
  const [deliverResultTarget, setDeliverResultTarget] = useState<HTMLDivElement | null>(null);
  // R12.3: the import result sheet follows the import across the page change (taken once, then gone).
  const [importResult, setImportResult] = useState<ImportSheetModel | null>(() => peekImportResult(timelineId));
  const [relinkRequest, setRelinkRequest] = useState<string | null>(null);
  const [importHints] = useState<Record<string, OfflineHint>>(() =>
    Object.fromEntries((importResult?.offline ?? []).map((o) => [o.name.toLowerCase(), { reasonWords: o.reasonWords, candidates: o.candidates }])),
  );

  const phoneActions = usePhoneActions({
    store,
    doc,
    ui,
    readOnly,
    editing,
    split: bladeAtPlayhead,
    nudge: nudgeSelection,
    extractAudio,
    undo,
    addMarker: () => addMarker('Marker'),
    setInOut: setMarks,
    selectMode,
    setSelectMode,
    sourceSizeFor,
    openSounds,
  });

  return { placePoolSource, mediaBinsTarget, setMediaBinsTarget, mediaDetailTarget, setMediaDetailTarget, mediaTimelineOpen, setMediaTimelineOpen, deliverControlsTarget, setDeliverControlsTarget, deliverResultTarget, setDeliverResultTarget, importResult, setImportResult, relinkRequest, setRelinkRequest, importHints, phoneActions };
}

export type ShellStateCtx = WorkspacePanelsCtx & ReturnType<typeof useShellState>;
