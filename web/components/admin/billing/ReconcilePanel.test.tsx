// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { SWRConfig } from 'swr';
import { ReconcilePanel } from './ReconcilePanel';
import { describeResyncError, describeResyncResult, outcomeLabel } from './reconcile-copy';
import { ApiError } from '@/lib/api/client';
import { getBillingAdminReconcile, normalizeReconcileOutcome, resyncBillingOrganization } from '@/lib/api/billing-admin';
import type { BillingReconcileItem, BillingReconcileList } from '@/lib/types/billing-admin';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

// The panel names the deployment's provider; a Paddle deployment keeps its wording.
vi.mock('@/lib/hooks/use-billing', () => ({ useMerchantLabel: () => 'Paddle' }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/api/billing-admin', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/billing-admin')>();
  return { ...actual, getBillingAdminReconcile: vi.fn(), resyncBillingOrganization: vi.fn() };
});

const listMock = vi.mocked(getBillingAdminReconcile);
const resyncMock = vi.mocked(resyncBillingOrganization);
const notifyMock = vi.mocked(notifications.show);

const ORG = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';

const item = (over: Partial<BillingReconcileItem> = {}): BillingReconcileItem => ({
  organizationId: ORG,
  organizationName: 'Northwind Studio',
  planKey: 'pro',
  status: 'Active',
  reason: 'change_pending',
  waitingSince: '2026-10-13T08:00:00.000Z',
  lastCheckedAt: null,
  lastOutcome: null,
  lastError: null,
  ...over,
});

const list = (items: BillingReconcileItem[]): BillingReconcileList => ({ stuckAfterSeconds: 900, items });

