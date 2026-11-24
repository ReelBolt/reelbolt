/**
 * The layer-document API module. fetch is stubbed: nothing here needs the backend running.
 *
 * Every route in this file is load-bearing for the editor's subject cut-outs — a wrong path or a
 * wrong body shape is a silent 404/400 at run time and no type checker sees it — so each one is
 * asserted with its exact URL and its exact JSON body.
 */

import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  createImageDocument,
  deleteImageDocument,
  deleteImageLayer,
  detectSubjects,
  getImageDocument,
  getImageDocuments,
  imageLayerMatteUrl,
  imageLayerUrl,
  updateImageLayer,
} from './image-documents';
import { ApiError } from './client';

const PROJECT = '11111111-1111-1111-1111-111111111111';
const DOCUMENT = '22222222-2222-2222-2222-222222222222';
const LAYER = '33333333-3333-3333-3333-333333333333';
const FILE = '44444444-4444-4444-4444-444444444444';

const DOCUMENTS_URL = '/api/v1/projects/' + PROJECT + '/image-documents';
const DOCUMENT_URL = DOCUMENTS_URL + '/' + DOCUMENT;
const LAYER_URL = DOCUMENT_URL + '/layers/' + LAYER;

afterEach(() => {
  vi.unstubAllGlobals();
});

