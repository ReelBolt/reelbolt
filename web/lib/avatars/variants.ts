/**
 * Pure helpers of avatar variants (docs/design/avatar-objects.md "Avatar variants"): the same car in another
 * colour, the same bottle with another label. A variant is a complete avatar of its own, grouped under its
 * original and shown as `Car · Red` wherever a name is shown.
 * No user-visible text lives here: every function answers a value or a key suffix the components look up in
 * `avatars.json` (`avatars.variants.*`).
 */
import type { AvatarKindAvailability, AvatarSubjectKind, ProjectAvatar } from '../types/avatars';
import { isLinkedAvatar } from './library';

// The kind is read here, not through `model.ts`, which itself names avatars with `displayName`.
const kindOf = (avatar: Pick<ProjectAvatar, 'subjectKind'>): AvatarSubjectKind => (avatar.subjectKind === 'Object' ? 'Object' : 'Person');

/** The most variants one avatar can have (`409 VARIANT_LIMIT_REACHED` past it). */
export const MAX_VARIANTS = 10;
export const VARIANT_LABEL_MAX = 60;
export const VARIANT_DESCRIPTION_MAX = 500;
export const VARIANT_INSTRUCTION_MAX = 1000;

/** What joins an original's name and a variant's label: `Car · Red`. */
export const VARIANT_NAME_SEPARATOR = ' · ';

type VariantFields = Pick<ProjectAvatar, 'id' | 'name' | 'variantOfAvatarId' | 'variantLabel'>;

/** True for a variant: an avatar that names its original. One whose original was deleted names none and is ordinary. */
export function isVariant(avatar: Pick<ProjectAvatar, 'variantOfAvatarId'> | null | undefined): boolean {
  return typeof avatar?.variantOfAvatarId === 'string' && avatar.variantOfAvatarId.length > 0;
}

/**
 * Whether this server knows variants: it then sends `variantCount` (a number) or `variantOfAvatarId` (a string
 * or null) on its avatars. A server from before variants sends neither, and no variant UI is shown.
 */
export function supportsVariants(avatars: readonly Pick<ProjectAvatar, 'variantCount' | 'variantOfAvatarId'>[] | null | undefined): boolean {
  return (avatars ?? []).some((a) => typeof a.variantCount === 'number' || a.variantOfAvatarId !== undefined);
}

/**
 * The original a variant belongs to, among the avatars given, or null (not a variant, or its original is not
 * listed). Variants are one level deep; a chain a later server might send is followed to its top, bounded.
 */
export function originalOf<T extends VariantFields>(avatar: VariantFields | null | undefined, avatars: readonly T[] | null | undefined): T | null {
  if (!avatar || !isVariant(avatar)) return null;
  const byId = new Map((avatars ?? []).map((a) => [a.id, a]));
  let found = byId.get(avatar.variantOfAvatarId!) ?? null;
  for (let hops = 0; found && isVariant(found) && hops < 4; hops += 1) {
    const up = byId.get(found.variantOfAvatarId!);
    if (!up || up.id === avatar.id) break;
    found = up;
  }
  return found && found.id !== avatar.id ? found : null;
}

/**
 * The name to show: `Car · Red` for a variant whose original is among `originals` and that has a label, else
 * the avatar's own name (an ordinary avatar, a variant whose original is gone, a server from before variants).
 */
export function displayName(avatar: VariantFields, originals: readonly VariantFields[] | null | undefined): string {
  const original = originalOf(avatar, originals);
  const label = (avatar.variantLabel ?? '').trim();
  return original && label ? `${original.name}${VARIANT_NAME_SEPARATOR}${label}` : avatar.name;
}

/** A function that names any avatar of one list (`displayName` bound to that list). */
export function displayNamer(avatars: readonly VariantFields[] | null | undefined): (avatar: VariantFields) => string {
  return (avatar) => displayName(avatar, avatars);
}

/** The own name a new variant gets: `Car · Red`, cut to the longest name allowed. */
export function variantDefaultName(original: Pick<ProjectAvatar, 'name'>, label: string, maxLength = 200): string {
  return `${original.name}${VARIANT_NAME_SEPARATOR}${label.trim()}`.slice(0, Math.max(1, maxLength)).trim();
}

/** One card of the Avatars tab: an avatar and the variants shown under it as chips. */
export interface AvatarGroup<T extends VariantFields = ProjectAvatar> {
  avatar: T;
  variants: T[];
}

/**
 * The avatars as cards, in the order given: each original with its variants under it (by label). A variant
 * whose original is not in the list (deleted meanwhile, filtered out) is a card of its own, like any avatar.
 */
export function groupByOriginal<T extends VariantFields>(avatars: readonly T[] | null | undefined): AvatarGroup<T>[] {
  const list = avatars ?? [];
  const groups: AvatarGroup<T>[] = [];
  const byOriginal = new Map<string, AvatarGroup<T>>();
  const parents = new Map<string, T>();
  for (const a of list) {
    const original = originalOf(a, list);
    if (original) parents.set(a.id, original);
  }
  for (const a of list) {
    if (parents.has(a.id)) continue;
    const group: AvatarGroup<T> = { avatar: a, variants: [] };
    groups.push(group);
    byOriginal.set(a.id, group);
  }
  for (const a of list) {
    const original = parents.get(a.id);
    if (original) byOriginal.get(original.id)?.variants.push(a);
  }
  for (const g of groups) {
    g.variants.sort((x, y) => (x.variantLabel ?? x.name).localeCompare(y.variantLabel ?? y.name));
  }
  return groups;
}

