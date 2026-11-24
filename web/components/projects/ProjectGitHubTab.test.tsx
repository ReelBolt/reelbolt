// @vitest-environment jsdom

/**
 * The project's Repository tab: connect, pick, choose files, link; then the linked view — sync,
 * change files, unlink. The network functions are stubbed but the error-mapping functions are NOT,
 * so every sentence asserted here is the one a user would see.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import type { GitHubAccount, GitHubFileList, GitHubRepositoryList, ProjectGitHubLink } from '@/lib/types/github';

const h = vi.hoisted(() => ({
  mutate: vi.fn(),
  mutateAccount: vi.fn(),
  link: vi.fn(),
  update: vi.fn(),
  unlink: vi.fn(),
  pull: vi.fn(),
  connect: vi.fn(),
  disconnect: vi.fn(),
  state: { data: null as ProjectGitHubLink | null, isLoading: false, error: null as unknown },
  account: { data: undefined as GitHubAccount | undefined, error: null as unknown },
  repositories: { data: undefined as GitHubRepositoryList | undefined, isLoading: false, error: null as unknown },
  files: { data: undefined as GitHubFileList | undefined, isLoading: false, error: null as unknown },
}));

vi.mock('@/lib/hooks/use-project-github', () => ({
  useProjectGitHub: () => ({ data: h.state.data, isLoading: h.state.isLoading, error: h.state.error, mutate: h.mutate }),
}));

vi.mock('@/lib/hooks/use-github-account', () => ({
  useGitHubAccount: () => ({ data: h.account.data, error: h.account.error, mutate: h.mutateAccount }),
}));

vi.mock('@/lib/hooks/use-github-repositories', () => ({
  useGitHubRepositories: () => ({ data: h.repositories.data, isLoading: h.repositories.isLoading, error: h.repositories.error, mutate: vi.fn() }),
  useGitHubFiles: () => ({ data: h.files.data, isLoading: h.files.isLoading, error: h.files.error, mutate: vi.fn() }),
  useGitHubBranches: () => ({ data: { defaultBranch: 'main', branches: ['main', 'release'], truncated: false }, isLoading: false, error: null }),
}));

vi.mock('@/lib/api/github', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/github')>()),
  linkProjectRepository: (...a: unknown[]) => h.link(...a),
  updateProjectRepository: (...a: unknown[]) => h.update(...a),
  unlinkProjectRepository: (...a: unknown[]) => h.unlink(...a),
  pullProjectRepository: (...a: unknown[]) => h.pull(...a),
  startGitHubConnect: (...a: unknown[]) => h.connect(...a),
  disconnectGitHubAccount: (...a: unknown[]) => h.disconnect(...a),
}));

import { ProjectGitHubTab, SYNC_POLL_MS, pullState, shortCommit, syncFinishedNotice } from './ProjectGitHubTab';
import { en } from '@/components/design-system/__tests__/en';

const linked = (over: Partial<ProjectGitHubLink> = {}): ProjectGitHubLink => ({
  projectId: 'p1',
  repository: 'acme/promo-videos',
  repositoryId: 991,
  installationId: 42,
  branch: 'main',
  defaultBranch: 'main',
  pathPrefix: null,
  includePaths: null,
  lastPulledCommit: 'a1b2c3d4e5f6',
  lastPulledAt: '2026-10-09T10:00:00Z',
  lastPulledFileCount: 12,
  lastError: null,
  linkedAt: '2026-10-09T09:00:00Z',
  ...over,
});

const refusal = (status: number, code: string, extra: Record<string, unknown> = {}) =>
  new ApiError(code, status, [], { error: code, ...extra });

const accountState = (over: Partial<GitHubAccount> = {}): GitHubAccount => ({
  connected: true,
  login: 'octocat',
  avatarUrl: null,
  canConnect: true,
  revokedReason: null,
  connectedAt: '2026-10-09T09:00:00Z',
  installUrl: 'https://github.com/apps/reelbolt-test/installations/new',
  ...over,
});

const repoOption = () => ({
  id: 991,
  fullName: 'acme/promo-videos',
  defaultBranch: 'main',
  isPrivate: true,
  installationId: 42,
  accountLogin: 'acme',
  accountType: 'User',
  repositorySelection: 'selected',
  linkedProjectId: null,
  manageUrl: 'https://github.com/settings/installations/42',
});

const fileList = (over: Partial<GitHubFileList> = {}): GitHubFileList => ({
  repository: 'acme/promo-videos',
  branch: 'main',
  commit: 'c0ffee',
  truncated: false,
  maxFiles: 2000,
  maxTotalBytes: 512 * 1024 * 1024,
  maxFileBytes: 50 * 1024 * 1024,
  entries: [
    { path: 'video', isDirectory: true, size: 0 },
    { path: 'video/scene.tsx', isDirectory: false, size: 100 },
    { path: 'api', isDirectory: true, size: 0 },
    { path: 'api/main.go', isDirectory: false, size: 300 },
    { path: 'README.md', isDirectory: false, size: 50 },
  ],
  ...over,
});

beforeEach(() => {
  notifications.clean();
  h.mutate.mockReset().mockResolvedValue(undefined);
  h.link.mockReset();
  h.update.mockReset();
  h.unlink.mockReset();
  h.pull.mockReset();
  h.state.data = null;
  h.state.isLoading = false;
  h.state.error = null;
  h.account.data = accountState();
  h.account.error = null;
  h.repositories.data = { repositories: [repoOption()], truncated: false, installUrl: 'https://github.com/apps/reelbolt-test/installations/new', accountLogin: 'acme' };
  h.repositories.isLoading = false;
  h.repositories.error = null;
  h.files.data = fileList();
  h.files.isLoading = false;
  h.files.error = null;
  h.connect.mockReset();
  h.disconnect.mockReset();
  h.mutateAccount.mockReset().mockResolvedValue(undefined);
  Object.defineProperty(window, 'location', {
    configurable: true,
    value: { ...window.location, assign: vi.fn(), href: 'http://localhost/app/projects/p1?tab=repository' },
  });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

function renderTab() {
  render(
    <MantineProvider>
      <Notifications />
      <ProjectGitHubTab projectId="p1" />
    </MantineProvider>,
  );
}

/** Words that describe how the server is set up. None of them belongs in the product UI. */
const SETUP_JARGON = /GitHub App|installation|install the app|webhook|credential|private key|client secret|Before you link/i;

