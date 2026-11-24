/**
 * The C2 single-file rename helper. `fetch` is stubbed: nothing here needs the backend running.
 */

import { describe, it, expect, afterEach, vi } from 'vitest';
import { addSampleFile, getFilePictureUsage, renameFile } from './files';
import { ApiError } from './client';

const PROJECT = '11111111-1111-1111-1111-111111111111';
const FILE = '22222222-2222-2222-2222-222222222222';

afterEach(() => {
  vi.unstubAllGlobals();
});

function stub(status: number, body: unknown) {
  const fetchMock = vi.fn<typeof fetch>(async () =>
    new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('renameFile', () => {
  it('PATCHes { fileName } to the file and returns the updated file', async () => {
    const updated = { id: FILE, originalFileName: 'sunset.mp4', originalPath: 'broll/sunset.mp4' };
    const fetchMock = stub(200, updated);

    await expect(renameFile(PROJECT, FILE, 'sunset.mp4')).resolves.toEqual(updated);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(`/api/v1/projects/${PROJECT}/files/${FILE}`);
    expect(init?.method).toBe('PATCH');
    expect(JSON.parse(String(init?.body))).toEqual({ fileName: 'sunset.mp4' });
  });

  it('surfaces a name clash as an ApiError carrying the server message', async () => {
    stub(409, { message: 'Another file in this folder is already called "intro.mp4".' });

    const error = await renameFile(PROJECT, FILE, 'intro.mp4').catch((cause: unknown) => cause);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect((error as ApiError).message).toContain('already called');
  });
});

describe('getFilePictureUsage', () => {
  it('GETs …/picture-usage and answers the { pictures } count', async () => {
    const fetchMock = stub(200, { pictures: 3 });
    await expect(getFilePictureUsage(PROJECT, FILE)).resolves.toBe(3);
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/v1/projects/${PROJECT}/files/${FILE}/picture-usage`);
  });

  it('reads a missing or odd count as none', async () => {
    stub(200, {});
    await expect(getFilePictureUsage(PROJECT, FILE)).resolves.toBe(0);
    stub(200, { pictures: -2 });
    await expect(getFilePictureUsage(PROJECT, FILE)).resolves.toBe(0);
  });
});

describe('addSampleFile', () => {
  it('POSTs the sample key to the project files/sample route', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ id: 'f1' }), { status: 201 }));
    vi.stubGlobal('fetch', fetchMock);
    await addSampleFile('p1');
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/projects/p1/files/sample');
    expect(init.method).toBe('POST');
    expect(JSON.parse(String(init.body))).toEqual({ sampleKey: 'first-film' });
    vi.unstubAllGlobals();
  });
});
