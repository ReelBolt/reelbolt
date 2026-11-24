// @vitest-environment jsdom
/** The "Size and quality" fold: platform chips, rate control, bitrate and the request it builds; nothing disabled on a phone. */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import { RenderSettings } from '../RenderSettings';

vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'phone',
}));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

afterEach(() => cleanup());

function setup(sizeLine: string | null = null) {
  const seen: RenderEditTimelineRequest[] = [];
  render(
    <MantineProvider>
      <RenderSettings inOut={{ inFrame: null, outFrame: null }} revision={3} readOnly={false} busy={false} onRender={() => {}} onRequestChange={(r) => seen.push(r)} sizeLine={sizeLine} />
    </MantineProvider>,
  );
  fireEvent.click(screen.getByTestId('render-size-toggle'));
  return seen;
}

describe('Size and quality fold', () => {
  it('shows the chips, rate control, audio and size line with no disabled control', () => {
    setup('About 12 MB');
    for (const id of ['youtube', 'reels', 'tiktok', 'small']) expect(screen.getByTestId(`render-platform-${id}`)).toBeTruthy();
    expect(screen.getByTestId('render-rate-control')).toBeTruthy();
    expect(screen.getByTestId('render-audio-kbps')).toBeTruthy();
    expect(screen.getByTestId('render-size-line').textContent).toBe('About 12 MB');
    const fold = screen.getByTestId('render-size');
    for (const el of Array.from(fold.querySelectorAll('button, input, select'))) expect((el as HTMLButtonElement).disabled).toBe(false);
  });

  it('a platform chip fills the request with the suggested bitrate', () => {
    const seen = setup();
    expect(seen[seen.length - 1].encode).toBeNull();
    fireEvent.click(screen.getByTestId('render-platform-youtube'));
    const last = seen[seen.length - 1];
    expect(last.preset).toBe('Hd1080p16x9');
    expect(last.encode).toMatchObject({ rateControl: 'Average', videoBitrateKbps: 8000, audioBitrateKbps: 320 });
    expect(screen.getByTestId('render-video-mbps')).toBeTruthy();
  });
});
