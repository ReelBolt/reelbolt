import { msg, type Msg } from '@/i18n/msg';

/**
 * Choosing which files of a linked cloud folder are synced, on a tree that is loaded one folder at a
 * time (a cloud drive is far too large to list in one go, unlike a git tree).
 *
 * The saved form is the server's (`includePaths`, the GitHub selection's format and rules): the
 * smallest list of folder-relative paths, a folder standing for everything inside it — including files
 * added to it later. So the selection kept here is exactly that list: a set of TOP-MOST chosen paths,
 * never a path together with one of its ancestors.
 */

/** `true` when `path` is `ancestor` itself or lies inside it, by whole segment. */
export function isWithinPath(path: string, ancestor: string): boolean {
  return path === ancestor || path.startsWith(`${ancestor}/`);
}

/** The folder a path sits in; `''` for the top of the linked folder. */
export function parentOf(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash < 0 ? '' : path.slice(0, slash);
}

/** A child path under `parent` (`''` is the top of the linked folder). */
export function joinPath(parent: string, name: string): string {
  return parent ? `${parent}/${name}` : name;
}

export type CheckState = 'checked' | 'partial' | 'unchecked';

/** Checked when the path or a folder above it is chosen; partial when something inside it is. */
export function checkStateOf(path: string, selected: ReadonlySet<string>): CheckState {
  for (const s of selected) if (isWithinPath(path, s)) return 'checked';
  for (const s of selected) if (isWithinPath(s, path)) return 'partial';
  return 'unchecked';
}

/**
 * What the tree knows about one folder: the paths of its loaded children, and whether that listing is
 * complete (a truncated listing can never stand for the whole folder). Undefined: not loaded yet.
 */
export type ChildrenOf = (folderPath: string) => { paths: string[]; complete: boolean } | undefined;

/**
 * Ticks `path`: it is chosen, anything inside it is folded into it, and a folder whose every child is
 * now chosen (from a complete listing) is chosen as the folder instead, so files added to it later
 * come too.
 */
export function tick(path: string, selected: ReadonlySet<string>, childrenOf: ChildrenOf): Set<string> {
  const next = new Set([...selected].filter((s) => !isWithinPath(s, path)));
  next.add(path);

  let current = path;
  while (current) {
    const parent = parentOf(current);
    if (!parent) break;
    const listing = childrenOf(parent);
    if (!listing || !listing.complete || listing.paths.length === 0) break;
    if (!listing.paths.every((p) => next.has(p))) break;
    for (const p of listing.paths) next.delete(p);
    next.add(parent);
    current = parent;
  }
  return next;
}

/**
 * Unticks `path`. When a folder above it was chosen, that folder is replaced by its children except
 * the way down to `path`, level by level — which only needs the folders on that way to be loaded, and
 * they are, since `path` is on screen.
 */
export function untick(path: string, selected: ReadonlySet<string>, childrenOf: ChildrenOf): Set<string> {
  const next = new Set([...selected].filter((s) => !isWithinPath(s, path)));
  const covering = [...next].find((s) => isWithinPath(path, s));
  if (!covering) return next;

  next.delete(covering);
  let folder = covering;
  while (folder !== path) {
    const rest = path.slice(folder.length + 1);
    const step = joinPath(folder, rest.split('/')[0]);
    for (const child of childrenOf(folder)?.paths ?? []) if (child !== step) next.add(child);
    folder = step;
  }
  return next;
}

/** Ticks or unticks, by the item's current state. */
export function toggle(path: string, selected: ReadonlySet<string>, childrenOf: ChildrenOf): Set<string> {
  return checkStateOf(path, selected) === 'checked'
    ? untick(path, selected, childrenOf)
    : tick(path, selected, childrenOf);
}

/** The list to send: sorted, top-most paths only. */
export function toIncludePaths(selected: ReadonlySet<string>): string[] {
  return [...selected].filter((s) => ![...selected].some((o) => o !== s && isWithinPath(s, o))).sort();
}

/** How a saved selection reads in a sentence. */
export function describeSelection(includePaths: string[] | null | undefined): Msg {
  if (!includePaths || includePaths.length === 0) return msg('integrations.cloud.selection.everyFile');
  if (includePaths.length <= 3) return msg('integrations.cloud.selection.list', { list: includePaths.join(', ') });
  return msg('integrations.cloud.selection.listMore', {
    list: includePaths.slice(0, 3).join(', '),
    count: includePaths.length - 3,
  });
}
