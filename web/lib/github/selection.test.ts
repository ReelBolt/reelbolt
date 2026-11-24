import { describe, expect, it } from 'vitest';
import {
  buildSelectionTree, checkState, checkStates, defaultSelection, describeIncludePaths, filesUnder, fromIncludePaths,
  isWithin, matchingFiles, selectionProblem, toIncludePaths, toggle, totals, visibleForSearch,
} from './selection';
import type { GitHubFileEntry } from '../types/github';

const f = (path: string, size = 10): GitHubFileEntry => ({ path, isDirectory: false, size });
const d = (path: string): GitHubFileEntry => ({ path, isDirectory: true, size: 0 });

const entries: GitHubFileEntry[] = [
  d('src'), d('src/video'), d('src/videos'), d('empty'),
  f('src/video/scene.tsx', 100), f('src/video/title.tsx', 200), f('src/videos/other.tsx', 300),
  f('src/app.ts', 400), f('README.md', 50),
];

const tree = () => buildSelectionTree(entries);
const node = (path: string) => {
  const find = (n: ReturnType<typeof tree>): ReturnType<typeof tree> | null =>
    n.path === path ? n : n.children.map(find).find(Boolean) ?? null;
  return find(tree())!;
};

describe('isWithin', () => {
  it('matches by whole path segment, like the server', () => {
    expect(isWithin('src/video/a', 'src/video')).toBe(true);
    expect(isWithin('src/video', 'src/video')).toBe(true);
    expect(isWithin('src/videos/a', 'src/video')).toBe(false);
    expect(isWithin('anything', '')).toBe(true);
  });
});

describe('buildSelectionTree', () => {
  it('sorts folders first, sums sizes and drops folders with nothing to sync', () => {
    const t = tree();
    expect(t.children.map((c) => c.name)).toEqual(['src', 'README.md']);
    expect(t.fileCount).toBe(5);
    expect(t.size).toBe(1050);
    expect(node('src').children.map((c) => c.name)).toEqual(['video', 'videos', 'app.ts']);
  });

  it('can be rooted at the old single-folder option, offering only what is inside it', () => {
    const rooted = buildSelectionTree(entries, 'src/video');
    expect(filesUnder(rooted)).toEqual(['src/video/scene.tsx', 'src/video/title.tsx']);
  });
});

describe('checkbox states', () => {
  it('a folder is checked, unchecked or indeterminate from its files', () => {
    const t = tree();
    const selected = new Set(['src/video/scene.tsx']);
    expect(checkState(node('src/video'), selected)).toBe('indeterminate');
    expect(checkState(node('src/videos'), selected)).toBe('unchecked');
    const states = checkStates(t, new Set(filesUnder(node('src/video'))));
    expect(states.get('src/video')).toBe('checked');
    expect(states.get('src')).toBe('indeterminate');
    expect(states.get('')).toBe('indeterminate');
  });

  it('a click on a half-ticked folder takes all of it; a click on a full one clears it', () => {
    const half = new Set(['src/video/scene.tsx']);
    const all = toggle(node('src/video'), half);
    expect([...all].sort()).toEqual(['src/video/scene.tsx', 'src/video/title.tsx']);
    expect([...toggle(node('src/video'), all)]).toEqual([]);
  });
});