function stub(status: number, body: unknown) {
  const fetchMock = vi.fn<typeof fetch>(async () =>
    new Response(body === undefined ? null : JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    }),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** The JSON body of call n, parsed. */
function bodyOf(fetchMock: ReturnType<typeof stub>, n = 0): unknown {
  return JSON.parse(String(fetchMock.mock.calls[n][1]?.body));
}

const layer = {
  id: LAYER,
  zOrder: 2,
  role: 'subject',
  kind: 'cutout',
  label: 'the person',
  sourceProjectFileId: FILE,
  imageUrl: LAYER_URL + '/image',
  matteUrl: null,
  mimeType: 'image/png',
  width: 800,
  height: 600,
  opacity: 1,
  transform: { x: 0, y: 0, scale: 1, rotation: 0 },
  segmentationModel: 'fal-ai/evf-sam',
  segmentationPrompt: 'the person',
  createdAt: '2026-01-01T00:00:00Z',
};

describe('reading layer documents', () => {
  it('lists the project documents as SUMMARIES, with no layers in them', async () => {
    // The route answers EditImageDocumentSummaryDto[]: a picker needs the name and the layer count,
    // and fetching every layer of every document to draw one dropdown is work nothing asked for.
    const documents = [
      { id: DOCUMENT, name: 'Cover layers', sourceProjectFileId: FILE, revision: 4, layerCount: 2 },
    ];
    const fetchMock = stub(200, documents);

    await expect(getImageDocuments(PROJECT)).resolves.toEqual(documents);

    expect(fetchMock.mock.calls[0][0]).toBe(DOCUMENTS_URL);
    expect(fetchMock.mock.calls[0][1]?.method).toBeUndefined();
  });

  it('reads one document with its layers', async () => {
    const document = { id: DOCUMENT, revision: 4, layers: [layer] };
    const fetchMock = stub(200, document);

    await expect(getImageDocument(PROJECT, DOCUMENT)).resolves.toEqual(document);

    expect(fetchMock.mock.calls[0][0]).toBe(DOCUMENT_URL);
  });
});

describe('createImageDocument', () => {
  it('POSTs the source file id, and the name only when one is given', async () => {
    const fetchMock = stub(201, { id: DOCUMENT, layers: [] });

    await createImageDocument(PROJECT, { sourceProjectFileId: FILE, name: 'Cover' });

    expect(fetchMock.mock.calls[0][0]).toBe(DOCUMENTS_URL);
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
    expect(bodyOf(fetchMock)).toEqual({ sourceProjectFileId: FILE, name: 'Cover' });
  });

  it('surfaces a refusal as an ApiError carrying the server message', async () => {
    stub(400, { message: 'That file is not an image.' });

    const error = await createImageDocument(PROJECT, { sourceProjectFileId: FILE }).catch(
      (cause: unknown) => cause,
    );

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(400);
    expect((error as ApiError).message).toBe('That file is not an image.');
  });
});

describe('detectSubjects', () => {
  // The one route that spends money, so it gets the most precise assertion: the exact prompt and the
  // exact fal endpoint id, which is a string nothing else type-checks.
  it('POSTs the prompt and the fal model to the subjects route and answers with the new layer', async () => {
    const fetchMock = stub(201, layer);

    await expect(
      detectSubjects(PROJECT, DOCUMENT, { prompt: 'the person', model: 'fal-ai/evf-sam' }),
    ).resolves.toEqual([layer]);

    expect(fetchMock.mock.calls[0][0]).toBe(DOCUMENT_URL + '/subjects');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
    expect(bodyOf(fetchMock)).toEqual({ prompt: 'the person', model: 'fal-ai/evf-sam' });
  });

  it('omits every field the caller left out rather than sending nulls', async () => {
    const fetchMock = stub(201, layer);

    await detectSubjects(PROJECT, DOCUMENT, { prompt: 'the dog' });

    expect(bodyOf(fetchMock)).toEqual({ prompt: 'the dog' });
  });

  it('reports an unpaid-for failure as an ApiError instead of a fake layer', async () => {
    stub(402, { message: 'Your workspace is out of credits.' });

    const error = await detectSubjects(PROJECT, DOCUMENT, { prompt: 'the person' }).catch(
      (cause: unknown) => cause,
    );

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toBe('Your workspace is out of credits.');
  });
});

describe('layer writes', () => {
  it('PATCHes just the fields it is given', async () => {
    const fetchMock = stub(200, { ...layer, role: 'overlay' });

    await updateImageLayer(PROJECT, DOCUMENT, LAYER, { role: 'overlay' });

    expect(fetchMock.mock.calls[0][0]).toBe(LAYER_URL);
    expect(fetchMock.mock.calls[0][1]?.method).toBe('PATCH');
    expect(bodyOf(fetchMock)).toEqual({ role: 'overlay' });
  });

  it('sends z-order, opacity and transform together when all three change', async () => {
    const fetchMock = stub(200, layer);

    await updateImageLayer(PROJECT, DOCUMENT, LAYER, {
      zOrder: 5,
      opacity: 0.5,
      transform: { x: 10, y: -4, scale: 1.2, rotation: 15 },
    });

    expect(bodyOf(fetchMock)).toEqual({
      zOrder: 5,
      opacity: 0.5,
      transform: { x: 10, y: -4, scale: 1.2, rotation: 15 },
    });
  });

  it('DELETEs one layer', async () => {
    const fetchMock = stub(204, undefined);

    await expect(deleteImageLayer(PROJECT, DOCUMENT, LAYER)).resolves.toBeUndefined();

    expect(fetchMock.mock.calls[0][0]).toBe(LAYER_URL);
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
  });

  it('DELETEs a whole document', async () => {
    const fetchMock = stub(204, undefined);

    await expect(deleteImageDocument(PROJECT, DOCUMENT)).resolves.toBeUndefined();

    expect(fetchMock.mock.calls[0][0]).toBe(DOCUMENT_URL);
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
  });
});

describe('the two byte routes', () => {
  // These serve PNG bytes to an img src rather than JSON, which is why they are URL builders: a
  // document's own imageUrl/matteUrl already point here, and this is the same route for a caller
  // holding only ids.
  it('builds the layer pixel URL and the matte URL', () => {
    expect(imageLayerUrl(PROJECT, DOCUMENT, LAYER)).toBe(LAYER_URL + '/image');
    expect(imageLayerMatteUrl(PROJECT, DOCUMENT, LAYER)).toBe(LAYER_URL + '/matte');
  });
});
