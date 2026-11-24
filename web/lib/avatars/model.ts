/**
 * Pure helpers of the Avatars tab (docs/design/avatars.md "Web"). No user-visible text lives here:
 * every function answers a value or a key suffix the component looks up in `avatars.json`.
 */
import type { ProjectFile } from '../types/project';
import {
  OBJECT_ROLES_BY_KIND,
  ROLES_BY_KIND,
  type AvatarKindAvailability,
  type AvatarLimits,
  type AvatarReferenceKind,
  type AvatarReferenceRole,
  type AvatarRequirements,
  type AvatarSubjectKind,
  type ProjectAvatar,
} from '../types/avatars';
import { displayName } from './variants';

/** The `project_files.source_kind` a file gets once it is a person's reference; any other kind is refused for a person. */
export const AVATAR_REFERENCE_SOURCE_KIND = 'avatar_reference';

/**
 * The `source_kind`s an OBJECT's file may carry besides none (`AvatarReferenceChecks.ObjectSourceKinds`): a file a
 * person's avatar already uses, a saved copy of an output, and a file unpacked from the member's own zip. Every
 * other kind (a repository, a cloud drive, a computer folder, anything added later) is refused by the server
 * (`reference_file_source_not_allowed`), so the picker does not offer it.
 */
export const OBJECT_SOURCE_KINDS: readonly string[] = [AVATAR_REFERENCE_SOURCE_KIND, 'output_copy', 'zip', 'avatar_variant', 'avatar_extraction'];

/** What the avatar shows. A server from before objects sends no kind, and every avatar is then a person. */
export function subjectKindOf(avatar: Pick<ProjectAvatar, 'subjectKind'> | null | undefined): AvatarSubjectKind {
  return avatar?.subjectKind === 'Object' ? 'Object' : 'Person';
}

export function isObjectAvatar(avatar: Pick<ProjectAvatar, 'subjectKind'> | null | undefined): boolean {
  return subjectKindOf(avatar) === 'Object';
}

/**
 * Which kinds are switched on and in the plan. A server from before objects sends no `kinds`: people are then on
 * (the route answered) with the old `entitled`, and objects are off.
 */
export function kindAvailability(req: Pick<AvatarRequirements, 'kinds' | 'entitled'> | null | undefined): Record<AvatarSubjectKind, AvatarKindAvailability> {
  const read = (k: AvatarKindAvailability | undefined, fallback: AvatarKindAvailability): AvatarKindAvailability =>
    (k ? { enabled: Boolean(k.enabled), entitled: Boolean(k.entitled) } : fallback);
  if (!req?.kinds) {
    return { Person: { enabled: true, entitled: Boolean(req?.entitled) }, Object: { enabled: false, entitled: false } };
  }
  return {
    Person: read(req.kinds.person, { enabled: true, entitled: Boolean(req.entitled) }),
    Object: read(req.kinds.object, { enabled: false, entitled: false }),
  };
}

/** The kinds that are switched on, people first. */
export function enabledKinds(req: Pick<AvatarRequirements, 'kinds' | 'entitled'> | null | undefined): AvatarSubjectKind[] {
  const kinds = kindAvailability(req);
  return (['Person', 'Object'] as const).filter((k) => kinds[k].enabled);
}

/** The avatars of one kind, in the order given. */
export function avatarsOfKind(avatars: readonly ProjectAvatar[] | null | undefined, kind: AvatarSubjectKind): ProjectAvatar[] {
  return (avatars ?? []).filter((a) => subjectKindOf(a) === kind);
}

/**
 * The roles a file of one kind may take: a person's by `ROLES_BY_KIND`; an object's by `OBJECT_ROLES_BY_KIND`,
 * narrowed to the roles the server lists (`requirements.objectRoles`) when it lists any.
 */
export function rolesFor(
  subject: AvatarSubjectKind, kind: AvatarReferenceKind, req?: Pick<AvatarRequirements, 'objectRoles'> | null,
): readonly AvatarReferenceRole[] {
  if (subject === 'Person') return ROLES_BY_KIND[kind];
  const all = OBJECT_ROLES_BY_KIND[kind];
  const listed = req?.objectRoles;
  if (!listed || listed.length === 0) return all;
  const narrowed = all.filter((r) => listed.includes(r));
  return narrowed.length > 0 ? narrowed : all;
}

/**
 * The limits of an object's files: the shared size limits with the object's own counts (pictures and videos; no
 * recordings), so the same checks serve both kinds.
 */
export function objectFileLimits(req: Pick<AvatarRequirements, 'limits' | 'objectLimits'>): AvatarLimits {
  return {
    ...req.limits,
    maxImages: req.objectLimits?.maxImages ?? req.limits.maxImages,
    maxVideos: req.objectLimits?.maxVideos ?? req.limits.maxVideos,
    maxAudio: 0,
  };
}

