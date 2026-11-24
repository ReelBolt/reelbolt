// @vitest-environment jsdom
import { Suspense } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const search = { value: new URLSearchParams() };
vi.mock('next/navigation', () => ({ useSearchParams: () => search.value }));
vi.mock('next/link', () => ({
  default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a>,
}));
// Records what the page tells the platform assistant, so the picture tools' context can be
// asserted on the page that owns it rather than inferred from a rendered body.
const assistantContext = { current: null as null | { projectId?: string; imageDocumentId?: string } };
vi.mock('@/components/assistant/AssistantPageContext', () => ({
  AssistantPageContext: (props: { projectId?: string; imageDocumentId?: string }) => {
    assistantContext.current = props;
    return null;
  },
}));
vi.mock('@/components/editor/thumbnail/ThumbnailEditor', () => ({
  ThumbnailEditor: ({ projectId, documentId }: { projectId: string; documentId: string }) => (
    <div data-testid="editor">{`${projectId}/${documentId}`}</div>
  ),
}));

const device = { value: 'desktop' as 'desktop' | 'phone' };
vi.mock('@/lib/editing/use-device-class', () => ({ useDeviceClass: () => device.value }));

import ThumbnailEditorPage from './page';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(() => {
  cleanup();
  device.value = 'desktop';
});

// React's use() reads a promise that already carries its settled state without suspending.
const resolved = <T,>(value: T) => Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });

const mount = () =>
  render(
    <MantineProvider>
      <Suspense fallback={null}>
        <ThumbnailEditorPage params={resolved({ documentId: 'doc-9' })} />
      </Suspense>
    </MantineProvider>,
  );

describe('the thumbnail editor route', () => {
  it('opens the editor on the document in the path and the project in the query', async () => {
    search.value = new URLSearchParams('project=proj-1');
    mount();
    expect((await screen.findByTestId('editor')).textContent).toBe('proj-1/doc-9');
    expect(screen.getByRole('heading', { name: 'Picture editor' })).toBeTruthy();
  });

  it('folds the header into one compact row on a phone (back link, name, label)', async () => {
    device.value = 'phone';
    search.value = new URLSearchParams('project=proj-1');
    mount();
    await screen.findByTestId('editor');
    const row = screen.getByTestId('picture-header-compact');
    expect(row.querySelector('a')?.getAttribute('href')).toContain('view=pictures');
    expect(row.querySelector('h1')?.textContent).toBe('Picture editor');
    expect(row.querySelector('[data-testid="early-preview-label"]')).toBeTruthy();
    expect(document.querySelector('nav[aria-label], .mantine-Breadcrumbs-root')).toBeNull();
  });

  it('labels the picture editor an early preview in its header, never "beta"', async () => {
    search.value = new URLSearchParams('project=proj-1');
    mount();
    await screen.findByTestId('editor');
    expect(screen.getByRole('button', { name: 'Early preview' })).toBeTruthy();
    expect((document.body.textContent ?? '').toLowerCase()).not.toMatch(/\bbeta\b/);
  });

  it('hands the assistant the open picture and its project', async () => {
    search.value = new URLSearchParams('project=proj-1');
    mount();
    await screen.findByTestId('editor');
    expect(assistantContext.current).toEqual({ projectId: 'proj-1', imageDocumentId: 'doc-9' });
  });

  it('asks the user to open it from a project when the project is missing', async () => {
    search.value = new URLSearchParams();
    mount();
    expect(await screen.findByText('Open this from a project')).toBeTruthy();
    expect(screen.queryByTestId('editor')).toBeNull();
    expect(screen.getByRole('link', { name: 'Go to the Editor' }).getAttribute('href')).toBe('/editor?view=pictures');
  });
});
