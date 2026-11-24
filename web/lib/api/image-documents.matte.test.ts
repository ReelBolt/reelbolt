import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import { deleteImageLayerMatte, putImageLayerMatte } from './image-documents';

const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>();
vi.stubGlobal('fetch', fetchMock);
afterEach(() => fetchMock.mockReset());

describe('layer mask routes', () => {
  it('PUTs the PNG with baseRevision and reads the new revision from the header', async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ id: 'L', kind: 'Source', matteUrl: '/m', hasMatte: true }), {
        status: 200,
        headers: { 'Content-Type': 'application/json', 'X-Image-Document-Revision': '8' },
      }),
    );
    const png = new Blob(['x'], { type: 'image/png' });
    const result = await putImageLayerMatte('p', 'd', 'L', png, 7);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/v1/projects/p/image-documents/d/layers/L/matte?baseRevision=7');
    expect(init?.method).toBe('PUT');
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('image/png');
    expect(init?.body).toBe(png);
    expect(result.revision).toBe(8);
    expect(result.layer?.kind).toBe('source');
  });

  it('turns a refusal into an ApiError with its status', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ code: 'MATTE_SIZE_MISMATCH', message: 'bad size' }), { status: 400 }));
    await expect(putImageLayerMatte('p', 'd', 'L', new Blob(['x']))).rejects.toMatchObject({ status: 400, message: 'bad size' });
    await expect(putImageLayerMatte('p', 'd', 'L', new Blob(['x']))).rejects.toBeInstanceOf(ApiError);
  });

  it('DELETE answers the revision and sends no body', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204, headers: { 'X-Image-Document-Revision': '9' } }));
    const result = await deleteImageLayerMatte('p', 'd', 'L', 8);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/projects/p/image-documents/d/layers/L/matte?baseRevision=8');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
    expect(result).toEqual({ layer: null, revision: 9 });
  });
});
