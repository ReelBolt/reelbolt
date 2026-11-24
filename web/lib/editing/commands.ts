import type { EditorStore, WorkspaceId } from './store/editor-store';
import { KEY_ACTIONS, type KeyAction, type KeyActionInfo } from './keymap';
import { nextAfter, nextEdit, previousBefore, previousEdit, shuttle, stepFrame } from './transport';
import { runSelectCommand, selectAll } from './selection';
import { allMarkerFrames } from './markers/markers';
import type { InOutMarks } from './store/editor-store';
import type { PlaceMode } from './media/place';
import type { SimplePlace } from './shell-choice';
import { COMPOSE_ACTIONS, composeUnavailable, runComposeCommand, type ComposeCommandEnv, type ComposeUnavailable } from './compose/commands';

/**
 * T-007: the one command catalogue. Every keymap action has exactly one descriptor here; the global
 * keydown listener, the command palette and (through the same `runCommand`) anything else that runs an
 * action share it, so a palette Undo and Mod+Z are one code path. No document, sync or render change.
 */

/** The timeline editing callbacks the commands need (a subset of `useTimelineEditing`). */
export interface CommandEditing {
  copy: () => void;
  cut: () => void;
  paste: () => void;
  duplicate: () => void;
  linkSelected: () => void;
  unlinkSelected: () => void;
  moveToTrack: (delta: -1 | 1) => void;
}

/**
 * The Source monitor's transport, handed to the command catalogue by the editor so Space, J K L, the
 * arrows, I, O and clear marks act on the source while the viewer shows it. UI-only: none of these
 * touches the document or the timeline's own marks.
 */
export interface SourceTransport {
  togglePlay: () => void;
  shuttleReverse: () => void;
  stop: () => void;
  shuttleForward: () => void;
  step: (delta: number) => void;
  goToStart: () => void;
  goToEnd: () => void;
  markIn: () => void;
  markOut: () => void;
  clearMarks: () => void;
}

/** The pages whose viewer carries the Source | Record switch. */
export const VIEWER_SWITCH_PAGES: ReadonlySet<WorkspaceId> = new Set<WorkspaceId>(['cut', 'edit']);

/** Everything a command may read or call. Built per key press (or per palette run) by the editor. */
export interface CommandContext {
  store: EditorStore;
  endFrame: number;
  undo: () => void;
  redo: () => void;
  addMarker: (name: string) => void;
  liftSelection: () => void;
  extractSelection: () => void;
  nudgeSelection: (delta: number) => void;
  bladeAtPlayhead: () => void;
  playInToOut: () => boolean;
  setMarks: (marks: InOutMarks) => void;
  editing: CommandEditing;
  setWorkspace: (workspace: WorkspaceId) => void;
  openCheatsheet: () => void;
  openPalette: () => void;
  /** T-012: puts the open source over the pictures below (picture-in-picture); absent where no source is open. */
  placeOver?: () => void;
  /** Shows the viewer (picture and transport) on the whole screen, or leaves it; absent where the host has no viewer to enlarge. */
  toggleFullscreen?: () => void;
  /** The Events lane: toggles and the act-on-event menu. Absent where there is no lane (the actions then do nothing). */
  events?: { toggleLane: () => void; toggleSnap: () => void; actOnNearest: () => void };
  /** What the Compose actions need from the workspace; absent in contexts that never run them. */
  compose?: ComposeCommandEnv;
  /** The Source monitor's transport; absent where there is no switch (phone, other pages). */
  source?: SourceTransport;
  /** Places the loaded source (the Source monitor's item, its marks, the timeline playhead); absent when no source is loaded. */
  place?: (mode: PlaceMode) => void;
  /** The key event, when the command came from the keyboard; absent from the palette. */
  event?: Pick<KeyboardEvent, 'preventDefault'>;
  /**
   * Present while the Simple shell shows. `setPlace` moves between its three places; `openFull` is asked when an
   * action needs something only the full editor shows (the host decides how: the palette switches, a chord offers).
   */
  simple?: { setPlace: (place: SimplePlace) => void; openFull: (id: KeyAction, fromKeyboard: boolean) => void };
}

