import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import { createImageGraphic, getImageGraphic, getImageGraphics, graphicErrorOf, imageGraphicsUrl } from './image-documents';
import type { EditImageLayer, ImageGraphicJob } from '../types/image-document';

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const fetchMock = vi.fn();
beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => vi.unstubAllGlobals());

const designed = {
  id: 'l9',
  kind: 'Generated',
  label: 'Sale badge',
  provenance: { operation: 'designGraphic', workspaceVersionId: 'v1' },
} as unknown as EditImageLayer;

const job = (over: Partial<ImageGraphicJob> = {}): ImageGraphicJob => ({
  id: 'g1',
  documentId: 'd',
  executionId: 'e',
  workflowId: 'w',
  status: 'Passed',
  description: 'A sale badge',
  frameWidth: 400,
  frameHeight: 200,
  placement: 'topRight',
  target: { x: 1, y: 2, width: 3, height: 4 },
  revisesLayerId: null,
  layerId: 'l9',
  layer: designed,
  summary: 'A red badge.',
  error: null,
  createdAt: '',
  completedAt: '',
  ...over,
});

describe('design jobs over the wire', () => {
  it('reads the list from the document route and normalises each inserted layer', async () => {
    fetchMock.mockResolvedValueOnce(json({ graphics: [job()], estimate: null, available: true, unavailableReason: null }));
    const result = await getImageGraphics('p', 'd');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/projects/p/image-documents/d/graphics');
    expect(imageGraphicsUrl('p', 'd')).toBe('/api/v1/projects/p/image-documents/d/graphics');
    expect(result.graphics[0].layer?.kind).toBe('generated');
    expect(result.available).toBe(true);
  });

  it('reads a sparse answer safely', async () => {
    fetchMock.mockResolvedValueOnce(json({}));
    expect(await getImageGraphics('p', 'd')).toEqual({ graphics: [], estimate: null, available: true, unavailableReason: null });
  });

  it('posts the request as it is and answers the job', async () => {
    fetchMock.mockResolvedValueOnce(json(job({ status: 'Queued', layer: null, layerId: null }), 201));
    const body = { description: 'A badge', placement: 'top' as const, canvasWidth: 1280, canvasHeight: 720 };
    const created = await createImageGraphic('p', 'd', body);
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'POST' });
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toEqual(body);
    expect(created.status).toBe('Queued');
  });

  it('reads one job', async () => {
    fetchMock.mockResolvedValueOnce(json(job()));
    const one = await getImageGraphic('p', 'd', 'g1');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/projects/p/image-documents/d/graphics/g1');
    expect(one.layer?.kind).toBe('generated');
  });
});

describe('a refused design', () => {
  const refuse = async (status: number, body: unknown) => {
    fetchMock.mockResolvedValueOnce(json(body, status));
    try {
      await createImageGraphic('p', 'd', { description: 'x' });
    } catch (cause) {
      return cause;
    }
    throw new Error('expected a refusal');
  };

  it('keeps the shortfall of a 402', async () => {
    const err = graphicErrorOf(
      await refuse(402, { code: 'INSUFFICIENT_CREDITS', message: 'You need 30 credits.', required: 30, balance: 4, low: 12, high: 30 }),
    );
    expect(err).toMatchObject({ status: 402, code: 'INSUFFICIENT_CREDITS', message: 'You need 30 credits.', required: 30, balance: 4, unavailable: false });
  });

  it('keeps the wait of a 429', async () => {
    const err = graphicErrorOf(await refuse(429, { code: 'GRAPHIC_RATE_LIMITED', message: 'Wait a minute.', retryAfterSeconds: 60 }));
    expect(err).toMatchObject({ code: 'GRAPHIC_RATE_LIMITED', message: 'Wait a minute.', retryAfterSeconds: 60 });
  });

  it('knows when this installation cannot design at all', async () => {
    expect(graphicErrorOf(await refuse(503, { code: 'GRAPHIC_AGENT_UNAVAILABLE', message: 'No designer.' })).unavailable).toBe(true);
    expect(graphicErrorOf(await refuse(409, { code: 'GRAPHIC_UNAVAILABLE', message: 'Off.' })).unavailable).toBe(true);
    const notRevisable = graphicErrorOf(await refuse(409, { code: 'GRAPHIC_NOT_REVISABLE', message: 'Cannot change it.' }));
    expect(notRevisable).toMatchObject({ code: 'GRAPHIC_NOT_REVISABLE', message: 'Cannot change it.', unavailable: false });
  });

  it('shows a 400 validation message verbatim', async () => {
    const err = graphicErrorOf(await refuse(400, { message: 'The description is too long.' }));
    expect(err.message).toBe('The description is too long.');
    expect(err.code).toBeNull();
  });

  it('reads anything else by its message only', () => {
    expect(graphicErrorOf(new Error('offline'))).toMatchObject({ status: null, code: null, message: 'offline' });
    expect(graphicErrorOf('weird').message).toBe('The graphic could not be started.');
    expect(graphicErrorOf(new ApiError('', 500)).message).toBe('The graphic could not be started.');
  });
});
