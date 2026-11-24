/**
 * The editor store — the spine of the P3 editable timeline shell.
 *
 * It owns exactly three things and keeps them apart on purpose:
 *
 *   1. **The document** (`EditTimeline`) and the server **revision** it was loaded at. The document
 *      changes only through {@link EditorStore.dispatch} / {@link EditorStore.dispatchBatch}, which
 *      run the pure engine (`apply`) and record the engine's inverse PROGRAM. There is no other
 *      write path: a component that wants to change the document issues an op.
 *   2. **History** — an undo/redo stack built on those inverse programs, plus a visible, jump-to-any-
 *      point list. No document snapshot is stored per step; each entry carries the forward program
 *      and the inverse program the engine returned.
 *   3. **UI state** — selection, playhead, zoom, scroll, tool, targeted tracks, in/out marks,
 *      snapping, workspace, per-track preview overrides. It lives OUTSIDE the document, so a view
 *      change can never produce an op, a history entry or a revision. `setUi` is the only way to
 *      change it and it never touches `doc`.
 *
 * ## Atomic batches
 *
 * `dispatchBatch` applies a program of ops as ONE history entry (this is what a composite edit
 * such as "remove silence" compiles to). It is all-or-nothing: if any op throws, every op already
 * applied is undone through its own inverse and the error is rethrown, so the store never holds a
 * half-applied batch. The entry's inverse is the concatenation of the per-op inverses in reverse
 * order, which is exactly what restores the document.
 *
 * ## Pending ops for the sync client
 *
 * Every op the store applied since the last successful save is kept in `pendingOps` (undo adds
 * the inverse program, redo re-adds the forward one), so the sync client can send the server an
 * honest op log for the revision it produces. The server stores them opaque; it never applies them.
 *
 * ## Per-track preview overrides are UI state in P3
 *
 * The thirteen-op catalogue has no track-structure op yet (that is P4), so lock/enable/mute/solo
 * toggles cannot be persisted into the document in this wave. They are therefore `ui.trackOverrides`:
 * real for the preview (the compositor and audio graph read the EFFECTIVE flags) and real for
 * editing (an op that names a UI-locked track is refused here, before the engine runs), but never a
 * write. `effectiveTrackFlags` is the one place that merges the document's flags with the overrides.
 *
 * No React in this module; `use-editor-store.ts` is the hook.
 */

import { apply, EditEngineError, type EditOp, type TrimMode } from '../engine';
import type { EditTimeline, Track } from '../types';
import type { PreviewQuality } from '../preview/device-budget';
import { normalizeSelection } from '../selection/selection';
import { reconcileLinkedSelection } from '../link/groups';

// ─────────────────────────────────────────────────────────────────────────────
// Types
// ─────────────────────────────────────────────────────────────────────────────

/** The editing tool the pointer currently drives on the timeline canvas. */
import type { ComposeEntryId } from '../compose/entries';
export type EditorTool = 'select' | 'blade' | 'trim' | 'hand';

/**
 * The tool the pointer drives OVER THE PICTURE (design §7.3). View state only: it never touches the
 * document. `select` is the transport's normal mode; `transform` and a tracked power window stay
 * pause-only, while the shape tools keep their handles during playback.
 */
export type ViewerTool = 'select' | 'mask' | 'reframe' | 'window' | 'transform' | 'pin';

/** The seven workspaces, in bar order. */
export type WorkspaceId = 'media' | 'cut' | 'edit' | 'compose' | 'color' | 'audio' | 'deliver';

/** The in/out range on the timeline (output clock), half-open `[inFrame, outFrame)`. */
export interface InOutMarks {
  inFrame: number | null;
  outFrame: number | null;
}

/** Per-track preview/editing overrides that are NOT persisted in P3 (see the module note). */
export interface TrackOverride {
  locked?: boolean;
  enabled?: boolean;
  mute?: boolean;
  solo?: boolean;
  /** Collapsed in the canvas — purely a view concern. */
  collapsed?: boolean;
}

