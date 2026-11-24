// @vitest-environment jsdom
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { RoomStepConfigEditor } from './RoomStepConfigEditor';
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

describe('RoomStepConfigEditor', () => {
  it('preserves unknown top-level keys when editing EditRoom rounds', () => {
    const handleChange = vi.fn();
    const configWithUnknown = JSON.stringify({
      version: 1,
      view: { from: 'Previous' },
      rounds: 2,
      maxTurns: 8,
      persistTranscript: true,
      fallbackToSoloEditor: true,
      customTemplateField: 'template-value',
      conveneGate: 'Shadow',
      conveneAcceptAt: 0.85,
      conveneMinMargin: 0.25,
      conveneProviderId: null,
    });

    const { container } = renderWithMantine(
      <RoomStepConfigEditor
        stepType="EditRoom"
        configJson={configWithUnknown}
        onChange={handleChange}
        allSteps={[]}
        currentStepIndex={0}
      />,
    );

    // Find the Fieldset (contains the form) and look for inputs within it
    const fieldset = container.querySelector('fieldset');
    expect(fieldset).toBeTruthy();

    // Find decimal inputs within the fieldset only
    const fieldsetInputs = Array.from(fieldset!.querySelectorAll('input[inputmode="decimal"]'));
    // Find by value since the form renders fields in alphabetical order (maxTurns before rounds)
    const roundsInput = fieldsetInputs.find((i) => (i as HTMLInputElement).value === '2') as HTMLInputElement;
    expect(roundsInput).toBeTruthy();
    expect(roundsInput.value).toBe('2');

    // Fire change event to update rounds
    fireEvent.change(roundsInput, { target: { value: '3' } });

    // Blur to trigger validation
    fireEvent.blur(roundsInput);

    // Find and click the "Apply" button within the fieldset
    const applyButton = within(fieldset!).getByRole('button', { name: 'Apply' });
    fireEvent.click(applyButton);

    // Verify onChange was called with the updated rounds and the unknown field preserved
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0] as string;
    const parsedArg = JSON.parse(callArg);
    expect(parsedArg.rounds).toBe(3);
    expect(parsedArg.customTemplateField).toBe('template-value');
    // The phase-2 convening-gate fields the editor now exposes round-trip like any other known field
    // (and, like every other field, an unknown one is still preserved untouched).
    expect(parsedArg.conveneGate).toBe('Shadow');
    expect(parsedArg.conveneAcceptAt).toBe(0.85);
    expect(parsedArg.conveneMinMargin).toBe(0.25);
    expect(parsedArg.conveneProviderId).toBeNull();
  });

  it('preserves unknown top-level keys when editing GraphicsRoom maxTurns', () => {
    const handleChange = vi.fn();
    const configWithUnknown = JSON.stringify({
      version: 1,
      view: { from: 'Previous' },
      rounds: 2,
      maxTurns: 8,
      persistTranscript: true,
      fallbackToSoloPlanner: true,
      graphicsRoomSeats: ['LayoutArtist', 'TimingArtist'],
      conveneGate: 'Off',
      conveneAcceptAt: 0.85,
      conveneMinMargin: 0.25,
      conveneProviderId: null,
    });

    const { container } = renderWithMantine(
      <RoomStepConfigEditor
        stepType="GraphicsRoom"
        configJson={configWithUnknown}
        onChange={handleChange}
        allSteps={[]}
        currentStepIndex={0}
      />,
    );

    // Find the Fieldset (contains the form) and look for inputs within it
    const fieldset = container.querySelector('fieldset');
    expect(fieldset).toBeTruthy();

    // Find decimal inputs within the fieldset only
    const fieldsetInputs = Array.from(fieldset!.querySelectorAll('input[inputmode="decimal"]'));
    // maxTurns is the second input (after rounds) - but be defensive and find by value
    const maxTurnsInput = fieldsetInputs.find((i) => (i as HTMLInputElement).value === '8') as HTMLInputElement;
    expect(maxTurnsInput).toBeTruthy();
    expect(maxTurnsInput.value).toBe('8');

    // Fire change event to update maxTurns
    fireEvent.change(maxTurnsInput, { target: { value: '10' } });

    // Blur to trigger validation
    fireEvent.blur(maxTurnsInput);

    // Find and click the "Apply" button within the fieldset
    const applyButton = within(fieldset!).getByRole('button', { name: 'Apply' });
    fireEvent.click(applyButton);

    // Verify onChange was called with the updated maxTurns and the unknown field preserved
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0] as string;
    const parsedArg = JSON.parse(callArg);
    expect(parsedArg.maxTurns).toBe(10);
    expect(parsedArg.graphicsRoomSeats).toEqual(['LayoutArtist', 'TimingArtist']);
    expect(parsedArg.conveneGate).toBe('Off');
  });

  it('preserves unknown top-level keys when editing ColorGradeRoom persistTranscript', () => {
    const handleChange = vi.fn();
    const configWithUnknown = JSON.stringify({
      version: 1,
      view: { from: 'Previous' },
      rounds: 2,
      maxTurns: 8,
      persistTranscript: true,
      fallbackToSoloColorist: true,
      gradeRoomTerminationMode: 'consensus',
      conveneGate: 'Gate',
      conveneAcceptAt: 0.9,
      conveneMinMargin: 0.3,
      conveneProviderId: '11111111-1111-1111-1111-111111111111',
    });

    const { container } = renderWithMantine(
      <RoomStepConfigEditor
        stepType="ColorGradeRoom"
        configJson={configWithUnknown}
        onChange={handleChange}
        allSteps={[]}
        currentStepIndex={0}
      />,
    );

    // Find the Fieldset (contains the form) and look for switches within it
    const fieldset = container.querySelector('fieldset');
    expect(fieldset).toBeTruthy();

    // Find all checkbox switches within the fieldset only
    const fieldsetSwitches = fieldset!.querySelectorAll('input[type="checkbox"][role="switch"]');
    // persistTranscript is the first switch
    const persistSwitch = fieldsetSwitches[0] as HTMLInputElement;

    expect(persistSwitch).toBeTruthy();
    expect(persistSwitch.checked).toBe(true);

    // Fire change event to toggle persistTranscript
    fireEvent.click(persistSwitch);
    fireEvent.change(persistSwitch, { target: { checked: !persistSwitch.checked } });

    // Find and click the "Apply" button within the fieldset
    const applyButton = within(fieldset!).getByRole('button', { name: 'Apply' });
    fireEvent.click(applyButton);

    // Verify onChange was called with the updated persistTranscript and the unknown field preserved
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0] as string;
    const parsedArg = JSON.parse(callArg);
    expect(parsedArg.persistTranscript).toBe(false);
    expect(parsedArg.gradeRoomTerminationMode).toBe('consensus');
    expect(parsedArg.conveneGate).toBe('Gate');
    expect(parsedArg.conveneProviderId).toBe('11111111-1111-1111-1111-111111111111');
  });

  /**
   * Media decision 7: the convening gate ships for the ColorGrade room only, and the editor has to
   * say so — an author who turns it on in the other two rooms would otherwise see no effect and have
   * no way to know the backend is ignoring it on purpose rather than silently broken.
   */
  it('tells the author that EditRoom and GraphicsRoom treat the convening gate as a no-op', () => {
    // Scoped to each test's own container: this file has no RTL auto-cleanup (vitest.config.ts sets
    // no `globals`/setup file), so earlier renders are still mounted in document.body.
    for (const stepType of ['EditRoom', 'GraphicsRoom'] as const) {
      const { container, unmount } = renderWithMantine(
        <RoomStepConfigEditor stepType={stepType} configJson={null} onChange={vi.fn()} />,
      );
      expect(within(container).getByText(/no effect on the/)).toBeTruthy();
      unmount();
    }
  });

  it('describes what the convening gate does for the ColorGrade room', () => {
    const { container } = renderWithMantine(
      <RoomStepConfigEditor stepType="ColorGradeRoom" configJson={null} onChange={vi.fn()} />,
    );
    expect(within(container).getByText(/The room always convenes\./)).toBeTruthy();
    expect(within(container).queryByText(/no effect on the/)).toBeNull();
  });
});
