import { ApiError, apiFetch } from './client';
import { formatBytes } from '../files/model';
import type {
  GitHubAccount,
  GitHubBranchList,
  GitHubConnectOutcome,
  GitHubFileList,
  GitHubSyncQueued,
  GitHubRepositoryList,
  LinkGitHubRepositoryRequest,
  ProjectGitHubLink,
  UpdateGitHubLinkRequest,
} from '../types/github';
import { readableServerMessage } from './describe-error';

/**
 * The repository link of one project, and the GitHub account it is chosen from (docs/github-app.md).
 *
 * The server is the only authority on either. It decides which repositories a person may choose by
 * asking GitHub with that person's own access token, and it refuses a link it cannot verify. The
 * dashboard therefore never validates a repository itself — it renders what the server returned and
 * maps a refusal back to a sentence. Every sentence here is written for the person using the
 * dashboard, not for whoever set the server up: no "installation", "webhook", "credential" or config
 * key ever reaches the screen.
 */

/**
 * `GET /api/v1/projects/{projectId}/github` — the link, or null when the project has none (the
 * endpoint answers 404 `not_linked`).
 *
 * A 404 here is read as "not linked" rather than as an error, and that is safe because this call is
 * only ever made from the project page, whose own project fetch has already established that the
 * project exists and is the caller's to see.
 */
export function getProjectGitHubLink(projectId: string): Promise<ProjectGitHubLink | null> {
  return apiFetch<ProjectGitHubLink>(`/api/v1/projects/${projectId}/github`).catch((error: unknown) => {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  });
}

/**
 * `PUT /api/v1/projects/{projectId}/github` — links a repository and queues its first sync. The answer
 * comes back at once with `syncing: true`; the sync runs in the background, and a sync that fails
 * never fails this call — the reason shows up later as `lastError` on the link.
 */
