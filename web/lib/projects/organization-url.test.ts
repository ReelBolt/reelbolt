import { describe, expect, it } from 'vitest';
import { parseOrganizationParams, toggleTag, withOrganizationParams } from './organization-url';

describe('organization url state', () => {
  it('defaults to all folders and no tags', () => {
    expect(parseOrganizationParams(new URLSearchParams(''))).toEqual({ folder: 'all', tagIds: [] });
    expect(parseOrganizationParams(new URLSearchParams('folder=all'))).toEqual({ folder: 'all', tagIds: [] });
  });
  it('parses folder, unfiled and repeated tags without duplicates', () => {
    expect(parseOrganizationParams(new URLSearchParams('folder=unfiled&tag=a&tag=b&tag=a'))).toEqual({ folder: 'unfiled', tagIds: ['a', 'b'] });
    expect(parseOrganizationParams(new URLSearchParams('folder=f1')).folder).toBe('f1');
  });
  it('writes state, keeps other params and omits defaults', () => {
    const out = withOrganizationParams(new URLSearchParams('q=x&folder=old&tag=z'), { folder: 'f1', tagIds: ['a', 'b'] });
    expect(out.toString()).toBe('q=x&folder=f1&tag=a&tag=b');
    expect(withOrganizationParams(out, { folder: 'all', tagIds: [] }).toString()).toBe('q=x');
  });
  it('toggles a tag', () => {
    expect(toggleTag(['a'], 'b')).toEqual(['a', 'b']);
    expect(toggleTag(['a', 'b'], 'a')).toEqual(['b']);
  });
});
