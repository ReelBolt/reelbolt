// @vitest-environment jsdom
/**
 * The platform admin's marketing list view.
 *
 * What these tests protect (compliance.md, "Export"):
 *   - the per-send process rule is stated, verbatim, above the download;
 *   - the view says the list holds only confirmed, verified addresses;
 *   - the download is a plain link to the CSV endpoint (no client-side copy);
 *   - counts and the last export come from GET /api/v1/marketing/admin/consents.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

const H = vi.hoisted(() => ({ fetch: vi.fn() }));
vi.stubGlobal('fetch', H.fetch);

import { MARKETING_SEND_RULE, MarketingListPanel } from './MarketingListPanel';

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

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const renderPanel = () =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MarketingListPanel />
      </SWRConfig>
    </MantineProvider>,
  );

beforeEach(() => {
  H.fetch.mockReset();
});

afterEach(cleanup);

describe('MarketingListPanel', () => {
  it('states the per-send rule above a plain CSV download link', async () => {
    H.fetch.mockResolvedValue(json({ counts: { pending: 1, optedIn: 5, withdrawn: 2, exportable: 4 }, lastExport: null }));
    renderPanel();

    expect(MARKETING_SEND_RULE).toBe(
      "Download a fresh list right before every send. Use each row's unsubscribe link as the email's List-Unsubscribe header, and remove anyone who unsubscribed in your sending tool before the next send.",
    );
    const rule = screen.getByTestId('marketing-send-rule');
    expect(rule.textContent).toContain(MARKETING_SEND_RULE);

    const link = screen.getByTestId('marketing-export-download');
    expect(link.tagName).toBe('A');
    expect(link.getAttribute('href')).toBe('/api/v1/marketing/admin/consents/export.csv');
    expect(link.textContent).toContain('Download CSV');
    // The rule comes before the button in document order.
    expect(rule.compareDocumentPosition(link) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    expect(screen.getByTestId('marketing-list-scope').textContent).toMatch(/only confirmed, verified addresses/);
  });

  it('shows the counts and the last export from the admin summary', async () => {
    H.fetch.mockResolvedValue(
      json({
        counts: { pending: 3, optedIn: 12, withdrawn: 7, exportable: 10 },
        lastExport: { exportedAt: '2026-10-12T09:30:00Z', rowCount: 10 },
      }),
    );
    renderPanel();

    await waitFor(() => expect(screen.getByTestId('marketing-count-exportable').textContent).toBe('10'));
    expect(screen.getByTestId('marketing-count-opted-in').textContent).toBe('12');
    expect(screen.getByTestId('marketing-count-pending').textContent).toBe('3');
    expect(screen.getByTestId('marketing-count-withdrawn').textContent).toBe('7');
    expect(screen.getByTestId('marketing-last-export').textContent).toMatch(/Last export: .*10 rows\./);
    expect(H.fetch.mock.calls[0][0]).toBe('/api/v1/marketing/admin/consents');
  });

  it('says the list was never exported when lastExport is null', async () => {
    H.fetch.mockResolvedValue(json({ counts: { pending: 0, optedIn: 0, withdrawn: 0, exportable: 0 }, lastExport: null }));
    renderPanel();

    await waitFor(() => expect(screen.getByTestId('marketing-last-export').textContent).toBe('This list has never been exported.'));
  });

  it('keeps the rule and the download visible when the summary cannot be read', async () => {
    H.fetch.mockResolvedValue(json({ error: 'forbidden' }, 403));
    renderPanel();

    await waitFor(() => expect(screen.getByText('Could not load the marketing list')).toBeTruthy());
    expect(screen.getByTestId('marketing-send-rule')).toBeTruthy();
    expect(screen.getByTestId('marketing-export-download')).toBeTruthy();
  });
});
