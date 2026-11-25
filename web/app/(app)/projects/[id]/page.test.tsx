// @vitest-environment jsdom
import { Suspense } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

// The URL is the tab's source of truth; `replace` updates it the way the real router would.
let currentSearch = '';
const replace = vi.fn((url: string) => { currentSearch = url.split('?')[1] ?? ''; });
vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace, push: vi.fn() }),
  usePathname: () => '/projects/p1',
  useSearchParams: () => new URLSearchParams(currentSearch),
}));

const projectState = vi.hoisted(() => ({
  data: { id: 'p1', name: 'Launch reel', description: null } as { id: string; name: string; description: null } | undefined,
  error: undefined as unknown,
  mutate: vi.fn(),
}));
vi.mock('@/lib/hooks/use-projects', () => ({
  useProject: () => ({ data: projectState.data, isLoading: false, error: projectState.error, mutate: projectState.mutate }),
}));
const data = vi.hoisted(() => ({
  filesError: undefined as unknown,
  files: [] as unknown[],
  outputs: [] as unknown[],
  cloudProviders: [] as { provider: string; displayName: string; configured: boolean }[] | undefined,
  storage: undefined as { usedBytes: number; limitBytes: number | null; byProject: [] } | undefined,
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: data.filesError ? undefined : data.files, error: data.filesError, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflows: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => ({ data: data.outputs, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-dashboard', () => ({ useDashboardOverview: () => ({ data: undefined, mutate: vi.fn() }) }));
// The page's assistant registration runs for real against a stub shell (mocking the registration
// component by name would make this test file look like a second registrar to the A6 guard).
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistant: () => ({ setContext: () => {}, registerPage: () => () => {} }) }));
vi.mock('@/components/files/FileManager', () => ({
  FileManager: ({ files }: { files: { originalFileName: string }[] }) => <div>files-panel: {files.map((f) => f.originalFileName).join(', ')}</div>,
}));
vi.mock('@/components/projects/ProjectForm', () => ({ ProjectForm: () => null }));
vi.mock('@/components/projects/ProjectSections', () => ({
  ProjectOverviewTab: () => <div>overview-panel</div>,
  ProjectWorkflowsTab: () => <div>workflows-panel</div>,
  ProjectRendersTab: () => <div>renders-panel</div>,
}));
vi.mock('@/components/workflow-apps/AppsGallery', () => ({ AppsGallery: () => <div>apps-gallery-panel</div> }));
vi.mock('@/components/projects/ProjectVoicesTab', () => ({ ProjectVoicesTab: () => <div>voices-panel</div> }));
// Avatars: the requirements route answers only while an operator switch (people or objects) is on; with both off, it is 404 and the tab is hidden.
const avatars = vi.hoisted(() => ({ requirements: undefined as unknown, error: undefined as unknown }));
vi.mock('@/lib/hooks/use-avatars', () => ({
  useAvatarRequirements: () => ({ data: avatars.requirements, error: avatars.error }),
  useProjectAvatars: () => ({ data: [] }),
  // The tab's permission row; with no answer and nothing in the requirements it shows nothing.
  useAvatarMediaConsent: () => ({ data: undefined, mutate: async () => undefined }),
}));
// The tab's "Being made" list; this project has no builds.
vi.mock('@/lib/hooks/use-avatar-builds', () => ({ useAvatarBuilds: () => ({ builds: [], off: false, refresh: async () => undefined }) }));
vi.mock('@/components/avatars/AvatarFormModal', () => ({ AvatarFormModal: () => null }));
vi.mock('@/components/avatars/ObjectFormModal', () => ({ ObjectFormModal: () => null }));
vi.mock('@/components/avatars/AvatarDialogs', () => ({
  AvatarEditModal: () => null, AvatarRevokeModal: () => null, AvatarDeleteModal: () => null, AvatarDetailsModal: () => null,
}));
vi.mock('@/components/projects/ProjectPronunciationTab', () => ({ ProjectPronunciationTab: () => <div>pronunciation-panel</div> }));
const profileTab = vi.hoisted(() => ({ throws: false }));
vi.mock('@/components/design-system/ProjectProfileTab', () => ({
  ProjectProfileTab: () => {
    if (profileTab.throws) throw new Error('profile boom');
    return <div>profile-panel</div>;
  },
}));
vi.mock('@/components/cloud-storage/hooks', () => ({ useCloudProviders: () => ({ data: data.cloudProviders }) }));
vi.mock('@/components/cloud-storage/ProjectCloudStorageTab', () => ({ ProjectCloudStorageTab: () => <div>cloud-panel</div> }));
vi.mock('@/components/folder-sync/ProjectFolderLinksTab', () => ({ ProjectFolderLinksTab: () => <div>folders-panel</div> }));
// Workspace storage: `undefined` by default, so the ≥ 90 % banner does not appear unless a test asks
// for it. The storage bar's own rendering is covered by the storage tests.
vi.mock('@/lib/hooks/use-storage', () => ({
  useStorageUsage: () => ({ data: data.storage }),
  useProjectFolderLinks: () => ({ data: [], error: undefined, isLoading: false, mutate: vi.fn() }),
}));

// The Repository tab's live GitHub call is what a lazy panel must not make. The link/account reads
// are stubbed; `listGitHubRepositories` is the `/github/repositories` request itself, counted.
const github = vi.hoisted(() => ({ repositoryCalls: 0 }));
vi.mock('@/lib/hooks/use-project-github', () => ({
  useProjectGitHub: () => ({ data: null, isLoading: false, error: null, mutate: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-github-account', () => ({
  useGitHubAccount: () => ({
    data: { connected: true, login: 'octocat', avatarUrl: null, canConnect: true, revokedReason: null },
    error: null,
    mutate: vi.fn(),
  }),
}));
vi.mock('@/lib/api/github', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/github')>()),
  listGitHubRepositories: () => {
    github.repositoryCalls += 1;
    return Promise.resolve({ repositories: [], truncated: false, installUrl: null, accountLogin: null });
  },
}));

import ProjectDetailPage from './page';
import { WorkflowAppsFlagProvider } from '@/components/shell/WorkflowAppsFlag';

beforeEach(() => {
  currentSearch = '';
  avatars.requirements = undefined;
  avatars.error = undefined;
  data.files = [];
  data.filesError = undefined;
  projectState.data = { id: 'p1', name: 'Launch reel', description: null };
  projectState.error = undefined;
  projectState.mutate.mockClear();
  data.outputs = [];
  data.cloudProviders = [];
  data.storage = undefined;
  github.repositoryCalls = 0;
  replace.mockClear();
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

/**
 * One params promise for every `rerender`. The page reads it with `use()`; a promise minted on each
 * render is a new, unresolved thing to React, so the page re-suspends, its layout effects are
 * cleaned up and re-run, and SWR revalidates the (already mounted) Repository panel a second time.
 */
const PROJECT_PARAMS = Promise.resolve({ id: 'p1' });

/** A fresh SWR cache per page render, so one test's GitHub answer cannot satisfy the next. */
function pageTree() {
  return (
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <Suspense fallback={null}><ProjectDetailPage params={PROJECT_PARAMS} /></Suspense>
      </MantineProvider>
    </SWRConfig>
  );
}

async function renderPage() {
  let utils!: ReturnType<typeof render>;
  await act(async () => { utils = render(pageTree()); });
  return utils;
}

async function rerenderPage(rerender: ReturnType<typeof render>['rerender']) {
  await act(async () => { rerender(pageTree()); });
}

/** The same page under the shell's WORKFLOW_APPS_ENABLED value. */
async function renderPageWithAppsFlag(enabled: boolean) {
  await act(async () => {
    render(
      <WorkflowAppsFlagProvider enabled={enabled}>
        <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
          <MantineProvider>
            <Suspense fallback={null}><ProjectDetailPage params={Promise.resolve({ id: 'p1' })} /></Suspense>
          </MantineProvider>
        </SWRConfig>
      </WorkflowAppsFlagProvider>,
    );
  });
}

const selectedTab = () => screen.getAllByRole('tab').find((t) => t.getAttribute('aria-selected') === 'true')?.textContent;

describe('Project page: apps gallery behind WORKFLOW_APPS_ENABLED', () => {
  it('shows the app gallery under an Apps tab by default', async () => {
    currentSearch = 'tab=workflows';
    await renderPage();
    expect(selectedTab()).toBe('Apps');
    expect(screen.getByText('apps-gallery-panel')).toBeTruthy();
    expect(screen.queryByText('workflows-panel')).toBeNull();
  });

  it('falls back to today\'s Workflows tab when the flag is off', async () => {
    currentSearch = 'tab=workflows';
    await renderPageWithAppsFlag(false);
    expect(selectedTab()).toBe('Workflows');
    expect(screen.getByText('workflows-panel')).toBeTruthy();
    expect(screen.queryByText('apps-gallery-panel')).toBeNull();
  });
});

describe('Project page tabs', () => {
  it('opens the Voices tab from a ?tab=voices deep link instead of falling back to Overview', async () => {
    currentSearch = 'tab=voices';
    await renderPage();
    expect(selectedTab()).toBe('Voices');
  });

  it('switches to Voices when the tab is clicked', async () => {
    const { rerender } = await renderPage();
    expect(selectedTab()).toBe('Overview');
    fireEvent.click(screen.getByRole('tab', { name: /Voices/ }));
    expect(replace).toHaveBeenCalledWith('/projects/p1?tab=voices', { scroll: false });
    await rerenderPage(rerender);
    expect(selectedTab()).toBe('Voices');
  });

  it('still falls back to Overview for an unknown tab value', async () => {
    currentSearch = 'tab=nope';
    await renderPage();
    expect(selectedTab()).toBe('Overview');
  });

  it('opens the Brand and voice tab from a ?tab=profile deep link', async () => {
    currentSearch = 'tab=profile';
    await renderPage();
    expect(selectedTab()).toBe('Brand and voice');
    expect(screen.getByText('profile-panel')).toBeTruthy();
  });

  it('shows a friendly fallback, not a crashed page, when the Brand and voice tab throws', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    currentSearch = 'tab=profile';
    profileTab.throws = true;
    try {
      await renderPage();
      expect(screen.getByText('Something went wrong showing this tab.')).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Reload' })).toBeTruthy();
      expect(screen.getByRole('tab', { name: /Overview/ })).toBeTruthy();
    } finally {
      profileTab.throws = false;
    }
  });

  it('switches to Brand and voice when the tab is clicked', async () => {
    const { rerender } = await renderPage();
    expect(selectedTab()).toBe('Overview');
    fireEvent.click(screen.getByRole('tab', { name: /Brand and voice/ }));
    expect(replace).toHaveBeenCalledWith('/projects/p1?tab=profile', { scroll: false });
    await rerenderPage(rerender);
    expect(selectedTab()).toBe('Brand and voice');
  });
});

describe('Project page lazy panels', () => {
  it('mounts neither the Repository nor the Renders panel, and calls no GitHub, on Overview', async () => {
    await renderPage();
    expect(screen.getByText('overview-panel')).toBeTruthy();
    expect(screen.queryByText('Link a GitHub repository')).toBeNull();
    expect(screen.queryByText('renders-panel')).toBeNull();
    expect(github.repositoryCalls).toBe(0);
  });

  it('mounts the Renders panel on first visit', async () => {
    const { rerender } = await renderPage();
    expect(screen.queryByText('renders-panel')).toBeNull();
    fireEvent.click(screen.getByRole('tab', { name: /Videos/ }));
    await rerenderPage(rerender);
    expect(screen.getByText('renders-panel')).toBeTruthy();
  });

  it('opens the Repository tab on first visit, fetches once, and keeps it mounted afterwards', async () => {
    const { rerender } = await renderPage();
    expect(github.repositoryCalls).toBe(0);

    fireEvent.click(screen.getByRole('tab', { name: /Repository/ }));
    expect(replace).toHaveBeenCalledWith('/projects/p1?tab=repository', { scroll: false });
    await rerenderPage(rerender);
    await waitFor(() => expect(github.repositoryCalls).toBe(1));
    expect(screen.getByText('Link a GitHub repository')).toBeTruthy();

    // Once visited, the panel stays mounted: switching away and back is not a second first visit.
    fireEvent.click(screen.getByRole('tab', { name: /Overview/ }));
    await rerenderPage(rerender);
    fireEvent.click(screen.getByRole('tab', { name: /Repository/ }));
    await rerenderPage(rerender);
    expect(github.repositoryCalls).toBe(1);
    expect(screen.getByText('Link a GitHub repository')).toBeTruthy();
  });

  it('treats a ?tab=repository deep link as visited and fetches once', async () => {
    currentSearch = 'tab=repository';
    await renderPage();
    expect(screen.getByText('Link a GitHub repository')).toBeTruthy();
    await waitFor(() => expect(github.repositoryCalls).toBe(1));
  });

  it('still mounts the Files panel (and its upload surface) for ?tab=files', async () => {
    currentSearch = 'tab=files';
    await renderPage();
    expect(screen.getByText(/files-panel/)).toBeTruthy();
  });
});

describe('Project page Cloud storage tab', () => {
  const configured = [{ provider: 'dropbox', displayName: 'Dropbox', configured: true }];

  it('is hidden when this server has no cloud storage set up', async () => {
    data.cloudProviders = [{ provider: 'dropbox', displayName: 'Dropbox', configured: false }];
    await renderPage();
    expect(screen.queryByRole('tab', { name: /Cloud storage/ })).toBeNull();
  });

  it('is offered next to Repository when a provider is set up, and ?tab=cloud opens it', async () => {
    data.cloudProviders = configured;
    currentSearch = 'tab=cloud';
    await renderPage();
    const names = screen.getAllByRole('tab').map((t) => t.textContent);
    expect(names.slice(-4)).toEqual(['Repository', 'Cloud storage', 'Folders on your computers', 'History']);
    expect(selectedTab()).toBe('Cloud storage');
    expect(screen.getByText('cloud-panel')).toBeTruthy();
  });

  it('falls back to Overview for ?tab=cloud when nothing is set up', async () => {
    data.cloudProviders = [];
    currentSearch = 'tab=cloud';
    await renderPage();
    expect(selectedTab()).toBe('Overview');
  });
});

describe('Project page computer folders', () => {
  it('offers the Folders tab even when no cloud storage is set up, and ?tab=folders opens it', async () => {
    data.cloudProviders = [];
    currentSearch = 'tab=folders';
    await renderPage();
    expect(selectedTab()).toBe('Folders on your computers');
    expect(screen.getByText('folders-panel')).toBeTruthy();
  });

  it('warns above every tab at 90 % of the plan storage, linking to Billing', async () => {
    data.storage = { usedBytes: 9_500_000_000, limitBytes: 10_000_000_000, byProject: [] };
    await renderPage();
    const warning = screen.getByTestId('project-storage-warning');
    expect(warning.textContent).toContain('nearly out of storage');
    expect(within(warning).getByRole('link', { name: /See plans and storage/ }).getAttribute('href')).toBe('/settings/billing');
  });

  it('says nothing about storage below 90 %, and nothing at all when it is unlimited', async () => {
    data.storage = { usedBytes: 5_000_000_000, limitBytes: 10_000_000_000, byProject: [] };
    await renderPage();
    expect(screen.queryByTestId('project-storage-warning')).toBeNull();

    data.storage = { usedBytes: 900_000_000_000, limitBytes: null, byProject: [] };
    const { unmount } = await renderPage();
    expect(screen.queryByTestId('project-storage-warning')).toBeNull();
    unmount();
  });
});

describe('Project page Files — earlier versions', () => {
  const render2 = (id: string, key: string, at: string, runNumber: number) => ({
    stepResultId: id, workflowExecutionId: `e${id}`, workflowStepId: 'compile', storageKey: key, fileName: `${id}.mp4`,
    producedAt: at, workflowDefinitionId: 'wf', takeGroupKey: 'wf:3:VideoCompile', runNumber, executionStatus: 'Passed',
  });
  const file = (name: string, key: string) => ({ id: name, originalFileName: name, storageKey: key, category: 'outputFiles' });

  it('lists only the latest take of each video and reveals earlier ones on request', async () => {
    data.outputs = [render2('r2', 'k2', '2026-10-05T11:00:00Z', 2), render2('r1', 'k1', '2026-10-05T09:00:00Z', 1)];
    data.files = [file('Reel - run 2.mp4', 'k2'), file('Reel - run 1.mp4', 'k1'), file('logo.png', 'k-logo')];
    currentSearch = 'tab=files';
    await renderPage();

    expect(screen.getByText('files-panel: Reel - run 2.mp4, logo.png')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Show 1 earlier version of your videos' }));
    expect(screen.getByText('files-panel: Reel - run 2.mp4, Reel - run 1.mp4, logo.png')).toBeTruthy();
  });
});

describe('Project page Avatars tab', () => {
  const requirements = {
    statementVersion: 'avatar-consent-2026-11', statements: { en: 'EN', it: 'IT' }, locales: ['en', 'it'],
    limits: { maxAvatarsPerProject: 20 }, mimeTypes: { image: [], video: [], audio: [] }, roles: [], bases: [], disclosureLabels: [],
    entitled: true, moderationEnabled: false, csamScanningEnabled: false,
  };

  it('is hidden when the requirements route answers 404 (switch off), and ?tab=avatars falls back to Overview', async () => {
    avatars.error = Object.assign(new Error('Not Found'), { status: 404 });
    currentSearch = 'tab=avatars';
    await renderPage();
    expect(screen.queryByRole('tab', { name: /Avatars/ })).toBeNull();
    expect(selectedTab()).toBe('Overview');
  });

  it('shows when only objects are switched on, with the objects in it', async () => {
    avatars.requirements = {
      ...requirements, entitled: false,
      kinds: { person: { enabled: false, entitled: false }, object: { enabled: true, entitled: true } },
      objectStatements: { en: 'OBJ' }, objectRoles: ['Front'], objectLimits: { maxImages: 8, maxVideos: 3 },
    };
    currentSearch = 'tab=avatars';
    await renderPage();
    expect(selectedTab()).toBe('Avatars');
    expect(screen.getByRole('button', { name: 'New object' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New avatar' })).toBeNull();
  });

  it('is hidden when the API answers with neither kind switched on', async () => {
    avatars.requirements = {
      ...requirements,
      kinds: { person: { enabled: false, entitled: true }, object: { enabled: false, entitled: true } },
    };
    currentSearch = 'tab=avatars';
    await renderPage();
    expect(screen.queryByRole('tab', { name: /Avatars/ })).toBeNull();
    expect(selectedTab()).toBe('Overview');
  });

  it('sits next to Voices when the API answers, and ?tab=avatars (the retention email link) opens it', async () => {
    avatars.requirements = requirements;
    currentSearch = 'tab=avatars';
    await renderPage();
    const names = screen.getAllByRole('tab').map((t) => t.textContent);
    expect(names[names.indexOf('Voices') + 1]).toBe('Avatars');
    expect(selectedTab()).toBe('Avatars');
    expect(screen.getByTestId('avatars-tab')).toBeTruthy();
  });
});

describe('Project page: failures', () => {
  const failing = (status: number, message: string) => Object.assign(new Error(message), { status });
  it.each([
    ['a server failure', failing(500, 'Internal Server Error'), 'Something went wrong on our side', true],
    ['a network failure', new TypeError('Failed to fetch'), 'Could not reach ReelBolt', true],
    ['a missing project', failing(404, ''), 'Not found: Project', false],
    ['a refused project', failing(403, ''), 'No permission', false],
  ])('says %s plainly with a way back, and Retry only when it can help', async (_n, err, title, retry) => {
    projectState.data = undefined;
    projectState.error = err;
    await renderPage();
    expect(screen.getByRole('alert').textContent).toContain(title);
    expect(screen.getByRole('alert').textContent).not.toContain('Internal Server Error');
    expect(screen.getByRole('link', { name: 'Back to projects' }).getAttribute('href')).toBe('/projects');
    const button = screen.queryByRole('button', { name: 'Retry' });
    expect(Boolean(button)).toBe(retry);
    if (button) {
      fireEvent.click(button);
      expect(projectState.mutate).toHaveBeenCalled();
    }
  });

  it('shows the project when it loads', async () => {
    await renderPage();
    expect(screen.getAllByText('Launch reel').length).toBeGreaterThan(0);
    expect(screen.queryByTestId('failure-card')).toBeNull();
  });

  it('words a Files tab failure instead of loading forever', async () => {
    data.filesError = failing(500, 'Internal Server Error');
    await renderPage();
    const card = screen.getByTestId('failure-card');
    expect(card.textContent).toContain('Something went wrong on our side');
    expect(card.textContent).not.toContain('Internal Server Error');
    expect(within(card).getByRole('button', { name: 'Retry', hidden: true })).toBeTruthy();
  });
});
