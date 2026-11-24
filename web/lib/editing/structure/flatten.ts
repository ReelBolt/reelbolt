/**
 * P8: the TypeScript mirror of `ReelBolt.Shared.Editing.TimelineFlattener`.
 *
 * Turns a document with compound and multicam clips into one with neither, so the preview
 * compositor, the audio graph and the parity classifier never learn that nesting exists — exactly
 * as the renderer never does. The rules are the C# ones, line for line:
 *
 *   - a compound clip is replaced by its sequence's tracks, each becoming a NEW track inserted right
 *     after the host track, items shifted by `recordIn − sourceIn` and clipped to the clip's span
 *     (a media clip's `sourceIn` advances by the head trim at its rate, a cut transition is dropped);
 *   - a disabled compound, a disabled host track or a missing sequence expands to nothing; nesting
 *     is followed to {@link MAX_NESTING_DEPTH} and a sequence already on the chain is dropped;
 *   - a multicam clip becomes one media clip per angle run, each reading
 *     `offsetFrames + sourceIn + (runStart − recordIn)` of its angle.
 *
 * Ids are DERIVED, never fresh, so the same document always flattens to the same ids. The C# side
 * hashes `scope:local` with MD5 into a GUID; the preview only needs stable, unique ids, so this
 * mirror uses the readable `scope/local` string instead — the two never meet (the server flattens
 * its own copy), and asserting byte-equal ids across the languages would buy nothing.
 *
 * Pure: the input document is never modified.
 */

import { MAX_NESTING_DEPTH } from '../engine/ops';
import type { ClipTransform, EditSequence, EditTimeline, Item, Keyframed, MediaClip, MulticamClip, Track } from '../types';

/** Whether `doc` carries anything {@link flattenTimeline} would change. */
export function needsFlattening(doc: EditTimeline): boolean {
  return doc.tracks.some((t) => t.items.some((i) => i.type === 'compoundClip' || i.type === 'multicamClip'));
}

/** A stable id for an expanded element: its scope (the host clip) and its own id or role. */
export function deriveId(scope: string, local: string): string {
  return `${scope}/${local}`;
}

/** The flattened document — the same object when there is nothing to flatten. */
export function flattenTimeline(doc: EditTimeline): EditTimeline {
  if (!needsFlattening(doc)) return doc;
  const sequences = new Map<string, EditSequence>();
  for (const sequence of doc.sequences ?? []) if (!sequences.has(sequence.id)) sequences.set(sequence.id, sequence);
  return { ...doc, tracks: flattenTracks(doc.tracks, sequences, 1, new Set()) };
}

/** The media clips a multicam clip plays, one per angle run, in record order. */
export function expandMulticam(clip: MulticamClip): MediaClip[] {
  return multicamRuns(clip);
}

/**
 * R08.2: the sound a multicam clip plays, as ONE media clip for an AUDIO track spanning the whole
 * clip (TimelineFlattener.cs derives the same thing): `audioSource` absent or `program` is the
 * FIRST angle's sound (the synchronised program feed), an angle id is that angle's sound, `none` or
 * a disabled clip is silent. Empty when silent.
 */
export function expandMulticamAudio(clip: MulticamClip): MediaClip[] {
  const source = clip.audioSource ?? 'program';
  if (source === 'none' || clip.enabled === false) return [];
  const angle = source === 'program' ? clip.angles[0] : clip.angles.find((a) => a.id === source) ?? clip.angles[0];
  if (angle === undefined) return [];
  return [
    {
      id: deriveId(clip.id, 'audio0'),
      type: 'mediaClip',
      name: `${clip.name} · ${angle.name}`,
      recordIn: clip.recordIn,
      recordOut: clip.recordOut,
      assetRef: angle.assetRef,
      sourceIn: Math.max(0, (angle.offsetFrames ?? 0) + (clip.sourceIn ?? 0)),
      playbackRate: 1,
      enabled: true,
      provenance: clip.provenance ?? null,
      gainDb: clip.gainDb ?? null,
      // P3: the sound comes from `angle`, so it carries that angle's source profile too.
      ...(angle.inputProfile != null ? { inputProfile: angle.inputProfile } : {}),
    },
  ];
}

