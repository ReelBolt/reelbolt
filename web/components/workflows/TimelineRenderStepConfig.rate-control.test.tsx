// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TimelineRenderStepConfig, createDefaultTimelineRenderStepConfig } from './TimelineRenderStepConfig';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({ matches: false, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn() })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () { return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() }; });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => cleanup());

const encode = { width: null, height: null, codec: 'H264' as const, crf: 18, speed: 'Medium' as const, audioBitrateKbps: 192 };

describe('TimelineRenderStepConfig rate control', () => {
  it('offers the rate control and shows the bitrate only for Capped or Average', () => {
    const onChange = vi.fn();
    const { rerender } = render(
      <MantineProvider><TimelineRenderStepConfig config={{ ...createDefaultTimelineRenderStepConfig(), encode }} onChange={onChange} /></MantineProvider>,
    );
    expect(screen.getByLabelText(/How to set the file size/)).toBeTruthy();
    expect(screen.queryByLabelText(/Video bitrate/)).toBeNull();
    rerender(
      <MantineProvider><TimelineRenderStepConfig config={{ ...createDefaultTimelineRenderStepConfig(), encode: { ...encode, rateControl: 'Average', videoBitrateKbps: 5000 } }} onChange={onChange} /></MantineProvider>,
    );
    expect((screen.getByLabelText(/Video bitrate/) as HTMLInputElement).value).toBe('5000');
    fireEvent.change(screen.getByLabelText(/Video bitrate/), { target: { value: '6000' } });
    expect(onChange).toHaveBeenCalled();
  });
});
