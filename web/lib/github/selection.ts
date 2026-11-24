/**
 * Choosing which files of a GitHub repository a project syncs (docs/github-app.md "Choosing which
 * files are synced"). Pure functions only, so every rule the selection tree shows is tested here
 * rather than through clicks.
 *
 * The model:
 *
 * - The tree is built from the flat listing the server returns for one branch.
 * - What the person has ticked is held as a set of FILE paths. Folders have no state of their own:
 *   a folder is checked when every file under it is, indeterminate when some are.
 * - What is SAVED is the smallest list of paths that says the same thing (`toIncludePaths`): a folder
 *   whose every file is ticked is saved as the folder — so files added to it later are synced too —
 *   and everything ticked is saved as `[]`, "the whole repository".
 * - A saved list is turned back into ticks with `fromIncludePaths`, using the same matching rule the
 *   server uses (a path matches itself or anything inside it, by whole segment).
 */
import type { GitHubFileEntry } from '../types/github';

export interface SelectionNode {
  /** Repository-relative path; `''` for the root. */
  path: string;
  name: string;
  isDirectory: boolean;
  /** Bytes for a file; the sum of every file below for a folder. */
  size: number;
  /** Files at or below this node. 1 for a file. */
  fileCount: number;
  children: SelectionNode[];
}

/** True when `path` is `folderOrFile` or lies inside it, by whole path segment. */
export function isWithin(path: string, folderOrFile: string): boolean {
  if (folderOrFile === '') return true;
  return path === folderOrFile || path.startsWith(`${folderOrFile}/`);
}

function nameOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? path : path.slice(i + 1);
}

function parentOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

/**
 * Builds the tree, rooted at `root` (`''` for the repository; a folder for a link made with the old
 * single-folder option, whose files can only come from inside it). Folders sort before files, both
 * alphabetically; folders that hold no file at all (only ignored content) are dropped, because ticking
 * them could never sync anything.
 */
export function buildSelectionTree(entries: GitHubFileEntry[], root = ''): SelectionNode {
  const rootNode: SelectionNode = { path: root, name: root ? nameOf(root) : '', isDirectory: true, size: 0, fileCount: 0, children: [] };
  const folders = new Map<string, SelectionNode>([[root, rootNode]]);

  const folderFor = (path: string): SelectionNode => {
    const existing = folders.get(path);
    if (existing) return existing;
    const node: SelectionNode = { path, name: nameOf(path), isDirectory: true, size: 0, fileCount: 0, children: [] };
    folders.set(path, node);
    folderFor(parentOf(path)).children.push(node);
    return node;
  };

  for (const entry of entries) {
    if (entry.isDirectory) continue;
    if (root && !isWithin(entry.path, root)) continue;
    if (entry.path === root) continue;
    const file: SelectionNode = { path: entry.path, name: nameOf(entry.path), isDirectory: false, size: Math.max(0, entry.size), fileCount: 1, children: [] };
    folderFor(parentOf(entry.path)).children.push(file);
  }

  const finish = (node: SelectionNode): void => {
    if (!node.isDirectory) return;
    node.children.forEach(finish);
    node.children = node.children.filter((c) => !c.isDirectory || c.fileCount > 0);
    node.children.sort((a, b) =>
      a.isDirectory === b.isDirectory ? a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) : a.isDirectory ? -1 : 1);
    node.fileCount = node.children.reduce((n, c) => n + c.fileCount, 0);
    node.size = node.children.reduce((n, c) => n + c.size, 0);
  };
  finish(rootNode);
  return rootNode;
}

/** Every file path below `node` (or `node` itself, for a file). */
export function filesUnder(node: SelectionNode): string[] {
  if (!node.isDirectory) return [node.path];
  const out: string[] = [];
  const walk = (n: SelectionNode) => {
    if (!n.isDirectory) out.push(n.path);
    else n.children.forEach(walk);
  };
  walk(node);
  return out;
}

export type CheckState = 'checked' | 'unchecked' | 'indeterminate';

/** A node's checkbox: a file is ticked or not; a folder follows its files. */
export function checkState(node: SelectionNode, selected: ReadonlySet<string>): CheckState {
  if (!node.isDirectory) return selected.has(node.path) ? 'checked' : 'unchecked';
  let some = false;
  let all = true;
  for (const path of filesUnder(node)) {
    if (selected.has(path)) some = true;
    else all = false;
    if (some && !all) return 'indeterminate';
  }
  if (node.fileCount === 0) return 'unchecked';
  return all ? 'checked' : some ? 'indeterminate' : 'unchecked';
}

/**
 * Every node's checkbox state in one walk — what the tree renders from, so a repository of thousands
 * of files is not re-walked once per visible folder.
 */
export function checkStates(tree: SelectionNode, selected: ReadonlySet<string>): Map<string, CheckState> {
  const states = new Map<string, CheckState>();
  const walk = (n: SelectionNode): number => {
    if (!n.isDirectory) {
      const on = selected.has(n.path);
      states.set(n.path, on ? 'checked' : 'unchecked');
      return on ? 1 : 0;
    }
    const ticked = n.children.reduce((sum, c) => sum + walk(c), 0);
    states.set(n.path, n.fileCount === 0 || ticked === 0 ? 'unchecked' : ticked === n.fileCount ? 'checked' : 'indeterminate');
    return ticked;
  };
  walk(tree);
  return states;
}

/**
 * Clicking a node's checkbox. A checked node is cleared; an unchecked or partly checked one is
 * filled — the usual rule, so a click on a half-ticked folder takes all of it.
 */
