// @vitest-environment jsdom
/**
 * "Cut points inside long shots": the sub-shot mode and its three numbers, shown only when the
 * mode is not Off. An untouched config must reach onChange unchanged.
 */
import { useState } from 'react';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import type { VideoAnalyzeStepConfig as Cfg } from '@/lib/types/workflow';

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: [] }),
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

function renderConfig(overrides: Partial<Cfg> & Record<string, unknown>) {
  const onChange: Mock = vi.fn();
  const config = { ...createDefaultVideoAnalyzeStepConfig(), ...overrides } as Cfg;
  render(
    <MantineProvider>
      <VideoAnalyzeStepConfig config={config} onChange={onChange} projectId="p1" />
    </MantineProvider>,
  );
  return { onChange, config };
}

describe('VideoAnalyzeStepConfig — cut points inside long shots', () => {
  it('renders the mode from the JSON', () => {
    renderConfig({ subShotMode: 'Content' });
    const select = screen.getByRole('combobox', { name: /^Cut points inside long shots/ }) as HTMLInputElement;
    expect(select.value).toBe('Where the picture pauses or changes');
  });

  it('writes subShotMode Content when chosen', () => {
    const { onChange } = renderConfig({});
    fireEvent.click(screen.getByRole('combobox', { name: /^Cut points inside long shots/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Where the picture pauses or changes' }));
    expect(onChange.mock.calls.at(-1)![0].subShotMode).toBe('Content');
  });

  it('hides the three numbers while Off and shows them with the defaults as placeholders', () => {
    const { onChange } = renderConfig({});
    expect(screen.queryByRole('textbox', { name: /^Shortest part/ })).toBeNull();
    expect(screen.queryByRole('textbox', { name: /^Longest part/ })).toBeNull();
    expect(screen.queryByRole('textbox', { name: /^Most parts per shot/ })).toBeNull();

    cleanup();
    renderConfig({ subShotMode: 'Even' });
    expect(screen.getByRole('textbox', { name: /^Shortest part/ }).getAttribute('placeholder')).toBe('1.2');
    expect(screen.getByRole('textbox', { name: /^Longest part/ }).getAttribute('placeholder')).toBe('4');
    expect(screen.getByRole('textbox', { name: /^Most parts per shot/ }).getAttribute('placeholder')).toBe('8');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('routes an edited number and keeps unknown keys', () => {
    const { onChange } = renderConfig({ subShotMode: 'Content', unknownKey: 'kept' } as Partial<Cfg> & Record<string, unknown>);
    fireEvent.change(screen.getByRole('textbox', { name: /^Most parts per shot/ }), { target: { value: '12' } });
    const next = onChange.mock.calls.at(-1)![0] as Record<string, unknown>;
    expect(next.maxSubShotsPerShot).toBe(12);
    expect(next.unknownKey).toBe('kept');
  });

  it('types 1 then 6 into the parts field without clamping mid-typing', async () => {
    const user = userEvent.setup();
    const onChange: Mock = vi.fn();
    function Harness() {
      const [config, setConfig] = useState({ ...createDefaultVideoAnalyzeStepConfig(), subShotMode: 'Content' } as Cfg);
      return (
        <MantineProvider>
          <VideoAnalyzeStepConfig config={config} onChange={(next) => { onChange(next); setConfig(next); }} projectId="p1" />
        </MantineProvider>
      );
    }
    render(<Harness />);
    const parts = screen.getByRole('textbox', { name: /^Most parts per shot/ });
    await user.click(parts);
    await user.keyboard('16');
    expect(onChange.mock.calls.at(-1)![0].maxSubShotsPerShot).toBe(16);
  });

  it('clamps a typed 99 to 64 on blur, not while typing', async () => {
    const user = userEvent.setup();
    const onChange: Mock = vi.fn();
    function Harness() {
      const [config, setConfig] = useState({ ...createDefaultVideoAnalyzeStepConfig(), subShotMode: 'Content' } as Cfg);
      return (
        <MantineProvider>
          <VideoAnalyzeStepConfig config={config} onChange={(next) => { onChange(next); setConfig(next); }} projectId="p1" />
        </MantineProvider>
      );
    }
    render(<Harness />);
    const parts = screen.getByRole('textbox', { name: /^Most parts per shot/ });
    await user.click(parts);
    await user.keyboard('99');
    expect(onChange.mock.calls.at(-1)![0].maxSubShotsPerShot).toBe(99);
    await user.tab();
    expect(onChange.mock.calls.at(-1)![0].maxSubShotsPerShot).toBe(64);
  });

  it('leaves an untouched config byte-identical', () => {
    const json = JSON.stringify({ ...createDefaultVideoAnalyzeStepConfig(), maxShotSec: 6, unknownKey: 'kept' });
    const parsed = JSON.parse(json) as Cfg;
    const { onChange } = renderConfig(parsed as Partial<Cfg>);
    expect(onChange).not.toHaveBeenCalled();
    expect(JSON.stringify(parsed)).toBe(json);
    expect(parsed).not.toHaveProperty('subShotMode');
  });
});
