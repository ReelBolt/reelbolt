// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import {
  VideoEnhanceStepConfig,
  createDefaultVideoEnhanceStepConfig,
} from './VideoEnhanceStepConfig';
import type { VideoEnhanceStepConfig as Cfg } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

const { FILES, seen, consentApi } = vi.hoisted(() => ({
  consentApi: { put: vi.fn() },
  FILES: { current: [] as { id: string; mimeType: string; originalFileName: string }[] },
  seen: { capabilities: [] as (string | null | undefined)[] },
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: FILES.current, error: null, isLoading: false }) }));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/editing/enhance/enhance-api', () => ({ putEnhanceConsent: (...a: unknown[]) => consentApi.put(...a) }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true, user: { orgRole: 'Owner' } }),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: (capability?: string | null) => {
    seen.capabilities.push(capability);
    return {
      data: [{ id: 'up-1', name: 'Upscaler', capability: 'VideoUpscale', model: 'up-model', isPlatformManaged: true, kind: 'FalAi', providerName: 'fal.ai' }],
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
afterEach(() => { cleanup(); consentApi.put.mockReset(); seen.capabilities = []; FILES.current = []; });


const prior = (label: string): StepData => ({ label, stepType: 'VideoGenerate' } as StepData);

function renderConfig(overrides: Partial<Cfg> = {}, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <VideoEnhanceStepConfig
        config={{ ...createDefaultVideoEnhanceStepConfig(), ...overrides }}
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

describe('VideoEnhanceStepConfig', () => {
  it('defaults to the free local 2x upscale, no consent, saved to Files', () => {
    expect(createDefaultVideoEnhanceStepConfig()).toMatchObject({
      op: 'Upscale', engine: 'Local', upscaleFactor: 2, allowSourceMediaEgress: false, registerProjectFile: true, providerId: null,
    });
  });

  it('offers the four operations', () => {
    renderConfig();
    fireEvent.click(screen.getByRole('combobox', { name: /^Improvement/ }));
    expect(screen.getAllByRole('option')).toHaveLength(5);
  });

  it('hides the consent switch and the provider for a local step', () => {
    renderConfig({ op: 'Upscale', engine: 'Local' });
    expect(screen.queryByRole('switch', { name: /Allow sending this video/ })).toBeNull();
    expect(screen.queryByRole('combobox', { name: /Provider/ })).toBeNull();
    expect(seen.capabilities).not.toContain('VideoUpscale');
    expect(screen.queryByTestId('egress-run-note')).toBeNull();
  });

  it('shows the consent switch and the upscale provider for a hosted upscale', () => {
    renderConfig({ op: 'Upscale', engine: 'Hosted' });
    expect(seen.capabilities).toContain('VideoUpscale');
    expect(screen.getByRole('switch', { name: /Allow sending this video/ })).toBeTruthy();
    // docs/design/app-inputs.md "Permission to send media as an app field" (8): the switch stays, one line under it.
    expect(screen.getByTestId('egress-run-note').textContent).toBe('You can also allow this each time you run, on the app page.');
  });

  it('ticking the box asks first, records the project consent, and only then turns the step flag on', async () => {
    consentApi.put.mockResolvedValue({});
    const onChange = renderConfig({ op: 'Upscale', engine: 'Hosted' });
    fireEvent.click(screen.getByRole('switch', { name: /Allow sending this video/ }));
    expect(consentApi.put).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByTestId('enhance-consent-check'));
    fireEvent.click(screen.getByTestId('enhance-consent-confirm'));
    await waitFor(() => expect(consentApi.put).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(last(onChange)).toMatchObject({ engine: 'Hosted', allowSourceMediaEgress: true }));
  });

  it('declining the dialog records nothing and leaves the flag off; a failed save leaves it off too', async () => {
    const onChange = renderConfig({ op: 'Upscale', engine: 'Hosted' });
    fireEvent.click(screen.getByRole('switch', { name: /Allow sending this video/ }));
    fireEvent.click(await screen.findByTestId('enhance-consent-cancel'));
    expect(consentApi.put).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();

    consentApi.put.mockRejectedValue(new Error('no'));
    fireEvent.click(screen.getByRole('switch', { name: /Allow sending this video/ }));
    fireEvent.click(await screen.findByTestId('enhance-consent-check'));
    fireEvent.click(screen.getByTestId('enhance-consent-confirm'));
    expect(await screen.findByTestId('enhance-consent-error')).toBeTruthy();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('unticking turns the step flag off without touching the project consent', () => {
    const onChange = renderConfig({ op: 'Upscale', engine: 'Hosted', allowSourceMediaEgress: true });
    fireEvent.click(screen.getByRole('switch', { name: /Allow sending this video/ }));
    expect(last(onChange)).toMatchObject({ allowSourceMediaEgress: false });
    expect(consentApi.put).not.toHaveBeenCalled();
  });

  it('switching a hosted op between Upscale and Interpolate resets the provider of the other capability', () => {
    const onChange = renderConfig({ op: 'Upscale', engine: 'Hosted', providerId: 'up-1' });
    fireEvent.click(screen.getByRole('combobox', { name: /^Improvement/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Make motion smoother' }));
    expect(last(onChange)).toMatchObject({ op: 'Interpolate', providerId: null });
  });

  it('uses the frame interpolation provider for a hosted interpolation', () => {
    renderConfig({ op: 'Interpolate', engine: 'Hosted', frameMultiplier: 2 });
    expect(seen.capabilities).toContain('FrameInterpolation');
  });

  it('switching a hosted op to a local-only one clears the provider, the consent and the op options', () => {
    const onChange = renderConfig({ op: 'Upscale', engine: 'Hosted', providerId: 'up-1', allowSourceMediaEgress: true });
    fireEvent.click(screen.getByRole('combobox', { name: /^Improvement/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Clean up grain and noise' }));
    expect(last(onChange)).toMatchObject({
      op: 'Denoise', engine: 'Local', providerId: null, allowSourceMediaEgress: false,
      upscaleFactor: null, targetHeight: null, frameMultiplier: null, denoiseStrength: 'Light',
    });
  });

  it('switching to interpolation drops the upscale size and sets a frame multiplier', () => {
    const onChange = renderConfig({ op: 'Upscale', upscaleFactor: 4 });
    fireEvent.click(screen.getByRole('combobox', { name: /^Improvement/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Make motion smoother' }));
    expect(last(onChange)).toMatchObject({ op: 'Interpolate', upscaleFactor: null, targetHeight: null, frameMultiplier: 2 });
  });

  it('gates the frame multiplier: 8 is local only', () => {
    renderConfig({ op: 'Interpolate', engine: 'Local', frameMultiplier: 2 });
    fireEvent.click(screen.getByRole('combobox', { name: /^Frames to add/ }));
    expect(screen.getByRole('option', { name: 'Eight times as many frames' })).toBeTruthy();
    cleanup();
    renderConfig({ op: 'Interpolate', engine: 'Hosted', frameMultiplier: 2 });
    fireEvent.click(screen.getByRole('combobox', { name: /^Frames to add/ }));
    expect(screen.queryByRole('option', { name: 'Eight times as many frames' })).toBeNull();
  });

  it('picks a video file but not an image', async () => {
    FILES.current = [
      { id: 'v1', mimeType: 'video/mp4', originalFileName: 'a.mp4' },
      { id: 'i1', mimeType: 'image/png', originalFileName: 'c.png' },
    ];
    const onChange = renderConfig({ source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null } });
    fireEvent.click(screen.getByRole('button', { name: 'Choose' }));
    await screen.findByRole('radio', { name: 'a.mp4' });
    expect(screen.queryByRole('radio', { name: 'c.png' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'a.mp4' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(last(onChange).source).toEqual({ kind: 'ProjectFile', projectFileId: 'v1', stepOrder: null, stepResultId: null });
  });

  it('writes the save-to-Files switch', () => {
    const onChange = renderConfig();
    fireEvent.click(screen.getByRole('switch', { name: /Save to Files/ }));
    expect(last(onChange).registerProjectFile).toBe(false);
  });

  it('an Extend step records the project consent before its flag turns on, like the other hosted ops', async () => {
    consentApi.put.mockResolvedValue({});
    const onChange = renderConfig({ op: 'Extend', engine: 'Hosted', targetAspect: '9:16' });
    fireEvent.click(screen.getByRole('switch', { name: /Allow sending this video/ }));
    expect(consentApi.put).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByTestId('enhance-consent-check'));
    fireEvent.click(screen.getByTestId('enhance-consent-confirm'));
    await waitFor(() => expect(consentApi.put).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(last(onChange)).toMatchObject({ op: 'Extend', engine: 'Hosted', allowSourceMediaEgress: true }));
  });

  it('offers Extend with a shape and a description', () => {
    renderConfig({ op: 'Extend', engine: 'Hosted', targetAspect: '9:16' });
    expect(screen.getByRole('combobox', { name: /shape/i })).toBeTruthy();
  });
});
