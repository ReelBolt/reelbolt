// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { renderWithI18n } from '@/test/i18n';

const H = vi.hoisted(() => ({ fetch: vi.fn(), show: vi.fn() }));
vi.stubGlobal('fetch', H.fetch);
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
vi.mock('@mantine/notifications', () => ({ notifications: { show: (a: unknown) => H.show(a) } }));

import { MarketingContactsPanel } from './MarketingContactsPanel';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const SYNC = {
  enabled: true,
  configured: true,
  campaignActive: false,
  lastRunAt: '2026-11-01T10:00:00Z',
  lastRunOk: true,
  lastErrorCode: null,
  lastReconcileAt: '2026-11-01T03:00:00Z',
  counts: { synced: 12, pendingPush: 3, failed: 1 },
};
const CONTACT = {
  id: 'c1',
  email: 'ada@example.com',
  status: 'optedIn',
  firstName: 'Ada',
  lastName: null,
  nameSource: 'Form',
  brevo: { state: 'synced', syncedAt: null, errorCode: null },
};

let sync: unknown;
let contacts: unknown;
let runResponse: () => Response;
let patchResponse: () => Response;

const realScrollIntoView = Element.prototype.scrollIntoView;

afterEach(() => {
  Element.prototype.scrollIntoView = realScrollIntoView;
});

beforeEach(() => {
  // Mantine's Combobox scrolls the selected option into view on a timer; jsdom has no scrollIntoView.
  Element.prototype.scrollIntoView = vi.fn();
  H.fetch.mockReset();
  H.show.mockReset();
  sync = SYNC;
  contacts = { items: [CONTACT], total: 1 };
  runResponse = () => json({ requested: true }, 202);
  patchResponse = () => json({ ...CONTACT, firstName: 'Grace' });
  H.fetch.mockImplementation((url: string, init?: RequestInit) => {
    if (url.startsWith('/api/v1/marketing/admin/contacts')) return Promise.resolve(json(contacts));
    if (url.endsWith('/contact-sync/run')) return Promise.resolve(runResponse());
    if (url.endsWith('/contact-sync')) return Promise.resolve(json(sync));
    if (url.endsWith('/name') && init?.method === 'PATCH') return Promise.resolve(patchResponse());
    return Promise.resolve(json({}, 404));
  });
});

const renderPanel = () =>
  renderWithI18n(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MarketingContactsPanel />
    </SWRConfig>,
    { locale: 'en' },
  );

const urls = () => H.fetch.mock.calls.map((c) => c[0] as string);

