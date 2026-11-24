/**
 * W0.6: the one mapper from the edit validator's refusals to plain sentences (d3 N1/N3, d1 N8).
 *
 * The engine's messages are written for developers: they carry the op name ("setProperty: …"),
 * ids, the raw JSON of the refused value and sometimes "[object Object]". Every "Edit refused" toast
 * passes its error through {@link refusalSentence}, which
 *   - says the common refusals in plain words (an out-of-range field names its range, a locked
 *     track names the track, a vanished clip says so);
 *   - for everything else, drops op names and raw values and swaps every id the document knows for
 *     its name ("“Interviews”"); an id it does not know is dropped rather than shown.
 * Pure: no UI, no store.
 */

import { KEYFRAME_PARAM_RANGE, type EditOp, type KeyframeParam } from './engine';
import type { EditTimeline, Track } from './types';

const OP_NAMES = [
  'addMarker', 'addTrack', 'addTransition', 'append', 'blade', 'closeGaps', 'deleteGap', 'extract', 'fitToFill', 'insert', 'join', 'lift', 'move',
  'moveKeyframe', 'moveTrack', 'nudge', 'overwrite', 'placeOnTop', 'removeKeyframe', 'removeMarker', 'removeTrack', 'removeTransition', 'replace',
  'retime', 'setBus', 'setColorGroup', 'setInOut', 'setKeyframe', 'setMarker', 'setProperty', 'setSequence', 'setTimelineSettings', 'setTrackProperty', 'trim',
];
const OP_PREFIX = new RegExp(`\\b(?:${OP_NAMES.join('|')}): `, 'g');
const GUID_SOURCE = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const IS_GUID = new RegExp(`^${GUID_SOURCE}$`, 'i');
const ANY_GUID = new RegExp(GUID_SOURCE, 'gi');

/** The words a person sees in the Inspector and mixer for each field key. */
const FIELD_LABELS: Readonly<Record<string, string>> = {
  name: 'Name', enabled: 'Enabled', text: 'Text', fontSize: 'Font size', fontFamily: 'Font', color: 'Colour', language: 'Language',
  playbackRate: 'Speed', sourceIn: 'Source in', gainDb: 'Clip gain', transform: 'Transform', crop: 'Crop', reverse: 'Reverse', sourceRotation: 'Source rotation', hold: 'Freeze frame',
  fadeInFrames: 'Fade in', fadeOutFrames: 'Fade out', transitionType: 'Transition type', easing: 'Easing', generatorType: 'Generator type',
  generatorConfig: 'Generator settings', assetRef: 'Source', grade: 'Colour grade', mask: 'Mask', keyer: 'Keyer', effects: 'Effects', regionEffects: 'Region effects',
  stabilize: 'Stabilise', reframe: 'Reframe', retimeQuality: 'Retime quality', cornerPin: 'Corner pin', matte: 'Matte', quad: 'Quad', template: 'Template',
  channelMap: 'Channel map', sequenceId: 'Sequence', activeAngleId: 'Angle', cuts: 'Cuts', angles: 'Angles',
  inputProfile: 'Input profile', blendMode: 'Blend mode',
  positionX: 'Position X', positionY: 'Position Y', scale: 'Scale', rotation: 'Rotation', opacity: 'Opacity',
  height: 'Height', locked: 'Lock', mute: 'Mute', solo: 'Solo', busId: 'Bus', pan: 'Pan', eq: 'EQ', dynamics: 'Dynamics', denoise: 'Noise reduction', leveler: 'Leveller',
};
const label = (key: string): string => FIELD_LABELS[key] ?? key;

