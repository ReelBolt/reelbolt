/**
 * P9: the editor's keymap — a closed catalogue of actions with NLE-standard default chords, a user's
 * overrides on top (stored in their editor preferences), and conflict detection.
 *
 * A chord is `Mod+Shift+Alt+Key` (any subset, in that order): `Mod` is Ctrl or ⌘, the key is the
 * PHYSICAL key (`KeyboardEvent.code`) normalised — `KeyB` → `B`, `Digit1` → `1`, `Comma` → `,` — so a
 * shortcut means the same key on every layout and Shift never changes which key it is (`Shift+,`,
 * not `<`). An override of `""` unbinds the action. Overrides for unknown actions are ignored.
 */

export type KeyAction =
  | 'playPause'
  | 'playInToOut'
  | 'shuttleReverse'
  | 'stop'
  | 'shuttleForward'
  | 'frameBack'
  | 'frameForward'
  | 'frameBack10'
  | 'frameForward10'
  | 'previousEdit'
  | 'nextEdit'
  | 'previousMarker'
  | 'nextMarker'
  | 'goToStart'
  | 'goToEnd'
  | 'markIn'
  | 'markOut'
  | 'clearInOut'
  | 'toolBlade'
  | 'toolSelect'
  | 'toolHand'
  | 'toolTransform'
  | 'toggleSnapping'
  | 'toggleEventsLane'
  | 'toggleEventSnap'
  | 'actOnEvent'
  | 'addMarker'
  | 'deleteSelection'
  | 'liftSelection'
  | 'nudgeBack'
  | 'nudgeForward'
  | 'nudgeBack10'
  | 'nudgeForward10'
  | 'deselect'
  | 'undo'
  | 'redo'
  | 'selectAll'
  | 'selectForward'
  | 'selectBackward'
  | 'selectAllOnTrack'
  | 'bladeAtPlayhead'
  | 'trimRipple'
  | 'trimRoll'
  | 'trimSlip'
  | 'trimSlide'
  | 'copyClips'
  | 'cutClips'
  | 'pasteClips'
  | 'duplicateClips'
  | 'moveTrackUp'
  | 'moveTrackDown'
  | 'linkClips'
  | 'unlinkClips'
  | 'toolMask'
  | 'toolReframe'
  | 'toolCornerPin'
  | 'maskShapeNext'
  | 'maskInvert'
  | 'composeRemoveEntry'
  | 'composeEntryPrevious'
  | 'composeEntryNext'
  | 'addAdjustmentClip'
  | 'placeOver'
  | 'pageMedia'
  | 'pageCut'
  | 'pageEdit'
  | 'pageCompose'
  | 'pageColour'
  | 'pageAudio'
  | 'pageDeliver'
  | 'toggleViewerMode'
  | 'viewerFullscreen'
  | 'placeInsert'
  | 'placeOverwrite'
  | 'placeAppend'
  | 'placeOnTop'
  | 'commandPalette';

export interface KeyActionInfo {
  id: KeyAction;
  label: string;
  group: 'Transport' | 'Navigate' | 'Marks' | 'Tools' | 'Edit' | 'Compose' | 'Pages';
  defaults: readonly string[];
}

