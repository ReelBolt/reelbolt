import type { CameraProfileId } from '@/lib/editing/color/camera-profiles';
import type { EditOp, PropertyKey, PropertyValue, TrimEdge, TrimMode } from '@/lib/editing/engine';
import type { EditorStore } from '@/lib/editing/store';
import type { BlendMode, EditTimeline, Item, MediaClip, Rational } from '@/lib/editing/types';
import { expandSelection, groupOf } from '@/lib/editing/link';
import { toggleItem } from '@/lib/editing/selection';
import { dbToVolumePercent } from '@/lib/editing/clip-depth';
import type { TitleClip, TitleTemplate } from '@/lib/editing/types';
import type { LookChange, LookState } from './look-actions';
import type { ClipSheetInfo, PhoneChannel } from './clip-sheet-actions';

/** The clip the trim, speed and volume sheets act on. */
export interface PhoneClipInfo {
  item: Item;
  name: string;
  /** A video or sound clip (can be sped up, has a source in-point). */
  isMedia: boolean;
  /** Its sound is on a picture track (no volume until the sound is extracted). */
  onPictureTrack: boolean;
  speed: number;
  /** The volume of the clip's sound (the paired sound clip for a picture clip), or null when it has no playable sound clip. */
  volumePercent: number | null;
  linked: boolean;
  /** How the clip blends with the pictures below it (a picture clip only). */
  blendMode?: BlendMode;
}

/** RM.10: the "Look and fix" sheet. Every method returns a plain sentence when refused, else null. */
export interface PhoneLookActions {
  state: LookState | null;
  /** The target's source is HDR: the render tone-maps it and ignores a camera profile, so the row is hidden. */
  hdr: boolean;
  /** The selected title clip, if the selection is one. */
  title: TitleClip | null;
  /** Compare: the preview shows the pictures without their grades. */
  bypass: boolean;
  setBypass: (on: boolean) => void;
  change: (change: LookChange) => string | null;
  applyToAll: () => string | null;
  /** The camera-profile row: sets (or, with null, turns off) the conversion on the target clip. One undo step. */
  setProfile: (profile: CameraProfileId | null) => string | null;
  addTitle: (text: string, template: TitleTemplate) => string | null;
  setTitleText: (text: string) => string | null;
  setTitleTemplate: (template: TitleTemplate) => string | null;
}

/** R07.7: the phone clip sheet. Every method returns a plain sentence when refused, else null. */
export interface PhoneClipSheetActions {
  info: ClipSheetInfo | null;
  fitFill: (kind: 'fit' | 'fill') => string | null;
  /** Turn the picture a quarter turn clockwise (0 -> 90 -> 180 -> 270 -> 0). */
  rotate: () => string | null;
  pad: (col: -1 | 0 | 1, row: -1 | 0 | 1) => string | null;
  /** Position X / Y and size, as a percent. */
  setChannel: (channel: Exclude<PhoneChannel, 'opacity'>, percent: number) => string | null;
  setOpacity: (percent: number) => string | null;
  /** The Blend row: how the clip's picture blends with the pictures below it. One undo step. */
  setBlendMode?: (mode: BlendMode) => string | null;
  setFade: (which: 'in' | 'out', seconds: number) => string | null;
  /** Magic blur: the project and ONE undoable `setProperty` on the selected clip (a plain sentence when refused). */
  objects?: {
    projectId: string | null;
    setProperty: (key: PropertyKey, value: PropertyValue, label?: string) => string | null;
  };
}

/** What the phone's Events sheet needs to plan and run an action on an event (the same controller the desktop lane uses). */
export interface PhoneEventsHost {
  store: EditorStore;
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  readOnly: boolean;
  /** Applies a program as one undo step; false when refused (the sentence is shown by the host). */
  apply: (ops: readonly EditOp[], label: string) => boolean;
  /** Opens the Media page with the playhead at the frame, so a sound can be placed there. */
  openSounds?: (frame: number) => void;
  isLocked: (trackId: string) => boolean;
}

