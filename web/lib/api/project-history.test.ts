import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  createProjectHistoryVersion,
  deleteProjectHistoryVersion,
  eraseProjectHistoryFile,
  getProjectHistoryChanges,
  getProjectHistoryItemDiff,
  getProjectHistoryVersion,
  listProjectHistoryVersions,
  previewProjectHistoryRestore,
  projectHistoryChangesUrl,
  projectHistoryItemContentUrl,
  projectHistoryItemDiffUrl,
  projectHistoryVersionUrl,
  projectHistoryVersionsUrl,
  restoreProjectHistory,
} from './project-history';

function mockFetchOnce(status: number, body: unknown, statusText = 'OK') {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    statusText,
    json: () => Promise.resolve(body),
  }));
}

const call = (n = 0) => {
  const [url, init] = vi.mocked(fetch).mock.calls[n] as [string, RequestInit | undefined];
  return { url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined };
};

afterEach(() => vi.unstubAllGlobals());

describe('the project history client', () => {
  it('builds the list URL with paging and no empty query', () => {
    expect(projectHistoryVersionsUrl('p1')).toBe('/api/v1/projects/p1/history/versions');
    expect(projectHistoryVersionsUrl('p1', { limit: 50, before: 9 }))
      .toBe('/api/v1/projects/p1/history/versions?limit=50&before=9');
  });

  it('encodes every id and item key that goes into a path', () => {
    expect(projectHistoryVersionsUrl('p/1')).toBe('/api/v1/projects/p%2F1/history/versions');
    expect(projectHistoryVersionUrl('p1', 'v/1')).toBe('/api/v1/projects/p1/history/versions/v%2F1');
    expect(projectHistoryChangesUrl('p1', 'v1', 'v 2'))
      .toBe('/api/v1/projects/p1/history/versions/v1/changes?to=v%202');
    expect(projectHistoryItemDiffUrl('p1', 'v1', 'File', 'k/ey', 'current'))
      .toBe('/api/v1/projects/p1/history/versions/v1/items/File/k%2Fey/diff?to=current');
    expect(projectHistoryItemContentUrl('p1', 'v1', 'File', 'k/ey'))
      .toBe('/api/v1/projects/p1/history/versions/v1/items/File/k%2Fey/content');
  });

  it('defaults the comparison target to current and marks an absent one as current', () => {
    expect(projectHistoryChangesUrl('p1', 'v1')).toBe('/api/v1/projects/p1/history/versions/v1/changes?to=current');
    expect(projectHistoryItemDiffUrl('p1', 'v1', 'Project', 'project', null))
      .toBe('/api/v1/projects/p1/history/versions/v1/items/Project/project/diff?to=current');
  });

  it('lists versions with a plain GET, including the server permission flags', async () => {
    mockFetchOnce(200, { versions: [], historyBytes: 0, retentionDays: 30, canEdit: true, canDelete: false });
    await expect(listProjectHistoryVersions('p1')).resolves.toEqual({
      versions: [],
      historyBytes: 0,
      retentionDays: 30,
      canEdit: true,
      canDelete: false,
    });
    expect(call()).toMatchObject({ url: '/api/v1/projects/p1/history/versions', method: 'GET' });
  });

  it('saves a named version with a POST body', async () => {
    mockFetchOnce(201, { id: 'v1', number: 1 });
    await createProjectHistoryVersion('p1', 'Before the review');
    expect(call()).toMatchObject({
      url: '/api/v1/projects/p1/history/versions',
      method: 'POST',
      body: { name: 'Before the review' },
    });
  });

  it('reads one version, the changes and one item diff through their encoded URLs', async () => {
    mockFetchOnce(200, { id: 'v1' });
    await getProjectHistoryVersion('p1', 'v1');
    expect(call().url).toBe('/api/v1/projects/p1/history/versions/v1');

    mockFetchOnce(200, { changes: [], unchangedCount: 0, truncated: false });
    await getProjectHistoryChanges('p1', 'v1', 'v2');
    expect(call().url).toBe('/api/v1/projects/p1/history/versions/v1/changes?to=v2');

    mockFetchOnce(200, { kind: 'File', key: 'f1', mode: 'binary' });
    await getProjectHistoryItemDiff('p1', 'v1', 'File', 'f1');
    expect(call().url).toBe('/api/v1/projects/p1/history/versions/v1/items/File/f1/diff?to=current');
  });

  it('deletes a version and erases a file, with DELETE', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 204, json: () => Promise.resolve(undefined) }));
    await deleteProjectHistoryVersion('p1', 'v1');
    expect(call()).toMatchObject({ url: '/api/v1/projects/p1/history/versions/v1', method: 'DELETE' });

    await eraseProjectHistoryFile('p1', 'f/1');
    expect(vi.mocked(fetch).mock.calls[1][0]).toBe('/api/v1/projects/p1/history/files/f%2F1');
    expect((vi.mocked(fetch).mock.calls[1][1] as RequestInit).method).toBe('DELETE');
  });

  it('previews and applies a restore with the same request body', async () => {
    const request = { scope: 'item' as const, items: [{ kind: 'File', key: 'f1' }] };
    mockFetchOnce(200, { scope: 'item', changes: [], blocked: [], estimateBytes: 0 });
    await previewProjectHistoryRestore('p1', 'v1', request);
    expect(call()).toMatchObject({
      url: '/api/v1/projects/p1/history/versions/v1/restore/preview',
      method: 'POST',
      body: request,
    });

    mockFetchOnce(200, { safetyVersionId: 's1', restored: [], removed: [], skipped: [], notices: [] });
    await restoreProjectHistory('p1', 'v1', { scope: 'project' });
    expect(call()).toMatchObject({
      url: '/api/v1/projects/p1/history/versions/v1/restore',
      method: 'POST',
      body: { scope: 'project' },
    });
  });

  it('surfaces the server refusal code of a restore', async () => {
    mockFetchOnce(409, { code: 'RESTORE_IN_PROGRESS', message: 'Another restore is running.' });
    await expect(restoreProjectHistory('p1', 'v1', { scope: 'project' }))
      .rejects.toMatchObject({ code: 'RESTORE_IN_PROGRESS', status: 409 });
  });
});
