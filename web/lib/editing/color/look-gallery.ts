/**
 * Look-first colour (R10.1, R10.2): the pure helpers behind the Color workspace's front door.
 * A look is one named grade node (`Look: Warm 100%`) in the clip's ordinary P7 stack, so it stays an
 * editable, bypassable node under Advanced; the five plain scalars are one node named `Fix`; a match
 * is one node named `Match`. Every builder returns a whole stack, so one `setProperty grade` is one
 * undo step, and `applyGradeToClips` composes those into a single batch for "apply to many".
 */

import type { EditTimeline, GradeNode, GradePrimaries, GradeStack, GradeWheel, MediaClip } from '../types';
import { IDENTITY_PRIMARIES, isIdentityPrimaries, isIdentityWheel, lookNodeScaled, LOOKS, withNode, withoutNode, type Look } from './grade';
import { applyPreviewGradeNodes, previewGradeNodesOf, type PreviewGradeNode } from './preview-grade';

export const GALLERY_LOOKS = [...LOOKS, 'Teal & orange'] as const;
export type GalleryLook = (typeof GALLERY_LOOKS)[number];

/** Strength is a percentage: 0 = no change, 100 = the look as the Colorist writes it, 150 = Strong. */
export const MIN_STRENGTH = 0;
export const MAX_STRENGTH = 150;
export const DEFAULT_STRENGTH = 100;

export const LOOK_PREFIX = 'Look: ';
export const FIX_NODE_NAME = 'Fix';
export const MATCH_NODE_NAME = 'Match';

const round = (v: number) => Math.round(v * 10000) / 10000;
const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

/** The strength percentage as the multiplier the look tables scale by (strength interpolation). */
export const strengthMultiplier = (pct: number): number => round(clamp(pct, MIN_STRENGTH, MAX_STRENGTH) / 100);

/** Teal shadows, orange highlights: a blockbuster split tone with a little contrast and colour. */
function tealOrange(id: string, mult: number): GradeNode {
  const scale = (target: number) => round(1 + (target - 1) * mult);
  const lift: GradeWheel = { r: round(-0.06 * mult), g: round(0.01 * mult), b: round(0.08 * mult) };
  const gamma: GradeWheel = { r: round(0.02 * mult), b: round(-0.02 * mult) };
  const gain: GradeWheel = { r: round(0.08 * mult), b: round(-0.07 * mult) };
  const primaries: GradePrimaries = {
    lift: isIdentityWheel(lift) ? null : lift,
    gamma: isIdentityWheel(gamma) ? null : gamma,
    gain: isIdentityWheel(gain) ? null : gain,
    contrast: scale(1.08),
    saturation: scale(1.1),
  };
  return { id, name: 'Teal & orange', enabled: true, primaries, curves: null, lut: null };
}

/** A gallery look at a strength percentage, named so the stack can find it again. */
export function lookLayerNode(id: string, look: GalleryLook, strengthPct: number): GradeNode {
  const pct = Math.round(clamp(strengthPct, MIN_STRENGTH, MAX_STRENGTH));
  const mult = strengthMultiplier(pct);
  const node = look === 'Teal & orange' ? tealOrange(id, mult) : lookNodeScaled(id, look as Look, mult);
  return { ...node, name: `${LOOK_PREFIX}${look} ${pct}%` };
}

const LOOK_NAME = /^Look: (.+) (\d+)%$/;
const isLookNode = (n: GradeNode) => typeof n.name === 'string' && LOOK_NAME.test(n.name);

/** The look layer of a stack (the first `Look: ...` node), or null. */
export function currentLook(stack: GradeStack | null | undefined): { look: GalleryLook; strength: number; nodeId: string } | null {
  for (const node of stack?.nodes ?? []) {
    const m = node.name ? LOOK_NAME.exec(node.name) : null;
    if (m && (GALLERY_LOOKS as readonly string[]).includes(m[1])) return { look: m[1] as GalleryLook, strength: Number(m[2]), nodeId: node.id };
  }
  return null;
}