export interface UiState {
  /** Playhead position in timeline frames. */
  playheadFrame: number;
  /** Selected item ids — a set, order-insensitive, duplicate-free (see `selection.ts`). */
  selection: string[];
  /** Zoom in CSS pixels per frame. */
  pixelsPerFrame: number;
  /** The first visible frame of the timeline viewport. */
  scrollFrame: number;
  tool: EditorTool;
  /** The viewer tool (§7.3): which handle layer the picture drives. View state only, never persisted in the document. */
  viewerTool: ViewerTool;
  /** Which program the one viewer shows: the timeline (record) or a source (source). View state only, never persisted. */
  viewerMode: 'record' | 'source';
  /** The pool item the Source monitor plays (its media ref), or null. View state only. */
  viewerSource: string | null;
  /** The picture-handles toggle in the viewer tool dock; absent or true is ON (the default). View state only. */
  transformTool?: boolean;
  /** The Compose page's selected list entry (§5.5). View state only, never persisted in the document; null picks the first. */
  composeEntry: ComposeEntryId | null;
  /** Tracks that receive insert/overwrite edits. */
  targetedTrackIds: string[];
  inOut: InOutMarks;
  snapping: boolean;
  workspace: WorkspaceId;
  trackOverrides: Record<string, TrackOverride>;
  /** Playback speed; 0 is paused, negative is reverse (JKL). */
  playbackSpeed: number;
  /** Loop in→out while playing. */
  loop: boolean;
  /** Playback resolution toggle (degradation path). */
  playbackResolution: PreviewQuality;
  /** P7: the before/after toggle — every grade bypassed in the PREVIEW only (never in a render or a still). */
  gradeBypass: boolean;
  /** How a trim-handle drag on the timeline edits the cut: ripple (default), roll, slip or slide. View state: it only chooses the op's `mode`. */
  trimMode: TrimMode;
  /** R08.1: "Edit sound separately" - while on, selecting a clip does not select its linked partners and trims make J/L cuts. View state only. */
  editSeparately: boolean;
}

/** One entry of the visible history list. */
export interface HistoryEntry {
  /** Monotonic id, for keys and for jump-to. */
  id: number;
  label: string;
  /** The ops that were applied, in order. */
  forward: readonly EditOp[];
  /** The program that undoes `forward`, in the order it must be applied. */
  inverse: readonly EditOp[];
  at: number;
}

export interface EditorState {
  timelineId: string;
  projectId: string;
  doc: EditTimeline;
  /** The server revision `doc` was last saved at (or loaded at). */
  revision: number;
  history: readonly HistoryEntry[];
  /** Number of history entries currently applied: `history[0..cursor)` are "done". */
  cursor: number;
  /** Ops applied since the last successful save, in order (inverse programs for undos). */
  pendingOps: readonly EditOp[];
  /** True once any op has been applied since the last save. */
  dirty: boolean;
  /**
   * The document is frozen (the sync client sets this while the timeline is in `conflict`): every
   * document write path refuses or no-ops, so a stale tab cannot drift from what the banner says.
   * View state (`setUi`) still moves.
   */
  readOnly: boolean;
  ui: UiState;
}

export type Listener = () => void;

export interface EditorStoreOptions {
  timelineId: string;
  projectId: string;
  doc: EditTimeline;
  revision: number;
  ui?: Partial<UiState>;
  now?: () => number;
}

export const DEFAULT_PIXELS_PER_FRAME = 2;

/** The sentence a refused edit carries while the store is read-only. */
export const READ_ONLY_MESSAGE = 'This timeline is read-only here because it was changed elsewhere. Reload to keep editing.';

/** The in/out marks a document stores (W0.6: they persist through `setInOut`), or none. */
export function marksOf(doc: EditTimeline): InOutMarks {
  const range = doc.inOut;
  return range ? { inFrame: range.inFrame, outFrame: range.outFrame } : { inFrame: null, outFrame: null };
}