describe('state shaping', () => {
  it('abbreviates a commit the way GitHub does', () => {
    expect(shortCommit('a1b2c3d4e5f6')).toBe('a1b2c3d');
    expect(shortCommit(null)).toBeNull();
  });

  it('reads a failure as failed and keeps the last success visible', () => {
    const state = pullState(linked({ lastError: 'Too many files are chosen to sync: 3, and the limit is 2.' }));
    expect(state).toMatchObject({ kind: 'failed', commit: 'a1b2c3d' });
  });

  it('re-words the operator message older servers stored (the staging file-limit failure)', () => {
    const state = pullState(linked({
      lastError: 'vecchiotom/ReelBolt@463690a1 holds 3995 files, over the configured limit of 2000. Link a subdirectory (pathPrefix) or raise GitHub:MaxFilesPerPull.',
    }));
    expect(state.kind).toBe('failed');
    const reason = (state as { reason: string }).reason;
    expect(reason).toContain('3,995');
    expect(reason).toContain('2,000');
    expect(reason).not.toMatch(/pathPrefix|GitHub:|MaxFilesPerPull/);
  });

  it('reads an empty commit as never synced, and a commit with no error as synced', () => {
    expect(pullState(linked({ lastPulledCommit: null, lastPulledAt: null }))).toEqual({ kind: 'never' });
    expect(pullState(linked())).toEqual({ kind: 'ok', commit: 'a1b2c3d', at: '2026-10-09T10:00:00Z', files: 12 });
  });

  it('describes a finished background sync in plain words: the file count, or the reason it failed', () => {
    const done = syncFinishedNotice(linked(), 'pull');
    expect(done.color).toBe('green');
    expect(en(done.title)).toBe('Sync finished');
    expect(typeof done.message === 'string' ? done.message : en(done.message)).toMatch(
      /^12 files from acme\/promo-videos \(main\) are in this project\./,
    );
    const failed = syncFinishedNotice(linked({ lastError: 'GitHub refused ReelBolt access to acme/promo-videos.' }), 'link');
    expect(failed.color).toBe('red');
    expect(en(failed.title)).toBe('acme/promo-videos is linked, but the first sync did not finish');
    expect(failed.message).toBe('GitHub refused ReelBolt access to acme/promo-videos.');
  });
});

