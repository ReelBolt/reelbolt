/**
 * The workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library (global avatars)"),
 * routes under `/api/v1/avatar-library` (`AvatarLibraryController`), camelCase JSON.
 * A library item belongs to ONE workspace and is a person or an object. Importing it into a project makes a
 * linked project avatar (`ProjectAvatar.fromLibrary`, `libraryItemId`) that follows the item.
 */
import type { AvatarLibraryRequirements, AvatarSubjectKind } from './avatars';

export type { AvatarLibraryRequirements } from './avatars';

/** Why a library item cannot be used, in the order the server checks them. An object never has one. */
export const LIBRARY_UNUSABLE_REASONS = ['consent_revoked', 'retention_expired'] as const;
export type LibraryUnusableReason = (typeof LIBRARY_UNUSABLE_REASONS)[number];

export interface AvatarLibraryItem {
  id: string;
  subjectKind: AvatarSubjectKind;
  name: string;
  description: string | null;
  usable: boolean;
  unusableReason: LibraryUnusableReason | string | null;
  /** How many files of each kind the item holds. */
  references: { images: number; videos: number; audio: number };
  /** The picture to show for it (`GET /{id}/references/{referenceId}/content`), or null. */
  primaryReferenceId: string | null;
  /** Null for an object: it has no keep-until date. */
  retentionUntil: string | null;
  /** How many projects hold an import of it. */
  importCount: number;
  /** Whether the project named in the list call (`?projectId=`) already holds an import; false without one. */
  imported: boolean;
  /** The caller may rename, confirm again, withdraw consent and delete it. */
  canManage: boolean;
  /**
   * The library item this one is a variant of (docs/design/avatar-objects.md "Avatar variants"): the same car in
   * another colour. Null for an original, and once the original is deleted (the variant stays, ungrouped). A server
   * from before library variants sends none.
   */
  variantOfItemId?: string | null;
  /** A variant's short label ("Red"), kept when it is ungrouped; null for an original. */
  variantLabel?: string | null;
  /** What sets a variant apart from its original; null for an original. */
  variantDescription?: string | null;
  /** How many items of the library are variants of this one (0 for a variant: one level only). */
  variantCount?: number;
}

/**
 * What `GET /api/v1/avatar-library` answers. The contract names the items; a bare array and an `{ items }`
 * envelope are both read (`libraryItemsOf`). An envelope may also carry the library requirements (`library`),
 * which the workspace page reads when it has no project to ask (`libraryRequirementsOf`).
 */
export type AvatarLibraryListResponse =
  | AvatarLibraryItem[]
  | { items: AvatarLibraryItem[]; library?: AvatarLibraryRequirements | null };

/**
 * Publishes a usable project avatar that is not itself an import. `consentStatement` carries the WORKSPACE
 * statement of the avatar's kind verbatim (`library.consentStatements[locale]` for a person,
 * `library.objectStatements[locale]` for an object). A person also sends the adult tick and the keep-for days.
 */
export interface PublishToLibraryRequest {
  projectId: string;
  avatarId: string;
  consentStatement: string;
  consentLocale: string;
  subjectIsAdult?: boolean;
  retentionDays?: number;
}

export interface UpdateLibraryItemRequest {
  name?: string;
  description?: string;
}

/** The statement again; for a person also the adult tick and the keep-for days. The files never change. */
export interface ReconfirmLibraryItemRequest {
  consentStatement: string;
  consentLocale: string;
  subjectIsAdult?: boolean;
  retentionDays?: number;
}

/** What deleting an item removed: its imports in the projects, then its stored files. */
export interface DeleteLibraryItemResponse {
  imports: { deleted: number; failed: number };
  objects: { deleted: number; failed: number };
}

export interface ImportLibraryAvatarRequest {
  libraryItemId: string;
}
