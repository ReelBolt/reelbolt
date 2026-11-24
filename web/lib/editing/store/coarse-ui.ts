/**
 * A view of the UI state for the editor shell that does not move with the playhead while playing.
 *
 * While the transport plays, `ui.playheadFrame` changes on every presented frame (30-60 times a second).
 * The shell (`EditorWorkspace`) reads the whole `ui` object, so each tick re-rendered the whole workspace
 * (Mantine boxes, buttons, tooltips, every panel; profiled at roughly a third of the main thread, 2-3 commits
 * per frame) to move a playhead that only a few small leaves paint per frame. Even one shell render a second
 * is a 60-85 ms main-thread block (3-5 missed vsyncs, a dropped frame a second in the perf spec), so the shell
 * reads `coarseUi`: identical to `ui`, except that WHILE PLAYING `playheadFrame` stays at the frame playback
 * started from. Paused (or stopped) it is exact again, so every seek, step and edit sees the real frame.
 *
 * Anything that shows the playhead while playing reads it through `LivePlayhead` / `withLivePlayhead`
 * (`components/editor/shell/LivePlayhead.tsx`), which re-renders only that leaf; event handlers read
 * `store.getState().ui.playheadFrame` at call time. Nothing here changes what the store holds.
 */

import type { EditorState, UiState } from './editor-store';

function sameExceptPlayhead(a: UiState, b: UiState): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]) as Set<keyof UiState>;
  for (const key of keys) {
    if (key === 'playheadFrame') continue;
    if (!Object.is(a[key], b[key])) return false;
  }
  return true;
}

/** A selector for `useEditorStore` that returns a referentially stable `ui` (it keeps its own cache). */
export function createCoarseUiSelector(): (state: EditorState) => UiState {
  let lastUi: UiState | null = null;
  let lastOut: UiState | null = null;
  let frozenAt: number | null = null;
  return (state) => {
    const ui = state.ui;
    if (ui === lastUi && lastOut) return lastOut;
    lastUi = ui;
    if (ui.playbackSpeed === 0) frozenAt = null;
    else if (frozenAt === null) frozenAt = ui.playheadFrame;
    const frame = frozenAt ?? ui.playheadFrame;
    if (lastOut && lastOut.playheadFrame === frame && sameExceptPlayhead(ui, lastOut)) return lastOut;
    lastOut = frame === ui.playheadFrame ? ui : { ...ui, playheadFrame: frame };
    return lastOut;
  };
}

/** The playhead for a leaf that does not need every frame: exact when paused, rounded down to `quantumSec` while playing. */
export function leafPlayheadFrame(state: EditorState, quantumSec: number): number {
  const { playheadFrame, playbackSpeed } = state.ui;
  if (playbackSpeed === 0 || quantumSec <= 0) return playheadFrame;
  const { num, den } = state.doc.settings.rate;
  const fps = den > 0 && num > 0 ? num / den : 30;
  // A quantum below one frame is one frame (no rounding); otherwise round to whole frames.
  const quantum = Math.max(1, Math.round(fps * quantumSec));
  return Math.floor(playheadFrame / quantum) * quantum;
}
