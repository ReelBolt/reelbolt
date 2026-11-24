// @vitest-environment jsdom
/** The Webhooks tab: list states, create with the secret shown once, rotate, deliveries. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WebhookEndpoint } from '@/lib/types/webhook';

const H = vi.hoisted(() => ({
  items: [] as unknown[],
  mutate: vi.fn(),
  createWebhook: vi.fn(),
  rotateWebhookSecret: vi.fn(),
  sendWebhookTest: vi.fn(),
  deleteWebhook: vi.fn(),
  listWebhookDeliveries: vi.fn(),
  redeliverWebhook: vi.fn(),
}));

vi.mock('@/lib/hooks/use-webhooks', () => ({
  useWebhooks: () => ({ data: H.items, error: undefined, mutate: H.mutate }),
}));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ data: [] }) }));
vi.mock('@/lib/api/webhooks', () => ({
  createWebhook: (...a: unknown[]) => H.createWebhook(...a),
  rotateWebhookSecret: (...a: unknown[]) => H.rotateWebhookSecret(...a),
  sendWebhookTest: (...a: unknown[]) => H.sendWebhookTest(...a),
  deleteWebhook: (...a: unknown[]) => H.deleteWebhook(...a),
  listWebhookDeliveries: (...a: unknown[]) => H.listWebhookDeliveries(...a),
  redeliverWebhook: (...a: unknown[]) => H.redeliverWebhook(...a),
  updateWebhook: vi.fn(),
}));

import { WebhooksPanel } from './WebhooksPanel';

function endpoint(over: Partial<WebhookEndpoint> = {}): WebhookEndpoint {
  return {
    id: 'w1',
    url: 'https://hooks.example.com/reelbolt',
    description: null,
    events: ['run.succeeded'],
    projectIds: null,
    enabled: true,
    disabledReason: null,
    createdAt: '2026-10-01T00:00:00Z',
    lastSuccessAt: null,
    failingSince: null,
    ...over,
  };
}

function renderPanel() {
  return render(
    <MantineProvider>
      <WebhooksPanel orgId="org-1" />
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addEventListener: vi.fn(), removeEventListener: vi.fn(),
    addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
  })) as unknown as typeof matchMedia;
  H.items = [endpoint()];
  H.mutate.mockReset();
  H.createWebhook.mockReset();
  H.rotateWebhookSecret.mockReset();
  H.listWebhookDeliveries.mockReset().mockResolvedValue({ data: [], nextCursor: null });
  H.redeliverWebhook.mockReset().mockResolvedValue(undefined);
});
afterEach(() => cleanup());

describe('WebhooksPanel', () => {
  it('shows the empty state', () => {
    H.items = [];
    renderPanel();
    expect(screen.getByText('No webhook endpoints yet')).toBeTruthy();
  });

  it('shows active, failing and disabled endpoints', () => {
    H.items = [
      endpoint(),
      endpoint({ id: 'w2', url: 'https://b.example.com/x', failingSince: '2026-10-20T00:00:00Z' }),
      endpoint({ id: 'w3', url: 'https://c.example.com/y', enabled: false, disabledReason: 'too many failures' }),
    ];
    renderPanel();
    expect(screen.getByText('hooks.example.com/reelbolt')).toBeTruthy();
    expect(screen.getByText('Active')).toBeTruthy();
    expect(screen.getByText(/Failing since/)).toBeTruthy();
    expect(screen.getByText('Disabled')).toBeTruthy();
  });

  it('creates an endpoint, shows the secret once and clears it on close', async () => {
    H.createWebhook.mockResolvedValue({ ...endpoint({ id: 'w9' }), secret: 'whsec_once_123' });
    renderPanel();
    fireEvent.click(screen.getAllByRole('button', { name: 'New endpoint' })[0]);
    fireEvent.change(await screen.findByLabelText(/Endpoint URL/), { target: { value: 'https://new.example.com/hook' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create endpoint' }));
    expect(await screen.findByText('whsec_once_123')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'I have stored it' }));
    await waitFor(() => expect(screen.queryByText('whsec_once_123')).toBeNull());
  });

  it('rejects a non-https URL without calling the server', async () => {
    renderPanel();
    fireEvent.click(screen.getAllByRole('button', { name: 'New endpoint' })[0]);
    fireEvent.change(await screen.findByLabelText(/Endpoint URL/), { target: { value: 'http://plain.example.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create endpoint' }));
    expect(await screen.findByText('Enter a valid https URL.')).toBeTruthy();
    expect(H.createWebhook).not.toHaveBeenCalled();
  });

  it('rotates the secret after confirmation and shows the new one once', async () => {
    H.rotateWebhookSecret.mockResolvedValue({ secret: 'whsec_rotated_456' });
    renderPanel();
    fireEvent.click(screen.getByRole('button', { name: /Rotate the signing secret/ }));
    expect(H.rotateWebhookSecret).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: 'Rotate secret' }));
    expect(await screen.findByText('whsec_rotated_456')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'I have stored it' }));
    await waitFor(() => expect(screen.queryByText('whsec_rotated_456')).toBeNull());
  });

  it('lists deliveries and redelivers one', async () => {
    H.listWebhookDeliveries.mockResolvedValue({
      data: [{
        id: 'd1', eventType: 'run.failed', status: 'failed', attempts: 3, nextAttemptAt: null,
        lastStatusCode: 500, lastError: null, createdAt: '2026-10-21T00:00:00Z', deliveredAt: null,
      }],
      nextCursor: null,
    });
    renderPanel();
    fireEvent.click(screen.getByRole('button', { name: /View deliveries of/ }));
    expect(await screen.findByText('run.failed')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Redeliver' }));
    await waitFor(() => expect(H.redeliverWebhook).toHaveBeenCalledWith('w1', 'd1'));
  });
});
