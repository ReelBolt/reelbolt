/**
 * P8: compound clips — create, open (edit the sequence), decompose — each compiled to a program of
 * the closed op catalogue, so every one is a single undo step whose inverse the engine computes.
 *
 *   - **create** lifts the selected items, adds a sequence holding them (one sequence track per
 *     source track with a FRESH Guid track id, items shifted so the earliest starts at sequence
 *     frame 0, item ids KEPT — item ids are unique document-wide, so moving an item into a sequence
 *     needs no re-id) and places a compound
 *     clip over their span on the topmost source track. Refused when that span is not free there
 *     once the selection is lifted.
 *   - **open** derives a sub-document whose tracks are the sequence's, edits it in its own store,
 *     and **commit** writes it back as ONE `setSequence` (the engine re-checks ids and nesting).
 *   - **decompose** lifts the compound and adds one fresh track per sequence track right below the
 *     host, holding the sequence's items respanned exactly as the flattener would (fresh ids, nested
 *     compound/multicam clips kept one level up), then removes the sequence when nothing else plays it.
 */

import { EditEngineError, type EditOp } from '../engine';
import type { CompoundClip, EditSequence, EditTimeline, Item, Track } from '../types';
import { newGuid } from '../ids';
import { respanItem } from './flatten';

export interface CompoundIds {
  sequenceId: string;
  compoundId: string;
  /** Mints each sequence track's id (a Guid, as the server binds it); defaults to {@link newGuid}. */
  newTrackId?: () => string;
}

/** The ops that turn the selected items into one compound clip. */
export function compileCreateCompound(doc: EditTimeline, itemIds: readonly string[], ids: CompoundIds, name = 'Compound clip'): EditOp[] {
  const wanted = new Set(itemIds);
  const picked: { track: Track; index: number; items: Item[] }[] = [];
  doc.tracks.forEach((track, index) => {
    const items = track.items.filter((i) => wanted.has(i.id));
    if (items.length > 0) picked.push({ track, index, items });
  });
  const found = picked.reduce((n, p) => n + p.items.length, 0);
  if (found === 0) throw new EditEngineError('select at least one item to make a compound clip');
  if (found !== wanted.size) throw new EditEngineError('every selected id must be an item of this timeline');
  if (picked.some((p) => p.track.locked)) throw new EditEngineError('a selected item sits on a locked track');

  const all = picked.flatMap((p) => p.items);
  const start = Math.min(...all.map((i) => i.recordIn));
  const end = Math.max(...all.map((i) => i.recordOut));
  const host = picked[0].track;
  const blocked = host.items.some((i) => !wanted.has(i.id) && i.recordIn < end && start < i.recordOut);
  if (blocked) throw new EditEngineError(`the span the compound needs on '${host.name}' is not free once the selection is lifted`);

  const sequence: EditSequence = {
    id: ids.sequenceId,
    name,
    tracks: picked.map((p) => ({
      ...structuredClone(p.track),
      id: (ids.newTrackId ?? newGuid)(),
      locked: false,
      items: p.items.map((i) => ({ ...structuredClone(i), recordIn: i.recordIn - start, recordOut: i.recordOut - start })),
    })),
  };
  const compound: CompoundClip = { id: ids.compoundId, type: 'compoundClip', name, recordIn: start, recordOut: end, sequenceId: ids.sequenceId, sourceIn: 0, enabled: true, provenance: null };

  return [
    ...all.map((i): EditOp => ({ type: 'lift', itemId: i.id })),
    { type: 'setSequence', sequenceId: ids.sequenceId, sequence },
    { type: 'placeOnTop', trackId: host.id, item: compound },
  ];
}

/** The sub-document a sequence is edited in: the parent's settings and sequences, the sequence's tracks. */
export function openSequenceDocument(doc: EditTimeline, sequenceId: string): EditTimeline {
  const sequence = (doc.sequences ?? []).find((q) => q.id === sequenceId);
  if (sequence === undefined) throw new EditEngineError(`no sequence '${sequenceId}' in this timeline`);
  return {
    ...structuredClone(doc),
    id: `${doc.id}:${sequenceId}`,
    name: sequence.name,
    tracks: structuredClone(sequence.tracks),
    // The parent's other sequences stay visible (nested compounds inside this one resolve), minus
    // this one, whose tracks are now the document's own — so its items are not counted twice.
    sequences: (doc.sequences ?? []).filter((q) => q.id !== sequenceId),
    markers: [],
    inOut: null,
    colorGroups: undefined,
  };
}

/** The one op that writes an edited sub-document back into its sequence. */
export function compileCommitSequence(doc: EditTimeline, sequenceId: string, edited: EditTimeline): EditOp {
  const sequence = (doc.sequences ?? []).find((q) => q.id === sequenceId);
  if (sequence === undefined) throw new EditEngineError(`no sequence '${sequenceId}' in this timeline`);
  return { type: 'setSequence', sequenceId, sequence: { ...sequence, tracks: structuredClone(edited.tracks) } };
}

/** The ops that expand a compound clip back onto the timeline. `newId` mints fresh item/track ids. */
export function compileDecompose(doc: EditTimeline, compoundId: string, newId: () => string): EditOp[] {
  const hostIndex = doc.tracks.findIndex((t) => t.items.some((i) => i.id === compoundId));
  if (hostIndex < 0) throw new EditEngineError(`unknown compound clip '${compoundId}'`);
  const compound = doc.tracks[hostIndex].items.find((i) => i.id === compoundId);
  if (compound?.type !== 'compoundClip') throw new EditEngineError(`'${compoundId}' is not a compound clip`);
  const sequence = (doc.sequences ?? []).find((q) => q.id === compound.sequenceId);
  if (sequence === undefined) throw new EditEngineError(`sequence '${compound.sequenceId}' does not exist`);

  const shift = compound.recordIn - (compound.sourceIn ?? 0);
  const ops: EditOp[] = [{ type: 'lift', itemId: compound.id }];
  let offset = 1;
  for (const nested of sequence.tracks) {
    const items = nested.items
      .map((item) => respanItem(item, shift, compound.recordIn, compound.recordOut, newId()))
      .filter((i): i is Item => i !== null);
    if (items.length === 0) continue;
    ops.push({ type: 'addTrack', index: hostIndex + offset, track: { ...structuredClone(nested), id: newId(), name: `${compound.name} / ${nested.name}`, locked: false, items } });
    offset++;
  }
  const otherPlayers = [...doc.tracks, ...(doc.sequences ?? []).flatMap((q) => q.tracks)].some((t) =>
    t.items.some((i) => i.id !== compound.id && i.type === 'compoundClip' && i.sequenceId === sequence.id),
  );
  if (!otherPlayers) ops.push({ type: 'setSequence', sequenceId: sequence.id, sequence: undefined });
  return ops;
}
