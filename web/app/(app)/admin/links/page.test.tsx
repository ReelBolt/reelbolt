// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { renderWithI18n } from '@/test/i18n';

const H = vi.hoisted(() => ({ fetch: vi.fn(), isPlatformAdmin: true }));
vi.stubGlobal('fetch', H.fetch);
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ isPlatformAdmin: H.isPlatformAdmin }) }));
vi.mock('next/link', () => ({
  default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a>,
}));

import AdminLinksPage from './page';

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const renderPage = (locale = 'en') =>
  renderWithI18n(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <AdminLinksPage />
    </SWRConfig>,
    { locale },
  );

beforeEach(() => {
  H.fetch.mockReset();
  H.isPlatformAdmin = true;
  H.fetch.mockImplementation((url: string) =>
    Promise.resolve(
      url.includes('/funnel')
        ? json({
            days: 30,
            generatedAt: '2026-11-11T08:00:00Z',
            urlCaptureEnabled: false,
            items: [],
            unattributed: { signups: 0, verified: 0, onboarded: 0, firstProject: 0, firstRun: 0, firstVideo: 0, paid: 0 },
          })
        : json({ items: [] }),
    ),
  );
});

describe('the admin Links page', () => {
  it('shows the links panel to a platform admin, under the Admin breadcrumb', async () => {
    renderPage();
    expect(screen.getByRole('heading', { level: 1, name: 'Links' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Admin' }).getAttribute('href')).toBe('/admin');
    expect(await screen.findByTestId('links-panel')).toBeTruthy();
    expect(await screen.findByText('No links yet')).toBeTruthy();
    const urls = H.fetch.mock.calls.map((call) => call[0] as string);
    expect(urls).toContain('/api/v1/marketing/admin/links');
    expect(urls).toContain('/api/v1/marketing/admin/links/funnel?days=30');
  });

  it('shows anyone else that the page is for platform administrators, and requests nothing', () => {
    H.isPlatformAdmin = false;
    renderPage();
    const alert = screen.getByTestId('links-admin-only');
    expect(alert.textContent).toContain('Platform administrators only');
    expect(alert.textContent).toContain('The API answers 403 for anyone else.');
    expect(screen.queryByTestId('links-panel')).toBeNull();
    expect(H.fetch).not.toHaveBeenCalled();
  });

  it('is translated', () => {
    H.isPlatformAdmin = false;
    renderPage('it');
    expect(screen.getByRole('heading', { level: 1, name: 'Link' })).toBeTruthy();
    expect(screen.getByTestId('links-admin-only').textContent).toContain('Solo per gli amministratori della piattaforma');
  });
});