/** The reference kind of a MIME type, as the server reads it (`AvatarReferenceChecks.KindOf`), or null when not accepted. */
export function kindOfMime(mimeType: string | null | undefined, req: Pick<AvatarRequirements, 'mimeTypes'>): AvatarReferenceKind | null {
  const mime = (mimeType ?? '').split(';')[0].trim().toLowerCase();
  if (req.mimeTypes.image.includes(mime)) return 'Image';
  if (req.mimeTypes.video.includes(mime)) return 'Video';
  if (req.mimeTypes.audio.includes(mime)) return 'Audio';
  return null;
}

/** The role a newly ticked file of a person starts with. */
export function defaultRole(kind: AvatarReferenceKind): AvatarReferenceRole {
  return ROLES_BY_KIND[kind][0];
}

/**
 * The role a newly ticked file of an object starts with: the first picture is the front view, a later one
 * another angle (the person can change either); a video shows it moving.
 */
export function defaultObjectRole(
  kind: AvatarReferenceKind, picked: readonly Pick<PickedReference, 'role'>[], req?: Pick<AvatarRequirements, 'objectRoles'> | null,
): AvatarReferenceRole {
  const roles = rolesFor('Object', kind, req);
  if (kind === 'Image' && picked.some((p) => p.role === 'Front') && roles.includes('Angle')) return 'Angle';
  return roles[0] ?? 'Front';
}

export function maxBytesFor(kind: AvatarReferenceKind, limits: AvatarLimits): number {
  return kind === 'Image' ? limits.maxImageBytes : kind === 'Video' ? limits.maxVideoBytes : limits.maxAudioBytes;
}

/**
 * The project files the picker offers.
 * A person: an accepted picture, video or recording that no other writer owns (repository, cloud drive, computer
 * folder, saved output copy), since the server refuses those (`reference_file_source_not_allowed`).
 * An object: an accepted picture or video (never a recording), a platform-made file included (an extracted frame,
 * a picture export, a saved output copy, `OBJECT_SOURCE_KINDS`); a file a connected source keeps in step is left out.
 */
export function eligibleReferenceFiles(
  files: readonly ProjectFile[], req: Pick<AvatarRequirements, 'mimeTypes'>, subject: AvatarSubjectKind = 'Person',
): ProjectFile[] {
  const fits = subject === 'Object'
    ? (f: ProjectFile) => {
      const kind = kindOfMime(f.mimeType, req);
      return (kind === 'Image' || kind === 'Video') && (!f.sourceKind || OBJECT_SOURCE_KINDS.includes(f.sourceKind));
    }
    : (f: ProjectFile) => kindOfMime(f.mimeType, req) !== null && (!f.sourceKind || f.sourceKind === AVATAR_REFERENCE_SOURCE_KIND);
  return files.filter(fits).sort((a, b) => a.originalFileName.localeCompare(b.originalFileName));
}

export interface PickedReference {
  fileId: string;
  kind: AvatarReferenceKind;
  role: AvatarReferenceRole;
  sizeBytes: number;
}

/** What is wrong with the picked files, as catalogue key suffixes under `avatars.form.problems` (empty = fine). */
export type SelectionProblem = 'noImage' | 'tooManyImages' | 'tooManyVideos' | 'tooManyAudio' | 'tooLarge';

export function selectionCounts(picked: readonly PickedReference[]): Record<AvatarReferenceKind, number> {
  const counts: Record<AvatarReferenceKind, number> = { Image: 0, Video: 0, Audio: 0 };
  for (const p of picked) counts[p.kind] += 1;
  return counts;
}

export function selectionProblems(picked: readonly PickedReference[], limits: AvatarLimits): SelectionProblem[] {
  const counts = selectionCounts(picked);
  const out: SelectionProblem[] = [];
  if (counts.Image === 0) out.push('noImage');
  if (counts.Image > limits.maxImages) out.push('tooManyImages');
  if (counts.Video > limits.maxVideos) out.push('tooManyVideos');
  if (counts.Audio > limits.maxAudio) out.push('tooManyAudio');
  if (picked.some((p) => p.sizeBytes > maxBytesFor(p.kind, limits))) out.push('tooLarge');
  return out;
}

/**
 * The one status badge of a card, as a key suffix under `avatars.status`. The order follows the
 * server's rule; `expiresSoon` is a ready avatar inside its last `soonDays` days.
 */
export type AvatarStatus =
  | 'ready'
  | 'expiresSoon'
  | 'withdrawn'
  | 'expired'
  | 'referenceMissing'
  | 'referenceHeld'
  | 'noPicture'
  | 'consentMissing'
  | 'unavailable';

