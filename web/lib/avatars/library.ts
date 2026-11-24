/**
 * Pure helpers of the workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library").
 * No user-visible text lives here: every function answers a value or a key suffix the component looks up in
 * `avatars.json` (`avatars.library.*`).
 */
import type {
  AvatarKindAvailability, AvatarLibraryRequirements, AvatarLimits, AvatarRequirements, AvatarSubjectKind, ProjectAvatar,
} from '../types/avatars';
import type { AvatarLibraryItem, AvatarLibraryListResponse } from '../types/avatar-library';

/** The limits the library dialogs read: the keep-for range, the grace period after a withdrawal, the text lengths. */
export type LibraryLimits = Pick<AvatarLimits,
  'minRetentionDays' | 'maxRetentionDays' | 'defaultRetentionDays' | 'revokedGraceDays' | 'maxNameLength' | 'maxDescriptionLength'>;

/** The limits the workspace page falls back on when no project could tell it (the contract's own figures). */
export const LIBRARY_FALLBACK_LIMITS: Readonly<LibraryLimits> = {
  minRetentionDays: 1,
  maxRetentionDays: 730,
  defaultRetentionDays: 365,
  revokedGraceDays: 30,
  maxNameLength: 200,
  maxDescriptionLength: 2000,
};

/** The limits the library dialogs use: a project's own when one answered, else the fallback. */
export function libraryLimits(limits?: Partial<LibraryLimits> | null): LibraryLimits {
  const out: LibraryLimits = { ...LIBRARY_FALLBACK_LIMITS };
  for (const key of Object.keys(out) as (keyof LibraryLimits)[]) {
    const value = limits?.[key];
    if (typeof value === 'number' && Number.isFinite(value) && value > 0) out[key] = value;
  }
  return out;
}

/**
 * The library, when the server has one switched on. A server from before the library sends no `library`
 * (or `enabled: false`): none of the library UI is then shown.
 */
export function libraryOf(req: Pick<AvatarRequirements, 'library'> | null | undefined): AvatarLibraryRequirements | null {
  const library = req?.library;
  return library && library.enabled === true ? library : null;
}

/** True for an import from the workspace library: it follows the library item and is not edited in the project. */
export function isLinkedAvatar(avatar: Pick<ProjectAvatar, 'fromLibrary' | 'libraryItemId'> | null | undefined): boolean {
  return avatar?.fromLibrary === true || Boolean(avatar?.libraryItemId);
}

/** Whether an Owner or Admin may save this project avatar to the library: usable, and not itself an import. */
export function canSaveToLibrary(
  avatar: Pick<ProjectAvatar, 'selectable' | 'fromLibrary' | 'libraryItemId'>, library: Pick<AvatarLibraryRequirements, 'canManage'> | null,
): boolean {
  return Boolean(library?.canManage) && avatar.selectable && !isLinkedAvatar(avatar);
}

/** The items of a list answer, whether the server sent a bare array or an `{ items }` envelope. */
export function libraryItemsOf(response: AvatarLibraryListResponse | null | undefined): AvatarLibraryItem[] {
  if (Array.isArray(response)) return response;
  return Array.isArray(response?.items) ? response.items : [];
}

/** The library requirements a list envelope carries, when it carries them and the library is on. */
export function libraryRequirementsOf(response: AvatarLibraryListResponse | null | undefined): AvatarLibraryRequirements | null {
  if (!response || Array.isArray(response)) return null;
  return libraryOf({ library: response.library ?? undefined });
}

export function libraryItemsOfKind(items: readonly AvatarLibraryItem[], kind: AvatarSubjectKind): AvatarLibraryItem[] {
  return items.filter((i) => (i.subjectKind === 'Object' ? 'Object' : 'Person') === kind);
}

/**
 * What joins an original's name and a variant's label: `Car · Red`. The same text as `VARIANT_NAME_SEPARATOR` in
 * `variants.ts` (kept here because that module imports this one).
 */
const LIBRARY_NAME_SEPARATOR = ' · ';

type LibraryVariantFields = Pick<AvatarLibraryItem, 'id' | 'name' | 'variantOfItemId' | 'variantLabel'>;

/** True for a library variant: an item that names its original. One whose original was deleted names none. */
export function isLibraryVariant(item: Pick<AvatarLibraryItem, 'variantOfItemId'> | null | undefined): boolean {
  return typeof item?.variantOfItemId === 'string' && item.variantOfItemId.length > 0;
}

/** One entry of the library: an item and the variants shown under it (by label). */
export interface LibraryGroup<T extends LibraryVariantFields = AvatarLibraryItem> {
  item: T;
  variants: T[];
}

/**
 * The items as entries, in the order given: each original with its variants under it, by label. A variant whose
 * original is not in the list (deleted meanwhile, of a kind filtered out) is an entry of its own, like any item.
 */
export function groupLibraryItems<T extends LibraryVariantFields>(items: readonly T[] | null | undefined): LibraryGroup<T>[] {
  const list = items ?? [];
  const byId = new Map(list.map((i) => [i.id, i]));
  const isGrouped = (i: T) => {
    if (!isLibraryVariant(i)) return false;
    const original = byId.get(i.variantOfItemId!);
    return Boolean(original) && original!.id !== i.id && !isLibraryVariant(original);
  };
  const groups: LibraryGroup<T>[] = [];
  const byOriginal = new Map<string, LibraryGroup<T>>();
  for (const i of list) {
    if (isGrouped(i)) continue;
    const group: LibraryGroup<T> = { item: i, variants: [] };
    groups.push(group);
    byOriginal.set(i.id, group);
  }
  for (const i of list) {
    if (isGrouped(i)) byOriginal.get(i.variantOfItemId!)?.variants.push(i);
  }
  for (const g of groups) {
    g.variants.sort((x, y) => (x.variantLabel ?? x.name).localeCompare(y.variantLabel ?? y.name));
  }
  return groups;
}