/** The catalogue, in the order the keymap editor lists it. */
export const KEY_ACTIONS: readonly KeyActionInfo[] = Object.freeze([
  { id: 'playPause', label: 'Play / pause', group: 'Transport', defaults: ['Space'] },
  { id: 'playInToOut', label: 'Play in to out', group: 'Transport', defaults: ['Shift+Space'] },
  { id: 'shuttleReverse', label: 'Shuttle reverse', group: 'Transport', defaults: ['J'] },
  { id: 'stop', label: 'Stop', group: 'Transport', defaults: ['K'] },
  { id: 'shuttleForward', label: 'Shuttle forward', group: 'Transport', defaults: ['L'] },
  { id: 'frameBack', label: 'Back one frame', group: 'Navigate', defaults: ['ArrowLeft'] },
  { id: 'frameForward', label: 'Forward one frame', group: 'Navigate', defaults: ['ArrowRight'] },
  { id: 'frameBack10', label: 'Back ten frames', group: 'Navigate', defaults: ['Shift+ArrowLeft'] },
  { id: 'frameForward10', label: 'Forward ten frames', group: 'Navigate', defaults: ['Shift+ArrowRight'] },
  { id: 'previousEdit', label: 'Previous edit', group: 'Navigate', defaults: ['ArrowUp'] },
  { id: 'nextEdit', label: 'Next edit', group: 'Navigate', defaults: ['ArrowDown'] },
  { id: 'previousMarker', label: 'Previous marker', group: 'Navigate', defaults: ['Shift+ArrowUp'] },
  { id: 'nextMarker', label: 'Next marker', group: 'Navigate', defaults: ['Shift+ArrowDown'] },
  { id: 'goToStart', label: 'Go to start', group: 'Navigate', defaults: ['Home'] },
  { id: 'goToEnd', label: 'Go to end', group: 'Navigate', defaults: ['End'] },
  { id: 'markIn', label: 'Mark in', group: 'Marks', defaults: ['I'] },
  { id: 'markOut', label: 'Mark out', group: 'Marks', defaults: ['O'] },
  { id: 'clearInOut', label: 'Clear in and out', group: 'Marks', defaults: ['Alt+X'] },
  { id: 'addMarker', label: 'Add marker', group: 'Marks', defaults: ['M'] },
  { id: 'toolBlade', label: 'Blade tool', group: 'Tools', defaults: ['B'] },
  { id: 'toolSelect', label: 'Select tool', group: 'Tools', defaults: ['V'] },
  { id: 'toolHand', label: 'Hand tool', group: 'Tools', defaults: ['H'] },
  { id: 'toolTransform', label: 'Picture handles on the viewer (move, resize, rotate)', group: 'Tools', defaults: ['Shift+T'] },
  { id: 'trimRipple', label: 'Trim mode: ripple (default)', group: 'Tools', defaults: ['R'] },
  { id: 'trimRoll', label: 'Trim mode: roll', group: 'Tools', defaults: ['T'] },
  { id: 'trimSlip', label: 'Trim mode: slip', group: 'Tools', defaults: ['Y'] },
  { id: 'trimSlide', label: 'Trim mode: slide', group: 'Tools', defaults: ['U'] },
  { id: 'toggleSnapping', label: 'Snapping on/off', group: 'Tools', defaults: ['N'] },
  { id: 'toggleEventsLane', label: 'Events lane on/off', group: 'Tools', defaults: ['E'] },
  { id: 'toggleEventSnap', label: 'Snap to events on/off', group: 'Tools', defaults: ['Shift+N'] },
  { id: 'actOnEvent', label: 'Act on the event at the playhead', group: 'Tools', defaults: ['Shift+E'] },
  { id: 'deleteSelection', label: 'Delete selection (closes the gap)', group: 'Edit', defaults: ['Delete', 'Backspace'] },
  { id: 'liftSelection', label: 'Lift selection (leaves a gap)', group: 'Edit', defaults: ['Shift+Delete', 'Shift+Backspace'] },
  { id: 'bladeAtPlayhead', label: 'Cut at playhead', group: 'Edit', defaults: ['Mod+B'] },
  { id: 'nudgeBack', label: 'Nudge left one frame', group: 'Edit', defaults: [','] },
  { id: 'nudgeForward', label: 'Nudge right one frame', group: 'Edit', defaults: ['.'] },
  { id: 'nudgeBack10', label: 'Nudge left ten frames', group: 'Edit', defaults: ['Shift+,'] },
  { id: 'nudgeForward10', label: 'Nudge right ten frames', group: 'Edit', defaults: ['Shift+.'] },
  { id: 'copyClips', label: 'Copy clips', group: 'Edit', defaults: ['Mod+C'] },
  { id: 'cutClips', label: 'Cut clips (copy, then delete)', group: 'Edit', defaults: ['Mod+X'] },
  { id: 'pasteClips', label: 'Paste clips at the playhead', group: 'Edit', defaults: ['Mod+V'] },
  { id: 'duplicateClips', label: 'Duplicate clips', group: 'Edit', defaults: ['Mod+D'] },
  { id: 'moveTrackUp', label: 'Move clip to the track above', group: 'Edit', defaults: ['Alt+ArrowUp'] },
  { id: 'moveTrackDown', label: 'Move clip to the track below', group: 'Edit', defaults: ['Alt+ArrowDown'] },
  { id: 'linkClips', label: 'Link selected clips (picture and sound)', group: 'Edit', defaults: ['Mod+Alt+L'] },
  { id: 'unlinkClips', label: 'Unlink selected clips', group: 'Edit', defaults: ['Mod+Shift+Alt+L'] },
  { id: 'deselect', label: 'Deselect', group: 'Edit', defaults: ['Escape'] },
  { id: 'undo', label: 'Undo', group: 'Edit', defaults: ['Mod+Z'] },
  { id: 'redo', label: 'Redo', group: 'Edit', defaults: ['Mod+Shift+Z', 'Mod+Y'] },
  { id: 'selectAll', label: 'Select all', group: 'Edit', defaults: ['Mod+A'] },
  { id: 'selectForward', label: 'Select forward from playhead', group: 'Edit', defaults: ['A'] },
  { id: 'selectBackward', label: 'Select backward from playhead', group: 'Edit', defaults: ['Shift+A'] },
  { id: 'selectAllOnTrack', label: 'Select all on track', group: 'Edit', defaults: ['Alt+A'] },
  { id: 'toolMask', label: 'Mask tool', group: 'Compose', defaults: ['Shift+M'] },
  { id: 'toolReframe', label: 'Reframe tool', group: 'Compose', defaults: ['Shift+F'] },
  { id: 'toolCornerPin', label: 'Corner pin tool', group: 'Compose', defaults: ['Shift+P'] },
  { id: 'maskShapeNext', label: 'Mask: switch between rectangle and ellipse', group: 'Compose', defaults: ['Shift+S'] },
  { id: 'maskInvert', label: 'Mask: invert', group: 'Compose', defaults: ['Shift+I'] },
  { id: 'composeRemoveEntry', label: 'Compose: remove the selected entry', group: 'Compose', defaults: ['Alt+Backspace'] },
  { id: 'composeEntryPrevious', label: 'Compose: previous entry', group: 'Compose', defaults: ['['] },
  { id: 'composeEntryNext', label: 'Compose: next entry', group: 'Compose', defaults: [']'] },
  { id: 'addAdjustmentClip', label: 'Add adjustment clip', group: 'Compose', defaults: ['Mod+Alt+A'] },
  { id: 'placeOver', label: 'Place over (picture-in-picture)', group: 'Edit', defaults: ['Mod+Alt+P'] },
  { id: 'pageMedia', label: 'Go to Media', group: 'Pages', defaults: ['Shift+1'] },
  { id: 'pageCut', label: 'Go to Cut', group: 'Pages', defaults: ['Shift+2'] },
  { id: 'pageEdit', label: 'Go to Edit', group: 'Pages', defaults: ['Shift+3'] },
  { id: 'pageCompose', label: 'Go to Compose', group: 'Pages', defaults: ['Shift+4'] },
  { id: 'pageColour', label: 'Go to Colour', group: 'Pages', defaults: ['Shift+5'] },
  { id: 'pageAudio', label: 'Go to Audio', group: 'Pages', defaults: ['Shift+6'] },
  { id: 'pageDeliver', label: 'Go to Deliver', group: 'Pages', defaults: ['Shift+7'] },
  { id: 'toggleViewerMode', label: 'Viewer: switch between Source and Record', group: 'Navigate', defaults: ['Shift+V'] },
  { id: 'viewerFullscreen', label: 'Viewer: full screen', group: 'Navigate', defaults: ['P'] },
  { id: 'placeInsert', label: 'Place the source: insert', group: 'Edit', defaults: ['F9'] },
  { id: 'placeOverwrite', label: 'Place the source: overwrite', group: 'Edit', defaults: ['F10'] },
  { id: 'placeAppend', label: 'Place the source: add to end', group: 'Edit', defaults: ['F8'] },
  { id: 'placeOnTop', label: 'Place the source: place on top', group: 'Edit', defaults: ['Shift+F8'] },
  { id: 'commandPalette', label: 'Command palette', group: 'Edit', defaults: ['Mod+K'] },
] as const);

