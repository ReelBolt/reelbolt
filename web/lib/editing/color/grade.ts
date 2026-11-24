/**
 * Colour (P7): the pure helpers behind the Color workspace — identity values, the effective grade of
 * a clip (its group's stack then its own), the look presets that mirror the Colorist's vocabulary
 * (the same words `ColorGradeFilterBuilder` turns into ffmpeg, here turned into primaries so a
 * proposal is an ordinary, editable, bypassable grade node), colour groups pre-populated from the
 * analysis look groups, and shot match from two frames' statistics.
 *
 * Nothing here applies a grade: the renderer (`GradeFilterBuilder`) and the compositor
 * (`preview-grade.ts`) do that, each from the same document fields.
 */

import type { ColorGroup, EditTimeline, GradeNode, GradePrimaries, GradeStack, GradeWheel, MediaClip } from '../types';
import type { AnalysedShot } from '../cut/cut';
import { mapSourceSilenceToRecord, type SourceMatcher } from '../silence/remove-silence';

export const IDENTITY_PRIMARIES: Readonly<Required<Omit<GradePrimaries, 'lift' | 'gamma' | 'gain' | 'offset'>>> = Object.freeze({
  contrast: 1,
  pivot: 0.5,
  saturation: 1,
  hue: 0,
  temperature: 0,
  tint: 0,
  exposure: 0,
});

export const WHEEL_KEYS = ['lift', 'gamma', 'gain', 'offset'] as const;
export type WheelKey = (typeof WHEEL_KEYS)[number];
export const SCALAR_KEYS = ['exposure', 'contrast', 'pivot', 'saturation', 'hue', 'temperature', 'tint'] as const;
export type ScalarKey = (typeof SCALAR_KEYS)[number];

export function isIdentityWheel(wheel: GradeWheel | null | undefined): boolean {
  if (!wheel) return true;
  return (wheel.r ?? 0) === 0 && (wheel.g ?? 0) === 0 && (wheel.b ?? 0) === 0 && (wheel.master ?? 0) === 0;
}

export function isIdentityPrimaries(p: GradePrimaries | null | undefined): boolean {
  if (!p) return true;
  return WHEEL_KEYS.every((k) => isIdentityWheel(p[k])) && SCALAR_KEYS.every((k) => (p[k] ?? IDENTITY_PRIMARIES[k]) === IDENTITY_PRIMARIES[k]);
}

/** True when the stack would change a pixel: enabled, with an enabled node that does something. */
export function isActiveGrade(stack: GradeStack | null | undefined): boolean {
  if (!stack || stack.enabled === false || !stack.nodes) return false;
  return stack.nodes.some((n) => n.enabled !== false && (!isIdentityPrimaries(n.primaries) || hasCurves(n) || ((n.lut?.intensity ?? 1) > 0 && !!n.lut)));
}

export const hasCurves = (node: GradeNode): boolean => !!node.curves && (['y', 'r', 'g', 'b'] as const).some((c) => (node.curves?.[c]?.length ?? 0) > 0);

/** The group a clip belongs to, if any. */
export function colorGroupOf(doc: EditTimeline, clipId: string): ColorGroup | null {
  return doc.colorGroups?.find((g) => g.clipIds.includes(clipId)) ?? null;
}

/** The stacks that grade a clip, in application order: its group's, then its own. */
export function effectiveGrade(doc: EditTimeline, clip: MediaClip): GradeStack[] {
  const stacks: GradeStack[] = [];
  const group = colorGroupOf(doc, clip.id);
  if (group?.grade) stacks.push(group.grade);
  if (clip.grade) stacks.push(clip.grade);
  return stacks;
}

export function newNode(id: string, name = 'Correction'): GradeNode {
  return { id, name, enabled: true, primaries: {}, curves: null, lut: null };
}

/** The stack with one node replaced (by id); the stack's `enabled` is kept. */
export function withNode(stack: GradeStack | null | undefined, node: GradeNode): GradeStack {
  const nodes = stack?.nodes ?? [];
  const index = nodes.findIndex((n) => n.id === node.id);
  return { enabled: stack?.enabled ?? true, nodes: index < 0 ? [...nodes, node] : nodes.map((n, i) => (i === index ? node : n)) };
}

export function withoutNode(stack: GradeStack | null | undefined, nodeId: string): GradeStack | null {
  const nodes = (stack?.nodes ?? []).filter((n) => n.id !== nodeId);
  return nodes.length === 0 && (stack?.enabled ?? true) ? null : { enabled: stack?.enabled ?? true, nodes };
}

// ─────────────────────────────────────────────────────────────────────────────
// Look presets — the Colorist's words as primaries
// ─────────────────────────────────────────────────────────────────────────────

