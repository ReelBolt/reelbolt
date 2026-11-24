// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { Project } from '@/lib/types/project';
import type { ProjectFolder, ProjectTag } from '@/lib/types/project-organization';

const H = vi.hoisted(() => ({
  setOrganization: vi.fn(),
  createTag: vi.fn(),
  organize: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('next/link', () => ({ default: ({ children, href, ...r }: { children: React.ReactNode; href: string }) => <a href={href} {...r}>{children}</a> }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));
vi.mock('@/components/outputs/VideoThumb', () => ({ VideoThumb: () => null }));
vi.mock('@/lib/hooks/use-project-tags', () => ({
  useProjectTagMutations: () => ({ setOrganization: H.setOrganization, createTag: H.createTag, organize: H.organize }),
}));

import { ProjectCard, type CardOrganization } from './ProjectCard';
import { ProjectRail } from './ProjectRail';
import { BulkBar } from './BulkBar';

const tag = (id: string, name: string, color = 'blue'): ProjectTag => ({ id, name, color, projectCount: 1, createdAt: '' });
const TAGS = [tag('t1', 'Acme'), tag('t2', 'Promo', 'red'), tag('t3', 'Draft'), tag('t4', 'Rush'), tag('t5', 'Legal')];
const FOLDERS: ProjectFolder[] = [{ id: 'f1', name: 'Clients', projectCount: 2, createdAt: '' }];
const project = (o: Partial<Project> = {}): Project => ({
  id: 'p1', name: 'Launch video', description: '', createdAt: '', updatedAt: '2026-01-01T00:00:00Z', folderId: null, tags: [], ...o,
} as Project);
const org = (o: Partial<CardOrganization> = {}): CardOrganization => ({
  tags: TAGS, folders: FOLDERS, showFolderCrumb: true, selecting: false, selected: false, onToggleSelect: vi.fn(), onDragStart: vi.fn(), ...o,
});
const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  H.setOrganization.mockReset().mockResolvedValue(undefined);
  H.createTag.mockReset().mockResolvedValue(tag('t9', 'New one', 'gray'));
  H.organize.mockReset().mockResolvedValue(undefined);
  H.notify.mockReset();
});
afterEach(cleanup);

describe('ProjectRail', () => {
  const props = () => ({
    folders: FOLDERS, tags: TAGS, totalCount: 5, unfiledCount: 3, folder: 'all' as const, tagIds: [] as string[], dropTarget: null,
    onFolder: vi.fn(), onToggleTag: vi.fn(), onClearTags: vi.fn(), onManage: vi.fn(), onDropOnFolder: vi.fn(), onDropTarget: vi.fn(),
  });
  it('lists All, No folder and folders with counts, and selects them', () => {
    const p = props();
    wrap(<ProjectRail {...p} />);
    expect(screen.getByText('All projects')).toBeTruthy();
    fireEvent.click(screen.getByText('No folder'));
    expect(p.onFolder).toHaveBeenCalledWith('unfiled');
    fireEvent.click(screen.getByText('Clients'));
    expect(p.onFolder).toHaveBeenCalledWith('f1');
  });
  it('toggles tag chips as pressed buttons', () => {
    const p = props();
    wrap(<ProjectRail {...p} tagIds={['t1']} />);
    expect(screen.getByRole('button', { name: 'Acme' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Promo' }));
    expect(p.onToggleTag).toHaveBeenCalledWith('t2');
  });
  it('accepts a project dropped on a folder', () => {
    const p = props();
    wrap(<ProjectRail {...p} />);
    fireEvent.drop(screen.getByText('Clients'), { dataTransfer: { types: ['application/x-reelbolt-project'], getData: () => 'p1' } });
    expect(p.onDropOnFolder).toHaveBeenCalledWith('f1', 'p1');
  });
});

describe('ProjectCard chips', () => {
  it('shows at most 3 chips then +n, and the folder crumb', () => {
    const p = project({ folderId: 'f1', tags: TAGS.map(({ id, name, color }) => ({ id, name, color })) });
    wrap(<ProjectCard project={p} organization={org()} />);
    expect(screen.getByText('Acme')).toBeTruthy();
    expect(screen.getByText('Draft')).toBeTruthy();
    expect(screen.queryByText('Rush')).toBeNull();
    expect(screen.getByText('+2')).toBeTruthy();
    expect(screen.getByText('Folder: Clients')).toBeTruthy();
  });
  it('shows tags without any organization props', () => {
    wrap(<ProjectCard project={project({ tags: [{ id: 't1', name: 'Acme', color: 'blue' }] })} />);
    expect(screen.getByText('Acme')).toBeTruthy();
  });
});

describe('ProjectCard menu (keyboard path)', () => {
  it('adds a tag from the Tags popover', async () => {
    wrap(<ProjectCard project={project()} organization={org()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Options for Launch video' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Tags...' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Promo' }));
    await waitFor(() => expect(H.setOrganization).toHaveBeenCalledWith('p1', { tagIds: ['t2'], folderId: null }));
  });
  it('creates a tag on type and attaches it', async () => {
    wrap(<ProjectCard project={project()} organization={org()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Options for Launch video' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Tags...' }));
    fireEvent.change(await screen.findByPlaceholderText('Find or create a tag'), { target: { value: 'New one' } });
    fireEvent.click(await screen.findByRole('button', { name: /Create tag/, hidden: true }));
    await waitFor(() => expect(H.createTag).toHaveBeenCalledWith({ name: 'New one', color: 'gray' }));
    await waitFor(() => expect(H.setOrganization).toHaveBeenCalledWith('p1', { tagIds: ['t9'], folderId: null }));
  });
  it('explains the cap at 20 tags and blocks adding', async () => {
    const many = Array.from({ length: 20 }, (_, i) => ({ id: `x${i}`, name: `Tag${i}`, color: 'gray' }));
    wrap(<ProjectCard project={project({ tags: many })} organization={org({ tags: [...TAGS, ...many.map((m) => tag(m.id, m.name))] })} />);
    fireEvent.click(screen.getByRole('button', { name: 'Options for Launch video' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Tags...' }));
    expect(await screen.findByText(/up to 20 tags/)).toBeTruthy();
    expect((await screen.findByRole('checkbox', { name: 'Acme', hidden: true }) as HTMLInputElement).disabled).toBe(true);
  });
  it('moves the project to a folder', async () => {
    wrap(<ProjectCard project={project()} organization={org()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Options for Launch video' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Move to folder...' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Clients' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move' }));
    await waitFor(() => expect(H.setOrganization).toHaveBeenCalledWith('p1', { tagIds: [], folderId: 'f1' }));
  });
});

describe('selection and bulk', () => {
  it('toggles selection from the checkbox instead of opening the project', () => {
    const onToggleSelect = vi.fn();
    wrap(<ProjectCard project={project()} organization={org({ selecting: true, onToggleSelect })} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Launch video' }));
    expect(onToggleSelect).toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Options for Launch video' })).toBeNull();
  });
  it('adds a tag to the selected projects in one organize call', async () => {
    const onDone = vi.fn();
    wrap(<BulkBar selectedIds={['p1', 'p2']} tags={TAGS} folders={FOLDERS} onDone={onDone} />);
    expect(screen.getByText('2 projects selected')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Add tag' }));
    fireEvent.click(screen.getByPlaceholderText('Choose a tag'));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme' }));
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(H.organize).toHaveBeenCalledWith({ addTagIds: ['t1'], removeTagIds: [], projectIds: ['p1', 'p2'] }));
    await waitFor(() => expect(onDone).toHaveBeenCalled());
  });
  it('clears the folder, and reports a failed all-or-nothing change without finishing', async () => {
    H.organize.mockRejectedValueOnce(new Error('boom'));
    const onDone = vi.fn();
    wrap(<BulkBar selectedIds={['p1']} tags={TAGS} folders={FOLDERS} onDone={onDone} />);
    fireEvent.click(screen.getByRole('button', { name: 'Clear folder' }));
    await waitFor(() => expect(H.organize).toHaveBeenCalledWith({ addTagIds: [], removeTagIds: [], clearFolder: true, projectIds: ['p1'] }));
    await waitFor(() => expect(H.notify).toHaveBeenCalled());
    expect(onDone).not.toHaveBeenCalled();
  });
});
