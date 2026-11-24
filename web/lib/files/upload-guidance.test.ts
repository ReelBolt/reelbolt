import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { NETWORK_FAILURE_MESSAGE, plainErrorMessage } from '@/lib/api/network-error';
import { explainUploadError, findDuplicates, isUnusualType, withUniqueNames } from './upload-guidance';
import { parseFileSummary } from './summary';

const pending = (name: string, type = '') => ({ file: new File(['x'], name, { type }), relativePath: name });

describe('upload guidance', () => {
  it('finds a same-name file in the target folder only', () => {
    const existing = [{ originalFileName: 'notes.txt', originalPath: 'notes.txt' }, { originalFileName: 'a.mp4', originalPath: 'clips/a.mp4' }];
    expect(findDuplicates([pending('notes.txt'), pending('b.txt')], existing, '')).toHaveLength(1);
    expect(findDuplicates([pending('notes.txt')], existing, 'clips')).toHaveLength(0);
    expect(findDuplicates([pending('A.mp4')], existing, 'clips')).toHaveLength(1);
  });
  it('flags an installer but not media or text', () => {
    expect(isUnusualType(pending('setup.exe', 'application/x-msdownload'))).toBe(true);
    expect(isUnusualType(pending('a.mp4', 'video/mp4'))).toBe(false);
    expect(isUnusualType(pending('notes.md'))).toBe(false);
  });
  it('gives a 413, the bare "Upload failed" and a network failure each a cause', () => {
    expect(explainUploadError(new ApiError('Upload failed', 413, []))).toMatch(/too large/);
    expect(explainUploadError(new Error('Upload failed'))).toMatch(/did not finish/);
    expect(explainUploadError(new TypeError('Failed to fetch'))).toBe(NETWORK_FAILURE_MESSAGE);
    expect(explainUploadError(new ApiError('Not allowed', 403, []))).toBe('Not allowed');
  });
  it('turns "Failed to fetch" into a plain message', () => {
    expect(plainErrorMessage(new TypeError('Failed to fetch'))).toBe(NETWORK_FAILURE_MESSAGE);
    expect(plainErrorMessage(new Error('Name taken'))).toBe('Name taken');
  });
  it('renders a JSON summary as prose', () => {
    const s = parseFileSummary('{"summary":"A design doc.","video_generation_relevance":"High."}');
    expect(s.text).toBe('A design doc.');
    expect(s.extras).toEqual([{ label: 'Video generation relevance', text: 'High.' }]);
    expect(parseFileSummary('Plain words.')).toEqual({ text: 'Plain words.', extras: [] });
  });
  it('numbers a kept duplicate before the extension, unique in the folder and among pending files', () => {
    const existing = [{ originalFileName: 'notes.txt', originalPath: 'notes.txt' }, { originalFileName: 'notes (2).txt', originalPath: 'notes (2).txt' }];
    const out = withUniqueNames([pending('Notes.txt'), pending('Notes.txt'), pending('new.txt')], existing, '');
    expect(out.map((o) => o.file.name)).toEqual(['Notes (3).txt', 'Notes.txt'.replace('Notes.txt', 'Notes (4).txt'), 'new.txt']);
  });
  it.each([
    'Failed to fetch', 'NetworkError when attempting to fetch resource.', 'Load failed', 'The network connection was lost.',
    'The Internet connection appears to be offline.', 'Network request failed',
  ])('recognises the network failure message %s', (m) => {
    expect(plainErrorMessage(new TypeError(m))).toBe(NETWORK_FAILURE_MESSAGE);
  });
  it('treats a TypeError "cancelled" as network but not a plain Error or "Upload failed"', () => {
    expect(plainErrorMessage(new TypeError('cancelled'))).toBe(NETWORK_FAILURE_MESSAGE);
    expect(plainErrorMessage(new Error('cancelled'))).toBe('cancelled');
    expect(plainErrorMessage(new Error('Upload failed'))).toBe('Upload failed');
  });
});