/** The view marks follow the document whenever an op, an undo or a reload changed its stored range. */
function syncMarks(previous: EditTimeline, next: EditTimeline, ui: UiState): UiState {
  return previous.inOut === next.inOut ? ui : { ...ui, inOut: marksOf(next) };
}

export function defaultUiState(doc: EditTimeline): UiState {
  return {
    playheadFrame: 0,
    selection: [],
    pixelsPerFrame: DEFAULT_PIXELS_PER_FRAME,
    scrollFrame: 0,
    tool: 'select',
    viewerTool: 'select',
    viewerMode: 'record',
    viewerSource: null,
    composeEntry: null,
    targetedTrackIds: doc.tracks.filter((t) => t.kind === 'video').slice(0, 1).map((t) => t.id),
    inOut: marksOf(doc),
    snapping: true,
    workspace: 'edit',
    trackOverrides: {},
    playbackSpeed: 0,
    loop: false,
    playbackResolution: 'auto',
    gradeBypass: false,
    trimMode: 'ripple',
    editSeparately: false,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Store
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Consulted before a program of ops is applied. Returns `true` to allow it; `false` means the guard took over
 * (it may ask the user, then dispatch again). Never consulted by undo, redo, `replaceDocument` or `jumpTo`.
 */
export type EditGuard = (ops: readonly EditOp[], doc: EditTimeline, label: string, exemptJobId?: string) => boolean;

/** Options of a dispatch. `exemptJobId`: the id of the background job whose own RESULT this is - the guard never holds a job's result by that job's own mark. */
export interface DispatchOptions {
  exemptJobId?: string;
}

const landedCallbacks = new WeakMap<readonly EditOp[], () => void>();

/**
 * Runs `fn` once, synchronously, when exactly this `ops` array lands (is applied as a history entry) through
 * `dispatchBatch`. An edit the guard holds lands later if the user chooses "Go on" (the guard re-dispatches the
 * same array); a refused or abandoned one never does. Callers use it to record "applied" only when it was.
 */
export function onBatchLanded(ops: readonly EditOp[], fn: () => void): void {
  landedCallbacks.set(ops, fn);
}

export class EditorStore {
  private state: EditorState;
  private editGuard: EditGuard | null = null;
  private readonly listeners = new Set<Listener>();
  private nextEntryId = 1;
  private readonly now: () => number;

  constructor(options: EditorStoreOptions) {
    this.now = options.now ?? (() => Date.now());
    this.state = {
      timelineId: options.timelineId,
      projectId: options.projectId,
      doc: options.doc,
      revision: options.revision,
      history: [],
      cursor: 0,
      pendingOps: [],
      dirty: false,
      readOnly: false,
      ui: { ...defaultUiState(options.doc), ...options.ui },
    };
  }

  // ── subscription ──────────────────────────────────────────────────────────

  getState(): EditorState {
    return this.state;
  }

  /** Installs (or, with null, removes) the guard `dispatch` / `dispatchBatch` consult. */
  setEditGuard(guard: EditGuard | null): void {
    this.editGuard = guard;
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private commit(next: EditorState): void {
    // A selection must only name items the timeline holds: once an edit moves an item away (into a
    // compound's sequence, a lift, a server reload), a stale id would make selection-driven edits
    // such as "Split at playhead" target nothing and silently do nothing.
    if (next.doc !== this.state.doc && next.ui.selection.length > 0) {
      const selection = normalizeSelection(next.doc, next.ui.selection);
      if (selection.length !== next.ui.selection.length) next = { ...next, ui: { ...next.ui, selection } };
    }
    this.state = next;
    for (const listener of this.listeners) listener();
  }

  // ── document edits ────────────────────────────────────────────────────────

  /** Applies one op as one history entry. Throws `EditEngineError` and changes nothing on refusal. */
  dispatch(op: EditOp, label?: string, options?: DispatchOptions): boolean {
    return this.dispatchBatch([op], label ?? describeOp(op), options);
  }

  /**
   * Applies a program of ops as ONE history entry, all-or-nothing. On a refusal partway through,
   * the ops already applied are undone through their own inverses and the error is rethrown.
   * Returns `false` when the edit guard held the batch (nothing changed yet), `true` when it landed.
   */
  dispatchBatch(ops: readonly EditOp[], label: string, options?: DispatchOptions): boolean {
    if (ops.length === 0) return true;
    this.refuseIfReadOnly();
    if (this.editGuard && !this.editGuard(ops, this.state.doc, label, options?.exemptJobId)) return false;

    let doc = this.state.doc;
    const inversesInOrder: EditOp[][] = [];

    for (const op of ops) {
      this.refuseIfUiLocked(doc, op);
      try {
        const result = apply(doc, op);
        doc = result.doc;
        inversesInOrder.push(result.inverse);
      } catch (error) {
        // Roll back what was applied: inverses in reverse order. Each inverse was produced by the
        // engine for the exact document it is applied to, so this cannot fail — and it replays as an
        // INVERSE program, so T-112's restore exemption applies here exactly as it does to `undo`
        // (S-LFR-14: without it, rolling back a batch that replaced a dangling colour group threw and
        // masked the real failure).
        for (let i = inversesInOrder.length - 1; i >= 0; i--) {
          for (const inverseOp of inversesInOrder[i]) doc = apply(doc, inverseOp, { inverse: true }).doc;
        }
        throw error;
      }
    }

    const inverse: EditOp[] = [];
    for (let i = inversesInOrder.length - 1; i >= 0; i--) inverse.push(...inversesInOrder[i]);

    const entry: HistoryEntry = {
      id: this.nextEntryId++,
      label,
      forward: [...ops],
      inverse,
      at: this.now(),
    };

    // Dispatching after an undo drops the redo tail — the standard, honest behaviour.
    const history = [...this.state.history.slice(0, this.state.cursor), entry];

    this.commit({
      ...this.state,
      doc,
      ui: syncMarks(this.state.doc, doc, this.state.ui),
      history,
      cursor: history.length,
      pendingOps: [...this.state.pendingOps, ...ops],
      dirty: true,
    });
    const landed = landedCallbacks.get(ops);
    if (landed) {
      landedCallbacks.delete(ops);
      landed();
    }
    return true;
  }

  canUndo(): boolean {
    return !this.state.readOnly && this.state.cursor > 0;
  }

  canRedo(): boolean {
    return !this.state.readOnly && this.state.cursor < this.state.history.length;
  }

  /**
   * Undoes the newest applied entry by replaying its inverse program. Returns `true` when the
   * document was restored, `false` when the program was refused.
   *
   * A refused inverse program is a FAILED UNDO, never an escaping `EditEngineError`: the ops already
   * replayed are rolled back so the document is byte-identical, and the cursor, history and pending
   * ops are left exactly as they were — the entry stays applied and the user can retry. (The engine
   * composes its inverses so a replay restores the exact document it was built from; a refusal means
   * the document was changed underneath the stack, which is a state to surface, not to crash on.)
   */
  undo(): boolean {
    if (!this.canUndo()) return false;
    const entry = this.state.history[this.state.cursor - 1];
    const doc = this.applyProgram(this.state.doc, entry.inverse, true);
    if (doc === undefined) return false;
    this.commit({
      ...this.state,
      doc,
      ui: syncMarks(this.state.doc, doc, this.state.ui),
      cursor: this.state.cursor - 1,
      pendingOps: [...this.state.pendingOps, ...entry.inverse],
      dirty: true,
    });
    return true;
  }

  /**
   * Redoes the next entry by replaying its forward program. Returns `false` (and changes nothing)
   * when the program is refused, the same contract as {@link EditorStore.undo}.
   */
  redo(): boolean {
    if (!this.canRedo()) return false;
    const entry = this.state.history[this.state.cursor];
    const doc = this.applyProgram(this.state.doc, entry.forward, false);
    if (doc === undefined) return false;
    this.commit({
      ...this.state,
      doc,
      ui: syncMarks(this.state.doc, doc, this.state.ui),
      cursor: this.state.cursor + 1,
      pendingOps: [...this.state.pendingOps, ...entry.forward],
      dirty: true,
    });
    return true;
  }

  /**
   * Replays a program onto `doc`, or `undefined` if any op is refused — the caller then changes
   * nothing. `asInverse` is true only for an entry's inverse program: the engine uses it to grant the
   * T-112 restore exemption, so the exemption can never be carried on an op a caller supplies.
   */
  private applyProgram(doc: EditTimeline, program: readonly EditOp[], asInverse: boolean): EditTimeline | undefined {
    let next = doc;
    for (const op of program) {
      try {
        next = apply(next, op, { inverse: asInverse }).doc;
      } catch {
        return undefined;
      }
    }
    return next;
  }

  /**
   * Moves the history cursor to `targetCursor` (0 = everything undone, `history.length` =
   * everything applied) by undoing or redoing entry by entry. Jumping is just repeated undo/redo,
   * so it is exactly as safe as they are: a refused step stops the walk where it is, leaving the
   * cursor at the last entry that did apply.
   */
  jumpTo(targetCursor: number): void {
    if (this.state.readOnly) return;
    const target = Math.max(0, Math.min(this.state.history.length, Math.trunc(targetCursor)));
    while (this.state.cursor > target && this.undo()) {
      /* step down until the target or a refusal */
    }
    while (this.state.cursor < target && this.redo()) {
      /* step up until the target or a refusal */
    }
  }

  /**
   * Records a successful save: the revision the server produced and how many of `pendingOps` that
   * save carried. Ops that arrived while the request was in flight stay pending and the store stays
   * dirty, so the next flush sends exactly them against the new revision — and the undo history is
   * untouched either way. Omitting `sentOpCount` means "everything that was pending".
   */
  markSaved(revision: number, sentOpCount?: number): void {
    const count = sentOpCount === undefined ? this.state.pendingOps.length : Math.max(0, sentOpCount);
    const remaining = this.state.pendingOps.slice(count);
    this.commit({ ...this.state, revision, pendingOps: remaining, dirty: remaining.length > 0 });
  }

  /**
   * Replaces the document wholesale (a server reload, a restore from a revision). Clears history and
   * pending ops — there is no inverse program for "the server said so".
   */
  replaceDocument(doc: EditTimeline, revision: number, options?: { markDirty?: boolean; pendingOps?: EditOp[] }): void {
    this.refuseIfReadOnly();
    this.commit({
      ...this.state,
      doc,
      ui: syncMarks(this.state.doc, doc, this.state.ui),
      revision,
      history: [],
      cursor: 0,
      pendingOps: options?.pendingOps ?? [],
      dirty: options?.markDirty ?? false,
    });
  }

  /** Freezes (or thaws) the document. Undo/redo/jump no-op and every other write throws while frozen. */
  setReadOnly(readOnly: boolean): void {
    if (this.state.readOnly === readOnly) return;
    this.commit({ ...this.state, readOnly });
  }

  // ── UI state ──────────────────────────────────────────────────────────────

  /** Changes view state only. Never produces an op, a history entry or a revision. */
  setUi(patch: Partial<UiState> | ((ui: UiState) => Partial<UiState>)): void {
    let resolved = typeof patch === 'function' ? patch(this.state.ui) : patch;
    const ui = { ...this.state.ui, ...resolved };
    // R08.1: selecting one half of a linked pair selects the group (unless "edit sound separately" is on).
    if (resolved.selection && resolved.selection !== this.state.ui.selection && !ui.editSeparately) {
      const selection = reconcileLinkedSelection(this.state.doc, this.state.ui.selection, resolved.selection);
      if (selection.length !== resolved.selection.length || selection.some((id, i) => id !== resolved.selection![i])) {
        resolved = { ...resolved, selection: normalizeSelection(this.state.doc, selection) };
      }
    }
    this.commit({ ...this.state, ui: { ...this.state.ui, ...resolved } });
  }

  /**
   * W0.6: sets the in/out marks. A complete pair (in >= 0, out > in) that differs from the stored
   * range is written to the document as one `setInOut` op, and clearing both clears it, so a reload
   * keeps the marks and the Deliver range reads them. A half-set pair stays view state until it is
   * complete. `persist: false` (a read-only timeline) only moves the marks on screen.
   */
  setInOutMarks(marks: InOutMarks, options?: { persist?: boolean }): void {
    this.setUi({ inOut: { inFrame: marks.inFrame, outFrame: marks.outFrame } });
    if (options?.persist === false) return;
    const stored = this.state.doc.inOut ?? null;
    const { inFrame, outFrame } = marks;
    if (inFrame === null && outFrame === null) {
      if (stored !== null) this.dispatch({ type: 'setInOut', inOut: null }, 'Clear in/out');
      return;
    }
    if (inFrame === null || outFrame === null || inFrame < 0 || outFrame <= inFrame) return;
    if (stored && stored.inFrame === inFrame && stored.outFrame === outFrame) return;
    this.dispatch({ type: 'setInOut', inOut: { inFrame, outFrame } }, 'Set in/out');
  }

  setTrackOverride(trackId: string, override: TrackOverride): void {
    this.setUi((ui) => ({
      trackOverrides: {
        ...ui.trackOverrides,
        [trackId]: { ...ui.trackOverrides[trackId], ...override },
      },
    }));
  }

  /** The document flags merged with the UI overrides — the one truth the preview and the gate read. */
  effectiveTrackFlags(track: Track): Required<Pick<TrackOverride, 'locked' | 'enabled' | 'mute' | 'solo'>> {
    const override = this.state.ui.trackOverrides[track.id] ?? {};
    return {
      locked: override.locked ?? track.locked,
      enabled: override.enabled ?? track.enabled,
      mute: override.mute ?? track.mute,
      solo: override.solo ?? track.solo,
    };
  }

  // ── guards ────────────────────────────────────────────────────────────────

  private refuseIfReadOnly(): void {
    if (this.state.readOnly) throw new EditEngineError(READ_ONLY_MESSAGE);
  }

  /** Refuses an op that names a UI-locked track, in the engine's own error type, before the engine runs. */
  private refuseIfUiLocked(doc: EditTimeline, op: EditOp): void {
    const trackIds = tracksNamedBy(doc, op);
    for (const trackId of trackIds) {
      const track = doc.tracks.find((t) => t.id === trackId);
      if (track && this.effectiveTrackFlags(track).locked && !track.locked) {
        throw new EditEngineError(`track '${trackId}' is locked in the editor`, op.type);
      }
    }
  }
}

export function createEditorStore(options: EditorStoreOptions): EditorStore {
  return new EditorStore(options);
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

/** Every track an op addresses — directly, or through an item that lives on it. */
export function tracksNamedBy(doc: EditTimeline, op: EditOp): string[] {
  const ids = new Set<string>();
  const byItem = (itemId: string): void => {
    for (const track of doc.tracks) {
      if (track.items.some((item) => item.id === itemId)) ids.add(track.id);
    }
  };
  switch (op.type) {
    case 'insert':
    case 'deleteGap':
    case 'closeGaps':
      ids.add(op.trackId);
      break;
    case 'move':
      ids.add(op.toTrackId);
      byItem(op.itemId);
      break;
    case 'overwrite':
    case 'lift':
    case 'extract':
    case 'trim':
    case 'blade':
    case 'setProperty':
    case 'replace':
    case 'fitToFill':
    case 'join':
    case 'nudge':
      byItem(op.itemId);
      break;
    case 'append':
    case 'placeOnTop':
    case 'removeTrack':
    case 'moveTrack':
      ids.add(op.trackId);
      break;
    case 'setTrackProperty':
      // Unlocking a UI-locked track is exactly what the lock toggle does; every other key respects it.
      if (op.key !== 'locked') ids.add(op.trackId);
      break;
    case 'retime':
      byItem(op.itemId);
      break;
    case 'addTransition':
      byItem(op.afterItemId);
      break;
    case 'removeTransition':
      byItem(op.transitionId);
      break;
    case 'setKeyframe':
    case 'removeKeyframe':
    case 'moveKeyframe':
      if ('trackId' in op.target) ids.add(op.target.trackId);
      else byItem(op.target.itemId);
      break;
    case 'setColorGroup':
      // A group names clips; the locked-track rule applies to each member's track.
      for (const clipId of op.group?.clipIds ?? []) byItem(clipId);
      break;
    case 'addTrack':
    case 'setTimelineSettings':
    case 'setInOut':
    case 'addMarker':
    case 'setMarker':
    case 'removeMarker':
      break;
    default:
      break;
  }
  return [...ids];
}

/** A short human label for a history entry when the caller gave none. */
export function describeOp(op: EditOp): string {
  switch (op.type) {
    case 'insert':
      return `Insert ${op.item.name || op.item.type}`;
    case 'overwrite':
      return `Overwrite ${op.item.name || op.itemId}`;
    case 'lift':
      return 'Lift';
    case 'extract':
      return 'Extract (ripple delete)';
    case 'trim':
      return `Trim ${op.mode} (${op.edge}) ${op.deltaFrames >= 0 ? '+' : ''}${op.deltaFrames}f`;
    case 'blade':
      return `Blade at ${op.atFrame}`;
    case 'deleteGap':
      return `Delete gap at ${op.atFrame}`;
    case 'closeGaps':
      return 'Close gaps';
    case 'move':
      return `Move to ${op.toRecordIn}`;
    case 'setProperty':
      return `Set ${op.key}`;
    case 'addMarker':
      return `Add marker ${op.marker.name}`;
    case 'setMarker':
      return 'Edit marker';
    case 'removeMarker':
      return 'Remove marker';
    case 'append':
      return `Append ${op.item.name || op.item.type}`;
    case 'replace':
      return `Replace with ${op.item.name || op.item.type}`;
    case 'fitToFill':
      return `Fit to fill ${op.recordIn}–${op.recordOut}`;
    case 'placeOnTop':
      return `Place ${op.item.name || op.item.type}`;
    case 'join':
      return 'Join clips';
    case 'nudge':
      return `Nudge ${op.deltaFrames >= 0 ? '+' : ''}${op.deltaFrames}f`;
    case 'addTrack':
      return `Add ${op.track.kind} track`;
    case 'removeTrack':
      return 'Remove track';
    case 'moveTrack':
      return 'Move track';
    case 'setTrackProperty':
      return `Set track ${op.key}`;
    case 'setTimelineSettings':
      return 'Change timeline settings';
    case 'setInOut':
      return op.inOut ? 'Set in/out' : 'Clear in/out';
    case 'setKeyframe':
      return `Set ${op.param} key at ${op.frame}`;
    case 'removeKeyframe':
      return `Remove ${op.param} key at ${op.frame}`;
    case 'moveKeyframe':
      return `Move ${op.param} key to ${op.toFrame}`;
    case 'retime':
      return `Retime ${op.playbackRate}×`;
    case 'addTransition':
      return `Add ${op.transition.transitionType} transition`;
    case 'removeTransition':
      return 'Remove transition';
    case 'setColorGroup':
      return op.group ? `Set colour group ${op.group.name}` : 'Remove colour group';
    case 'setSequence':
      return op.sequence ? `Set sequence ${op.sequence.name}` : 'Remove sequence';
    case 'setBus':
      return op.bus ? `Set bus ${op.bus.name}` : 'Remove bus';
    default:
      return 'Edit';
  }
}
