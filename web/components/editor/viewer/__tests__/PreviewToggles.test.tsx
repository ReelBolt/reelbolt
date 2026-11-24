import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PreviewToggles } from '../PreviewToggles';
import { readStoredQuality, storeQuality } from '@/lib/editing/preview/device-budget';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
global.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

describe('PreviewToggles (R04.5)', () => {
  afterEach(() => cleanup());

  it('names the quality in plain words and reports a pick', () => {
    const onPick = vi.fn();
    render(
      <MantineProvider env="test">
        <PreviewToggles playbackResolution="auto" onPlaybackResolution={onPick} degraded={false} onDegraded={() => undefined} degradedAvailable />
      </MantineProvider>,
    );
    for (const word of ['Auto', 'Full', 'Half', 'Quarter']) expect(screen.getByText(word)).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Full'));
    expect(onPick).toHaveBeenCalledWith('full');
    expect(screen.getByText('Preview quality')).toBeTruthy();
  });

  it('the Simple preview switch reflects and reports the state, and Auto says why it chose it', () => {
    const onDegraded = vi.fn();
    render(
      <MantineProvider env="test">
        <PreviewToggles playbackResolution="auto" onPlaybackResolution={() => undefined} degraded onDegraded={onDegraded} degradedAvailable autoReason="Started on the simple preview because this is a phone." />
      </MantineProvider>,
    );
    const toggle = screen.getByTestId('degraded-toggle') as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    fireEvent.click(toggle);
    expect(onDegraded).toHaveBeenCalledWith(false);
    expect(screen.getByTestId('auto-preview-reason').textContent).toMatch(/because this is a phone/);
    expect(screen.getByText('Simple preview')).toBeTruthy();
  });
});

describe('remembered quality', () => {
  it('defaults to Auto, remembers a pick and ignores junk', () => {
    localStorage.clear();
    expect(readStoredQuality()).toBe('auto');
    storeQuality('quarter');
    expect(readStoredQuality()).toBe('quarter');
    localStorage.setItem('reelbolt:editor:preview-quality', 'ultra');
    expect(readStoredQuality()).toBe('auto');
    localStorage.clear();
  });
});