/** The stack with its look layer set (replaced in place, so order and the id stay); strength 0 removes it. */
export function withLook(stack: GradeStack | null | undefined, newId: () => string, look: GalleryLook, strengthPct: number): GradeStack | null {
  const existing = (stack?.nodes ?? []).find(isLookNode);
  if (strengthPct <= 0) return existing ? withoutNode(stack, existing.id) : (stack ?? null);
  return withNode(stack, lookLayerNode(existing?.id ?? newId(), look, strengthPct));
}

export function withoutLook(stack: GradeStack | null | undefined): GradeStack | null {
  const existing = (stack?.nodes ?? []).find(isLookNode);
  return existing ? withoutNode(stack, existing.id) : (stack ?? null);
}

// ── the five plain scalars ───────────────────────────────────────────────────

export const FIX_KEYS = ['brightness', 'contrast', 'saturation', 'warmth', 'tint'] as const;
export type FixKey = (typeof FIX_KEYS)[number];
export type FixValues = Record<FixKey, number>;

export const FIX_IDENTITY: Readonly<FixValues> = Object.freeze({ brightness: 0, contrast: 1, saturation: 1, warmth: 0, tint: 0 });

/** Slider ranges inside the validator's (exposure -4..4, contrast 0.25..3, saturation 0..3, temperature/tint -1..1). */
export const FIX_SPEC: Readonly<Record<FixKey, { label: string; min: number; max: number; step: number; hint: string }>> = {
  brightness: { label: 'Brightness', min: -1.5, max: 1.5, step: 0.05, hint: 'Lighter or darker, in stops' },
  contrast: { label: 'Contrast', min: 0.5, max: 1.8, step: 0.02, hint: 'Flatter or punchier' },
  saturation: { label: 'Saturation', min: 0, max: 2, step: 0.02, hint: 'Colour amount' },
  warmth: { label: 'Warmth', min: -1, max: 1, step: 0.02, hint: 'Cooler or warmer' },
  tint: { label: 'Tint', min: -1, max: 1, step: 0.02, hint: 'Greener or more magenta' },
};

/** The scalars as the `Fix` node's primaries (brightness is exposure, warmth is temperature). */
export function fixPrimaries(v: FixValues): GradePrimaries {
  return { exposure: round(v.brightness), contrast: round(v.contrast), saturation: round(v.saturation), temperature: round(v.warmth), tint: round(v.tint) };
}

export function fixValuesOf(stack: GradeStack | null | undefined): FixValues {
  const p = stack?.nodes?.find((n) => n.name === FIX_NODE_NAME)?.primaries;
  return {
    brightness: p?.exposure ?? IDENTITY_PRIMARIES.exposure,
    contrast: p?.contrast ?? IDENTITY_PRIMARIES.contrast,
    saturation: p?.saturation ?? IDENTITY_PRIMARIES.saturation,
    warmth: p?.temperature ?? IDENTITY_PRIMARIES.temperature,
    tint: p?.tint ?? IDENTITY_PRIMARIES.tint,
  };
}

/** The stack with its `Fix` node set from the scalars; all-identity scalars remove the node. */
export function withFix(stack: GradeStack | null | undefined, newId: () => string, values: FixValues): GradeStack | null {
  const existing = stack?.nodes?.find((n) => n.name === FIX_NODE_NAME);
  const primaries = fixPrimaries(values);
  if (isIdentityPrimaries(primaries)) return existing ? withoutNode(stack, existing.id) : (stack ?? null);
  return withNode(stack, { id: existing?.id ?? newId(), name: FIX_NODE_NAME, enabled: true, primaries, curves: null, lut: null });
}

// ── match (strength) ────────────────────────────────────────────────────────

