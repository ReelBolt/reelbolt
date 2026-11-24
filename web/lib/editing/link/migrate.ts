/**
 * R08.1 step 2 migration: link the pairs older timelines already hold and give footage that has sound
 * its missing companion. Pure; the caller dispatches the returned ops as one batch (one undo step)
 * after the user confirms the plain-language summary. Idempotent: run on its own output it returns
 * nothing.
 */

import type { EditOp } from '../engine';
import { newGuid } from '../ids';
import { companionAudioOp } from '../media/pool-insert';
import type { EditTimeline, MediaClip, Track } from '../types';

/** What the probe knows about a source's sound. Keyed by `assetKey`. */
export type AudioProbes = ReadonlyMap<string, boolean | null> | Readonly<Record<string, boolean | null>>;

/** The key under which a source's `hasAudio` probe is looked up: its project-file id or step-result id. */
export function assetKey(clip: Pick<MediaClip, 'assetRef'>): string {
  return clip.assetRef.projectFileId ?? clip.assetRef.stepResultId ?? '';
}

export interface MigrationPlan {
  ops: EditOp[];
  /** Existing pairs that were linked. */
  linked: number;
  /** Companions that were added. */
  added: number;
  /** "Linked 2 clips, added sound for 1." / "Nothing to link." */
  summary: string;
}

const hasAudioOf = (probes: AudioProbes, key: string): boolean | null => {
  if (key === '') return null;
  const value = probes instanceof Map ? probes.get(key) : (probes as Readonly<Record<string, boolean | null>>)[key];
  return value === undefined ? null : value;
};

const sameSource = (a: MediaClip, b: MediaClip): boolean =>
  a.assetRef.kind === b.assetRef.kind && (a.assetRef.projectFileId ?? null) === (b.assetRef.projectFileId ?? null) && (a.assetRef.stepResultId ?? null) === (b.assetRef.stepResultId ?? null);

const plural = (n: number, one: string, many: string): string => `${n} ${n === 1 ? one : many}`;

export function migrateToLinkedAudio(doc: EditTimeline, probes: AudioProbes, newId: () => string = newGuid): MigrationPlan {
  const ops: EditOp[] = [];
  let linked = 0;
  let added = 0;
  // Audio clips already claimed by a picture clip (by id), so two pictures never share one sound.
  const claimed = new Set<string>();
  const audioTracks = doc.tracks.filter((t) => t.kind === 'audio');
  // Tracks grow while we plan insertions, so keep a working copy of the audio track occupancy.
  const working: Track[] = doc.tracks.map((t) => ({ ...t, items: [...t.items] }));
  const workingDoc: EditTimeline = { ...doc, tracks: working };

  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const clip of track.items) {
      if (clip.type !== 'mediaClip' || clip.hold === true) continue;
      if (clip.linkId != null) {
        // Already linked: it has its partner, or its partner was removed on purpose; leave it be.
        continue;
      }
      const partner = audioTracks
        .flatMap((t) => t.items)
        .find(
          (a): a is MediaClip =>
            a.type === 'mediaClip' &&
            a.linkId == null &&
            !claimed.has(a.id) &&
            sameSource(a, clip) &&
            a.recordIn === clip.recordIn &&
            a.recordOut === clip.recordOut &&
            (a.sourceIn ?? 0) === (clip.sourceIn ?? 0),
        );
      if (partner) {
        const linkId = newId();
        claimed.add(partner.id);
        ops.push({ type: 'setProperty', itemId: clip.id, key: 'linkId', value: linkId });
        ops.push({ type: 'setProperty', itemId: partner.id, key: 'linkId', value: linkId });
        linked += 1;
        continue;
      }
      if (hasAudioOf(probes, assetKey(clip)) !== true) continue;
      const linkId = newId();
      const companion: MediaClip = {
        id: newId(),
        type: 'mediaClip',
        name: clip.name,
        recordIn: clip.recordIn,
        recordOut: clip.recordOut,
        assetRef: clip.assetRef,
        sourceIn: clip.sourceIn ?? 0,
        playbackRate: clip.playbackRate ?? 1,
        enabled: clip.enabled !== false,
        provenance: { origin: 'manual', editedByUser: true },
        linkId,
      };
      if (clip.reverse) companion.reverse = true;
      const op = companionAudioOp(workingDoc, companion, newId, { ripple: false });
      applyToWorking(working, op);
      ops.push(op);
      ops.push({ type: 'setProperty', itemId: clip.id, key: 'linkId', value: linkId });
      added += 1;
    }
  }
  return { ops, linked, added, summary: summarize(linked, added) };
}

/** Mirrors the planned insertion into the working copy so the next companion sees the room taken. */
function applyToWorking(working: Track[], op: EditOp): void {
  if (op.type === 'insert') {
    const track = working.find((t) => t.id === op.trackId);
    track?.items.splice(op.index, 0, op.item);
  } else if (op.type === 'addTrack') {
    working.splice(op.index, 0, { ...op.track, items: [...op.track.items] });
  }
}

export function summarize(linked: number, added: number): string {
  if (linked === 0 && added === 0) return 'Nothing to link.';
  const parts: string[] = [];
  if (linked > 0) parts.push(`Linked ${plural(linked, 'clip', 'clips')}`);
  if (added > 0) parts.push(`${linked > 0 ? 'added' : 'Added'} sound for ${added}`);
  return `${parts.join(', ')}.`;
}
