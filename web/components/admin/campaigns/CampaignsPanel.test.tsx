// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

const H = vi.hoisted(() => ({ fetch: vi.fn() }));
vi.stubGlobal('fetch', H.fetch);

import { CampaignsPanel } from './CampaignsPanel';
import { ApiError } from '@/lib/api/client';
import { campaignMessage, sendBlockReason } from '@/lib/api/marketingCampaigns';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null }),
});
class RO { observe() {} unobserve() {} disconnect() {} }
vi.stubGlobal('ResizeObserver', RO);

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const draft = {
  id: 'c1', subject: 'Hello', html: '<p>Hi</p>', status: 'draft', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  testSentAt: null, testCurrent: false, confirmedRecipientCount: null, finalRecipientCount: null, dispatchStartedAt: null,
  failureCode: null, stats: null, statsAt: null,
};
const settings = { footerOk: true, listSendEnabled: true, testRecipientMasked: 'a***@example.com', brevoConfigured: true };

function mockApi(rows: unknown[], cfg = settings) {
  H.fetch.mockImplementation(async (url?: string) => {
    if (String(url).endsWith('/settings')) return json(cfg);
    if (String(url).includes('/campaigns/c1')) return json(rows[0]);
    return json(rows);
  });
}
const renderPanel = () =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <CampaignsPanel />
      </SWRConfig>
    </MantineProvider>,
  );

beforeEach(() => H.fetch.mockReset());
afterEach(cleanup);