const CODE_KEYS: Record<string, string> = { Comma: ',', Period: '.', Slash: '/', Semicolon: ';', Quote: "'", BracketLeft: '[', BracketRight: ']', Minus: '-', Equal: '=', Backquote: '`', Backslash: '\\' };

/** The normalised key of an event: the physical key where the browser reports one, else the key. */
export function keyOf(event: Pick<KeyboardEvent, 'code' | 'key'>): string {
  const code = event.code ?? '';
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit[0-9]$/.test(code)) return code.slice(5);
  if (/^Numpad[0-9]$/.test(code)) return code.slice(6);
  if (CODE_KEYS[code]) return CODE_KEYS[code];
  if (code === 'Space') return 'Space';
  if (code.length > 0 && !code.startsWith('Shift') && !code.startsWith('Control') && !code.startsWith('Alt') && !code.startsWith('Meta')) return code;
  const key = event.key ?? '';
  if (key === ' ') return 'Space';
  if (key.length === 1) return key.toUpperCase();
  return key;
}

/** The chord an event spells, e.g. `Mod+Shift+Z`. */
export function eventToChord(event: Pick<KeyboardEvent, 'code' | 'key' | 'ctrlKey' | 'metaKey' | 'shiftKey' | 'altKey'>): string {
  const parts: string[] = [];
  if (event.ctrlKey || event.metaKey) parts.push('Mod');
  if (event.shiftKey) parts.push('Shift');
  if (event.altKey) parts.push('Alt');
  parts.push(keyOf(event));
  return parts.join('+');
}