export function toggle(node: SelectionNode, selected: ReadonlySet<string>): Set<string> {
  const next = new Set(selected);
  const files = filesUnder(node);
  if (checkState(node, selected) === 'checked') files.forEach((p) => next.delete(p));
  else files.forEach((p) => next.add(p));
  return next;
}

/**
 * The smallest list of paths that selects exactly the ticked files: `[]` when everything is ticked
 * (the whole repository, or the whole of `root`), a folder when all of it is, a file otherwise.
 * `null` when nothing is ticked — there is nothing to link.
 */
export function toIncludePaths(tree: SelectionNode, selected: ReadonlySet<string>): string[] | null {
  if (tree.fileCount === 0) return null;
  const states = checkStates(tree, selected);
  const state = states.get(tree.path);
  if (state === 'unchecked') return null;
  if (state === 'checked') return tree.path === '' ? [] : [tree.path];

  const out: string[] = [];
  const walk = (node: SelectionNode) => {
    const s = states.get(node.path);
    if (s === 'unchecked') return;
    if (s === 'checked') { out.push(node.path); return; }
    node.children.forEach(walk);
  };
  tree.children.forEach(walk);
  return out;
}

/** The ticks a saved selection stands for. `null`/`[]` ticks every file of the tree. */
export function fromIncludePaths(tree: SelectionNode, includePaths: string[] | null | undefined): Set<string> {
  const files = filesUnder(tree);
  if (!includePaths || includePaths.length === 0) return new Set(files);
  return new Set(files.filter((f) => includePaths.some((inc) => isWithin(f, inc))));
}

export interface SelectionTotals {
  files: number;
  bytes: number;
  /** Ticked files over the per-file size limit: they will be skipped, not synced. */
  oversized: number;
}

export function totals(tree: SelectionNode, selected: ReadonlySet<string>, maxFileBytes: number): SelectionTotals {
  let files = 0;
  let bytes = 0;
  let oversized = 0;
  const walk = (n: SelectionNode) => {
    if (n.isDirectory) { n.children.forEach(walk); return; }
    if (!selected.has(n.path)) return;
    files++;
    bytes += n.size;
    if (maxFileBytes > 0 && n.size > maxFileBytes) oversized++;
  };
  walk(tree);
  return { files, bytes, oversized };
}

/** What is wrong with a selection, if anything, in words; null when it can be saved. */
export type SelectionProblem =
  | { kind: 'empty' }
  | { kind: 'too_many_files'; files: number; limit: number }
  | { kind: 'too_large'; bytes: number; limit: number };

export function selectionProblem(
  t: SelectionTotals,
  limits: { maxFiles: number; maxTotalBytes: number },
): SelectionProblem | null {
  if (t.files === 0) return { kind: 'empty' };
  if (limits.maxFiles > 0 && t.files > limits.maxFiles) return { kind: 'too_many_files', files: t.files, limit: limits.maxFiles };
  if (limits.maxTotalBytes > 0 && t.bytes > limits.maxTotalBytes) return { kind: 'too_large', bytes: t.bytes, limit: limits.maxTotalBytes };
  return null;
}

/**
 * The search filter: the files whose path contains the text (any case), plus every folder on the
 * way to one, so a match is shown where it lives. Returns the set of visible paths, or null when
 * there is no search.
 */
export function visibleForSearch(tree: SelectionNode, query: string): Set<string> | null {
  const needle = query.trim().toLowerCase();
  if (!needle) return null;
  const visible = new Set<string>();
  const walk = (n: SelectionNode): boolean => {
    if (!n.isDirectory) {
      const hit = n.path.toLowerCase().includes(needle);
      if (hit) visible.add(n.path);
      return hit;
    }
    // A folder whose own name matches shows all of itself.
    if (n.path && n.name.toLowerCase().includes(needle)) {
      visible.add(n.path);
      filesUnder(n).forEach((p) => visible.add(p));
      const addFolders = (f: SelectionNode) => { if (f.isDirectory) { visible.add(f.path); f.children.forEach(addFolders); } };
      addFolders(n);
      return true;
    }
    let any = false;
    for (const c of n.children) if (walk(c)) any = true;
    if (any) visible.add(n.path);
    return any;
  };
  walk(tree);
  return visible;
}

/** The file paths a search shows — what "Select matches" and "Clear matches" act on. */
export function matchingFiles(tree: SelectionNode, visible: ReadonlySet<string>): string[] {
  return filesUnder(tree).filter((p) => visible.has(p));
}

/**
 * The selection a new link starts with: every file, when that fits within the limits; nothing,
 * when it does not — a repository bigger than one project can sync is one where the person has to
 * choose, and ticking everything would only start them on an error.
 */
export function defaultSelection(
  tree: SelectionNode,
  limits: { maxFiles: number; maxTotalBytes: number },
): Set<string> {
  const all = new Set(filesUnder(tree));
  const fits = (limits.maxFiles <= 0 || tree.fileCount <= limits.maxFiles) &&
    (limits.maxTotalBytes <= 0 || tree.size <= limits.maxTotalBytes);
  return fits ? all : new Set();
}

/** One line summarising a saved selection, for the linked view. */
export function describeIncludePaths(includePaths: string[] | null | undefined, pathPrefix: string | null | undefined): string {
  if (!includePaths || includePaths.length === 0) {
    return pathPrefix ? `Everything in ${pathPrefix}/` : 'Every file in the repository';
  }
  const shown = includePaths.slice(0, 3).join(', ');
  const more = includePaths.length - 3;
  return more > 0 ? `${shown} and ${more} more` : shown;
}
