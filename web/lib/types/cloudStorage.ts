/**
 * Mirrors `Controllers/Dto/CloudStorageDtos.cs` (Inference API) — see docs/design/cloud-drive-sync.md
 * "HTTP API". `provider` is always the URL slug. No field here is, or ever may be, a token.
 */

/** The three providers, by the slug the server uses in URLs and JSON. */
export type CloudProviderSlug = 'google-drive' | 'onedrive' | 'dropbox';

/** `GET /api/v1/cloud-storage/providers` — all three, and whether this server has each one set up. */
export interface CloudProvider {
  provider: CloudProviderSlug;
  displayName: string;
  configured: boolean;
}

/** `GET /api/v1/cloud-storage/connections` — one of the caller's connected accounts in this workspace. */
export interface CloudConnection {
  id: string;
  provider: CloudProviderSlug;
  accountEmail: string | null;
  accountDisplayName: string | null;
  createdAt: string;
  /** Why the account stopped working (it needs reconnecting), in plain words; null while it works. */
  lastError: string | null;
}

/** What kind of space a drive is. */
export type CloudDriveKind = 'personal' | 'sharedDrive' | 'sharePointLibrary' | 'teamSpace';

/** `GET connections/{id}/drives` — a space to pick a folder from. `driveId` null is the account's own space. */
export interface CloudDrive {
  driveId: string | null;
  name: string;
  kind: CloudDriveKind;
}

/** A folder; `folderId` null is the drive's root. */
export interface CloudFolder {
  folderId: string | null;
  name: string;
  displayPath: string;
}

export interface CloudItem {
  id: string;
  name: string;
  isFolder: boolean;
  size: number | null;
  modifiedAt: string | null;
}

/** `GET connections/{id}/folders?driveId=&folderId=` — one level, folders first, at most 500 items. */
export interface CloudFolderList {
  folder: CloudFolder;
  items: CloudItem[];
  truncated: boolean;
}

/** `GET /api/v1/projects/{projectId}/cloud-links` — a project's link to a cloud folder. */
export interface ProjectCloudLink {
  id: string;
  provider: CloudProviderSlug;
  /** Null when the account the link used was disconnected: the link needs a new account. */
  connectionId: string | null;
  accountEmail: string | null;
  driveId: string | null;
  folderId: string | null;
  folderDisplayPath: string;
  /** Folder-relative files and folders; null means every file of the folder. */
  includePaths: string[] | null;
  lastSyncedAt: string | null;
  lastSyncedFileCount: number;
  /** Why the last sync did not finish, in plain words. Null after a success. */
  lastError: string | null;
  /** True while a sync is queued or running in the background. */
  syncing: boolean;
}

/** `POST /api/v1/projects/{projectId}/cloud-links`. `driveId`/`folderId` null: the own space / its root. */
export interface CreateProjectCloudLinkRequest {
  connectionId: string;
  driveId?: string | null;
  folderId?: string | null;
  /** Null or empty: every file of the folder. */
  includePaths?: string[] | null;
}

/** `PATCH .../cloud-links/{linkId}` — `includePaths` null leaves it alone, `[]` means every file. */
export interface UpdateProjectCloudLinkRequest {
  includePaths: string[] | null;
}

/** `POST .../cloud-links/{linkId}/sync` — answered `202` before anything is copied. */
export interface CloudSyncQueued {
  status: 'queued' | 'already_syncing';
}

/** The reason codes the connect callback can put in `?cloud=error&reason=`. */
export type CloudConnectFailure =
  | 'denied'
  | 'invalid_state'
  | 'expired'
  | 'session_mismatch'
  | 'exchange_failed'
  | 'account_unavailable'
  | 'not_configured';