describe('toIncludePaths / fromIncludePaths', () => {
  it('everything ticked is saved as "the whole repository"', () => {
    const t = tree();
    expect(toIncludePaths(t, new Set(filesUnder(t)))).toEqual([]);
  });

  it('nothing ticked cannot be saved', () => {
    expect(toIncludePaths(tree(), new Set())).toBeNull();
  });

  it('a whole folder is saved as the folder, a partial one as its files', () => {
    const selected = new Set([...filesUnder(node('src/video')), 'src/videos/other.tsx', 'README.md']);
    expect(toIncludePaths(tree(), selected)).toEqual(['src/video', 'src/videos', 'README.md']);
    const partial = new Set(['src/video/scene.tsx']);
    expect(toIncludePaths(tree(), partial)).toEqual(['src/video/scene.tsx']);
  });

  it('round-trips through the saved form', () => {
    const t = tree();
    const saved = ['src/video', 'README.md'];
    const ticks = fromIncludePaths(t, saved);
    expect([...ticks].sort()).toEqual(['README.md', 'src/video/scene.tsx', 'src/video/title.tsx']);
    expect(toIncludePaths(t, ticks)).toEqual(saved);
  });

  it('a saved folder does not swallow a sibling that only shares its prefix', () => {
    expect([...fromIncludePaths(tree(), ['src/video'])]).not.toContain('src/videos/other.tsx');
  });

  it('null or [] ticks everything', () => {
    expect(fromIncludePaths(tree(), null).size).toBe(5);
    expect(fromIncludePaths(tree(), []).size).toBe(5);
  });

  it('a rooted tree saves everything as its root folder', () => {
    const rooted = buildSelectionTree(entries, 'src/video');
    expect(toIncludePaths(rooted, new Set(filesUnder(rooted)))).toEqual(['src/video']);
  });
});

describe('totals and limits', () => {
  it('counts files and bytes, and flags files over the per-file limit', () => {
    const t = tree();
    expect(totals(t, new Set(filesUnder(t)), 250)).toEqual({ files: 5, bytes: 1050, oversized: 2 });
  });

  it('names the problem with the numbers', () => {
    expect(selectionProblem({ files: 0, bytes: 0, oversized: 0 }, { maxFiles: 10, maxTotalBytes: 100 })).toEqual({ kind: 'empty' });
    expect(selectionProblem({ files: 11, bytes: 1, oversized: 0 }, { maxFiles: 10, maxTotalBytes: 100 }))
      .toEqual({ kind: 'too_many_files', files: 11, limit: 10 });
    expect(selectionProblem({ files: 1, bytes: 101, oversized: 0 }, { maxFiles: 10, maxTotalBytes: 100 }))
      .toEqual({ kind: 'too_large', bytes: 101, limit: 100 });
    expect(selectionProblem({ files: 10, bytes: 100, oversized: 0 }, { maxFiles: 10, maxTotalBytes: 100 })).toBeNull();
  });

  it('starts a new link with everything when it fits and with nothing when it does not', () => {
    const t = tree();
    expect(defaultSelection(t, { maxFiles: 5, maxTotalBytes: 0 }).size).toBe(5);
    expect(defaultSelection(t, { maxFiles: 4, maxTotalBytes: 0 }).size).toBe(0);
    expect(defaultSelection(t, { maxFiles: 0, maxTotalBytes: 1000 }).size).toBe(0);
  });
});

describe('search', () => {
  it('shows matching files and the folders on the way to them', () => {
    const t = tree();
    const visible = visibleForSearch(t, 'SCENE')!;
    expect(visible.has('src/video/scene.tsx')).toBe(true);
    expect(visible.has('src/video')).toBe(true);
    expect(visible.has('src')).toBe(true);
    expect(visible.has('README.md')).toBe(false);
    expect(matchingFiles(t, visible)).toEqual(['src/video/scene.tsx']);
  });

  it('a matching folder shows all of itself', () => {
    const t = tree();
    expect(matchingFiles(t, visibleForSearch(t, 'videos')!)).toEqual(['src/videos/other.tsx']);
  });

  it('no query means no filter', () => {
    expect(visibleForSearch(tree(), '  ')).toBeNull();
  });
});

describe('describeIncludePaths', () => {
  it('summarises the saved choice in a line', () => {
    expect(describeIncludePaths(null, null)).toBe('Every file in the repository');
    expect(describeIncludePaths(null, 'video')).toBe('Everything in video/');
    expect(describeIncludePaths(['a', 'b'], null)).toBe('a, b');
    expect(describeIncludePaths(['a', 'b', 'c', 'd', 'e'], null)).toBe('a, b, c and 2 more');
  });
});