/** Everything the phone action bar and its sheets can do; built once from the editor's own handlers (see usePhoneActions). */
export interface PhoneActions {
  readOnly: boolean;
  rate: Rational;
  selection: readonly string[];
  /** The one clip (or one linked picture and sound pair) the selection is, else null. */
  clip: PhoneClipInfo | null;
  canSplit: boolean;
  selectMode: boolean;
  setSelectMode: (on: boolean) => void;
  clearSelection: () => void;
  snapping: boolean;
  trimMode: TrimMode;
  setTrimMode: (mode: TrimMode) => void;
  addText: () => void;
  addMarker: () => void;
  split: () => void;
  remove: () => void;
  duplicate: () => void;
  lift: () => void;
  nudge: (deltaFrames: number) => void;
  moveInOrder: (direction: -1 | 1) => void;
  extractAudio: () => void;
  link: () => void;
  unlink: () => void;
  freezeFrame: () => void;
  copy: () => void;
  cut: () => void;
  paste: () => void;
  previousMarker: () => void;
  nextMarker: () => void;
  clearInOut: () => void;
  toggleSnapping: () => void;
  /** A frame stepper on the trim sheet. A plain sentence when refused, else null. */
  trimStep: (edge: TrimEdge, deltaFrames: number) => string | null;
  /** Typed in / out / duration. A plain sentence when refused, else null. */
  trimNumeric: (field: 'sourceIn' | 'recordOut' | 'duration', frames: number) => string | null;
  /** Called when the trim sheet opens; returns the history position to fold back to. */
  beginTrim: () => number;
  /** Called when the trim sheet closes: the session becomes one undo step. */
  endTrim: (start: number) => void;
  setSpeed: (speed: number) => string | null;
  setVolume: (percent: number) => string | null;
  look: PhoneLookActions;
  clipSheet: PhoneClipSheetActions;
  /** The Events sheet's host; absent where there is no timeline to read events for. */
  events?: PhoneEventsHost;
}

/** The clip the selection stands for: one clip, or one linked group (picture and its sound). */
export function clipOfSelection(doc: EditTimeline, selection: readonly string[]): PhoneClipInfo | null {
  if (selection.length === 0) return null;
  const first = selection[0];
  const group = new Set(expandSelection(doc, [first]));
  if (selection.length !== group.size || selection.some((id) => !group.has(id))) return null;
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === first);
    if (!item) continue;
    const media = item.type === 'mediaClip' ? (item as MediaClip) : null;
    const sound = track.kind === 'audio' ? media : ((groupOf(doc, first).find((m) => m.track.kind === 'audio' && m.item.type === 'mediaClip')?.item as MediaClip | undefined) ?? null);
    return {
      item,
      name: item.name || 'Clip',
      isMedia: media !== null,
      onPictureTrack: track.kind === 'video',
      speed: media?.playbackRate ?? 1,
      volumePercent: sound ? dbToVolumePercent(typeof sound.gainDb?.value === 'number' ? sound.gainDb.value : 0) : null,
      linked: group.size > 1,
      blendMode: media?.blendMode,
    };
  }
  return null;
}

/** Whether the playhead is strictly inside one of the selected clips (a split needs that). */
export function canSplitAt(doc: EditTimeline, selection: readonly string[], playheadFrame: number): boolean {
  const ids = new Set(selection);
  return doc.tracks.some((t) => t.items.some((i) => ids.has(i.id) && i.recordIn < playheadFrame && playheadFrame < i.recordOut));
}

/**
 * Select mode (RM.5): a tap on a clip adds it to the selection or takes it out (what shift-click does on a
 * desktop), a linked partner comes and goes with it. A tap on empty lane space changes nothing: only the Clear
 * button throws the group away.
 */
export function selectModeTap(doc: EditTimeline, selection: readonly string[], itemId: string | null): string[] | null {
  return itemId === null ? null : toggleItem(doc, selection, itemId);
}
