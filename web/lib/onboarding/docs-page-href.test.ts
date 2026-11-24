import { describe, expect, it } from 'vitest';
import { docsPageHref } from './guide-links';

describe('docsPageHref', () => {
  it('points outside the /app base path', () => {
    expect(docsPageHref('api/', 'en')).toBe('/docs/api/');
    expect(docsPageHref('api/webhooks', 'en')).toBe('/docs/api/webhooks');
    expect(docsPageHref('/api/webhooks', 'it')).toBe('/docs/it/api/webhooks');
  });
});
