// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import type { VideoAnalyzeStepConfig as VideoAnalyzeStepConfigValue } from '@/lib/types/workflow';
import type { Mock } from 'vitest';

afterEach(() => cleanup());

beforeAll(() => {
  // Mock matchMedia
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(query => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });

  // Mock ResizeObserver
  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

const renderWithMantine = (component: React.ReactElement) => {
  return render(<MantineProvider>{component}</MantineProvider>);
};

describe('VideoAnalyzeStepConfig', () => {
  it('routes an edited field through onChange while preserving unknown top-level keys', () => {
    const handleChange = vi.fn();

    const config = {
      ...createDefaultVideoAnalyzeStepConfig(),
      unknownKey: 'test-value',
    } as VideoAnalyzeStepConfigValue & { unknownKey: string };

    renderWithMantine(
      <VideoAnalyzeStepConfig
        config={config}
        onChange={handleChange}
        projectId="test-project"
        currentStepIndex={0}
        allSteps={[]}
      />,
    );

    // Verify the form renders
    expect(screen.getByText('Detect silence')).toBeTruthy();

    // Find and interact with the detectSilence switch
    const detectSilenceSwitch = screen.getByRole('switch', { name: /^Detect silence/ });
    expect(detectSilenceSwitch).toBeTruthy();

    // Fire change event to toggle the switch
    fireEvent.click(detectSilenceSwitch);

    // Verify onChange was called with the updated detectSilence and the unknown key preserved
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;
    expect(callArg.detectSilence).toBe(false);
    expect(callArg.unknownKey).toBe('test-value');
  });
});
