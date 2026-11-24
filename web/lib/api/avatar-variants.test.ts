/**
 * The avatar variants and builds client: the exact URL, method and body of every route
 * (docs/design/avatar-objects.md "Avatar variants"). `fetch` is stubbed, so these assert the contract the
 * Inference API serves.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  avatarBuildPath, avatarBuildsPath, cancelAvatarBuild, estimateAvatarVariant, getAvatarBuild, listAvatarBuilds, startAvatarVariant,
} from './avatar-variants';
import { avatarsPath, createAvatar, updateAvatar } from './avatars';
import { ApiError } from './client';

function respond(body: unknown, status = 200) {
  const fetchMock = vi.fn<typeof fetch>(async () =>
    new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const call = (mock: ReturnType<typeof respond>) => {
  const [url, init] = mock.mock.calls[0] as [string, RequestInit | undefined];
  return { url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(init.body as string) : undefined };
};

afterEach(() => vi.unstubAllGlobals());

describe('avatar variants client', () => {
  it('builds its paths under the project avatars', () => {
    expect(avatarBuildsPath('p 1')).toBe(`${avatarsPath('p 1')}/builds`);
    expect(avatarBuildPath('p1', 'j/1')).toBe('/api/v1/projects/p1/avatars/builds/j%2F1');
  });

  it('asks what a variant would cost without writing anything', async () => {
    const mock = respond({ billingEnabled: true, credits: 12, affordable: true, pictures: 2, videos: 0 });
    const estimate = await estimateAvatarVariant('p1', 'a1', { referenceIds: ['r1', 'r2'], includeVideos: false });
    expect(call(mock)).toEqual({
      url: '/api/v1/projects/p1/avatars/a1/variants/estimate', method: 'POST', body: { referenceIds: ['r1', 'r2'], includeVideos: false },
    });
    expect(estimate.credits).toBe(12);
  });

  it('starts a variant build with the instruction, the texts, the chosen files and the object statement', async () => {
    const mock = respond({ jobId: 'j1', state: 'queued' }, 202);
    const request = {
      instruction: 'Paint it red.', variantLabel: 'Red', variantDescription: 'matte red paint', referenceIds: ['r1'], includeVideos: true,
      consentStatement: 'statement', consentLocale: 'it',
    };
    expect(await startAvatarVariant('p1', 'a1', request)).toEqual({ jobId: 'j1', state: 'queued' });
    expect(call(mock)).toEqual({ url: '/api/v1/projects/p1/avatars/a1/variants', method: 'POST', body: request });
  });

  it('carries the refusal code of a start that needs the permission to send media', async () => {
    respond({ code: 'CONSENT_REQUIRED', message: 'Allow sending media first.' }, 409);
    const refusal = await startAvatarVariant('p1', 'a1', {
      instruction: 'x', variantLabel: 'x', variantDescription: 'x', referenceIds: ['r1'], includeVideos: false, consentStatement: 's', consentLocale: 'en',
    }).catch((e) => e);
    expect(refusal).toBeInstanceOf(ApiError);
    expect(refusal).toMatchObject({ status: 409, code: 'CONSENT_REQUIRED' });
  });

  it('lists the builds, reads one and stops one', async () => {
    let mock = respond([{ jobId: 'j1', kind: 'variant', state: 'pictures' }]);
    await listAvatarBuilds('p1');
    expect(call(mock)).toMatchObject({ url: '/api/v1/projects/p1/avatars/builds', method: 'GET' });
    mock = respond({ jobId: 'j1', kind: 'variant', state: 'ready', avatarId: 'a9' });
    expect((await getAvatarBuild('p1', 'j1')).avatarId).toBe('a9');
    expect(call(mock)).toMatchObject({ url: '/api/v1/projects/p1/avatars/builds/j1', method: 'GET' });
    mock = respond({ jobId: 'j1', kind: 'variant', state: 'cancelled' });
    expect((await cancelAvatarBuild('p1', 'j1'))?.state).toBe('cancelled');
    expect(call(mock)).toEqual({ url: '/api/v1/projects/p1/avatars/builds/j1/cancel', method: 'POST', body: undefined });
  });

  it('takes a stop that answers with no body, and still reports a refused one', async () => {
    respond(undefined, 202);
    await expect(cancelAvatarBuild('p1', 'j1')).resolves.toBeUndefined();
    respond(undefined, 204);
    await expect(cancelAvatarBuild('p1', 'j1')).resolves.toBeUndefined();
    respond({ code: 'BUILD_NOT_RUNNING' }, 409);
    await expect(cancelAvatarBuild('p1', 'j1')).rejects.toMatchObject({ status: 409, code: 'BUILD_NOT_RUNNING' });
  });

  it('makes a variant from other files with the ordinary create, plus the three variant fields', async () => {
    let mock = respond({ id: 'a2' }, 201);
    await createAvatar('p1', {
      subjectKind: 'Object', name: 'Car · Red', consentStatement: 's', consentLocale: 'en', references: [{ projectFileId: 'f1', role: 'Front' }],
      variantOfAvatarId: 'a1', variantLabel: 'Red', variantDescription: 'matte red paint',
    });
    expect(call(mock)).toMatchObject({
      url: '/api/v1/projects/p1/avatars', method: 'POST',
      body: { subjectKind: 'Object', variantOfAvatarId: 'a1', variantLabel: 'Red', variantDescription: 'matte red paint' },
    });
    mock = respond({ id: 'a2' });
    await updateAvatar('p1', 'a2', { variantDescription: 'gloss red paint' });
    expect(call(mock)).toEqual({ url: '/api/v1/projects/p1/avatars/a2', method: 'PATCH', body: { variantDescription: 'gloss red paint' } });
  });
});
