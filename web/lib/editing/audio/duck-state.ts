/**
 * R08.5: a re-derivable duck. The curve is ordinary keyframes on the music track's fader (so it
 * renders, previews and undoes like any automation); what makes it "on" is this small setting —
 * which tracks are music and which are voice — kept per timeline in this browser. While it is on,
 * the Audio workspace re-derives the curve whenever the voice clips move.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, Keyframed, Track } from '../types';
import { DEFAULT_DUCKING, deriveDucking } from './ducking';

export interface DuckConfig {
  musicTrackId: string;
  dialogueTrackIds: string[];
  depthDb: number;
  attackFrames: number;
  releaseFrames: number;
}

const KEY = (timelineId: string) => `rb.editor.duck.${timelineId}`;

export function defaultDuckConfig(musicTrackId: string, dialogueTrackIds: string[]): DuckConfig {
  return { musicTrackId, dialogueTrackIds, depthDb: DEFAULT_DUCKING.depthDb, attackFrames: DEFAULT_DUCKING.attackFrames, releaseFrames: DEFAULT_DUCKING.releaseFrames };
}

export function loadDuckConfig(timelineId: string, storage: Pick<Storage, 'getItem'> | null = safeStorage()): DuckConfig | null {
  try {
    const raw = storage?.getItem(KEY(timelineId));
    if (!raw) return null;
    const v = JSON.parse(raw) as Partial<DuckConfig>;
    if (typeof v.musicTrackId !== 'string' || !Array.isArray(v.dialogueTrackIds)) return null;
    return {
      musicTrackId: v.musicTrackId,
      dialogueTrackIds: v.dialogueTrackIds.filter((x): x is string => typeof x === 'string'),
      depthDb: typeof v.depthDb === 'number' ? v.depthDb : DEFAULT_DUCKING.depthDb,
      attackFrames: typeof v.attackFrames === 'number' ? v.attackFrames : DEFAULT_DUCKING.attackFrames,
      releaseFrames: typeof v.releaseFrames === 'number' ? v.releaseFrames : DEFAULT_DUCKING.releaseFrames,
    };
  } catch {
    return null;
  }
}

export function saveDuckConfig(timelineId: string, config: DuckConfig | null, storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeStorage()): void {
  try {
    if (config) storage?.setItem(KEY(timelineId), JSON.stringify(config));
    else storage?.removeItem(KEY(timelineId));
  } catch {
    /* private mode: the duck still works for this session */
  }
}

function safeStorage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
}

/** A saved config is live only while its tracks still exist and the voice set is non-empty. */
export function resolveDuck(doc: EditTimeline, config: DuckConfig | null): DuckConfig | null {
  if (!config) return null;
  const audio = new Map(doc.tracks.filter((t) => t.kind === 'audio').map((t) => [t.id, t]));
  if (!audio.has(config.musicTrackId)) return null;
  const dialogue = config.dialogueTrackIds.filter((id) => id !== config.musicTrackId && audio.has(id));
  return dialogue.length === 0 ? null : { ...config, dialogueTrackIds: dialogue };
}

/** The curve the music fader should hold now (its static value stays the bed). */
export function deriveDuckCurve(doc: EditTimeline, config: DuckConfig): Keyframed<number> {
  const music = doc.tracks.find((t) => t.id === config.musicTrackId);
  const bed = typeof music?.gainDb?.value === 'number' ? music.gainDb.value : 0;
  return deriveDucking(doc, { dialogueTrackIds: config.dialogueTrackIds, bedDb: bed, depthDb: config.depthDb, attackFrames: config.attackFrames, releaseFrames: config.releaseFrames });
}

const sameKeys = (a: Keyframed<number> | null | undefined, b: Keyframed<number>): boolean => {
  const ka = a?.keys ?? [];
  const kb = b.keys ?? [];
  if (ka.length !== kb.length) return false;
  return ka.every((k, i) => k.frame === kb[i].frame && Math.abs(k.value - kb[i].value) < 1e-6);
};

/** True when the stored keys no longer match what the voice clips imply. */
export function duckIsStale(doc: EditTimeline, config: DuckConfig): boolean {
  const music = doc.tracks.find((t) => t.id === config.musicTrackId);
  return !sameKeys(music?.gainDb, deriveDuckCurve(doc, config));
}

export function compileDuck(doc: EditTimeline, config: DuckConfig): EditOp {
  return { type: 'setTrackProperty', trackId: config.musicTrackId, key: 'gainDb', value: deriveDuckCurve(doc, config) };
}

export function compileRemoveDuck(doc: EditTimeline, config: DuckConfig): EditOp {
  const music = doc.tracks.find((t) => t.id === config.musicTrackId);
  return { type: 'setTrackProperty', trackId: config.musicTrackId, key: 'gainDb', value: { value: typeof music?.gainDb?.value === 'number' ? music.gainDb.value : 0, keys: null } };
}

const MUSIC = /music|bed|score|song|soundtrack/i;
const VOICE = /voice|\bvo\b|dialog|narrat|speech|interview|talk|host|speaker/i;

/** A best guess at which track is music and which are voice, from their names; null when unsure. */
export function guessDuckTracks(tracks: readonly Track[]): { music: string | null; voice: string[] } {
  const audio = tracks.filter((t) => t.kind === 'audio');
  const music = audio.find((t) => MUSIC.test(t.name))?.id ?? null;
  let voice = audio.filter((t) => t.id !== music && VOICE.test(t.name)).map((t) => t.id);
  if (music && voice.length === 0) voice = audio.filter((t) => t.id !== music && t.items.length > 0).map((t) => t.id);
  return { music, voice };
}
