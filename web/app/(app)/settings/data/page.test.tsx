// @vitest-environment jsdom
/**
 * /settings/data: the one account page the legal re-acceptance gate exempts (S-22). It must carry
 * the data export and the account deletion, and keep to them.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/components/settings/DataExportCard', () => ({ DataExportCard: () => <div data-testid="data-export-card-stub" /> }));
vi.mock('@/components/settings/DeleteAccountCard', () => ({ DeleteAccountCard: () => <div data-testid="delete-account-card-stub" /> }));

import DataSettingsPage from './page';

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

describe('/settings/data', () => {
  it('holds the data export and the account deletion', () => {
    render(
      <MantineProvider>
        <DataSettingsPage />
      </MantineProvider>,
    );
    expect(screen.getByRole('heading', { name: 'Your data' })).toBeTruthy();
    expect(screen.getByTestId('data-export-card-stub')).toBeTruthy();
    expect(screen.getByTestId('delete-account-card-stub')).toBeTruthy();
  });
});
