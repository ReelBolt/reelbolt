// @vitest-environment jsdom
/**
 * A17-UI: who may change an agent's provider, and through WHICH endpoint.
 *
 * A built-in agent definition is a row shared by every workspace, so a workspace cannot write its
 * choice on it. A platform admin edits the definition; a workspace Owner/Admin gets its own
 * override, keyed by agent type. Sending the wrong one is the bug this test exists for: both calls
 * would "succeed" against the wrong lever and quietly change the wrong scope.
 */
import { beforeAll, afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  isPlatformAdmin: false,
  orgRole: 'Owner' as string | null,
  selectable: [] as unknown[],
  selectableCalls: [] as { capability: unknown; enabled: unknown }[],
  overrides: [] as unknown[],
  models: [] as unknown[],
  modelsCalls: [] as unknown[],
  mutateModels: vi.fn(),
  setAgent: vi.fn(),
  setOrg: vi.fn(),
  clearOrg: vi.fn(),
  mutateOverrides: vi.fn(),
}));

vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({
    isPlatformAdmin: H.isPlatformAdmin,
    isOrgAdmin: H.orgRole === 'Owner' || H.orgRole === 'Admin',
    user: { orgRole: H.orgRole },
  }),
}));
// Only the selectable hook exists in this mock: the platform-admin path must not read the full
// provider tree any more, so a call to anything else fails the test.
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: (capability: unknown, enabled: unknown) => {
    H.selectableCalls.push({ capability, enabled });
    return { data: H.selectable, isLoading: false };
  },
}));
vi.mock('@/lib/hooks/use-agents', () => ({
  useOrgAgentOverrides: () => ({ data: H.overrides, mutate: H.mutateOverrides }),
  useOrgAgentModels: (enabled: unknown) => {
    H.modelsCalls.push(enabled);
    return { data: enabled ? H.models : undefined, mutate: H.mutateModels };
  },
}));
vi.mock('@/lib/api/agents', () => ({
  setAgentInferenceProvider: (...a: unknown[]) => H.setAgent(...a),
  setOrgAgentOverride: (...a: unknown[]) => H.setOrg(...a),
  clearOrgAgentOverride: (...a: unknown[]) => H.clearOrg(...a),
}));

import { AgentInferenceProviderSelect } from './AgentInferenceProviderSelect';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  H.isPlatformAdmin = false;
  H.orgRole = 'Owner';
  H.overrides = [];
  H.models = [];
  H.modelsCalls = [];
  H.selectableCalls = [];
});

const SELECTABLE = [
  { id: 'p-org', name: 'Acme vLLM', capability: 'Chat', model: 'm', isPlatformManaged: false, kind: 'OpenAICompatible', providerName: 'Acme' },
];
const PLATFORM_ROW = {
  id: 'p-plat', name: 'GPT-4o', capability: 'Chat', model: 'gpt-4o', isPlatformManaged: true, kind: 'AzureOpenAI', providerName: 'ReelBolt Azure',
};

function renderSelect(over: Partial<React.ComponentProps<typeof AgentInferenceProviderSelect>> = {}) {
  return render(
    <MantineProvider>
      <AgentInferenceProviderSelect
        agentId="a-1"
        inferenceProviderId={null}
        inferenceProviderName={null}
        onUpdated={vi.fn()}
        agentType="Director"
        isBuiltIn
        {...over}
      />
    </MantineProvider>,
  );
}

