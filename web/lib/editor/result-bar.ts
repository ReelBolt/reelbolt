/**
 * What the bar under a paid AI run says, in plain words. Pure so the wording is tested.
 */

import { creditsText } from './credit-estimate';

export interface ResultInfo {
  /** The tool's name as the user knows it ("Erase object", "Cut out subject"). */
  title: string;
  /** The layers the run added, in order. */
  layerIds: string[];
  /** Their names, same order. */
  layerNames: string[];
  /** What the run cost; null when nothing was charged or it is not known. */
  charged: number | null;
  /** A note from the service ("the picture was shrunk to fit"). */
  notice: string | null;
  /** The undo step this run recorded, so Undo and Try again only act when it is still the latest. */
  step: string;
}

/** "Erase object added 'Erase object'." / "Cut out subject added 3 options." */
export function changedWords(info: Pick<ResultInfo, 'title' | 'layerNames'>, options: boolean): string {
  const n = info.layerNames.length;
  if (n === 0) return `${info.title} finished, but nothing new came back.`;
  if (options && n > 1) return `${info.title} found ${n} options. Showing option 1.`;
  if (n === 1) return `${info.title} added a new layer, "${info.layerNames[0]}". Your original is untouched.`;
  return `${info.title} added ${n} new layers. Your originals are untouched.`;
}

/** "Cost 4.5 credits." / "No credits were charged." */
export function chargedWords(charged: number | null): string {
  return charged !== null && charged > 0 ? `It cost ${creditsText(charged)}.` : 'No credits were charged.';
}

export function optionLabel(index: number, total: number): string {
  return `Option ${index + 1} of ${total}`;
}
