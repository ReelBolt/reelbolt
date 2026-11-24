import { describe, expect, it } from 'vitest';
import type { ProjectFile } from '../types/project';
import {
  buildFolderTree, fileKind, fileScope, findFolder, flattenFolders, formatBytes, joinPath, listFolder, normalizeDir,
  parentDir, timeAgo,
} from './model';

function f(name: string, dir: string | undefined, over: Partial<ProjectFile> = {}): ProjectFile {
  return {
    id: `${dir ?? ''}/${name}`, projectId: 'p', originalFileName: name, directoryPath: dir, category: 'userFiles',
    mimeType: 'text/plain', sizeBytes: 10, storageKey: 'k', agentSummary: null, summaryStatus: 'Done',
    indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z', ...over,
  };
}
const all = { scope: 'all', kind: 'all', query: '' } as const;
const byName = { key: 'name', dir: 'asc' } as const;

describe('paths', () => {
  it('normalizes separators and edges', () => {
    expect(normalizeDir('\\a//b/ ')).toBe('a/b');
    expect(normalizeDir(undefined)).toBe('');
    expect(joinPath('a/', '/b', '', 'c')).toBe('a/b/c');
    expect(parentDir('a/b/c')).toBe('a/b');
    expect(parentDir('a')).toBe('');
  });
});

describe('classification', () => {
  it('classifies by mime, falling back to extension', () => {
    expect(fileKind({ mimeType: 'video/mp4', originalFileName: 'a.mp4' })).toBe('video');
    expect(fileKind({ mimeType: 'application/octet-stream', originalFileName: 'notes.md' })).toBe('text');
    expect(fileKind({ mimeType: 'application/zip', originalFileName: 'a.zip' })).toBe('other');
  });
  it('maps category to scope', () => {
    expect(fileScope({ category: 'outputFiles' })).toBe('renders');
    expect(fileScope({ category: 'agentFiles' })).toBe('generated');
    expect(fileScope({ category: 'userFiles' })).toBe('sources');
  });
});

describe('folder tree', () => {
  const files = [f('a.txt', undefined), f('b.txt', 'footage'), f('c.txt', 'footage/day1'), f('d.txt', 'footage/day1')];
  it('counts files at every ancestor', () => {
    const t = buildFolderTree(files);
    expect(t.fileCount).toBe(4);
    expect(findFolder(t, 'footage')?.fileCount).toBe(3);
    expect(findFolder(t, 'footage/day1')?.fileCount).toBe(2);
    expect(findFolder(t, 'nope')).toBeNull();
  });
  it('merges virtual folders and clears the flag when real files exist', () => {
    const t = buildFolderTree(files, ['footage/day2', 'footage/day1']);
    expect(findFolder(t, 'footage/day2')?.virtual).toBe(true);
    expect(findFolder(t, 'footage/day1')?.virtual).toBe(false);
    expect(flattenFolders(t).map((n) => n.path)).toEqual(['footage', 'footage/day1', 'footage/day2']);
  });
});

describe('listFolder', () => {
  const files = [f('a.txt', undefined), f('b.mp4', 'footage', { mimeType: 'video/mp4' }), f('c.txt', 'footage/day1')];
  const tree = buildFolderTree(files);
  it('shows direct children only', () => {
    const root = listFolder(files, tree, '', all, byName);
    expect(root.files.map((x) => x.originalFileName)).toEqual(['a.txt']);
    expect(root.folders.map((x) => x.name)).toEqual(['footage']);
  });
  it('searches the whole project, flat, when a query is active', () => {
    const r = listFolder(files, tree, '', { ...all, query: 'c.t' }, byName);
    expect(r.folders).toEqual([]);
    expect(r.files.map((x) => x.originalFileName)).toEqual(['c.txt']);
  });
  it('hides folders that contain nothing matching the type filter', () => {
    const r = listFolder(files, tree, '', { ...all, kind: 'text' }, byName);
    expect(r.folders.map((x) => x.name)).toEqual(['footage']);
    const v = listFolder(files, tree, 'footage', { ...all, kind: 'video' }, byName);
    expect(v.folders).toEqual([]);
    expect(v.files.map((x) => x.originalFileName)).toEqual(['b.mp4']);
  });
});

describe('formatting', () => {
  it('formats bytes', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5 MB');
    // A whole number keeps no decimal ("15 GB", not "15.0 GB"); anything else keeps one.
    expect(formatBytes(16_106_127_360)).toBe('15 GB');
    expect(formatBytes(4_509_715_660)).toBe('4.2 GB');
    expect(formatBytes(512)).toBe('512 B');
  });
  it('formats relative time', () => {
    const now = Date.parse('2026-10-04T12:00:00Z');
    expect(timeAgo('2026-10-04T11:59:50Z', now)).toBe('just now');
    expect(timeAgo('2026-10-04T11:30:00Z', now)).toBe('30 min ago');
    expect(timeAgo('2026-10-04T09:00:00Z', now)).toBe('3 h ago');
    expect(timeAgo(null, now)).toBe('never');
  });
});
