import { KEY_ACTIONS, type KeyAction } from './keymap';

/**
 * R12.6: every keyboard action has a way to do it with a finger, or is marked keyboard-only here with the
 * reason. `__tests__/keymap-touch.test.ts` fails when an action in `keymap.ts` is in neither list, so a new
 * shortcut cannot ship without deciding what a phone does about it.
 */
export type TouchSurface = 'Action bar' | 'Trim sheet' | 'More sheet' | 'Transport' | 'Timeline toolbar' | 'Header' | 'Select mode' | 'Dock';

export type TouchParityEntry =
  | { kind: 'touch'; surface: TouchSurface; /** The visible control's plain label. */ control: string }
  | { kind: 'keyboard-only'; /** Plain sentence for the docs. */ reason: string };

const touch = (surface: TouchSurface, control: string): TouchParityEntry => ({ kind: 'touch', surface, control });
const keys = (reason: string): TouchParityEntry => ({ kind: 'keyboard-only', reason });

export const TOUCH_PARITY: Readonly<Record<KeyAction, TouchParityEntry>> = Object.freeze({
  playPause: touch('Transport', 'Play / Pause'),
  playInToOut: keys('Loop in to out is the Loop button; the shifted play shortcut has no separate touch control.'),
  shuttleReverse: keys('Shuttle (J, K, L) is for keyboards; Play and Pause cover the phone.'),
  stop: keys('Pause stops playback.'),
  shuttleForward: keys('Shuttle (J, K, L) is for keyboards; Play and Pause cover the phone.'),
  frameBack: touch('Transport', 'Step back one frame'),
  frameForward: touch('Transport', 'Step forward one frame'),
  frameBack10: keys('The transport steps one frame; drag the strip to move the playhead further.'),
  frameForward10: keys('The transport steps one frame; drag the strip to move the playhead further.'),
  previousEdit: touch('Transport', 'Previous edit'),
  nextEdit: touch('Transport', 'Next edit'),
  previousMarker: touch('More sheet', 'Previous marker'),
  nextMarker: touch('More sheet', 'Next marker'),
  goToStart: touch('Transport', 'Go to start'),
  goToEnd: touch('Transport', 'Go to end'),
  markIn: touch('Transport', 'Mark in'),
  markOut: touch('Transport', 'Mark out'),
  clearInOut: touch('More sheet', 'Clear in and out'),
  addMarker: touch('Action bar', 'Marker'),
  toolBlade: touch('Timeline toolbar', 'Blade tool'),
  toolSelect: touch('Timeline toolbar', 'Select tool'),
  toolHand: touch('Timeline toolbar', 'Pan tool'),
  toolTransform: keys('The viewer tool dock button turns the picture handles on and off on a larger screen; on a phone tapping the picture always selects it.'),
  trimRipple: touch('Trim sheet', 'Ripple'),
  trimRoll: touch('Trim sheet', 'Roll'),
  trimSlip: touch('Trim sheet', 'Slip'),
  trimSlide: touch('Trim sheet', 'Slide'),
  toggleSnapping: touch('More sheet', 'Snapping'),
  toggleEventsLane: touch('More sheet', 'Events'),
  toggleEventSnap: keys('The phone has no Events lane to snap to; its Events sheet lists the events around the playhead.'),
  actOnEvent: touch('More sheet', 'Events, then Act on this event'),
  deleteSelection: touch('Action bar', 'Delete'),
  liftSelection: touch('More sheet', 'Lift (leave a gap)'),
  bladeAtPlayhead: touch('Action bar', 'Split'),
  nudgeBack: touch('Action bar', 'Nudge'),
  nudgeForward: touch('Action bar', 'Nudge'),
  nudgeBack10: touch('Action bar', 'Nudge'),
  nudgeForward10: touch('Action bar', 'Nudge'),
  copyClips: touch('More sheet', 'Copy'),
  cutClips: touch('More sheet', 'Cut'),
  pasteClips: touch('More sheet', 'Paste at the playhead'),
  duplicateClips: touch('Action bar', 'Duplicate'),
  moveTrackUp: keys('Moving a clip to another track is for a larger screen; Move earlier and Move later keep a clip on its track.'),
  moveTrackDown: keys('Moving a clip to another track is for a larger screen; Move earlier and Move later keep a clip on its track.'),
  linkClips: touch('More sheet', 'Link picture and sound'),
  unlinkClips: touch('More sheet', 'Unlink'),
  deselect: touch('Select mode', 'Clear'),
  undo: touch('Header', 'Undo'),
  redo: touch('Header', 'Redo'),
  selectAll: touch('Timeline toolbar', 'Select, Select all'),
  selectForward: touch('Timeline toolbar', 'Select, Select forward from the playhead'),
  selectBackward: touch('Timeline toolbar', 'Select, Select backward from the playhead'),
  selectAllOnTrack: touch('Timeline toolbar', 'Select, Select all on this track'),
  toolMask: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  toolReframe: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  toolCornerPin: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  maskShapeNext: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  maskInvert: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  composeRemoveEntry: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  composeEntryPrevious: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  composeEntryNext: keys('Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page.'),
  placeOver: touch('Action bar', 'Add media, then Place over'),
  addAdjustmentClip: keys('Adjustment clips are added from the Compose page on a larger screen.'),
  pageMedia: touch('Dock', 'Media'),
  pageCut: keys('Cut, Compose and Colour are desktop pages; the phone\'s dock has Media, Edit, Audio, Look and fix, Captions.'),
  pageEdit: touch('Dock', 'Edit'),
  pageCompose: keys('Cut, Compose and Colour are desktop pages; the phone\'s dock has Media, Edit, Audio, Look and fix, Captions.'),
  pageColour: keys('Cut, Compose and Colour are desktop pages; the phone\'s dock has Media, Edit, Audio, Look and fix, Captions.'),
  pageAudio: touch('Dock', 'Audio'),
  toggleViewerMode: keys('The Source | Record switch is a desktop and tablet viewer feature; the phone has one viewer and no source monitor.'),
  viewerFullscreen: keys('Full screen is for the desktop and tablet viewer; on a phone the picture already uses the width, and turning the phone makes it larger.'),
  placeInsert: keys('On the phone, the Add media sheet has Insert and Add to end buttons; the four placement chords are for a keyboard.'),
  placeOverwrite: keys('On the phone, the Add media sheet has Insert and Add to end buttons; the four placement chords are for a keyboard.'),
  placeAppend: keys('On the phone, the Add media sheet has Insert and Add to end buttons; the four placement chords are for a keyboard.'),
  placeOnTop: keys('On the phone, the Add media sheet has Insert and Add to end buttons; the four placement chords are for a keyboard.'),
  pageDeliver: touch('Header', 'Export'),
  commandPalette: keys('A command palette needs a keyboard; the phone\'s More sheet lists actions, it does not search commands.'),
});

/** The actions a phone cannot do by touch, in the keymap's order, with the plain reason (for the docs). */
export function keyboardOnlyActions(): { id: KeyAction; label: string; reason: string }[] {
  return KEY_ACTIONS.flatMap((a) => {
    const entry = TOUCH_PARITY[a.id];
    return entry.kind === 'keyboard-only' ? [{ id: a.id, label: a.label, reason: entry.reason }] : [];
  });
}