export const LOOKS = ['Warm', 'Cool', 'Filmic', 'Vibrant', 'Muted', 'Mono'] as const;
export type Look = (typeof LOOKS)[number];
export const STRENGTHS = ['Subtle', 'Normal', 'Strong'] as const;
export type Strength = (typeof STRENGTHS)[number];
export const SHADOW_TONES = ['Neutral', 'Lifted', 'Deepened'] as const;
export const HIGHLIGHT_TONES = ['Neutral', 'Softened', 'Brightened'] as const;

/**
 * A look as a grade node. The numbers mirror `ColorGradeFilterBuilder`'s table (colour cast as
 * lift/gamma wheels, contrast/saturation/gamma as scalars, tone words as lift/gain masters), scaled
 * by the strength word exactly as the compile step scales them, so accepting a Colorist proposal in
 * the editor grades the same way the compile would have — and then stays editable.
 */
export function lookNode(id: string, look: Look, strength: Strength = 'Normal', shadow: (typeof SHADOW_TONES)[number] = 'Neutral', highlight: (typeof HIGHLIGHT_TONES)[number] = 'Neutral'): GradeNode {
  const mult = strength === 'Subtle' ? 0.5 : strength === 'Strong' ? 1.5 : 1;
  return { ...lookNodeScaled(id, look, mult, shadow, highlight), name: `${look} (${strength})` };
}

/** A look at a continuous strength multiplier (0 = no change, 1 = Normal, 1.5 = Strong); the look gallery's builder. */
export function lookNodeScaled(id: string, look: Look, mult: number, shadow: (typeof SHADOW_TONES)[number] = 'Neutral', highlight: (typeof HIGHLIGHT_TONES)[number] = 'Neutral'): GradeNode {
  const cast: Record<Look, { rs: number; bs: number; rm: number; bm: number }> = {
    Warm: { rs: 0.1, bs: -0.1, rm: 0.05, bm: -0.05 },
    Cool: { rs: -0.1, bs: 0.1, rm: -0.05, bm: 0.05 },
    Filmic: { rs: 0, bs: 0.04, rm: 0, bm: 0 },
    Vibrant: { rs: 0, bs: 0, rm: 0, bm: 0 },
    Muted: { rs: 0, bs: 0, rm: 0, bm: 0 },
    Mono: { rs: 0, bs: 0, rm: 0, bm: 0 },
  };
  const tone: Record<Look, { contrast: number; saturation: number; gamma: number }> = {
    Warm: { contrast: 1, saturation: 1.06, gamma: 1 },
    Cool: { contrast: 1, saturation: 1.03, gamma: 1 },
    Filmic: { contrast: 1.1, saturation: 0.9, gamma: 0.97 },
    Vibrant: { contrast: 1.06, saturation: 1.28, gamma: 1 },
    Muted: { contrast: 0.96, saturation: 0.78, gamma: 1 },
    Mono: { contrast: 1.06, saturation: 0, gamma: 1 },
  };
  const scale = (target: number) => round(1 + (target - 1) * mult);
  const c = cast[look];
  const t = tone[look];
  const lift: GradeWheel = { r: round(c.rs * mult), b: round(c.bs * mult), master: shadow === 'Lifted' ? round(0.04 * mult) : shadow === 'Deepened' ? round(-0.04 * mult) : 0 };
  const gamma: GradeWheel = { r: round(c.rm * mult), b: round(c.bm * mult), master: t.gamma === 1 ? 0 : round((t.gamma - 1) * mult) };
  const gain: GradeWheel = { master: highlight === 'Softened' ? round(-0.05 * mult) : highlight === 'Brightened' ? round(0.06 * mult) : 0 };
  const primaries: GradePrimaries = {
    lift: isIdentityWheel(lift) ? null : lift,
    gamma: isIdentityWheel(gamma) ? null : gamma,
    gain: isIdentityWheel(gain) ? null : gain,
    contrast: scale(t.contrast),
    saturation: look === 'Mono' ? 0 : scale(t.saturation),
  };
  return { id, name: look, enabled: true, primaries, curves: null, lut: null };
}

const round = (v: number) => Math.round(v * 10000) / 10000;

// ─────────────────────────────────────────────────────────────────────────────
// Colour groups from the analysis look groups
// ─────────────────────────────────────────────────────────────────────────────

export interface LookGroup {
  id: string;
  shotIds: string[];
  representativeShotId: string | null;
  cohesion: number | null;
  colorTemperatureClass: string | null;
  toneClass: string | null;
}

