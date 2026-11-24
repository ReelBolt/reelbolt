// @vitest-environment jsdom
/**
 * The exits from /accept-terms. S-22: the data link goes to /settings/data, the only account page
 * the proxy's legal gate exempts; /settings itself stays gated, so a link there would bounce back.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ logout: vi.fn() }) }));

import { LegalGateExits } from './LegalGateExits';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

afterEach(cleanup);

describe('LegalGateExits', () => {
  it('links data export and deletion to /settings/data, under the /app basePath', () => {
    render(
      <MantineProvider>
        <LegalGateExits />
      </MantineProvider>,
    );
    const link = screen.getByRole('link', { name: 'Download your data or delete your account' });
    expect(link.getAttribute('href')).toBe('/app/settings/data');
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy();
  });
});