export interface CommandDescriptor {
  id: KeyAction;
  /** English label from the catalogue; UI renders `keyActionLabelMsg(id)`. */
  label: string;
  group: KeyActionInfo['group'];
  run: (ctx: CommandContext) => void;
}

const prevent = (ctx: CommandContext) => ctx.event?.preventDefault();

/** The seven page actions, in bar order, with the workspace each one opens. */
export const PAGE_COMMANDS: Readonly<Record<string, WorkspaceId>> = Object.freeze({
  pageMedia: 'media',
  pageCut: 'cut',
  pageEdit: 'edit',
  pageCompose: 'compose',
  pageColour: 'color',
  pageAudio: 'audio',
  pageDeliver: 'deliver',
});

const nudgeStep = (id: KeyAction): number => (id === 'nudgeBack' ? -1 : id === 'nudgeBack10' ? -10 : id === 'nudgeForward' ? 1 : 10);

const composeRun = (id: KeyAction, ctx: CommandContext) => { if (ctx.compose) runComposeCommand(id, ctx.store, ctx.compose, ctx.setWorkspace); };

/** The actions that move the timeline's own playhead or marks: while Source shows they act on the source instead (or do nothing), never on the timeline. */
const SOURCE_ACTIONS: Readonly<Partial<Record<KeyAction, (src: SourceTransport) => void>>> = {
  playPause: (s) => s.togglePlay(),
  playInToOut: (s) => s.togglePlay(),
  shuttleReverse: (s) => s.shuttleReverse(),
  stop: (s) => s.stop(),
  shuttleForward: (s) => s.shuttleForward(),
  frameBack: (s) => s.step(-1),
  frameForward: (s) => s.step(1),
  frameBack10: (s) => s.step(-10),
  frameForward10: (s) => s.step(10),
  goToStart: (s) => s.goToStart(),
  goToEnd: (s) => s.goToEnd(),
  markIn: (s) => s.markIn(),
  markOut: (s) => s.markOut(),
  clearInOut: (s) => s.clearMarks(),
  // Edit and marker jumps and timeline markers have no source meaning: in Source they change nothing.
  previousEdit: () => {},
  nextEdit: () => {},
  previousMarker: () => {},
  nextMarker: () => {},
  addMarker: () => {},
};

/** T-018: the four placement actions and the mode each one runs. */
export const PLACE_ACTIONS: Readonly<Partial<Record<KeyAction, PlaceMode>>> = Object.freeze({ placeInsert: 'insert', placeOverwrite: 'overwrite', placeAppend: 'append', placeOnTop: 'placeOnTop' });

/**
 * What each action does while the Source monitor shows. `transport` acts on the source (SOURCE_ACTIONS);
 * `writer` changes the timeline document, so in Source it does nothing (the palette says "Switch to Record
 * to edit the timeline"); `place` is the one way Source writes forward; `open` is not a timeline write
 * (selection, tools, pages, undo and redo as the escape hatch). A `Record` makes this exhaustive: a new
 * keymap action does not compile until someone decides.
 */
