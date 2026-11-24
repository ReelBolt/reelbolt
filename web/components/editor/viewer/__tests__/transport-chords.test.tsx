// @vitest-environment jsdom
/**
 * T-007: the transport's shortcut-bearing controls read the live keymap, so a remap updates both the
 * visible tooltip (built by the same helper) and `aria-keyshortcuts`. The previous release baked
 * "(Shift+↑)" and "(Space)" into the copy, so a remapped action showed a stale chord.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TransportBar } from '../TransportBar';

afterEach(cleanup);

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const props = {
  playheadFrame: 0,
  rate: { num: 30, den: 1 },
  endFrame: 300,
  speed: 0,
  loop: false,
  inFrame: 0,
  outFrame: 100,
  onSeek: vi.fn(),
  onSpeed: vi.fn(),
  onStep: vi.fn(),
  onPrevEdit: vi.fn(),
  onNextEdit: vi.fn(),
  onPrevMarker: vi.fn(),
  onNextMarker: vi.fn(),
  onMarkIn: vi.fn(),
  onMarkOut: vi.fn(),
  onToggleLoop: vi.fn(),
  onPlayInToOut: vi.fn(),
};

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

describe('transport chords read the live keymap', () => {
  it('states the chord a remap gives the previous/next marker, play-in-to-out, mark in and play', () => {
    wrap(
      <TransportBar
        {...props}
        chords={{ previousMarker: 'Shift+J', nextMarker: 'Shift+K', playInToOut: 'Alt+P', markIn: 'Alt+I', playPause: 'Mod+Space' }}
      />,
    );
    expect(document.querySelector('[aria-label="Previous marker"]')?.getAttribute('aria-keyshortcuts')).toBe('Shift+J');
    expect(document.querySelector('[aria-label="Next marker"]')?.getAttribute('aria-keyshortcuts')).toBe('Shift+K');
    expect(document.querySelector('[aria-label="Play in to out"]')?.getAttribute('aria-keyshortcuts')).toBe('Alt+P');
    expect(document.querySelector('[aria-label="Mark in"]')?.getAttribute('aria-keyshortcuts')).toBe('Alt+I');
    expect(document.querySelector('[aria-label="Play"]')?.getAttribute('aria-keyshortcuts')).toBe('Control+Space');
  });

  it('omits the attribute when the caller has no live chord for an action', () => {
    wrap(<TransportBar {...props} />);
    expect(document.querySelector('[aria-label="Previous marker"]')?.hasAttribute('aria-keyshortcuts')).toBe(false);
    expect(document.querySelector('[aria-label="Play in to out"]')?.hasAttribute('aria-keyshortcuts')).toBe(false);
  });
});
