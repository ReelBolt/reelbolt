// @vitest-environment jsdom
/**
 * P9 UI: the keymap editor captures a key, flags a conflict and saves overrides; the preset picker
 * applies a built-in mastering target and saves the current value; the review panel posts a comment
 * at the playhead and resolves a thread; the secondary fields add an HSL qualifier and a window to a
 * node and write the tracked window back; the offline banner offers the download/discard escape.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { GradeNode } from '@/lib/editing/types';

const api = vi.hoisted(() => ({
  createEditorPreset: vi.fn(async () => ({})),
  deleteEditorPreset: vi.fn(async () => undefined),
  createReviewComment: vi.fn(async () => ({})),
  updateReviewComment: vi.fn(async () => ({})),
  deleteReviewComment: vi.fn(async () => undefined),
}));
const hooks = vi.hoisted(() => ({
  presets: [] as unknown[],
  comments: [] as unknown[],
  mutate: vi.fn(async () => undefined),
}));

vi.mock('@/lib/editing/api/editor-p9', async (importOriginal) => ({ ...(await importOriginal<object>()), ...api }));
vi.mock('@/lib/hooks/use-editor-p9', () => ({
  useEditorPresets: () => ({ data: hooks.presets, mutate: hooks.mutate }),
  useReviewComments: () => ({ data: hooks.comments, mutate: hooks.mutate, isLoading: false, error: undefined }),
  useEditorPreferences: () => ({ data: undefined, mutate: hooks.mutate }),
}));

import { KeymapEditor } from '../prefs/KeymapEditor';
import { PresetPicker } from '../presets/PresetPicker';
import { ReviewPanel } from '../review/ReviewPanel';
import { SecondaryFields } from '../color/SecondaryFields';
import { ReadOnlyBanner } from '../sync/ReadOnlyBanner';
import { MASTERING_PRESETS } from '@/lib/editing/presets';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('KeymapEditor', () => {
  it('captures a new chord, refuses to save a conflict, and saves once resolved', async () => {
    const onSave = vi.fn();
    wrap(<KeymapEditor opened onClose={() => {}} overrides={{}} onSave={onSave} />);
    await waitFor(() => expect(screen.getByTestId('keymap-row-toolBlade')).toBeTruthy());
    fireEvent.click(screen.getByTestId('keymap-change-toolBlade'));
    fireEvent.keyDown(window, { code: 'KeyM', key: 'm' });
    await waitFor(() => expect(screen.getByTestId('keymap-conflicts').textContent).toMatch(/M is bound to Add marker and Blade tool/));
    expect((screen.getByTestId('keymap-save') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('keymap-change-toolBlade'));
    fireEvent.keyDown(window, { code: 'KeyC', key: 'c' });
    await waitFor(() => expect(screen.queryByTestId('keymap-conflicts')).toBeNull());
    fireEvent.click(screen.getByTestId('keymap-save'));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith({ toolBlade: 'C' }));
  });
});

describe('PresetPicker', () => {
  it('applies a built-in mastering target and saves the current value as a project preset', async () => {
    const onApply = vi.fn();
    wrap(<PresetPicker projectId="p" kind="mastering" builtIns={MASTERING_PRESETS} current={{ targetLufs: -18, truePeakDbtp: -2 }} readOnly={false} onApply={onApply} testId="mp" />);
    fireEvent.click(screen.getByTestId('mp-select'));
    fireEvent.click(await screen.findByText('Podcast (−16 LUFS)'));
    expect(onApply).toHaveBeenCalledWith({ targetLufs: -16, truePeakDbtp: -1, loudnessRange: 11 }, 'Podcast (−16 LUFS)');
    fireEvent.change(screen.getByTestId('mp-name'), { target: { value: 'Our level' } });
    fireEvent.click(screen.getByTestId('mp-save'));
    await waitFor(() => expect(api.createEditorPreset).toHaveBeenCalledWith('p', 'mastering', 'Our level', { targetLufs: -18, truePeakDbtp: -2 }));
  });
});

describe('ReviewPanel', () => {
  it('posts a comment at the playhead (or over the marked range) and resolves a thread', async () => {
    hooks.comments = [{ id: 'r1', timelineId: 't', parentId: null, authorUserId: 'u', authorName: 'ana@example.com', frame: 45, durationFrames: null, body: 'Tighten this', resolved: false, createdAt: '1', updatedAt: '1' }];
    const onSeek = vi.fn();
    wrap(<ReviewPanel projectId="p" timelineId="t" rate={{ num: 30, den: 1 }} playheadFrame={90} inOut={{ inFrame: 30, outFrame: 60 }} onSeek={onSeek} />);
    fireEvent.change(screen.getByTestId('review-body'), { target: { value: 'Colour jumps here' } });
    fireEvent.click(screen.getByTestId('review-add'));
    await waitFor(() => expect(api.createReviewComment).toHaveBeenCalledWith('p', 't', { body: 'Colour jumps here', frame: 90, durationFrames: null }));
    fireEvent.click(screen.getByTestId('review-seek-r1'));
    expect(onSeek).toHaveBeenCalledWith(45);
    fireEvent.click(screen.getByTestId('review-resolve-r1'));
    await waitFor(() => expect(api.updateReviewComment).toHaveBeenCalledWith('p', 't', 'r1', { resolved: true }));
  });
});

describe('SecondaryFields', () => {
  it('adds a qualifier and a window, and writes the tracked window back', async () => {
    const writes: GradeNode[] = [];
    const node: GradeNode = { id: 'n1', primaries: { saturation: 1.3 } };
    const track = vi.fn(async (n: GradeNode) => ({ ...n.window!, offsetX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' as const }, { frame: 20, value: 0.1, interp: 'linear' as const }] } }));
    const view = wrap(<SecondaryFields node={node} readOnly={false} onChange={(n) => writes.push(n)} onTrackWindow={track} />);
    fireEvent.click(screen.getByTestId('grade-qualifier-toggle'));
    expect(writes[0].qualifier).toMatchObject({ hueCenter: 25, hueWidth: 25 });
    view.rerender(<MantineProvider><SecondaryFields node={writes[0]} readOnly={false} onChange={(n) => writes.push(n)} onTrackWindow={track} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('grade-window-toggle'));
    expect(writes[1].window?.shape.kind).toBe('ellipse');
    view.rerender(<MantineProvider><SecondaryFields node={writes[1]} readOnly={false} onChange={(n) => writes.push(n)} onTrackWindow={track} /></MantineProvider>);
    await act(async () => {
      fireEvent.click(screen.getByTestId('grade-window-track'));
    });
    await waitFor(() => expect(writes[2]?.window?.offsetX?.keys).toHaveLength(2));
    expect(writes[2].qualifier).toEqual(writes[0].qualifier);
  });
});

describe('ReadOnlyBanner (offline)', () => {
  it('shows the queue state while offline and the download/discard escape after a refused rebase', () => {
    const base = { revision: 3, lastSavedAt: null, conflictRevision: 5, violations: [], attempt: 0 };
    const view = wrap(<ReadOnlyBanner save={{ ...base, status: 'offline', message: 'You are offline.', queuedOps: 2 }} onReload={() => {}} />);
    expect(screen.getByTestId('offline-banner').textContent).toMatch(/offline/i);
    const download = vi.fn();
    const discard = vi.fn();
    view.rerender(<MantineProvider><ReadOnlyBanner save={{ ...base, status: 'conflict', message: 'Your offline edits were not applied: they touch the same track.', offlineRefused: true }} onReload={() => {}} onDownloadQueued={download} onDiscardQueued={discard} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('offline-download'));
    fireEvent.click(screen.getByTestId('offline-discard'));
    expect(download).toHaveBeenCalled();
    expect(discard).toHaveBeenCalled();
  });
});