/** Whether a chord is only modifiers (not bindable). */
export const isModifierOnly = (chord: string): boolean => /(^|\+)(Shift|Control|Alt|Meta|Mod)$/.test(chord) || chord.endsWith('+') || chord === '';

export type Keymap = ReadonlyMap<string, KeyAction>;

/** Each action's effective chords: the override when one is set (`""` = none), else the defaults. */
export function effectiveChords(overrides: Readonly<Record<string, string>>): Record<KeyAction, string[]> {
  const out = {} as Record<KeyAction, string[]>;
  for (const action of KEY_ACTIONS) {
    const override = overrides[action.id];
    out[action.id] = override === undefined ? [...action.defaults] : override === '' ? [] : [override];
  }
  return out;
}

/**
 * Chord → action, built from the effective chords. A chord the user set themselves always beats a default
 * (a new default chord must never silently take a remap's key); among equals the action listed first wins.
 * `conflicts` still reports every double binding.
 */
export function buildKeymap(overrides: Readonly<Record<string, string>>): Keymap {
  const map = new Map<string, KeyAction>();
  const chords = effectiveChords(overrides);
  for (const pass of ['override', 'default'] as const) {
    for (const action of KEY_ACTIONS) {
      if ((overrides[action.id] !== undefined) !== (pass === 'override')) continue;
      for (const chord of chords[action.id]) if (!map.has(chord)) map.set(chord, action.id);
    }
  }
  return map;
}

/**
 * The chords that really run each action: {@link effectiveChords} minus a chord another action won. Controls
 * advertise these (tooltips, `aria-keyshortcuts`), so a beaten default is never shown as working.
 */
export function liveChords(overrides: Readonly<Record<string, string>>): Record<KeyAction, string[]> {
  const map = buildKeymap(overrides);
  const out = {} as Record<KeyAction, string[]>;
  const chords = effectiveChords(overrides);
  for (const action of KEY_ACTIONS) out[action.id] = chords[action.id].filter((chord) => map.get(chord) === action.id);
  return out;
}

/** Every chord bound to more than one action. */
export function conflicts(overrides: Readonly<Record<string, string>>): { chord: string; actions: KeyAction[] }[] {
  const byChord = new Map<string, KeyAction[]>();
  const chords = effectiveChords(overrides);
  for (const action of KEY_ACTIONS) for (const chord of chords[action.id]) byChord.set(chord, [...(byChord.get(chord) ?? []), action.id]);
  return [...byChord.entries()].filter(([, actions]) => actions.length > 1).map(([chord, actions]) => ({ chord, actions }));
}

/** The overrides with unknown actions dropped (so a stale stored keymap cannot bind nonsense). */
export function sanitizeOverrides(raw: unknown): Record<string, string> {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const known = new Set<string>(KEY_ACTIONS.map((a) => a.id));
  const out: Record<string, string> = {};
  for (const [key, value] of Object.entries(raw as Record<string, unknown>)) if (known.has(key) && typeof value === 'string' && value.length <= 32) out[key] = value;
  return out;
}

/** The chord as the help line shows it (`Mod` as Ctrl/⌘). */
export function displayChord(chord: string, mac = false): string {
  return chord.replace(/^Mod\+/, mac ? '⌘' : 'Ctrl+').replace('Arrow', '').replace('Space', '␣');
}

/**
 * The chord in the `aria-keyshortcuts` grammar: `Mod` becomes `Control` (or `Meta` on a Mac), `Space`
 * stays a word, and no glyph is used. Kept separate from {@link displayChord} because the attribute must
 * be machine-readable, not the visible copy.
 */
export function ariaChord(chord: string, mac = false): string {
  return chord
    .split('+')
    .map((part) => (part === 'Mod' ? (mac ? 'Meta' : 'Control') : part === 'Space' ? 'Space' : part))
    .join('+');
}

