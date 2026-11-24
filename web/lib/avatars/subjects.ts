/**
 * Pure rules of the subject picker: which of a project's people and objects can be shown together in one clip
 * or picture (docs/design/avatar-objects.md "Decisions": at most 3 subjects, at most one of them a person).
 * No user-visible text lives here: every function answers a value or a key suffix the components look up.
 */
import type { AvatarSubjectKind, ProjectAvatar } from '../types/avatars';
import { isObjectAvatar, needsPictureConsent, subjectKindOf } from './model';
import { pickerGroups, type PickerGroup } from './step-picker';

/** The most subjects one clip or picture can show. A server may report a smaller number for a model. */
export const MAX_SUBJECTS = 3;
/** The most people among them. */
export const MAX_PEOPLE = 1;

export interface SubjectRules {
  /** A picture, not a video: a person whose consent does not cover pictures yet is not offered. */
  forPictures?: boolean;
  /** The most subjects allowed here (defaults to {@link MAX_SUBJECTS}; never more than it). */
  max?: number;
}

/** What is wrong with a chosen set, as key suffixes under `avatars.subjects.problems`. */
export type SubjectProblem = 'tooMany' | 'secondPerson';

/**
 * Why one avatar cannot be ticked right now, as a key suffix under `avatars.subjects.blocked`, or null:
 * `unusable` (the server's one rule says no; the reason words come from `useAvatarWords`), `noPictureConsent`
 * (a person confirmed under the older statement, pictures only), `tooMany`, `secondPerson`.
 */
export type SubjectBlock = 'unusable' | 'noPictureConsent' | 'tooMany' | 'secondPerson';

/** The limit in force: the caller's, kept inside 1..{@link MAX_SUBJECTS}. */
export function subjectLimit(max?: number | null): number {
  if (typeof max !== 'number' || !Number.isFinite(max)) return MAX_SUBJECTS;
  return Math.min(MAX_SUBJECTS, Math.max(1, Math.floor(max)));
}

/** Why the avatar can never be chosen here, whatever else is ticked. */
export function subjectUnusable(avatar: ProjectAvatar, rules: SubjectRules = {}): 'unusable' | 'noPictureConsent' | null {
  if (!avatar.selectable) return 'unusable';
  if (rules.forPictures && needsPictureConsent(avatar)) return 'noPictureConsent';
  return null;
}

/** The avatars that can be chosen here at all, in the picker's order (people first, then objects). */
export function usableSubjects(avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): ProjectAvatar[] {
  return pickerGroups(avatars).flatMap((g) => g.avatars).filter((a) => subjectUnusable(a, rules) === null);
}

/** The chosen avatars the list still holds, in the order they were chosen. An id the project no longer has is left out. */
export function chosenSubjects(ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined): ProjectAvatar[] {
  const byId = new Map((avatars ?? []).map((a) => [a.id, a]));
  return ids.flatMap((id) => {
    const a = byId.get(id);
    return a ? [a] : [];
  });
}

const peopleIn = (list: readonly ProjectAvatar[]): number => list.filter((a) => !isObjectAvatar(a)).length;

/** What is wrong with the chosen set as a whole (empty = fine). An empty set is fine here: the caller decides whether one is needed. */
export function subjectProblems(ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): SubjectProblem[] {
  const out: SubjectProblem[] = [];
  if (new Set(ids).size > subjectLimit(rules.max)) out.push('tooMany');
  if (peopleIn(chosenSubjects([...new Set(ids)], avatars)) > MAX_PEOPLE) out.push('secondPerson');
  return out;
}

/** Why `avatar` cannot be added to the chosen set right now, or null when it can. An avatar already chosen is never blocked (it can be unticked). */
export function subjectBlock(avatar: ProjectAvatar, ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): SubjectBlock | null {
  if (ids.includes(avatar.id)) return null;
  const never = subjectUnusable(avatar, rules);
  if (never) return never;
  if (new Set(ids).size >= subjectLimit(rules.max)) return 'tooMany';
  if (!isObjectAvatar(avatar) && peopleIn(chosenSubjects(ids, avatars)) >= MAX_PEOPLE) return 'secondPerson';
  return null;
}

/** True when `avatar` may be added to the chosen set. */
export function canAddSubject(avatar: ProjectAvatar, ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): boolean {
  return subjectBlock(avatar, ids, avatars, rules) === null;
}

/** Ticks or unticks one avatar; adding one the rules refuse changes nothing. The order is the order chosen. */
export function toggleSubject(ids: readonly string[], avatar: ProjectAvatar, avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): string[] {
  if (ids.includes(avatar.id)) return ids.filter((id) => id !== avatar.id);
  return canAddSubject(avatar, ids, avatars, rules) ? [...ids, avatar.id] : [...ids];
}

/**
 * The chosen ids that can really be sent: those the project still has and that are usable here. A chosen avatar
 * that became unusable meanwhile (a file removed, consent withdrawn) is shown ticked with its reason, and is
 * what this leaves out.
 */
export function sendableSubjectIds(ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): string[] {
  return chosenSubjects([...new Set(ids)], avatars).filter((a) => subjectUnusable(a, rules) === null).map((a) => a.id);
}

/** True when a chosen avatar is no longer usable here (or no longer in the project). */
export function hasUnusableChosen(ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined, rules: SubjectRules = {}): boolean {
  return sendableSubjectIds(ids, avatars, rules).length !== new Set(ids).size;
}

/**
 * The one person a speech file can drive: exactly one subject is chosen and it is a person. With an object in the
 * set, or nobody, nothing talks (the server takes `speechFileId` for one person only).
 */
export function talkingSubject(ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined): ProjectAvatar | null {
  const chosen = chosenSubjects([...new Set(ids)], avatars);
  return chosen.length === 1 && !isObjectAvatar(chosen[0]) ? chosen[0] : null;
}

/** The picker's groups, people first; a kind with none is left out. */
export function subjectGroups(avatars: readonly ProjectAvatar[] | null | undefined): PickerGroup[] {
  return pickerGroups(avatars);
}

/** The names of the chosen avatars, in the order chosen. */
export function subjectNames(ids: readonly string[], avatars: readonly ProjectAvatar[] | null | undefined): string[] {
  return chosenSubjects(ids, avatars).map((a) => a.name);
}

/** How many of each kind the project has. */
export function subjectCounts(avatars: readonly ProjectAvatar[] | null | undefined): Record<AvatarSubjectKind, number> {
  const counts: Record<AvatarSubjectKind, number> = { Person: 0, Object: 0 };
  for (const a of avatars ?? []) counts[subjectKindOf(a)] += 1;
  return counts;
}
