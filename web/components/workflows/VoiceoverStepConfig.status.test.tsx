// @vitest-environment jsdom
/**
 * Customer e2e finding 31: the default speech provider had failed its connection test and nothing
 * in the Voiceover step said so. The step now shows the provider status (contract C3) and links to
 * the project's Voices tab so a voice can actually be chosen.
 */
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { InferenceProviderStatus } from '@/lib/types/inference-provider';

const { STATUS } = vi.hoisted(() => ({ STATUS: { current: null as InferenceProviderStatus | null } }));
vi.mock('@/lib/api/inference-providers', () => ({
  getProviderStatus: vi.fn(async () => STATUS.current),
}));
vi.mock('@/lib/hooks/use-voices', () => ({ useProjectVoices: () => ({ data: [] }) }));
// A17-UI: the step reads the member-facing selectable list, not the platform admin's, so the mock
// must supply that hook or the component silently falls back to "nothing configured".
vi.mock('@/lib/hooks/use-inference-providers', () => ({ useSelectableProviders: () => ({ data: [], isLoading: false }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ isAdmin: false, user: { orgRole: 'Member' } }) }));

import { VoiceoverStepConfig, createDefaultVoiceoverStepConfig } from './VoiceoverStepConfig';
import { describeVoiceProviderStatus } from './VoiceProviderStatusNotice';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});
afterEach(() => cleanup());

const base: InferenceProviderStatus = {
  capability: 'SpeechSynthesis', providerName: 'local-fish-tts', lastTestOk: true, lastTestAt: null,
  healthy: true, usingFallback: false, message: null,
};

function renderStep() {
  return render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <VoiceoverStepConfig config={createDefaultVoiceoverStepConfig()} onChange={vi.fn()} projectId="p1" />
      </MantineProvider>
    </SWRConfig>,
  );
}

describe('describeVoiceProviderStatus', () => {
  it('says nothing for a healthy provider', () => {
    expect(describeVoiceProviderStatus(base)).toBeNull();
    expect(describeVoiceProviderStatus(undefined)).toBeNull();
  });

  it('warns that narration will probably fail when the provider is unhealthy', () => {
    const n = describeVoiceProviderStatus({ ...base, healthy: false, lastTestOk: false });
    expect(n?.level).toBe('error');
    expect(n?.title).toBe('Narration will probably fail');
    expect(n?.body).toContain('local-fish-tts');
  });

  it('explains a fallback provider', () => {
    const n = describeVoiceProviderStatus({ ...base, providerName: 'qa-local-fish', usingFallback: true });
    expect(n?.level).toBe('warning');
    expect(n?.body).toContain('qa-local-fish');
  });

  it("prefers the server's own plain-language message", () => {
    const n = describeVoiceProviderStatus({ ...base, healthy: false, message: 'The voice service is down.' });
    expect(n?.body).toBe('The voice service is down.');
  });

  it('handles no provider at all', () => {
    expect(describeVoiceProviderStatus({ ...base, providerName: null, healthy: false })?.title).toBe('No voice service is set up');
  });
});

describe('VoiceoverStepConfig provider status and voice choice', () => {
  it('shows a clear warning when the speech provider is unhealthy', async () => {
    STATUS.current = { ...base, healthy: false, lastTestOk: false };
    renderStep();
    expect(await screen.findByText('Narration will probably fail')).toBeTruthy();
  });

  it('shows a notice when a fallback provider will be used', async () => {
    STATUS.current = { ...base, providerName: 'qa-local-fish', usingFallback: true };
    renderStep();
    expect(await screen.findByText('Using a backup voice service')).toBeTruthy();
  });

  it('shows nothing when the provider is healthy', async () => {
    STATUS.current = base;
    renderStep();
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByText('Narration will probably fail')).toBeNull();
    expect(screen.queryByText('Using a backup voice service')).toBeNull();
  });

  it("links to the project's Voices tab", () => {
    STATUS.current = base;
    renderStep();
    const link = screen.getByRole('link', { name: /Voices tab/ });
    expect(link.getAttribute('href')).toBe('/projects/p1?tab=voices');
  });
});
