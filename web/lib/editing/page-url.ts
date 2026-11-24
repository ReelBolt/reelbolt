import type { WorkspaceId } from './store/editor-store';

/**
 * The editor page is client state that also has an address. This module is the one place the URL
 * spelling of a page, the store's id for it and the device-local memory meet; it has no React and no
 * DOM of its own, so the parse/build/remember rules are testable without a browser.
 *
 * The URL says `colour`; the document model (and therefore the store) still says `color`, and is not
 * changed. `color` in a link is accepted as the legacy alias.
 */

/** Pages in switcher order. `colour` is the URL spelling of the store's `color`. */
export const EDITOR_PAGES = ['media', 'cut', 'edit', 'compose', 'colour', 'audio', 'deliver'] as const;

export type EditorPage = (typeof EDITOR_PAGES)[number];

export const DEFAULT_EDITOR_PAGE: EditorPage = 'edit';

/** Per device, like the preview-quality preference: a convenience, never document state. */
export const LAST_PAGE_STORAGE_KEY = 'reelbolt:editor:last-page';

/**
 * Own properties only (a null-prototype map), so `constructor`, `__proto__` or `toString` can never be
 * read as an alias: the value is re-validated before it is returned.
 */
const PAGE_ALIASES: Readonly<Record<string, EditorPage>> = Object.freeze(
  Object.assign(Object.create(null) as Record<string, EditorPage>, { color: 'colour' }),
);

/** The part of `localStorage` this module needs (a fake is enough in a test). */
export interface PageStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

/** The part of `history` this module needs. `state` is preserved so Next's router can restore. */
export interface PageHistory {
  readonly state: unknown;
  pushState(data: unknown, unused: string, url?: string | URL | null): void;
  replaceState(data: unknown, unused: string, url?: string | URL | null): void;
}

/** The part of `window` the popstate subscription needs. */
export interface PageEventTarget {
  addEventListener(type: 'popstate', listener: () => void): void;
  removeEventListener(type: 'popstate', listener: () => void): void;
}

/** The part of the editor store a page change touches: UI state only. */
export interface PageStore {
  getState(): { ui: { workspace: WorkspaceId } };
  setUi(patch: { workspace: WorkspaceId }): void;
}

export function isEditorPage(value: unknown): value is EditorPage {
  return typeof value === 'string' && (EDITOR_PAGES as readonly string[]).includes(value);
}

/**
 * A `?page=` value to the page it names. Absent is `null` (the caller restores the remembered page),
 * `color` is the alias, and an unknown value is Edit — a bad link still opens a usable page.
 */
export function normalizePageParam(value: string | null | undefined): EditorPage | null {
  if (value == null || value === '') return null;
  const lower = value.toLowerCase();
  const alias = Object.hasOwn(PAGE_ALIASES, lower) ? PAGE_ALIASES[lower] : undefined;
  if (alias && isEditorPage(alias)) return alias;
  return isEditorPage(lower) ? lower : DEFAULT_EDITOR_PAGE;
}

/** `"page=colour&x=1"` → `'colour'`; no `page` → `null`; an unknown page → `'edit'`. */
export function pageFromSearch(search: string): EditorPage | null {
  return normalizePageParam(new URLSearchParams(search).get('page'));
}

/** The page named by a full or relative href; the hash is ignored. */
export function pageFromHref(href: string): EditorPage | null {
  const query = href.indexOf('?');
  if (query < 0) return null;
  const hash = href.indexOf('#', query);
  return pageFromSearch(href.slice(query + 1, hash < 0 ? undefined : hash));
}

/** The store's id for a page: the URL says `colour`, the store says `color`. */
export function pageToWorkspace(page: EditorPage): WorkspaceId {
  return page === 'colour' ? 'color' : page;
}

/** The URL spelling of a store workspace id. */
export function workspaceToPage(workspace: WorkspaceId): EditorPage {
  return workspace === 'color' ? 'colour' : workspace;
}

