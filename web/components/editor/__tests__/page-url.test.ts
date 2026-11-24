import { describe, expect, it, vi } from 'vitest';
import {
  DEFAULT_EDITOR_PAGE,
  EDITOR_PAGES,
  LAST_PAGE_STORAGE_KEY,
  changeEditorPage,
  initialiseEditorPage,
  isEditorPage,
  normalizePageParam,
  pageFromHref,
  pageFromSearch,
  pageToWorkspace,
  rememberedPage,
  rememberPage,
  writePageToHistory,
  resolveInitialPage,
  subscribeToPageNavigation,
  withPageParam,
  workspaceToPage,
  type PageStorage,
} from '@/lib/editing/page-url';
import type { WorkspaceId } from '@/lib/editing/store';

/**
 * T-001: the page is a place. These are the rules behind `?page=`, tested without React or a
 * browser: the vocabulary, the legacy alias, the unknown-value fallback, build/preserve, the
 * per-device memory, and the page-to-workspace mapping the store keeps using.
 */

function memoryStorage(initial: Record<string, string> = {}): PageStorage & { read(key: string): string | null } {
  const values = new Map(Object.entries(initial));
  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => {
      values.set(key, value);
    },
    read: (key) => values.get(key) ?? null,
  };
}

function fakeHistory(state: unknown = { __NA: true }) {
  const calls: Array<{ kind: 'push' | 'replace'; url: string | null }> = [];
  return {
    state,
    calls,
    pushState: vi.fn((_data: unknown, _unused: string, url?: string | URL | null) => {
      calls.push({ kind: 'push', url: url == null ? null : String(url) });
    }),
    replaceState: vi.fn((_data: unknown, _unused: string, url?: string | URL | null) => {
      calls.push({ kind: 'replace', url: url == null ? null : String(url) });
    }),
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
  };
}

function fakeStore(workspace: WorkspaceId = 'edit') {
  let ui = { workspace };
  return {
    getState: () => ({ ui }),
    setUi: (patch: { workspace: WorkspaceId }) => {
      ui = { ...ui, ...patch };
    },
    workspace: () => ui.workspace,
  };
}

describe('the page vocabulary', () => {
  it('names the seven pages in workflow order and maps them onto the store ids', () => {
    expect(EDITOR_PAGES).toEqual(['media', 'cut', 'edit', 'compose', 'colour', 'audio', 'deliver']);
    for (const page of EDITOR_PAGES) {
      const workspace = pageToWorkspace(page);
      expect(isEditorPage(page)).toBe(true);
      expect(workspaceToPage(workspace)).toBe(page);
    }
    expect(pageToWorkspace('colour')).toBe('color');
    expect(workspaceToPage('color')).toBe('colour');
  });

  it('reads ?page=, accepts the color alias, and falls back to edit on an unknown value', () => {
    expect(pageFromSearch('page=media')).toBe('media');
    expect(pageFromSearch('page=cut&x=1')).toBe('cut');
    expect(pageFromSearch('page=colour')).toBe('colour');
    expect(pageFromSearch('page=color')).toBe('colour');
    expect(pageFromSearch('page=nonsense')).toBe(DEFAULT_EDITOR_PAGE);
    expect(pageFromSearch('')).toBeNull();
    expect(pageFromSearch('other=1')).toBeNull();
    expect(normalizePageParam(null)).toBeNull();
    expect(normalizePageParam('AUDIO')).toBe('audio');
  });

  it('never reads an Object.prototype member as a page alias', () => {
    for (const value of ['constructor', '__proto__', 'toString', 'hasOwnProperty', 'valueOf']) {
      expect(normalizePageParam(value), value).toBe(DEFAULT_EDITOR_PAGE);
      expect(pageFromSearch(`page=${value}`), value).toBe(DEFAULT_EDITOR_PAGE);
    }
    expect(normalizePageParam('color')).toBe('colour');
    expect(normalizePageParam('Color')).toBe('colour');
  });

  it('reads the page from a full or relative href and ignores the hash', () => {
    expect(pageFromHref('/editor/p/timeline/t?page=audio')).toBe('audio');
    expect(pageFromHref('https://example.test/editor/p/timeline/t?x=1&page=color#marks')).toBe('colour');
    expect(pageFromHref('/editor/p/timeline/t#page=audio')).toBeNull();
    expect(pageFromHref('/editor/p/timeline/t')).toBeNull();
  });

  it('builds an href that replaces page in place and keeps every other parameter and the hash', () => {
    expect(withPageParam('/editor/p/timeline/t?page=edit&x=1#h', 'colour')).toBe(
      '/editor/p/timeline/t?page=colour&x=1#h',
    );
    expect(withPageParam('/editor/p/timeline/t', 'audio')).toBe('/editor/p/timeline/t?page=audio');
    expect(withPageParam('/editor/p/timeline/t?page=edit', 'edit')).toBe('/editor/p/timeline/t?page=edit');
  });
});

