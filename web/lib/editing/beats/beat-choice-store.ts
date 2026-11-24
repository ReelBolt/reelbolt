'use client';

/**
 * The music clip and grid correction chosen in "Cut to the music", shared with the timeline overlay and
 * beat snapping so all three use the same grid. In memory, one entry per project (timeline); the
 * correction resets when the music clip changes. No entry means "no dialog choice": callers fall back to
 * their own music clip pick with no correction.
 */

import { useSyncExternalStore } from 'react';
import { NO_CORRECTION, type BeatCorrection } from './beat-grid';

export interface BeatChoice {
  musicClipId: string;
  correction: BeatCorrection;
}

const choices = new Map<string, BeatChoice>();
const listeners = new Set<() => void>();
const emit = (): void => {
  for (const l of listeners) l();
};
const subscribe = (l: () => void): (() => void) => {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
};

export function getBeatChoice(projectId: string): BeatChoice | null {
  return choices.get(projectId) ?? null;
}

/** Sets the music clip; the correction resets when it differs from the stored clip. */
export function setBeatMusicClip(projectId: string, musicClipId: string): void {
  const prev = choices.get(projectId);
  if (prev && prev.musicClipId === musicClipId) return;
  choices.set(projectId, { musicClipId, correction: NO_CORRECTION });
  emit();
}

/** Sets the correction for `musicClipId` (storing the clip too, resetting first if it changed). */
export function setBeatCorrection(projectId: string, musicClipId: string, correction: BeatCorrection): void {
  choices.set(projectId, { musicClipId, correction });
  emit();
}

export function clearBeatChoice(projectId: string): void {
  if (choices.delete(projectId)) emit();
}

export function useBeatChoice(projectId: string): BeatChoice | null {
  return useSyncExternalStore(subscribe, () => getBeatChoice(projectId), () => null);
}
