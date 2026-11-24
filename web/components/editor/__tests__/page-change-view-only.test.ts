import { afterEach, describe, expect, it, vi } from 'vitest';
import { createEditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import {
  changeEditorPage,
  initialiseEditorPage,
  subscribeToPageNavigation,
  type PageHistory,
  type PageStorage,
} from '@/lib/editing/page-url';

/**
 * T-001 row 5: a page change is navigation, not an edit. The document, the revision, the undo
 * history, the pending ops and the save flags keep their identity, and nothing is fetched. This is
 * the test that would fail if a future page switch were wired through the router or the store's
 * edit path.
 */

interface TestHistory extends PageHistory {
  href: string;
  pushes: string[];
  replaces: string[];
}

function fakeHistory(initialHref: string): TestHistory {
  const history: TestHistory = {
    state: { __NA: true } as unknown,
    href: initialHref,
    pushes: [],
    replaces: [],
    pushState(_data: unknown, _unused: string, url?: string | URL | null): void {
      const next = url == null ? history.href : String(url);
      history.pushes.push(next);
      history.href = next;
    },
    replaceState(_data: unknown, _unused: string, url?: string | URL | null): void {
      const next = url == null ? history.href : String(url);
      history.replaces.push(next);
      history.href = next;
    },
  };
  return history;
}

function fakeStorage(): PageStorage & { values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => {
      values.set(key, value);
    },
  };
}

function fakeTarget() {
  const listeners = new Set<() => void>();
  return {
    listeners,
    addEventListener: (_type: 'popstate', listener: () => void) => {
      listeners.add(listener);
    },
    removeEventListener: (_type: 'popstate', listener: () => void) => {
      listeners.delete(listener);
    },
    fire: () => {
      for (const listener of listeners) listener();
    },
  };
}

const href = '/editor/project/timeline/timeline?page=edit';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('a page change never edits or fetches', () => {
  it('changes the workspace and pushes one entry while document, revision, history and sync state keep identity', () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);

    const store = createEditorStore({
      timelineId: 'timeline',
      projectId: 'project',
      doc: standardDoc(),
      revision: 7,
    });
    const history = fakeHistory(href);
    const storage = fakeStorage();
    const before = store.getState();

    expect(changeEditorPage(store, 'audio', { history, href: history.href, storage })).toBe(true);

    const after = store.getState();
    expect(after).not.toBe(before);
    expect(after.ui.workspace).toBe('audio');
    expect(after.doc).toBe(before.doc);
    expect(after.revision).toBe(before.revision);
    expect(after.history).toBe(before.history);
    expect(after.cursor).toBe(before.cursor);
    expect(after.pendingOps).toBe(before.pendingOps);
    expect(after.dirty).toBe(before.dirty);
    expect(after.readOnly).toBe(before.readOnly);
    expect(history.pushes).toEqual(['/editor/project/timeline/timeline?page=audio']);
    expect(history.replaces).toEqual([]);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('leaves the same state intact across several changes and a popstate, with one push per change', () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);

    const store = createEditorStore({
      timelineId: 'timeline',
      projectId: 'project',
      doc: standardDoc(),
      revision: 12,
    });
    const history = fakeHistory(href);
    const storage = fakeStorage();
    const target = fakeTarget();
    const baseline = store.getState();

    changeEditorPage(store, 'colour', { history, href: history.href, storage });
    changeEditorPage(store, 'deliver', { history, href: history.href, storage });
    history.href = '/editor/project/timeline/timeline?page=audio';
    const unsubscribe = subscribeToPageNavigation(store, { target, href: () => history.href, storage });
    target.fire();
    unsubscribe();

    const after = store.getState();
    expect(after.ui.workspace).toBe('audio');
    expect(after.doc).toBe(baseline.doc);
    expect(after.revision).toBe(baseline.revision);
    expect(after.history).toBe(baseline.history);
    expect(after.pendingOps).toBe(baseline.pendingOps);
    expect(after.dirty).toBe(baseline.dirty);
    expect(history.pushes).toEqual([
      '/editor/project/timeline/timeline?page=colour',
      '/editor/project/timeline/timeline?page=deliver',
    ]);
    expect(history.replaces).toEqual([]);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('normalising the URL on mount is still view state only', () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);

    const store = createEditorStore({
      timelineId: 'timeline',
      projectId: 'project',
      doc: standardDoc(),
      revision: 3,
    });
    const before = store.getState();
    const history = fakeHistory(href);
    const storage = fakeStorage();

    initialiseEditorPage({ history, href: '/editor/project/timeline/timeline?page=color', storage });

    expect(store.getState()).toBe(before);
    expect(history.replaces).toEqual(['/editor/project/timeline/timeline?page=colour']);
    expect(history.pushes).toEqual([]);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
