import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, GradeStack, MediaClip, TitleClip, TitleTemplate } from '@/lib/editing/types';
import {
  currentLook, DEFAULT_STRENGTH, FIX_KEYS, FIX_NODE_NAME, FIX_SPEC, fixValuesOf, withFix, withLook, withoutLook,
  type FixKey, type FixValues, type GalleryLook,
} from '@/lib/editing/color/look-gallery';
import { buildGraphicOps } from '@/lib/editing/structure/graphics';
import { sanitizeTitleText } from '@/lib/editing/title-text';
import type { BuildResult } from '@/lib/editing/timeline-edit/clipboard';

/**
 * RM.10 / R10.6: the pure builders behind the phone "Look and fix" sheet. Every one composes the helpers the
 * desktop Look panel already uses (look-gallery, graphics), so a tap here and a click there store the same
 * document. No new op, no new field. Each result is the ops of ONE undo step.
 */

export interface LookState {
  /** The picture clip the look controls act on. */
  target: MediaClip;
  look: { look: GalleryLook; strength: number } | null;
  fix: FixValues;
  /** Colour work the phone does not edit (wheels, curves, keyer, match): kept untouched, shown read-only. */
  extraAdjustments: number;
  /** Picture clips on the timeline (what "Apply to all clips" reaches). */
  clipCount: number;
}

/** The picture clips of a timeline (a grade is picture work; sound clips are left alone). */
export function pictureClips(doc: EditTimeline): MediaClip[] {
  return doc.tracks.filter((t) => t.kind === 'video').flatMap((t) => t.items).filter((i): i is MediaClip => i.type === 'mediaClip');
}

/** The clip the look controls act on: the selected picture clip, else the one under the playhead. */
export function lookTargetOf(doc: EditTimeline, selection: readonly string[], playheadFrame: number): MediaClip | null {
  const clips = pictureClips(doc);
  const selected = clips.find((c) => selection.includes(c.id));
  if (selected) return selected;
  const under = clips.filter((c) => c.recordIn <= playheadFrame && playheadFrame < c.recordOut && c.enabled !== false);
  return under.length > 0 ? under[under.length - 1] : (clips.find((c) => c.recordIn >= playheadFrame) ?? null);
}

export function lookStateOf(doc: EditTimeline, target: MediaClip | null): LookState | null {
  if (!target) return null;
  const grade = target.grade ?? null;
  const look = currentLook(grade);
  const extra = (grade?.nodes ?? []).filter((n) => !((n.name ?? '').startsWith('Look: ') || n.name === FIX_NODE_NAME)).length;
  return { target, look: look ? { look: look.look, strength: look.strength } : null, fix: fixValuesOf(grade), extraAdjustments: extra, clipCount: pictureClips(doc).length };
}

export type LookChange =
  | { kind: 'look'; look: GalleryLook | null }
  | { kind: 'strength'; percent: number }
  | { kind: 'fix'; key: FixKey; value: number }
  | { kind: 'resetFix' };

const gradeOp = (itemId: string, value: GradeStack | null): EditOp => ({ type: 'setProperty', itemId, key: 'grade', value });

export function lookChangeLabel(change: LookChange): string {
  switch (change.kind) {
    case 'look': return change.look ? `${change.look} look` : 'Remove look';
    case 'strength': return 'Look strength';
    case 'fix': return FIX_SPEC[change.key].label;
    case 'resetFix': return 'Reset fixes';
  }
}

/** One look change on the target: ONE `setProperty grade`. */
export function buildLookOps(target: MediaClip, change: LookChange, newId: () => string): BuildResult<{ ops: EditOp[]; label: string }> {
  const grade = target.grade ?? null;
  const look = currentLook(grade);
  const fix = fixValuesOf(grade);
  let next: GradeStack | null;
  switch (change.kind) {
    case 'look':
      next = change.look ? withLook(grade, newId, change.look, look?.strength ?? DEFAULT_STRENGTH) : withoutLook(grade);
      break;
    case 'strength':
      if (!look) return { ok: false, reason: 'Pick a look first, then set how strong it is.' };
      next = withLook(grade, newId, look.look, change.percent);
      break;
    case 'fix': {
      const spec = FIX_SPEC[change.key];
      if (!Number.isFinite(change.value) || change.value < spec.min || change.value > spec.max) {
        return { ok: false, reason: `${spec.label} is between ${spec.min} and ${spec.max}. The old value is kept.` };
      }
      next = withFix(grade, newId, { ...fix, [change.key]: change.value });
      break;
    }
    case 'resetFix':
      next = withFix(grade, newId, { brightness: 0, contrast: 1, saturation: 1, warmth: 0, tint: 0 });
      break;
  }
  return { ok: true, ops: [gradeOp(target.id, next)], label: lookChangeLabel(change) };
}

/** "Apply to all clips": the target's look and fixes onto every picture clip, each keeping its other adjustments. One batch. */
export function buildApplyToAllOps(doc: EditTimeline, target: MediaClip, newId: () => string): BuildResult<{ ops: EditOp[]; count: number }> {
  const grade = target.grade ?? null;
  const look = currentLook(grade);
  const fix = fixValuesOf(grade);
  const hasFix = FIX_KEYS.some((k) => fix[k] !== fixValuesOf(null)[k]);
  if (!look && !hasFix) return { ok: false, reason: 'Pick a look or change a setting first, then apply it to all clips.' };
  const clips = pictureClips(doc);
  const ops = clips.map((c) => {
    const withL = look ? withLook(c.grade, newId, look.look, look.strength) : withoutLook(c.grade);
    return gradeOp(c.id, withFix(withL, newId, fix));
  });
  return { ok: true, ops, count: clips.length };
}

// ── titles ───────────────────────────────────────────────────────────────────

/** The selected title clip, if the selection is exactly one. */
export function selectedTitleOf(doc: EditTimeline, selection: readonly string[]): TitleClip | null {
  if (selection.length !== 1) return null;
  for (const t of doc.tracks) for (const i of t.items) if (i.id === selection[0] && i.type === 'titleClip') return i as TitleClip;
  return null;
}

/** Add a title at the playhead (the Wave 2 title path). Empty text is refused in plain words. */
export function buildAddTitle(doc: EditTimeline, text: string, template: TitleTemplate, at: number, newId: () => string): BuildResult<{ ops: EditOp[]; itemId: string; label: string }> {
  const clean = sanitizeTitleText(text).trim();
  if (clean.length === 0) return { ok: false, reason: 'Type the words for the title first.' };
  const r = buildGraphicOps(doc, { spec: { kind: 'title', text: clean, template }, at, newId });
  return { ok: true, ops: r.ops, itemId: r.itemId, label: r.label };
}

/** New words for a title clip. */
export function buildTitleTextOps(title: TitleClip, text: string): BuildResult<{ ops: EditOp[]; label: string }> {
  const clean = sanitizeTitleText(text).trim();
  if (clean.length === 0) return { ok: false, reason: 'A title needs some words. The old words are kept.' };
  if (clean === title.text) return { ok: false, reason: 'The title already says that.' };
  return { ok: true, ops: [{ type: 'setProperty', itemId: title.id, key: 'text', value: clean }], label: 'Edit title text' };
}

export function buildTitleTemplateOps(title: TitleClip, template: TitleTemplate): BuildResult<{ ops: EditOp[]; label: string }> {
  if ((title.template ?? 'plain') === template) return { ok: false, reason: 'The title already has that style.' };
  return { ok: true, ops: [{ type: 'setProperty', itemId: title.id, key: 'template', value: template === 'plain' ? null : template }], label: 'Title style' };
}
