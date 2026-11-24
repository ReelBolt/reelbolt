// @vitest-environment jsdom
/**
 * prompted-region-effects T-006 — the VideoCompile form's effect rows, and the one list they share
 * with the free tracker's hidden objects.
 *
 * What these tests protect:
 *  - adding or removing a row changes the engine's own field and NOTHING else (the ticket's "old
 *    fields byte-identical" rule), because a compile config that quietly rewrote itself would change
 *    the rendered argv of an existing step;
 *  - a row is written where the engine that follows its object reads it — `censorLabels` (+ the
 *    shared style) for the free tracker, one `regionEffects` entry for segmentation, never both;
 *  - the effect control offers only the effects the chosen engine can actually render;
 *  - every refusal the engine has for the paid list is said on the screen, with its code, before a run;
 *  - the effect select offers exactly the closed enum, and never a free-text filter string.
 */
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { useState } from 'react';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));

import { VideoCompileStepConfig, createDefaultVideoCompileStepConfig } from './VideoCompileStepConfig';
import type { VideoCompileStepConfig as VideoCompileStepConfigValue, VideoRegionEffect } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

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
});
afterEach(() => cleanup());

/** An earlier 'Watch footage' step whose tracked objects the object control can offer. */
const ANALYZE_STEPS = [{
  tempId: 'a',
  stepType: 'VideoAnalyze',
  label: 'Watch footage',
  videoAnalyzeConfig: {
    trackObjects: [
      { label: 'speedometer' },
      { label: 'licence plate', method: 'Feature' },
      { label: 'speedometer', method: 'Segmentation' },
    ],
  },
}] as unknown as StepData[];

/** An earlier 'Watch footage' step that follows nothing yet: the object control is free text only. */
const SILENT_ANALYZE_STEPS = [
  { tempId: 'a', stepType: 'VideoAnalyze', label: 'Watch footage' },
] as unknown as StepData[];

function renderConfig(
  overrides: Partial<VideoCompileStepConfigValue> = {},
  allSteps: StepData[] = SILENT_ANALYZE_STEPS,
) {
  const config = { ...createDefaultVideoCompileStepConfig(), ...overrides };
  const onChange = vi.fn();
  render(
    <MantineProvider>
      <VideoCompileStepConfig
        config={config}
        onChange={onChange}
        currentStepIndex={1}
        allSteps={allSteps}
        projectId="p1"
      />
    </MantineProvider>,
  );
  return { onChange, config };
}

/** Controlled harness: a committed row only comes back if the config does, so the round trip is real. */
function renderControlled(overrides: Partial<VideoCompileStepConfigValue> = {}) {
  const onChange = vi.fn();
  function Harness() {
    const [config, setConfig] = useState({ ...createDefaultVideoCompileStepConfig(), ...overrides });
    return (
      <VideoCompileStepConfig
        config={config}
        onChange={(next) => {
          onChange(next);
          setConfig(next);
        }}
        currentStepIndex={1}
        allSteps={ANALYZE_STEPS}
        projectId="p1"
      />
    );
  }
  render(<MantineProvider><Harness /></MantineProvider>);
  return onChange;
}

const lastCall = (onChange: ReturnType<typeof vi.fn>) =>
  onChange.mock.calls.at(-1)![0] as VideoCompileStepConfigValue;