export type SourceDecision = 'transport' | 'writer' | 'place' | 'open';
export const SOURCE_DECISIONS: Readonly<Record<KeyAction, SourceDecision>> = Object.freeze({
  playPause: 'transport', playInToOut: 'transport', shuttleReverse: 'transport', stop: 'transport', shuttleForward: 'transport',
  frameBack: 'transport', frameForward: 'transport', frameBack10: 'transport', frameForward10: 'transport',
  previousEdit: 'transport', nextEdit: 'transport', previousMarker: 'transport', nextMarker: 'transport',
  goToStart: 'transport', goToEnd: 'transport', markIn: 'transport', markOut: 'transport', clearInOut: 'transport', addMarker: 'transport',
  toolBlade: 'open', toolSelect: 'open', toolHand: 'open', trimRipple: 'open', trimRoll: 'open', trimSlip: 'open', trimSlide: 'open', toggleSnapping: 'open',
  deleteSelection: 'writer', liftSelection: 'writer', bladeAtPlayhead: 'writer',
  nudgeBack: 'writer', nudgeForward: 'writer', nudgeBack10: 'writer', nudgeForward10: 'writer',
  copyClips: 'open', cutClips: 'writer', pasteClips: 'writer', duplicateClips: 'writer',
  moveTrackUp: 'writer', moveTrackDown: 'writer', linkClips: 'writer', unlinkClips: 'writer',
  deselect: 'open', undo: 'open', redo: 'open', selectAll: 'open', selectForward: 'open', selectBackward: 'open', selectAllOnTrack: 'open',
  toolMask: 'open', toolReframe: 'open', toolCornerPin: 'open', toolTransform: 'open',
  maskShapeNext: 'writer', maskInvert: 'writer', composeRemoveEntry: 'writer', composeEntryPrevious: 'open', composeEntryNext: 'open', addAdjustmentClip: 'writer',
  pageMedia: 'open', pageCut: 'open', pageEdit: 'open', pageCompose: 'open', pageColour: 'open', pageAudio: 'open', pageDeliver: 'open',
  toggleViewerMode: 'open', viewerFullscreen: 'open',
  toggleEventsLane: 'open', toggleEventSnap: 'open', actOnEvent: 'writer',
  placeInsert: 'place', placeOverwrite: 'place', placeAppend: 'place', placeOnTop: 'place', placeOver: 'place',
  commandPalette: 'open',
});

/**
 * A writer run from a menu or a toolbar button is a deliberate pointer action on the timeline (a chord is not, so
 * it stays blocked above): the viewer returns to Record first, so the user sees what changed.
 */
export function showRecordFirst(store: Pick<EditorStore, 'getState' | 'setUi'>): void {
  if (store.getState().ui.viewerMode === 'source') store.setUi({ viewerMode: 'record' });
}

const COMPOSE_TOOL_ACTIONS: ReadonlySet<KeyAction> = new Set<KeyAction>(['toolMask', 'toolReframe', 'toolCornerPin']);

/**
 * What each action does while the Simple shell shows. `works` runs as in the full editor; `opensFull` needs a page,
 * panel or lane only the full editor shows, so the host offers it (and runs the action there); `off` has no meaning
 * without the Source monitor. A `Record` makes this exhaustive: a new keymap action does not compile until someone
 * decides. The two page actions marked `works` move between the Simple shell's own places (Edit, Export).
 */
