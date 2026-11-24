// @vitest-environment jsdom
import { Suspense } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';

/**
 * Regression test for the reported bug: on the project page, a file dragged onto the page did
 * nothing and the header "Upload files" button did nothing either, while attaching the same file
 * through the assistant chat did upload it.
 *
 * Both symptoms had one cause. Mantine v9 keeps an inactive \`Tabs.Panel\` MOUNTED but wraps it in
 * React\`s <Activity mode="hidden">, which UNMOUNTS the subtree\`s effects and detaches its DOM refs.
 * The whole upload surface - the window-wide drop listeners (\`useWindowFileDrop\`) and the hidden
 * <input type="file"> the header button clicks through (\`registerPicker\`) - lives in the Files
 * panel, so both were inert until something switched to that tab. The assistant composer is mounted
 * outside the tabs, which is why the same upload worked from chat.
 *
 * These assertions fail against that behaviour: they exercise the page as a user meets it, on the
 * DEFAULT tab, rather than on \`?tab=files\`.
 */

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

let currentSearch = '';
const replace = vi.fn((url: string) => { currentSearch = url.split('?')[1] ?? ''; });
vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace, push: vi.fn() }),
  usePathname: () => '/projects/p1',
  useSearchParams: () => new URLSearchParams(currentSearch),
}));

vi.mock('@/lib/hooks/use-projects', () => ({
  useProject: () => ({ data: { id: 'p1', name: 'Launch reel', description: null }, isLoading: false, error: undefined, mutate: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [], mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflows: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => ({ data: [], mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-dashboard', () => ({ useDashboardOverview: () => ({ data: undefined, mutate: vi.fn() }) }));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({
  useAssistantShell: () => ({ openWithPrompt: vi.fn() }),
  useAssistant: () => ({ setContext: () => {}, registerPage: () => () => {} }),
}));
vi.mock('@/components/projects/ProjectForm', () => ({ ProjectForm: () => null }));
vi.mock('@/components/projects/ProjectSections', () => ({
  ProjectOverviewTab: () => <div>overview-panel</div>,
  ProjectWorkflowsTab: () => <div>workflows-panel</div>,
  ProjectRendersTab: () => <div>renders-panel</div>,
}));
vi.mock('@/components/workflow-apps/AppsGallery', () => ({ AppsGallery: () => <div>apps-gallery-panel</div> }));
vi.mock('@/components/projects/ProjectVoicesTab', () => ({ ProjectVoicesTab: () => <div>voices-panel</div> }));
vi.mock('@/components/projects/ProjectPronunciationTab', () => ({ ProjectPronunciationTab: () => <div>pronunciation-panel</div> }));

// Only the two upload calls are stubbed; everything else in the module stays real.
const uploadFileWithProgress = vi.hoisted(() =>
  vi.fn((_projectId: string, _file: File, _onProgress: (percent: number) => void, _options?: { relativePath?: string }) =>
    Promise.resolve({ id: 'f1' })),
);
const uploadFile = vi.hoisted(() => vi.fn((_projectId: string, _file: File) => Promise.resolve({ id: 'f1' })));
vi.mock('@/lib/api/files', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/files')>()),
  uploadFileWithProgress,
  uploadFile,
}));

import ProjectDetailPage from './page';

/** A drop event the page's window listener accepts: \`types\` carries "Files", \`files\` the payload. */
function dropEvent(type: string, files: File[], types: string[] = ['Files']) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', {
    value: { types, files, items: [], getData: () => '', setData: () => {}, dropEffect: 'none' },
  });
  return event;
}

function dropFile(name = 'footage.mp4') {
  const file = new File(['hello'], name, { type: 'video/mp4' });
  document.dispatchEvent(dropEvent('dragenter', [file]));
  const over = dropEvent('dragover', [file]);
  document.dispatchEvent(over);
  expect(over.defaultPrevented).toBe(true); // the browser only fires `drop` if dragover is cancelled
  document.dispatchEvent(dropEvent('drop', [file]));
  return file;
}

async function renderPage() {
  await act(async () => {
    render(
      <MantineProvider>
        <Notifications />
        <Suspense fallback={null}><ProjectDetailPage params={Promise.resolve({ id: 'p1' })} /></Suspense>
      </MantineProvider>,
    );
  });
}

beforeEach(() => {
  currentSearch = '';
  replace.mockClear();
  uploadFileWithProgress.mockClear();
  uploadFile.mockClear();
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('project page upload surface', () => {
  it('uploads a file dropped on the page while the default (Overview) tab is showing', async () => {
    await renderPage();
    expect(screen.getByText('overview-panel')).toBeTruthy();

    await act(async () => { dropFile(); });

    expect(uploadFileWithProgress).toHaveBeenCalledTimes(1);
    expect(uploadFileWithProgress.mock.calls[0][0]).toBe('p1');
  });

  it('opens the file picker from the header "Upload files" button on the default tab', async () => {
    await renderPage();

    const click = vi.spyOn(HTMLInputElement.prototype, 'click');
    try {
      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
      });
      expect(replace).toHaveBeenCalledWith('/projects/p1?tab=files', { scroll: false });
      // The Files panel's own hidden <input type="file"> is the control the button drives.
      expect(click.mock.instances.some((i) => (i as unknown as HTMLInputElement).type === 'file')).toBe(true);
    } finally {
      click.mockRestore();
    }
  });
});
