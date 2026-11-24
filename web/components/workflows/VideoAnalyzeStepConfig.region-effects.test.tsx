// @vitest-environment jsdom
/**
 * prompted-region-effects T-006 — the VideoAnalyze form.
 *
 * The object editor replaces the old label box with one row per object plus a method select, and the
 * segmentation knobs are step-level, so they appear as soon as ANY object is followed with
 * segmentation. What these tests protect:
 *  - an object that was never touched keeps its pre-feature shape ({ label } and nothing else);
 *  - a hand-marked quad is carried through an edit of a labelled target, never rewritten;
 *  - a segmentation target with no consent says so in words instead of failing silently at run time.
 */
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

// The provider picker inside the segmentation panel reads the member-facing selectable list.
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: () => ({ data: [], isLoading: false }),
}));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isAdmin: false, isPlatformAdmin: false, isOrgAdmin: false, user: { orgRole: 'Member' } }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));

import { VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import type { VideoAnalyzeStepConfig as VideoAnalyzeStepConfigValue } from '@/lib/types/workflow';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  // A function, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});
afterEach(() => cleanup());

function renderConfig(overrides: Partial<VideoAnalyzeStepConfigValue> = {}) {
  const config = { ...createDefaultVideoAnalyzeStepConfig(), ...overrides };
  const onChange = vi.fn();
  render(
    <MantineProvider>
      <VideoAnalyzeStepConfig
        config={config}
        onChange={onChange}
        currentStepIndex={0}
        allSteps={[]}
        projectId="p1"
      />
    </MantineProvider>,
  );
  return { onChange, config };
}

const lastCall = (onChange: ReturnType<typeof vi.fn>) =>
  onChange.mock.calls.at(-1)![0] as VideoAnalyzeStepConfigValue;

// The whole step form is a large render (dozens of Mantine inputs). Every test here mounts it, so
// the suite pins its own timeout instead of sitting at the 5s default and timing out on a loaded box.
describe('VideoAnalyzeStepConfig — tracked objects', { timeout: 15_000 }, () => {
  it('shows one row per object: a prompt field and a method select, no segmentation knobs yet', () => {
    renderConfig({ trackObjects: [{ label: 'speedometer' }] });

    const prompt = screen.getByRole('textbox', { name: /^What to follow/ });
    expect(prompt.getAttribute('maxlength')).toBe('120');
    expect((prompt as HTMLInputElement).value).toBe('speedometer');
    // Absent method reads as Feature, the C# record's default.
    expect(screen.getByRole('combobox', { name: /^How to follow it/ })).toBeTruthy();
    expect(screen.queryByRole('switch', { name: /^Send this clip to the configured provider/ })).toBeNull();
  });

  it('writes only the chosen target\'s method, leaving the rest of the config alone', () => {
    const { onChange, config } = renderConfig({ trackObjects: [{ label: 'speedometer' }] });

    fireEvent.click(screen.getByRole('combobox', { name: /^How to follow it/ }));
    fireEvent.click(screen.getByRole('option', { name: 'AI segmentation (paid)' }));

    expect(lastCall(onChange)).toEqual({
      ...config,
      trackObjects: [{ label: 'speedometer', method: 'Segmentation' }],
    });
  });

  it('reveals the consent, the provider and the four knobs once an object uses segmentation', () => {
    renderConfig({ trackObjects: [{ label: 'speedometer', method: 'Segmentation' }] });

    expect(screen.getByRole('switch', { name: /^Send this clip to the configured provider/ })).toBeTruthy();
    expect(screen.getByRole('combobox', { name: /^Segmentation provider/ })).toBeTruthy();
    for (const label of [/^Longest clip analysed/, /^Detection threshold/, /^Objects at most per clip/, /^Provider timeout/]) {
      expect(screen.getByRole('textbox', { name: label })).toBeTruthy();
    }
  });

  it('warns in words, naming the refusal, while consent is unticked', () => {
    renderConfig({ trackObjects: [{ label: 'speedometer', method: 'Segmentation' }] });

    expect(screen.getByText(/EGRESS_REFUSED/)).toBeTruthy();
    // The provider is named as the destination, and retention is stated, never implied.
    expect(screen.getByText(/uploaded to the segmentation provider/)).toBeTruthy();
  });

  it('records the consent on the config when the box is ticked', () => {
    const { onChange } = renderConfig({ trackObjects: [{ label: 'speedometer', method: 'Segmentation' }] });

    fireEvent.click(screen.getByRole('switch', { name: /^Send this clip to the configured provider/ }));

    // Only the payload is asserted: this component is controlled, so the warning about the CURRENT
    // config stays on screen until the parent hands back a config with the consent set.
    expect(lastCall(onChange).allowSourceMediaEgress).toBe(true);
  });

  it('adds an object without touching any other field', () => {
    const { onChange, config } = renderConfig();

    fireEvent.click(screen.getByRole('button', { name: /^Add an object/ }));

    expect(lastCall(onChange)).toEqual({ ...config, trackObjects: [{ label: '' }] });
  });

  it('keeps a hand-marked area when a labelled target beside it is edited', () => {
    const region = { x0: 0, y0: 0, x1: 0.2, y1: 0, x2: 0, y2: 0.2, x3: 0.2, y3: 0.2 };
    const { onChange } = renderConfig({ trackObjects: [{ label: 'plate' }, { label: 'marked by hand', region }] });

    fireEvent.change(screen.getByRole('textbox', { name: /^What to follow/ }), { target: { value: 'plate 2' } });

    expect(lastCall(onChange).trackObjects).toEqual([{ label: 'plate 2' }, { label: 'marked by hand', region }]);
    // The area is not a row of its own, so it is reported as a count rather than silently dropped.
    expect(screen.getByText(/1 area marked by hand/)).toBeTruthy();
  });
});