/** The picture runs of a multicam clip, one per angle run, in record order. */
function multicamRuns(clip: MulticamClip): MediaClip[] {
  const byId = new Map(clip.angles.map((a) => [a.id, a]));
  const active = byId.get(clip.activeAngleId) ?? clip.angles[0];
  if (active === undefined) return [];
  const runs: { at: number; angle: typeof active }[] = [{ at: clip.recordIn, angle: active }];
  const cuts = [...(clip.cuts ?? [])].sort((a, b) => a.atFrame - b.atFrame);
  for (const cut of cuts) {
    if (cut.atFrame <= clip.recordIn || cut.atFrame >= clip.recordOut) continue;
    const angle = byId.get(cut.angleId);
    if (angle === undefined || cut.atFrame <= runs[runs.length - 1].at) continue;
    runs.push({ at: cut.atFrame, angle });
  }
  const result: MediaClip[] = [];
  runs.forEach((run, i) => {
    const start = run.at;
    const end = i + 1 < runs.length ? runs[i + 1].at : clip.recordOut;
    if (end <= start) return;
    result.push({
      id: deriveId(clip.id, `run${i}`),
      type: 'mediaClip',
      name: `${clip.name} · ${run.angle.name}`,
      recordIn: start,
      recordOut: end,
      assetRef: run.angle.assetRef,
      sourceIn: Math.max(0, (run.angle.offsetFrames ?? 0) + (clip.sourceIn ?? 0) + (start - clip.recordIn)),
      playbackRate: 1,
      enabled: clip.enabled !== false,
      provenance: clip.provenance ?? null,
      gainDb: clip.gainDb ?? null,
      // P3: the angle's source profile travels onto every run (TimelineFlattener.cs does the same).
      ...(run.angle.inputProfile != null ? { inputProfile: run.angle.inputProfile } : {}),
    });
  });
  return result;
}

/** The angle playing at `frame` (record clock) of a multicam clip. */
export function angleAt(clip: MulticamClip, frame: number): string {
  let live = clip.activeAngleId;
  for (const cut of [...(clip.cuts ?? [])].sort((a, b) => a.atFrame - b.atFrame)) {
    if (cut.atFrame <= frame) live = cut.angleId;
  }
  return live;
}

function flattenTracks(tracks: readonly Track[], sequences: Map<string, EditSequence>, depth: number, onPath: Set<string>): Track[] {
  const output: Track[] = [];
  for (const track of tracks) {
    const items: Item[] = [];
    const nested: Track[] = [];
    for (const item of track.items) {
      if (item.type === 'multicamClip') {
        if (item.enabled === false) continue;
        if (track.kind === 'audio') {
          // On an audio track the angle runs ARE the sound; an explicit source replaces them.
          items.push(...(item.audioSource == null ? expandMulticam(item) : expandMulticamAudio(item)));
          continue;
        }
        items.push(...expandMulticam(item));
        // R08.2: the picture plays on the host track; the sound gets a derived audio track right after it.
        const sound = expandMulticamAudio(item);
        if (sound.length > 0) {
          nested.push({ ...track, id: deriveId(item.id, 'audio'), name: `${item.name} / audio`, kind: 'audio', mute: track.mute, items: sound });
        }
      } else if (item.type === 'compoundClip') {
        if (item.enabled === false || !track.enabled) continue;
        const sequence = sequences.get(item.sequenceId);
        if (sequence === undefined || depth >= MAX_NESTING_DEPTH || onPath.has(item.sequenceId)) continue;
        onPath.add(item.sequenceId);
        const inner = flattenTracks(sequence.tracks, sequences, depth + 1, onPath);
        onPath.delete(item.sequenceId);
        for (const innerTrack of inner) {
          const placed = placeNestedTrack(innerTrack, item.id, item.name, item.recordIn, item.recordOut, item.sourceIn ?? 0, track);
          if (placed !== null) nested.push(placed);
        }
      } else {
        items.push(item);
      }
    }
    output.push({ ...track, items });
    output.push(...nested);
  }
  return output;
}

function placeNestedTrack(nested: Track, scope: string, scopeName: string, recordIn: number, recordOut: number, sourceIn: number, host: Track): Track | null {
  const shift = recordIn - sourceIn;
  const items: Item[] = [];
  for (const item of nested.items) {
    const placed = respanItem(item, shift, recordIn, recordOut, deriveId(scope, item.id));
    if (placed !== null) items.push(placed);
  }
  if (items.length === 0) return null;
  return {
    ...nested,
    id: deriveId(scope, nested.id),
    name: `${scopeName} / ${nested.name}`,
    mute: nested.mute || (host.kind === 'audio' && host.mute),
    items,
  };
}

