import { describe, expect, it } from 'vitest';
import { revalidationFilter, revalidationPrefixes } from './revalidate';

describe('revalidationFilter', () => {
  it('always reaches a project and everything under it', () => {
    const matches = revalidationFilter('workflow');

    expect(matches('/api/v1/projects')).toBe(true);
    expect(matches('/api/v1/projects/p1')).toBe(true);
    expect(matches('/api/v1/projects/p1/files')).toBe(true);
    expect(matches('/api/v1/projects/p1/workflows/w1')).toBe(true);
    expect(matches('/api/v1/projects?archived=false')).toBe(true);
  });

  it('does not reach what merely starts with the same letters, or what is not a url key', () => {
    const matches = revalidationFilter('workflow');

    expect(matches('/api/v1/projectsx')).toBe(false);
    expect(matches('/api/v1/agents')).toBe(false);
    expect(matches(['/api/v1/projects', 'p1'])).toBe(false);
    expect(matches(null)).toBe(false);
    expect(matches(undefined)).toBe(false);
  });

  it.each([
    ['style', '/api/v1/styles'],
    ['agent', '/api/v1/agents'],
    ['connection', '/api/v1/inference-providers'],
    ['run', '/api/v1/billing'],
  ] as const)('a %s change also reaches %s', (kind, prefix) => {
    expect(revalidationFilter(kind)(prefix)).toBe(true);
    expect(revalidationFilter('file')(prefix)).toBe(false);
  });

  it('a kind it does not know still refreshes projects, and nothing else', () => {
    expect(revalidationPrefixes('other')).toEqual(['/api/v1/projects']);
  });
});
