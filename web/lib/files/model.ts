import type { ProjectFile } from '../types/project';
import { formatBytes as formatBytesShared } from '../utils/bytes';
import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

export type FileKind = 'video' | 'audio' | 'image' | 'text' | 'other';
export type FileScope = 'all' | 'sources' | 'generated' | 'renders';
export type FileSortKey = 'name' | 'modified' | 'size';

const TEXT_EXT = new Set([
  'txt', 'md', 'markdown', 'json', 'csv', 'tsv', 'yml', 'yaml', 'xml', 'html', 'css', 'js', 'jsx', 'ts', 'tsx',
  'srt', 'vtt', 'log', 'toml', 'ini', 'sh', 'py', 'go', 'cs', 'sql', 'env',
]);

export function extensionOf(name: string): string {
  const i = name.lastIndexOf('.');
  return i < 0 ? '' : name.slice(i + 1).toLowerCase();
}

export function fileKind(file: Pick<ProjectFile, 'mimeType' | 'originalFileName'>): FileKind {
  const mime = (file.mimeType || '').toLowerCase();
  if (mime.startsWith('video/')) return 'video';
  if (mime.startsWith('audio/')) return 'audio';
  if (mime.startsWith('image/')) return 'image';
  if (mime.startsWith('text/') || mime.includes('json') || mime.includes('xml') || mime.includes('yaml')) return 'text';
  return TEXT_EXT.has(extensionOf(file.originalFileName)) ? 'text' : 'other';
}

export function fileScope(file: Pick<ProjectFile, 'category'>): Exclude<FileScope, 'all'> {
  if (file.category === 'outputFiles') return 'renders';
  if (file.category === 'agentFiles') return 'generated';
  return 'sources';
}

/** Trims, unifies separators and strips leading/trailing slashes; '' is the project root. */
export function normalizeDir(path: string | null | undefined): string {
  return (path ?? '')
    .trim()
    .replace(/\\/g, '/')
    .replace(/\/{2,}/g, '/')
    .replace(/^\/+|\/+$/g, '');
}

export function joinPath(...parts: string[]): string {
  return normalizeDir(parts.map(normalizeDir).filter(Boolean).join('/'));
}

export function baseName(path: string): string {
  const p = normalizeDir(path);
  return p.slice(p.lastIndexOf('/') + 1);
}

export function parentDir(path: string): string {
  const p = normalizeDir(path);
  const i = p.lastIndexOf('/');
  return i < 0 ? '' : p.slice(0, i);
}

export function dirOf(file: Pick<ProjectFile, 'directoryPath'>): string {
  return normalizeDir(file.directoryPath);
}

export interface FolderNode {
  name: string;
  path: string;
  /** Files anywhere below this folder. */
  fileCount: number;
  children: FolderNode[];
  /** True when the folder only exists on the client (created, nothing uploaded into it yet). */
  virtual: boolean;
}

/** Folder tree for a set of files plus client-side "virtual" folders that have no files yet. */
export function buildFolderTree(files: ProjectFile[], virtualFolders: string[] = []): FolderNode {
  const root: FolderNode = { name: '', path: '', fileCount: 0, children: [], virtual: false };
  const byPath = new Map<string, FolderNode>([['', root]]);

  const ensure = (path: string, virtual: boolean): FolderNode => {
    const existing = byPath.get(path);
    if (existing) {
      if (!virtual) existing.virtual = false;
      return existing;
    }
    const parent = ensure(parentDir(path), virtual);
    const node: FolderNode = { name: baseName(path), path, fileCount: 0, children: [], virtual };
    parent.children.push(node);
    byPath.set(path, node);
    return node;
  };

  for (const f of files) {
    const dir = dirOf(f);
    root.fileCount += 1;
    if (!dir) continue;
    ensure(dir, false);
    let p = dir;
    while (p) {
      byPath.get(p)!.fileCount += 1;
      p = parentDir(p);
    }
  }
  for (const v of virtualFolders.map(normalizeDir).filter(Boolean)) ensure(v, true);

  const sort = (n: FolderNode) => {
    n.children.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true }));
    n.children.forEach(sort);
  };
  sort(root);
  return root;
}

export function findFolder(root: FolderNode, path: string): FolderNode | null {
  const target = normalizeDir(path);
  if (!target) return root;
  let node: FolderNode | undefined = root;
  for (const seg of target.split('/')) {
    node = node.children.find((c) => c.name === seg);
    if (!node) return null;
  }
  return node;
}

export function flattenFolders(root: FolderNode): FolderNode[] {
  const out: FolderNode[] = [];
  const walk = (n: FolderNode) => {
    for (const c of n.children) {
      out.push(c);
      walk(c);
    }
  };
  walk(root);
  return out;
}

