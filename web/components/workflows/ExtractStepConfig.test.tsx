// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { ExtractStepConfig, createDefaultExtractStepConfig } from './ExtractStepConfig';
import type { ExtractStepConfig as ExtractStepConfigValue } from '@/lib/types/workflow';
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

describe('ExtractStepConfig', () => {
  it('preserves unknown top-level keys in the onChange payload through field edits', () => {
    const handleChange = vi.fn();
    const baseConfig = createDefaultExtractStepConfig('Project');
    const configWithUnknownKey: ExtractStepConfigValue & { unknownField?: string } = {
      ...baseConfig,
      unknownField: 'unknown-value',
    };

    renderWithMantine(
      <ExtractStepConfig
        config={configWithUnknownKey as ExtractStepConfigValue}
        onChange={handleChange}
        allSteps={[]}
        currentStepIndex={0}
      />,
    );

    // Verify initial render
    expect(screen.getByText(/Operation/)).toBeTruthy();

    // Find the "Path" field and modify it
    const pathInput = screen.getByDisplayValue('$.components') as HTMLInputElement;
    expect(pathInput).toBeTruthy();

    // Fire change event to update path - this triggers onChange directly via the patch() call
    fireEvent.change(pathInput, { target: { value: '$.newPath' } });

    // Verify onChange was called with the updated config
    expect(handleChange).toHaveBeenCalled();

    // Get the actual argument passed to onChange
    const onChangePayload = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;

    // Assert that the unknown field is preserved in the onChange payload
    expect(onChangePayload.unknownField).toBe('unknown-value');

    // Also verify that the edited field is included
    expect(onChangePayload.path).toBe('$.newPath');
  });

  it('should export createDefaultExtractStepConfig unchanged', () => {
    const config = createDefaultExtractStepConfig('Project');
    expect(config.operation).toBe('Project');
    expect(config.version).toBe(1);
    expect(config.idPrefix).toBe('i');
    expect(typeof createDefaultExtractStepConfig).toBe('function');
  });
});
