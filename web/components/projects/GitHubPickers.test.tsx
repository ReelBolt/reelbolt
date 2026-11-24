// @vitest-environment jsdom

/**
 * The repository picker and the file selector (docs/github-app.md). The pure helpers are tested
 * directly because they decide what the reader sees; the components are tested for what reaches the
 * parent — the selection the person made is exactly what gets linked.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { GitHubFileList, GitHubRepositoryList } from '@/lib/types/github';

const h = vi.hoisted(() => ({
  repositories: { data: undefined as GitHubRepositoryList | undefined, isLoading: false, error: null as unknown },
  files: { data: undefined as GitHubFileList | undefined, isLoading: false, error: null as unknown },
  mutate: vi.fn(),
}));

vi.mock('@/lib/hooks/use-github-repositories', () => ({
  useGitHubRepositories: () => ({ data: h.repositories.data, isLoading: h.repositories.isLoading, error: h.repositories.error, mutate: h.mutate }),
  useGitHubFiles: () => ({ data: h.files.data, isLoading: h.files.isLoading, error: h.files.error, mutate: h.mutate }),
  useGitHubBranches: () => ({ data: undefined, isLoading: false, error: null, mutate: h.mutate }),
}));

import {
  GitHubRepositoryPicker, describeRepository, filterRepositories, groupRepositories,
} from './GitHubRepositoryPicker';
import { GitHubFileSelector, type FileSelection } from './GitHubFileSelector';
import type { GitHubRepositoryOption } from '@/lib/types/github';
import { en } from '@/components/design-system/__tests__/en';

const repo = (over: Partial<GitHubRepositoryOption> = {}): GitHubRepositoryOption => ({
  id: 1,
  fullName: 'acme/promo-videos',
  defaultBranch: 'main',
  isPrivate: true,
  installationId: 42,
  accountLogin: 'acme',
  accountType: 'User',
  repositorySelection: 'selected',
  linkedProjectId: null,
  manageUrl: 'https://github.com/settings/installations/42',
  ...over,
});

const files = (over: Partial<GitHubFileList> = {}): GitHubFileList => ({
  repository: 'acme/promo-videos',
  branch: 'main',
  commit: 'c0ffee1234',
  truncated: false,
  maxFiles: 2000,
  maxTotalBytes: 512 * 1024 * 1024,
  maxFileBytes: 50 * 1024 * 1024,
  entries: [
    { path: 'video', isDirectory: true, size: 0 },
    { path: 'video/scene.tsx', isDirectory: false, size: 100 },
    { path: 'video/title.tsx', isDirectory: false, size: 200 },
    { path: 'api', isDirectory: true, size: 0 },
    { path: 'api/main.go', isDirectory: false, size: 300 },
    { path: 'README.md', isDirectory: false, size: 50 },
  ],
  ...over,
});

beforeEach(() => {
  h.repositories.data = undefined;
  h.repositories.isLoading = false;
  h.repositories.error = null;
  h.files.data = undefined;
  h.files.isLoading = false;
  h.files.error = null;
  h.mutate.mockReset();
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

describe('groupRepositories', () => {
  it('groups by the GitHub account, because that is what owns the installation', () => {
    const groups = groupRepositories([
      repo({ id: 1, fullName: 'acme/a', accountLogin: 'acme' }),
      repo({ id: 2, fullName: 'reelbolt/b', accountLogin: 'reelbolt', accountType: 'Organization' }),
      repo({ id: 3, fullName: 'acme/c', accountLogin: 'acme' }),
    ]);

    expect(groups.map((g) => g.accountLogin)).toEqual(['acme', 'reelbolt']);
    expect(groups[0].repositories.map((r) => r.fullName)).toEqual(['acme/a', 'acme/c']);
    expect(groups[1].accountType).toBe('Organization');
  });

  it('orders deterministically, so the list cannot reshuffle under the cursor between renders', () => {
    const once = groupRepositories([
      repo({ id: 3, fullName: 'acme/zeta' }),
      repo({ id: 1, fullName: 'acme/alpha' }),
      repo({ id: 2, fullName: 'acme/mid' }),
    ]);
    const twice = groupRepositories([
      repo({ id: 2, fullName: 'acme/mid' }),
      repo({ id: 3, fullName: 'acme/zeta' }),
      repo({ id: 1, fullName: 'acme/alpha' }),
    ]);

    expect(once[0].repositories.map((r) => r.fullName)).toEqual(['acme/alpha', 'acme/mid', 'acme/zeta']);
    expect(twice).toEqual(once);
  });
});

describe('filterRepositories', () => {
  const all = [
    repo({ id: 1, fullName: 'acme/promo-videos' }),
    repo({ id: 2, fullName: 'acme/Website' }),
    repo({ id: 3, fullName: 'reelbolt/private-thing' }),
  ];

  it('returns everything for an empty or whitespace query', () => {
    expect(filterRepositories(all, '')).toHaveLength(3);
    expect(filterRepositories(all, '   ')).toHaveLength(3);
  });

  it('matches the owner as well as the name, ignoring case', () => {
    expect(filterRepositories(all, 'REELBOLT').map((r) => r.id)).toEqual([3]);
    expect(filterRepositories(all, 'website').map((r) => r.id)).toEqual([2]);
    expect(filterRepositories(all, 'acme/').map((r) => r.id)).toEqual([1, 2]);
  });

  it('returns nothing rather than everything when nothing matches', () => {
    expect(filterRepositories(all, 'nope')).toEqual([]);
    expect(filterRepositories(all, 'promo/')).toEqual([]);
  });

  it('trims the query, so a stray space does not hide the list', () => {
    // ' acme/promo ' is not a miss: trimming makes it a substring of acme/promo-videos, which is
    // exactly what somebody pasting a name with a trailing space expects.
    expect(filterRepositories(all, ' acme/promo ').map((r) => r.id)).toEqual([1]);
  });
});

describe('describeRepository', () => {
  it('says which are private, and which this workspace already follows', () => {
    expect(en(describeRepository(repo()))).toBe('Private repository');
    expect(en(describeRepository(repo({ isPrivate: false })))).toBe('Public repository');
    expect(en(describeRepository(repo({ linkedProjectId: 'p9' })))).toBe('Already linked to another project in this workspace');
  });
});

describe('GitHubFileSelector', () => {
  const last = (onChange: ReturnType<typeof vi.fn>): FileSelection => onChange.mock.calls.at(-1)![0] as FileSelection;

  it('starts a new link with every file ticked when it fits, and reports "everything"', async () => {
    h.files.data = files();
    const onChange = vi.fn();
    wrap(<GitHubFileSelector projectId="p1" repositoryId={1} branch="main" onChange={onChange} />);

    await waitFor(() => expect(onChange).toHaveBeenCalled());
    expect(last(onChange).includePaths).toEqual([]);
    expect(last(onChange).totals.files).toBe(4);
    expect(screen.getByText(/4 of 4 files chosen/)).toBeTruthy();
  });

  it('turns a folder untick into the smallest list of paths', async () => {
    h.files.data = files();
    const onChange = vi.fn();
    wrap(<GitHubFileSelector projectId="p1" repositoryId={1} branch="main" onChange={onChange} />);

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Folder api' }));

    await waitFor(() => expect(last(onChange).includePaths).toEqual(['video', 'README.md']));
    expect(last(onChange).totals.files).toBe(3);
  });

  it('starts from the saved choice when editing', async () => {
    h.files.data = files();
    const onChange = vi.fn();
    wrap(<GitHubFileSelector projectId="p1" repositoryId={null} branch="main" initialIncludePaths={['video/scene.tsx']} onChange={onChange} />);

    await waitFor(() => expect(last(onChange).includePaths).toEqual(['video/scene.tsx']));
    const video = screen.getByRole('checkbox', { name: 'Folder video' }) as HTMLInputElement;
    expect(video.checked).toBe(false);
    expect(video.getAttribute('data-indeterminate')).toBe('true');
  });

  it('starts with nothing ticked when the repository is over the limit, and says why it cannot be saved', async () => {
    h.files.data = files({ maxFiles: 2 });
    const onChange = vi.fn();
    wrap(<GitHubFileSelector projectId="p1" repositoryId={1} branch="main" onChange={onChange} />);

    await waitFor(() => expect(last(onChange).problem).toEqual({ kind: 'empty' }));
    expect(last(onChange).includePaths).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    await waitFor(() => expect(last(onChange).problem).toEqual({ kind: 'too_many_files', files: 4, limit: 2 }));
    expect(screen.getByText(/one project can sync at most 2/)).toBeTruthy();
  });

  it('narrows the tree by search and selects only the matches', async () => {
    h.files.data = files();
    const onChange = vi.fn();
    wrap(<GitHubFileSelector projectId="p1" repositoryId={1} branch="main" onChange={onChange} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Clear' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Search files' }), { target: { value: 'scene' } });
    expect(screen.queryByRole('checkbox', { name: 'README.md' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Select 1 match' }));
    await waitFor(() => expect(last(onChange).includePaths).toEqual(['video/scene.tsx']));
  });

  it('explains an empty repository instead of showing an empty tree', () => {
    h.files.data = files({ commit: null, entries: [] });
    wrap(<GitHubFileSelector projectId="p1" repositoryId={1} branch="main" onChange={vi.fn()} />);

    expect(screen.getByText(/has no files yet/)).toBeTruthy();
  });

  it('shows the reason a listing failed, with a retry', () => {
    h.files.error = Object.assign(new Error('x'), {});
    wrap(<GitHubFileSelector projectId="p1" repositoryId={1} branch="main" onChange={vi.fn()} />);

    expect(screen.getByText('The files could not be listed')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(h.mutate).toHaveBeenCalled();
  });
});

describe('GitHubRepositoryPicker', () => {
  const list = (repositories: GitHubRepositoryOption[], installUrl: string | null = 'https://github.com/apps/x/installations/new'): GitHubRepositoryList =>
    ({ repositories, truncated: false, installUrl, accountLogin: 'acme' });

  it('narrows the list as the reader types, without another request', () => {
    h.repositories.data = list([
      repo({ id: 1, fullName: 'acme/promo-videos' }),
      repo({ id: 2, fullName: 'acme/website' }),
    ]);
    wrap(<GitHubRepositoryPicker projectId="p1" value={null} onChange={vi.fn()} />);

    expect(screen.getByRole('button', { name: /acme\/website/ })).toBeTruthy();
    fireEvent.change(screen.getByRole('textbox', { name: /Find a repository/ }), { target: { value: 'promo' } });

    expect(screen.getByRole('button', { name: /acme\/promo-videos/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /acme\/website/ })).toBeNull();
  });

  it('says so when nothing matches, rather than showing an empty picker', () => {
    h.repositories.data = list([repo()]);
    wrap(<GitHubRepositoryPicker projectId="p1" value={null} onChange={vi.fn()} />);

    fireEvent.change(screen.getByRole('textbox', { name: /Find a repository/ }), { target: { value: 'zzz' } });
    expect(screen.getByText(/No repository matches/)).toBeTruthy();
  });

  it('reports the repository the reader chose', () => {
    const onChange = vi.fn();
    h.repositories.data = list([repo({ id: 7, fullName: 'acme/promo-videos' })]);
    wrap(<GitHubRepositoryPicker projectId="p1" value={null} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /acme\/promo-videos/ }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ id: 7 }));
  });

  it('offers the GitHub access page, in plain words, when ReelBolt can read nothing yet', () => {
    h.repositories.data = list([], 'https://github.com/apps/reelbolt-test/installations/new');
    wrap(<GitHubRepositoryPicker projectId="p1" value={null} onChange={vi.fn()} />);

    expect(screen.getByText(/has not been given access to any of your/)).toBeTruthy();
    const access = screen.getByRole('link', { name: /Give ReelBolt access to more repositories on GitHub/ });
    expect(access.getAttribute('href')).toBe('https://github.com/apps/reelbolt-test/installations/new');
    // Setup vocabulary stays out of the product.
    expect(document.body.textContent).not.toMatch(/install|webhook|credential/i);
  });

  it('does not let a repository already linked to another project be chosen again', () => {
    const onChange = vi.fn();
    h.repositories.data = list([repo({ id: 7, linkedProjectId: 'p9' })]);
    wrap(<GitHubRepositoryPicker projectId="p1" value={null} onChange={onChange} />);

    const button = screen.getByRole('button', { name: /acme\/promo-videos/ }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });
});
