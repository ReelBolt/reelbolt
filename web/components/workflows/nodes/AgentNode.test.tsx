// @vitest-environment jsdom
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { SchemaDrivenStepConfigForm } from '../SchemaDrivenStepConfigForm';
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

describe('AgentNode SchemaDrivenStepConfigForm', () => {
  it('performs a round-trip test for cacheMode field: change select and apply', () => {
    // This test verifies the cacheMode field through SchemaDrivenStepConfigForm directly.
    // The onChange closure reproduces AgentNode's real mapping logic:
    // onChange={(next) => onChange({ cacheMode: next.cacheMode === null ? null : (next.cacheMode as CacheMode) })}
    const handleChange = vi.fn();

    const { container } = renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="Agent"
        value={{ cacheMode: 'Default' }}
        onChange={(next) => {
          // Reproduce AgentNode's cacheMode onChange logic
          const cacheValue = (next as Record<string, unknown>).cacheMode;
          handleChange({
            cacheMode: cacheValue === null || cacheValue === undefined ? null : cacheValue,
          });
        }}
        fields={['cacheMode']}
      />,
    );

    // Find the select combobox and change the value
    const comboboxes = screen.getAllByRole('combobox');
    expect(comboboxes.length).toBeGreaterThan(0);

    const selectElement = comboboxes[0] as HTMLInputElement;
    // Change from 'Default' to 'Always'
    fireEvent.change(selectElement, { target: { value: 'Always' } });

    // Find and click the Apply button (scoped to this render's container)
    const applyButton = within(container).getByRole('button', { name: 'Apply' });
    fireEvent.click(applyButton);

    // Verify onChange was called with the updated cacheMode value
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0];
    expect(callArg.cacheMode).toBe('Always');
  });

  it('performs a round-trip test for inputMappingJson field: change text input and apply', () => {
    // This test verifies the inputMappingJson field through SchemaDrivenStepConfigForm directly.
    // The onChange closure reproduces AgentNode's real mapping logic:
    // onChange={(next) => onChange({ inputMappingJson: (next.inputMappingJson as string | null) ?? null })}
    const handleChange = vi.fn();

    const { container } = renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="Agent"
        value={{ inputMappingJson: null }}
        onChange={(next) => {
          // Reproduce AgentNode's inputMappingJson onChange logic
          handleChange({
            inputMappingJson: ((next as Record<string, unknown>).inputMappingJson as string | null) ?? null,
          });
        }}
        fields={['inputMappingJson']}
      />,
    );

    // Find the text input and change its value
    const textboxes = screen.getAllByRole('textbox');
    expect(textboxes.length).toBeGreaterThan(0);

    const inputField = textboxes[0] as HTMLInputElement;
    const testMapping = '{"input": "$.data.field"}';
    fireEvent.change(inputField, { target: { value: testMapping } });

    // Blur to trigger validation
    fireEvent.blur(inputField);

    // Find and click the Apply button (scoped to this render's container)
    const applyButton = within(container).getByRole('button', { name: 'Apply' });
    fireEvent.click(applyButton);

    // Verify onChange was called with the typed JSON value
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0];
    expect(callArg.inputMappingJson).toBe(testMapping);
  });
});