/** The `lookGroups` (`k{n}`) of a VideoAnalyze artifact, read defensively. */
export function readLookGroups(artifact: unknown): LookGroup[] {
  if (typeof artifact !== 'object' || artifact === null) return [];
  const raw = (artifact as Record<string, unknown>).lookGroups ?? (artifact as Record<string, unknown>).LookGroups;
  if (!Array.isArray(raw)) return [];
  const out: LookGroup[] = [];
  raw.forEach((entry, index) => {
    if (typeof entry !== 'object' || entry === null) return;
    const e = entry as Record<string, unknown>;
    const idsRaw = e.shotIds ?? e.ShotIds;
    const shotIds = Array.isArray(idsRaw) ? idsRaw.filter((s): s is string => typeof s === 'string') : [];
    if (shotIds.length === 0) return;
    const str = (a: string, b: string) => (typeof (e[a] ?? e[b]) === 'string' ? ((e[a] ?? e[b]) as string) : null);
    const num = (a: string, b: string) => (typeof (e[a] ?? e[b]) === 'number' ? ((e[a] ?? e[b]) as number) : null);
    out.push({ id: str('id', 'Id') ?? `k${index}`, shotIds, representativeShotId: str('representativeShotId', 'RepresentativeShotId'), cohesion: num('cohesion', 'Cohesion'), colorTemperatureClass: str('colorTemperatureClass', 'ColorTemperatureClass'), toneClass: str('toneClass', 'ToneClass') });
  });
  return out;
}

/**
 * Colour groups pre-populated from the look groups: each look group becomes a group of the clips
 * on `trackId` that show one of its shots, skipping clips already grouped and look groups that end
 * up with no member. Pure; the caller dispatches one `setColorGroup` per result.
 */
export function colorGroupsFromLookGroups(doc: EditTimeline, trackId: string, shots: readonly AnalysedShot[], lookGroups: readonly LookGroup[], fps: number, newId: () => string, matches?: SourceMatcher): ColorGroup[] {
  const track = doc.tracks.find((t) => t.id === trackId);
  if (!track) return [];
  const taken = new Set((doc.colorGroups ?? []).flatMap((g) => g.clipIds));
  const existingLook = new Set((doc.colorGroups ?? []).map((g) => g.lookGroupId).filter((x): x is string => typeof x === 'string'));
  const byShot = new Map(shots.map((s) => [s.id, s] as const));
  const groups: ColorGroup[] = [];
  for (const look of lookGroups) {
    if (existingLook.has(look.id)) continue;
    const members: string[] = [];
    for (const item of track.items) {
      if (item.type !== 'mediaClip' || taken.has(item.id)) continue;
      const shows = look.shotIds.some((shotId) => {
        const shot = byShot.get(shotId);
        return shot !== undefined && mapSourceSilenceToRecord(item, { id: shot.id, startSec: shot.startSec, endSec: shot.endSec, sourceIndex: shot.sourceIndex }, fps, matches) !== null;
      });
      if (shows) {
        members.push(item.id);
        taken.add(item.id);
      }
    }
    if (members.length === 0) continue;
    const words = [look.colorTemperatureClass, look.toneClass].filter((w): w is string => !!w);
    groups.push({ id: newId(), name: `${look.id}${words.length ? ` · ${words.join(' / ')}` : ''}`, clipIds: members, grade: null, lookGroupId: look.id });
  }
  return groups;
}

// ─────────────────────────────────────────────────────────────────────────────
// Shot match
// ─────────────────────────────────────────────────────────────────────────────

/** Mean RGB (0..1) and luma of a frame, as the scopes compute them. */
export interface FrameStats {
  meanR: number;
  meanG: number;
  meanB: number;
  meanLuma: number;
  /** Standard deviation of luma, 0..1 — a contrast proxy. */
  lumaSpread: number;
}

/**
 * The primaries that move `target`'s statistics onto `reference`'s: a per-channel gain from the
 * mean ratio, an exposure from the luma ratio and a contrast from the spread ratio, all clamped
 * into the validator's ranges. A first-order match a colourist then refines — never a claim of
 * equality.
 */
export function shotMatchPrimaries(target: FrameStats, reference: FrameStats): GradePrimaries {
  const eps = 1e-3;
  const lumaRatio = (reference.meanLuma + eps) / (target.meanLuma + eps);
  const exposure = clamp(Math.log2(lumaRatio), -4, 4);
  // After exposure, residual per-channel balance as gain offsets around the master.
  const gainOf = (ref: number, tgt: number) => clamp((ref + eps) / ((tgt + eps) * lumaRatio) - 1, -1, 1);
  const gain: GradeWheel = { r: round(gainOf(reference.meanR, target.meanR)), g: round(gainOf(reference.meanG, target.meanG)), b: round(gainOf(reference.meanB, target.meanB)) };
  const contrast = clamp((reference.lumaSpread + eps) / (target.lumaSpread + eps), 0.25, 3);
  return { exposure: round(exposure), gain: isIdentityWheel(gain) ? null : gain, contrast: round(contrast), pivot: round(clamp(reference.meanLuma, 0, 1)) };
}

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));
