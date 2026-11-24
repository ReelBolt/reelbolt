// @vitest-environment jsdom
/**
 * The provider page of the model catalog: one account, its models, each model's capabilities and —
 * for a platform admin only — each capability's prices.
 *
 * The API calls are stubbed so each assertion is on the exact request body the page sends; the price
 * book is served through a stubbed `fetch`, which also proves a workspace page never asks for it.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type {
  InferenceModel,
  InferenceProvider,
  ModelCapability,
  ModelCapabilityPrice,
} from '@/lib/types/inference-provider';

const H = vi.hoisted(() => ({
  provider: undefined as unknown,
  useProviderCalls: [] as unknown[][],
  mutate: vi.fn(),
  push: vi.fn(),
  createModel: vi.fn(),
  updateCapability: vi.fn(),
  createCapability: vi.fn(),
  createPrice: vi.fn(),
  retirePrice: vi.fn(),
  testCapability: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: H.push }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: H.notify } }));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useProvider: (...args: unknown[]) => {
    H.useProviderCalls.push(args);
    return { data: H.provider, isLoading: false, mutate: H.mutate };
  },
}));
vi.mock('@/lib/api/inference-providers', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/inference-providers')>()),
  createModel: (...a: unknown[]) => H.createModel(...a),
  updateCapability: (...a: unknown[]) => H.updateCapability(...a),
  createCapability: (...a: unknown[]) => H.createCapability(...a),
  createPrice: (...a: unknown[]) => H.createPrice(...a),
  retirePrice: (...a: unknown[]) => H.retirePrice(...a),
  testCapability: (...a: unknown[]) => H.testCapability(...a),
}));

import { ProviderDetail } from './ProviderDetail';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const PRICE: ModelCapabilityPrice = {
  id: 'pr-1',
  unit: 'LlmInputTokens',
  variant: null,
  costUsdPerUnit: 0.0000025,
  creditsMillisPerUnitOverride: null,
  // 0.0005 millicredits per token = 0.5 credits per million tokens.
  effectiveCreditsMillisPerUnit: 0.0005,
  effectiveMarginPercent: 16.67,
  effectiveFrom: '2026-01-01T00:00:00.000Z',
  effectiveTo: null,
  notes: null,
};

function cap(id: string, capability: ModelCapability['capability'], over: Partial<ModelCapability> = {}): ModelCapability {
  return {
    id,
    capability,
    isDefault: false,
    isEnabled: true,
    priceSource: 'Table',
    lastTestAt: null,
    lastTestOk: null,
    lastTestError: null,
    prices: [],
    ...over,
  };
}

const GPT: InferenceModel = {
  id: 'm-1',
  modelCode: 'gpt-4o',
  displayName: 'GPT-4o',
  isEnabled: true,
  capabilities: [
    cap('c-chat', 'Chat', { isDefault: true, prices: [PRICE], lastTestAt: '2026-10-01T00:00:00Z', lastTestOk: true }),
    cap('c-vision', 'Vision'),
  ],
};

const WHISPER: InferenceModel = {
  id: 'm-2',
  modelCode: 'whisper-1',
  displayName: 'Whisper',
  isEnabled: true,
  capabilities: [cap('c-asr', 'Transcription', { lastTestAt: '2026-10-01T00:00:00Z', lastTestOk: false, lastTestError: '401' })],
};

function platformProvider(over: Partial<InferenceProvider> = {}): InferenceProvider {
  return {
    id: 'p-1',
    organizationId: null,
    name: 'Azure production',
    kind: 'AzureOpenAI',
    endpoint: 'https://azure.example',
    hasApiKey: true,
    apiKeyLastFour: 'wxyz',
    isEnabled: true,
    isSystem: false,
    timeoutSeconds: null,
    personalOAuthMode: false,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    models: [GPT, WHISPER],
    ...over,
  };
}

/** The same provider as a workspace sees it: the org API answers `prices: null` everywhere. */
function orgProvider(): InferenceProvider {
  const strip = (m: InferenceModel): InferenceModel => ({
    ...m,
    capabilities: m.capabilities.map((c) => ({ ...c, prices: null })),
  });
  return platformProvider({ organizationId: 'org-1', models: [strip(GPT), strip(WHISPER)] });
}

const fetchMock = vi.fn<typeof fetch>();

function renderDetail(scope: 'platform' | 'org' = 'platform') {
  return render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <ProviderDetail
          id="p-1"
          scope={scope}
          breadcrumbs={[{ label: 'Inference Providers', href: '/admin/inference-providers' }]}
          listHref="/admin/inference-providers"
        />
      </MantineProvider>
    </SWRConfig>,
  );
}

