import { beforeEach, describe, expect, it, vi } from 'vitest';

const getImageDocuments = vi.fn();
const createImageDocument = vi.fn();
vi.mock('@/lib/api/image-documents', () => ({
  getImageDocuments: (...a: unknown[]) => getImageDocuments(...a),
  createImageDocument: (...a: unknown[]) => createImageDocument(...a),
}));

import { canEditAsThumbnail, documentForImage, thumbnailEditorHref } from './open-thumbnail-editor';

const summary = (id: string, sourceProjectFileId: string, updatedAt: string) => ({
  id, name: null, sourceProjectFileId, revision: 1, layerCount: 1, createdAt: updatedAt, updatedAt,
});

beforeEach(() => {
  getImageDocuments.mockReset();
  createImageDocument.mockReset();
});

describe('thumbnailEditorHref', () => {
  it('carries the project as a query parameter and encodes both ids', () => {
    expect(thumbnailEditorHref('p1', 'd1')).toBe('/editor/thumbnails/d1?project=p1');
    expect(thumbnailEditorHref('p/1', 'd 1')).toBe('/editor/thumbnails/d%201?project=p%2F1');
  });
});

describe('canEditAsThumbnail', () => {
  it('accepts images only', () => {
    expect(canEditAsThumbnail({ mimeType: 'image/png' })).toBe(true);
    expect(canEditAsThumbnail({ mimeType: 'IMAGE/JPEG' })).toBe(true);
    expect(canEditAsThumbnail({ mimeType: 'video/mp4' })).toBe(false);
    expect(canEditAsThumbnail({ mimeType: '' })).toBe(false);
  });
});

describe('documentForImage', () => {
  const file = { id: 'f1', originalFileName: 'cover.png' };

  it('reuses the most recently updated document already opened on that image', async () => {
    getImageDocuments.mockResolvedValue([
      summary('old', 'f1', '2026-10-01T00:00:00Z'),
      summary('other', 'f2', '2026-10-09T00:00:00Z'),
      summary('new', 'f1', '2026-10-05T00:00:00Z'),
    ]);
    await expect(documentForImage('p', file)).resolves.toBe('new');
    expect(createImageDocument).not.toHaveBeenCalled();
  });

  it('creates a document named after the file when there is none', async () => {
    getImageDocuments.mockResolvedValue([summary('other', 'f2', '2026-10-09T00:00:00Z')]);
    createImageDocument.mockResolvedValue({ id: 'created' });
    await expect(documentForImage('p', file, () => Promise.resolve(null))).resolves.toBe('created');
    expect(createImageDocument).toHaveBeenCalledWith('p', { sourceProjectFileId: 'f1', name: 'cover.png' });
  });

  it('sends the size this browser decoded, so the canvas opens at the photo\'s shape even if the server cannot read it', async () => {
    getImageDocuments.mockResolvedValue([]);
    createImageDocument.mockResolvedValue({ id: 'created' });
    const measure = vi.fn().mockResolvedValue({ width: 1080, height: 1920 });
    await expect(documentForImage('p', file, measure)).resolves.toBe('created');
    expect(measure).toHaveBeenCalledWith('/api/v1/projects/p/files/f1/media');
    expect(createImageDocument).toHaveBeenCalledWith('p', {
      sourceProjectFileId: 'f1',
      name: 'cover.png',
      width: 1080,
      height: 1920,
    });
  });

  it('still creates the document when the browser cannot decode the photo', async () => {
    getImageDocuments.mockResolvedValue([]);
    createImageDocument.mockResolvedValue({ id: 'created' });
    await documentForImage('p', file, () => Promise.resolve(null));
    expect(createImageDocument).toHaveBeenCalledWith('p', { sourceProjectFileId: 'f1', name: 'cover.png' });
  });

  it('does not decode the photo when a document already exists', async () => {
    getImageDocuments.mockResolvedValue([summary('old', 'f1', '2026-10-01T00:00:00Z')]);
    const measure = vi.fn();
    await documentForImage('p', file, measure);
    expect(measure).not.toHaveBeenCalled();
  });

  it('lets a failure to list or create reach the caller', async () => {
    getImageDocuments.mockRejectedValue(new Error('offline'));
    await expect(documentForImage('p', file)).rejects.toThrow('offline');
  });
});
