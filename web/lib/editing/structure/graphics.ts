/**
 * R10.5 / R07.6: titles and colour cards with options. Builds on the same placement rule as
 * `add-at-playhead` (the topmost free video track, else a new one above), but takes a chosen
 * template, text, length, colour and track, and says where the item landed.
 *
 * `TitleClip.position` is an optional 3x3 pad (the same one captions use). The dialog sets it through
 * `spec.position`; absent means "Template default" (the template sets the layout: "Lower third" sits low,
 * "Plain" is centred). The render ignores it for "Lower third" and "Card", which keep their own layout.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, GeneratorClip, TextPosition, TitleClip, TitleTemplate, Track } from '../types';
import { framesToTimecode } from '../time';
import { trackDisplayName, type LandedAt } from '../media/place';

/** A title template with the plain name the dialog shows. */
export const TITLE_TEMPLATE_CHOICES: ReadonlyArray<{ id: TitleTemplate; label: string; hint: string }> = [
  { id: 'plain', label: 'Plain (centred)', hint: 'Large text in the middle of the picture.' },
  { id: 'lowerThird', label: 'Lower third', hint: 'Smaller text near the bottom, like a name caption.' },
  { id: 'boxed', label: 'Boxed', hint: 'Text on a solid box so it stays readable on any picture.' },
  { id: 'outline', label: 'Outline', hint: 'Text with a dark outline.' },
  { id: 'card', label: 'Card', hint: 'Text on a full-screen card.' },
];

/** The 3x3 pad in reading order, with the plain names the controls show. */
export const TEXT_POSITION_CHOICES: ReadonlyArray<{ id: TextPosition; label: string }> = [
  { id: 'topLeft', label: 'Top left' },
  { id: 'topCenter', label: 'Top centre' },
  { id: 'topRight', label: 'Top right' },
  { id: 'middleLeft', label: 'Middle left' },
  { id: 'center', label: 'Centre' },
  { id: 'middleRight', label: 'Middle right' },
  { id: 'bottomLeft', label: 'Bottom left' },
  { id: 'bottomCenter', label: 'Bottom centre' },
  { id: 'bottomRight', label: 'Bottom right' },
];

export const TITLE_DEFAULT_SECONDS = 3;
export const CARD_DEFAULT_SECONDS = 3;

/** Accessible colour presets for colour cards (name + hex). */
export const COLOR_PRESETS: ReadonlyArray<{ name: string; hex: string }> = [
  { name: 'Black', hex: '#000000' },
  { name: 'White', hex: '#ffffff' },
  { name: 'Grey', hex: '#808080' },
  { name: 'Red', hex: '#d93025' },
  { name: 'Orange', hex: '#f29900' },
  { name: 'Yellow', hex: '#f6d51f' },
  { name: 'Green', hex: '#188038' },
  { name: 'Teal', hex: '#12a4a4' },
  { name: 'Blue', hex: '#1a73e8' },
  { name: 'Purple', hex: '#8430ce' },
  { name: 'Pink', hex: '#e8559b' },
  { name: 'Navy', hex: '#14213d' },
];

const HEX = /^#[0-9a-fA-F]{6}$/;
export const isHexColor = (value: string): boolean => HEX.test(value.trim());

export type GraphicSpec =
  | { kind: 'title'; text: string; template: TitleTemplate; position?: TextPosition | null; seconds?: number }
  | { kind: 'colorCard'; color: string; seconds?: number };

export interface GraphicInput {
  spec: GraphicSpec;
  at: number;
  /** Preferred video track; the topmost free one is used when it is missing, locked or busy there. */
  trackId?: string | null;
  newId: () => string;
  isLocked?: (track: Track) => boolean;
}

export interface GraphicResult {
  ops: EditOp[];
  itemId: string;
  landedAt: LandedAt;
  label: string;
}

const isFree = (track: Track, from: number, to: number): boolean => track.items.every((i) => i.recordOut <= from || i.recordIn >= to);

/** The ops that place the described title or colour card at `at`. */
export function buildGraphicOps(doc: EditTimeline, input: GraphicInput): GraphicResult {
  const { spec } = input;
  const isLocked = input.isLocked ?? ((t: Track) => t.locked);
  const rate = doc.settings.rate;
  const fps = rate.den > 0 && rate.num > 0 ? rate.num / rate.den : 30;
  const seconds = spec.seconds ?? (spec.kind === 'title' ? TITLE_DEFAULT_SECONDS : CARD_DEFAULT_SECONDS);
  const recordIn = Math.max(0, Math.trunc(input.at));
  const recordOut = recordIn + Math.max(1, Math.round(seconds * fps));
  const itemId = input.newId();

  const item: TitleClip | GeneratorClip =
    spec.kind === 'title'
      ? { id: itemId, type: 'titleClip', name: 'Title', recordIn, recordOut, text: spec.text, fontSize: 64, color: '#ffffff', enabled: true, template: spec.template === 'plain' ? null : spec.template, ...(spec.position ? { position: spec.position } : {}), provenance: { origin: 'manual', editedByUser: true } }
      : { id: itemId, type: 'generatorClip', name: 'Colour card', recordIn, recordOut, generatorType: 'color', generatorConfig: { color: spec.color.toLowerCase() }, enabled: true, provenance: { origin: 'manual', editedByUser: true } };

  const videos = doc.tracks.filter((t) => t.kind === 'video');
  const preferred = input.trackId ? videos.find((t) => t.id === input.trackId) : undefined;
  const chosen =
    preferred && !isLocked(preferred) && isFree(preferred, recordIn, recordOut)
      ? preferred
      : [...videos].reverse().find((t) => !isLocked(t) && isFree(t, recordIn, recordOut));

  const label = spec.kind === 'title' ? 'Add title' : 'Add colour card';
  const tc = framesToTimecode(recordIn, rate);
  if (chosen) {
    return { ops: [{ type: 'placeOnTop', trackId: chosen.id, item }], itemId, landedAt: { trackName: trackDisplayName(chosen), frame: recordIn, tc }, label };
  }
  const last = videos[videos.length - 1];
  const index = last ? doc.tracks.indexOf(last) + 1 : 0;
  const track: Track = { id: input.newId(), kind: 'video', name: `V${videos.length + 1}`, height: 64, locked: false, enabled: true, mute: false, solo: false, items: [item] };
  return { ops: [{ type: 'addTrack', index, track }], itemId, landedAt: { trackName: trackDisplayName(track), frame: recordIn, tc }, label };
}
