// @vitest-environment jsdom
/** Stored-data rule, part 2: a compound clip and a new picture keep their English default names under the Italian UI. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp } from '@/lib/editing/engine';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { StructurePanel } from '../panels/StructurePanel';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('requestAnimationFrame', () => 0);
vi.stubGlobal('cancelAnimationFrame', () => {});

const mocks = vi.hoisted(() => ({ createImageDocument: vi.fn(async () => ({ id: 'new-1' })) }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));
vi.mock('@/lib/hooks/use-image-documents', () => ({ useImageDocuments: () => ({ data: [], isLoading: false, error: undefined, mutate: vi.fn(async () => undefined) }) }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/api/image-documents', () => ({
  createImageDocument: mocks.createImageDocument,
  deleteImageDocument: vi.fn(), updateImageDocument: vi.fn(), duplicateImageDocument: vi.fn(),
}));
vi.mock('@/lib/editor/open-thumbnail-editor', () => ({
  documentForImage: vi.fn(),
  isEditableImageType: () => true,
  thumbnailEditorHref: (p: string, d: string) => `/editor/thumbnails/${d}?project=${p}`,
}));

import { PicturesPanel } from '../PicturesPanel';

afterEach(cleanup);

describe('stored names stay English with the interface in Italian (part 2)', () => {
  it('a compound clip made with an empty name is stored as "Compound clip"', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('a', 0, 30), media('b', 30, 60, 100)])]), revision: 1 });
    let n = 0;
    const props = { doc: store.getState().doc, readOnly: false, playheadFrame: 0, playbackSpeed: 0, newId: () => `n${++n}`, onOp: (op: EditOp, l?: string) => store.dispatch(op, l), onApply: (ops: readonly EditOp[], l: string) => store.dispatchBatch(ops, l), onSelect: () => {}, selection: ['a', 'b'] };
    renderWithI18n(<StructurePanel {...props} />, { locale: 'it' });
    fireEvent.change(screen.getAllByRole('textbox')[0], { target: { value: '' } });
    fireEvent.click(screen.getByTestId('compound-create'));
    expect(store.getState().doc.sequences?.[0].name).toBe('Compound clip');
  });

  it('a new picture is created with the English default name', async () => {
    renderWithI18n(<PicturesPanel projectId="p1" />, { locale: 'it' });
    fireEvent.click(screen.getByTestId('picture-new-create'));
    await waitFor(() => expect(mocks.createImageDocument).toHaveBeenCalled());
    const call = mocks.createImageDocument.mock.calls[0] as unknown as [string, { name: string }];
    expect(call[1].name).toBe('Untitled thumbnail');
  });
});