export function avatarStatus(avatar: ProjectAvatar, now: Date = new Date(), soonDays = 30): AvatarStatus {
  if (avatar.selectable) {
    // An object has no keep-until date (null on the wire), so it never expires.
    if (isObjectAvatar(avatar) || !avatar.retentionUntil) return 'ready';
    const left = new Date(avatar.retentionUntil).getTime() - now.getTime();
    return left <= soonDays * 86_400_000 ? 'expiresSoon' : 'ready';
  }
  switch (avatar.unselectableReason) {
    case 'consent_revoked': return 'withdrawn';
    case 'retention_expired': return 'expired';
    case 'reference_missing': return 'referenceMissing';
    case 'reference_held': return 'referenceHeld';
    case 'no_image_reference': return 'noPicture';
    case 'consent_missing': return 'consentMissing';
    default: return 'unavailable';
  }
}

export function statusColor(status: AvatarStatus): string {
  switch (status) {
    case 'ready': return 'green';
    case 'expiresSoon': return 'yellow';
    case 'withdrawn':
    case 'expired': return 'gray';
    default: return 'red';
  }
}

/** The file id of the avatar's primary picture (the server's choice, else its first picture), or null. */
export function primaryPictureFileId(avatar: ProjectAvatar): string | null {
  const primary = avatar.references.find((r) => r.id === avatar.primaryReferenceId && r.kind === 'Image' && r.state === 'ok');
  const first = primary ?? avatar.references.find((r) => r.kind === 'Image' && r.state === 'ok');
  return first?.projectFileId ?? null;
}

/** One avatar that uses a file, with what it shows. */
export interface AvatarFileUse {
  name: string;
  kind: AvatarSubjectKind;
}

/**
 * Which avatars use each file, with their kind: the Files tab badges such a file "Avatar: <name>" for a
 * person and "Object: <name>" for an object. A variant is named as everywhere else (`Car · Red`).
 */
export function avatarUsesByFile(avatars: readonly ProjectAvatar[] | null | undefined): Map<string, AvatarFileUse[]> {
  const out = new Map<string, AvatarFileUse[]>();
  for (const a of avatars ?? []) {
    const ids = new Set(a.references.map((r) => r.projectFileId));
    if (a.consent?.documentFileId) ids.add(a.consent.documentFileId);
    for (const id of ids) {
      const uses = out.get(id) ?? [];
      uses.push({ name: displayName(a, avatars), kind: subjectKindOf(a) });
      out.set(id, uses);
    }
  }
  return out;
}

/** Which avatars use each file, by name only (people and objects alike). */
export function avatarNamesByFile(avatars: readonly ProjectAvatar[] | null | undefined): Map<string, string[]> {
  return new Map([...avatarUsesByFile(avatars)].map(([id, uses]) => [id, uses.map((u) => u.name)]));
}

/**
 * The statement language a form opens on: the active UI language when the server has the statement in it, else
 * English. `statements` is the consent statement for a person and the object statement for an object.
 */
export function defaultStatementLocale(uiLocale: string, locales: readonly string[], statements: Record<string, string>): string {
  const base = uiLocale.split('-')[0].toLowerCase();
  if (locales.includes(base) && statements[base]) return base;
  if (statements.en) return 'en';
  return locales.find((l) => statements[l]) ?? locales[0] ?? 'en';
}

/** The language the person consent statement opens in. */
export function defaultConsentLocale(uiLocale: string, req: Pick<AvatarRequirements, 'locales' | 'statements'>): string {
  return defaultStatementLocale(uiLocale, req.locales, req.statements);
}

/** The object statement per language; empty from a server from before objects. */
export function objectStatements(req: Pick<AvatarRequirements, 'objectStatements'>): Record<string, string> {
  return req.objectStatements ?? {};
}

/** The languages a statement is served in, in the server's order. */
export function statementLocales(locales: readonly string[], statements: Record<string, string>): string[] {
  const listed = locales.filter((l) => statements[l]);
  return listed.length > 0 ? listed : Object.keys(statements).filter((l) => statements[l]);
}

/**
 * True only when the server says a PERSON's consent does not cover pictures yet (it was confirmed under the older
 * statement). An object, or a server that says nothing, is never flagged.
 */
export function needsPictureConsent(avatar: Pick<ProjectAvatar, 'subjectKind' | 'coversPictures'>): boolean {
  return !isObjectAvatar(avatar) && avatar.coversPictures === false;
}

/** The `fileId` a refusal body names, when it names one. */
export function refusedFileId(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const id = (body as Record<string, unknown>).fileId;
  return typeof id === 'string' && id ? id : null;
}
