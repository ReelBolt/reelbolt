/**
 * The workspace avatar library client: the exact URL, method and body of every route
 * (docs/design/avatar-objects.md "The workspace avatar library"). `fetch` is stubbed, so these assert the
 * contract the Inference API serves.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  avatarLibraryPath, avatarLibraryPictureUrl, deleteAvatarLibraryItem, getAvatarLibraryItem, importLibraryAvatar,
  listAvatarLibrary, publishToAvatarLibrary, reconfirmAvatarLibraryItem, revokeAvatarLibraryConsent, updateAvatarLibraryItem,
} from './avatar-library';
import { ApiError } from './client';

function respond(body: unknown, status = 200) {
  const fetchMock = vi.fn<typeof fetch>(async () =>
    new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const call = (mock: ReturnType<typeof respond>) => {
  const [url, init] = mock.mock.calls[0] as [string, RequestInit | undefined];
  return { url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(init.body as string) : undefined };
};

afterEach(() => vi.unstubAllGlobals());

describe('avatar library client', () => {
  it('lists the library, with the project when there is one', async () => {
    let mock = respond([]);
    await listAvatarLibrary();
    expect(call(mock)).toMatchObject({ url: '/api/v1/avatar-library', method: 'GET' });
    mock = respond([]);
    await listAvatarLibrary('p 1');
    expect(call(mock).url).toBe('/api/v1/avatar-library?projectId=p%201');
    expect(avatarLibraryPath(null)).toBe('/api/v1/avatar-library');
  });

  it('reads one item and builds the picture address', async () => {
    const mock = respond({ id: 'l1' });
    await getAvatarLibraryItem('l1');
    expect(call(mock)).toMatchObject({ url: '/api/v1/avatar-library/l1', method: 'GET' });
    expect(avatarLibraryPictureUrl('l1', 'r1')).toBe('/api/v1/avatar-library/l1/references/r1/content');
  });

  it('publishes a project avatar with the statement and, for a person, the adult tick and the keep-for days', async () => {
    const mock = respond({ id: 'l1' }, 201);
    await publishToAvatarLibrary({ projectId: 'p1', avatarId: 'a1', consentStatement: 'text', consentLocale: 'it', subjectIsAdult: true, retentionDays: 365 });
    expect(call(mock)).toEqual({
      url: '/api/v1/avatar-library', method: 'POST',
      body: { projectId: 'p1', avatarId: 'a1', consentStatement: 'text', consentLocale: 'it', subjectIsAdult: true, retentionDays: 365 },
    });
  });

  it('renames, confirms again, withdraws and deletes by item id', async () => {
    let mock = respond({ id: 'l1' });
    await updateAvatarLibraryItem('l1', { name: 'New' });
    expect(call(mock)).toEqual({ url: '/api/v1/avatar-library/l1', method: 'PATCH', body: { name: 'New' } });

    mock = respond({ id: 'l1' });
    await reconfirmAvatarLibraryItem('l1', { consentStatement: 'text', consentLocale: 'en' });
    expect(call(mock)).toEqual({ url: '/api/v1/avatar-library/l1/reconfirm', method: 'POST', body: { consentStatement: 'text', consentLocale: 'en' } });

    mock = respond({ id: 'l1' });
    await revokeAvatarLibraryConsent('l1');
    expect(call(mock)).toMatchObject({ url: '/api/v1/avatar-library/l1/revoke-consent', method: 'POST', body: undefined });

    mock = respond({ imports: { deleted: 2, failed: 0 }, objects: { deleted: 3, failed: 0 } });
    const report = await deleteAvatarLibraryItem('l1');
    expect(call(mock)).toMatchObject({ url: '/api/v1/avatar-library/l1', method: 'DELETE' });
    expect(report.imports.deleted).toBe(2);
  });

  it('imports an item into a project', async () => {
    const mock = respond({ id: 'a9', fromLibrary: true, libraryItemId: 'l1' }, 201);
    const avatar = await importLibraryAvatar('p1', 'l1');
    expect(call(mock)).toEqual({ url: '/api/v1/projects/p1/avatars/import', method: 'POST', body: { libraryItemId: 'l1' } });
    expect(avatar.fromLibrary).toBe(true);
  });

  it('keeps the refusal code of a refused import', async () => {
    respond({ code: 'AVATAR_ALREADY_IMPORTED', message: 'Already imported.' }, 409);
    const err = await importLibraryAvatar('p1', 'l1').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe('AVATAR_ALREADY_IMPORTED');
  });
});