export function linkProjectRepository(
  projectId: string,
  request: LinkGitHubRepositoryRequest,
): Promise<ProjectGitHubLink> {
  return apiFetch<ProjectGitHubLink>(`/api/v1/projects/${projectId}/github`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/**
 * `PATCH /api/v1/projects/{projectId}/github` — changes the branch and/or the chosen files, then
 * queues a re-sync (`syncing: true` on the answer) so the project comes to match the new choice. A
 * change that changes nothing queues nothing (`syncing: false`).
 */
export function updateProjectRepository(
  projectId: string,
  request: UpdateGitHubLinkRequest,
): Promise<ProjectGitHubLink> {
  return apiFetch<ProjectGitHubLink>(`/api/v1/projects/${projectId}/github`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/**
 * `DELETE /api/v1/projects/{projectId}/github` — removes the link and NOTHING else: the files a
 * previous sync wrote stay in the project, and the server answers 204.
 */
export function unlinkProjectRepository(projectId: string): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${projectId}/github`, { method: 'DELETE' });
}

/**
 * `POST /api/v1/projects/{projectId}/github/pull` — "Sync now": queues a sync of the linked branch as
 * it is right now. The server answers `202` before anything is pulled; follow the link's `syncing`
 * flag for the outcome.
 */
export function pullProjectRepository(projectId: string): Promise<GitHubSyncQueued> {
  return apiFetch<GitHubSyncQueued>(`/api/v1/projects/${projectId}/github/pull`, { method: 'POST' });
}

/**
 * `GET /api/v1/projects/{projectId}/github/files` — every file and folder of a branch, for the file
 * selection tree. Without `repositoryId`, the repository already linked to the project.
 */
export function listGitHubFiles(
  projectId: string,
  repositoryId: number | null,
  branch: string | null,
): Promise<GitHubFileList> {
  const query = new URLSearchParams();
  if (repositoryId !== null) query.set('repositoryId', String(repositoryId));
  if (branch) query.set('branch', branch);
  const qs = query.toString();
  return apiFetch<GitHubFileList>(`/api/v1/projects/${projectId}/github/files${qs ? `?${qs}` : ''}`);
}

/** `GET /api/v1/projects/{projectId}/github/branches` — default branch first. */
export function listGitHubBranches(projectId: string, repositoryId: number | null): Promise<GitHubBranchList> {
  const qs = repositoryId !== null ? `?repositoryId=${repositoryId}` : '';
  return apiFetch<GitHubBranchList>(`/api/v1/projects/${projectId}/github/branches${qs}`);
}

// ---- the connected GitHub account ----

/** `GET /api/v1/github/account` — the workspace's connection. Carries no token, ever. */
export function getGitHubAccount(): Promise<GitHubAccount> {
  return apiFetch<GitHubAccount>('/api/v1/github/account');
}

/**
 * `POST /api/v1/github/account/connect` — starts the sign-in with GitHub and returns the URL to go to.
 *
 * It is a POST returning JSON rather than a redirect because this is called with `fetch`: a
 * cross-site redirect cannot be followed usefully from an XHR. The caller assigns the returned URL to
 * `window.location`, and the server has already set the HttpOnly cookie that binds the flow to this
 * browser.
 */
export async function startGitHubConnect(projectId?: string | null): Promise<string> {
  const body = await apiFetch<{ authorizeUrl: string }>('/api/v1/github/account/connect', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ projectId: projectId ?? null }),
  });
  return body.authorizeUrl;
}

/** `DELETE /api/v1/github/account` — forgets the connection. */
export function disconnectGitHubAccount(): Promise<void> {
  return apiFetch<void>('/api/v1/github/account', { method: 'DELETE' });
}

/** `GET /api/v1/projects/{projectId}/github/repositories` — the repositories this person may link. */
export function listGitHubRepositories(projectId: string): Promise<GitHubRepositoryList> {
  return apiFetch<GitHubRepositoryList>(`/api/v1/projects/${projectId}/github/repositories`);
}

/** The repository's page on GitHub. */
export function repositoryUrl(fullName: string): string {
  return `https://github.com/${fullName.split('/').map(encodeURIComponent).join('/')}`;
}

/** The branch's file listing on GitHub. */
export function branchUrl(fullName: string, branch: string): string {
  return `${repositoryUrl(fullName)}/tree/${branch.split('/').map(encodeURIComponent).join('/')}`;
}

/** One commit on GitHub. */
export function commitUrl(fullName: string, sha: string): string {
  return `${repositoryUrl(fullName)}/commit/${encodeURIComponent(sha)}`;
}

// ---- errors ----

/**
 * The machine-readable `error` code a failing response carries, when it carries one. Every refusal
 * from these controllers is a JSON body with an `error` code, and the code — never the prose — is
 * what the UI maps to a sentence.
 */
export function githubErrorCode(error: unknown): string | null {
  const body = bodyOf(error);
  const code = body?.error;
  return typeof code === 'string' && code !== '' ? code : null;
}

function bodyOf(error: unknown): Record<string, unknown> | null {
  if (!(error instanceof ApiError)) return null;
  const body = error.body;
  return typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : null;
}

/** Seconds to wait, from a `429` body's `retryAfterSeconds`. */
export function retryAfterSeconds(error: unknown): number | null {
  const value = bodyOf(error)?.retryAfterSeconds;
  return typeof value === 'number' && value > 0 ? Math.ceil(value) : null;
}

/**
 * The shape of every code this controller writes: lower-case words joined by underscores. It is what
 * tells a code apart from a sentence, and it deliberately requires the underscore: a single word such
 * as "boom" is left alone.
 */
const ERROR_CODE_SHAPE = /^[a-z0-9]+(_[a-z0-9]+)+$/;

/** A body `message` when it is prose, and null when it is not. */
function proseMessage(error: unknown): string | null {
  const message = bodyOf(error)?.message;
  if (typeof message === 'string' && message.trim() && !ERROR_CODE_SHAPE.test(message.trim())) {
    return message.trim();
  }
  const own = readableServerMessage(error);
  return own && !ERROR_CODE_SHAPE.test(own.trim()) ? own : null;
}

/**
 * The one sentence every "ReelBolt cannot use this repository" answer gets. The server deliberately
 * merges several causes into one answer (ReelBolt was not given access to it, or another workspace
 * already uses it), and splitting them here would invent a distinction the server refuses to make.
 */
const REPOSITORY_NOT_AVAILABLE =
  'ReelBolt cannot use that repository. Make sure ReelBolt has been given access to it on GitHub ' +
  '(use “Give ReelBolt access to more repositories”), then choose it again.';

/** The deployment cannot do GitHub at all yet. */
const NOT_SET_UP =
  'Linking GitHub repositories is not set up on this server yet. Ask your administrator to turn it on.';

/** No connected account in this workspace. */
const CONNECT_FIRST =
  'Connect your GitHub account first. ReelBolt only shows and syncs repositories your own GitHub ' +
  'account can open.';

function tooSoonSentence(error: unknown): string {
  const seconds = retryAfterSeconds(error);
  return seconds
    ? `A sync of this project ran moments ago. Wait ${seconds} second${seconds === 1 ? '' : 's'}, then try again.`
    : 'A sync of this project ran moments ago. Wait a few seconds, then try again.';
}

/** A file-limit refusal (`422`), in words, with the two numbers when the server sent them. */
function limitSentence(error: unknown): string | null {
  const code = githubErrorCode(error);
  const body = bodyOf(error);
  const actual = typeof body?.actual === 'number' ? body.actual : null;
  const limit = typeof body?.limit === 'number' ? body.limit : null;

  if (code === 'too_many_files') {
    return actual !== null && limit !== null
      ? `Too many files are chosen: ${actual.toLocaleString('en-US')}, and one project can sync at most ` +
        `${limit.toLocaleString('en-US')}. Choose fewer files or folders.`
      : proseMessage(error) ?? 'Too many files are chosen. Choose fewer files or folders.';
  }
  if (code === 'selection_too_large') {
    return actual !== null && limit !== null
      ? `The chosen files add up to ${formatBytes(actual)}, and one project can sync at most ` +
        `${formatBytes(limit)}. Choose fewer files or folders.`
      : proseMessage(error) ?? 'The chosen files are too large to sync together. Choose fewer files or folders.';
  }
  if (code === 'repository_too_large') {
    return 'This repository is too large for GitHub to list in one go, so ReelBolt cannot sync it. ' +
      'Nothing was changed.';
  }
  if (code === 'selection_unreadable') {
    return 'The saved choice of files could not be read. Choose the files again.';
  }
  return null;
}

/**
 * The sentence for a refused link or a refused change of branch/files. Each documented code gets its
 * own sentence, because each one has a different fix; anything unrecognised degrades to a sentence
 * and never prints a code at the user.
 */
export function describeGitHubLinkError(error: unknown): string {
  const code = githubErrorCode(error);

  switch (code) {
    case 'github_account_not_connected':
      return CONNECT_FIRST;
    case 'github_app_not_configured':
    case 'github_oauth_not_configured':
      return NOT_SET_UP;
    case 'invalid_repository':
      return 'That is not a GitHub repository. Choose one from the list.';
    case 'repository_not_found':
    case 'github_app_not_installed':
      return REPOSITORY_NOT_AVAILABLE;
    case 'branch_not_found':
      return 'That branch does not exist in this repository any more. Choose another branch.';
    case 'invalid_branch':
      return 'That branch name is not valid. Choose a branch from the list.';
    case 'project_already_linked':
      return 'This project is already linked to a repository. Reload the page to see it.';
    case 'repository_already_linked':
      return 'That repository is already linked to another project in this workspace. A repository ' +
        'can be linked to one project at a time.';
    case 'invalid_include_paths':
      return 'Some of the chosen files or folders could not be used. Clear the selection and choose again.';
    case 'invalid_path_prefix':
      return 'That folder is not valid. Choose the files and folders from the list.';
    case 'sync_too_soon':
    case 'sync_in_progress':
      return tooSoonSentence(error);
    case 'not_linked':
      return 'This project is not linked to a repository any more. Reload the page.';
    case 'github_error':
      return 'GitHub did not answer as expected. Wait a moment and try again.';
  }

  const limit = limitSentence(error);
  if (limit) return limit;

  if (error instanceof ApiError && error.status === 404) return REPOSITORY_NOT_AVAILABLE;
  if (error instanceof ApiError && error.status === 503) return NOT_SET_UP;

  return proseMessage(error) ?? 'The repository could not be linked. Try again.';
}

/** The sentence to show when "Sync now" is refused. */
export function describeGitHubPullError(error: unknown): string {
  const code = githubErrorCode(error);

  if (code === 'not_linked') {
    return 'This project is not linked to a repository any more. Reload the page.';
  }
  if (code === 'sync_too_soon' || code === 'sync_in_progress') return tooSoonSentence(error);
  if (code === 'github_account_not_connected') return CONNECT_FIRST;
  if (code === 'repository_not_found') return REPOSITORY_NOT_AVAILABLE;
  if (code === 'github_app_not_configured') return NOT_SET_UP;

  const limit = limitSentence(error);
  if (limit) return limit;

  if (code === 'github_error') {
    const reason = proseMessage(error);
    return reason ?? 'GitHub did not answer as expected. Wait a moment, then sync again.';
  }

  return proseMessage(error) ?? 'The sync did not run. Wait a moment, then try again.';
}

/**
 * The reason the last sync failed, as the linked view shows it. Newer servers already store a plain
 * sentence; older rows hold an operator message (REST paths, config keys), so the known ones are
 * re-worded here and anything else that still looks technical gets a plain sentence instead.
 */
export function describeLastSyncError(lastError: string | null | undefined): string | null {
  if (!lastError) return null;
  const message = lastError.trim();

  const files = /holds (\d+) files, over the configured limit of (\d+)/.exec(message);
  if (files) {
    return `Too many files are chosen: ${Number(files[1]).toLocaleString('en-US')}, and one project can ` +
      `sync at most ${Number(files[2]).toLocaleString('en-US')}. Choose fewer files or folders, then sync again.`;
  }
  if (/is \d+ bytes across \d+ files, over the configured limit/.test(message)) {
    return 'The chosen files are too large to sync together. Choose fewer files or folders, then sync again.';
  }
  if (/truncated the tree/i.test(message)) {
    return 'This repository is too large for GitHub to list in one go, so ReelBolt cannot sync it. Nothing was changed.';
  }
  if (/GitHub answered (401|403)|refused an installation token/i.test(message)) {
    return 'GitHub refused ReelBolt access to the repository. Give ReelBolt access to it again on GitHub, then sync.';
  }
  if (/GitHub answered 404|not found/i.test(message) && /\/repos\/|@/.test(message)) {
    return 'GitHub could not find the repository, the branch or a file in it. It may have been renamed or deleted.';
  }
  if (/GitHub:|\/repos\/|GitHub answered \d+/.test(message)) {
    return 'GitHub did not answer as expected. Wait a few minutes, then sync again.';
  }

  return message;
}

/** The sentence to show when unlinking is refused. */
export function describeGitHubUnlinkError(error: unknown): string {
  const code = githubErrorCode(error);

  // The link is already gone — which is what the caller wanted.
  if (code === 'not_linked' || (error instanceof ApiError && error.status === 404)) {
    return 'This project is not linked to a repository any more. It may have been unlinked already.';
  }

  return proseMessage(error) ?? 'The repository could not be unlinked. Try again.';
}

/**
 * The sentence for a refused account read. The `409` family is one condition — the stored connection
 * cannot be used — and reconnecting is the fix for every member of it.
 */
export function describeGitHubAccountError(error: unknown): string {
  const code = githubErrorCode(error);

  if (code === 'github_oauth_not_configured' || code === 'github_app_not_configured') return NOT_SET_UP;
  if (code === 'github_account_revoked' || code === 'github_account_expired' || code === 'github_account_unreadable') {
    return 'Your GitHub connection has stopped working — access may have been removed in your GitHub ' +
      'settings, or it may have expired. Connect your GitHub account again.';
  }
  if (code === 'not_connected' || code === 'github_account_not_connected' || (error instanceof ApiError && error.status === 409)) {
    return CONNECT_FIRST;
  }

  return proseMessage(error) ?? 'Your GitHub account could not be read. Try again.';
}

/** The sentence for a refused repository list. */
export function describeGitHubRepositoryListError(error: unknown): string {
  const code = githubErrorCode(error);

  if (code === 'github_error') {
    return 'GitHub could not be reached to list your repositories. Wait a moment and try again; if it ' +
      'keeps happening, disconnect and connect your GitHub account again.';
  }

  return describeGitHubAccountError(error);
}

/** The sentence for a refused file or branch listing. */
export function describeGitHubFilesError(error: unknown): string {
  const code = githubErrorCode(error);

  if (code === 'branch_not_found') {
    return 'That branch does not exist any more. It may have been renamed or deleted — choose another branch.';
  }
  if (code === 'invalid_branch') return 'That branch name is not valid. Choose a branch from the list.';
  if (code === 'repository_not_found') {
    return 'Your GitHub account cannot open this repository any more. ReelBolt’s access to it may have ' +
      'been removed on GitHub, or you may have lost access to it.';
  }
  if (code === 'not_linked') return 'This project is not linked to a repository any more. Reload the page.';
  if (code === 'github_app_not_configured' || code === 'github_oauth_not_configured') return NOT_SET_UP;
  if (code === 'github_error') return 'GitHub could not be reached to list the files. Wait a moment and try again.';

  return describeGitHubAccountError(error);
}

/**
 * The sentence for what the sign-in with GitHub reported back in the query string. The ones that
 * mean "you cancelled" or "you took too long" are deliberately not phrased as failures.
 */
export function describeGitHubConnectOutcome(outcome: string | null | undefined): string | null {
  if (!outcome) return null;

  switch (outcome as GitHubConnectOutcome) {
    case 'connected':
      return 'GitHub account connected. Now choose a repository.';
    case 'denied':
      return 'You did not approve ReelBolt on GitHub, so no account was connected.';
    case 'expired':
      return 'That sign-in took too long and expired. Start again.';
    case 'state_mismatch':
    case 'invalid_state':
      return 'That sign-in could not be verified — it may have been started in a different browser, ' +
        'or in a tab that has since been reloaded. Start again.';
    case 'identity_unavailable':
      return 'GitHub approved the request but did not say which account it was for. Try again.';
    case 'not_configured':
      return NOT_SET_UP;
    default:
      return 'Connecting your GitHub account did not finish. Try again.';
  }
}