export interface FileFilters {
  scope: FileScope;
  kind: FileKind | 'all';
  query: string;
}

export function matchesFilters(file: ProjectFile, filters: FileFilters): boolean {
  if (filters.scope !== 'all' && fileScope(file) !== filters.scope) return false;
  if (filters.kind !== 'all' && fileKind(file) !== filters.kind) return false;
  const q = filters.query.trim().toLowerCase();
  if (q) {
    const hay = `${file.originalFileName} ${dirOf(file)}`.toLowerCase();
    if (!q.split(/\s+/).every((t) => hay.includes(t))) return false;
  }
  return true;
}

export function compareFiles(key: FileSortKey, dir: 'asc' | 'desc') {
  const sign = dir === 'asc' ? 1 : -1;
  return (a: ProjectFile, b: ProjectFile): number => {
    let r = 0;
    if (key === 'name') r = a.originalFileName.localeCompare(b.originalFileName, undefined, { sensitivity: 'base', numeric: true });
    else if (key === 'size') r = a.sizeBytes - b.sizeBytes;
    else r = Date.parse(a.uploadedAt) - Date.parse(b.uploadedAt);
    return r * sign;
  };
}

export interface FolderListing {
  folders: FolderNode[];
  files: ProjectFile[];
}

/**
 * What the current folder shows. While a query is active the listing is flat across the whole
 * project (a filename search that only looked in the open folder would hide most matches), which
 * is why folders are dropped from it.
 */
export function listFolder(
  files: ProjectFile[],
  tree: FolderNode,
  path: string,
  filters: FileFilters,
  sort: { key: FileSortKey; dir: 'asc' | 'desc' },
): FolderListing {
  const filtered = files.filter((f) => matchesFilters(f, filters));
  const sorter = compareFiles(sort.key, sort.dir);
  if (filters.query.trim()) return { folders: [], files: filtered.sort(sorter) };

  const here = normalizeDir(path);
  const node = findFolder(tree, here);
  const inFolder = filtered.filter((f) => dirOf(f) === here).sort(sorter);
  // A folder only shows when something filtered lives below it, unless nothing is being filtered.
  const filtering = filters.scope !== 'all' || filters.kind !== 'all';
  const folders = (node?.children ?? []).filter((c) =>
    filtering ? filtered.some((f) => dirOf(f) === c.path || dirOf(f).startsWith(`${c.path}/`)) : true,
  );
  return { folders, files: inFolder };
}

// Kept as a named export (and now a thin wrapper): every caller has always read it from here, while
// the implementation moved to `lib/utils/bytes.ts` so modules that must not import the file tree —
// `lib/api/client.ts`, for the storage-quota sentence — can print a size too.
export function formatBytes(bytes: number): string {
  return formatBytesShared(bytes);
}

/**
 * "5 min ago", "just now". Pass the component's `useT()` and `useFormat().locale` for the active
 * language; without them the English wording is returned.
 */
export function timeAgo(iso: string | null | undefined, now: number = Date.now(), t: T = enT, locale: string = 'en-US'): string {
  if (!iso) return t('libLabels.files.timeNever');
  const at = Date.parse(iso);
  if (Number.isNaN(at)) return t('libLabels.files.timeUnknown');
  const s = Math.max(0, Math.round((now - at) / 1000));
  if (s < 45) return t('libLabels.files.timeJustNow');
  const m = Math.round(s / 60);
  if (m < 60) return t('libLabels.files.timeMinutes', { count: m });
  const h = Math.round(m / 60);
  if (h < 24) return t('libLabels.files.timeHours', { count: h });
  const d = Math.round(h / 24);
  if (d < 30) return t('libLabels.files.timeDays', { count: d });
  return new Date(at).toLocaleDateString(locale === 'en' || locale === 'en-XA' ? 'en-US' : locale, { month: 'short', day: 'numeric', year: 'numeric' });
}

/** "42s", "3m 5s", "1h 5m" since `startIso`. Pass `useT()` for the active language. */
export function formatElapsed(startIso: string | null | undefined, now: number = Date.now(), t: T = enT): string {
  if (!startIso) return '';
  const at = Date.parse(startIso);
  if (Number.isNaN(at)) return '';
  const s = Math.max(0, Math.round((now - at) / 1000));
  if (s < 60) return t('libLabels.files.elapsedSeconds', { s });
  const m = Math.floor(s / 60);
  if (m < 60) return t('libLabels.files.elapsedMinutes', { m, s: s % 60 });
  return t('libLabels.files.elapsedHours', { h: Math.floor(m / 60), m: m % 60 });
}
