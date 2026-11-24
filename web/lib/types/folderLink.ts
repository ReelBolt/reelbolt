/**
 * Computer folder sync, as the dashboard sees it
 * (docs/design/local-folder-sync-and-storage.md Part 1, "Dashboard API").
 *
 * Mirrors `ReelBolt.Inference.Api.Controllers.ProjectFolderLinksController`'s
 * `ProjectFolderLinkResponse` field for field. A link is created only from the desktop app, because the
 * folder picker is local; the dashboard lists, pauses, resumes and unlinks.
 */

/**
 * Why a link is paused. `guard` is the mass-delete guard: the folder looked empty or mostly gone, so
 * the server stopped before deleting anything and says so — the files are still in the project.
 * `user` is somebody pressing Pause.
 */
export type FolderLinkPauseReason = 'guard' | 'user';

/** `GET /api/v1/projects/{projectId}/folder-links` — one entry per linked folder on one computer. */
export interface ProjectFolderLink {
  id: string;
  deviceId: string;
  deviceName: string;
  /** The computer was seen within the server's two-minute window. */
  deviceOnline: boolean;
  /** The computer was revoked (re-paired elsewhere): it will never sync this link again. */
  deviceRevoked: boolean;
  /** Where the folder lands in the project (project-relative; unique per project). */
  targetFolder: string;
  /** What the computer calls the folder — display only, never a path the server acts on. */
  displayPath: string;
  paused: boolean;
  /** Non-null only while paused. */
  pausedReason: FolderLinkPauseReason | null;
  fileCount: number;
  lastChangeAt: string | null;
  lastReconciledAt: string | null;
  /** Why the last sync did not finish, in plain words. Null after a success. */
  lastError: string | null;
}
