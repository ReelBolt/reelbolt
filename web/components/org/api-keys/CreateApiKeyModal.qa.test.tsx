// @vitest-environment jsdom
/**
 * the create request body against OrgApiKeysController.Create — scope names, projectIds,
 * expiresAt window (> now+1h, <= now+365d+10min), caps in whole credits — plus the Trial notice,
 * the 402 handling and the secret leaving the DOM on close.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';

const H = vi.hoisted(() => ({
  createApiKey: vi.fn(),
  planKey: 'pro' as string,
}));

vi.mock('next/link', () => ({ default: ({ children, href }: { children: unknown; href: string }) => <a href={href}>{children as never}</a> }));
vi.mock('@/lib/hooks/use-projects', () => ({
  useProjects: () => ({ data: [{ id: 'p-1', name: 'Alpha' }, { id: 'p-2', name: 'Beta' }] }),
}));
vi.mock('@/lib/hooks/use-billing', () => ({
  useBillingSummary: () => ({ data: { subscription: { planKey: H.planKey } } }),
}));
vi.mock('@/lib/api/api-keys', () => ({
  createApiKey: (...a: unknown[]) => H.createApiKey(...a),
}));

import { CreateApiKeyModal } from './CreateApiKeyModal';

const HOUR = 60 * 60 * 1000;
const DAY = 24 * HOUR;
const SCOPES = new Set([
  'projects.read', 'projects.write', 'files.read', 'files.write', 'workflows.read', 'workflows.write',
  'runs.read', 'runs.execute', 'outputs.read', 'webhooks.manage',
]);

function created(secret = 'rbk_secret_once') {
  return { key: { id: 'k1', name: 'CI' }, secret };
}

function renderModal(onClose = vi.fn(), onCreated = vi.fn()) {
  return render(
    <MantineProvider>
      <CreateApiKeyModal opened onClose={onClose} onCreated={onCreated} />
    </MantineProvider>,
  );
}

function fillName(value = 'CI pipeline') {
  fireEvent.change(screen.getByLabelText(/^Name/), { target: { value } });
}

function body(): Record<string, unknown> {
  expect(H.createApiKey).toHaveBeenCalledTimes(1);
  return H.createApiKey.mock.calls[0][0] as Record<string, unknown>;
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
  Element.prototype.scrollIntoView = vi.fn();
  H.planKey = 'pro';
  H.createApiKey.mockReset().mockResolvedValue(created());
});
afterEach(() => cleanup());

describe('CreateApiKeyModal request body (QA)', () => {
  it('never ticks "Allow sending media" through Select all; ticked on purpose, it is sent as media.egress', async () => {
    renderModal();
    fillName();
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    const egress = screen.getByRole('checkbox', { name: /Allow sending media/ }) as HTMLInputElement;
    expect(egress.checked).toBe(false);
    fireEvent.click(egress);
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    expect(body().scopes as string[]).toContain('media.egress');
  });

  it('sends only server-known scope names, no projectIds for all projects, and a 90-day default expiry', async () => {
    renderModal();
    fillName();
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    const before = Date.now();
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    const b = body();
    expect(b.name).toBe('CI pipeline');
    expect((b.scopes as string[]).length).toBeGreaterThan(0);
    for (const s of b.scopes as string[]) expect(SCOPES.has(s)).toBe(true);
    expect('projectIds' in b).toBe(false);
    expect(b.caps).toEqual({ dailyCredits: 200 });
    expect('confirmCaps' in b).toBe(false);
    const at = Date.parse(b.expiresAt as string);
    expect(at - before).toBeGreaterThan(89 * DAY);
    expect(at - before).toBeLessThan(91 * DAY);
  });

  it('clamps the 1-year preset inside the server window (<= now+365d+10min, > now+1h)', async () => {
    renderModal();
    fillName();
    fireEvent.click(screen.getByLabelText('Start runs'));
    fireEvent.click(screen.getByLabelText('1 year'));
    const now = Date.now();
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    const at = Date.parse(body().expiresAt as string);
    expect(at).toBeLessThanOrEqual(now + 365 * DAY + 10 * 60 * 1000);
    expect(at).toBeGreaterThan(now + HOUR);
    expect(body().scopes).toEqual(['runs.execute']);
  });

  it('sends caps as whole credits (the server multiplies by 1000) and omits blank windows', async () => {
    renderModal();
    fillName();
    fireEvent.click(screen.getByLabelText('Start runs'));
    fireEvent.change(screen.getByLabelText('Daily credits'), { target: { value: '150' } });
    fireEvent.change(screen.getByLabelText('Monthly credits'), { target: { value: '9000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    expect(body().caps).toEqual({ dailyCredits: 150, monthlyCredits: 9000 });
  });

  it('sends the chosen project ids when restricted to some projects', async () => {
    renderModal();
    fillName();
    fireEvent.click(screen.getByLabelText('Start runs'));
    fireEvent.click(screen.getByLabelText('Some projects'));
    const select = screen.getByPlaceholderText('Choose projects');
    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: 'Beta', hidden: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    expect(body().projectIds).toEqual(['p-2']);
  });

  it('refuses a custom expiry less than one hour ahead on the client (server: invalid_expiry, English message)', async () => {
    renderModal();
    fillName();
    fireEvent.click(screen.getByLabelText('Start runs'));
    fireEvent.click(screen.getByLabelText('Custom'));
    const pad = (n: number) => String(n).padStart(2, '0');
    const past = new Date(Date.now() - 3 * DAY);
    fireEvent.change(screen.getByLabelText('Expiry date'), {
      target: { value: `${past.getFullYear()}-${pad(past.getMonth() + 1)}-${pad(past.getDate())}` },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await new Promise((r) => setTimeout(r, 20));
    // Expected: a translated client-side error and no request. A past date reaching the server is a gap.
    expect(H.createApiKey).not.toHaveBeenCalled();
  });
});

describe('CreateApiKeyModal plan + secret (QA)', () => {
  it('shows the Trial notice before submit', () => {
    H.planKey = 'trial';
    renderModal();
    expect(screen.getByTestId('api-key-plan-hint').textContent).toMatch(/Running apps and uploading files need a paid plan/);
  });

  it('maps a 402 PLAN_NOT_ENTITLED to the plan alert instead of the raw message', async () => {
    H.createApiKey.mockRejectedValue(new ApiError('Plan does not include API access', 402, [], { code: 'PLAN_NOT_ENTITLED' }));
    renderModal();
    fillName();
    fireEvent.click(screen.getByLabelText('Start runs'));
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    expect(await screen.findByTestId('api-key-plan-blocked')).toBeTruthy();
    expect(screen.queryByText('Plan does not include API access')).toBeNull();
  });

  it('shows the secret once, does not log it, and drops it when closed', async () => {
    const log = vi.spyOn(console, 'log');
    const info = vi.spyOn(console, 'info');
    const onClose = vi.fn();
    const onCreated = vi.fn();
    const view = renderModal(onClose, onCreated);
    fillName();
    fireEvent.click(screen.getByLabelText('Start runs'));
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    expect(await screen.findByText('rbk_secret_once')).toBeTruthy();
    expect(onCreated).toHaveBeenCalledTimes(1);
    expect(onCreated.mock.calls[0]).toEqual([]);
    fireEvent.click(screen.getByRole('button', { name: 'I have stored it' }));
    expect(onClose).toHaveBeenCalled();
    // Reopen: the same instance must come back on a blank form, not the old secret.
    view.rerender(
      <MantineProvider>
        <CreateApiKeyModal opened onClose={onClose} onCreated={onCreated} />
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.queryByText('rbk_secret_once')).toBeNull());
    const logged = [...log.mock.calls, ...info.mock.calls].flat().map(String).join(' ');
    expect(logged.includes('rbk_secret_once')).toBe(false);
  });
});
