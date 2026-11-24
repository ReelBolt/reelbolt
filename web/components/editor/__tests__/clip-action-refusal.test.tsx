// @vitest-environment jsdom
/**
 * Security MT-1 (PR #321): every action in the clip action menu (Delete, Lift, Split, Nudge) that the
 * engine refuses shows the one plain-sentence "Edit refused" toast (refusalSentence): no track or clip
 * ids, no op names. Rendered through the real EditorWorkspace and store; the selected clip sits on a
 * locked track, so the engine refuses every one of them.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { EditorWorkspace } from '../EditorWorkspace';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { SaveState, SyncClient } from '@/lib/editing/sync';

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

const show = vi.spyOn(notifications, 'show').mockImplementation(() => '');
beforeEach(() => show.mockClear());
// These tests are about the full editor: say so, or a desktop with no stored choice waits for the saved preferences.
window.localStorage.setItem('reelbolt:editor:shell', 'full');
afterEach(cleanup);

const saved: SaveState = { status: 'saved', revision: 1, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };
const fakeSync = () =>
  ({ getState: () => saved, subscribe: () => () => {}, attachOps: vi.fn(), flush: vi.fn(), getQueuedEntry: () => null, discardQueue: vi.fn() }) as unknown as SyncClient;

/** V1 (locked, named "Picture") holds clip c1 [0-60), selected, playhead inside it. */
function mount(): EditorStore {
  const store = createEditorStore({
    timelineId: 't',
    projectId: 'p',
    doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0, { name: 'Opening shot' })], { name: 'Picture', locked: true })]),
    revision: 1,
  });
  store.setUi({ workspace: 'edit', selection: ['c1'], playheadFrame: 30 });
  render(
    <MantineProvider>
      <EditorWorkspace store={store} sync={fakeSync()} onReload={() => {}} />
    </MantineProvider>,
  );
  return store;
}

const refusals = () => show.mock.calls.map(([n]) => n).filter((n) => n.title === 'Edit refused');

describe('Clip action menu refusals (security MT-1)', () => {
  it.each([
    ['Delete', 'clip-action-delete'],
    ['Lift', 'clip-action-lift'],
    ['Split', 'clip-action-split'],
    ['Nudge', 'clip-action-nudge-forward'],
  ])('%s on a locked track shows a plain sentence, no ids or op names', (_label, testId) => {
    const store = mount();
    const before = store.getState().doc;
    fireEvent.click(screen.getByTestId('clip-actions-open'));
    fireEvent.click(screen.getByTestId(testId));

    const shown = refusals();
    expect(shown).toHaveLength(1);
    const message = String(shown[0].message);
    expect(message).toBe('The track “Picture” is locked. Unlock it to change its clips.');
    expect(message).not.toMatch(/track-v1|'c1'|\b(extract|lift|blade|nudge):/);
    expect(store.getState().doc).toBe(before);
  });
});
