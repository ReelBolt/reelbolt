// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InferenceProviderTable } from './InferenceProviderTable';
import type {
  InferenceModel,
  InferenceProvider,
  InferenceProviderCapability,
} from '@/lib/types/inference-provider';

// Polyfill ResizeObserver for jsdom
vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    constructor(_callback: ResizeObserverCallback) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  }
);

// Polyfill scrollIntoView for jsdom
Element.prototype.scrollIntoView = vi.fn();

/** One model serving `capabilities`, the first of them as the default when `isDefault`. */
function model(id: string, capabilities: InferenceProviderCapability[], isDefault = false): InferenceModel {
  return {
    id,
    modelCode: `${id}-code`,
    displayName: id,
    isEnabled: true,
    capabilities: capabilities.map((capability, i) => ({
      id: `${id}-${capability}`,
      capability,
      isDefault: isDefault && i === 0,
      isEnabled: true,
      priceSource: 'Table',
      lastTestAt: null,
      lastTestOk: null,
      lastTestError: null,
      prices: null,
    })),
  };
}

function provider(over: Partial<InferenceProvider> = {}): InferenceProvider {
  return {
    id: '123e4567-e89b-12d3-a456-426614174002',
    organizationId: null,
    name: 'Acme vLLM',
    kind: 'OpenAICompatible',
    endpoint: 'https://vllm.acme.example/v1',
    hasApiKey: true,
    apiKeyLastFour: 'abcd',
    isEnabled: true,
    isSystem: false,
    timeoutSeconds: 30,
    personalOAuthMode: false,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    models: [model('llama-3-8b', ['Chat'])],
    ...over,
  };
}

const renderTable = (providers: InferenceProvider[], props: Partial<React.ComponentProps<typeof InferenceProviderTable>> = {}) =>
  render(
    <MantineProvider>
      <InferenceProviderTable providers={providers} onDelete={vi.fn()} {...props} />
    </MantineProvider>
  );