describe('not linked: connecting', () => {
  it('offers to connect, in plain words, and none of the setup instructions', () => {
    h.account.data = accountState({ connected: false, login: null });
    renderTab();

    expect(screen.getByRole('button', { name: /Connect GitHub account/ })).toBeTruthy();
    expect(screen.queryByText(/Choose a repository/)).toBeNull();
    expect(document.body.textContent).not.toMatch(SETUP_JARGON);
  });

  it('starts the sign-in with GitHub and navigates there', async () => {
    h.account.data = accountState({ connected: false, login: null });
    h.connect.mockResolvedValue('https://github.com/login/oauth/authorize?x=1');
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Connect GitHub account/ }));

    await waitFor(() => expect(h.connect).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(window.location.assign).toHaveBeenCalledWith('https://github.com/login/oauth/authorize?x=1'));
  });

  it('says plainly when this server cannot link repositories, and offers no button that cannot work', () => {
    h.account.data = accountState({ connected: false, login: null, canConnect: false });
    renderTab();

    expect(screen.getByText(/not set up on this server yet/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Connect GitHub account/ })).toBeNull();
    expect(document.body.textContent).not.toMatch(SETUP_JARGON);
  });
});

describe('not linked: choosing and linking', () => {
  const pick = () => fireEvent.click(screen.getByRole('button', { name: /acme\/promo-videos/ }));

  it('walks repository → branch → files, and offers the GitHub access page', async () => {
    renderTab();

    const access = screen.getByRole('link', { name: /Give ReelBolt access to more repositories on GitHub/ });
    expect(access.getAttribute('href')).toBe('https://github.com/apps/reelbolt-test/installations/new');

    pick();
    expect(await screen.findByRole('checkbox', { name: 'Folder video' })).toBeTruthy();
    expect(screen.getByDisplayValue('main (default)')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(SETUP_JARGON);
  });

  it('sends exactly the files the person ticked — the selection round-trips into the link request', async () => {
    h.link.mockResolvedValue(linked({ includePaths: ['README.md', 'video'], syncing: true }));
    renderTab();
    pick();

    // Everything starts ticked; untick the api folder.
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Folder api' }));
    const linkButton = await screen.findByRole('button', { name: /Link and sync 2 files/ });
    fireEvent.click(linkButton);

    await waitFor(() => expect(h.link).toHaveBeenCalledWith('p1', {
      repository: 'acme/promo-videos',
      branch: null,
      includePaths: ['video', 'README.md'],
    }));
  });

  it('sends an empty list — "every file" — when everything stays ticked', async () => {
    h.link.mockResolvedValue(linked());
    renderTab();
    pick();

    fireEvent.click(await screen.findByRole('button', { name: /Link and sync 3 files/ }));

    await waitFor(() => expect(h.link).toHaveBeenCalledWith('p1', expect.objectContaining({ includePaths: [] })));
  });

  it('will not link a selection over the limit, and says why with the numbers', async () => {
    h.files.data = fileList({ maxFiles: 2 });
    renderTab();
    pick();

    // Over the limit, a new link starts with nothing ticked.
    const button = await screen.findByRole('button', { name: /Link repository/ }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));

    expect((await screen.findAllByText(/one project can sync at most 2/)).length).toBeGreaterThan(0);
    expect((screen.getByRole('button', { name: /Link repository/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('turns "connect your account first" into a Connect button', async () => {
    h.link.mockRejectedValue(refusal(409, 'github_account_not_connected'));
    renderTab();
    pick();

    fireEvent.click(await screen.findByRole('button', { name: /Link and sync/ }));

    expect(await screen.findByText(/Connect your GitHub account first/)).toBeTruthy();
  });

  it('explains a repository ReelBolt cannot use, without setup vocabulary', async () => {
    h.link.mockRejectedValue(refusal(404, 'github_app_not_installed'));
    renderTab();
    pick();

    fireEvent.click(await screen.findByRole('button', { name: /Link and sync/ }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toMatch(/ReelBolt cannot use that repository/);
    expect(alert.textContent).not.toMatch(SETUP_JARGON);
  });

  it('a new link is announced at once and its first sync is followed until the files are in', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.link.mockResolvedValue(linked({ syncing: true, lastPulledCommit: null, lastPulledAt: null, lastPulledFileCount: 0 }));
    h.mutate.mockImplementation(async (data?: ProjectGitHubLink) => {
      // The first call puts the PUT answer in the cache; every re-read after it finds the sync done.
      h.state.data = data ?? linked({ lastPulledFileCount: 3 });
      return h.state.data;
    });
    renderTab();
    pick();

    fireEvent.click(await screen.findByRole('button', { name: /Link and sync 3 files/ }));

    expect(await screen.findByText(/Copying 3 files now/)).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(SYNC_POLL_MS); });
    expect(await screen.findByText('acme/promo-videos is linked and synced')).toBeTruthy();
    expect(screen.getByText(/3 files from acme\/promo-videos/)).toBeTruthy();
  });

  it('a link whose first sync failed is announced with the reason, not as a failed link', async () => {
    h.link.mockResolvedValue(linked({ lastError: 'Too many files are chosen to sync: 3995, and the limit is 2000. Choose fewer files or folders. Nothing was changed.' }));
    renderTab();
    pick();

    fireEvent.click(await screen.findByRole('button', { name: /Link and sync/ }));

    expect(await screen.findByText(/is linked, but the first sync did not finish/)).toBeTruthy();
    expect(screen.getByText(/Too many files are chosen to sync: 3995/)).toBeTruthy();
  });
});

describe('linked', () => {
  it('shows the repository, branch and commit as links to GitHub, the files included and the count', () => {
    h.state.data = linked({ includePaths: ['video', 'README.md'] });
    renderTab();

    expect(screen.getByRole('link', { name: 'acme/promo-videos' }).getAttribute('href')).toBe('https://github.com/acme/promo-videos');
    expect(screen.getByRole('link', { name: 'main' }).getAttribute('href')).toBe('https://github.com/acme/promo-videos/tree/main');
    expect(screen.getByRole('link', { name: 'a1b2c3d' }).getAttribute('href')).toBe('https://github.com/acme/promo-videos/commit/a1b2c3d4e5f6');
    expect(screen.getByText('video, README.md')).toBeTruthy();
    expect(screen.getByText('12 files')).toBeTruthy();
    expect(screen.getByText('Synced')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(SETUP_JARGON);
  });

  it('shows the old single-folder option as what is included', () => {
    h.state.data = linked({ pathPrefix: 'video' });
    renderTab();

    expect(screen.getByText('Everything in video/')).toBeTruthy();
  });

  it('shows the stored failure in plain words, and offers to choose fewer files for a limit failure', () => {
    h.state.data = linked({
      lastError: 'vecchiotom/ReelBolt@463690a1 holds 3995 files, over the configured limit of 2000. Link a subdirectory (pathPrefix) or raise GitHub:MaxFilesPerPull.',
    });
    renderTab();

    expect(screen.getByText('The last sync did not finish')).toBeTruthy();
    expect(screen.getByText(/Too many files are chosen: 3,995/)).toBeTruthy();
    expect(screen.getByText(/still has the files from the last successful sync \(commit a1b2c3d\)/)).toBeTruthy();
    expect(screen.getByRole('button', { name: /Choose fewer files/ })).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/Try again\.$/);
  });

  /**
   * Makes the link re-read answer each of `answers` in turn (the last one repeats), and makes the hook
   * return it on the next render — what SWR's mutate does.
   */
  const linkReads = (...answers: ProjectGitHubLink[]) => {
    let i = 0;
    h.mutate.mockImplementation(async () => {
      const next = answers[Math.min(i, answers.length - 1)];
      i += 1;
      h.state.data = next;
      return next;
    });
  };

  /** Lets one polling interval pass. */
  const nextPoll = () => act(async () => { await vi.advanceTimersByTimeAsync(SYNC_POLL_MS); });

  it('Sync now is answered at once, shows Syncing…, re-reads the link every 3 s and announces the file count when it is done', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.state.data = linked({ lastPulledFileCount: 12 });
    h.pull.mockResolvedValue({ status: 'queued' });
    linkReads(linked({ syncing: true }), linked({ syncing: false, lastPulledFileCount: 468 }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Sync now/ }));

    await waitFor(() => expect(h.pull).toHaveBeenCalledWith('p1'));
    expect(await screen.findByText('Syncing files from GitHub…')).toBeTruthy();
    expect((screen.getByRole('button', { name: /Syncing…/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(h.mutate).not.toHaveBeenCalled();

    await nextPoll();
    expect(h.mutate).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Sync finished')).toBeNull();

    await nextPoll();
    expect(h.mutate).toHaveBeenCalledTimes(2);
    expect(await screen.findByText('Sync finished')).toBeTruthy();
    expect(screen.getByText(/468 files from acme\/promo-videos \(main\) are in this project/)).toBeTruthy();
    expect(screen.queryByText('Syncing files from GitHub…')).toBeNull();

    // Over: no more re-reads.
    await nextPoll();
    expect(h.mutate).toHaveBeenCalledTimes(2);
  });

  it('a background sync that fails shows the reason in plain words', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.state.data = linked();
    h.pull.mockResolvedValue({ status: 'already_syncing' });
    linkReads(linked({ lastError: 'Too many files are chosen to sync: 3995, and the limit is 2000. Choose fewer files or folders. Nothing was changed.' }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Sync now/ }));
    await waitFor(() => expect(h.pull).toHaveBeenCalled());
    await nextPoll();

    expect(await screen.findByText('The sync did not finish')).toBeTruthy();
    expect(screen.getAllByText(/Too many files are chosen to sync: 3995/).length).toBeGreaterThan(0);
  });

  it('follows a sync that was already running when the page opened', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.state.data = linked({ syncing: true });
    linkReads(linked({ syncing: false, lastPulledFileCount: 3 }));
    renderTab();

    expect(screen.getAllByText('Syncing…').length).toBeGreaterThan(0);
    expect(screen.getByText('Syncing files from GitHub…')).toBeTruthy();
    await nextPoll();

    expect(await screen.findByText('Sync finished')).toBeTruthy();
    expect(screen.getByText(/3 files from acme\/promo-videos/)).toBeTruthy();
    await nextPoll();
    expect(h.mutate).toHaveBeenCalledTimes(1);
  });

  it('a sync asked for too soon says how long to wait', async () => {
    h.state.data = linked();
    h.pull.mockRejectedValue(refusal(429, 'sync_too_soon', { retryAfterSeconds: 12 }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Sync now/ }));

    expect(await screen.findByText(/Wait 12 seconds/)).toBeTruthy();
  });

  it('Change files opens the selector on the saved choice and saves the new one', async () => {
    h.state.data = linked({ includePaths: ['video', 'README.md'] });
    h.update.mockResolvedValue(linked({ includePaths: ['video'], syncing: true }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Change files/ }));
    const readme = await screen.findByRole('checkbox', { name: 'README.md' }) as HTMLInputElement;
    expect(readme.checked).toBe(true);
    expect((screen.getByRole('checkbox', { name: 'Folder api' }) as HTMLInputElement).checked).toBe(false);

    fireEvent.click(readme);
    fireEvent.click(screen.getByRole('button', { name: /Save and sync/ }));

    await waitFor(() => expect(h.update).toHaveBeenCalledWith('p1', { branch: null, includePaths: ['video'] }));
    expect(await screen.findByText(/Syncing the new choice of files now/)).toBeTruthy();
  });

  it('asks for a connected account before files can be changed', () => {
    h.state.data = linked();
    h.account.data = accountState({ connected: false, login: null });
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Change files/ }));

    expect(screen.getByText(/Connect your GitHub account to change which files/)).toBeTruthy();
    expect(screen.getByRole('button', { name: /Connect GitHub account/ })).toBeTruthy();
  });

  it('unlink confirms first, says the files stay, then unlinks', async () => {
    h.state.data = linked();
    h.unlink.mockResolvedValue(undefined);
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Unlink repository/ }));

    expect(await screen.findByText('Unlink this repository?')).toBeTruthy();
    expect(screen.getByText(/The files it already brought in stay in the project/)).toBeTruthy();
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Unlink' }));

    await waitFor(() => expect(h.unlink).toHaveBeenCalledWith('p1'));
  });
});

describe('a link that could not be read', () => {
  it('says so and offers a retry rather than pretending the project has no repository', async () => {
    h.state.error = new ApiError('boom', 500);
    renderTab();

    expect(screen.getByText('The repository link could not be loaded')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
  });
});