/**
 * The item translated by `shift` and clipped to `[start, end)` under `id`, or null when nothing of
 * it survives. Shared by the flattener and by compound decomposition (which keeps nested compound
 * and multicam clips, clipping their own source offset and cuts the same way).
 */
export function respanItem(item: Item, shift: number, start: number, end: number, id: string): Item | null {
  const recordIn = item.recordIn + shift;
  const recordOut = item.recordOut + shift;
  const newIn = Math.max(recordIn, start);
  const newOut = Math.min(recordOut, end);
  if (newOut <= newIn) return null;
  const headTrim = newIn - recordIn;
  const tailTrim = recordOut - newOut;
  switch (item.type) {
    case 'mediaClip': {
      const rate = item.playbackRate && item.playbackRate > 0 ? item.playbackRate : 1;
      const base = item.sourceIn ?? 0;
      const sourceIn = item.hold ? base : item.reverse ? base + Math.round(tailTrim * rate) : base + Math.round(headTrim * rate);
      const out: MediaClip = { ...item, id, recordIn: newIn, recordOut: newOut, sourceIn: Math.max(0, sourceIn), gainDb: shiftKeys(item.gainDb, shift), transform: shiftTransform(item.transform, shift) };
      if (item.fadeInFrames !== undefined) out.fadeInFrames = Math.max(0, Math.min(item.fadeInFrames - headTrim, newOut - newIn));
      if (item.fadeOutFrames !== undefined) out.fadeOutFrames = Math.max(0, Math.min(item.fadeOutFrames - tailTrim, newOut - newIn));
      return out;
    }
    case 'titleClip':
      // A cut title keeps everything it carries (text, template, pad position, transform); only its time moves.
      return { ...item, id, recordIn: newIn, recordOut: newOut, ...(item.transform ? { transform: shiftTransform(item.transform, shift) } : {}) };
    case 'insertClip':
      return { ...item, id, recordIn: newIn, recordOut: newOut, quad: shiftKeys(item.quad, shift) };
    case 'transition':
      // Half a transition is not a transition: a clipped one is dropped and the cut stays hard.
      if (headTrim !== 0 || tailTrim !== 0) return null;
      return { ...item, id, recordIn: newIn, recordOut: newOut };
    case 'compoundClip':
      return { ...item, id, recordIn: newIn, recordOut: newOut, sourceIn: (item.sourceIn ?? 0) + headTrim };
    case 'multicamClip': {
      const shifted = (item.cuts ?? []).map((c) => ({ ...c, atFrame: c.atFrame + shift }));
      let active = item.activeAngleId;
      for (const cut of shifted) if (cut.atFrame <= newIn) active = cut.angleId;
      const cuts = shifted.filter((c) => c.atFrame > newIn && c.atFrame < newOut);
      return { ...item, id, recordIn: newIn, recordOut: newOut, sourceIn: (item.sourceIn ?? 0) + headTrim, activeAngleId: active, cuts: item.cuts === undefined ? undefined : cuts, gainDb: shiftKeys(item.gainDb, shift) };
    }
    default:
      return { ...item, id, recordIn: newIn, recordOut: newOut };
  }
}

function shiftKeys<T>(channel: Keyframed<T> | null | undefined, shift: number): Keyframed<T> | null | undefined {
  if (!channel?.keys || channel.keys.length === 0 || shift === 0) return channel;
  return { ...channel, keys: channel.keys.map((k) => ({ ...k, frame: k.frame + shift })) };
}

function shiftTransform(transform: ClipTransform | null | undefined, shift: number): ClipTransform | null | undefined {
  if (!transform || shift === 0) return transform;
  return {
    positionX: shiftKeys(transform.positionX, shift),
    positionY: shiftKeys(transform.positionY, shift),
    scale: shiftKeys(transform.scale, shift),
    scaleY: shiftKeys(transform.scaleY, shift),
    rotation: shiftKeys(transform.rotation, shift),
    opacity: shiftKeys(transform.opacity, shift),
  };
}
