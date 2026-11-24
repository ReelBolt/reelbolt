/**
 * The inference-provider client: every route of the model-catalog contract (provider accounts,
 * models, capabilities, platform-only prices) and the member-facing reads. `fetch` is stubbed, so
 * each assertion is on the URL, method and body the client builds.
 */

import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  createCapability,
  createImageToolModels,
  createModel,
  createPrice,
  createProvider,
  deleteCapability,
  deleteModel,
  deleteProvider,
  getConsolidationReport,
  getProviderStatus,
  getSelectableProviders,
  getSelectableProvidersUrl,
  isByoNotInPlan,
  isProviderAccessDenied,
  retirePrice,
  testCapability,
  testProviderConfig,
  updateCapability,
  updateModel,
  updateProvider,
} from './inference-providers';
import { ApiError } from './client';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('getProviderStatus', () => {
  it('GETs the status for one capability and returns it unchanged', async () => {
    const status = {
      capability: 'SpeechSynthesis',
      providerName: 'qa-local-fish',
      lastTestOk: true,
      lastTestAt: '2026-10-04T10:00:00Z',
      healthy: true,
      usingFallback: true,
      message: 'Your default voice service failed its last connection check, so another voice service that is working will be used instead.',
    };
    const fetchMock = vi.fn<typeof fetch>(async () =>
      new Response(JSON.stringify(status), { status: 200, headers: { 'content-type': 'application/json' } }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getProviderStatus('SpeechSynthesis')).resolves.toEqual(status);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/inference-providers/status?capability=SpeechSynthesis');
    expect(fetchMock.mock.calls[0][1]?.method ?? 'GET').toBe('GET');
  });
});

/**
 * A17-UI: the same CRUD against a WORKSPACE's own providers. The scope argument is the only thing
 * that changes, and it must change the URL — a workspace write sent to the platform route would be a
 * 403 for its admin rather than the write they asked for.
 */
describe('provider scope', () => {
  const ok = (body: unknown = {}) =>
    vi.fn<typeof fetch>(async () =>
      new Response(body === undefined ? null : JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );

  it('defaults to the platform routes', async () => {
    const fetchMock = ok({ id: 'p1' });
    vi.stubGlobal('fetch', fetchMock);
    await createProvider({ name: 'n', kind: 'AzureOpenAI', endpoint: 'e', isEnabled: true, personalOAuthMode: false });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/inference-providers');
  });

  it("sends a workspace write to the singular /api/v1/org/ route", async () => {
    const fetchMock = ok({ id: 'p1' });
    vi.stubGlobal('fetch', fetchMock);
    await createProvider({ name: 'n', kind: 'AzureOpenAI', endpoint: 'e', isEnabled: true, personalOAuthMode: false }, 'org');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/org/inference-providers');
  });

  it('scopes delete and test too', async () => {
    const fetchMock = ok({ ok: true, latencyMs: 1, error: null, responsePreview: null });
    vi.stubGlobal('fetch', fetchMock);
    await deleteProvider('p1', 'org');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/org/inference-providers/p1');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
    await testCapability('p1', 'm1', 'c1', 'org');
    expect(fetchMock.mock.calls[1][0]).toBe('/api/v1/org/inference-providers/p1/models/m1/capabilities/c1/test');
    expect(fetchMock.mock.calls[1][1]?.method).toBe('POST');
  });
});

describe('model catalog routes', () => {
  const ok = (body: unknown = {}) =>
    vi.fn<typeof fetch>(async () =>
      new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } }),
    );
  const call = (fetchMock: ReturnType<typeof ok>, i = 0) => ({
    url: fetchMock.mock.calls[i][0],
    method: fetchMock.mock.calls[i][1]?.method ?? 'GET',
    body: fetchMock.mock.calls[i][1]?.body ? JSON.parse(String(fetchMock.mock.calls[i][1]?.body)) : undefined,
  });

  it('creates a provider with its first models in one POST', async () => {
    const fetchMock = ok({ id: 'p1' });
    vi.stubGlobal('fetch', fetchMock);
    await createProvider({
      name: 'Azure', kind: 'AzureOpenAI', endpoint: 'https://a.example', apiKey: 'k', isEnabled: true, personalOAuthMode: false,
      models: [{ modelCode: 'gpt-4o', displayName: 'GPT-4o', isEnabled: true, capabilities: [{ capability: 'Chat', isDefault: true, isEnabled: true }] }],
    });
    expect(call(fetchMock)).toEqual({
      url: '/api/v1/inference-providers',
      method: 'POST',
      body: {
        name: 'Azure', kind: 'AzureOpenAI', endpoint: 'https://a.example', apiKey: 'k', isEnabled: true, personalOAuthMode: false,
        models: [{ modelCode: 'gpt-4o', displayName: 'GPT-4o', isEnabled: true, capabilities: [{ capability: 'Chat', isDefault: true, isEnabled: true }] }],
      },
    });
  });

  it('keeps, replaces or clears the key: omitted, a value, or ""', async () => {
    const fetchMock = ok({ id: 'p1' });
    vi.stubGlobal('fetch', fetchMock);
    await updateProvider('p1', { name: 'n' });
    await updateProvider('p1', { apiKey: 'new' });
    await updateProvider('p1', { apiKey: '' });
    expect(call(fetchMock, 0)).toEqual({ url: '/api/v1/inference-providers/p1', method: 'PUT', body: { name: 'n' } });
    expect(call(fetchMock, 1).body).toEqual({ apiKey: 'new' });
    expect(call(fetchMock, 2).body).toEqual({ apiKey: '' });
  });

  it('adds, edits and deletes a model under a provider', async () => {
    const fetchMock = ok({ id: 'm1' });
    vi.stubGlobal('fetch', fetchMock);
    await createModel('p1', { modelCode: 'gpt-4o', displayName: 'GPT-4o', isEnabled: true, capabilities: [] });
    await updateModel('p1', 'm1', { displayName: 'GPT 4o' }, 'org');
    await deleteModel('p1', 'm1');
    expect(call(fetchMock, 0)).toMatchObject({ url: '/api/v1/inference-providers/p1/models', method: 'POST' });
    expect(call(fetchMock, 1)).toEqual({ url: '/api/v1/org/inference-providers/p1/models/m1', method: 'PUT', body: { displayName: 'GPT 4o' } });
    expect(call(fetchMock, 2)).toMatchObject({ url: '/api/v1/inference-providers/p1/models/m1', method: 'DELETE' });
  });

  it('adds, edits and deletes a capability under a model', async () => {
    const fetchMock = ok({ id: 'c1' });
    vi.stubGlobal('fetch', fetchMock);
    await createCapability('p1', 'm1', { capability: 'Vision', isDefault: false, isEnabled: true });
    await updateCapability('p1', 'm1', 'c1', { isDefault: true });
    await deleteCapability('p1', 'm1', 'c1');
    expect(call(fetchMock, 0)).toEqual({
      url: '/api/v1/inference-providers/p1/models/m1/capabilities',
      method: 'POST',
      body: { capability: 'Vision', isDefault: false, isEnabled: true },
    });
    expect(call(fetchMock, 1)).toEqual({ url: '/api/v1/inference-providers/p1/models/m1/capabilities/c1', method: 'PUT', body: { isDefault: true } });
    expect(call(fetchMock, 2)).toMatchObject({ url: '/api/v1/inference-providers/p1/models/m1/capabilities/c1', method: 'DELETE' });
  });

  it('adds and retires prices on the platform route only', async () => {
    const fetchMock = ok({ id: 'pr1' });
    vi.stubGlobal('fetch', fetchMock);
    await createPrice('p1', 'm1', 'c1', { unit: 'LlmInputTokens', costUsdPerUnit: 0.0000025, creditsMillisPerUnitOverride: 0.0005 });
    await retirePrice('p1', 'm1', 'c1', 'pr1', '2026-10-12T10:00:00.000Z');
    expect(call(fetchMock, 0)).toEqual({
      url: '/api/v1/inference-providers/p1/models/m1/capabilities/c1/prices',
      method: 'POST',
      body: { unit: 'LlmInputTokens', costUsdPerUnit: 0.0000025, creditsMillisPerUnitOverride: 0.0005 },
    });
    expect(call(fetchMock, 1)).toMatchObject({
      url: '/api/v1/inference-providers/p1/models/m1/capabilities/c1/prices/pr1/retire?at=2026-10-12T10%3A00%3A00.000Z',
      method: 'POST',
    });
  });

  it('tests an unsaved configuration, reusing a saved key by provider id', async () => {
    const fetchMock = ok({ ok: true, latencyMs: 1, error: null, responsePreview: null });
    vi.stubGlobal('fetch', fetchMock);
    await testProviderConfig({ kind: 'Anthropic', endpoint: 'e', modelCode: 'claude', capability: 'Chat', providerId: 'p1' });
    expect(call(fetchMock)).toEqual({
      url: '/api/v1/inference-providers/test',
      method: 'POST',
      body: { kind: 'Anthropic', endpoint: 'e', modelCode: 'claude', capability: 'Chat', providerId: 'p1' },
    });
  });

  it('adds the fal image-tool models and reads the consolidation report', async () => {
    const fetchMock = ok([]);
    vi.stubGlobal('fetch', fetchMock);
    await createImageToolModels('p1', 'org');
    await getConsolidationReport();
    expect(call(fetchMock, 0)).toMatchObject({ url: '/api/v1/org/inference-providers/p1/image-tools', method: 'POST' });
    expect(call(fetchMock, 1)).toMatchObject({ url: '/api/v1/inference-providers/consolidation-report', method: 'GET' });
  });
});