export type SimpleDecision = 'works' | 'opensFull' | 'off';
export const SIMPLE_DECISIONS: Readonly<Record<KeyAction, SimpleDecision>> = Object.freeze({
  playPause: 'works', playInToOut: 'works', shuttleReverse: 'works', stop: 'works', shuttleForward: 'works',
  frameBack: 'works', frameForward: 'works', frameBack10: 'works', frameForward10: 'works',
  previousEdit: 'works', nextEdit: 'works', previousMarker: 'works', nextMarker: 'works',
  goToStart: 'works', goToEnd: 'works', markIn: 'works', markOut: 'works', clearInOut: 'works', addMarker: 'works',
  toolBlade: 'works', toolSelect: 'works', toolHand: 'works', trimRipple: 'works', trimRoll: 'works', trimSlip: 'works', trimSlide: 'works', toggleSnapping: 'works',
  deleteSelection: 'works', liftSelection: 'works', bladeAtPlayhead: 'works',
  nudgeBack: 'works', nudgeForward: 'works', nudgeBack10: 'works', nudgeForward10: 'works',
  copyClips: 'works', cutClips: 'works', pasteClips: 'works', duplicateClips: 'works',
  moveTrackUp: 'works', moveTrackDown: 'works', linkClips: 'works', unlinkClips: 'works',
  deselect: 'works', undo: 'works', redo: 'works', selectAll: 'works', selectForward: 'works', selectBackward: 'works', selectAllOnTrack: 'works',
  toolMask: 'opensFull', toolReframe: 'opensFull', toolCornerPin: 'opensFull', toolTransform: 'works',
  maskShapeNext: 'opensFull', maskInvert: 'opensFull', composeRemoveEntry: 'opensFull', composeEntryPrevious: 'opensFull', composeEntryNext: 'opensFull', addAdjustmentClip: 'opensFull',
  pageMedia: 'opensFull', pageCut: 'opensFull', pageEdit: 'works', pageCompose: 'opensFull', pageColour: 'opensFull', pageAudio: 'opensFull', pageDeliver: 'works',
  toggleViewerMode: 'off', viewerFullscreen: 'works',
  toggleEventsLane: 'opensFull', toggleEventSnap: 'opensFull', actOnEvent: 'opensFull',
  placeInsert: 'off', placeOverwrite: 'off', placeAppend: 'off', placeOnTop: 'off', placeOver: 'off',
  commandPalette: 'works',
});

