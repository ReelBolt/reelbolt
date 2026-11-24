// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TransactionReference } from './TransactionReference';

describe('TransactionReference', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
  });

  afterEach(cleanup);

  it('shows the reference verbatim, with a way to copy it', () => {
    render(
      <MantineProvider>
        <TransactionReference value="txn_01m4f0t4pwhjqjt09z5cfchz3m" />
      </MantineProvider>,
    );

    expect(screen.getByTestId('checkout-transaction-id').textContent).toBe(
      'txn_01m4f0t4pwhjqjt09z5cfchz3m',
    );
    expect(screen.getByRole('button', { name: 'Copy reference' })).toBeTruthy();
  });
});