/** The member-facing list is NOT scope-parameterized: the server derives the workspace from the token. */
describe('getSelectableProviders', () => {
  it('omits the capability filter when none is asked for', () => {
    expect(getSelectableProvidersUrl(null)).toBe('/api/v1/inference-providers/selectable');
    expect(getSelectableProvidersUrl()).toBe('/api/v1/inference-providers/selectable');
  });

  it('encodes the capability when one is asked for', () => {
    expect(getSelectableProvidersUrl('SpeechSynthesis')).toBe(
      '/api/v1/inference-providers/selectable?capability=SpeechSynthesis',
    );
  });

  it('GETs the list', async () => {
    const rows = [{ id: 'p1', name: 'Jev', capability: 'Decision', model: 'jev-latest', isPlatformManaged: true, kind: 'TypeSafe', providerName: 'TypeSafe' }];
    const fetchMock = vi.fn<typeof fetch>(async () =>
      new Response(JSON.stringify(rows), { status: 200, headers: { 'content-type': 'application/json' } }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getSelectableProviders('Decision')).resolves.toEqual(rows);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/inference-providers/selectable?capability=Decision');
  });
});

/**
 * The two refusals the UI must render as something other than an error: 402 is the workspace's PLAN,
 * 403 is the caller's ROLE. Anything else stays an error.
 */
describe('refusal predicates', () => {
  it('recognises only a 402 as the BYO plan limit', () => {
    expect(isByoNotInPlan(new ApiError('plan', 402))).toBe(true);
    expect(isByoNotInPlan(new ApiError('forbidden', 403))).toBe(false);
    expect(isByoNotInPlan(new ApiError('conflict', 409))).toBe(false);
    expect(isByoNotInPlan(new Error('plan'))).toBe(false);
    expect(isByoNotInPlan(null)).toBe(false);
  });

  it('recognises a 403 as a role refusal', () => {
    expect(isProviderAccessDenied(new ApiError('forbidden', 403))).toBe(true);
    expect(isProviderAccessDenied(new ApiError('plan', 402))).toBe(false);
    expect(isProviderAccessDenied(undefined)).toBe(false);
  });
});
