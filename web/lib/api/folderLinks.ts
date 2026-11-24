import { apiFetch } from './client';
import type { ProjectFolderLink } from '../types/folderLink';

/**
 * Computer folder sync (docs/design/local-folder-sync-and-storage.md Part 1). The desktop app keeps a
 * chosen folder on somebody's computer mirrored into one target folder of a project; these are the
 * routes the dashboard uses to see and manage those links. Creating one is only possible from the
 * desktop app, because the folder picker is local — there is deliberately no create here.
 */

const links = (projectId: string) => `/api/v1/projects/${encodeURIComponent(projectId)}/folder-links`;

/** `GET /api/v1/projects/{projectId}/folder-links` — `View` on the project is enough to read. */
export function listProjectFolderLinks(projectId: string): Promise<ProjectFolderLink[]> {
  return apiFetch<ProjectFolderLink[]>(links(projectId));
}

/** `POST .../folder-links/{id}/pause` — refuses the computer's uploads and deletes until resumed. */
export function pauseProjectFolderLink(projectId: string, linkId: string): Promise<ProjectFolderLink> {
  return apiFetch<ProjectFolderLink>(`${links(projectId)}/${encodeURIComponent(linkId)}/pause`, { method: 'POST' });
}

/** `POST .../folder-links/{id}/resume` — lets syncing carry on. */
export function resumeProjectFolderLink(projectId: string, linkId: string): Promise<ProjectFolderLink> {
  return apiFetch<ProjectFolderLink>(`${links(projectId)}/${encodeURIComponent(linkId)}/resume`, { method: 'POST' });
}

/** `DELETE .../folder-links/{id}` (`204`) — unlinks it. The files it brought in stay in the project. */
export function unlinkProjectFolderLink(projectId: string, linkId: string): Promise<void> {
  return apiFetch<void>(`${links(projectId)}/${encodeURIComponent(linkId)}`, { method: 'DELETE' });
}

/** How a link's computer reads on screen, in one word. */
export function deviceState(link: ProjectFolderLink): 'online' | 'offline' | 'disconnected' {
  if (link.deviceRevoked) return 'disconnected';
  return link.deviceOnline ? 'online' : 'offline';
}

/**
 * The sentence for a paused link. A `guard` pause must say that syncing stopped so nothing was
 * deleted — that is the whole point of the guard, and a bare "Paused" would read like a person's
 * choice.
 */
export function pausedExplanation(link: ProjectFolderLink): string {
  if (link.pausedReason === 'guard') {
    return `Syncing paused by ReelBolt so nothing was deleted: the folder on ${link.deviceName || 'your computer'} looked empty or mostly gone. ` +
      'If you really removed those files, resume it from the desktop app; otherwise check the folder is still there and resume.';
  }
  return 'Syncing is paused. Nothing new is copied from this computer until you resume it.';
}
