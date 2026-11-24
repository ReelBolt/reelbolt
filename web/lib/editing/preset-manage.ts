/** R12.8: pure helpers for managing saved presets — copy names and a plain-words preview of what applying one does. */

import type { PresetKind } from './api/editor-p9';

/** "Warm" → "Warm copy" → "Warm copy 2", never a name already taken (case-insensitive), at most 80 characters. */
export function uniqueCopyName(name: string, taken: readonly string[]): string {
  const used = new Set(taken.map((n) => n.trim().toLowerCase()));
  const base = name.replace(/ copy( \d+)?$/i, '').trim().slice(0, 70) || 'Preset';
  let candidate = `${base} copy`;
  for (let n = 2; used.has(candidate.toLowerCase()); n++) candidate = `${base} copy ${n}`;
  return candidate;
}

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object';

/** What applying the preset does, in plain words (no field names). */
export function describePreset(kind: PresetKind, payload: unknown): string {
  if (!isObj(payload)) return 'No details.';
  if (kind === 'mastering') {
    const parts = [`loudness ${payload.targetLufs} LUFS`, `peaks no higher than ${payload.truePeakDbtp} dB`];
    if (typeof payload.loudnessRange === 'number') parts.push(`loudness range ${payload.loudnessRange}`);
    return `Sets ${parts.join(', ')}.`;
  }
  if (kind === 'transition') {
    const frames = Number(payload.framesBefore ?? 0) + Number(payload.framesAfter ?? 0);
    const easing = typeof payload.easing === 'string' ? `, ${payload.easing} easing` : '';
    return `A ${String(payload.transitionType)} transition, ${frames} frames long${easing}.`;
  }
  const effects = Array.isArray(payload.effects) ? payload.effects.filter(isObj) : [];
  if (effects.length === 0) return 'No effects.';
  return `Adds ${effects.map((e) => `${String(e.type)}${typeof e.amount === 'number' ? ` ${e.amount}` : ''}`).join(', ')}.`;
}
