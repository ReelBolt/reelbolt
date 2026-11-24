// @vitest-environment jsdom
/** Daily spend limit on the key screens: create default and confirmation, legacy warning, error mapping, trial notice. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { ApiKeyItem } from '@/lib/types/api-key';

const H = vi.hoisted(() => ({
  createApiKey: vi.fn(),
  updateApiKey: vi.fn(),
  planKey: 'pro' as string,
}));

vi.mock('next/link', () => ({ default: ({ children, href }: { children: unknown; href: string }) => <a href={href}>{children as never}</a> }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-billing', () => ({
  useBillingSummary: () => ({ data: { subscription: { planKey: H.planKey } } }),
}));
vi.mock('@/lib/api/api-keys', () => ({
  createApiKey: (...a: unknown[]) => H.createApiKey(...a),
  updateApiKey: (...a: unknown[]) => H.updateApiKey(...a),
}));

import { CreateApiKeyModal } from './CreateApiKeyModal';
import { EditApiKeyModal } from './EditApiKeyModal';
import { ApiKeysTable } from './ApiKeysTable';
import { TrialReadOnlyNotice } from './TrialReadOnlyNotice';
import { dailyCapNeedsConfirmation, mapApiKeyError } from './daily-cap';

function key(status: 'capped' | 'waived' | 'legacy_uncapped', daily: number | null): ApiKeyItem {
  return {
    id: 'k1', name: 'CI', keyPrefix: 'rbk_abc', lastFour: '1234', createdBy: { id: 'u', name: 'Ann' },
    scopes: ['runs.read'], caps: { dailyCredits: daily, weeklyCredits: null, monthlyCredits: null },
    expiresAt: null, neverExpires: true, createdAt: '2026-01-01T00:00:00Z', lastUsedAt: null,
    status: 'active', revokedAt: null, dailyCap: { status, defaultCredits: 200 },
  };
}

function wrap(node: React.ReactNode) {
  return render(<MantineProvider>{node}</MantineProvider>);
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
  H.createApiKey.mockReset().mockResolvedValue({ key: key('capped', 200), secret: 's' });
  H.updateApiKey.mockReset().mockResolvedValue(key('capped', 200));
});
afterEach(() => cleanup());

function createFilled() {
  wrap(<CreateApiKeyModal opened onClose={vi.fn()} onCreated={vi.fn()} />);
  fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'CI' } });
  fireEvent.click(screen.getByLabelText('Start runs'));
}

describe('create modal daily limit', () => {
  it('prefills 200 and sends it without a confirmation flag', async () => {
    createFilled();
    expect((screen.getByLabelText('Daily credits') as HTMLInputElement).value).toBe('200');
    expect(screen.queryByTestId('api-key-cap-confirm')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    const body = H.createApiKey.mock.calls[0][0] as Record<string, unknown>;
    expect(body.caps).toEqual({ dailyCredits: 200 });
    expect('confirmCaps' in body).toBe(false);
  });

  it('asks for confirmation when the limit is removed and then sends confirmCaps', async () => {
    createFilled();
    fireEvent.click(screen.getByLabelText('No daily limit'));
    expect(screen.getByTestId('api-key-cap-confirm')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await screen.findByText('Tick the box to confirm the daily limit change.');
    expect(H.createApiKey).not.toHaveBeenCalled();
    fireEvent.click(screen.getByLabelText('I understand and want to do this'));
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(H.createApiKey).toHaveBeenCalled());
    const body = H.createApiKey.mock.calls[0][0] as Record<string, unknown>;
    expect(body.caps).toEqual({ noDailyCap: true });
    expect(body.confirmCaps).toBe(true);
  });

  it('asks for confirmation when the limit is raised above the default', () => {
    createFilled();
    fireEvent.change(screen.getByLabelText('Daily credits'), { target: { value: '500' } });
    expect(screen.getByTestId('api-key-cap-confirm')).toBeTruthy();
  });
});

describe('edit modal', () => {
  it('warns on a legacy key and sets the default with one click, without a confirmation', async () => {
    wrap(<EditApiKeyModal item={key('legacy_uncapped', null)} opened onClose={vi.fn()} onSaved={vi.fn()} />);
    expect(screen.getByTestId('api-key-legacy-warning')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Set daily limit to 200' }));
    await waitFor(() => expect(H.updateApiKey).toHaveBeenCalledWith('k1', { caps: { dailyCredits: 200 } }));
  });

  it('leaves the daily limit alone when a legacy key is saved with the field empty', async () => {
    wrap(<EditApiKeyModal item={key('legacy_uncapped', null)} opened onClose={vi.fn()} onSaved={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(H.updateApiKey).toHaveBeenCalled());
    const body = H.updateApiKey.mock.calls[0][1] as { caps: Record<string, unknown> };
    expect('dailyCredits' in body.caps).toBe(false);
    expect('noDailyCap' in body.caps).toBe(false);
  });

  it('shows the neutral note for a waived key', () => {
    wrap(<EditApiKeyModal item={key('waived', null)} opened onClose={vi.fn()} onSaved={vi.fn()} />);
    expect(screen.getByTestId('api-key-waived-note').textContent).toBe('No daily limit (confirmed)');
    expect(screen.queryByTestId('api-key-legacy-warning')).toBeNull();
  });
});

describe('table', () => {
  const noop = vi.fn();
  it('flags a legacy key and notes a waived one', () => {
    wrap(
      <ApiKeysTable
        items={[key('legacy_uncapped', null), { ...key('waived', null), id: 'k2' }]}
        capsEnforced
        onEdit={noop}
        onViewAudit={noop}
        onRevoke={noop}
      />,
    );
    expect(screen.getByTestId('api-key-legacy-badge')).toBeTruthy();
    expect(screen.getByTestId('api-key-waived-badge')).toBeTruthy();
  });

  it('shows the read-only notice only on the Trial plan, with no keys needed', () => {
    H.planKey = 'trial';
    const { unmount } = wrap(<TrialReadOnlyNotice />);
    expect(screen.queryByTestId('api-key-trial-notice')).not.toBeNull();
    unmount();
    H.planKey = 'pro';
    wrap(<TrialReadOnlyNotice />);
    expect(screen.queryByTestId('api-key-trial-notice')).toBeNull();
  });
});

describe('edit modal re-save rules', () => {
  async function save(item: ApiKeyItem) {
    wrap(<EditApiKeyModal item={item} opened onClose={vi.fn()} onSaved={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(H.updateApiKey).toHaveBeenCalled());
    return H.updateApiKey.mock.calls[0][1] as { caps: Record<string, unknown>; confirmCaps?: boolean };
  }

  it('re-saving an unchanged above-default value sends confirmCaps with no confirm step', async () => {
    const body = await save(key('capped', 500));
    expect(screen.queryByTestId('api-key-cap-confirm')).toBeNull();
    expect(body.caps.dailyCredits).toBe(500);
    expect(body.confirmCaps).toBe(true);
  });

  it('re-saving a waived (no limit) key sends confirmCaps with no confirm step', async () => {
    const body = await save(key('waived', null));
    expect(screen.queryByTestId('api-key-cap-confirm')).toBeNull();
    expect(body.caps.noDailyCap).toBe(true);
    expect(body.confirmCaps).toBe(true);
  });

  it('a lowered value sends no confirmCaps', async () => {
    const body = await save(key('capped', 100));
    expect(body.caps.dailyCredits).toBe(100);
    expect('confirmCaps' in body).toBe(false);
  });

  it('raising past the current cap shows the confirm step', () => {
    wrap(<EditApiKeyModal item={key('capped', 500)} opened onClose={vi.fn()} onSaved={vi.fn()} />);
    fireEvent.change(screen.getByLabelText('Daily credits'), { target: { value: '900' } });
    expect(screen.getByTestId('api-key-cap-confirm')).toBeTruthy();
  });
});

describe('create modal server default', () => {
  it('prefills the default the server sent', () => {
    wrap(<CreateApiKeyModal opened onClose={vi.fn()} onCreated={vi.fn()} defaultDailyCap={300} />);
    expect((screen.getByLabelText('Daily credits') as HTMLInputElement).value).toBe('300');
  });
});

describe('error mapping and confirmation rule', () => {
  const t = (k: string) => k;
  it('maps the two new refusal codes and nothing else', () => {
    expect(mapApiKeyError(new ApiError('x', 400, [], { code: 'cap_confirmation_required' }), t)).toBe('capGuard.confirmRequired');
    expect(mapApiKeyError(new ApiError('x', 402, [], { code: 'API_PLAN_READ_ONLY' }), t)).toBe('capGuard.readOnlyPlan');
    expect(mapApiKeyError(new ApiError('x', 400, [], { code: 'other' }), t)).toBeNull();
    expect(mapApiKeyError(new Error('x'), t)).toBeNull();
  });

  it('requires confirmation only for removal or a raise above the default', () => {
    expect(dailyCapNeedsConfirmation({ noDailyCap: false, daily: 200 })).toBe(false);
    expect(dailyCapNeedsConfirmation({ noDailyCap: false, daily: 201 })).toBe(true);
    expect(dailyCapNeedsConfirmation({ noDailyCap: false, daily: 500, current: 500 })).toBe(false);
    expect(dailyCapNeedsConfirmation({ noDailyCap: false, daily: 501, current: 500 })).toBe(true);
    expect(dailyCapNeedsConfirmation({ noDailyCap: false, daily: 150, current: 100 })).toBe(false);
    expect(dailyCapNeedsConfirmation({ noDailyCap: true, daily: '' })).toBe(true);
    expect(dailyCapNeedsConfirmation({ noDailyCap: true, daily: '', currentWaived: true })).toBe(false);
  });
});