beforeEach(() => {
  H.provider = platformProvider();
  H.useProviderCalls = [];
  for (const fn of [H.mutate, H.push, H.createModel, H.updateCapability, H.createCapability, H.createPrice, H.retirePrice, H.testCapability, H.notify]) {
    fn.mockReset();
  }
  H.createModel.mockResolvedValue({});
  H.updateCapability.mockResolvedValue({});
  H.createCapability.mockResolvedValue({});
  H.createPrice.mockResolvedValue({});
  H.retirePrice.mockResolvedValue({});
  H.testCapability.mockResolvedValue({ ok: true, latencyMs: 12, error: null, responsePreview: null });
  fetchMock.mockReset();
  fetchMock.mockImplementation(async () =>
    new Response(
      JSON.stringify([{ unit: 'LlmInputTokens', variant: null, costUsdPerUnit: 0.0000025, notes: 'Vendor list price' }]),
      { status: 200, headers: { 'content-type': 'application/json' } },
    ),
  );
  vi.stubGlobal('fetch', fetchMock);
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('ProviderDetail (platform)', () => {
  it('renders the account, its models and each model’s capabilities', () => {
    renderDetail();
    expect(H.useProviderCalls[0]).toEqual(['p-1', 'platform']);
    expect(screen.getByText('https://azure.example')).toBeTruthy();
    expect(screen.getByText('•••• wxyz')).toBeTruthy();
    expect(screen.getByText('GPT-4o')).toBeTruthy();
    expect(screen.getByText('gpt-4o')).toBeTruthy();
    expect(screen.getByText('Whisper')).toBeTruthy();

    const chat = screen.getByTestId('capability-c-chat');
    expect(within(chat).getByText('Chat')).toBeTruthy();
    // The Default badge and the Default switch's label.
    expect(within(chat).getAllByText('Default')).toHaveLength(2);
    expect(within(chat).getByText(/^Passed/)).toBeTruthy();
    expect(within(screen.getByTestId('capability-c-asr')).getByText(/^Failed/)).toBeTruthy();
    expect(within(screen.getByTestId('capability-c-vision')).getByText('Never tested')).toBeTruthy();
    // A key or endpoint change reaches every model, and the page says so.
    expect(screen.getByText(/changing either applies to every model below/)).toBeTruthy();
  });

  it('adds a model under the provider with its first capability', async () => {
    renderDetail();
    fireEvent.click(screen.getByRole('button', { name: 'Add model' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('combobox', { name: 'Deployment name' }), { target: { value: 'gpt-4o-mini' } });
    fireEvent.change(within(dialog).getByLabelText('Display name'), { target: { value: 'GPT-4o mini' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add model' }));

    await waitFor(() => expect(H.createModel).toHaveBeenCalled());
    expect(H.createModel).toHaveBeenCalledWith(
      'p-1',
      {
        modelCode: 'gpt-4o-mini',
        displayName: 'GPT-4o mini',
        isEnabled: true,
        capabilities: [{ capability: 'Chat', isDefault: false, isEnabled: true }],
      },
      'platform',
    );
    expect(H.mutate).toHaveBeenCalled();
  });

  it('makes a capability the default with a PUT of just that flag', async () => {
    renderDetail();
    fireEvent.click(screen.getByRole('switch', { name: 'Default for GPT-4o · Vision' }));
    await waitFor(() => expect(H.updateCapability).toHaveBeenCalled());
    expect(H.updateCapability).toHaveBeenCalledWith('p-1', 'm-1', 'c-vision', { isDefault: true }, 'platform');
  });

  it('offers only the capabilities this kind serves and the model lacks when adding one', async () => {
    renderDetail();
    fireEvent.click(screen.getByRole('button', { name: 'Add capability to GPT-4o' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('combobox', { name: /Capability/ }));
    // Azure OpenAI serves Chat, Transcription, Vision, Decision and Embedding; the model has Chat and Vision.
    const listbox = await screen.findByRole('listbox', { hidden: true });
    const offered = within(listbox).getAllByRole('option', { hidden: true }).map((o) => o.textContent);
    expect(offered).toEqual(['Transcription (ASR)', 'Decision (Jev / logprob)', 'Embedding (semantic file search)']);
  });

  it('offers "Test again" once a capability has been tested and plain "Test" before that', () => {
    renderDetail();
    const tested = within(screen.getByTestId('capability-c-asr'));
    expect(tested.getByRole('button', { name: 'Test Whisper · Transcription' }).textContent).toBe('Test again');
    const untested = within(screen.getByTestId('capability-c-vision'));
    expect(untested.getByRole('button', { name: /^Test / }).textContent).toBe('Test');
  });

  it('tests a capability and refreshes its last test', async () => {
    renderDetail();
    fireEvent.click(screen.getByRole('button', { name: 'Test Whisper · Transcription' }));
    await waitFor(() => expect(H.testCapability).toHaveBeenCalledWith('p-1', 'm-2', 'c-asr', 'platform'));
    expect(H.mutate).toHaveBeenCalled();
  });

  it('shows each price’s cost, effective credits and margin, token prices per million tokens', () => {
    renderDetail();
    const chat = screen.getByTestId('capability-c-chat');
    expect(within(chat).getByText('$2.5 / 1M tokens')).toBeTruthy();
    expect(within(chat).getByText('0.5 credits / 1M tokens')).toBeTruthy();
    expect(within(chat).getByText('16.7%')).toBeTruthy();
    expect(within(chat).getByText('from markup')).toBeTruthy();
    // A capability with no price says what that costs the platform.
    expect(within(screen.getByTestId('capability-c-vision')).getByText(/No price yet/)).toBeTruthy();
  });

  it('adds a price pre-filled from the price book, sending the override per single unit', async () => {
    renderDetail();
    fireEvent.click(screen.getByRole('button', { name: 'Add price to GPT-4o · Chat' }));
    const dialog = await screen.findByRole('dialog');

    // The price book is asked for this kind, capability and model, and pre-fills the cost.
    await waitFor(() =>
      expect((within(dialog).getByLabelText('Cost (USD per 1M tokens)') as HTMLInputElement).value).toBe('2.5'),
    );
    expect(String(fetchMock.mock.calls[0][0])).toBe('/api/v1/pricing/price-book?kind=AzureOpenAI&capability=Chat&model=gpt-4o');

    // The unit select offers the capability's units only.
    fireEvent.click(within(dialog).getByRole('combobox', { name: 'Unit' }));
    const listbox = await screen.findByRole('listbox', { hidden: true });
    const units = within(listbox).getAllByRole('option', { hidden: true }).map((o) => o.textContent);
    expect(units).toEqual(['LlmInputTokens', 'LlmOutputTokens', 'LlmCachedInputTokens']);

    fireEvent.change(within(dialog).getByLabelText('Credits override (credits per 1M tokens)'), { target: { value: '0.6' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add price' }));

    await waitFor(() => expect(H.createPrice).toHaveBeenCalled());
    const [providerId, modelId, capId, body] = H.createPrice.mock.calls[0];
    expect([providerId, modelId, capId]).toEqual(['p-1', 'm-1', 'c-chat']);
    expect(body).toEqual({
      unit: 'LlmInputTokens',
      variant: undefined,
      costUsdPerUnit: 0.0000025,
      // 0.6 credits per million tokens = 0.0006 millicredits per token.
      creditsMillisPerUnitOverride: 0.0006,
      effectiveFrom: expect.any(String),
      notes: 'Vendor list price',
    });
  });

  it('retires an open price now, after a confirmation', async () => {
    renderDetail();
    fireEvent.click(screen.getByRole('button', { name: 'Retire LlmInputTokens price of GPT-4o · Chat' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Retire' }));
    await waitFor(() => expect(H.retirePrice).toHaveBeenCalled());
    const [providerId, modelId, capId, priceId, at] = H.retirePrice.mock.calls[0];
    expect([providerId, modelId, capId, priceId]).toEqual(['p-1', 'm-1', 'c-chat', 'pr-1']);
    expect(Number.isNaN(Date.parse(at as string))).toBe(false);
  });

  it('keeps the system provider read-only except for its prices', () => {
    H.provider = platformProvider({
      isSystem: true,
      name: 'ReelBolt compute',
      models: [{ id: 'm-c', modelCode: 'render', displayName: 'Render', isEnabled: true, capabilities: [cap('c-comp', 'Compute')] }],
    });
    renderDetail();
    expect(screen.getByText('System provider')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add model' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Test Render · Compute' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Add price to Render · Compute' })).toBeTruthy();
  });
});

describe('ProviderDetail (org)', () => {
  it('reads the workspace route, renders no price and never calls a price route', async () => {
    H.provider = orgProvider();
    renderDetail('org');
    expect(H.useProviderCalls[0]).toEqual(['p-1', 'org']);
    expect(screen.getByText('GPT-4o')).toBeTruthy();
    expect(screen.queryByText(/^Prices/)).toBeNull();
    expect(screen.queryByText('$2.5 / 1M tokens')).toBeNull();
    expect(screen.queryByRole('button', { name: /^Add price/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Retire/ })).toBeNull();

    // The capability actions still work, against the workspace route.
    fireEvent.click(screen.getByRole('switch', { name: 'Default for GPT-4o · Vision' }));
    await waitFor(() => expect(H.updateCapability).toHaveBeenCalledWith('p-1', 'm-1', 'c-vision', { isDefault: true }, 'org'));
    fireEvent.click(screen.getByRole('button', { name: 'Test GPT-4o · Chat' }));
    await waitFor(() => expect(H.testCapability).toHaveBeenCalledWith('p-1', 'm-1', 'c-chat', 'org'));

    expect(H.createPrice).not.toHaveBeenCalled();
    expect(H.retirePrice).not.toHaveBeenCalled();
    // Nothing went to the network at all: no price book, no pricing settings.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('even ignores prices a response might carry, because the scope decides', () => {
    // Defence in depth: should the org API ever answer prices, the workspace page still shows none.
    H.provider = platformProvider({ organizationId: 'org-1' });
    renderDetail('org');
    expect(screen.queryByText('$2.5 / 1M tokens')).toBeNull();
  });
});
