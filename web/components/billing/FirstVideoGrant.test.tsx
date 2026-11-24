// @vitest-environment jsdom
import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: vi.fn() }));

import { useBillingSummary } from '@/lib/hooks/use-billing';
import { FirstVideoGrantCard } from './FirstVideoGrantCard';
import { FirstVideoGrantCallout } from './FirstVideoGrantCallout';

global.matchMedia = ((q: string) => ({
  matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
  addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
})) as unknown as typeof matchMedia;

const grant = { credits: 750, remaining: 600, expiresAt: '2027-03-04T12:00:00Z' };
const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
const summary = (firstVideoGrant: typeof grant | null, firstVideoGrantStatus?: string) =>
  vi.mocked(useBillingSummary).mockReturnValue({ data: { firstVideoGrant, firstVideoGrantStatus }, error: undefined } as unknown as ReturnType<typeof useBillingSummary>);

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('FirstVideoGrantCard', () => {
  it('shows credits left and the expiry date', () => {
    wrap(<FirstVideoGrantCard grant={grant} />);
    expect(screen.getByText('Your first video is on us')).toBeTruthy();
    expect(screen.getByText(/Use them by March 4, 2027/)).toBeTruthy();
    expect(screen.getByText(/720p with a watermark/)).toBeTruthy();
  });
  it('renders nothing without a grant', () => {
    wrap(<FirstVideoGrantCard grant={null} />);
    expect(screen.queryByTestId('first-video-grant-card')).toBeNull();
  });
});

describe('FirstVideoGrantCallout', () => {
  it('shows the expiry and the trial limits while credits remain', () => {
    summary(grant);
    wrap(<FirstVideoGrantCallout />);
    expect(screen.getByText(/They expire on March 4, 2027/)).toBeTruthy();
    expect(screen.getByText(/720p with a watermark/)).toBeTruthy();
  });
  it('is hidden when there is no grant or nothing is left', () => {
    summary(null);
    wrap(<FirstVideoGrantCallout />);
    expect(screen.queryByTestId('first-video-grant-callout')).toBeNull();
    cleanup();
    summary({ ...grant, remaining: 0 });
    wrap(<FirstVideoGrantCallout />);
    expect(screen.queryByTestId('first-video-grant-callout')).toBeNull();
  });
});

describe('firstVideoGrantStatus', () => {
  const note = /not available from this connection or address/;
  it('unavailable: card and callout show the plain note with a plans link', () => {
    wrap(<FirstVideoGrantCard grant={null} status="unavailable" />);
    expect(screen.getByText(note)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing#plans');
    expect(document.body.textContent).not.toMatch(/VPN|Tor|email/i);
    cleanup();
    summary(null, 'unavailable');
    wrap(<FirstVideoGrantCallout />);
    expect(screen.getByText(note)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'See plans' })).toBeTruthy();
  });
  it('granted: shows the grant, not the note', () => {
    wrap(<FirstVideoGrantCard grant={grant} status="granted" />);
    expect(screen.getByText('Your first video is on us')).toBeTruthy();
    cleanup();
    summary(grant, 'granted');
    wrap(<FirstVideoGrantCallout />);
    expect(screen.getByTestId('first-video-grant-callout')).toBeTruthy();
    expect(screen.queryByText(note)).toBeNull();
  });
  it('none: shows nothing', () => {
    wrap(<FirstVideoGrantCard grant={null} status="none" />);
    expect(screen.queryByText(/free first video|first video is on us|Make your first video/)).toBeNull();
    cleanup();
    summary(null, 'none');
    wrap(<FirstVideoGrantCallout />);
    expect(screen.queryByText(/free first video|first video is on us|Make your first video/)).toBeNull();
  });
});