/** Every id the document knows (tracks, items, markers, sequences, buses, colour groups) → its name. */
function namesOf(doc: EditTimeline | null | undefined): Map<string, string> {
  const names = new Map<string, string>();
  if (!doc) return names;
  const addTracks = (tracks: readonly Track[]) => {
    for (const t of tracks) {
      names.set(t.id, t.name);
      for (const item of t.items) names.set(item.id, item.name);
    }
  };
  addTracks(doc.tracks);
  for (const m of doc.markers ?? []) names.set(m.id, m.name);
  for (const s of doc.sequences ?? []) {
    names.set(s.id, s.name);
    addTracks(s.tracks);
  }
  for (const b of doc.buses ?? []) names.set(b.id, b.name);
  for (const g of doc.colorGroups ?? []) names.set(g.id, g.name);
  return names;
}

const sentence = (text: string): string => {
  const t = text.replace(/\s+/g, ' ').trim().replace(/[\s,;:]+$/, '');
  if (t.length === 0) return 'The edit was refused.';
  const s = t.charAt(0).toUpperCase() + t.slice(1);
  return /[.!?]$/.test(s) ? s : `${s}.`;
};

const rangeSentence = (param: KeyframeParam): string => {
  const r = KEYFRAME_PARAM_RANGE[param];
  if (param === 'gainDb') return `Clip gain must be between ${r.min} and ${r.max} dB.`;
  return r.exclusiveMin ? `${label(param)} must be more than ${r.min} and at most ${r.max}.` : `${label(param)} must be between ${r.min} and ${r.max}.`;
};

/**
 * The plain sentence for a refused edit. `doc` is the document the edit was tried against; `op`, when
 * the caller has it, names the track a refused track setting was for.
 */
export function refusalSentence(error: unknown, doc?: EditTimeline | null, op?: EditOp): string {
  const raw = error instanceof Error ? error.message : typeof error === 'string' ? error : String(error);
  const names = namesOf(doc);
  const nameOf = (id: string): string | null => {
    const n = names.get(id);
    return n !== undefined && n.trim().length > 0 ? `“${n}”` : null;
  };
  let text = raw.replace(OP_PREFIX, '');

  // An out-of-range Inspector field: name the field and its range, never the raw value.
  const property = /setProperty value for '(\w+)' must be (.*?)(?:, found [\s\S]*)?$/.exec(text);
  if (property) {
    const [, key, expected] = property;
    const channel = /whose '(\w+)' is a keyframed number/.exec(expected);
    if (channel && channel[1] in KEYFRAME_PARAM_RANGE) return rangeSentence(channel[1] as KeyframeParam);
    if (key === 'gainDb') return rangeSentence('gainDb');
    return sentence(`${label(key)} must be ${expected.replace(ANY_GUID, (id) => nameOf(id) ?? 'an item').replace(/'/g, '')}`);
  }
  const trackProperty = /is not a legal value for track property '(\w+)'/.exec(text);
  if (trackProperty) {
    const named = op && 'trackId' in op && typeof op.trackId === 'string' ? nameOf(op.trackId) : null;
    return sentence(`That ${label(trackProperty[1])} setting is not allowed${named ? ` on ${named}` : ''}`);
  }
  const locked = /track '([^']+)' is locked/.exec(text);
  if (locked) return `The track ${nameOf(locked[1]) ?? 'you are editing'} is locked. Unlock it to change its clips.`;
  if (/unknown item id/.test(text)) return 'That clip is no longer on the timeline.';
  if (/unknown track id/.test(text)) return 'That track is no longer on the timeline.';
  if (/unknown marker id/.test(text)) return 'That marker no longer exists.';

  // Everything else: drop raw values, put names in place of ids, drop unknown ids.
  text = text
    .replace(/,? found [\s\S]*$/, '')
    .replace(/\{[\s\S]*\}/g, '')
    .replace(/\[object Object\]/g, 'that value')
    .replace(/'([^']*)'/g, (_m, inner: string) => nameOf(inner) ?? (IS_GUID.test(inner) ? 'an item' : `“${inner}”`))
    .replace(ANY_GUID, (id) => nameOf(id) ?? 'an item');
  return sentence(text);
}
