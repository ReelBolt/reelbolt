// @vitest-environment jsdom
/**
 * Customer e2e finding 10: the new-workflow page showed an empty browser tab title and "Project"
 * in the breadcrumb, and labelled the template picker "Apply WOW Template (Optional)".
 */
import { Suspense } from 'react';
import { render, screen, cleanup, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => ({ data: { id: 'p1', name: 'Spring launch' } }) }));
// The project's apps: `source` is the template each of its workflows was made from.
const H = vi.hoisted(() => ({ sources: ['quick-win-promo'] as string[] }));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({
  useWorkflowApps: () => ({ data: { apps: H.sources.map((source) => ({ workflowId: source, source })), recipes: [] }, error: undefined }),
}));
vi.mock('@/lib/api/workflows', () => ({
  getWorkflowTemplates: vi.fn(async () => [
    { key: 'quick-win-promo', name: 'Promo from your code', description: 'A promo of the app you built.', version: 1, autoCreateOnProject: true, requiresUserInput: false, stepCount: 11 },
    { key: 'reel', name: '15-second vertical reel', description: 'A punchy reel.', version: 2, autoCreateOnProject: false, requiresUserInput: true, stepCount: 6 },
  ]),
  applyWorkflowTemplate: vi.fn(),
  createWorkflow: vi.fn(),
}));
vi.mock('@/components/workflows/FlowchartBuilder', () => ({ FlowchartBuilderWrapper: () => <div data-testid="builder" /> }));

import NewWorkflowPage from './page';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});
afterEach(() => {
  cleanup();
  H.sources = ['quick-win-promo'];
});

async function renderPage() {
  const params = Promise.resolve({ id: 'p1' });
  await act(async () => {
    await params;
  });
  await act(async () => {
    render(
      <SWRConfig value={{ provider: () => new Map() }}>
        <MantineProvider>
          <Suspense fallback={null}>
            <NewWorkflowPage params={params} />
          </Suspense>
        </MantineProvider>
      </SWRConfig>,
    );
  });
}

describe('New workflow page', () => {
  it("names the project in the breadcrumb and the browser tab, and labels the template picker plainly", async () => {
    await renderPage();

    const crumb = await screen.findByRole('link', { name: 'Spring launch' });
    expect(crumb.getAttribute('href')).toBe('/projects/p1');
    await waitFor(() => expect(document.title).toBe('New workflow · Spring launch · ReelBolt'));
    expect(screen.getByText('Start from a template')).toBeTruthy();
    expect(await screen.findByText('15-second vertical reel')).toBeTruthy();
    expect(screen.queryByText(/WOW/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/v2 ·|6 steps/);
    // This project already has the promo it was created with: it is not offered a second time.
    expect(screen.queryByText('Promo from your code')).toBeNull();
  });

  // A project started from clips was given the footage edit, not the promo from code.
  it('offers the promo from code to a project that was started from clips', async () => {
    H.sources = ['video-derush-edit'];
    await renderPage();
    expect(await screen.findByText('15-second vertical reel')).toBeTruthy();
    expect(screen.getByRole('radio', { name: /Promo from your code/ })).toBeTruthy();
  });
});
