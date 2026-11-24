// @vitest-environment jsdom
/**
 * T-002: page identity. The document title is "<page> · <film>", the page name is the h1, and a page
 * switch moves focus to it — never on the first mount, and never behind an open dialog.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { EditorWorkspace } from '../EditorWorkspace';
import { createEditorStore } from '@/lib/editing/store';
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
// These tests are about the full editor: say so, or a desktop with no stored choice waits for the saved preferences.
window.localStorage.setItem('reelbolt:editor:shell', 'full');
afterEach(cleanup);

const saved: SaveState = { status: 'saved', revision: 1, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };
const fakeSync = () => ({ getState: () => saved, subscribe: () => () => {}, attachOps: vi.fn(), flush: vi.fn(), getQueuedEntry: () => null, discardQueue: vi.fn() }) as unknown as SyncClient;

function mount() {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });
  store.setUi({ workspace: 'edit', selection: ['c1'], playheadFrame: 30 });
  render(
    <MantineProvider>
      <EditorWorkspace store={store} sync={fakeSync()} onReload={() => {}} />
    </MantineProvider>,
  );
  return store;
}

describe('page identity (T-002)', () => {
  it('sets the document title and the h1 on mount, without stealing focus', () => {
    const before = document.activeElement;
    mount();
    expect(document.title).toBe('Edit · Fixture');
    const heading = screen.getByTestId('page-heading');
    expect(heading.tagName).toBe('H1');
    expect(heading.textContent).toBe('Edit');
    expect(document.activeElement).toBe(before);
  });

  it('title, h1 and focus follow a page switch', () => {
    mount();
    fireEvent.click(screen.getByTestId('workspace-audio'));
    expect(document.title).toBe('Audio · Fixture');
    const heading = screen.getByTestId('page-heading');
    expect(heading.textContent).toBe('Audio');
    expect(document.activeElement).toBe(heading);
    fireEvent.click(screen.getByTestId('workspace-color'));
    expect(document.title).toBe('Colour · Fixture');
    expect(screen.getByTestId('page-heading').textContent).toBe('Colour');
  });

  it('restores the previous document title on unmount', () => {
    document.title = 'Previous document title';
    const view = render(
      <MantineProvider>
        <EditorWorkspace store={createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 })} sync={fakeSync()} onReload={() => {}} />
      </MantineProvider>,
    );
    expect(document.title).toContain('Edit');
    cleanup();
    expect(document.title).toBe('Previous document title');
    void view;
  });
});
