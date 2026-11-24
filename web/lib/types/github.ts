/**
 * Mirrors `Controllers/Dto/GitHubDtos.cs` (Inference API) — see docs/github-app.md.
 *
 * The link is one repository per project and one project per repository. Every field here is
 * read-only from the dashboard's point of view: the repository id, the installation id and the
 * default branch all come from GitHub's own answer at link time, never from the request body, which
 * is what makes the link trustworthy rather than merely well-formed.
 */

/** `GET/PUT/PATCH /api/v1/projects/{id}/github` — the linked repository, as the dashboard sees it. */
export interface ProjectGitHubLink {
  projectId: string;
  /** `owner/name` as it was when linked. Display only; the link is keyed on `repositoryId`. */
  repository: string;
  /** GitHub's numeric, immutable repository id. */
  repositoryId: number;
  installationId: number;
  /** The branch a push must target to be synced. Defaults to the repository's default branch. */
  branch: string;
  /** The repository's default branch, as GitHub reported it at link time. */
  defaultBranch?: string;
  /**
   * The old single-folder option: the folder the sync is confined to, with files stored relative to
   * it. Null for every link made with the file selection.
   */
  pathPrefix: string | null;
  /**
   * The chosen files and folders (repository-relative; a folder includes everything under it).
   * Null means every file is synced.
   */
  includePaths?: string[] | null;
  /** Commit SHA the project's GitHub-sourced files currently mirror. Null before the first sync. */
  lastPulledCommit: string | null;
  /** When the last SUCCESSFUL sync finished. */
  lastPulledAt: string | null;
  /** Files the project mirrors after the last successful sync. */
  lastPulledFileCount: number;
  /** Why the last sync attempt failed, in plain words. Null after a success, which is what clears it. */
  lastError: string | null;
  /** When the link was made. */
  linkedAt?: string | null;
  /**
   * True while a sync of this project is queued or running in the background — after "Sync now", a new
   * link or a changed selection. The dashboard re-reads the link every few seconds until it is false;
   * the outcome is then on the link (`lastPulledFileCount`, or `lastError` in plain words).
   */
  syncing?: boolean;
}

/** `PUT /api/v1/projects/{id}/github` — `repository` is `owner/name` or any github.com URL for it. */
export interface LinkGitHubRepositoryRequest {
  repository: string;
  /** Empty means the repository's default branch. */
  branch?: string | null;
  /** The old single-folder option. The dashboard no longer sends it. */
  pathPrefix?: string | null;
  /** Repository-relative files and folders to sync; empty or absent means every file. */
  includePaths?: string[] | null;
}

/**
 * `PATCH /api/v1/projects/{id}/github` — changes what the link follows and re-syncs. A field left
 * out (or null) is unchanged; `branch: ''` means the default branch and `includePaths: []` every file.
 */
export interface UpdateGitHubLinkRequest {
  branch?: string | null;
  includePaths?: string[] | null;
}

/**
 * `POST /api/v1/projects/{id}/github/pull` — answered `202 Accepted` before anything is pulled:
 * `queued` when a sync was queued, `already_syncing` when one was already queued or running. What the
 * sync did is read from the link afterwards.
 */
export interface GitHubSyncQueued {
  status: 'queued' | 'already_syncing';
}

/**
 * `GET /api/v1/github/account` — the workspace's connection to a person's GitHub account.
 *
 * `canConnect` is false when the deployment has no GitHub App client id, client secret or callback
 * URL, which is what tells the UI to explain instead of offering a button that cannot work.
 */
export interface GitHubAccount {
  connected: boolean;
  /** The GitHub login, for display. */
  login: string | null;
  avatarUrl: string | null;
  canConnect: boolean;
  /** Why the connection stopped working: `user_revoked`, `disconnected`, `expired`, `invalid_grant`. */
  revokedReason: string | null;
  connectedAt: string | null;
  /** GitHub's page for giving ReelBolt access to repositories, or null when this server has none set. */
  installUrl: string | null;
}

/**
 * One repository the connected person may choose, as GitHub's own answer describes it.
 *
 * `repositorySelection` is GitHub's `all` or `selected`; when it is `selected`, `manageUrl` is the
 * page where more repositories are added. That is the only way to reach a repository ReelBolt was
 * never given — GitHub will not list one, by design.
 */
export interface GitHubRepositoryOption {
  id: number;
  fullName: string;
  defaultBranch: string;
  isPrivate: boolean;
  installationId: number;
  accountLogin: string;
  accountType: string;
  repositorySelection: string | null;
  /** Set when THIS workspace already follows it; null when it is free. */
  linkedProjectId: string | null;
  manageUrl: string;
}

/** `GET /api/v1/projects/{id}/github/repositories` — the repository picker's list. */
export interface GitHubRepositoryList {
  repositories: GitHubRepositoryOption[];
  /** True when the person has more repositories than one answer carries. */
  truncated: boolean;
  installUrl: string | null;
  accountLogin: string | null;
}

/** One file or folder of a branch, for the file selection tree. */
export interface GitHubFileEntry {
  /** Repository-relative path. */
  path: string;
  isDirectory: boolean;
  /** Bytes; 0 for a folder. */
  size: number;
}

/**
 * `GET /api/v1/projects/{id}/github/files?repositoryId=&branch=` — every file and folder of one
 * branch, plus the limits one sync works within. `commit` is null for an empty repository;
 * `truncated` means GitHub could not list it all, which also means it cannot be synced.
 */
export interface GitHubFileList {
  repository: string;
  branch: string;
  commit: string | null;
  entries: GitHubFileEntry[];
  truncated: boolean;
  maxFiles: number;
  maxTotalBytes: number;
  maxFileBytes: number;
}

/** `GET /api/v1/projects/{id}/github/branches?repositoryId=` — default branch first. */
export interface GitHubBranchList {
  defaultBranch: string;
  branches: string[];
  truncated: boolean;
}

/**
 * The short token the OAuth callback puts in the query string. Mapped to a sentence by
 * `describeGitHubConnectOutcome`; it is never shown to the user as-is.
 */
export type GitHubConnectOutcome =
  | 'connected'
  | 'denied'
  | 'expired'
  | 'state_mismatch'
  | 'invalid_state'
  | 'exchange_failed'
  | 'identity_unavailable'
  | 'not_configured';
