import { describe, expect, it } from 'vitest';
import { DEFAULT_FILTERS } from '@/components/marketplace/marketplace-model';
import { meUrl, posterUrl, previewUrl, recipesUrl } from './marketplace';

describe('recipesUrl: filter, sort and search state to request', () => {
  it('sends only a limit for the default state, on the anonymous prefix', () => {
    expect(recipesUrl(DEFAULT_FILTERS)).toBe('/api/v1/public/marketplace/recipes?limit=24');
  });

  it('maps every filter to its query parameter and trims the search text', () => {
    const url = new URL(
      recipesUrl(
        { q: '  launch reel ', category: 'promo', cost: 'Uses credits and a voice', stepType: 'Narration', language: 'it', sort: 'rated' },
        'abc',
        12,
      ),
      'http://x',
    );
    expect(url.pathname).toBe('/api/v1/public/marketplace/recipes');
    expect(Object.fromEntries(url.searchParams)).toEqual({
      q: 'launch reel',
      category: 'promo',
      cost: 'Uses credits and a voice',
      stepType: 'Narration',
      language: 'it',
      sort: 'rated',
      cursor: 'abc',
      limit: '12',
    });
  });

  it('leaves the default sort and empty filters out', () => {
    const params = new URL(recipesUrl({ ...DEFAULT_FILTERS, sort: 'copied' }), 'http://x').searchParams;
    expect(params.get('sort')).toBe('copied');
    expect(params.has('q')).toBe(false);
    expect(params.has('category')).toBe(false);
  });
});

describe('recipe URLs', () => {
  it('derives poster and preview from the id; the signed-in call is off the public prefix', () => {
    expect(posterUrl('r1')).toBe('/api/v1/public/marketplace/recipes/r1/poster');
    expect(previewUrl('r1')).toBe('/api/v1/public/marketplace/recipes/r1/preview');
    expect(meUrl('r1')).toBe('/api/v1/marketplace/recipes/r1/me');
  });
});
