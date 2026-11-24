// @vitest-environment jsdom
/**
 * The hub row menu's Rename and Delete dialogs: Enter (or a double click) while the request is in flight must not send
 * it a second time.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { Notifications, notifications } from '@mantine/notifications';

const api = vi.hoisted(() => ({
  list: vi.fn(),
  rename: vi.fn(),
  remove: vi.fn(),
  trash: vi.fn(),
  restore: vi.fn(),
  create: vi.fn(),
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }) }));
const renderRows = vi.hoisted(() => ({ data: [] as Record<string, unknown>[] }));
vi.mock('@/lib/hooks/use-timelines', () => ({ useProjectTimelines: () => ({ data: renderRows.data }) }));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => ({ data: [] }) }));
vi.mock('@/lib/editing/sync', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync')>()),
  listEditTimelines: (...args: unknown[]) => api.list(...args),
  renameEditTimeline: (...args: unknown[]) => api.rename(...args),
  deleteEditTimeline: (...args: unknown[]) => api.remove(...args),
  listDeletedTimelines: (...args: unknown[]) => api.trash(...args),
  restoreTimeline: (...args: unknown[]) => api.restore(...args),
  createEditTimeline: (...args: unknown[]) => api.create(...args),
}));

import { TimelinesView } from '../TimelinesView';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const ROW = { id: 'tl-1', name: 'Cut one', revision: 3, updatedAt: '2026-10-10T10:00:00Z', seedStepResultId: null };

beforeEach(() => {
  api.list.mockReset().mockResolvedValue([ROW]);
  api.rename.mockReset();
  api.remove.mockReset();
  api.trash.mockReset().mockResolvedValue([]);
  api.restore.mockReset();
  api.create.mockReset();
  renderRows.data = [];
});
afterEach(() => {
  notifications.clean();
  cleanup();
});

const mount = () =>
  render(
    <MantineProvider>
      <Notifications />
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <TimelinesView projectId="p" />
      </SWRConfig>
    </MantineProvider>,
  );

describe('TimelinesView rename and delete', () => {
  it('sends a rename once even when Enter is pressed twice while it is in flight', async () => {
    let finish: (v?: unknown) => void = () => {};
    api.rename.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    mount();
    fireEvent.click(await screen.findByTestId('timeline-more-tl-1'));
    fireEvent.click(await screen.findByTestId('timeline-rename-tl-1'));
    const input = await screen.findByTestId('timeline-rename-input');
    fireEvent.change(input, { target: { value: 'Renamed' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    fireEvent.keyDown(input, { key: 'Enter' });
    fireEvent.click(screen.getByTestId('timeline-rename-confirm'));
    expect(api.rename).toHaveBeenCalledTimes(1);
    expect(api.rename).toHaveBeenCalledWith('p', 'tl-1', 'Renamed');
    finish();
    await waitFor(() => expect(screen.queryByTestId('timeline-rename-input')).toBeNull());
    expect(api.rename).toHaveBeenCalledTimes(1);
  });

  it('sends a delete once when Delete is clicked twice while it is in flight', async () => {
    let finish: (v?: unknown) => void = () => {};
    api.remove.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    mount();
    fireEvent.click(await screen.findByTestId('timeline-more-tl-1'));
    fireEvent.click(await screen.findByTestId('timeline-delete-tl-1'));
    const confirm = await screen.findByTestId('timeline-delete-confirm');
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    expect(api.remove).toHaveBeenCalledTimes(1);
    finish();
    await waitFor(() => expect(screen.queryByTestId('timeline-delete-confirm')).toBeNull());
  });
});

const ROWS = [
  { id: 'tl-a', name: 'Beach trip', revision: 1, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-03-01T00:00:00Z', seedStepResultId: null },
  { id: 'tl-b', name: 'Alpha cut', revision: 1, createdAt: '2026-02-01T00:00:00Z', updatedAt: '2026-01-15T00:00:00Z', seedStepResultId: null },
];
const rowOrder = () => screen.getAllByRole('listitem').map((el) => el.getAttribute('data-testid'));

describe('R01.1 search and sort', () => {
  beforeEach(() => window.localStorage.clear());

  it('filters by name as you type and remembers the chosen order', async () => {
    api.list.mockResolvedValue(ROWS);
    mount();
    await screen.findByTestId('timeline-row-tl-a');
    expect(rowOrder()).toEqual(['timeline-row-tl-a', 'timeline-row-tl-b']);
    fireEvent.change(screen.getByTestId('timeline-search'), { target: { value: 'ALPHA' } });
    expect(rowOrder()).toEqual(['timeline-row-tl-b']);
    fireEvent.change(screen.getByTestId('timeline-search'), { target: { value: 'zzz' } });
    expect(screen.getByTestId('timelines-no-match').textContent).toContain('zzz');
  });

  it('starts in the remembered order', async () => {
    window.localStorage.setItem('reelbolt:editor:hub:timeline-sort', 'name');
    api.list.mockResolvedValue(ROWS);
    mount();
    await screen.findByTestId('timeline-row-tl-a');
    expect(rowOrder()).toEqual(['timeline-row-tl-b', 'timeline-row-tl-a']);
  });
});

describe('R01.1 delete, undo and recently deleted', () => {
  it('shows an Undo toast after delete and Undo restores the timeline', async () => {
    api.list.mockResolvedValue(ROWS);
    api.remove.mockResolvedValue(undefined);
    api.restore.mockResolvedValue(ROWS[0]);
    mount();
    fireEvent.click(await screen.findByTestId('timeline-more-tl-a'));
    fireEvent.click(await screen.findByTestId('timeline-delete-tl-a'));
    expect((await screen.findByTestId('timeline-delete-text')).textContent).toContain('30 days');
    fireEvent.click(screen.getByTestId('timeline-delete-confirm'));
    const undo = await screen.findByTestId('timeline-undo-delete');
    expect(screen.getAllByTestId('timeline-undo-toast')[0].textContent).toContain('Deleted “Beach trip”');
    fireEvent.click(undo);
    await waitFor(() => expect(api.restore).toHaveBeenCalledWith('p', 'tl-a'));
  });

  it('lists deleted timelines with days left and restores one', async () => {
    api.list.mockResolvedValue(ROWS);
    const deletedAt = new Date(Date.now() - 3 * 86_400_000).toISOString();
    const purgesAt = new Date(Date.now() + 27 * 86_400_000).toISOString();
    api.trash.mockResolvedValue([{ ...ROW, id: 'tl-gone', name: 'Old one', deletedAt, purgesAt }]);
    api.restore.mockResolvedValue(ROW);
    mount();
    fireEvent.click(await screen.findByTestId('timelines-trash-toggle'));
    expect((await screen.findByTestId('timeline-trash-row-tl-gone')).textContent).toContain('Deleted 3 days ago - removed for good in 27 days');
    fireEvent.click(screen.getByTestId('timeline-restore-tl-gone'));
    await waitFor(() => expect(api.restore).toHaveBeenCalledWith('p', 'tl-gone'));
  });

  it('shows no Recently deleted section when the trash is empty', async () => {
    api.list.mockResolvedValue(ROWS);
    mount();
    await screen.findByTestId('timeline-row-tl-a');
    expect(screen.queryByTestId('timelines-trash')).toBeNull();
  });
});

describe('R01.3 new timeline format', () => {
  it('starts with Untitled video selected and creates with the chosen format and frame rate', async () => {
    api.list.mockResolvedValue(ROWS);
    api.create.mockResolvedValue({ id: 'new', unplacedItems: [] });
    mount();
    fireEvent.click((await screen.findAllByTestId('timeline-create'))[0]);
    const name = (await screen.findByTestId('timeline-name')) as HTMLInputElement;
    expect(name.value).toBe('Untitled video');
    fireEvent.click(screen.getByTestId('timeline-format'));
    fireEvent.click(await screen.findByText('Vertical 9:16'));
    fireEvent.click(screen.getByTestId('timeline-fps'));
    fireEvent.click(await screen.findByText('24 frames per second'));
    fireEvent.click(screen.getByTestId('timeline-create-confirm'));
    await waitFor(() => expect(api.create).toHaveBeenCalled());
    expect(api.create.mock.calls[0][1]).toMatchObject({
      name: 'Untitled video',
      settings: { width: 1080, height: 1920, rate: { num: 24, den: 1 }, pixelAspect: { num: 1, den: 1 }, startTimecodeFrames: 0 },
    });
  });
});

describe('TimelinesView workflow renders', () => {
  const base = { workflowStepId: 's', timelineStorageKey: 'k', fileName: 'f.mp4' };
  it('shows one row per run, labelled by workflow and run, until intermediates are switched on', async () => {
    renderRows.data = [
      { ...base, stepResultId: 'r2', workflowExecutionId: 'A', createdAt: '2026-01-02T00:00:00Z', isFinal: true, workflowName: 'Promo', runNumber: 3, stepLabel: 'Render' },
      { ...base, stepResultId: 'r1', workflowExecutionId: 'A', createdAt: '2026-01-01T00:00:00Z', isFinal: false, workflowName: 'Promo', runNumber: 3, stepLabel: 'Draft' },
      // A run whose final render has no timeline has no isFinal row: its latest row still shows.
      { ...base, stepResultId: 'x2', workflowExecutionId: 'B', createdAt: '2026-01-04T00:00:00Z', isFinal: false, workflowName: 'Teaser', runNumber: 1, stepLabel: 'Cut' },
      { ...base, stepResultId: 'x1', workflowExecutionId: 'B', createdAt: '2026-01-03T00:00:00Z', isFinal: false, workflowName: 'Teaser', runNumber: 1, stepLabel: 'Rough' },
    ];
    mount();
    await screen.findByTestId('render-edit-r2');
    expect(screen.queryByTestId('render-edit-r1')).toBeNull();
    expect(screen.getByTestId('render-edit-x2')).toBeTruthy();
    expect(screen.queryByTestId('render-edit-x1')).toBeNull();
    expect(screen.getByTestId('renders-list').textContent).toContain('Promo · run 3');
    fireEvent.click(screen.getByTestId('renders-intermediate'));
    expect(await screen.findByTestId('render-edit-r1')).toBeTruthy();
    expect(screen.getByTestId('render-edit-x1')).toBeTruthy();
  });
  it('limits the new-timeline select to final rows too', async () => {
    renderRows.data = [
      { ...base, stepResultId: 'r2', workflowExecutionId: 'A', createdAt: '2026-01-02T00:00:00Z', isFinal: true, workflowName: 'Promo', runNumber: 3, stepLabel: 'Render' },
      { ...base, stepResultId: 'r1', workflowExecutionId: 'A', createdAt: '2026-01-01T00:00:00Z', isFinal: false, workflowName: 'Promo', runNumber: 3, stepLabel: 'Draft' },
    ];
    mount();
    await screen.findByTestId('render-edit-r2');
    fireEvent.click(screen.getByRole('button', { name: /new timeline/i }));
    fireEvent.click(await screen.findByTestId('timeline-seed'));
    await screen.findByRole('option', { name: 'Promo · run 3', hidden: true });
    expect(screen.queryByRole('option', { name: 'Promo · run 3 · Draft', hidden: true })).toBeNull();
    fireEvent.click(screen.getByTestId('timeline-seed-intermediate'));
    expect(await screen.findByRole('option', { name: 'Promo · run 3 · Draft', hidden: true })).toBeTruthy();
  });
});
