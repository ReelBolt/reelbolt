import { describe, expect, it, vi } from 'vitest';

const notFound = vi.fn(() => {
  throw new Error('NEXT_NOT_FOUND');
});
vi.mock('next/navigation', () => ({ notFound }));
vi.mock('@/components/tags/TagDetailView', () => ({ TagDetailView: () => null }));

describe('tag detail route', () => {
  it('404s on an id that is not a GUID', async () => {
    const { default: Page } = await import('./page');
    await expect(Page({ params: Promise.resolve({ id: '..%2F..%2Fadmin' }) })).rejects.toThrow('NEXT_NOT_FOUND');
    expect(notFound).toHaveBeenCalled();
  });
  it('renders for a GUID', async () => {
    const { default: Page } = await import('./page');
    const el = await Page({ params: Promise.resolve({ id: '3f2b8f0e-1a4c-4d6e-9b7a-0c1d2e3f4a5b' }) });
    expect(el).toBeTruthy();
  });
});
