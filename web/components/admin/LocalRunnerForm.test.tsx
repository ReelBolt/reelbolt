// @vitest-environment jsdom
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RunnerLlmResponse } from '@/lib/types/runner';

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

let llm: { data?: RunnerLlmResponse; isLoading: boolean } = { isLoading: false };
vi.mock('@/lib/hooks/use-runners', () => ({ useOrgRunnerLlm: () => llm }));

import { InferenceProviderForm } from './InferenceProviderForm';
import { CAPABILITIES_SERVED_BY_KIND, kindOptionsForScope, KIND_FIELD_HINTS, LOCAL_RUNNER_LABEL } from './provider-catalog';
import { KIND_BADGE } from './InferenceProviderTable';
import { selectableLabel } from '@/lib/types/inference-provider';
import { NO_SHARING_MESSAGE, deviceLabel } from './LocalRunnerFields';

const DEVICES: RunnerLlmResponse = {
  devices: [
    { deviceId: 'd1', name: 'Studio PC', online: true, enabled: true, serverKind: 'ollama', models: ['llama3.2', 'qwen2.5'] },
    { deviceId: 'd2', name: 'Laptop', online: false, enabled: true, serverKind: 'ollama', models: [] },
  ],
};

function renderForm(scope: 'org' | 'platform') {
  return render(
    <MantineProvider>
      <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} scope={scope} />
    </MantineProvider>,
  );
}

function chooseKind(label: string) {
  fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
  fireEvent.click(screen.getByText(label));
}

describe('LocalRunner provider kind', () => {
  beforeEach(() => {
    llm = { data: DEVICES, isLoading: false };
    global.matchMedia = ((query: string) => ({
      matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {},
      addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true,
    })) as unknown as typeof global.matchMedia;
  });
  afterEach(() => cleanup());

  it('swaps endpoint and API key for the device picker and notice', () => {
    renderForm('org');
    expect(screen.getByLabelText(/API Key/i)).toBeDefined();
    chooseKind(LOCAL_RUNNER_LABEL);
    expect(screen.queryByLabelText(/API Key/i)).toBeNull();
    expect(screen.getByRole('combobox', { name: /Model name/i })).toBeDefined();
    expect(screen.getByText(/No credits are used/)).toBeDefined();
    expect(screen.getByRole('combobox', { name: 'Computer' })).toBeDefined();
  });

  it('lists computers with online and sharing state and offers their models', () => {
    renderForm('org');
    chooseKind(LOCAL_RUNNER_LABEL);
    fireEvent.click(screen.getByRole('combobox', { name: 'Computer' }));
    expect(screen.getByText('Laptop (offline)')).toBeDefined();
    fireEvent.click(screen.getByText('Studio PC (online, sharing)'));
    fireEvent.focus(screen.getByRole('combobox', { name: /Model name/i }));
    expect(screen.getByText('qwen2.5')).toBeDefined();
  });

  it('shows the plain empty state with a Runners link when nothing is shared', () => {
    llm = { data: { devices: [] }, isLoading: false };
    renderForm('org');
    chooseKind(LOCAL_RUNNER_LABEL);
    expect(screen.getByText(NO_SHARING_MESSAGE)).toBeDefined();
    expect(screen.getByRole('link', { name: 'Runners' }).getAttribute('href')).toBe('/runners');
  });

  it('is not offered by the platform admin form', () => {
    renderForm('platform');
    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    expect(screen.queryByText(LOCAL_RUNNER_LABEL)).toBeNull();
    expect(kindOptionsForScope('platform').some((o) => o.value === 'LocalRunner')).toBe(false);
    expect(kindOptionsForScope('org').some((o) => o.value === 'LocalRunner')).toBe(true);
  });

  it('has an entry in every exhaustive kind table, Chat, Vision and Embedding', () => {
    expect(CAPABILITIES_SERVED_BY_KIND.LocalRunner).toEqual(['Chat', 'Vision', 'Embedding']);
    expect(KIND_FIELD_HINTS.LocalRunner).toBeDefined();
    expect(KIND_BADGE.LocalRunner.label).toBeTruthy();
  });

  it('tags a local model in pickers and labels devices', () => {
    expect(selectableLabel({ name: 'Llama', providerName: 'Mine', kind: 'LocalRunner' })).toBe('Llama · Mine (on my computer)');
    expect(selectableLabel({ name: 'GPT', providerName: 'Azure', kind: 'AzureOpenAI' })).toBe('GPT · Azure');
    expect(deviceLabel(DEVICES.devices[0])).toBe('Studio PC (online, sharing)');
  });
});