// The whole step form is a large render (dozens of Mantine inputs). Every test here mounts it, so
// the suite pins its own timeout instead of sitting at the 5s default and timing out on a loaded box.
describe('VideoCompileStepConfig — effects on objects', { timeout: 15_000 }, () => {
  it('writes nothing while the new row is unnamed, then only the field its engine reads', () => {
    const { onChange, config } = renderConfig({}, ANALYZE_STEPS);

    fireEvent.click(screen.getByRole('button', { name: /^Add an effect/ }));
    // The row is a draft until it names an object: a half-typed word must never reach the config.
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('combobox', { name: 'Object' }));
    fireEvent.click(screen.getByRole('option', { name: 'speedometer — segmentation (paid)' }));

    // One `regionEffects` entry, and not one other field of the config rewritten.
    expect(lastCall(onChange)).toEqual({ ...config, regionEffects: [{ target: 'speedometer', effect: 'Blur' }] });
  });

  it('removes only the row that was removed', () => {
    const effects: VideoRegionEffect[] = [
      { target: 'g1', effect: 'Blur' },
      { target: 'g2', effect: 'Outline' },
    ];
    const { onChange } = renderConfig({ regionEffects: effects });

    fireEvent.click(screen.getAllByRole('button', { name: /^Remove this effect/ })[0]);

    expect(lastCall(onChange).regionEffects).toEqual([{ target: 'g2', effect: 'Outline' }]);
  });

  it('offers exactly the six effects, in the engine\'s own order', () => {
    renderConfig({ regionEffects: [{ target: 'g1', effect: 'Blur' }] });

    fireEvent.click(screen.getByRole('combobox', { name: /^Effect$/ }));

    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      'Blur',
      'Pixelate',
      'Highlight it and dim the rest',
      'Spotlight: dim everything else',
      'Colour pop: keep colour on it only',
      'Outline',
    ]);
  });

  it('names EFFECT_INVALID while a row has no object', () => {
    renderConfig({ regionEffects: [{ target: '   ', effect: 'Blur' }] });

    expect(screen.getByText(/EFFECT_INVALID/)).toBeTruthy();
  });

  it('says EFFECT_REQUIRES_REENCODE when the mode is stream copy', () => {
    renderConfig({ mode: 'StreamCopy', regionEffects: [{ target: 'g1', effect: 'Blur' }] });

    expect(screen.getByText(/EFFECT_REQUIRES_REENCODE/)).toBeTruthy();
  });

  it('stops at eight rows and names EFFECT_LIMIT_EXCEEDED for a list written elsewhere', () => {
    const eight = Array.from({ length: 8 }, (_, i) => ({ target: `g${i + 1}`, effect: 'Blur' as const }));
    renderConfig({ regionEffects: eight });
    expect(screen.getByRole('button', { name: /^Add an effect/ }).hasAttribute('disabled')).toBe(true);
    // At the limit the section only explains the cap; nothing is refused yet.
    expect(screen.queryByText(/EFFECT_LIMIT_EXCEEDED: this config lists/)).toBeNull();

    cleanup();

    const nine = [...eight, { target: 'g9', effect: 'Blur' as const }];
    renderConfig({ regionEffects: nine });
    // A list written elsewhere (assistant, template, API) is named, not silently truncated.
    expect(screen.getByText(/EFFECT_LIMIT_EXCEEDED: this config lists 9 effects/)).toBeTruthy();
  });

  it('writes a row\'s own fields without disturbing its siblings', () => {
    const effects: VideoRegionEffect[] = [
      { target: 'g1', effect: 'Blur' },
      { target: 'g2', effect: 'Pixelate', invert: true },
    ];
    const { onChange } = renderConfig({ regionEffects: effects });

    const strength = screen.getAllByRole('textbox', { name: /^Strength/ })[0];
    fireEvent.change(strength, { target: { value: '1.5' } });

    expect(lastCall(onChange).regionEffects).toEqual([
      { target: 'g1', effect: 'Blur', strength: 1.5 },
      { target: 'g2', effect: 'Pixelate', invert: true },
    ]);
  });

  it('renders a saved config\'s regionEffects as segmentation rows, with only their own extras', () => {
    renderConfig({ regionEffects: [{ target: 'g1', effect: 'Outline', invert: true }] });

    const objects = screen.getByRole('combobox', { name: 'Object' }) as HTMLInputElement;
    expect(objects.value).toBe('g1');
    const effect = screen.getByRole('combobox', { name: 'Effect' }) as HTMLInputElement;
    expect(effect.value).toBe('Outline');
    // The paid engine's own knobs, under the row…
    expect(screen.getByLabelText('Strength')).toBeTruthy();
    expect(screen.getByRole('switch', { name: /^Everything except this object/ })).toBeTruthy();
    // …and never the free tracker's, which that engine cannot render.
    expect(screen.queryByLabelText('Padding')).toBeNull();
    expect(screen.queryByLabelText('Hold (s)')).toBeNull();
  });

  it('writes a tracked object to censorLabels, and never to regionEffects', () => {
    const onChange = renderControlled();

    fireEvent.click(screen.getByRole('button', { name: /^Add an effect/ }));
    fireEvent.click(screen.getByRole('combobox', { name: 'Object' }));
    fireEvent.click(screen.getByRole('option', { name: 'speedometer — tracked' }));

    const written = lastCall(onChange);
    expect(written.censorLabels).toEqual(['speedometer']);
    // The row's effect control shows Blur, so Blur is what the config says.
    expect(written.censorStyle).toBe('Blur');
    expect(written.regionEffects).toBeUndefined();
  });

  it('writes a segmentation object to regionEffects, and never to censorLabels', () => {
    const onChange = renderControlled();

    fireEvent.click(screen.getByRole('button', { name: /^Add an effect/ }));
    fireEvent.click(screen.getByRole('combobox', { name: 'Object' }));
    fireEvent.click(screen.getByRole('option', { name: 'speedometer — segmentation (paid)' }));

    expect(lastCall(onChange).regionEffects).toEqual([{ target: 'speedometer', effect: 'Blur' }]);

    fireEvent.click(screen.getByRole('combobox', { name: 'Effect' }));
    fireEvent.click(screen.getByRole('option', { name: 'Highlight it and dim the rest' }));

    const written = lastCall(onChange);
    expect(written.regionEffects).toEqual([{ target: 'speedometer', effect: 'Highlight' }]);
    expect(written.censorLabels).toBeUndefined();
  });

  it('offers only the effects the chosen engine can render', () => {
    renderConfig({ censorLabels: ['licence plate'] });
    fireEvent.click(screen.getByRole('combobox', { name: 'Effect' }));
    const tracked = screen.getAllByRole('option').map((option) => option.textContent);
    expect(tracked).toEqual(['Blur', 'Pixelate', 'Solid black (strongest)']);
    expect(tracked).not.toContain('Highlight it and dim the rest');

    cleanup();

    renderConfig({ regionEffects: [{ target: 'g1', effect: 'Highlight' }] });
    fireEvent.click(screen.getByRole('combobox', { name: 'Effect' }));
    const segmentation = screen.getAllByRole('option').map((option) => option.textContent);
    expect(segmentation).toEqual([
      'Blur',
      'Pixelate',
      'Highlight it and dim the rest',
      'Spotlight: dim everything else',
      'Colour pop: keep colour on it only',
      'Outline',
    ]);
    expect(segmentation).not.toContain('Solid black (strongest)');
  });

  it('moves a row to the other engine\'s field when its object is re-pointed', () => {
    const onChange = renderControlled({ censorLabels: ['licence plate'], censorStyle: 'Pixelate' });

    fireEvent.click(screen.getByRole('combobox', { name: 'Object' }));
    fireEvent.click(screen.getByRole('option', { name: 'speedometer — segmentation (paid)' }));

    const written = lastCall(onChange);
    expect(written.censorLabels).toBeNull();
    expect(written.regionEffects).toEqual([{ target: 'speedometer', effect: 'Blur' }]);
  });
});