describe('InferenceProviderTable', () => {
  beforeEach(() => {
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('shows one row per provider account: endpoint host, key last four, status and models count', () => {
    renderTable([
      provider({
        models: [model('GPT-4o', ['Chat', 'Vision'], true), model('Whisper', ['Transcription'])],
      }),
    ]);
    expect(screen.getByText('Acme vLLM')).toBeDefined();
    expect(screen.getByText('vllm.acme.example')).toBeDefined();
    expect(screen.getByText('•••• abcd')).toBeDefined();
    expect(screen.getByText('Enabled')).toBeDefined();
    expect(screen.getByText('2')).toBeDefined();
    // One chip per distinct capability served under the provider.
    for (const capability of ['Chat', 'Vision', 'Transcription']) {
      expect(screen.getByText(capability)).toBeDefined();
    }
  });

  it('Renders a FishAudio/SpeechSynthesis provider with both badges showing their real labels', () => {
    renderTable([provider({ name: 'Fish Audio Provider', kind: 'FishAudio', models: [model('s2', ['SpeechSynthesis'])] })]);
    // The kind badge shows "Fish Audio", not the raw "FishAudio" enum value.
    expect(screen.getByText('Fish Audio')).toBeDefined();
    expect(screen.getByText('SpeechSynthesis')).toBeDefined();
  });

  it('Renders an OpenAI-compatible Embedding provider with its capability badge', () => {
    renderTable([provider({ name: 'Local Qwen Embedding', models: [model('qwen', ['Embedding'])] })]);
    expect(screen.getByText('OpenAI-compatible')).toBeDefined();
    expect(screen.getByText('Embedding')).toBeDefined();
  });

  it('Renders a BlackForestLabs image provider with its real kind label and capabilities', () => {
    renderTable([provider({ name: 'FLUX', kind: 'BlackForestLabs', models: [model('flux', ['ImageGeneration', 'ImageEdit'])] })]);
    expect(screen.getByText('Black Forest Labs')).toBeDefined();
    expect(screen.getByText('ImageGeneration')).toBeDefined();
    expect(screen.getByText('ImageEdit')).toBeDefined();
  });

  // An exhaustive Record that was never widened degrades to the grey default, which reads as "no
  // colour assigned" rather than as "someone forgot".
  it('Renders a FalAi provider with the vendor label and its segmentation and depth capabilities', () => {
    renderTable([
      provider({
        name: 'fal.ai tools',
        kind: 'FalAi',
        endpoint: 'https://queue.fal.run',
        models: [model('evf-sam', ['Segmentation'], true), model('depth', ['Depth'])],
      }),
    ]);
    expect(screen.getByText('fal.ai')).toBeDefined();
    expect(screen.queryByText('FalAi')).toBeNull();
    expect(screen.getByText('Segmentation')).toBeDefined();
    expect(screen.getByText('Depth')).toBeDefined();
    expect(screen.getByText('queue.fal.run')).toBeDefined();
  });

  it('Renders an ElevenLabs provider with its vendor label and both audio capabilities, not the raw enum value', () => {
    renderTable([
      provider({
        name: 'ElevenLabs direct',
        kind: 'ElevenLabs',
        endpoint: 'https://api.elevenlabs.io',
        models: [model('music_v2_5', ['MusicGeneration'], true), model('eleven_text_to_sound_v2', ['SoundEffectGeneration'])],
      }),
    ]);
    expect(screen.getByText('ElevenLabs')).toBeDefined();
    expect(screen.getByText('MusicGeneration')).toBeDefined();
    expect(screen.getByText('SoundEffectGeneration')).toBeDefined();
    expect(screen.getByText('api.elevenlabs.io')).toBeDefined();
  });

  it('says a provider with no key has none', () => {
    renderTable([provider({ hasApiKey: false, apiKeyLastFour: null })]);
    expect(screen.getByText('No key')).toBeDefined();
  });

  it('shows the system provider with a System badge and no edit or delete', () => {
    renderTable([
      provider({
        id: 'sys',
        name: 'ReelBolt compute',
        isSystem: true,
        models: [model('render', ['Compute'])],
      }),
    ]);
    expect(screen.getByText('System')).toBeDefined();
    expect(screen.getByText('Compute')).toBeDefined();
    expect(screen.queryByRole('link', { name: 'Edit ReelBolt compute' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete ReelBolt compute' })).toBeNull();
    // Still openable, to see and price its capabilities.
    expect(screen.getByRole('link', { name: 'ReelBolt compute' }).getAttribute('href')).toBe('/admin/inference-providers/sys');
  });

  // The same table serves the platform list and the workspace's, so a row must open the page that
  // can actually edit it.
  it('links a platform row to the admin detail page by default', () => {
    renderTable([provider()]);
    expect(
      screen.getByRole('link', { name: 'Edit Acme vLLM' }).getAttribute('href')
    ).toBe('/admin/inference-providers/123e4567-e89b-12d3-a456-426614174002');
  });

  it('links a workspace row to the workspace detail page under scope="org"', () => {
    renderTable([provider()], { scope: 'org' });
    expect(
      screen.getByRole('link', { name: 'Edit Acme vLLM' }).getAttribute('href')
    ).toBe('/org/inference-providers/123e4567-e89b-12d3-a456-426614174002');
  });

  it('hides the row actions when read-only', () => {
    renderTable([provider()], { scope: 'org', readOnly: true });
    expect(screen.queryByRole('link', { name: 'Edit Acme vLLM' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete Acme vLLM' })).toBeNull();
  });

  // Below `sm` the rows are stacked cards, so every value carries a label of its own (hidden by CSS
  // once the real header row shows).
  it('labels every cell for the stacked mobile layout', () => {
    renderTable([provider()]);
    for (const label of ['Name', 'Kind', 'Endpoint', 'Key', 'Status', 'Models', 'Capabilities']) {
      expect(screen.getAllByText(label).length).toBeGreaterThanOrEqual(2);
    }
  });
});
