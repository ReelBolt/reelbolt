import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FILTERS,
  hasActiveFilters,
  isSort,
  previewAllowed,
  ratingDisplay,
  signInHref,
} from './marketplace-model';

describe('ratingDisplay', () => {
  it('says "new" below five ratings, whatever the average', () => {
    expect(ratingDisplay({ ratingAverage: 5, ratingCount: 4 })).toEqual({ kind: 'new' });
    expect(ratingDisplay({ ratingAverage: null, ratingCount: 0 })).toEqual({ kind: 'new' });
  });
  it('shows the average from five ratings on', () => {
    expect(ratingDisplay({ ratingAverage: 4.2, ratingCount: 5 })).toEqual({ kind: 'average', average: 4.2, count: 5 });
  });
  it('never shows an average the server did not send', () => {
    expect(ratingDisplay({ ratingAverage: null, ratingCount: 9 })).toEqual({ kind: 'new' });
  });
});

describe('previewAllowed', () => {
  it('plays only with a fine pointer and without a reduced-motion preference', () => {
    expect(previewAllowed({ coarsePointer: false, reducedMotion: false })).toBe(true);
    expect(previewAllowed({ coarsePointer: true, reducedMotion: false })).toBe(false);
    expect(previewAllowed({ coarsePointer: false, reducedMotion: true })).toBe(false);
  });
});

describe('filters', () => {
  it('knows when a filter is active (the sort is not a filter)', () => {
    expect(hasActiveFilters(DEFAULT_FILTERS)).toBe(false);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, sort: 'rated' })).toBe(false);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, q: ' x ' })).toBe(true);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, language: 'it' })).toBe(true);
  });
  it('accepts only the three sorts', () => {
    expect(['new', 'copied', 'rated'].every(isSort)).toBe(true);
    expect(isSort('price')).toBe(false);
  });
});

describe('signInHref', () => {
  it('returns to the same recipe, with the app base path', () => {
    expect(signInHref('/community/3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b')).toBe(
      '/login?next=%2Fapp%2Fcommunity%2F3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b',
    );
  });
  it('falls back to the gallery for a missing or odd path', () => {
    expect(signInHref(null)).toBe('/login?next=%2Fapp%2Fcommunity');
    expect(signInHref('//evil')).toBe('/login?next=%2Fapp%2Fcommunity');
  });
});
