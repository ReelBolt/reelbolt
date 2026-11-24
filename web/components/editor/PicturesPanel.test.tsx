// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('requestAnimationFrame', () => 0);
vi.stubGlobal('cancelAnimationFrame', () => {});
vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }));

const mocks = vi.hoisted(() => ({
  refresh: vi.fn(async () => undefined),
  deleteImageDocument: vi.fn(async () => undefined),
  updateImageDocument: vi.fn(async () => ({})),
  duplicateImageDocument: vi.fn(async () => ({})),
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));
vi.mock('@/lib/hooks/use-image-documents', () => ({
  useImageDocuments: () => ({
    data: [{ id: 'd1', name: 'Launch cover', sourceProjectFileId: null, revision: 1, layerCount: 2, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' }],
    isLoading: false,
    error: undefined,
    mutate: mocks.refresh,
  }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/api/image-documents', () => ({
  deleteImageDocument: mocks.deleteImageDocument,
  updateImageDocument: mocks.updateImageDocument,
  duplicateImageDocument: mocks.duplicateImageDocument,
}));
vi.mock('@/lib/editor/open-thumbnail-editor', () => ({
  documentForImage: vi.fn(),
  isEditableImageType: () => true,
  thumbnailEditorHref: (p: string, d: string) => `/editor/thumbnails/${d}?project=${p}`,
}));

import { PicturesPanel } from './PicturesPanel';

const ui = () => (
  <MantineProvider env="test">
    <PicturesPanel projectId="p1" />
  </MantineProvider>
);

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);

describe('PicturesPanel manage menu (PE-007, PE-008)', () => {
  it('duplicates a picture and refreshes the list', async () => {
    render(ui());
    fireEvent.click(screen.getByRole('button', { name: 'More for Launch cover' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Duplicate' }));
    await waitFor(() => expect(mocks.duplicateImageDocument).toHaveBeenCalledWith('p1', 'd1'));
    await waitFor(() => expect(mocks.refresh).toHaveBeenCalled());
  });

  it('deletes a picture only after a confirmation, then refreshes the list', async () => {
    render(ui());
    fireEvent.click(screen.getByRole('button', { name: 'More for Launch cover' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete picture' }));
    expect(await screen.findByText('Delete this picture?')).toBeTruthy();
    expect(mocks.deleteImageDocument).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Delete picture' }));
    await waitFor(() => expect(mocks.deleteImageDocument).toHaveBeenCalledWith('p1', 'd1'));
    await waitFor(() => expect(mocks.refresh).toHaveBeenCalled());
  });

  it('renames a picture with the trimmed name', async () => {
    render(ui());
    fireEvent.click(screen.getByRole('button', { name: 'More for Launch cover' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    const field = await screen.findByLabelText('Picture name');
    fireEvent.change(field, { target: { value: '  Final cover  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save name' }));
    await waitFor(() => expect(mocks.updateImageDocument).toHaveBeenCalledWith('p1', 'd1', { name: 'Final cover' }));
  });
});
