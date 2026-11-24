// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RegenerateVideoClipsModal, getVideoGenerateInfo } from './RegenerateVideoClipsModal';

afterEach(() => cleanup());

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});

describe('getVideoGenerateInfo', () => {
  it('detects no video step', () => {
    expect(getVideoGenerateInfo([{ stepType: 'Agent' }])).toEqual({ has: false, capCredits: null });
  });
  it('sums max spend caps', () => {
    const steps = [
      { stepType: 'VideoGenerate' as const, videoGenerateConfigJson: '{"maxSpendCredits":2.5}' },
      { stepType: 'VideoGenerate' as const, videoGenerateConfigJson: '{"maxSpendCredits":1}' },
    ];
    expect(getVideoGenerateInfo(steps)).toEqual({ has: true, capCredits: 3.5 });
  });
  it('has no estimate when a cap is missing', () => {
    expect(getVideoGenerateInfo([{ stepType: 'VideoGenerate', videoGenerateConfigJson: '{}' }]))
      .toEqual({ has: true, capCredits: null });
  });
});

describe('RegenerateVideoClipsModal', () => {
  it('defaults to reuse and can force regenerate', () => {
    const onChoose = vi.fn();
    render(
      <MantineProvider>
        <RegenerateVideoClipsModal opened onClose={() => {}} onChoose={onChoose} capCredits={3.5} />
      </MantineProvider>,
    );
    expect(screen.getByText(/Upper bound: 3\.50 credits/)).toBeTruthy();
    fireEvent.click(screen.getByText(/reuse previous clips/));
    expect(onChoose).toHaveBeenLastCalledWith(false);
    fireEvent.click(screen.getByText(/Regenerate \(costs money\)/));
    expect(onChoose).toHaveBeenLastCalledWith(true);
  });
});
