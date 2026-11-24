// @vitest-environment jsdom
/**
 * The Simple shell: the same workspace, laid out for a first video. One store, one document; three places; one
 * panel that follows the selection; the full editor one click away. See docs/design/editor-simple-mode.md.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { EditorWorkspace } from '../EditorWorkspace';
import { createEditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { SaveState, SyncClient } from '@/lib/editing/sync';
import { SHELL_INTRO_STORAGE_KEY, SHELL_STORAGE_KEY } from '@/lib/editing/shell-choice';
import { PREFERENCES_WAIT_MS } from '../workspace/useShellChoice';

vi.mock('@/lib/hooks/use-media', async (importOriginal) => ({ ...(await importOriginal<object>()), useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-media-derivatives', async (importOriginal) => ({ ...(await importOriginal<object>()), useRequestTimelineDerivatives: () => undefined }));
vi.mock('../timeline/TimelineCanvas', () => ({ TimelineCanvas: () => null }));
vi.mock('../viewer', async (importOriginal) => ({ ...(await importOriginal<object>()), ProgramViewer: () => null }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
globalThis.fetch = vi.fn(() => new Promise<Response>(() => {})) as unknown as typeof fetch;
// A desktop: jsdom's default 1024 px is a tablet by the editor's rule (600-1100 px), and a tablet has no choice to wait for.
Object.defineProperty(window, 'innerWidth', { value: 1440, configurable: true });

beforeEach(() => {
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const saved: SaveState = { status: 'saved', revision: 1, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };
const fakeSync = () => ({ getState: () => saved, subscribe: () => () => {}, attachOps: vi.fn(), flush: vi.fn(), getQueuedEntry: () => null, discardQueue: vi.fn(), retry: vi.fn(), requestSignIn: vi.fn() }) as unknown as SyncClient;

function mount(options: { clips?: boolean } = {}) {
  const tracks = options.clips === false ? [track(TRACKS.v1, 'video', [])] : [track(TRACKS.v1, 'video', [media('c1', 0, 60, 0), media('c2', 60, 60, 0)])];
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc(tracks), revision: 1 });
  store.setUi({ workspace: 'edit', playheadFrame: 30 });
  render(
    <MantineProvider>
      <EditorWorkspace store={store} sync={fakeSync()} onReload={() => {}} />
    </MantineProvider>,
  );
  return store;
}
const shellOf = () => screen.getByTestId('editor-workspace').getAttribute('data-shell');

describe('which shell opens', () => {
  it('a stored choice wins at once, with no wait', () => {
    window.localStorage.setItem(SHELL_STORAGE_KEY, 'simple');
    mount();
    expect(shellOf()).toBe('simple');
    cleanup();
    window.localStorage.setItem(SHELL_STORAGE_KEY, 'full');
    mount();
    expect(shellOf()).toBe('full');
  });

  it('a tablet starts Simple at once', () => {
    Object.defineProperty(window, 'innerWidth', { value: 900, configurable: true });
    try {
      mount();
      expect(shellOf()).toBe('simple');
      expect(screen.getByTestId('editor-workspace').getAttribute('data-layout')).toBe('tablet');
    } finally {
      Object.defineProperty(window, 'innerWidth', { value: 1440, configurable: true });
    }
  });

  it('with no stored choice a desktop waits for the saved preferences, then starts Simple when they never answer', () => {
    vi.useFakeTimers();
    mount();
    expect(shellOf()).toBe('pending');
    act(() => {
      vi.advanceTimersByTime(PREFERENCES_WAIT_MS + 10);
    });
    expect(shellOf()).toBe('simple');
    // A default is not a choice: nothing is stored until the user picks.
    expect(window.localStorage.getItem(SHELL_STORAGE_KEY)).toBeNull();
  });
});

describe('the Simple shell', () => {
  beforeEach(() => {
    window.localStorage.setItem(SHELL_STORAGE_KEY, 'simple');
    window.localStorage.setItem(SHELL_INTRO_STORAGE_KEY, '1');
  });

  it('shows three places, the task list and the ask box, and none of the seven pages', () => {
    mount();
    expect(screen.getByTestId('simple-place-edit').getAttribute('aria-current')).toBe('page');
    expect(screen.getByTestId('simple-place-look').getAttribute('aria-current')).toBeNull();
    expect(screen.getByTestId('simple-place-export').getAttribute('data-active')).toBe('false');
    expect(screen.getByTestId('simple-tasks')).toBeTruthy();
    expect(screen.getByTestId('simple-ask')).toBeTruthy();
    expect(screen.queryByTestId('workspace-audio')).toBeNull();
    expect(screen.queryByTestId('page-band')).toBeNull();
    // The timeline keeps its short toolbar: no trim modes, no close-gaps, no select menu.
    const toolbar = screen.getByTestId('timeline-toolbar');
    expect(toolbar.getAttribute('data-simple')).toBe('true');
    expect(within(toolbar).getByTestId('blade-at-playhead')).toBeTruthy();
    expect(within(toolbar).queryByTestId('close-all-gaps')).toBeNull();
    expect(screen.getByTestId('early-preview-label')).toBeTruthy();
  });

  it('tasks that need a clip are off on an empty timeline; the rest are on', () => {
    mount({ clips: false });
    expect((screen.getByTestId('simple-task-silence') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('simple-task-format') as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByTestId('simple-task-title') as HTMLButtonElement).disabled).toBe(false);
  });

  it('the panel follows the selection, and comes back to the tasks when nothing is selected', () => {
    const store = mount();
    act(() => store.setUi({ selection: ['c1'] }));
    expect(screen.getByTestId('simple-selection').getAttribute('data-kind')).toBe('clip');
    expect(screen.getByTestId('clip-sheet')).toBeTruthy();
    expect(screen.queryByTestId('simple-tasks')).toBeNull();
    act(() => store.setUi({ selection: ['c1', 'c2'] }));
    expect(screen.getByTestId('simple-selection').getAttribute('data-kind')).toBe('many');
    act(() => store.setUi({ selection: [] }));
    expect(screen.getByTestId('simple-tasks')).toBeTruthy();
  });

  it('a task opens its own panel with a way back; a place change returns to the task list', () => {
    mount();
    fireEvent.click(screen.getByTestId('simple-task-format'));
    expect(screen.getByTestId('simple-panel-back')).toBeTruthy();
    expect(screen.queryByTestId('simple-tasks')).toBeNull();
    fireEvent.click(screen.getByTestId('simple-panel-back'));
    expect(screen.getByTestId('simple-tasks')).toBeTruthy();
    fireEvent.click(screen.getByTestId('simple-task-title'));
    expect(screen.getByTestId('simple-title-panel')).toBeTruthy();
    fireEvent.click(screen.getByTestId('simple-place-look'));
    expect(screen.getByTestId('look-sheet')).toBeTruthy();
    // The Look place has no "on desktop" note: the full editor is one click away.
    expect(screen.queryByTestId('look-desktop-note')).toBeNull();
    fireEvent.click(screen.getByTestId('simple-place-edit'));
    expect(screen.getByTestId('simple-tasks')).toBeTruthy();
  });

  it('Export is a place: the header button opens it, with the one-column export and the way to the rest', () => {
    mount();
    fireEvent.click(screen.getByTestId('open-deliver'));
    expect(screen.getByTestId('editor-workspace').getAttribute('data-place')).toBe('export');
    expect(screen.getByTestId('deliver-workspace').getAttribute('data-simple')).toBe('true');
    expect(screen.getByTestId('simple-more-export')).toBeTruthy();
    expect(screen.queryByTestId('deliver-handoff-section')).toBeNull();
    fireEvent.click(screen.getByTestId('simple-more-export'));
    expect(shellOf()).toBe('full');
    expect(screen.getByTestId('editor-workspace').getAttribute('data-workspace')).toBe('deliver');
  });

  it('the ask box opens the suggestions panel with the helper and the words filled in, and asks nothing by itself', () => {
    mount();
    fireEvent.change(screen.getByTestId('simple-ask-text'), { target: { value: 'cut the pauses' } });
    fireEvent.click(screen.getByTestId('simple-ask-go'));
    expect((screen.getByTestId('proposal-brief') as HTMLTextAreaElement).value).toBe('cut the pauses');
    // Only the estimate and the availability reads may have been made: no proposal request.
    const calls = (globalThis.fetch as unknown as { mock: { calls: unknown[][] } }).mock.calls.map((c) => String(c[0]));
    expect(calls.some((url) => /\/proposals$/.test(url))).toBe(false);
  });

  it('the switch goes to the full editor and back, keeping the selection and the playhead, and stores the choice', () => {
    const store = mount();
    act(() => store.setUi({ selection: ['c2'], playheadFrame: 77 }));
    fireEvent.click(within(screen.getByTestId('shell-switch')).getByText('Full'));
    expect(shellOf()).toBe('full');
    expect(window.localStorage.getItem(SHELL_STORAGE_KEY)).toBe('full');
    expect(screen.getByTestId('page-band')).toBeTruthy();
    expect(store.getState().ui.selection).toEqual(['c2']);
    expect(store.getState().ui.playheadFrame).toBe(77);
    fireEvent.click(within(screen.getByTestId('shell-switch')).getByText('Simple'));
    expect(shellOf()).toBe('simple');
    expect(window.localStorage.getItem(SHELL_STORAGE_KEY)).toBe('simple');
    expect(store.getState().ui.selection).toEqual(['c2']);
  });

  it('a page the Simple shell does not have turns into its place: Deliver is Export, and the page underneath is Edit', () => {
    const store = mount();
    act(() => store.setUi({ workspace: 'deliver' }));
    expect(screen.getByTestId('editor-workspace').getAttribute('data-place')).toBe('export');
    expect(store.getState().ui.workspace).toBe('edit');
  });

  it('the first visit says what this is, once', () => {
    window.localStorage.removeItem(SHELL_INTRO_STORAGE_KEY);
    mount();
    expect(screen.getByTestId('simple-intro')).toBeTruthy();
    fireEvent.click(screen.getByTestId('simple-intro-dismiss'));
    expect(screen.queryByTestId('simple-intro')).toBeNull();
    expect(window.localStorage.getItem(SHELL_INTRO_STORAGE_KEY)).toBe('1');
  });

  it('stays small: the controls shown at rest are under the ceiling (the full editor shows several times as many)', () => {
    mount();
    const controls = (root: HTMLElement) => root.querySelectorAll('button, a[href], input, select, textarea, [role="tab"], [role="slider"], [role="switch"]').length;
    const simple = controls(screen.getByTestId('editor-workspace'));
    expect(simple).toBeLessThanOrEqual(SIMPLE_CONTROL_CEILING);
    fireEvent.click(within(screen.getByTestId('shell-switch')).getByText('Full'));
    const full = controls(screen.getByTestId('editor-workspace'));
    expect(full).toBeGreaterThan(simple);
  });
});

/**
 * How many interactive controls the Simple shell may show at rest (Edit place, nothing selected, media list empty).
 * Raising this number is a decision, not a fix: the shell exists to be small.
 */
const SIMPLE_CONTROL_CEILING = 45;
