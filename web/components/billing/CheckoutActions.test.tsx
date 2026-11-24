// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CheckoutActions } from './CheckoutActions';

describe('CheckoutActions', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
  });

  afterEach(cleanup);

  it('offers the billing page and the dashboard, with basePath-relative hrefs', () => {
    render(
      <MantineProvider>
        <CheckoutActions />
      </MantineProvider>,
    );

    // No '/app' prefix: next.config.ts sets basePath, and Next prepends it at render time. A
    // hardcoded '/app/...' here would resolve to '/app/app/...'.
    expect(screen.getByRole('link', { name: 'Check your plan and balance' }).getAttribute('href')).toBe(
      '/settings/billing',
    );
    expect(screen.getByRole('link', { name: 'Go to the dashboard' }).getAttribute('href')).toBe(
      '/dashboard',
    );
  });
});
