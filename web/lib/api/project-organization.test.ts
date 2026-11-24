import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import { deleteProjectTag, organizeProjects, projectOrganizationErrorMessage, setProjectOrganization, updateProjectTag } from './project-organization';

afterEach(() => vi.restoreAllMocks());

function mockFetch(status: number, body?: unknown) {
  const spy = vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
    new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }),
  );
  return spy;
}

describe('project organization api', () => {
  it('organize resolves on 204 and posts the body', async () => {
    const spy = mockFetch(204);
    await expect(organizeProjects({ projectIds: ['p'], addTagIds: ['t'], removeTagIds: [], clearFolder: true })).resolves.toBeUndefined();
    const [url, init] = spy.mock.calls[0];
    expect(String(url)).toContain('/api/v1/projects/organize');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toMatchObject({ projectIds: ['p'], clearFolder: true });
  });
  it('uses PUT for organization and PATCH for tags', async () => {
    const spy = mockFetch(200, {});
    await setProjectOrganization('p1', { tagIds: [], folderId: null });
    await updateProjectTag('t1', { color: 'teal' });
    expect(spy.mock.calls[0][1]?.method).toBe('PUT');
    expect(String(spy.mock.calls[0][0])).toContain('/api/v1/projects/p1/organization');
    expect(spy.mock.calls[1][1]?.method).toBe('PATCH');
  });
  it.each(['TAG_NAME_TAKEN', 'TAG_LIMIT_REACHED', 'FOLDER_NAME_TAKEN', 'FOLDER_LIMIT_REACHED', 'PROJECT_TAG_LIMIT_REACHED'])(
    'maps %s to a plain sentence',
    (code) => {
      const err = new ApiError('raw', 409, [], { code, message: 'raw' });
      const out = projectOrganizationErrorMessage(err);
      expect(out).not.toBe('raw');
      expect(out).not.toContain(code);
      expect(out).toMatch(/^[A-Z].*\.$/);
    },
  );
  it('passes unknown errors through', () => {
    expect(projectOrganizationErrorMessage(new Error('Disk is full'))).toBe('Disk is full');
  });
});

describe('url path segments', () => {
  it('encodes a hostile id so it cannot leave its route', async () => {
    const { getTagDetailActivityUrl } = await import('./project-organization');
    const url = getTagDetailActivityUrl('../../admin/users', 30);
    expect(url).not.toContain('../');
    expect(url.startsWith('/api/v1/project-tags/')).toBe(true);
    expect(url).toContain('%2F');
  });
  it('encodes ids on write routes', async () => {
    const spy = mockFetch(204);
    await deleteProjectTag('../x');
    expect(String(spy.mock.calls[0][0])).toBe('/api/v1/project-tags/..%2Fx');
  });
});
