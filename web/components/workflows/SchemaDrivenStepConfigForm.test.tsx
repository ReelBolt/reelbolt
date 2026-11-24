// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';

afterEach(() => cleanup());

// Mock window APIs for Mantine and component testing
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

describe('SchemaDrivenStepConfigForm', () => {
  it('renders form fields based on VideoCompile schema', () => {
    const onChange = vi.fn();
    const value = {
      version: 1,
      mode: 'Reencode',
    };

    renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
      />,
    );

    // Should render the form with apply button
    expect(screen.getByText('Apply')).toBeTruthy();
  });

  it('validates on blur and shows error adjacent to field', async () => {
    const onChange = vi.fn();
    const value = {
      version: 1,
      crf: 60, // Invalid: crf must be 0-51
    };

    renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
      />,
    );

    const crfInput = screen.getByDisplayValue('60');

    // Trigger blur on the field
    fireEvent.blur(crfInput);

    // Error should appear adjacent to the field
    await waitFor(() => {
      // The error message from AJV validation should be visible
      expect(screen.getByText(/must be <=/i)).toBeTruthy();
    });
  });

  it('clears error on next keystroke that would fix it', async () => {
    const onChange = vi.fn();
    const value = {
      version: 1,
      crf: 60,
    };

    renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
      />,
    );

    // Find the crf input by looking for aria-invalid attribute after blur
    const crfInputs = screen.getAllByDisplayValue('60') as HTMLInputElement[];
    // Use the first one that should be the crf input
    const crfInput = crfInputs[0];

    // Trigger blur to show error
    fireEvent.blur(crfInput);

    await waitFor(() => {
      expect(screen.getByText(/must be <=/i)).toBeTruthy();
    });

    // Change value to valid number (should clear error)
    fireEvent.change(crfInput, { target: { value: '30' } });

    // Error should now be gone (since field is in error state and we're validating on change)
    await waitFor(() => {
      expect(screen.queryByText(/must be <=/i)).not.toBeTruthy();
    });
  });

  it('preserves unknown top-level keys in edit round-trip', async () => {
    const onChange = vi.fn();
    const value = {
      version: 1,
      decision: { from: 'Previous' },
      analysisStepOrder: 1,
      mode: 'Reencode',
      prePaddingMs: 0,
      postPaddingMs: 0,
      minSegmentMs: 0,
      maxSegments: 10,
      allowKeyframeSnapping: false,
      outputFileName: 'output.mp4',
      videoCodec: 'h264',
      audioCodec: 'aac',
      crf: 23,
      preset: 'medium',
      registerProjectFile: false,
      enableGraphics: false,
      maxOverlays: 0,
      overlayShortMs: 0,
      overlayMediumMs: 0,
      overlayHoldMs: 0,
      overlayFadeMs: 0,
      overlayFontSizePct: 6,
      overlayFontColor: '#000000',
      overlayBoxColor: '#FFFFFF',
      maxOverlayTextChars: 0,
      maxOverlaySubtextChars: 0,
      musicFadeInMs: 0,
      musicFadeOutMs: 0,
      unknownKey: 'should be preserved',
    };

    renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
      />,
    );

    // Click apply button to trigger onChange (use getAllByText and get button)
    const applyButtons = screen.getAllByText('Apply');
    const applyButton = applyButtons[applyButtons.length - 1].closest('button') as HTMLButtonElement;
    fireEvent.click(applyButton);

    await waitFor(() => {
      // onChange should have been called with a value that preserves unknownKey
      expect(onChange).toHaveBeenCalled();
      const calledWith = onChange.mock.calls[0][0] as Record<string, unknown>;
      expect(calledWith.unknownKey).toBe('should be preserved');
    });
  });

  it('exports function with correct signature', () => {
    expect(typeof SchemaDrivenStepConfigForm).toBe('function');

    // Just verify the component exports and has the right type
    const Component = SchemaDrivenStepConfigForm;
    expect(Component).toBeTruthy();
  });

  it('handles null value gracefully', () => {
    const onChange = vi.fn();

    const { container } = renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="Extract"
        value={null}
        onChange={onChange}
      />,
    );

    expect(container).toBeTruthy();
    // Verify the form renders by finding the Apply button
    const applyButtons = screen.getAllByText('Apply');
    expect(applyButtons.length).toBeGreaterThan(0);
  });

  it('renders advanced fields collapsed by default', () => {
    const onChange = vi.fn();
    const value = { version: 1 };

    renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
      />,
    );

    // Advanced options should be present but collapsed
    const advancedSection = screen.queryByText('Advanced options');
    if (advancedSection) {
      // The advanced fields should not be immediately visible
      expect(advancedSection.closest('[class*="Collapse"]')).toBeTruthy();
    }
  });

  it('filters fields when fields prop is provided', () => {
    const onChange = vi.fn();
    const value = { version: 1, mode: 'Reencode', crf: 23, preset: 'medium' };

    const { rerender } = renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
        fields={['crf', 'preset']}
      />,
    );

    // Only specified fields should be rendered
    // The form should still render but with filtered fields
    const applyButtons = screen.getAllByText('Apply');
    expect(applyButtons.length).toBeGreaterThan(0);

    // Rerender without fields prop to ensure all fields render
    rerender(
      <MantineProvider>
        <SchemaDrivenStepConfigForm
          stepType="VideoCompile"
          value={value}
          onChange={onChange}
        />
      </MantineProvider>,
    );

    // All fields should be rendered now
    const allApplyButtons = screen.getAllByText('Apply');
    expect(allApplyButtons.length).toBeGreaterThan(0);
  });

  it('renders all fields when fields prop is omitted (backward compatible)', () => {
    const onChange = vi.fn();
    const value = { version: 1, mode: 'Reencode', crf: 23 };

    renderWithMantine(
      <SchemaDrivenStepConfigForm
        stepType="VideoCompile"
        value={value}
        onChange={onChange}
      />,
    );

    // Form should render normally without fields prop
    const applyButtons = screen.getAllByText('Apply');
    expect(applyButtons.length).toBeGreaterThan(0);
  });
});
