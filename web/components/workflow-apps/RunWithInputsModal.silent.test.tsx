// @vitest-environment jsdom

/**
 * The run dialog says so before a run starts when it will produce a silent video, and warns when a
 * narrated run has no usable speech provider. Both are advisory: the run button stays enabled.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { InferenceProviderStatus } from '@/lib/types/inference-provider';
import type { StepType } from '@/lib/types/workflow';

vi.mock('@/lib/api/inference-providers', () => ({
  getProviderStatus: vi.fn(),
}));
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-billing')>();
  // No plan cap and no cost estimate: this file is about the notices, not billing.
  return {
    ...actual,
    useOutputHeightCap: () => null,
    useWorkflowCostEstimate: () => ({ data: undefined, error: undefined, isLoading: false, mutate: vi.fn() }),
    useWorkflowCostEstimateWithBrief: () => ({ data: undefined, error: undefined, isLoading: false, mutate: vi.fn() }),
  };
});
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({ data: [] }),
  useEditingStyles: () => ({ data: [] }),
}));

import { getProviderStatus } from '@/lib/api/inference-providers';
import { RunWithInputsModal } from './RunWithInputsModal';
import { SILENT_RUN_TITLE } from '@/components/workflows/SilentRunNotice';

const statusMock = vi.mocked(getProviderStatus);

const healthy: InferenceProviderStatus = {
  capability: 'SpeechSynthesis', providerName: 'local-fish-tts', lastTestOk: true, lastTestAt: null,
  healthy: true, usingFallback: false, message: null,
};
const unhealthy: InferenceProviderStatus = {
  capability: 'SpeechSynthesis', providerName: 'local-fish-tts', lastTestOk: false, lastTestAt: null,
  healthy: false, usingFallback: false, message: null,
};

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
  // The brief is an autosizing Textarea, which listens for font loading; jsdom has no FontFaceSet.
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});

beforeEach(() => {
  statusMock.mockReset();
  statusMock.mockResolvedValue(healthy);
});

afterEach(() => cleanup());

/** Lets the pending provider-status request resolve and render, so absence assertions mean something. */
const settle = () => act(() => new Promise<void>((resolve) => setTimeout(resolve, 0)));

type Step = { stepType?: StepType; agentType?: string | null };

/** Renders the dialog open for a workflow made of these steps, with a fresh SWR cache per test. */
function renderRun(steps: Step[]) {
  return render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={vi.fn()} steps={steps} workflowName="Promo" />
      </MantineProvider>
    </SWRConfig>,
  );
}

const authorStep: Step = { stepType: 'Agent', agentType: 'AuthorAgent' };
const scriptStep: Step = { stepType: 'Agent', agentType: 'ScriptwriterAgent' };
const voiceStep: Step = { stepType: 'Voiceover' };

describe('RunWithInputsModal: silent video and speech notices', () => {
  it('tells the user a video run with no Voiceover step will be silent, and keeps the run button enabled', async () => {
    renderRun([scriptStep, authorStep]);
    expect(await screen.findByText(SILENT_RUN_TITLE)).toBeTruthy();
    expect(screen.getByText('This workflow has no Voiceover step, so the film is silent. Add a Voiceover step to narrate it.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Start run' }) as HTMLButtonElement).disabled).toBe(false);
    // No Voiceover step, so there is no speech provider to ask about.
    expect(statusMock).not.toHaveBeenCalled();
  });

  it('shows no silent-video note for a footage edit, which keeps its source audio', async () => {
    renderRun([{ stepType: 'VideoAnalyze' }, { stepType: 'VideoCompile' }]);
    await settle();
    expect(screen.queryByText(SILENT_RUN_TITLE)).toBeNull();
  });

  it('shows no silent-video note for a workflow that produces no video', async () => {
    renderRun([scriptStep]);
    await settle();
    expect(screen.queryByText(SILENT_RUN_TITLE)).toBeNull();
    expect(statusMock).not.toHaveBeenCalled();
  });

  it('shows no silent-video note when the workflow has a Voiceover step', async () => {
    renderRun([authorStep, voiceStep]);
    await settle();
    expect(statusMock).toHaveBeenCalledWith('SpeechSynthesis');
    expect(screen.queryByText(SILENT_RUN_TITLE)).toBeNull();
  });

  it('warns, in warning colour, when a Voiceover step has no usable speech provider', async () => {
    statusMock.mockResolvedValue(unhealthy);
    renderRun([authorStep, voiceStep]);
    expect(await screen.findByText('Narration will probably fail')).toBeTruthy();
    expect(screen.queryByText(SILENT_RUN_TITLE)).toBeNull();
    expect((screen.getByRole('button', { name: 'Start run' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('warns about a Voiceover step with no speech provider even when the workflow is not a video run', async () => {
    statusMock.mockResolvedValue(unhealthy);
    renderRun([scriptStep, voiceStep]);
    expect(await screen.findByText('Narration will probably fail')).toBeTruthy();
  });

  it('shows no alert when a Voiceover step has a healthy speech provider', async () => {
    renderRun([authorStep, voiceStep]);
    await settle();
    expect(statusMock).toHaveBeenCalledWith('SpeechSynthesis');
    expect(screen.queryByText('Narration will probably fail')).toBeNull();
    expect(screen.queryByText(SILENT_RUN_TITLE)).toBeNull();
  });
});

describe('RunWithInputsModal: the permission to send media', () => {
  const manifest = {
    version: 2,
    inputs: [
      { kind: 'notes' as const, label: 'Anything else?' },
      { kind: 'input' as const, id: 'allowMediaEgress', type: 'toggle' as const, label: 'Allow sending your media to AI services', required: true, more: false, permission: 'mediaEgress', help: 'Needed by: Find the plate (finds the object)' },
    ],
  };

  it('keeps the notes box of a page whose only field is the permission, and blocks Start until it is given', async () => {
    const onConfirm = vi.fn();
    render(
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>
          <RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} steps={[{ stepType: 'VideoAnalyze' }]} manifest={manifest} workflowName="Blur" />
        </MantineProvider>
      </SWRConfig>,
    );
    await settle();
    expect(screen.queryByTestId('run-modal-inputs')).toBeNull();
    const start = screen.getByRole('button', { name: 'Start run' }) as HTMLButtonElement;
    expect(start.disabled).toBe(true);
    expect(screen.getByTestId('app-permission-block').textContent).toContain('Needed by: Find the plate (finds the object)');
    expect(screen.getByTestId('app-permission-error').textContent).toBe('Allow sending your media to AI services to run this app.');

    fireEvent.click(screen.getByTestId('app-permission-switch'));
    expect(start.disabled).toBe(false);
    expect(screen.queryByTestId('app-permission-error')).toBeNull();
    fireEvent.click(start);
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onConfirm.mock.calls[0]![0]).toMatchObject({ inputs: { allowMediaEgress: true } });
  });
});
