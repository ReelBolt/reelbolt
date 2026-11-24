import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import {
  createImageExport,
  createTextLayer,
  documentPatchBody,
  detectSubjects,
  errorCodeOf,
  getOperationCapabilities,
  getImageDocument,
  isOperationUnavailable,
  isRevisionConflict,
  putBlobToStorage,
  runImageOperation,
  segmentSubjects,
  sourceTooLarge,
  updateImageDocument,
  updateImageLayer,
  uploadEditorAsset,
} from './image-documents';
import type { EditImageLayer } from '../types/image-document';
import { defaultTextContent } from '../editor/text-content';

const layer = (over: Partial<EditImageLayer> = {}): EditImageLayer => ({
  id: 'l1',
  zOrder: 1,
  role: 'subject',
  kind: 'Cutout' as unknown as EditImageLayer['kind'],
  label: 'Person',
  sourceProjectFileId: null,
  imageUrl: '/i',
  matteUrl: null,
  mimeType: 'image/png',
  width: 10,
  height: 10,
  opacity: 1,
  transform: { x: 0, y: 0, scale: 1, rotation: 0 },
  segmentationModel: null,
  segmentationPrompt: null,
  createdAt: '2026-10-10T00:00:00Z',
  ...over,
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const fetchMock = vi.fn();
beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => vi.unstubAllGlobals());

const lastCall = () => {
  const [url, init] = fetchMock.mock.calls[fetchMock.mock.calls.length - 1] as [string, RequestInit];
  return { url, init, body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined };
};

describe('wire normalisation', () => {
  it('reads the server\'s PascalCase layer kind as the web\'s lower-case union', async () => {
    fetchMock.mockResolvedValue(
      json({ id: 'd', projectId: 'p', name: null, sourceProjectFileId: 's', revision: 3, createdAt: '', updatedAt: '', layers: [layer(), layer({ id: 'l2', kind: 'Text' as never })] }),
    );
    const doc = await getImageDocument('p', 'd');
    expect(doc.layers.map((l) => l.kind)).toEqual(['cutout', 'text']);
  });

  it('answers a single layer from the extended subjects route as a one-element list, and an array as an array', async () => {
    fetchMock.mockResolvedValueOnce(json(layer()));
    expect((await segmentSubjects('p', 'd', { prompt: 'the person' })).map((l) => l.id)).toEqual(['l1']);
    fetchMock.mockResolvedValueOnce(json([layer(), layer({ id: 'l2' })]));
    expect((await segmentSubjects('p', 'd', { returnMultipleMasks: true })).map((l) => l.id)).toEqual(['l1', 'l2']);
  });

  it('answers detectSubjects as the array the route returns, one layer per mask', async () => {
    fetchMock.mockResolvedValueOnce(json([layer({ id: 'first' }), layer({ id: 'second' })]));
    expect((await detectSubjects('p', 'd', { prompt: 'x' })).map((l) => l.id)).toEqual(['first', 'second']);
  });
});

describe('request shapes', () => {
  it('posts point and box prompts under the contract\'s names', async () => {
    fetchMock.mockResolvedValue(json([layer()]));
    await segmentSubjects('p', 'd', {
      pointPrompts: [{ x: 10, y: 20, label: 1 }],
      boxPrompts: [{ xMin: 1, yMin: 2, xMax: 3, yMax: 4 }],
      returnMultipleMasks: true,
      maxMasks: 3,
    });
    const { url, init, body } = lastCall();
    expect(url).toBe('/api/v1/projects/p/image-documents/d/subjects');
    expect(init.method).toBe('POST');
    expect(body).toEqual({
      pointPrompts: [{ x: 10, y: 20, label: 1 }],
      boxPrompts: [{ xMin: 1, yMin: 2, xMax: 3, yMax: 4 }],
      returnMultipleMasks: true,
      maxMasks: 3,
    });
  });

  it('patches the document with canvas size, background and baseRevision', async () => {
    fetchMock.mockResolvedValue(json({ id: 'd', layers: [] }));
    await updateImageDocument('p', 'd', { canvasWidth: 1080, canvasHeight: 1080, backgroundColor: null, baseRevision: 4 });
    const { url, init, body } = lastCall();
    expect(url).toBe('/api/v1/projects/p/image-documents/d');
    expect(init.method).toBe('PATCH');
    // null (transparent) goes out as the empty string, the server's way of clearing a colour
    expect(body).toEqual({ canvasWidth: 1080, canvasHeight: 1080, backgroundColor: '', baseRevision: 4 });
  });

  it('patches a layer with the extended fields', async () => {
    fetchMock.mockResolvedValue(json(layer()));
    await updateImageLayer('p', 'd', 'l1', { visible: false, locked: true, blendMode: 'multiply', label: 'x', zOrder: 2 });
    expect(lastCall().body).toEqual({ visible: false, locked: true, blendMode: 'multiply', label: 'x', zOrder: 2 });
    expect(lastCall().url).toBe('/api/v1/projects/p/image-documents/d/layers/l1');
  });

  it('sends text content in wire form: no stroke/shadow/maxWidth keys when absent', async () => {
    fetchMock.mockResolvedValue(json(layer({ kind: 'text' })));
    await createTextLayer('p', 'd', {
      content: defaultTextContent({ stroke: null, shadow: null, maxWidth: null }),
      transform: { x: 40, y: 40 },
    });
    const { url, body } = lastCall();
    expect(url).toBe('/api/v1/projects/p/image-documents/d/layers/text');
    expect(body.content).not.toHaveProperty('stroke');
    expect(body.content).not.toHaveProperty('shadow');
    expect(body.content).not.toHaveProperty('maxWidth');
    expect(body.transform).toEqual({ x: 40, y: 40 });
  });

  it('posts an operation and normalises the layers it answers with', async () => {
    fetchMock.mockResolvedValue(
      json({ layers: [layer({ kind: 'Generated' as never })], operation: 'upscale', provider: { kind: 'FalAi', model: 'm' }, usage: { unit: 'image', quantity: 1 } }),
    );
    const result = await runImageOperation('p', 'd', { operation: 'upscale', layerId: 'l1', params: { factor: 2 } });
    expect(lastCall().url).toBe('/api/v1/projects/p/image-documents/d/operations');
    expect(lastCall().body).toEqual({ operation: 'upscale', layerId: 'l1', params: { factor: 2 } });
    expect(result.layers[0].kind).toBe('generated');
    expect(result.provider).toEqual({ kind: 'FalAi', model: 'm' });
  });

  it('registers an export with the uploaded key', async () => {
    fetchMock.mockResolvedValue(json({ export: {}, projectFile: {} }));
    await createImageExport('p', 'd', { key: 'k', width: 1280, height: 720, fileName: 'thumb.png' });
    expect(lastCall().url).toBe('/api/v1/projects/p/image-documents/d/exports');
    expect(lastCall().body).toEqual({ key: 'k', width: 1280, height: 720, fileName: 'thumb.png' });
  });
});

describe('documentPatchBody: the server\'s clearing rules', () => {
  it('turns a null background into "" (clear) and leaves a colour alone', () => {
    expect(documentPatchBody({ backgroundColor: null })).toEqual({ backgroundColor: '' });
    expect(documentPatchBody({ backgroundColor: '#112233' })).toEqual({ backgroundColor: '#112233' });
  });

  it('sends the two sides of the canvas together, completing a lone one from the current size', () => {
    expect(documentPatchBody({ canvasWidth: 1920 }, { canvasWidth: 1280, canvasHeight: 720 })).toEqual({
      canvasWidth: 1920,
      canvasHeight: 720,
    });
    expect(documentPatchBody({ canvasWidth: 0, canvasHeight: 0 })).toEqual({ canvasWidth: 0, canvasHeight: 0 });
  });

  it('passes name and baseRevision through untouched and omits what was not given', () => {
    expect(documentPatchBody({ name: '', baseRevision: 7 })).toEqual({ name: '', baseRevision: 7 });
  });
});

describe('presigned upload', () => {
  const ticket = { key: 'projects/p/agentFiles/image-editor/d/mask-1.png', url: 'https://r2.example/put?sig=1', headers: { 'Content-Type': 'image/png', 'x-amz-meta': 'a' }, expiresAt: '' };

  it('PUTs the blob to the signed URL with the ticket headers and no credentials', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 200 }));
    const blob = new Blob(['png'], { type: 'image/png' });
    await putBlobToStorage(ticket, blob);
    const { url, init } = lastCall();
    expect(url).toBe(ticket.url);
    expect(init.method).toBe('PUT');
    expect(init.headers).toEqual(ticket.headers);
    expect(init.body).toBe(blob);
    expect(init.credentials).toBe('omit');
  });

  it('reports a refusal and a network failure in plain words', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 403 }));
    await expect(putBlobToStorage(ticket, new Blob(['x']))).rejects.toThrow('HTTP 403');
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    await expect(putBlobToStorage(ticket, new Blob(['x']))).rejects.toThrow('could not reach storage');
  });

  it('asks for a ticket sized to the blob, uploads, and answers the key', async () => {
    fetchMock.mockResolvedValueOnce(json(ticket));
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 200 }));
    const blob = new Blob(['12345'], { type: 'image/png' });
    await expect(uploadEditorAsset('p', 'd', 'mask', blob)).resolves.toBe(ticket.key);
    const first = JSON.parse((fetchMock.mock.calls[0][1] as RequestInit).body as string);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/projects/p/image-documents/d/assets/upload-url');
    expect(first).toEqual({ purpose: 'mask', contentType: 'image/png', contentLength: 5 });
    expect(fetchMock.mock.calls[1][0]).toBe(ticket.url);
  });

  it('does not PUT when the ticket request itself is refused', async () => {
    fetchMock.mockResolvedValueOnce(json({ code: 'EXPORT_KEY_INVALID', message: 'no' }, 400));
    await expect(uploadEditorAsset('p', 'd', 'export', new Blob(['x']))).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('error helpers', () => {
  const err = (status: number, body: unknown) => new ApiError('msg', status, [], body);

  it('reads the code of an error body', () => {
    expect(errorCodeOf(err(409, { code: 'REVISION_CONFLICT' }))).toBe('REVISION_CONFLICT');
    expect(errorCodeOf(err(500, { message: 'x' }))).toBeNull();
    expect(errorCodeOf(new Error('x'))).toBeNull();
  });

  it('treats a 409 with the conflict code, or with no code, as a revision conflict', () => {
    expect(isRevisionConflict(err(409, { code: 'REVISION_CONFLICT' }))).toBe(true);
    expect(isRevisionConflict(err(409, { message: 'stale' }))).toBe(true);
    expect(isRevisionConflict(err(409, { code: 'SEGMENTATION_PROVIDER_UNAVAILABLE' }))).toBe(false);
    expect(isRevisionConflict(err(500, {}))).toBe(false);
  });

  it('recognises an unavailable operation by its code, whichever capability is missing', () => {
    expect(isOperationUnavailable(err(409, { code: 'OPERATION_UNSUPPORTED' }))).toBe(true);
    expect(isOperationUnavailable(err(409, { code: 'IMAGE_EDIT_PROVIDER_UNAVAILABLE' }))).toBe(true);
    expect(isOperationUnavailable(err(409, { code: 'IMAGE_GENERATION_PROVIDER_UNAVAILABLE' }))).toBe(true);
    expect(isOperationUnavailable(err(409, { code: 'MASK_REQUIRED' }))).toBe(false);
    expect(isOperationUnavailable(err(424, { code: 'OPERATION_TIMEOUT' }))).toBe(false);
    expect(isOperationUnavailable(err(424, { code: 'OPERATION_FAILED' }))).toBe(false);
  });

  it('reads the size of the copy a 422 SOURCE_TOO_LARGE asks for, and nothing else', () => {
    expect(
      sourceTooLarge(err(422, { code: 'SOURCE_TOO_LARGE', message: 'too big', width: 1247, height: 831, source: 'displayed' })),
    ).toEqual({ code: 'SOURCE_TOO_LARGE', message: 'too big', width: 1247, height: 831, source: 'displayed' });
    expect(sourceTooLarge(err(422, { code: 'SOURCE_TOO_LARGE', width: 1500, height: 1000, source: 'smaller' }))?.source).toBe('smaller');
    expect(sourceTooLarge(err(422, { code: 'OPERATION_INVALID', width: 1, height: 1 }))).toBeNull();
    expect(sourceTooLarge(err(422, { code: 'SOURCE_TOO_LARGE' }))).toBeNull();
    expect(sourceTooLarge(err(400, { code: 'SOURCE_TOO_LARGE', width: 1, height: 1 }))).toBeNull();
    expect(sourceTooLarge(new Error('x'))).toBeNull();
  });

  it('asks the capabilities route of the tool, and a vendor failure keeps its plain message through a 424', async () => {
    fetchMock.mockResolvedValueOnce(json({ operation: 'expand', takesPrompt: false, expand: null }));
    await getOperationCapabilities('p', 'expand');
    expect(lastCall().url).toBe('/api/v1/projects/p/image-documents/operations/expand/capabilities');

    fetchMock.mockResolvedValueOnce(json({ operation: 'generate', takesPrompt: true }));
    await getOperationCapabilities('p', 'generate', { documentId: 'd', width: 1280.4, height: 720 }, { subjects: true });
    expect(lastCall().url).toBe(
      '/api/v1/projects/p/image-documents/operations/generate/capabilities?documentId=d&width=1280&height=720&subjects=true',
    );

    fetchMock.mockResolvedValueOnce(json({ operation: 'generate', takesPrompt: true }));
    await getOperationCapabilities('p', 'generate', undefined, { subjects: true });
    expect(lastCall().url).toBe('/api/v1/projects/p/image-documents/operations/generate/capabilities?subjects=true');

    fetchMock.mockResolvedValueOnce(json({ operation: 'generate', takesPrompt: true }));
    await getOperationCapabilities('p', 'generate', { documentId: 'd', width: 10, height: 20 }, { subjects: false });
    expect(lastCall().url).toBe('/api/v1/projects/p/image-documents/operations/generate/capabilities?documentId=d&width=10&height=20');

    fetchMock.mockResolvedValueOnce(
      json({ code: 'OPERATION_FAILED', message: 'The AI service could not expand the picture. It said: too large' }, 424),
    );
    await expect(runImageOperation('p', 'd', { operation: 'expand', layerId: 'l', params: { aspect: '9:16' } })).rejects.toMatchObject({
      status: 424,
      message: 'The AI service could not expand the picture. It said: too large',
    });
  });
});