describe('CampaignsPanel', () => {
  it('shows the disabled state on 404', async () => {
    H.fetch.mockImplementation(async () => json({}, 404));
    renderPanel();
    expect(await screen.findByTestId('campaigns-disabled')).toBeTruthy();
  });

  it('previews in an iframe with an empty sandbox and has no recipient input', async () => {
    mockApi([]);
    renderPanel();
    const frame = (await screen.findByTestId('campaign-preview')) as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(screen.queryByLabelText(/recipient|to|email/i, { selector: 'input' })).toBeNull();
    expect(screen.getByText(/footer and the unsubscribe link are added/i)).toBeTruthy();
  });

  it('warns on SendUnknown and offers no resend', async () => {
    mockApi([{ ...draft, status: 'sendUnknown' }]);
    renderPanel();
    expect(await screen.findByTestId('send-unknown-warning')).toBeTruthy();
    expect(screen.queryByText(/resend/i)).toBeNull();
  });

  it('keeps send disabled with a reason until a test send and a dry run exist', async () => {
    mockApi([draft]);
    renderPanel();
    fireEvent.click(await screen.findByText('Edit'));
    const send = (await screen.findByTestId('send-button')) as HTMLButtonElement;
    expect(send.disabled).toBe(true);
    expect(screen.getByTestId('send-reason').textContent).toMatch(/test/i);
    expect(screen.getByTestId('test-recipient').textContent).toContain('a***@example.com');
  });

  it('enables send after a current test and a fresh dry run, then requires the typed count', async () => {
    mockApi([{ ...draft, testCurrent: true }]);
    renderPanel();
    fireEvent.click(await screen.findByText('Edit'));
    H.fetch.mockImplementation(async (url?: string) => {
      if (String(url).endsWith('/dry-run')) return json({ recipientCount: 42, suppressedCount: 1, sampleMasked: ['b***@x.io'], footerOk: true, errors: [], listSendEnabled: true });
      if (String(url).endsWith('/settings')) return json(settings);
      if (String(url).endsWith('/send')) return json({ ...draft, status: 'queued' }, 202);
      return json([{ ...draft, testCurrent: true }]);
    });
    fireEvent.click(await screen.findByText('Dry run'));
    await screen.findByTestId('dry-run-result');
    await waitFor(() => expect((screen.getByTestId('send-button') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('send-button'));
    const sendBtn = (await screen.findByText('Send 42 emails')).closest('button') as HTMLButtonElement;
    expect(sendBtn.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Recipient count'), { target: { value: '42' } });
    expect(sendBtn.disabled).toBe(false);
    fireEvent.click(sendBtn);
    await waitFor(() => {
      const call = H.fetch.mock.calls.find(([u]) => String(u).endsWith('/send'));
      expect(JSON.parse(call![1].body)).toEqual({ confirmRecipientCount: 42 });
    });
  });
});

describe('QA fixes', () => {
  const sent = { ...draft, status: 'sent' };
  it('Refresh results makes the request', async () => {
    mockApi([sent]);
    renderPanel();
    fireEvent.click(await screen.findByText('Refresh results'));
    await waitFor(() => expect(H.fetch.mock.calls.some(([u, i]) => String(u).endsWith('/c1/refresh-results') && (i as RequestInit).method === 'POST')).toBe(true));
  });

  it('shows a failed campaign reason in words and offers Duplicate as draft', async () => {
    mockApi([{ ...draft, status: 'failed', failureCode: 'erasure_pending' }]);
    renderPanel();
    expect((await screen.findByTestId('failure-reason')).textContent).not.toContain('_');
    const actions = screen.getByTestId('campaign-actions-row');
    expect(actions.querySelector('td')?.getAttribute('colspan')).toBeTruthy();
    expect(screen.getAllByRole('columnheader').every((h) => h.textContent !== '')).toBe(true);
    fireEvent.click(screen.getByText('Duplicate as draft'));
    await waitFor(() => expect(H.fetch.mock.calls.some(([u]) => String(u).endsWith('/c1/duplicate'))).toBe(true));
  });

  it('resolves a SendUnknown only after confirming', async () => {
    mockApi([{ ...draft, status: 'sendUnknown' }]);
    renderPanel();
    fireEvent.click(await screen.findByText('I checked Brevo: it was not sent'));
    expect(H.fetch.mock.calls.some(([u]) => String(u).endsWith('/resolve'))).toBe(false);
    fireEvent.click(await screen.findByText('Yes, it was not sent'));
    await waitFor(() => {
      const call = H.fetch.mock.calls.find(([u]) => String(u).endsWith('/c1/resolve'));
      expect(JSON.parse((call?.[1] as RequestInit).body as string)).toEqual({ outcome: 'notSent' });
    });
    expect(screen.getByText('Send unknown')).toBeTruthy();
  });

  it('shows an error with retry when the list fails', async () => {
    H.fetch.mockImplementation(async (url?: string) => (String(url).endsWith('/settings') ? json(settings) : json({}, 500)));
    renderPanel();
    expect(await screen.findByTestId('campaigns-list-error')).toBeTruthy();
    expect(screen.queryByText('No campaigns yet.')).toBeNull();
  });

  it('reads error before code on a 502 and never shows a raw code', () => {
    const e = new ApiError('Internal Server Error', 502, [], { error: 'send_failed', code: 'brevo_http_500' });
    expect(campaignMessage(e)).toMatch(/Nothing was sent/);
    expect(campaignMessage(new ApiError('Internal Server Error', 500))).not.toMatch(/Internal Server Error/);
    expect(campaignMessage(new ApiError('x', 500, [], { error: 'weird_code' }))).not.toContain('_');
  });
});

describe('send gating and messages', () => {
  it('names the first missing precondition', () => {
    const ok = { listSendEnabled: true, testCurrent: true, dryRunFresh: true, dirty: false };
    expect(sendBlockReason(ok)).toBeNull();
    expect(sendBlockReason({ ...ok, listSendEnabled: false })).toMatch(/switched off/);
    expect(sendBlockReason({ ...ok, testCurrent: false })).toMatch(/test/);
    expect(sendBlockReason({ ...ok, dryRunFresh: false })).toMatch(/dry run/i);
  });

  it.each([
    'list_send_disabled', 'not_draft', 'recipient_count_changed', 'campaign_active', 'rate_limited', 'subject_missing',
    'subject_too_long', 'subject_invalid_characters', 'html_missing', 'html_too_large', 'no_recipients', 'test_send_required',
    'test_recipient_missing', 'brevo_key_missing', 'brevo_list_missing', 'brevo_test_list_missing', 'nothing_to_refresh',
    'send_failed', 'refresh_failed', 'html_too_short', 'html_too_deep', 'html_no_body', 'footer_placement_failed', 'footer_controller_name_missing',
    'footer_address_missing', 'public_base_url_missing', 'unresolved_send', 'consent_changed', 'erasure_unavailable',
    'brevo_bad_response', 'lease_lost', 'not_sent',
  ])('gives a plain message for %s', (code) => {
    const msg = campaignMessage(new ApiError('raw', 409, [], { error: code }));
    expect(msg).not.toBe('raw');
    expect(msg).not.toContain('_');
  });
});

describe('preview keeps the opened detail', () => {
  const withPreview = { ...draft, previewHtml: '<p>Hi</p><footer>unsubscribe</footer>', previewError: null };
  const frameDoc = async () => ((await screen.findByTestId('campaign-preview')) as HTMLIFrameElement).getAttribute('srcdoc') ?? '';

  it('shows server HTML with footer after open from list and after a list refresh', async () => {
    H.fetch.mockImplementation(async (url?: string) => {
      if (String(url).endsWith('/settings')) return json(settings);
      if (String(url).includes('/campaigns/c1')) return json(withPreview);
      return json([{ ...draft, previewHtml: null }]);
    });
    renderPanel();
    fireEvent.click(await screen.findByText('Edit'));
    await waitFor(async () => expect(await frameDoc()).toContain('unsubscribe'));
    await waitFor(() => expect(H.fetch.mock.calls.length).toBeGreaterThan(3));
    expect(await frameDoc()).toContain('unsubscribe');
  });

  it('shows server HTML after save and a preview error as plain message', async () => {
    let saved = false;
    H.fetch.mockImplementation(async (url?: string, init?: RequestInit) => {
      if (String(url).endsWith('/settings')) return json(settings);
      if (init?.method === 'PUT') { saved = true; return json({ ...withPreview, subject: 'Hello 2', previewHtml: null, previewError: 'preview_failed' }); }
      if (String(url).includes('/campaigns/c1')) return json(withPreview);
      return json([{ ...draft, subject: saved ? 'Hello 2' : 'Hello', previewHtml: null }]);
    });
    renderPanel();
    fireEvent.click(await screen.findByText('Edit'));
    await waitFor(async () => expect(await frameDoc()).toContain('unsubscribe'));
    fireEvent.change(screen.getByLabelText(/subject/i), { target: { value: 'Hello 2' } });
    fireEvent.click(screen.getByText(/save draft/i));
    expect(await screen.findByTestId('preview-error')).toBeTruthy();
  });
});
