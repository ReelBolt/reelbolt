/**
 * W0.6 (P5-GEN-03): the "Add colour card" and "Add title" edits. Each builds a valid item at the
 * playhead, three seconds long, and places it on the topmost unlocked video track whose span is
 * free (`placeOnTop`, never a ripple); when no video track has room it adds a new video track above
 * the others holding the item. The result is one program the caller dispatches as one undo step.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, GeneratorClip, Item, TitleClip, Track } from '../types';

export type PlayheadAddKind = 'colorCard' | 'title';

const DEFAULT_SECONDS = 3;

function framesPerSecond(doc: EditTimeline): number {
  const { num, den } = doc.settings.rate;
  return den > 0 && num > 0 ? num / den : 30;
}

function buildItem(kind: PlayheadAddKind, id: string, recordIn: number, recordOut: number): Item {
  if (kind === 'colorCard') {
    const card: GeneratorClip = { id, type: 'generatorClip', name: 'Colour card', recordIn, recordOut, generatorType: 'color', generatorConfig: { color: '#000000' }, enabled: true, provenance: null };
    return card;
  }
  const title: TitleClip = { id, type: 'titleClip', name: 'Title', recordIn, recordOut, text: 'Title', fontSize: 64, color: '#ffffff', enabled: true, template: null, provenance: null };
  return title;
}

const isFree = (track: Track, recordIn: number, recordOut: number): boolean => track.items.every((i) => i.recordOut <= recordIn || i.recordIn >= recordOut);

export function addAtPlayhead(doc: EditTimeline, kind: PlayheadAddKind, playheadFrame: number, newId: () => string): { ops: EditOp[]; itemId: string } {
  const recordIn = Math.max(0, Math.trunc(playheadFrame));
  const recordOut = recordIn + Math.max(1, Math.round(DEFAULT_SECONDS * framesPerSecond(doc)));
  const itemId = newId();
  const item = buildItem(kind, itemId, recordIn, recordOut);

  // Later video tracks composite on top; try them first.
  const videoIndexes = doc.tracks.map((t, i) => ({ t, i })).filter(({ t }) => t.kind === 'video');
  for (let k = videoIndexes.length - 1; k >= 0; k--) {
    const { t } = videoIndexes[k];
    if (!t.locked && isFree(t, recordIn, recordOut)) return { ops: [{ type: 'placeOnTop', trackId: t.id, item }], itemId };
  }

  const count = videoIndexes.length + 1;
  const index = videoIndexes.length > 0 ? videoIndexes[videoIndexes.length - 1].i + 1 : 0;
  const track: Track = { id: newId(), kind: 'video', name: `V${count}`, height: 64, locked: false, enabled: true, mute: false, solo: false, items: [item] };
  return { ops: [{ type: 'addTrack', index, track }], itemId };
}
