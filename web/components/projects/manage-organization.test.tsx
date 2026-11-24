// @vitest-environment jsdom
// QA (project-organization): the manage dialog had no test. Pins delete-with-count confirmation for tags and folders.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ProjectFolder, ProjectTag } from '@/lib/types/project-organization';

const H = vi.hoisted(() => ({
  deleteTag: vi.fn(),
  updateTag: vi.fn(),
  createTag: vi.fn(),
  deleteFolder: vi.fn(),
  renameFolder: vi.fn(),
  createFolder: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));
vi.mock('@/lib/hooks/use-project-tags', () => ({
  useProjectTagMutations: () => ({ deleteTag: H.deleteTag, updateTag: H.updateTag, createTag: H.createTag }),
}));
vi.mock('@/lib/hooks/use-project-folders', () => ({
  useProjectFolderMutations: () => ({ deleteFolder: H.deleteFolder, renameFolder: H.renameFolder, createFolder: H.createFolder }),
}));

import { ManageOrganizationModal } from './ManageOrganizationModal';

const TAGS: ProjectTag[] = [{ id: 't1', name: 'Acme', color: 'blue', projectCount: 3, createdAt: '' }];
const FOLDERS: ProjectFolder[] = [{ id: 'f1', name: 'Clients', projectCount: 1, createdAt: '' }];
const wrap = () =>
  render(
    <MantineProvider>
      <ManageOrganizationModal opened onClose={vi.fn()} tags={TAGS} folders={FOLDERS} />
    </MantineProvider>,
  );

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  for (const f of [H.deleteTag, H.updateTag, H.createTag, H.deleteFolder, H.renameFolder, H.createFolder]) f.mockReset().mockResolvedValue(undefined);
  H.notify.mockReset();
});
afterEach(cleanup);

describe('ManageOrganizationModal', () => {
  it('asks before deleting a tag, says how many projects lose it, then deletes', async () => {
    wrap();
    fireEvent.click(screen.getByLabelText('Delete: Acme'));
    expect(screen.getByText(/3 projects will lose this tag/)).toBeTruthy();
    expect(H.deleteTag).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Yes, delete'));
    await waitFor(() => expect(H.deleteTag).toHaveBeenCalledWith('t1'));
  });

  it('asks before deleting a folder and says its projects stay', async () => {
    wrap();
    fireEvent.click(screen.getByRole('tab', { name: 'Folders' }));
    fireEvent.click(await screen.findByLabelText('Delete: Clients'));
    expect(screen.getByText(/1 project stays/)).toBeTruthy();
    fireEvent.click(screen.getByText('Yes, delete'));
    await waitFor(() => expect(H.deleteFolder).toHaveBeenCalledWith('f1'));
  });
});
