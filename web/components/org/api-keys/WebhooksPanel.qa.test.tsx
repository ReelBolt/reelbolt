// @vitest-environment jsdom
/** webhook edit re-open, disabled-reason mapping, deliveries pagination. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WebhookEndpoint } from '@/lib/types/webhook';

const H = vi.hoisted(() => ({
  items: [] as unknown[],
  mutate: vi.fn(),
  updateWebhook: vi.fn(),
  listWebhookDeliveries: vi.fn(),
}));

vi.mock('next/link', () => ({ default: ({ children, href }: { children: unknown; href: string }) => <a href={href}>{children as never}</a> }));
vi.mock('@/lib/hooks/use-webhooks', () => ({
  useWebhooks: () => ({ data: H.items, error: undefined, mutate: H.mutate }),
}));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ data: [] }) }));
vi.mock('@/lib/api/webhooks', () => ({
  createWebhook: vi.fn(),
  rotateWebhookSecret: vi.fn(),
  sendWebhookTest: vi.fn(),
  deleteWebhook: vi.fn(),
  listWebhookDeliveries: (...a: unknown[]) => H.listWebhookDeliveries(...a),
  redeliverWebhook: vi.fn(),
  updateWebhook: (...a: unknown[]) => H.updateWebhook(...a),
}));

import { WebhooksPanel } from './WebhooksPanel';

function endpoint(over: Partial<WebhookEndpoint> = {}): WebhookEndpoint {
  return {
    id: 'w1',
    url: 'https://hooks.example.com/reelbolt',
    description: null,
    events: ['run.step_completed'],
    projectIds: null,
    enabled: true,
    disabledReason: null,
    createdAt: '2026-10-01T00:00:00Z',
    lastSuccessAt: null,
    failingSince: null,
    ...over,
  };
}

function delivery(id: string) {
  return {
    id, eventType: 'run.failed', status: 'delivered', attempts: 1, nextAttemptAt: null,
    lastStatusCode: 200, lastError: null, createdAt: '2026-10-21T00:00:00Z', deliveredAt: '2026-10-21T00:00:01Z',
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
  H.updateWebhook.mockReset().mockResolvedValue(endpoint());
  H.listWebhookDeliveries.mockReset();
});
afterEach(() => cleanup());

describe('WebhooksPanel (QA)', () => {
  it('re-opening Edit on the same endpoint after Cancel still shows its saved URL', async () => {
    renderPanel();
    const editButton = () => screen.getByRole('button', { name: 'Edit webhook https://hooks.example.com/reelbolt' });
    fireEvent.click(editButton());
    const url1 = (await screen.findByLabelText(/Endpoint URL/)) as HTMLInputElement;
    expect(url1.value).toBe('https://hooks.example.com/reelbolt');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByLabelText(/Endpoint URL/)).toBeNull());

    fireEvent.click(editButton());
    const url2 = (await screen.findByLabelText(/Endpoint URL/)) as HTMLInputElement;
    expect(url2.value).toBe('https://hooks.example.com/reelbolt');
  });

  it('maps every server disabledReason through i18n', () => {
    H.items = [
      endpoint({ id: 'a', url: 'https://a.example.com/x', enabled: false, disabledReason: 'api_key_revoked' }),
      endpoint({ id: 'b', url: 'https://b.example.com/x', enabled: false, disabledReason: 'No successful delivery for 72 hours.' }),
      endpoint({ id: 'c', url: 'https://c.example.com/x', enabled: false, disabledReason: 'Disabled by a member.' }),
    ];
    renderPanel();
    expect(screen.getByText('Disabled because its API key was revoked')).toBeTruthy();
    expect(screen.getByText(/Disabled after/)).toBeTruthy();
    expect(screen.getByText('Disabled')).toBeTruthy();
    expect(screen.queryByText('Disabled by a member.')).toBeNull();
  });

  it('pages deliveries with the server cursor and appends the next page', async () => {
    H.listWebhookDeliveries
      .mockResolvedValueOnce({ data: [delivery('d1')], nextCursor: 'c-2' })
      .mockResolvedValueOnce({ data: [delivery('d2')], nextCursor: null });
    renderPanel();
    fireEvent.click(screen.getByRole('button', { name: /View deliveries of/ }));
    await waitFor(() => expect(screen.getAllByText('run.failed')).toHaveLength(1));
    expect(H.listWebhookDeliveries).toHaveBeenLastCalledWith('w1', { limit: 25, cursor: null });
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getAllByText('run.failed')).toHaveLength(2));
    expect(H.listWebhookDeliveries).toHaveBeenLastCalledWith('w1', { limit: 25, cursor: 'c-2' });
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });
});