describe('AgentInferenceProviderSelect', () => {
  it('gives a platform admin the platform-wide per-agent override, from the selectable Chat list', async () => {
    H.isPlatformAdmin = true;
    // A platform admin's own workspace rows are not offered for a definition every workspace shares.
    H.selectable = [PLATFORM_ROW, ...SELECTABLE];
    H.setAgent.mockResolvedValue({});
    renderSelect();

    expect(H.selectableCalls[0]).toEqual({ capability: 'Chat', enabled: true });
    fireEvent.click(screen.getByRole('combobox', { name: 'Model' }));
    expect(screen.queryByText('Acme vLLM · Acme')).toBeNull();
    fireEvent.click(await screen.findByText('GPT-4o · ReelBolt Azure'));

    await waitFor(() => expect(H.setAgent).toHaveBeenCalledWith('a-1', 'p-plat'));
    expect(H.setOrg).not.toHaveBeenCalled();
  });

  it("keeps a platform admin's current choice visible by name when it is no longer selectable", () => {
    H.isPlatformAdmin = true;
    H.selectable = [PLATFORM_ROW];
    renderSelect({ inferenceProviderId: 'gone-1234567', inferenceProviderName: 'Old GPT' });
    expect(screen.getByDisplayValue('Old GPT')).toBeTruthy();
  });

  it('gives a workspace Owner the workspace override for a built-in agent, offered only Chat rows', async () => {
    H.selectable = SELECTABLE;
    H.setOrg.mockResolvedValue({});
    renderSelect();

    fireEvent.click(screen.getByRole('combobox', { name: 'Model' }));
    fireEvent.click(await screen.findByText('Acme vLLM · Acme'));

    await waitFor(() => expect(H.setOrg).toHaveBeenCalledWith('Director', 'p-org'));
    // Never the platform lever: a workspace cannot write on a shared built-in row.
    expect(H.setAgent).not.toHaveBeenCalled();
    expect(H.mutateOverrides).toHaveBeenCalled();
  });

  it("shows the workspace's existing override for this agent type", () => {
    H.overrides = [{ agentType: 'Director', inferenceProviderId: 'p-org', updatedAt: '2026-10-01T00:00:00Z' }];
    H.selectable = SELECTABLE;
    renderSelect();
    expect(screen.getByDisplayValue('Acme vLLM · Acme')).toBeTruthy();
  });

  it('clears the workspace override when the default is chosen again', async () => {
    H.overrides = [{ agentType: 'Director', inferenceProviderId: 'p-org', updatedAt: '2026-10-01T00:00:00Z' }];
    H.selectable = SELECTABLE;
    H.clearOrg.mockResolvedValue(undefined);
    renderSelect();

    fireEvent.click(screen.getByRole('combobox', { name: 'Model' }));
    fireEvent.click(await screen.findByText('Default model'));

    await waitFor(() => expect(H.clearOrg).toHaveBeenCalledWith('Director'));
  });

  it('is read-only for a plain Member', () => {
    H.orgRole = 'Member';
    H.selectable = SELECTABLE;
    renderSelect();
    expect(screen.queryByRole('combobox')).toBeNull();
    expect(screen.getByText('Default model')).toBeTruthy();
  });

  it('is read-only for a workspace admin on a CUSTOM agent, whose provider lives on its own row', () => {
    H.selectable = SELECTABLE;
    renderSelect({ isBuiltIn: false });
    expect(screen.queryByRole('combobox')).toBeNull();
  });

  describe('recommendation, relative cost and reset', () => {
    const row = (id: string, name: string, costIndex: number | null, platform = true) => ({
      id, name, capability: 'Chat', model: id, isPlatformManaged: platform, kind: 'OpenAICompatible', providerName: 'P', costIndex,
    });
    const REC = {
      agentType: 'Director',
      recommendedModelId: 'p-rec',
      recommendedModelName: 'Flash',
      why: 'Fast and cheap for planning',
      choiceModelId: null,
      effectiveModelId: 'p-rec',
      effectiveModelName: 'Flash',
      source: 'recommended',
    };
    function setup() {
      H.selectable = [
        row('p-rec', 'Flash', 2),
        row('p-cheap', 'Tiny', 0.4),
        row('p-pricey', 'Big', 5),
        row('p-same', 'Same', 2.5),
        row('p-byo', 'Mine', null, false),
      ];
      H.models = [REC];
    }

    it('shows the recommended model with its reason and labels the clear option', async () => {
      setup();
      renderSelect();
      expect(screen.getByText('Recommended: Flash')).toBeTruthy();
      expect(screen.getByText('Fast and cheap for planning')).toBeTruthy();
      expect(screen.getByDisplayValue('Recommended (Flash)')).toBeTruthy();
    });

    it('omits the reason when there is none, and falls back to "Default model" without a recommendation', () => {
      H.selectable = [row('p-a', 'A', 1)];
      H.models = [{ ...REC, recommendedModelId: null, recommendedModelName: null, why: null }];
      renderSelect();
      expect(screen.queryByText(/Recommended:/)).toBeNull();
      expect(screen.getByDisplayValue('Default model')).toBeTruthy();
    });

    it('shows relative cost: cheaper, pricier, similar (none) and own account', async () => {
      setup();
      renderSelect();
      fireEvent.click(screen.getByRole('combobox', { name: 'Model' }));
      expect(await screen.findByText('≈ 5× cheaper')).toBeTruthy();
      expect(screen.getByText('≈ 2.5× pricier')).toBeTruthy();
      expect(screen.getByText('your own account')).toBeTruthy();
      // 2.5 vs 2.0 is within 1.5x: no note on that option.
      const same = screen.getByText('Same · P').closest('[role="option"]');
      expect(same?.textContent).toBe('Same · P');
    });

    it('hides reset without a workspace choice', () => {
      setup();
      renderSelect();
      expect(screen.queryByRole('button', { name: 'Reset to recommended' })).toBeNull();
    });

    it('resets through the clear call and revalidates when a workspace choice is set', async () => {
      setup();
      H.overrides = [{ agentType: 'Director', inferenceProviderId: 'p-pricey', updatedAt: '2026-10-01T00:00:00Z' }];
      H.clearOrg.mockResolvedValue(undefined);
      renderSelect();
      fireEvent.click(screen.getByRole('button', { name: 'Reset to recommended' }));
      await waitFor(() => expect(H.clearOrg).toHaveBeenCalledWith('Director'));
      await waitFor(() => expect(H.mutateModels).toHaveBeenCalled());
      expect(H.mutateOverrides).toHaveBeenCalled();
    });

    it('does not fetch agent-models for a Member', () => {
      H.orgRole = 'Member';
      setup();
      renderSelect();
      expect(H.modelsCalls.length).toBeGreaterThan(0);
      expect(H.modelsCalls.every((e) => e === false)).toBe(true);
      expect(screen.queryByText(/Recommended:/)).toBeNull();
    });
  });
});
