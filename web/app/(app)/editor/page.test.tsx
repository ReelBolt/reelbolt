// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import hub from '@/components/editor/hub/hub.module.css';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
const nav = vi.hoisted(() => ({ search: new URLSearchParams(), replace: vi.fn(), push: vi.fn() }));
vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: nav.push, replace: nav.replace }),
  usePathname: () => '/editor',
  useSearchParams: () => nav.search,
}));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));

const projects = { data: [{ id: 'p1', name: 'Launch reel' }, { id: 'p2', name: 'Interviews' }], isLoading: false, error: undefined };
let renders: { data: unknown[] | undefined } = { data: [] };
let outputs: { data: unknown[] } = { data: [] };
let editTimelines: unknown[] = [];
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => projects }));
vi.mock('@/lib/hooks/use-timelines', () => ({ useProjectTimelines: () => renders }));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => outputs }));
vi.mock('swr', () => ({ default: () => ({ data: editTimelines, error: undefined, isLoading: false, mutate: vi.fn() }) }));
const notif = vi.hoisted(() => ({ show: vi.fn() }));
vi.mock('@mantine/notifications', () => ({ notifications: notif }));
const api = vi.hoisted(() => ({ listEditTimelines: vi.fn(), createEditTimeline: vi.fn() }));
vi.mock('@/lib/editing/sync', async (importOriginal) => ({ ...(await importOriginal<object>()), ...api }));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({ ...(await importOriginal<object>()), ...api }));
let pictures: { data: unknown[] | undefined; isLoading: boolean; error: undefined } = { data: [], isLoading: false, error: undefined };
vi.mock('@/lib/hooks/use-image-documents', () => ({ useImageDocuments: () => pictures }));
vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({
    data: [
      { id: 'img1', originalFileName: 'cover.jpg', mimeType: 'image/jpeg' },
      { id: 'psd1', originalFileName: 'layers.psd', mimeType: 'image/vnd.adobe.photoshop' },
      { id: 'vid1', originalFileName: 'clip.mp4', mimeType: 'video/mp4' },
    ],
  }),
}));

import EditorHubPage from './page';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  renders = { data: [] };
  outputs = { data: [] };
  editTimelines = [];
  pictures = { data: [], isLoading: false, error: undefined };
  nav.search = new URLSearchParams();
  nav.replace.mockReset();
  nav.push.mockReset();
  api.listEditTimelines.mockReset();
  api.createEditTimeline.mockReset();
  notif.show.mockReset();
});
afterEach(cleanup);

const renderPage = () => render(<MantineProvider><EditorHubPage /></MantineProvider>);