const RUNNERS: Record<KeyAction, (ctx: CommandContext) => void> = {
  undo: (ctx) => { prevent(ctx); ctx.undo(); },
  redo: (ctx) => { prevent(ctx); ctx.redo(); },
  selectAll: (ctx) => { prevent(ctx); ctx.store.setUi({ selection: selectAll(ctx.store.getState().doc) }); },
  selectForward: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ selection: runSelectCommand(s.doc, s.ui, 'forward') }); },
  selectBackward: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ selection: runSelectCommand(s.doc, s.ui, 'backward') }); },
  selectAllOnTrack: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ selection: runSelectCommand(s.doc, s.ui, 'track') }); },
  playPause: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ playbackSpeed: shuttle(s.ui.playbackSpeed, 'Space') }); },
  playInToOut: (ctx) => {
    prevent(ctx);
    const s = ctx.store.getState();
    if (s.ui.inOut.inFrame === null || s.ui.inOut.outFrame === null || !ctx.playInToOut()) ctx.store.setUi({ playbackSpeed: shuttle(s.ui.playbackSpeed, 'Space') });
  },
  shuttleReverse: (ctx) => { const s = ctx.store.getState(); ctx.store.setUi({ playbackSpeed: shuttle(s.ui.playbackSpeed, 'J') }); },
  stop: (ctx) => { ctx.store.setUi({ playbackSpeed: 0 }); },
  shuttleForward: (ctx) => { const s = ctx.store.getState(); ctx.store.setUi({ playbackSpeed: shuttle(s.ui.playbackSpeed, 'L') }); },
  frameBack: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ playheadFrame: stepFrame(s.ui.playheadFrame, -1, Math.max(0, ctx.endFrame - 1)), playbackSpeed: 0 }); },
  frameForward: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ playheadFrame: stepFrame(s.ui.playheadFrame, 1, Math.max(0, ctx.endFrame - 1)), playbackSpeed: 0 }); },
  frameBack10: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ playheadFrame: stepFrame(s.ui.playheadFrame, -10, Math.max(0, ctx.endFrame - 1)), playbackSpeed: 0 }); },
  frameForward10: (ctx) => { prevent(ctx); const s = ctx.store.getState(); ctx.store.setUi({ playheadFrame: stepFrame(s.ui.playheadFrame, 10, Math.max(0, ctx.endFrame - 1)), playbackSpeed: 0 }); },
  previousEdit: (ctx) => { prevent(ctx); const s = ctx.store.getState(); const frame = previousEdit(s.doc, s.ui.playheadFrame); if (frame !== null) ctx.store.setUi({ playheadFrame: frame }); },
  previousMarker: (ctx) => { prevent(ctx); const s = ctx.store.getState(); const frame = previousBefore(allMarkerFrames(s.doc), s.ui.playheadFrame); if (frame !== null) ctx.store.setUi({ playheadFrame: frame }); },
  nextEdit: (ctx) => { prevent(ctx); const s = ctx.store.getState(); const frame = nextEdit(s.doc, s.ui.playheadFrame); if (frame !== null) ctx.store.setUi({ playheadFrame: Math.min(frame, Math.max(0, ctx.endFrame - 1)) }); },
  nextMarker: (ctx) => { prevent(ctx); const s = ctx.store.getState(); const frame = nextAfter(allMarkerFrames(s.doc), s.ui.playheadFrame); if (frame !== null) ctx.store.setUi({ playheadFrame: Math.min(frame, Math.max(0, ctx.endFrame - 1)) }); },
  goToStart: (ctx) => { ctx.store.setUi({ playheadFrame: 0 }); },
  goToEnd: (ctx) => { ctx.store.setUi({ playheadFrame: Math.max(0, ctx.endFrame - 1) }); },
  markIn: (ctx) => { const s = ctx.store.getState(); ctx.setMarks({ ...s.ui.inOut, inFrame: s.ui.playheadFrame }); },
  markOut: (ctx) => { const s = ctx.store.getState(); ctx.setMarks({ ...s.ui.inOut, outFrame: s.ui.playheadFrame + 1 }); },
  clearInOut: (ctx) => { ctx.setMarks({ inFrame: null, outFrame: null }); },
  toolBlade: (ctx) => { ctx.store.setUi({ tool: 'blade', viewerTool: 'select' }); },
  toolSelect: (ctx) => { ctx.store.setUi({ tool: 'select', viewerTool: 'select' }); },
  toolHand: (ctx) => { ctx.store.setUi({ tool: 'hand', viewerTool: 'select' }); },
  toolTransform: (ctx) => { const s = ctx.store.getState(); ctx.store.setUi({ transformTool: s.ui.transformTool === false, viewerTool: 'select' }); },
  toggleSnapping: (ctx) => { const s = ctx.store.getState(); ctx.store.setUi({ snapping: !s.ui.snapping }); },
  toggleEventsLane: (ctx) => { ctx.events?.toggleLane(); },
  toggleEventSnap: (ctx) => { ctx.events?.toggleSnap(); },
  actOnEvent: (ctx) => { prevent(ctx); ctx.events?.actOnNearest(); },
  addMarker: (ctx) => { ctx.addMarker('Marker'); },
  deleteSelection: (ctx) => { prevent(ctx); ctx.extractSelection(); },
  liftSelection: (ctx) => { prevent(ctx); ctx.liftSelection(); },
  bladeAtPlayhead: (ctx) => { prevent(ctx); ctx.bladeAtPlayhead(); },
  trimRipple: (ctx) => { ctx.store.setUi({ trimMode: 'ripple' }); },
  trimRoll: (ctx) => { ctx.store.setUi({ trimMode: 'roll' }); },
  trimSlip: (ctx) => { ctx.store.setUi({ trimMode: 'slip' }); },
  trimSlide: (ctx) => { ctx.store.setUi({ trimMode: 'slide' }); },
  copyClips: (ctx) => { prevent(ctx); ctx.editing.copy(); },
  cutClips: (ctx) => { prevent(ctx); ctx.editing.cut(); },
  pasteClips: (ctx) => { prevent(ctx); ctx.editing.paste(); },
  duplicateClips: (ctx) => { prevent(ctx); ctx.editing.duplicate(); },
  linkClips: (ctx) => { prevent(ctx); ctx.editing.linkSelected(); },
  unlinkClips: (ctx) => { prevent(ctx); ctx.editing.unlinkSelected(); },
  moveTrackUp: (ctx) => { prevent(ctx); ctx.editing.moveToTrack(-1); },
  moveTrackDown: (ctx) => { prevent(ctx); ctx.editing.moveToTrack(1); },
  nudgeBack: (ctx) => { prevent(ctx); ctx.nudgeSelection(nudgeStep('nudgeBack')); },
  nudgeForward: (ctx) => { prevent(ctx); ctx.nudgeSelection(nudgeStep('nudgeForward')); },
  nudgeBack10: (ctx) => { prevent(ctx); ctx.nudgeSelection(nudgeStep('nudgeBack10')); },
  nudgeForward10: (ctx) => { prevent(ctx); ctx.nudgeSelection(nudgeStep('nudgeForward10')); },
  deselect: (ctx) => {
    const s = ctx.store.getState();
    // §7.3: Escape first leaves a picture tool and keeps the selection; the paused auto-show stays.
    // A second Escape (no tool active) then clears the selection, as before.
    if (s.ui.viewerTool !== 'select') {
      ctx.store.setUi({ viewerTool: 'select' });
      return;
    }
    ctx.store.setUi({ selection: [] });
  },
  toolMask: (ctx) => composeRun('toolMask', ctx),
  toolReframe: (ctx) => composeRun('toolReframe', ctx),
  toolCornerPin: (ctx) => composeRun('toolCornerPin', ctx),
  maskShapeNext: (ctx) => composeRun('maskShapeNext', ctx),
  maskInvert: (ctx) => composeRun('maskInvert', ctx),
  composeRemoveEntry: (ctx) => composeRun('composeRemoveEntry', ctx),
  composeEntryPrevious: (ctx) => composeRun('composeEntryPrevious', ctx),
  composeEntryNext: (ctx) => composeRun('composeEntryNext', ctx),
  addAdjustmentClip: (ctx) => composeRun('addAdjustmentClip', ctx),
  placeInsert: (ctx) => { prevent(ctx); ctx.place?.('insert'); },
  placeOverwrite: (ctx) => { prevent(ctx); ctx.place?.('overwrite'); },
  placeAppend: (ctx) => { prevent(ctx); ctx.place?.('append'); },
  placeOnTop: (ctx) => { prevent(ctx); ctx.place?.('placeOnTop'); },
  placeOver: (ctx) => { prevent(ctx); ctx.placeOver?.(); },
  pageMedia: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageMedia),
  pageCut: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageCut),
  pageEdit: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageEdit),
  pageCompose: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageCompose),
  pageColour: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageColour),
  pageAudio: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageAudio),
  pageDeliver: (ctx) => ctx.setWorkspace(PAGE_COMMANDS.pageDeliver),
  viewerFullscreen: (ctx) => { prevent(ctx); ctx.toggleFullscreen?.(); },
  toggleViewerMode: (ctx) => {
    const ui = ctx.store.getState().ui;
    if (!VIEWER_SWITCH_PAGES.has(ui.workspace)) return;
    if (ui.viewerMode === 'source') ctx.store.setUi({ viewerMode: 'record' });
    else ctx.store.setUi({ viewerMode: 'source' });
  },
  commandPalette: (ctx) => ctx.openPalette(),
};