/**
 * The name to show for a library item: `Car · Red` for a variant whose original is among `items` and that has a
 * label, else the item's own name (an original, an ungrouped variant, a server from before library variants).
 */
export function libraryDisplayName(item: LibraryVariantFields, items: readonly LibraryVariantFields[] | null | undefined): string {
  const label = (item.variantLabel ?? '').trim();
  if (!isLibraryVariant(item) || !label) return item.name;
  const original = (items ?? []).find((i) => i.id === item.variantOfItemId && i.id !== item.id);
  return original ? `${original.name}${LIBRARY_NAME_SEPARATOR}${label}` : item.name;
}

/** How many files of each kind an item holds; zeros when the server sent no counts. */
export function libraryFileCounts(item: Pick<AvatarLibraryItem, 'references'>): { images: number; videos: number; audio: number } {
  const count = (n: unknown) => (typeof n === 'number' && Number.isFinite(n) && n > 0 ? Math.floor(n) : 0);
  return { images: count(item.references?.images), videos: count(item.references?.videos), audio: count(item.references?.audio) };
}

/** The workspace statement of a kind per language: a person's consent, or an object's statement. */
export function libraryStatements(
  library: Pick<AvatarLibraryRequirements, 'consentStatements' | 'objectStatements'> | null | undefined, kind: AvatarSubjectKind,
): Record<string, string> {
  return (kind === 'Object' ? library?.objectStatements : library?.consentStatements) ?? {};
}

/** The one status badge of a library item, as a key suffix under `avatars.library.status`. */
export type LibraryItemStatus = 'ready' | 'expiresSoon' | 'withdrawn' | 'expired' | 'unavailable';

export function libraryItemStatus(item: Pick<AvatarLibraryItem, 'usable' | 'unusableReason' | 'retentionUntil' | 'subjectKind'>, now: Date = new Date(), soonDays = 30): LibraryItemStatus {
  if (item.usable) {
    // An object has no keep-until date, so it never expires.
    if (item.subjectKind === 'Object' || !item.retentionUntil) return 'ready';
    const left = new Date(item.retentionUntil).getTime() - now.getTime();
    return left <= soonDays * 86_400_000 ? 'expiresSoon' : 'ready';
  }
  switch (item.unusableReason) {
    case 'consent_revoked': return 'withdrawn';
    case 'retention_expired': return 'expired';
    default: return 'unavailable';
  }
}

export function libraryStatusColor(status: LibraryItemStatus): string {
  switch (status) {
    case 'ready': return 'green';
    case 'expiresSoon': return 'yellow';
    case 'withdrawn':
    case 'expired': return 'gray';
    default: return 'red';
  }
}

/**
 * Why an item cannot be imported into a project right now, as a key suffix under `avatars.library.picker.blocked`
 * (null = it can). The same rules as creating one: the kind must be in the plan and the project must have room.
 */
export type ImportBlock = 'imported' | 'unusable' | 'notInPlan' | 'projectFull';

export function importBlock(
  item: Pick<AvatarLibraryItem, 'imported' | 'usable' | 'subjectKind'>,
  kinds: Record<AvatarSubjectKind, AvatarKindAvailability>,
  projectFull: Record<AvatarSubjectKind, boolean>,
  /**
   * The item is a variant whose original is already in this project: it lands under that import as a variant,
   * which does not count toward the project's limit (the server applies the variant rules instead).
   */
  variantOfImported = false,
): ImportBlock | null {
  const kind: AvatarSubjectKind = item.subjectKind === 'Object' ? 'Object' : 'Person';
  if (item.imported) return 'imported';
  if (!item.usable) return 'unusable';
  if (!kinds[kind].entitled) return 'notInPlan';
  if (projectFull[kind] && !variantOfImported) return 'projectFull';
  return null;
}

/** True for a whole number of days inside the allowed keep-for range. */
export function retentionDaysOk(days: number, limits: Pick<LibraryLimits, 'minRetentionDays' | 'maxRetentionDays'>): boolean {
  return Number.isInteger(days) && days >= limits.minRetentionDays && days <= limits.maxRetentionDays;
}

/**
 * The statements a statement refusal carries (`400 consent_statement_required` / `object_statement_required`
 * answer with the texts), so the dialog can show the server's current text for a fresh tick. Null when the
 * body carries none.
 */
export function statementsFromRefusal(body: unknown): Record<string, string> | null {
  if (typeof body !== 'object' || body === null) return null;
  const statements = (body as Record<string, unknown>).statements;
  if (typeof statements !== 'object' || statements === null || Array.isArray(statements)) return null;
  const out: Record<string, string> = {};
  for (const [locale, text] of Object.entries(statements)) {
    if (typeof text === 'string' && text.trim()) out[locale] = text;
  }
  return Object.keys(out).length > 0 ? out : null;
}

/** True when the list call answered "not found": the library is off on this server (or it is an older server). */
export function isLibraryOff(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { status?: unknown }).status === 404;
}
