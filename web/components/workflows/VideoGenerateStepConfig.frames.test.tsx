// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup, within } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { VideoGenerateStepConfig, createDefaultVideoGenerateStepConfig } from './VideoGenerateStepConfig';
import type { VideoGenerateStepConfig as Cfg } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true, user: { orgRole: 'Owner' } }),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: () => ({ data: [], isLoading: false, error: undefined }),
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
afterEach(() => cleanup());

const steps = [
  { label: 'Grab the frame', stepType: 'ExtractFrame' },
  { label: 'Other', stepType: 'Agent' },
  { label: 'Generate', stepType: 'VideoGenerate' },
] as StepData[];

/**
 * Clicks one option of an open Mantine Select. The Advanced section is a Collapse, inside which jsdom leaves the
 * select's dropdown unrevealed (display: none) even though it is open, so the option is found in the dropdown the
 * combobox itself points at, regardless of visibility.
 */
function pick(combobox: HTMLElement, optionName: string) {
  fireEvent.click(combobox);
  const dropdown = document.getElementById(combobox.getAttribute('aria-controls') ?? '') as HTMLElement;
  fireEvent.click(within(dropdown).getByRole('option', { name: optionName, hidden: true }));
}

function renderConfig(overrides: Partial<Cfg> = {}, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <VideoGenerateStepConfig config={{ ...createDefaultVideoGenerateStepConfig(), ...overrides }} onChange={onChange} allSteps={steps} currentStepIndex={2} />
    </MantineProvider>,
  );
  return onChange;
}
const last = (m: Mock) => m.mock.calls.at(-1)![0] as Cfg;

describe('VideoGenerateStepConfig — first and last frame from a step', () => {
  it('leaves both unset by default and does not add them to the default config', () => {
    expect(createDefaultVideoGenerateStepConfig()).not.toHaveProperty('firstFrame');
    expect(createDefaultVideoGenerateStepConfig()).not.toHaveProperty('lastFrame');
  });

  it('opens Advanced by itself when a frame is set, and picks an earlier step as the first frame', async () => {
    const onChange = renderConfig({ firstFrame: { from: 'Previous' } });
    const first = await screen.findByRole('combobox', { name: /First frame from step/ });
    expect((first as HTMLInputElement).value).toBe('The previous step');
    pick(first, '1. Grab the frame');
    expect(last(onChange).firstFrame).toEqual({ from: 'Step', stepOrder: 1 });
  });

  it('needs a first frame before a last frame, and clearing the first frame clears the last', async () => {
    renderConfig();
    fireEvent.click(screen.getByRole('button', { name: 'Advanced' }));
    const lastBox = await screen.findByRole('combobox', { name: /Last frame from step/ });
    expect((lastBox as HTMLInputElement).disabled).toBe(true);
    cleanup();

    const onChange = renderConfig({ firstFrame: { from: 'Step', stepOrder: 1 }, lastFrame: { from: 'Previous' } });
    pick(await screen.findByRole('combobox', { name: /First frame from step/ }), 'None');
    expect(last(onChange)).toMatchObject({ firstFrame: null, lastFrame: null });
  });

  it('warns that the picture is sent out when egress is not allowed', async () => {
    renderConfig({ firstFrame: { from: 'Previous' }, allowSourceMediaEgress: false });
    expect(await screen.findByText(/switch on .Allow sending your media/)).toBeTruthy();
    cleanup();
    renderConfig({ firstFrame: { from: 'Previous' }, allowSourceMediaEgress: true });
    await screen.findByRole('combobox', { name: /First frame from step/ });
    expect(screen.queryByText(/switch on .Allow sending your media/)).toBeNull();
  });

  it('is not offered when the prompt comes from a planner step', () => {
    renderConfig({ planSource: 'StepRef' });
    fireEvent.click(screen.getByRole('button', { name: 'Advanced' }));
    expect(screen.queryByRole('textbox', { name: /First frame from step/ })).toBeNull();
  });
});