/** One descriptor per keymap action, in catalogue order. */
export const COMMANDS: readonly CommandDescriptor[] = Object.freeze(
  KEY_ACTIONS.map((info) => Object.freeze({ id: info.id, label: info.label, group: info.group, run: RUNNERS[info.id] })),
);

const BY_ID = new Map<KeyAction, CommandDescriptor>(COMMANDS.map((c) => [c.id, c]));

export function commandDescriptor(id: KeyAction): CommandDescriptor | undefined {
  return BY_ID.get(id);
}

/** Run an action through the one catalogue. Absent id: no-op (a stale stored keymap cannot bind nonsense). */
export function runCommand(id: KeyAction, ctx: CommandContext): void {
  if (ctx.simple) {
    const decision = SIMPLE_DECISIONS[id];
    if (decision === 'off') return;
    if (decision === 'opensFull') {
      ctx.event?.preventDefault();
      ctx.simple.openFull(id, ctx.event !== undefined);
      return;
    }
    if (id === 'pageEdit') return ctx.simple.setPlace('edit');
    if (id === 'pageDeliver') return ctx.simple.setPlace('export');
  }
  const ui = ctx.store.getState().ui;
  if (ui.viewerMode === 'source') {
    const onSource = ctx.source ? SOURCE_ACTIONS[id] : undefined;
    if (onSource && ctx.source) {
      ctx.event?.preventDefault();
      onSource(ctx.source);
      return;
    }
    // The timeline is not what the picture shows: a writer does nothing here (the palette says why).
    if (SOURCE_DECISIONS[id] === 'writer') {
      ctx.event?.preventDefault();
      return;
    }
    // A picture tool belongs to the timeline's picture: entering one returns to Record, then runs.
    if (COMPOSE_TOOL_ACTIONS.has(id)) ctx.store.setUi({ viewerMode: 'record' });
  }
  BY_ID.get(id)?.run(ctx);
}

