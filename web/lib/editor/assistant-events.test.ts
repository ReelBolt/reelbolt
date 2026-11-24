// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  IMAGE_DOCUMENT_CHANGED_EVENT,
  announceImageDocumentChange,
  onImageDocumentChange,
  parseImageDocumentChange,
  resetAnnouncedImageDocumentChanges,
  type ImageDocumentChange,
} from './assistant-events';

const change = (over: Partial<ImageDocumentChange> = {}): ImageDocumentChange => ({
  changeId: 'c1',
  projectId: 'p',
  documentId: 'd',
  kind: 'layersAdded',
  layerIds: ['l1'],
  summary: 'Added a title.',
  ...over,
});

beforeEach(() => resetAnnouncedImageDocumentChanges());
afterEach(() => resetAnnouncedImageDocumentChanges());

describe('announcing a picture change', () => {
  it('reaches every listener as a window event, once per change id', () => {
    const heard = vi.fn();
    const raw = vi.fn();
    const stop = onImageDocumentChange(heard);
    window.addEventListener(IMAGE_DOCUMENT_CHANGED_EVENT, raw);

    expect(announceImageDocumentChange(change())).toBe(true);
    expect(announceImageDocumentChange(change())).toBe(false);
    expect(announceImageDocumentChange(change({ changeId: 'c2', kind: 'layersChanged' }))).toBe(true);

    expect(heard.mock.calls.map(([c]) => c.changeId)).toEqual(['c1', 'c2']);
    expect(raw).toHaveBeenCalledTimes(2);
    stop();
    window.removeEventListener(IMAGE_DOCUMENT_CHANGED_EVENT, raw);
  });

  it('stops delivering once the listener unsubscribes', () => {
    const heard = vi.fn();
    const stop = onImageDocumentChange(heard);
    stop();
    announceImageDocumentChange(change());
    expect(heard).not.toHaveBeenCalled();
  });

  it('never announces a change without an id', () => {
    const heard = vi.fn();
    const stop = onImageDocumentChange(heard);
    expect(announceImageDocumentChange(change({ changeId: '' }))).toBe(false);
    expect(heard).not.toHaveBeenCalled();
    stop();
  });
});

describe('reading the data frame', () => {
  it('reads a well-formed frame, keeping only string layer ids', () => {
    expect(
      parseImageDocumentChange({
        changeId: 'c1',
        projectId: 'p',
        documentId: 'd',
        kind: 'graphicQueued',
        layerIds: ['a', 3, null, ''],
        summary: 'Started a badge.',
        graphicId: 'g1',
      }),
    ).toEqual({
      changeId: 'c1',
      projectId: 'p',
      documentId: 'd',
      kind: 'graphicQueued',
      layerIds: ['a'],
      summary: 'Started a badge.',
      graphicId: 'g1',
    });
  });

  it('tolerates missing layer ids and summary', () => {
    expect(parseImageDocumentChange({ changeId: 'c', projectId: 'p', documentId: 'd', kind: 'documentChanged' })).toEqual({
      changeId: 'c',
      projectId: 'p',
      documentId: 'd',
      kind: 'documentChanged',
      layerIds: [],
      summary: '',
    });
  });

  it('refuses anything that is not a change it knows', () => {
    expect(parseImageDocumentChange(null)).toBeNull();
    expect(parseImageDocumentChange('x')).toBeNull();
    expect(parseImageDocumentChange({ changeId: 'c', projectId: 'p', documentId: 'd', kind: 'exploded' })).toBeNull();
    expect(parseImageDocumentChange({ projectId: 'p', documentId: 'd', kind: 'layersAdded' })).toBeNull();
  });
});