/** The variants of one avatar among the avatars given, by label. */
export function variantsOf<T extends VariantFields>(original: Pick<ProjectAvatar, 'id'>, avatars: readonly T[] | null | undefined): T[] {
  return groupByOriginal(avatars).find((g) => g.avatar.id === original.id)?.variants ?? [];
}

/**
 * The avatars that count toward the per-kind limit of a project: variants do not. A variant whose original is
 * gone names none and counts again, as it does on the server.
 */
export function countedAvatars<T extends Pick<ProjectAvatar, 'variantOfAvatarId'>>(avatars: readonly T[] | null | undefined): T[] {
  return (avatars ?? []).filter((a) => !isVariant(a));
}

/** How many variants an avatar has: the server's count, or the listed ones when that is larger or missing. */
export function variantCountOf(original: Pick<ProjectAvatar, 'id' | 'variantCount'>, avatars: readonly VariantFields[] | null | undefined): number {
  const listed = (avatars ?? []).filter((a) => a.variantOfAvatarId === original.id).length;
  const said = typeof original.variantCount === 'number' && Number.isFinite(original.variantCount) ? Math.max(0, Math.floor(original.variantCount)) : 0;
  return Math.max(listed, said);
}

/**
 * Why "Add a variant" is not offered on an avatar, or null when it is:
 * - `unsupported`: a server from before variants;
 * - `isVariant`: variants are one level deep, a variant has none of its own;
 * - `linked`: an import from the workspace library follows its library item;
 * - `withdrawn`: a person whose consent was withdrawn;
 * - `notInPlan`: the kind is not in the workspace's plan;
 * - `limit`: it already has the most variants it can (the tab says so in a plain line).
 */
export type AddVariantBlock = 'unsupported' | 'isVariant' | 'linked' | 'withdrawn' | 'notInPlan' | 'limit';

export function addVariantBlock(
  avatar: ProjectAvatar,
  avatars: readonly ProjectAvatar[] | null | undefined,
  kinds: Record<AvatarSubjectKind, AvatarKindAvailability>,
): AddVariantBlock | null {
  if (!supportsVariants(avatars)) return 'unsupported';
  if (isVariant(avatar)) return 'isVariant';
  if (isLinkedAvatar(avatar)) return 'linked';
  if (kindOf(avatar) === 'Person' && avatar.consent?.revokedAt) return 'withdrawn';
  if (!kinds[kindOf(avatar)].entitled) return 'notInPlan';
  if (variantCountOf(avatar, avatars) >= MAX_VARIANTS) return 'limit';
  return null;
}

/** True when a variant can be added to this avatar right now. */
export function canAddVariant(
  avatar: ProjectAvatar,
  avatars: readonly ProjectAvatar[] | null | undefined,
  kinds: Record<AvatarSubjectKind, AvatarKindAvailability>,
): boolean {
  return addVariantBlock(avatar, avatars, kinds) === null;
}

/**
 * Whether the picture model may make the variant: objects only (a person gets variants from other files), and
 * the object must be usable as it is.
 */
export type AiVariantBlock = 'person' | 'unusable';

export function aiVariantBlock(avatar: Pick<ProjectAvatar, 'subjectKind' | 'selectable'>): AiVariantBlock | null {
  if (kindOf(avatar) === 'Person') return 'person';
  return avatar.selectable ? null : 'unusable';
}

/** The labels an original's variants already use, for the "already used" check. */
export function takenVariantLabels(original: Pick<ProjectAvatar, 'id'>, avatars: readonly VariantFields[] | null | undefined, exceptId?: string): string[] {
  return (avatars ?? [])
    .filter((a) => a.variantOfAvatarId === original.id && a.id !== exceptId)
    .map((a) => (a.variantLabel ?? '').trim())
    .filter(Boolean);
}

/** What is wrong with a text, as a key suffix under `avatars.variants.problems` (null = fine). */
export type VariantTextProblem = 'required' | 'tooLong';
export type VariantLabelProblem = VariantTextProblem | 'taken';

const sameLabel = (a: string, b: string) => a.trim().toLocaleLowerCase() === b.trim().toLocaleLowerCase();

/** A label is 1..60 characters and not already used by another variant of the same original. */
export function variantLabelProblem(label: string, taken: readonly string[] = []): VariantLabelProblem | null {
  const text = label.trim();
  if (!text) return 'required';
  if (text.length > VARIANT_LABEL_MAX) return 'tooLong';
  return taken.some((other) => sameLabel(other, text)) ? 'taken' : null;
}

/** The description of a variant is required: 1..500 characters. */
export function variantDescriptionProblem(description: string): VariantTextProblem | null {
  const text = description.trim();
  if (!text) return 'required';
  return text.length > VARIANT_DESCRIPTION_MAX ? 'tooLong' : null;
}

/** What to change is required: 1..1000 characters. */
export function variantInstructionProblem(instruction: string): VariantTextProblem | null {
  const text = instruction.trim();
  if (!text) return 'required';
  return text.length > VARIANT_INSTRUCTION_MAX ? 'tooLong' : null;
}

/** The description a model-made variant starts with: the instruction, cut to the longest description allowed. */
export function descriptionFromInstruction(instruction: string): string {
  return instruction.trim().slice(0, VARIANT_DESCRIPTION_MAX).trim();
}

/** The usable pictures and videos of an original that the picture model can change, in the avatar's order. */
export function changeableReferences(avatar: Pick<ProjectAvatar, 'references'>): { pictures: ProjectAvatar['references']; videos: ProjectAvatar['references'] } {
  const ok = avatar.references.filter((r) => r.state === 'ok');
  return { pictures: ok.filter((r) => r.kind === 'Image'), videos: ok.filter((r) => r.kind === 'Video') };
}
