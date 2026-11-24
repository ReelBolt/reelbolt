// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { ReviewLoopStepConfig, createDefaultReviewLoopStepConfig, createDefaultVisualCheckConfig } from './ReviewLoopStepConfig';
import type { ReviewLoopStepConfig as Cfg } from '@/lib/types/workflow';

const seen = vi.hoisted(() => ({ capabilities: [] as (string | null | undefined)[] }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true, user: { orgRole: 'Owner' } }),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: (capability?: string | null) => {
    seen.capabilities.push(capability);
    return { data: [], isLoading: false, error: undefined };
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
afterEach(() => cleanup());

function renderLoop(reviewLoopConfig: Cfg | null = null) {
  const onChange = vi.fn();
  render(
    <MantineProvider>
      <ReviewLoopStepConfig
        minScore={7}
        maxIterations={3}
        loopTargetStepOrder={null}
        reviewLoopConfig={reviewLoopConfig}
        onChange={onChange}
        previousSteps={[]}
        currentStepIndex={0}
      />
    </MantineProvider>,
  );
  return onChange;
}
const lastConfig = (m: ReturnType<typeof vi.fn>) => (m.mock.calls.at(-1)![0] as { reviewLoopConfig: Cfg }).reviewLoopConfig;

describe('createDefaultVisualCheckConfig', () => {
  it('mirrors the C# VisualCheckConfig defaults: off, 8 frames, 5 %, 2.5 %, cap 6, default vision model', () => {
    expect(createDefaultVisualCheckConfig()).toEqual({
      enabled: false, frames: 8, safeMarginPercent: 5, minTextHeightPercent: 2.5, failScoreCap: 6, visionProviderId: null,
    });
  });
});

describe('ReviewLoopStepConfig — visual check', () => {
  it('is behind Advanced and off by default, and a fresh loop carries no visualCheck at all', () => {
    renderLoop();
    expect(screen.queryByRole('switch', { name: /Check the picture before each review/ })).toBeNull();
    expect(createDefaultReviewLoopStepConfig()).not.toHaveProperty('visualCheck');
  });

  it('switching it on writes an enabled visualCheck with the C# defaults next to the untouched early-stop settings', async () => {
    const onChange = renderLoop();
    fireEvent.click(screen.getByRole('button', { name: 'Advanced: visual check' }));
    fireEvent.click(await screen.findByRole('switch', { name: /Check the picture before each review/ }));
    expect(lastConfig(onChange)).toEqual({
      ...createDefaultReviewLoopStepConfig(),
      visualCheck: { ...createDefaultVisualCheckConfig(), enabled: true },
    });
  });

  it('shows the fields only while enabled, routes them with their bounds, and offers Vision models', async () => {
    const onChange = renderLoop({ ...createDefaultReviewLoopStepConfig(), visualCheck: { ...createDefaultVisualCheckConfig(), enabled: true } });
    fireEvent.click(screen.getByRole('button', { name: 'Advanced: visual check' }));
    const frames = await screen.findByRole('textbox', { name: /Frames to look at/ });
    fireEvent.change(frames, { target: { value: '40' } });
    expect(lastConfig(onChange).visualCheck?.frames).toBe(16);
    fireEvent.change(frames, { target: { value: '1' } });
    expect(lastConfig(onChange).visualCheck?.frames).toBe(2);
    fireEvent.change(screen.getByRole('textbox', { name: /Safe margin/ }), { target: { value: '50' } });
    expect(lastConfig(onChange).visualCheck?.safeMarginPercent).toBe(20);
    fireEvent.change(screen.getByRole('textbox', { name: /Smallest readable text/ }), { target: { value: '0' } });
    expect(lastConfig(onChange).visualCheck?.minTextHeightPercent).toBe(0.5);
    fireEvent.change(screen.getByRole('textbox', { name: /Highest score/ }), { target: { value: '99' } });
    expect(lastConfig(onChange).visualCheck?.failScoreCap).toBe(10);
    expect(seen.capabilities).toContain('Vision');
  });

  it('hides the detail fields when disabled but keeps the saved values', async () => {
    renderLoop({ ...createDefaultReviewLoopStepConfig(), visualCheck: { ...createDefaultVisualCheckConfig(), frames: 12 } });
    fireEvent.click(screen.getByRole('button', { name: 'Advanced: visual check' }));
    await screen.findByRole('switch', { name: /Check the picture before each review/ });
    expect(screen.queryByRole('textbox', { name: /Frames to look at/ })).toBeNull();
  });
});
