// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import {
  AudioCleanupStepConfig,
  createDefaultAudioCleanupStepConfig,
} from './AudioCleanupStepConfig';
import type { AudioCleanupStepConfig as Cfg } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

const { FILES, seen } = vi.hoisted(() => ({
  FILES: { current: [] as { id: string; mimeType: string; originalFileName: string }[] },
  seen: { capabilities: [] as (string | null | undefined)[] },
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: FILES.current, error: null, isLoading: false }) }));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true, user: { orgRole: 'Owner' } }),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: (capability?: string | null) => {
    seen.capabilities.push(capability);
    return {
      data: [{ id: 'iso-1', name: 'Isolator', capability: 'AudioIsolation', model: 'iso-model', isPlatformManaged: true, kind: 'FalAi', providerName: 'fal.ai' }],
      isLoading: false,
      error: undefined,
    };
  },
}));

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
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => { cleanup(); seen.capabilities = []; FILES.current = []; });

const prior = (label: string): StepData => ({ label, stepType: 'VideoGenerate' } as StepData);

function renderConfig(overrides: Partial<Cfg> = {}, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <AudioCleanupStepConfig
        config={{ ...createDefaultAudioCleanupStepConfig(), ...overrides }}
        onChange={onChange}
        allSteps={[prior('Make the clip')]}
        currentStepIndex={1}
        projectId="p1"
      />
    </MantineProvider>,
  );
  return onChange;
}

const last = (m: Mock) => m.mock.calls.at(-1)![0] as Cfg;

describe('AudioCleanupStepConfig', () => {
  it('defaults to the free voice fix, no consent, saved to Files', () => {
    expect(createDefaultAudioCleanupStepConfig()).toMatchObject({
      target: 'VoiceBasic', allowSourceMediaEgress: false, registerProjectFile: true, outputKind: 'Auto', providerId: null,
    });
  });

  it('offers exactly the seven fixes, each with a free or credits hint', () => {
    renderConfig();
    fireEvent.click(screen.getByRole('combobox', { name: /^Fix/ }));
    expect(screen.getAllByRole('option')).toHaveLength(7);
    expect(screen.getByRole('option', { name: 'Remove hum (free)' })).toBeTruthy();
    expect(screen.getByRole('option', { name: 'Keep only the voice (uses credits)' })).toBeTruthy();
    expect(screen.getByRole('option', { name: 'Remove background music (uses credits)' })).toBeTruthy();
  });

  it('hides the consent switch and the provider for a local fix', () => {
    renderConfig({ target: 'Hum' });
    expect(screen.queryByRole('switch', { name: /Allow sending this audio/ })).toBeNull();
    expect(screen.queryByRole('combobox', { name: /Provider/ })).toBeNull();
    expect(seen.capabilities).not.toContain('AudioIsolation');
  });

  it('shows the consent switch and the audio cleanup provider for an AI fix, and writes the consent', () => {
    const onChange = renderConfig({ target: 'IsolateVoice' });
    expect(seen.capabilities).toContain('AudioIsolation');
    fireEvent.click(screen.getByRole('switch', { name: /Allow sending this audio/ }));
    expect(last(onChange)).toMatchObject({ target: 'IsolateVoice', allowSourceMediaEgress: true });
  });

  it('switching an AI fix to a local one clears the provider, the consent and the length cap', () => {
    const onChange = renderConfig({ target: 'RemoveMusic', providerId: 'iso-1', allowSourceMediaEgress: true, maxSourceMinutes: 10 });
    fireEvent.click(screen.getByRole('combobox', { name: /^Fix/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Reduce wind noise (free)' }));
    expect(last(onChange)).toMatchObject({ target: 'Wind', providerId: null, allowSourceMediaEgress: false, maxSourceMinutes: null });
  });

  it('picks a video or audio file but not an image', async () => {
    FILES.current = [
      { id: 'v1', mimeType: 'video/mp4', originalFileName: 'a.mp4' },
      { id: 'a1', mimeType: 'audio/wav', originalFileName: 'b.wav' },
      { id: 'i1', mimeType: 'image/png', originalFileName: 'c.png' },
    ];
    const onChange = renderConfig({ source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null } });
    fireEvent.click(screen.getByRole('button', { name: 'Choose' }));
    await screen.findByRole('radio', { name: 'b.wav' });
    expect(screen.queryByRole('radio', { name: 'c.png' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'b.wav' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(last(onChange).source).toEqual({ kind: 'ProjectFile', projectFileId: 'a1', stepOrder: null, stepResultId: null });
  });

  it('writes the result type and the save-to-Files switch', () => {
    const onChange = renderConfig();
    fireEvent.click(screen.getByRole('combobox', { name: /Result type/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Video with the clean audio' }));
    expect(last(onChange).outputKind).toBe('VideoWithCleanAudio');
    fireEvent.click(screen.getByRole('switch', { name: /Save to Files/ }));
    expect(last(onChange).registerProjectFile).toBe(false);
  });
});
