import { msg, type Msg } from '@/i18n/msg';
import type { PlaceMode } from '@/lib/editing/media/place';

/** The four placements plus T-012's "Place over" (a picture-in-picture above what is there). */
export type PlaceChoice = PlaceMode | 'placeOver';

/** The four placements and their test ids. The shown words are `editor-misc.place.choices.<mode>.label|hint`. */
export const PLACE_MODES: ReadonlyArray<{ mode: PlaceMode; testId: string }> = [
  { mode: 'insert', testId: 'media-insert-at-playhead' },
  { mode: 'overwrite', testId: 'media-overwrite' },
  { mode: 'append', testId: 'media-append' },
  { mode: 'placeOnTop', testId: 'media-place-on-top' },
];

/**
 * T-010: the ONE label source for the four placements. Every host (pool tile menu, source viewer, Cut and
 * the phone) resolves these two messages; no English literal lives in a component.
 */
/** "Place over": offered next to the four, but not one of the engine's `PlaceMode`s (it picks its own track and adds the corner preset). */
export const PLACE_OVER: { mode: PlaceChoice; testId: string } = { mode: 'placeOver', testId: 'media-place-over' };

export const placeChoiceLabelMsg = (mode: PlaceChoice): Msg => msg(`editor-misc.place.choices.${mode}.label`);
export const placeChoiceHintMsg = (mode: PlaceChoice): Msg => msg(`editor-misc.place.choices.${mode}.hint`);
