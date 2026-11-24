// @vitest-environment jsdom

/**
 * The trademark credit line. The sentence is pinned verbatim (it must read the same as the
 * marketing site's footer), and the registered-mark symbol is pinned out: the mark is an
 * application, not a registration.
 */
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TrademarkNotice } from './TrademarkNotice';

// MantineProvider reads the colour scheme through matchMedia, which jsdom does not implement.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => false,
  }),
});

afterEach(cleanup);

describe('TrademarkNotice', () => {
  it('prints the pinned sentence with the unregistered-mark symbol', () => {
    render(
      <MantineProvider>
        <TrademarkNotice />
      </MantineProvider>,
    );
    expect(screen.getByText('ReelBolt™ is a trademark of Tommaso Celano.')).toBeTruthy();
  });

  it('never uses the registered-mark symbol', () => {
    render(
      <MantineProvider>
        <TrademarkNotice />
      </MantineProvider>,
    );
    expect(screen.getByText(/trademark/).textContent).not.toMatch(/®/);
  });
});
