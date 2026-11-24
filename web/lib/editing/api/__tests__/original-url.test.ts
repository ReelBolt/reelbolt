/** @vitest-environment node */
/**
 * Seed 1 / PA-2: the original bytes of a pool source must come from a route that streams media.
 * `files/{id}/content` is the text editor's route and answers 409 for any media file, which left
 * every audio clip silent and every proxy-less video black.
 */

import { describe, expect, it } from 'vitest';
import { originalContentUrl } from '../media';

describe('originalContentUrl', () => {
  it('reads a project file through the inline, Range-aware media route', () => {
    expect(originalContentUrl('p1', 'file:abc')).toBe('/api/v1/projects/p1/files/abc/media');
  });

  it('reads a step result through the outputs download route', () => {
    expect(originalContentUrl('p1', 'step_result:xyz')).toBe('/api/v1/projects/p1/outputs/xyz/download');
  });

  it('never points at the text-editor content route', () => {
    expect(originalContentUrl('p1', 'file:abc')).not.toMatch(/\/content$/);
  });

  it('refuses tokens it does not know', () => {
    expect(originalContentUrl('p1', 'nope:abc')).toBeNull();
    expect(originalContentUrl('p1', 'file:')).toBeNull();
    expect(originalContentUrl('p1', 'abc')).toBeNull();
  });
});