const KEYBOARD_OWNING_ROLES = '[role="slider"],[role="spinbutton"],[role="textbox"],[role="combobox"],[role="listbox"],[role="option"],[role="menu"],[role="menuitem"],[role="radio"]';
const ACTIVATABLE = 'button,a[href],summary,[role="button"],[role="checkbox"],[role="switch"],[role="tab"]';
/**
 * A list that opts in with `data-editor-shortcuts="through"` (the Compose list) keeps only the keys it moves
 * with; every other editor chord, Escape and Space included, goes through to the editor. Enter stays with the
 * focused entry (it activates it).
 */
const SHORTCUTS_THROUGH = '[data-editor-shortcuts="through"]';
const LIST_OWN_KEYS = /^(ArrowUp|ArrowDown|Home|End|Enter)$/;
/**
 * `data-editor-own-keys` picks which keys a through-host keeps: unset = a list's (arrows, Home, End, Enter);
 * `radio` = a radio group's arrows, kept only while the focus came from the keyboard (a mouse click leaves the
 * radio focused and its arrows must still step frames) plus Enter; `slider` = a slider's arrows, Home, End and Page keys; `activate` = Enter alone (a lone button: Space,
 * J, K, L and the rest go through).
 */
const SLIDER_KEYS = /^(Arrow(Left|Right|Up|Down)|Home|End|PageUp|PageDown)$/;
const RADIO_ARROWS = /^Arrow(Left|Right|Up|Down)$/;
function ownKeys(host: Element, element: Element, chord: string): boolean {
  const kind = host.getAttribute('data-editor-own-keys');
  if (kind === 'activate') return chord === 'Enter';
  if (kind === 'slider') return SLIDER_KEYS.test(chord);
  if (kind === 'radio') {
    if (chord === 'Enter') return true;
    if (!RADIO_ARROWS.test(chord)) return false;
    try {
      return element.matches(':focus-visible');
    } catch {
      return true;
    }
  }
  return LIST_OWN_KEYS.test(chord);
}
/** A text-entry control: still blocked inside a through-host (only a radio or checkbox input is not text). */
function takesText(element: HTMLElement): boolean {
  const tag = element.tagName;
  if (tag === 'TEXTAREA' || tag === 'SELECT' || element.isContentEditable) return true;
  if (tag !== 'INPUT') return false;
  const type = (element as HTMLInputElement).type;
  return type !== 'radio' && type !== 'checkbox' && type !== 'button';
}
/** A focused tab keeps only the keys its tab list moves with; every other shortcut goes through. */
const TAB_LIST_KEYS = /(^|\+)(ArrowLeft|ArrowRight|ArrowUp|ArrowDown|Home|End)$/;

/**
 * W0.6 (UI2-09): whether a global editor shortcut must NOT fire for a key press on `target`:
 * while a modal dialog is open (the shortcut would edit the timeline behind it), inside any dialog,
 * in a text field or a control that owns its keys (slider, menu, combobox…), the arrow/Home/End keys
 * on a focused tab, or Space/Enter on a focused button, link or tab (that key activates it).
 */
export function shortcutBlocked(target: EventTarget | null, chord: string): boolean {
  const doc = typeof document === 'undefined' ? null : document;
  if (doc?.querySelector('[role="dialog"][aria-modal="true"],dialog[open]')) return true;
  const element = target && typeof (target as Element).closest === 'function' ? (target as HTMLElement) : null;
  if (!element) return false;
  const tag = element.tagName;
  const through = element.closest(SHORTCUTS_THROUGH);
  if (through && !takesText(element) && !element.closest('[role="dialog"],[role="alertdialog"],dialog')) return ownKeys(through, element, chord);
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || element.isContentEditable) return true;
  if (element.closest('[role="dialog"],[role="alertdialog"],dialog')) return true;
  if (element.closest(KEYBOARD_OWNING_ROLES)) return true;
  if (TAB_LIST_KEYS.test(chord) && element.closest('[role="tab"]')) return true;
  if ((chord === 'Space' || chord === 'Enter') && element.closest(ACTIVATABLE)) return true;
  return false;
}

/** "?" (Shift+/) with no Ctrl, Meta or Alt: opens the keyboard shortcuts. Callers check `shortcutBlocked` first. */
export function isHelpKey(event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey'>): boolean {
  return event.key === '?' && !event.ctrlKey && !event.metaKey && !event.altKey;
}
