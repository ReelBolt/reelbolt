import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ProjectFile } from '@/lib/types/project';
import type { MediaPoolItem } from '@/lib/editing/media/types';

const state: { files: ProjectFile[] | undefined; pool: MediaPoolItem[]; loading: boolean } = { files: [], pool: [], loading: false };

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: state.files, error: undefined, isLoading: state.loading }),
}));
vi.mock('@/lib/hooks/use-media', () => ({
  useMediaPool: () => ({ data: state.pool }),
}));
vi.mock('swr', async (importOriginal) => {
  const real = await importOriginal<typeof import('swr')>();
  // Filmstrip lookups: none exist yet, so a video tile falls back to its icon.
  return { ...real, default: () => ({ data: undefined }) };
});

import { MediaPickerModal } from '../MediaPickerModal';
import { gridNavigate, canConfirm, toggleSelection, selectAllVisible, filterMediaFiles } from '../media-picker-model';

const file = (id: string, name: string, mimeType: string, sizeBytes = 2048): ProjectFile => ({
  id, projectId: 'p1', originalFileName: name, mimeType, sizeBytes, category: 'userFiles', storageKey: id,
  agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-01-01T00:00:00Z',
});

const FILES = [
  file('a', 'beach.png', 'image/png'),
  file('b', 'city.jpg', 'image/jpeg'),
  file('c', 'clip.mp4', 'video/mp4', 5 * 1024 * 1024),
  file('d', 'song.mp3', 'audio/mpeg'),
  file('e', 'dunes.png', 'image/png'),
];

function mount(props: Partial<React.ComponentProps<typeof MediaPickerModal>> = {}) {
  const onConfirm = vi.fn();
  const onClose = vi.fn();
  render(
    <MantineProvider>
      <MediaPickerModal opened onClose={onClose} projectId="p1" kind="both" onConfirm={onConfirm} {...props} />
    </MantineProvider>,
  );
  return { onConfirm, onClose };
}

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({ matches: false, media: query, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false, onchange: null }),
  });
  window.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});

beforeEach(() => {
  state.files = FILES;
  state.pool = [];
  state.loading = false;
});
afterEach(cleanup);

const tile = (name: string) => document.querySelector<HTMLElement>(`[data-tile][aria-label="${name}"]`) as HTMLElement;
const tiles = () => Array.from(document.querySelectorAll<HTMLElement>('[data-tile]'));

