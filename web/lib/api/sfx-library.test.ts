import { describe, expect, it } from 'vitest';
import { sfxLibraryUrl, sfxPreviewUrl } from './sfx-library';

describe('sfx-library urls', () => {
  it('builds the list query, trimming and skipping empty filters', () => {
    expect(sfxLibraryUrl({ search: ' boom ', category: 'impact', maxDurationMs: 3000, page: 2 }))
      .toBe('/api/v1/sfx-library?query=boom&category=impact&maxDurationMs=3000&page=2&pageSize=50');
    expect(sfxLibraryUrl({ search: '', category: null, maxDurationMs: null, page: 1 }))
      .toBe('/api/v1/sfx-library?page=1&pageSize=50');
  });
  it('points the preview at the stream endpoint', () => {
    expect(sfxPreviewUrl('abc')).toBe('/api/v1/sfx-library/abc/preview');
  });
});
