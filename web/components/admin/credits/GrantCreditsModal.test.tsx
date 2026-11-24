// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/api/promotions', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/promotions')>()),
  grantOrgCredits: vi.fn(),
}));

import { grantOrgCredits, type AdminOrg } from '@/lib/api/promotions';
import { GrantCreditsModal, dateInputValue, validateGrant } from './GrantCreditsModal';

const grantMock = vi.mocked(grantOrgCredits);

const ORG: AdminOrg = { id: 'org-1', name: 'Acme', kind: 'Team', ownerEmail: 'o@acme.test', planKey: 'team', balance: 10 };

describe('validateGrant', () => {
  const now = new Date('2026-10-12T12:00:00');

  it('accepts a grant inside the bounds', () => {
    expect(validateGrant({ credits: 100, expiresOn: '2027-01-10', reason: 'Goodwill' }, now)).toEqual({});
  });

  it('bounds the credits, the expiry and the reason', () => {
    const errors = validateGrant({ credits: 0, expiresOn: '2026-10-12', reason: ' ' }, now);
    expect(errors.credits).toBeDefined();
    expect(errors.expiresOn).toBe('At least one day from now.');
    expect(errors.reason).toBeDefined();
    expect(validateGrant({ credits: 1_000_001, expiresOn: '2030-01-01', reason: 'x' }, now)).toMatchObject({
      credits: expect.any(String),
      expiresOn: 'At most three years from now.',
    });
  });
});

describe('GrantCreditsModal', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('sends the request id it was opened with, so a double submit grants once', async () => {
    grantMock.mockResolvedValue({
      id: 'g1', credits: 250, expiresAt: '2027-01-10T23:59:59Z', reason: 'Outage credit',
      grantedByUserId: 'u1', createdAt: '2026-10-12T00:00:00Z',
    });
    const onGranted = vi.fn();
    render(
      <MantineProvider>
        <GrantCreditsModal org={ORG} requestId="req-123" onClose={vi.fn()} onGranted={onGranted} />
      </MantineProvider>,
    );

    fireEvent.change(screen.getByTestId('grant-credits-amount'), { target: { value: '250' } });
    fireEvent.change(screen.getByTestId('grant-credits-reason'), { target: { value: 'Outage credit' } });
    fireEvent.click(screen.getByTestId('grant-credits-submit'));

    await waitFor(() => expect(grantMock).toHaveBeenCalledTimes(1));
    const [orgId, body] = grantMock.mock.calls[0];
    expect(orgId).toBe('org-1');
    expect(body).toMatchObject({ requestId: 'req-123', credits: 250, reason: 'Outage credit' });
    // The default expiry is 90 days out, sent as an instant.
    expect(new Date(body.expiresAt).getDate()).toBe(new Date(`${dateInputValue(90)}T23:59:59`).getDate());
    await waitFor(() => expect(onGranted).toHaveBeenCalled());
  });

  it('refuses a grant without a reason, and sends nothing', async () => {
    render(
      <MantineProvider>
        <GrantCreditsModal org={ORG} requestId="req-1" onClose={vi.fn()} onGranted={vi.fn()} />
      </MantineProvider>,
    );
    fireEvent.change(screen.getByTestId('grant-credits-amount'), { target: { value: '10' } });
    fireEvent.click(screen.getByTestId('grant-credits-submit'));
    expect(await screen.findByText(/Say why/)).toBeTruthy();
    expect(grantMock).not.toHaveBeenCalled();
  });
});