describe('remembering the page per device', () => {
  it('stores and reads the last page', () => {
    const storage = memoryStorage();
    expect(rememberedPage(storage)).toBeNull();
    rememberPage(storage, 'audio');
    expect(storage.read(LAST_PAGE_STORAGE_KEY)).toBe('audio');
    expect(rememberedPage(storage)).toBe('audio');
  });

  it('treats missing or throwing storage as "never opened the editor"', () => {
    expect(rememberedPage(null)).toBeNull();
    expect(rememberedPage(undefined)).toBeNull();
    const broken: PageStorage = {
      getItem: () => {
        throw new Error('storage off');
      },
      setItem: () => {
        throw new Error('storage off');
      },
    };
    expect(rememberedPage(broken)).toBeNull();
    expect(() => rememberPage(broken, 'cut')).not.toThrow();
  });

  it('prefers the URL page, then the remembered page, then edit', () => {
    const storage = memoryStorage({ [LAST_PAGE_STORAGE_KEY]: 'audio' });
    expect(resolveInitialPage('/editor/p/timeline/t?page=cut', storage)).toBe('cut');
    expect(resolveInitialPage('/editor/p/timeline/t?page=color', storage)).toBe('colour');
    expect(resolveInitialPage('/editor/p/timeline/t', storage)).toBe('audio');
    expect(resolveInitialPage('/editor/p/timeline/t', null)).toBe('edit');
  });

  it('normalises with replaceState on mount and remembers the result', () => {
    const history = fakeHistory();
    const storage = memoryStorage({ [LAST_PAGE_STORAGE_KEY]: 'audio' });
    const page = initialiseEditorPage({ history, href: '/editor/p/timeline/t', storage });

    expect(page).toBe('audio');
    expect(history.calls).toEqual([{ kind: 'replace', url: '/editor/p/timeline/t?page=audio' }]);
    expect(storage.read(LAST_PAGE_STORAGE_KEY)).toBe('audio');
  });

  it('normalises an unknown page to edit in the URL without adding an entry', () => {
    const history = fakeHistory();
    const storage = memoryStorage();
    expect(initialiseEditorPage({ history, href: '/editor/p/timeline/t?page=nonsense', storage })).toBe('edit');
    expect(history.calls).toEqual([{ kind: 'replace', url: '/editor/p/timeline/t?page=edit' }]);
    expect(storage.read(LAST_PAGE_STORAGE_KEY)).toBe('edit');
  });
});

describe('a page change and back/forward', () => {
  it('changes only the workspace, remembers it, and pushes exactly one entry', () => {
    const store = fakeStore('edit');
    const history = fakeHistory();
    const storage = memoryStorage();
    const changed = changeEditorPage(store, 'colour', {
      history,
      href: '/editor/p/timeline/t?page=edit',
      storage,
    });

    expect(changed).toBe(true);
    expect(store.workspace()).toBe('color');
    expect(history.calls).toEqual([{ kind: 'push', url: '/editor/p/timeline/t?page=colour' }]);
    expect(storage.read(LAST_PAGE_STORAGE_KEY)).toBe('colour');
  });

  it('does not write a second entry when the page is already active', () => {
    const store = fakeStore('audio');
    const history = fakeHistory();
    expect(
      changeEditorPage(store, 'audio', { history, href: '/editor/p/timeline/t?page=audio', storage: null }),
    ).toBe(false);
    expect(history.calls).toEqual([]);
  });

  it('reads the page back into the store on popstate and never pushes', () => {
    const store = fakeStore('edit');
    const target = fakeTarget();
    const storage = memoryStorage();
    let href = '/editor/p/timeline/t?page=edit';

    const unsubscribe = subscribeToPageNavigation(store, { target, href: () => href, storage });
    href = '/editor/p/timeline/t?page=deliver';
    for (const listener of target.listeners) listener();

    expect(store.workspace()).toBe('deliver');
    expect(storage.read(LAST_PAGE_STORAGE_KEY)).toBe('deliver');
    expect(target.listeners.size).toBe(1);

    unsubscribe();
    expect(target.listeners.size).toBe(0);
  });

  it('uses the remembered page when a traversed entry has none', () => {
    const store = fakeStore('edit');
    const target = fakeTarget();
    const storage = memoryStorage({ [LAST_PAGE_STORAGE_KEY]: 'compose' });
    subscribeToPageNavigation(store, { target, href: () => '/editor/p/timeline/t', storage });
    for (const listener of target.listeners) listener();
    expect(store.workspace()).toBe('compose');
  });
});

describe('writePageToHistory keeps the mark Next needs on an entry', () => {
  /** `history` before Next patches it: `null` really is stored, and Next reloads the page on a back to an unmarked entry. */
  const unpatched = (state: unknown) => {
    const h = {
      state,
      pushState(data: unknown) { h.state = data; },
      replaceState(data: unknown) { h.state = data; },
    };
    return h;
  };

  it('an unpatched first write does not wipe the mark', () => {
    const marked = { __NA: true, __PRIVATE_NEXTJS_INTERNALS_TREE: ['tree'] };
    const h = unpatched(marked);
    writePageToHistory(h, 'http://x/app/editor/p/timeline/t', 'edit', 'replace');
    expect(h.state).toEqual(marked);
  });

  it('an unpatched push keeps the mark too', () => {
    const marked = { __NA: true };
    const h = unpatched(marked);
    writePageToHistory(h, 'http://x/app/editor/p/timeline/t', 'colour', 'push');
    expect(h.state).toEqual(marked);
  });

  it('a patched write is left as Next made it', () => {
    const h = unpatched({ __NA: true });
    h.replaceState = (data: unknown) => { h.state = { ...(data as object), __NA: true, patched: true }; };
    writePageToHistory(h, 'http://x/app/editor/p/timeline/t', 'edit', 'replace');
    expect(h.state).toEqual({ __NA: true, patched: true });
  });

  it('an entry with no mark to keep stays as it was written', () => {
    const h = unpatched(null);
    writePageToHistory(h, 'http://x/app/editor/p/timeline/t', 'edit', 'replace');
    expect(h.state).toBeNull();
  });
});
