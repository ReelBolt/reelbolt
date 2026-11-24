// @vitest-environment jsdom
/**
 * The `alpha.first_project` call-site (alpha-launch T-018). What this protects: the funnel event is
 * emitted from the REAL transition — the project form reporting a successful creation — and not on
 * a page load. `emitFunnelEvent` itself enforces once-per-user and the consent gate (see
 * `web/lib/telemetry/funnel.test.ts`); this test proves the page calls it at all, on the transition.
 */
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { userKeyFromEmail } from '@/lib/telemetry';

const mocks = vi.hoisted(() => ({
  emitFunnelEvent: vi.fn(),
  push: vi.fn(),
  mutate: vi.fn(),
  mutateOverview: vi.fn(),
  captured: { onSuccess: undefined as ((project?: { id: string }) => void) | undefined },
}));

// Only the emitter is replaced; `userKeyFromEmail` stays real, so the key asserted below is the one
// the app actually derives.
vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  emitFunnelEvent: mocks.emitFunnelEvent,
}));
vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: mocks.push, replace: vi.fn() }),
  usePathname: () => '/projects',
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock('@/lib/hooks/use-project-folders', () => ({ useProjectFolders: () => ({ data: [], mutate: vi.fn() }),
  useProjectFolderMutations: () => ({}),
}));
vi.mock('@/lib/hooks/use-project-tags', () => ({
  useProjectTags: () => ({ data: [], mutate: vi.fn() }),
  useProjectTagMutations: () => ({ setOrganization: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'Ada@Example.com' } }) }));
vi.mock('@/lib/hooks/use-projects', () => ({
  useProjects: () => ({ data: [], isLoading: false, error: undefined, mutate: mocks.mutate }),
}));
vi.mock('@/lib/hooks/use-dashboard', () => ({
  useDashboardOverview: () => ({ data: undefined, mutate: mocks.mutateOverview }),
}));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({
  useAssistantShell: () => ({ openWithPrompt: vi.fn() }),
}));
// The form is the transition's source: capturing its `onSuccess` is how this test drives it.
vi.mock('@/components/projects/ProjectForm', () => ({
  ProjectForm: (props: { onSuccess: (project?: { id: string }) => void }) => {
    mocks.captured.onSuccess = props.onSuccess;
    return null;
  },
}));

import ProjectsPage from './page';

// MantineProvider reads window.matchMedia, which jsdom does not implement.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

beforeEach(() => {
  mocks.emitFunnelEvent.mockClear();
});

afterEach(() => {
  cleanup();
});

describe('ProjectsPage funnel call-site', () => {
  it('does not emit anything merely because the page rendered', () => {
    render(
      <MantineProvider env="test">
        <ProjectsPage />
      </MantineProvider>,
    );
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
  });

  it('emits alpha.first_project when the form reports a created project', () => {
    render(
      <MantineProvider env="test">
        <ProjectsPage />
      </MantineProvider>,
    );

    act(() => {
      mocks.captured.onSuccess?.({ id: 'p1' });
    });

    expect(mocks.emitFunnelEvent).toHaveBeenCalledWith('alpha.first_project', userKeyFromEmail('Ada@Example.com'));
    // The transition is what moves the page on, exactly as before.
    expect(mocks.push).toHaveBeenCalledWith('/projects/p1?tab=files');
  });
});
