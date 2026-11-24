import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, Track } from '@/lib/editing/types';
import { compileDuck, compileRemoveDuck, defaultDuckConfig, duckIsStale, guessDuckTracks, resolveDuck, type DuckConfig } from '@/lib/editing/audio/duck-state';
import { compileCleanVoice, isCleanVoice } from '../audio/CleanVoice';

/** What the one-tap "Duck music" can do right now, in plain words. */
export interface DuckState {
  /** The ducking that is on, if any. */
  live: DuckConfig | null;
  /** The voice moved since the curve was written: "Update ducking" is worth a tap. */
  stale: boolean;
  /** The config a tap would write, or null with `reason` saying what is missing. */
  next: DuckConfig | null;
  reason: string | null;
  musicName: string | null;
}

export function duckState(doc: EditTimeline, saved: DuckConfig | null): DuckState {
  const live = resolveDuck(doc, saved);
  const guess = guessDuckTracks(doc.tracks);
  const nameOf = (id: string | null) => doc.tracks.find((t) => t.id === id)?.name ?? null;
  const musicId = live?.musicTrackId ?? guess.music;
  const voice = live?.dialogueTrackIds ?? guess.voice;
  if (!musicId) return { live, stale: false, next: null, reason: 'Add a music track first. A track named Music is picked up by itself.', musicName: null };
  if (voice.length === 0 || voice.every((id) => (doc.tracks.find((t) => t.id === id)?.items.length ?? 0) === 0)) {
    return { live, stale: false, next: null, reason: 'Put a voice or dialogue clip on its own audio track so the music has something to duck under.', musicName: nameOf(musicId) };
  }
  const next = live ?? defaultDuckConfig(musicId, voice);
  return { live, stale: live ? duckIsStale(doc, live) : false, next, reason: null, musicName: nameOf(musicId) };
}

/** One tap: the ops (a single op) and label that turn ducking on or refresh it. */
export function compileDuckTap(doc: EditTimeline, config: DuckConfig): { ops: EditOp[]; label: string } {
  return { ops: [compileDuck(doc, config)], label: 'Duck music under voice' };
}

export function compileDuckOff(doc: EditTimeline, config: DuckConfig): { ops: EditOp[]; label: string } {
  return { ops: [compileRemoveDuck(doc, config)], label: 'Remove ducking' };
}

/**
 * The tracks "Clean voice" treats: the voice tracks the names point to; else the audio track of the selected
 * clip; else, when no track is called music, every audio track that holds a clip.
 */
export function cleanVoiceTargets(doc: EditTimeline, selectedItemId: string | null): Track[] {
  const audio = doc.tracks.filter((t) => t.kind === 'audio' && t.items.length > 0);
  const guess = guessDuckTracks(doc.tracks);
  const named = audio.filter((t) => guess.voice.includes(t.id));
  if (named.length > 0) return named;
  const ofSelection = selectedItemId ? audio.filter((t) => t.items.some((i) => i.id === selectedItemId)) : [];
  if (ofSelection.length > 0) return ofSelection;
  return guess.music ? [] : audio;
}

/** One undo step for every target. Already clean tracks are switched off together (a second tap undoes the first). */
export function compileCleanVoiceTap(targets: readonly Track[]): { ops: EditOp[]; label: string; on: boolean } {
  const on = !targets.every(isCleanVoice);
  return { ops: targets.flatMap((t) => compileCleanVoice(t, on)), label: on ? 'Clean voice' : 'Clean voice off', on };
}

export const allClean = (targets: readonly Track[]): boolean => targets.length > 0 && targets.every(isCleanVoice);