describe('MarketingContactsPanel', () => {
  it('shows counts, last run and the contact row', async () => {
    renderPanel();
    expect(await screen.findByTestId('contact-sync-count-synced')).toHaveProperty('textContent', '12');
    expect(screen.getByTestId('contact-sync-count-waiting').textContent).toBe('3');
    expect(await screen.findByText('ada@example.com')).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: 'First name' })).toBeTruthy();
  });

  it('says so when sync is disabled', async () => {
    sync = { ...SYNC, enabled: false };
    renderPanel();
    expect(await screen.findByTestId('contact-sync-disabled')).toBeTruthy();
  });

  it('shows a failed run with its stable code', async () => {
    sync = { ...SYNC, lastRunOk: false, lastErrorCode: 'brevo_unauthorized' };
    renderPanel();
    expect(await screen.findByText('Failed (brevo_unauthorized)')).toBeTruthy();
  });

  it('shows the empty state', async () => {
    contacts = { items: [], total: 0 };
    renderPanel();
    expect(await screen.findByTestId('contacts-empty')).toBeTruthy();
  });

  it('Sync now posts the request and confirms', async () => {
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-sync-now'));
    expect((await screen.findByTestId('contact-sync-message')).textContent).toContain('Sync requested');
    expect(H.fetch.mock.calls.some((c) => (c[0] as string).endsWith('/run') && (c[1] as RequestInit).method === 'POST')).toBe(true);
  });

  it('Sync now shows the rate-limit message on 429', async () => {
    runResponse = () => json({ code: 'rate_limited' }, 429);
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-sync-now'));
    expect((await screen.findByTestId('contact-sync-message')).textContent).toContain('requested very recently');
  });

  it('Sync now shows the disabled message on 409', async () => {
    runResponse = () => json({ code: 'sync_disabled' }, 409);
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-sync-now'));
    expect((await screen.findByTestId('contact-sync-message')).textContent).toContain('turned off');
  });

  it('edits a name and PATCHes the trimmed values, empty as null', async () => {
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-edit-c1'));
    fireEvent.change(await screen.findByTestId('contact-edit-first-name'), { target: { value: ' Grace ' } });
    fireEvent.click(screen.getByTestId('contact-edit-save'));
    await waitFor(() => expect(H.show).toHaveBeenCalled());
    const call = H.fetch.mock.calls.find((c) => (c[1] as RequestInit | undefined)?.method === 'PATCH');
    expect(call?.[0]).toBe('/api/v1/marketing/admin/consents/c1/name');
    expect(JSON.parse((call?.[1] as RequestInit).body as string)).toEqual({ firstName: 'Grace', lastName: null });
  });

  it('shows the invalid_name message and keeps the dialog open', async () => {
    patchResponse = () => json({ code: 'invalid_name' }, 400);
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-edit-c1'));
    fireEvent.click(await screen.findByTestId('contact-edit-save'));
    expect((await screen.findByTestId('contact-edit-error')).textContent).toContain('not valid');
    expect(H.show).not.toHaveBeenCalled();
  });

  it('search changes the requested URL', async () => {
    renderPanel();
    await screen.findByText('ada@example.com');
    fireEvent.change(screen.getByTestId('contacts-search'), { target: { value: 'ada' } });
    await waitFor(() => expect(urls().some((u) => u.includes('q=ada') && u.includes('status=all'))).toBe(true));
    expect(urls().some((u) => u.includes('limit=50') && u.includes('offset=0'))).toBe(true);
  });

  it('the status filter changes the requested URL', async () => {
    renderPanel();
    await screen.findByText('ada@example.com');
    fireEvent.click(screen.getByTestId('contacts-status'));
    fireEvent.click(await screen.findByText('Unsubscribed', {}, { timeout: 3000 }));
    await waitFor(() => expect(urls().some((u) => u.includes('status=withdrawn') && u.includes('offset=0'))).toBe(true));
  });

  it('pagination moves forward and back through offsets', async () => {
    contacts = { items: [CONTACT], total: 120 };
    renderPanel();
    await screen.findByText('ada@example.com');
    fireEvent.click(screen.getByRole('button', { name: '2' }));
    await waitFor(() => expect(urls().some((u) => u.includes('offset=50'))).toBe(true));
    const before = urls().length;
    fireEvent.click(screen.getByRole('button', { name: '1' }));
    await waitFor(() => expect(urls().slice(before).some((u) => u.includes('offset=0'))).toBe(true));
  });

  it('Sync now is disabled while the request is in flight', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => {
      release = r;
    });
    H.fetch.mockImplementation((url: string) => {
      if (url.startsWith('/api/v1/marketing/admin/contacts')) return Promise.resolve(json(contacts));
      if (url.endsWith('/contact-sync/run')) return gate.then(() => json({ requested: true }, 202));
      return Promise.resolve(json(sync));
    });
    renderPanel();
    const btn = await screen.findByTestId('contact-sync-now');
    fireEvent.click(btn);
    await waitFor(() => expect((screen.getByTestId('contact-sync-now') as HTMLButtonElement).disabled).toBe(true));
    release();
    await screen.findByTestId('contact-sync-message');
    expect((screen.getByTestId('contact-sync-now') as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows the per-row Brevo error code as visible text', async () => {
    contacts = { items: [{ ...CONTACT, brevo: { state: 'failed', syncedAt: null, errorCode: 'brevo_rejected' } }], total: 1 };
    renderPanel();
    expect((await screen.findByTestId('contact-brevo-error-c1')).textContent).toContain('brevo_rejected');
  });

  it('limits name inputs to 100 characters', async () => {
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-edit-c1'));
    expect((await screen.findByTestId('contact-edit-first-name')).getAttribute('maxlength')).toBe('100');
  });

  it('clearing an existing name sends nulls', async () => {
    contacts = { items: [{ ...CONTACT, lastName: 'Lovelace' }], total: 1 };
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-edit-c1'));
    fireEvent.change(await screen.findByTestId('contact-edit-first-name'), { target: { value: '' } });
    fireEvent.change(screen.getByTestId('contact-edit-last-name'), { target: { value: '  ' } });
    fireEvent.click(screen.getByTestId('contact-edit-save'));
    await waitFor(() => expect(H.show).toHaveBeenCalled());
    const call = H.fetch.mock.calls.find((c) => (c[1] as RequestInit | undefined)?.method === 'PATCH');
    expect(JSON.parse((call?.[1] as RequestInit).body as string)).toEqual({ firstName: null, lastName: null });
  });

  it('limits the last name and the search box', async () => {
    renderPanel();
    expect(screen.getByTestId('contacts-search').getAttribute('maxlength')).toBe('200');
    fireEvent.click(await screen.findByTestId('contact-edit-c1'));
    expect((await screen.findByTestId('contact-edit-last-name')).getAttribute('maxlength')).toBe('100');
  });

  it('a generic save error shows a message, not a crash', async () => {
    patchResponse = () => json({ message: 'boom' }, 500);
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-edit-c1'));
    fireEvent.click(await screen.findByTestId('contact-edit-save'));
    expect((await screen.findByTestId('contact-edit-error')).textContent).toContain('Could not save the name');
    expect(H.show).not.toHaveBeenCalled();
  });

  it('a generic Sync now error shows a message, not a crash', async () => {
    runResponse = () => json({ message: 'boom' }, 500);
    renderPanel();
    fireEvent.click(await screen.findByTestId('contact-sync-now'));
    expect((await screen.findByTestId('contact-sync-message')).textContent).toContain('Could not start the sync');
  });

  it('formats counts with the locale formatter, not raw digits', async () => {
    sync = { ...SYNC, counts: { synced: 12345, pendingPush: 3, failed: 1 } };
    renderPanel();
    const el = await screen.findByTestId('contact-sync-count-synced');
    expect(el.textContent).toBe(new Intl.NumberFormat('en').format(12345));
    expect(el.textContent).not.toBe('12345');
  });

  it('shows each contact status as a badge', async () => {
    contacts = { items: [{ ...CONTACT, status: 'withdrawn' }], total: 1 };
    renderPanel();
    expect((await screen.findByTestId('contact-status-c1')).textContent).toBe('Unsubscribed');
    expect(screen.getByRole('columnheader', { name: 'Status' })).toBeTruthy();
  });
});
