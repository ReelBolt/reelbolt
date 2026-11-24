// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TimecodeInput } from '../TimecodeInput';
import { TimelineSettingsPanel, FRAME_RATE_LOCKED_REASON, timelineHasClips } from '../../panels/TimelineSettingsPanel';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

afterEach(cleanup);
const R30 = { num: 30, den: 1 };

const mount = (onCommit = vi.fn(), frames = 0) => {
  render(
    <MantineProvider>
      <TimecodeInput label="Start" frames={frames} rate={R30} onCommit={onCommit} data-testid="tc" />
    </MantineProvider>,
  );
  return { input: screen.getByTestId('tc') as HTMLInputElement, onCommit };
};

describe('R01.5 TimecodeInput', () => {
  it('shows typed digits as a timecode and commits the frames on Enter', () => {
    const { input, onCommit } = mount();
    expect(input.getAttribute('inputmode')).toBe('numeric');
    for (const typed of ['1', '12', '121', '1215']) fireEvent.change(input, { target: { value: typed } });
    expect(input.value).toBe('00:00:12:15');
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onCommit).toHaveBeenCalledExactlyOnceWith(12 * 30 + 15);
  });

  it('cancels on Escape and keeps the stored value', () => {
    const { input, onCommit } = mount(vi.fn(), 30);
    fireEvent.change(input, { target: { value: '99' } });
    fireEvent.keyDown(input, { key: 'Escape' });
    fireEvent.blur(input);
    expect(onCommit).not.toHaveBeenCalled();
    expect(input.value).toBe('00:00:01:00');
  });

  it('reverts an invalid entry and says why', () => {
    const { input, onCommit } = mount(vi.fn(), 30);
    fireEvent.change(input, { target: { value: '0099' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onCommit).not.toHaveBeenCalled();
    expect(input.value).toBe('00:00:01:00');
    expect(screen.getByText('Frames go up to 29 at this frame rate.')).toBeTruthy();
  });
});

describe('R01.3 settings panel frame rate lock', () => {
  const settings = { width: 1920, height: 1080, rate: R30, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit' as const, startTimecodeFrames: 0 };
  it('on an empty timeline the rate is editable and applies as one patch', () => {
    const onChange = vi.fn();
    render(
      <MantineProvider>
        <TimelineSettingsPanel settings={settings} onChange={onChange} hasClips={false} />
      </MantineProvider>,
    );
    expect(screen.queryByTestId('settings-rate-locked')).toBeNull();
    expect((screen.getByTestId('settings-fps') as HTMLInputElement).disabled).toBe(false);
    fireEvent.change(screen.getByTestId('settings-rate-num'), { target: { value: '24' } });
    fireEvent.click(screen.getByTestId('settings-apply'));
    expect(onChange).toHaveBeenCalledWith({ rate: { num: 24, den: 1 } });
  });
  it('with clips the rate controls are disabled and the reason is visible', () => {
    render(
      <MantineProvider>
        <TimelineSettingsPanel settings={settings} onChange={vi.fn()} hasClips />
      </MantineProvider>,
    );
    expect(screen.getByTestId('settings-rate-locked').textContent).toBe(FRAME_RATE_LOCKED_REASON);
    expect((screen.getByTestId('settings-fps') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByTestId('settings-rate-num') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByTestId('settings-rate-den') as HTMLInputElement).disabled).toBe(true);
  });
  it('timelineHasClips sees clips on tracks and inside compounds', () => {
    expect(timelineHasClips({ tracks: [{ items: [] }] })).toBe(false);
    expect(timelineHasClips({ tracks: [{ items: [{}] }] })).toBe(true);
    expect(timelineHasClips({ tracks: [{ items: [] }], sequences: [{ tracks: [{ items: [{}] }] }] })).toBe(true);
  });
});