describe('MediaPickerModal', () => {
  it('lists only images and videos for kind "both", and only images for "image"', () => {
    mount();
    expect(tiles().map((t) => t.getAttribute('aria-label'))).toEqual(['beach.png', 'city.jpg', 'clip.mp4', 'dunes.png']);
    cleanup();
    mount({ kind: 'image' });
    expect(tiles().map((t) => t.getAttribute('aria-label'))).toEqual(['beach.png', 'city.jpg', 'dunes.png']);
  });

  it('single select has radio semantics and confirms the one file', () => {
    const { onConfirm } = mount();
    expect(screen.getByRole('radiogroup')).toBeTruthy();
    fireEvent.click(tile('beach.png'));
    fireEvent.click(tile('city.jpg'));
    expect(tile('beach.png').getAttribute('aria-checked')).toBe('false');
    expect(tile('city.jpg').getAttribute('aria-checked')).toBe('true');
    expect(screen.queryByRole('button', { name: 'Select all' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect((onConfirm.mock.calls[0][0] as ProjectFile[]).map((f) => f.id)).toEqual(['b']);
  });

  it('multi select has checkbox semantics, counts, select all and clear', () => {
    const { onConfirm } = mount({ multiple: true });
    expect(tiles().every((t) => t.getAttribute('role') === 'checkbox')).toBe(true);
    fireEvent.click(tile('beach.png'));
    expect(screen.getByTestId('media-picker-count').textContent).toBe('1 selected');
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    expect(screen.getByTestId('media-picker-count').textContent).toBe('4 selected');
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(screen.getByTestId('media-picker-count').textContent).toBe('0 selected');
    expect((screen.getByRole('button', { name: 'Confirm' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(tile('city.jpg'));
    fireEvent.click(tile('beach.png'));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect((onConfirm.mock.calls[0][0] as ProjectFile[]).map((f) => f.id)).toEqual(['b', 'a']);
  });

  it('pre-selects value ids', () => {
    mount({ multiple: true, value: ['c', 'e'] });
    expect(tile('clip.mp4').getAttribute('aria-checked')).toBe('true');
    expect(tile('dunes.png').getAttribute('aria-checked')).toBe('true');
    expect(tile('beach.png').getAttribute('aria-checked')).toBe('false');
  });

  it('searches by name and shows an empty state', () => {
    mount();
    fireEvent.change(screen.getByLabelText('Search files'), { target: { value: 'CITY' } });
    expect(tiles().map((t) => t.getAttribute('aria-label'))).toEqual(['city.jpg']);
    fireEvent.change(screen.getByLabelText('Search files'), { target: { value: 'zzz' } });
    expect(screen.getByText('No files match "zzz".')).toBeTruthy();
  });

  it('shows the no-files empty state and the loading skeleton', () => {
    state.files = [file('d', 'song.mp3', 'audio/mpeg')];
    mount();
    expect(screen.getByText('This project has no matching files yet.')).toBeTruthy();
    cleanup();
    state.loading = true;
    mount();
    expect(screen.getAllByTestId('media-picker-skeleton').length).toBeGreaterThan(0);
  });

  it('respects maxSelect and minSelect', () => {
    const { onConfirm } = mount({ multiple: true, maxSelect: 2, minSelect: 2 });
    expect(screen.getByTestId('media-picker-count').textContent).toBe('0 of 2 selected');
    fireEvent.click(tile('beach.png'));
    expect((screen.getByRole('button', { name: 'Confirm' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(tile('city.jpg'));
    fireEvent.click(tile('dunes.png'));
    expect(tile('dunes.png').getAttribute('aria-checked')).toBe('false');
    expect(screen.getByText('You can choose up to 2.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect((onConfirm.mock.calls[0][0] as ProjectFile[]).length).toBe(2);
  });

  it('moves focus with the arrow keys and toggles with a click on the focused tile', () => {
    mount({ multiple: true });
    const all = tiles();
    all[0].focus();
    fireEvent.keyDown(all[0], { key: 'ArrowRight' });
    expect(document.activeElement).toBe(all[1]);
    fireEvent.keyDown(all[1], { key: 'End' });
    expect(document.activeElement).toBe(all[3]);
    fireEvent.keyDown(all[3], { key: 'Home' });
    expect(document.activeElement).toBe(all[0]);
    expect(all.filter((t) => t.tabIndex === 0)).toHaveLength(1);
    expect(all[0].tagName).toBe('BUTTON');
  });

  it('opens a preview: image at ?w=1280, video with controls, never an original URL for thumbnails', async () => {
    mount();
    for (const img of Array.from(document.querySelectorAll('img'))) {
      expect(img.getAttribute('src')).toMatch(/\?w=320$/);
      expect(img.getAttribute('loading')).toBe('lazy');
    }
    expect(document.querySelectorAll('video').length).toBe(0);
    fireEvent.click(screen.getByRole('button', { name: 'Preview beach.png' }));
    const large = within(await screen.findByRole('dialog', { name: /Preview: beach.png/ })).getByRole('img') as HTMLImageElement;
    expect(large.getAttribute('src')).toBe('/api/v1/projects/p1/files/a/media?w=1280');
    cleanup();
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Preview clip.mp4' }));
    const video = await screen.findByTestId('media-picker-preview-video');
    expect(video.getAttribute('src')).toBe('/api/v1/projects/p1/files/c/media');
    expect(video.hasAttribute('controls')).toBe(true);
  });

  it('shows a video icon (no video element) when there is no derivative, and the duration from the pool', () => {
    state.pool = [{ mediaRef: 'file:c', kind: 'video', fileName: 'clip.mp4', mimeType: 'video/mp4', sizeBytes: 1, category: 'userFiles', probe: { durationSec: 62.5 } as MediaPoolItem['probe'], bins: [], filmstripUrl: '' }];
    mount();
    expect(screen.getAllByTestId('media-picker-placeholder').length).toBe(1);
    expect(document.querySelectorAll('video').length).toBe(0);
    expect(screen.getByText(/5 MB · 1:02/)).toBeTruthy();
  });

  it('Cancel closes without confirming', () => {
    const { onClose, onConfirm } = mount();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(onConfirm).not.toHaveBeenCalled();
  });
});

describe('media-picker-model', () => {
  const single = { multiple: false };
  it('toggleSelection and selectAllVisible honour the cap', () => {
    expect(toggleSelection(['a'], 'b', single)).toEqual(['b']);
    expect(toggleSelection(['a'], 'a', single)).toEqual([]);
    expect(toggleSelection(['a', 'b'], 'c', { multiple: true, maxSelect: 2 })).toEqual(['a', 'b']);
    expect(selectAllVisible(['a'], ['a', 'b', 'c', 'd'], { multiple: true, maxSelect: 3 })).toEqual(['a', 'b', 'c']);
  });
  it('canConfirm needs the minimum and stays under the cap', () => {
    expect(canConfirm(0, single)).toBe(false);
    expect(canConfirm(1, single)).toBe(true);
    expect(canConfirm(0, { multiple: true, minSelect: 0 })).toBe(true);
    expect(canConfirm(3, { multiple: true, maxSelect: 2 })).toBe(false);
  });
  it('gridNavigate moves in a grid and stops at the edges', () => {
    expect(gridNavigate(0, 'ArrowDown', 5, 2)).toBe(2);
    expect(gridNavigate(4, 'ArrowDown', 5, 2)).toBe(4);
    expect(gridNavigate(2, 'ArrowUp', 5, 2)).toBe(0);
    expect(gridNavigate(0, 'ArrowLeft', 5, 2)).toBe(0);
    expect(gridNavigate(0, 'End', 5, 2)).toBe(4);
  });
  it('filterMediaFiles applies kind, extra rule and query', () => {
    expect(filterMediaFiles(FILES, 'audio', '').map((f) => f.id)).toEqual(['d']);
    expect(filterMediaFiles(FILES, 'image', '', (f) => f.id !== 'a').map((f) => f.id)).toEqual(['b', 'e']);
  });
});