describe('Editor hub', () => {
  it('speaks to customers, not developers, and has no v1 file importer', () => {
    renderPage();
    const text = document.body.textContent ?? '';
    for (const jargon of ['timeline.json', 'manifest', 'Offline Inspection', 'Advanced: open a saved timeline file', 'V1', 'A3']) {
      expect(text).not.toContain(jargon);
    }
    expect(screen.getByRole('heading', { name: 'Editor' })).toBeTruthy();
  });

  it('labels the editor an early preview beside its title, never "beta"', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Early preview' }));
    expect(await screen.findByText('The editor is still being finished. Your work is saved, but some tools may change.')).toBeTruthy();
    expect((document.body.textContent ?? '').toLowerCase()).not.toMatch(/\bbeta\b/);
  });

  it('on a phone the project picker wraps under the title, so it never covers the Early preview label', () => {
    renderPage();
    const label = screen.getByRole('button', { name: 'Early preview' });
    const picker = screen.getByTestId('editor-project');
    const titleArea = label.closest(`.${hub.headerTitle}`) as HTMLElement;
    expect(titleArea).toBeTruthy();
    expect(titleArea.contains(picker)).toBe(false);
    // The header row may wrap; the picker is the item that gives way (it shrinks, then drops a line).
    const row = titleArea.parentElement as HTMLElement;
    expect(row.style.getPropertyValue('--group-wrap')).toBe('wrap');
    expect(picker.closest(`.${hub.picker}`)).toBeTruthy();
  });

  it('hub.module.css: the title area never shrinks below the label, the picker takes its own line on phones', () => {
    const css = readFileSync(path.resolve(__dirname, '../../../components/editor/hub/hub.module.css'), 'utf8');
    const rule = (src: string, sel: string) => src.match(new RegExp(`\\.${sel}\\s*\\{([^}]*)\\}`))?.[1] ?? '';
    expect(rule(css, 'headerTitle')).toMatch(/min-width:\s*auto/);
    expect(rule(css, 'picker')).toMatch(/flex:\s*0 1 280px/);
    const phone = css.slice(css.indexOf('@media (max-width: 599.98px)'));
    expect(rule(phone, 'picker')).toMatch(/flex-basis:\s*100%/);
    expect(rule(phone, 'pickerInput')).toMatch(/min-height:\s*44px/);
  });

  it('points an empty project at a new timeline or a workflow run', () => {
    renderPage();
    expect(screen.getByTestId('timelines-empty')).toBeTruthy();
    expect(screen.getByRole('link', { name: /Run a workflow/ }).getAttribute('href')).toBe('/projects/p1?tab=workflows');
    expect(screen.getAllByTestId('timeline-create')).toHaveLength(1);
  });

  it('lists editable timelines, each opening in the timeline editor', () => {
    editTimelines = [{ id: 't1', name: 'Cut A', revision: 7, seedStepResultId: null, createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z' }];
    renderPage();
    expect(screen.getByRole('link', { name: 'Open Cut A' }).getAttribute('href')).toBe('/editor/p1/timeline/t1');
    expect(screen.getByText('Revision 7')).toBeTruthy();
  });

  it('offers workflow renders not yet opened, named after their run, and opens one as a seeded timeline', async () => {
    renders = { data: [{ stepResultId: 'sr1', workflowExecutionId: 'ex1', workflowStepId: 'st1', fileName: 'acf96fd3.mp4', durationSec: 20, createdAt: '2026-10-04T12:00:00Z' }] };
    outputs = { data: [{ stepResultId: 'sr1', fileName: 'acf96fd3.mp4', workflowName: '20s teaser', runNumber: 3, executionStatus: 'Passed', isFinal: true }] };
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockResolvedValue({ id: 'new-t' });
    renderPage();
    expect(screen.getByText('20s teaser, Run 3')).toBeTruthy();
    expect(screen.getByText('Final')).toBeTruthy();
    fireEvent.click(screen.getByTestId('render-edit-sr1'));
    await waitFor(() => expect(nav.push).toHaveBeenCalledWith('/editor/p1/timeline/new-t'));
    expect(api.createEditTimeline).toHaveBeenCalledWith('p1', { name: '20s teaser, Run 3', seedStepResultId: 'sr1' });
  });

  it('notes items that could not be placed when a run opens as a timeline, and still opens it', async () => {
    renders = { data: [{ stepResultId: 'sr1', workflowExecutionId: 'ex1', workflowStepId: 'st1', fileName: 'a.mp4', createdAt: '2026-10-04T12:00:00Z' }] };
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockResolvedValue({ id: 'new-t', unplacedItems: [{ track: 'A3', itemId: 'm1', label: null, reason: 'asset_unresolved' }] });
    renderPage();
    fireEvent.click(screen.getByTestId('render-edit-sr1'));
    await waitFor(() => expect(nav.push).toHaveBeenCalledWith('/editor/p1/timeline/new-t'));
    expect(notif.show.mock.calls[0][0].message).toContain('1 item from this run could not be placed');
  });

  it('does not offer a render again once a timeline was made from it', () => {
    renders = { data: [{ stepResultId: 'sr1', workflowExecutionId: 'ex1', workflowStepId: 'st1', fileName: 'a.mp4', createdAt: '2026-10-04T12:00:00Z' }] };
    editTimelines = [{ id: 't1', name: 'Teaser', revision: 2, seedStepResultId: 'sr1', createdAt: '2026-10-04T12:00:00Z', updatedAt: '2026-10-04T12:00:00Z' }];
    renderPage();
    expect(screen.queryByTestId('renders-list')).toBeNull();
    expect(screen.getByRole('link', { name: 'Open Teaser' })).toBeTruthy();
  });

  it('keeps the project and the view in the URL', () => {
    renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Pictures' }));
    expect(nav.replace).toHaveBeenCalledWith('/editor?project=p1&view=pictures', { scroll: false });
  });

  it('has exactly two tabs, Videos (the default) and Pictures', () => {
    renderPage();
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Videos', 'Pictures']);
    expect(screen.getByRole('tab', { name: 'Videos' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.queryByRole('tab', { name: 'Timelines' })).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Media' })).toBeNull();
  });

  it.each(['timelines', 'media'])('lands an old view=%s link on Videos and rewrites the URL', (old) => {
    nav.search = new URLSearchParams(`project=p2&view=${old}`);
    renderPage();
    expect(screen.getByRole('tab', { name: 'Videos' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByTestId('timelines-empty')).toBeTruthy();
    expect(nav.replace).toHaveBeenCalledWith('/editor?project=p2&view=videos', { scroll: false });
    expect(nav.push).not.toHaveBeenCalled();
  });

  it('treats an unknown view as Videos without rewriting the URL', () => {
    nav.search = new URLSearchParams('project=p2&view=bogus');
    renderPage();
    expect(screen.getByRole('tab', { name: 'Videos' }).getAttribute('aria-selected')).toBe('true');
    expect(nav.replace).not.toHaveBeenCalled();
  });

  it("lists the project's pictures, opening each in the picture editor", () => {
    nav.search = new URLSearchParams('view=pictures');
    pictures = {
      data: [{ id: 'doc1', name: 'Launch cover', sourceProjectFileId: 'img1', revision: 4, layerCount: 3, createdAt: '', updatedAt: '2026-10-09T10:00:00Z' }],
      isLoading: false,
      error: undefined,
    };
    renderPage();
    expect(screen.getByText('Your pictures')).toBeTruthy();
    expect(screen.getByText(/3 layers/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open Launch cover' }).getAttribute('href')).toBe('/editor/thumbnails/doc1?project=p1');
    expect(screen.queryByTestId('timelines-empty')).toBeNull();
  });

  it('starts a picture from images only, never a layered export', () => {
    nav.search = new URLSearchParams('view=pictures');
    renderPage();
    expect(screen.getByText('No pictures yet')).toBeTruthy();
    fireEvent.click(screen.getByPlaceholderText('Pick an image'));
    expect(screen.getByRole('option', { name: 'cover.jpg' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'layers.psd' })).toBeNull();
    expect(screen.queryByRole('option', { name: 'clip.mp4' })).toBeNull();
  });
});