/** The same href with `page` set, every other query parameter and the hash kept. */
export function withPageParam(href: string, page: EditorPage): string {
  const hashAt = href.indexOf('#');
  const hash = hashAt < 0 ? '' : href.slice(hashAt);
  const withoutHash = hashAt < 0 ? href : href.slice(0, hashAt);
  const queryAt = withoutHash.indexOf('?');
  const path = queryAt < 0 ? withoutHash : withoutHash.slice(0, queryAt);
  const params = new URLSearchParams(queryAt < 0 ? '' : withoutHash.slice(queryAt + 1));
  params.set('page', page);
  return `${path}?${params.toString()}${hash}`;
}

/** The page this device last used, or null when it has never opened the editor (or storage is off). */
export function rememberedPage(storage: PageStorage | null | undefined): EditorPage | null {
  if (!storage) return null;
  try {
    return normalizePageParam(storage.getItem(LAST_PAGE_STORAGE_KEY));
  } catch {
    return null;
  }
}

/** Remembers the page for the next cold load. Storage being full or off is never fatal. */
export function rememberPage(storage: PageStorage | null | undefined, page: EditorPage): void {
  try {
    storage?.setItem(LAST_PAGE_STORAGE_KEY, page);
  } catch {
    // A convenience, not a requirement: the URL already carries the page for this visit.
  }
}

/** The page a cold load opens: the URL's, else this device's remembered page, else Edit. */
export function resolveInitialPage(href: string, storage: PageStorage | null | undefined): EditorPage {
  return pageFromHref(href) ?? rememberedPage(storage) ?? DEFAULT_EDITOR_PAGE;
}

/**
 * Writes the page into the current history entry/moves to a new one. `null` state is deliberate:
 * Next's patched `pushState`/`replaceState` copies its own internal state onto the entry (so
 * back/forward can restore the same tree — no remount) and keeps its router URL in step. It does
 * not fetch: the timeline page does not read `page` for data.
 */
export function writePageToHistory(
  history: PageHistory,
  href: string,
  page: EditorPage,
  mode: 'push' | 'replace',
): void {
  const next = withPageParam(href, page);
  if (next === href) return;
  const before = history.state as { __NA?: unknown } | null | undefined;
  if (mode === 'push') history.pushState(null, '', next);
  else history.replaceState(null, '', next);
  // The first write runs before Next patches `history` (child effects run before the router's), so `null` really is
  // stored and the entry loses Next's `__NA` mark. Next reloads the whole page on a back to an unmarked entry: on a phone
  // that is every sheet opened from another sheet, and the reload drops the selection and the undo stack.
  if (before?.__NA && !(history.state as { __NA?: unknown } | null | undefined)?.__NA) history.replaceState(before, '', next);
}

/**
 * Mount: resolve the page from the URL (or this device's memory), write it back with `replaceState`
 * so first-load normalisation adds no history entry, remember it, and return it. The caller passes
 * the result into the store before the workspace renders.
 */
export function initialiseEditorPage(options: {
  history: PageHistory;
  href: string;
  storage: PageStorage | null | undefined;
}): EditorPage {
  const page = resolveInitialPage(options.href, options.storage);
  rememberPage(options.storage, page);
  writePageToHistory(options.history, options.href, page, 'replace');
  return page;
}

export interface PageChangeEnv {
  history: PageHistory;
  href: string;
  storage: PageStorage | null | undefined;
}

/**
 * A page change: UI state, one history entry and the remembered page — nothing else. Returns false
 * when the page is already active (no entry, no state write).
 */
export function changeEditorPage(store: PageStore, page: EditorPage, env: PageChangeEnv): boolean {
  const workspace = pageToWorkspace(page);
  if (store.getState().ui.workspace === workspace) return false;
  store.setUi({ workspace });
  rememberPage(env.storage, page);
  writePageToHistory(env.history, env.href, page, 'push');
  return true;
}

/**
 * Back/forward: the URL is the source of truth. Reads the page into the store and remembers it, and
 * never pushes an entry — the traversal already moved. Returns the unsubscribe.
 */
export function subscribeToPageNavigation(
  store: PageStore,
  options: { target: PageEventTarget; href: () => string; storage: PageStorage | null | undefined },
): () => void {
  const onPopState = () => {
    const page = resolveInitialPage(options.href(), options.storage);
    rememberPage(options.storage, page);
    store.setUi({ workspace: pageToWorkspace(page) });
  };
  options.target.addEventListener('popstate', onPopState);
  return () => options.target.removeEventListener('popstate', onPopState);
}