/** Primaries moved `mult` of the way from identity (0 = none, 1 = full) — match strength. */
export function scalePrimaries(p: GradePrimaries, mult: number): GradePrimaries {
  const toward = (v: number | undefined, identity: number) => round(identity + ((v ?? identity) - identity) * mult);
  const wheel = (w: GradeWheel | null | undefined): GradeWheel | null => {
    if (!w) return null;
    const scaled: GradeWheel = { r: round((w.r ?? 0) * mult), g: round((w.g ?? 0) * mult), b: round((w.b ?? 0) * mult), master: round((w.master ?? 0) * mult) };
    return isIdentityWheel(scaled) ? null : scaled;
  };
  return {
    exposure: toward(p.exposure, 0),
    contrast: toward(p.contrast, 1),
    saturation: toward(p.saturation, 1),
    temperature: toward(p.temperature, 0),
    tint: toward(p.tint, 0),
    pivot: p.pivot,
    lift: wheel(p.lift),
    gamma: wheel(p.gamma),
    gain: wheel(p.gain),
  };
}

/** The stack with its `Match` node set (replaced in place), from primaries already scaled by the strength. */
export function withMatch(stack: GradeStack | null | undefined, newId: () => string, primaries: GradePrimaries): GradeStack {
  const existing = stack?.nodes?.find((n) => n.name === MATCH_NODE_NAME);
  return withNode(stack, { id: existing?.id ?? newId(), name: MATCH_NODE_NAME, enabled: true, primaries, curves: null, lut: null });
}

// ── apply to many ───────────────────────────────────────────────────────────

export type ApplyScope = 'selected' | 'all' | 'sameFile';

export const mediaClipsOf = (doc: EditTimeline): MediaClip[] => doc.tracks.flatMap((t) => t.items).filter((i): i is MediaClip => i.type === 'mediaClip');

/** The clips a scope covers: the selected media clips, every media clip, or every clip of the same source file as `anchor`. */
export function clipsInScope(doc: EditTimeline, scope: ApplyScope, selection: readonly string[], anchor: MediaClip | null): MediaClip[] {
  const clips = mediaClipsOf(doc);
  if (scope === 'all') return clips;
  if (scope === 'selected') return clips.filter((c) => selection.includes(c.id));
  if (!anchor) return [];
  const key = JSON.stringify(anchor.assetRef);
  return clips.filter((c) => JSON.stringify(c.assetRef) === key);
}

/** Re-ids a copied grade's nodes so two clips never share a node id. */
export function cloneStack(stack: GradeStack, newId: () => string): GradeStack {
  return { ...structuredClone(stack), nodes: (stack.nodes ?? []).map((n) => ({ ...structuredClone(n), id: newId() })) };
}

/** One `setProperty grade` per clip, giving each a grade from `produce` (its current stack in, the new one out). */
export function applyGradeToClips(clips: readonly MediaClip[], produce: (clip: MediaClip) => GradeStack | null) {
  return clips.map((c) => ({ type: 'setProperty' as const, itemId: c.id, key: 'grade' as const, value: produce(c) }));
}

// ── thumbnails ──────────────────────────────────────────────────────────────

/** A look's preview grade nodes (what the compositor would draw) — used to render gallery thumbnails. */
export function lookPreviewGrade(look: GalleryLook, strengthPct: number): PreviewGradeNode[] {
  return previewGradeNodesOf([{ enabled: true, nodes: [lookLayerNode('thumb', look, strengthPct)] }]);
}

/** Grades an RGBA buffer in place with `grade` (the same maths as the viewer shader). */
export function gradeRgba(data: Uint8ClampedArray, grade: readonly PreviewGradeNode[]): void {
  for (let i = 0; i < data.length; i += 4) {
    const [r, g, b] = applyPreviewGradeNodes(grade, [data[i] / 255, data[i + 1] / 255, data[i + 2] / 255]);
    data[i] = Math.round(r * 255);
    data[i + 1] = Math.round(g * 255);
    data[i + 2] = Math.round(b * 255);
  }
}