/** Why a command cannot run in the current state (a catalogue key suffix), or null when it can. */
export type CommandUnavailable = 'nothingToUndo' | 'nothingToRedo' | 'nothingToPaste' | 'viewerNeedsPage' | 'noSource' | 'switchToRecord' | 'fullEditorOnly' | ComposeUnavailable;

export interface CommandAvailability {
  store: EditorStore;
  canUndo: boolean;
  canRedo: boolean;
  hasClipboard: boolean;
  /** Read-only and phone state, for the Compose actions (absent: writable desktop). */
  readOnly?: boolean;
  phone?: boolean;
  /** Whether a source is loaded for placement (the Source monitor's item on Cut or Edit). */
  hasSource?: boolean;
  /** The Simple shell shows: an action it switches off says so, and one that opens the full editor is always available. */
  simple?: boolean;
}

const NEEDS_SELECTION: ReadonlySet<KeyAction> = new Set<KeyAction>([
  'deleteSelection', 'liftSelection', 'cutClips', 'copyClips', 'duplicateClips', 'linkClips', 'unlinkClips',
  'nudgeBack', 'nudgeForward', 'nudgeBack10', 'nudgeForward10', 'moveTrackUp', 'moveTrackDown',
]);

/** `null` = available. The palette prints the translated reason for the rest. */
export function commandUnavailable(id: KeyAction, state: CommandAvailability): CommandUnavailable | null {
  if (state.simple) {
    if (SIMPLE_DECISIONS[id] === 'off') return 'fullEditorOnly';
    if (SIMPLE_DECISIONS[id] === 'opensFull') return null;
  }
  if (SOURCE_DECISIONS[id] === 'writer' && state.store.getState().ui.viewerMode === 'source') return 'switchToRecord';
  if (COMPOSE_ACTIONS.has(id)) return composeUnavailable(id, state.store, { readOnly: state.readOnly ?? false, phone: state.phone ?? false });
  if (id === 'toggleViewerMode') {
    if (state.phone) return 'desktopOnly';
    const ui = state.store.getState().ui;
    if (!VIEWER_SWITCH_PAGES.has(ui.workspace)) return 'viewerNeedsPage';
    return null;
  }
  if (id in PLACE_ACTIONS) {
    if (state.phone) return 'desktopOnly';
    if (state.readOnly) return 'readOnly';
    return state.hasSource ? null : 'noSource';
  }
  if (id === 'undo' && !state.canUndo) return 'nothingToUndo';
  if (id === 'redo' && !state.canRedo) return 'nothingToRedo';
  if (id === 'pasteClips' && !state.hasClipboard) return 'nothingToPaste';
  if (NEEDS_SELECTION.has(id) && state.store.getState().ui.selection.length === 0) return 'selectionRequired';
  return null;
}