function renderPanel() {
  return render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <ReconcilePanel />
      </SWRConfig>
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false,
    media: q,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  listMock.mockReset();
  resyncMock.mockReset();
  notifyMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe('reconcile copy for another provider', () => {
  it('names the given provider and never Paddle', () => {
    expect(outcomeLabel('stale', 'Stripe')).toBe("Stripe's state could not be applied; check the logs");
    expect(describeResyncError(new ApiError('Bad Gateway', 502), 'Stripe')).toMatch(/^Stripe could not be reached/);
    expect(outcomeLabel('healed')).toBe('Updated from your payment provider');
  });
});

describe('reconcile copy', () => {
  it('reads an outcome whatever its casing', () => {
    expect(normalizeReconcileOutcome('Healed')).toBe('Healed');
    expect(normalizeReconcileOutcome('inSync')).toBe('InSync');
    expect(normalizeReconcileOutcome('nothing_to_reconcile')).toBe('NothingToReconcile');
    expect(normalizeReconcileOutcome('mystery')).toBeNull();
    expect(outcomeLabel('stale', 'Paddle')).toBe("Paddle's state could not be applied; check the logs");
    expect(outcomeLabel('mystery')).toBe('mystery');
  });

  it('never shows a bare error code, but keeps a sentence the server wrote', () => {
    expect(describeResyncError(new ApiError('change_in_progress', 409, [], { error: 'change_in_progress' }))).toMatch(
      /^Another billing change/,
    );
    expect(describeResyncError(new ApiError('Bad Gateway', 502), 'Paddle')).toMatch(/^Paddle could not be reached/);
    expect(describeResyncError(new ApiError('Not Found', 404))).toMatch(/^No workspace has that ID/);
    expect(describeResyncError(new ApiError('x', 404, [], { message: 'Organization not found.' }))).toBe(
      'Organization not found.',
    );
  });

  it('says what differed and what was applied', () => {
    expect(
      describeResyncResult(
        { outcome: 'Healed', drift: ['plan', 'cancel_at_period_end'], subscriptionEventsApplied: 1, transactionsApplied: 2 },
        'Northwind Studio',
      ),
    ).toBe('Northwind Studio: Differed: plan, scheduled cancellation. Applied 1 subscription event and 2 transactions.');
  });
});

describe('ReconcilePanel', () => {
  it('shows the empty state when nothing is waiting', async () => {
    listMock.mockResolvedValue(list([]));
    renderPanel();

    expect(await screen.findByText('Nothing is waiting on Paddle')).toBeTruthy();
    expect(screen.getByText(/unconfirmed for more than 15 minutes/)).toBeTruthy();
  });

  it('lists a waiting workspace with its reason in plain words and the last check', async () => {
    listMock.mockResolvedValue(
      list([
        item(),
        item({
          organizationId: '00000000-0000-0000-0000-000000000002',
          organizationName: 'Harbor Films',
          reason: 'reconcile_failing',
          waitingSince: '2026-10-12T08:00:00.000Z',
          lastCheckedAt: '2026-10-13T09:00:00.000Z',
          lastOutcome: 'providerUnavailable',
          lastError: 'HTTP 503 from Paddle',
        }),
      ]),
    );
    renderPanel();

    expect(await screen.findByText('Northwind Studio')).toBeTruthy();
    expect(screen.getByText('Plan change not confirmed')).toBeTruthy();
    expect(screen.getByText('Paddle could not be read')).toBeTruthy();
    expect(screen.getByText('Paddle could not be reached; nothing changed')).toBeTruthy();
    expect(screen.getByText('HTTP 503 from Paddle')).toBeTruthy();
    expect(screen.getByText('Not checked yet')).toBeTruthy();
    const times = document.querySelectorAll('time[datetime="2026-10-13T08:00:00.000Z"]');
    expect(times.length).toBe(1);
  });

  it('enables Resync by ID only for a GUID', async () => {
    const user = userEvent.setup();
    listMock.mockResolvedValue(list([]));
    renderPanel();
    await screen.findByText('Nothing is waiting on Paddle');

    const input = screen.getByLabelText('Resync a workspace by ID');
    const submit = screen.getByRole('button', { name: 'Resync from Paddle' });
    expect((submit as HTMLButtonElement).disabled).toBe(true);

    await user.type(input, 'not-an-id');
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/That is not a workspace ID/)).toBeTruthy();

    await user.clear(input);
    await user.type(input, ORG);
    expect((submit as HTMLButtonElement).disabled).toBe(false);
  });

  it('confirms, posts once with the reason, reports the outcome and refreshes the list', async () => {
    const user = userEvent.setup();
    listMock.mockResolvedValue(list([item()]));
    let resolve: (value: Awaited<ReturnType<typeof resyncBillingOrganization>>) => void = () => {};
    resyncMock.mockImplementation(
      () =>
        new Promise((r) => {
          resolve = r;
        }),
    );
    renderPanel();

    await user.click(await screen.findByRole('button', { name: 'Resync from Paddle: Northwind Studio' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/never adds credits by itself/)).toBeTruthy();
    expect(within(dialog).getByText(/delayed webhook/)).toBeTruthy();

    await user.type(within(dialog).getByLabelText('Reason (optional)'), 'Ticket 4411');
    expect(within(dialog).getByText(/11\/500 characters/)).toBeTruthy();

    const confirm = within(dialog).getByRole('button', { name: 'Resync from Paddle' });
    await user.click(confirm);
    await user.click(confirm);
    expect(resyncMock).toHaveBeenCalledTimes(1);
    expect(resyncMock).toHaveBeenCalledWith(ORG, { reason: 'Ticket 4411' });

    const callsBefore = listMock.mock.calls.length;
    resolve({ outcome: 'healed', drift: ['plan'], subscriptionEventsApplied: 1, transactionsApplied: 0 });

    await waitFor(() => expect(notifyMock).toHaveBeenCalledTimes(1));
    expect(notifyMock.mock.calls[0][0]).toMatchObject({ title: 'Updated from Paddle', color: 'green' });
    await waitFor(() => expect(listMock.mock.calls.length).toBeGreaterThan(callsBefore));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('keeps the modal open with a plain message on 409', async () => {
    const user = userEvent.setup();
    listMock.mockResolvedValue(list([item()]));
    resyncMock.mockRejectedValue(new ApiError('change_in_progress', 409, [], { error: 'change_in_progress' }));
    renderPanel();

    await user.click(await screen.findByRole('button', { name: 'Resync from Paddle: Northwind Studio' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Resync from Paddle' }));

    expect(await within(dialog).findByText(/Another billing change for this workspace is in progress/)).toBeTruthy();
    expect(screen.queryByText('change_in_progress')).toBeNull();
    expect(notifyMock).not.toHaveBeenCalled();
  });
});
