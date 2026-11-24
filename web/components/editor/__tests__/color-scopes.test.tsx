// @vitest-environment jsdom
/**
 * T-006 §8.2: the scopes are docked on the Colour page and can be collapsed. The statistics are computed
 * whether or not the panel is expanded, so "Match to reference" works from any tab.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ScopesPanel } from '../color/ScopesPanel';
import { resetColourPagePrefsForTests, SCOPES_HEIGHT_MIN, SCOPES_PREFS_KEY } from '@/lib/editing/color/colour-page-prefs';

const calls = vi.hoisted(() => ({ sample: 0, compute: 0 }));

vi.mock('@/lib/editing/color/scopes', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/color/scopes')>();
  const data = {
    waveform: new Uint32Array(4),
    parade: [new Uint32Array(4), new Uint32Array(4), new Uint32Array(4)] as [Uint32Array, Uint32Array, Uint32Array],
    vectorscope: new Uint32Array(4),
    histogram: { y: new Uint32Array(4), r: new Uint32Array(4), g: new Uint32Array(4), b: new Uint32Array(4) },
    columns: 2,
    levels: 2,
    vectorSize: 2,
    stats: { meanR: 0.2, meanG: 0.2, meanB: 0.2, meanLuma: 0.2, lumaSpread: 0.1 },
    clipped: { black: 0, white: 0 },
  };
  return {
    ...actual,
    sampleCanvas: () => {
      calls.sample += 1;
      return { data: new Uint8ClampedArray(4), width: 1, height: 1 };
    },
    computeScopes: () => {
      calls.compute += 1;
      return data;
    },
  };
});

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
beforeEach(() => {
  localStorage.clear();
  resetColourPagePrefsForTests();
  calls.sample = 0;
  calls.compute = 0;
});
afterEach(cleanup);
afterEach(() => vi.restoreAllMocks());

/** A stand-in 2D context that records which canvas (which scope) was drawn. */
function recordDraws(): string[] {
  const drawn: string[] = [];
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement) {
    drawn.push(this.getAttribute('data-scope') ?? '');
    return { fillRect: () => undefined, beginPath: () => undefined, arc: () => undefined, stroke: () => undefined, fillStyle: '', strokeStyle: '', globalAlpha: 1 } as unknown as CanvasRenderingContext2D;
  } as never);
  return drawn;
}

const getCanvas = () => ({ width: 4, height: 4 }) as HTMLCanvasElement;

describe('ScopesPanel', () => {
  it('computes statistics on mount even while collapsed, and expands on the collapse control', () => {
    const onStats = vi.fn();
    render(
      <MantineProvider>
        <ScopesPanel getCanvas={getCanvas} onStats={onStats} defaultOpen={false} />
      </MantineProvider>,
    );
    expect(onStats).toHaveBeenCalled();
    expect(screen.getByTestId('scopes-panel').getAttribute('data-open')).toBe('false');
    expect(screen.queryByTestId('scopes-canvas')).toBeNull();
    fireEvent.click(screen.getByTestId('scopes-toggle'));
    expect(screen.getByTestId('scopes-panel').getAttribute('data-open')).toBe('true');
    expect(screen.getByTestId('scopes-canvas')).toBeTruthy();
  });

  it('draws and reports on mount when expanded by default', () => {
    const onStats = vi.fn();
    render(
      <MantineProvider>
        <ScopesPanel getCanvas={getCanvas} onStats={onStats} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('scopes-canvas')).toBeTruthy();
    expect(onStats).toHaveBeenCalled();
  });

  it('offers one scope, two side by side and all four, and remembers the layout on this device', () => {
    const view = render(
      <MantineProvider>
        <ScopesPanel getCanvas={getCanvas} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('scopes-panel').getAttribute('data-layout')).toBe('single');
    expect(screen.getByTestId('scopes-layout-single').getAttribute('aria-checked')).toBe('true');
    expect(screen.getByTestId('scopes-kind')).toBeTruthy();

    act(() => fireEvent.click(screen.getByTestId('scopes-layout-four')));
    expect(screen.getByTestId('scopes-panel').getAttribute('data-layout')).toBe('four');
    expect([0, 1, 2, 3].map((slot) => screen.getByTestId(`scopes-canvas-${slot}`).getAttribute('data-scope'))).toEqual(['waveform', 'parade', 'vectorscope', 'histogram']);
    expect(screen.queryByTestId('scopes-kind')).toBeNull();
    expect(JSON.parse(localStorage.getItem(SCOPES_PREFS_KEY) ?? '{}').layout).toBe('four');

    act(() => fireEvent.click(screen.getByTestId('scopes-layout-two')));
    expect([0, 1].map((slot) => screen.getByTestId(`scopes-canvas-${slot}`).getAttribute('data-scope'))).toEqual(['waveform', 'vectorscope']);
    act(() => fireEvent.change(screen.getByTestId('scopes-kind-right'), { target: { value: 'histogram' } }));
    expect(screen.getByTestId('scopes-canvas-1').getAttribute('data-scope')).toBe('histogram');

    // Another visit on the same device opens on the remembered layout.
    view.unmount();
    resetColourPagePrefsForTests();
    render(
      <MantineProvider>
        <ScopesPanel getCanvas={getCanvas} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('scopes-panel').getAttribute('data-layout')).toBe('two');
    expect(screen.getByTestId('scopes-canvas-1').getAttribute('data-scope')).toBe('histogram');
  });

  it('draws all four scopes from ONE sample and ONE binning pass per tick', () => {
    const drawn = recordDraws();
    localStorage.setItem(SCOPES_PREFS_KEY, JSON.stringify({ layout: 'four' }));
    vi.useFakeTimers();
    try {
      render(
        <MantineProvider>
          <ScopesPanel getCanvas={getCanvas} intervalMs={100} />
        </MantineProvider>,
      );
      const samples = calls.sample;
      const computes = calls.compute;
      const draws = drawn.length;
      act(() => {
        vi.advanceTimersByTime(100);
      });
      expect(calls.sample - samples).toBe(1);
      expect(calls.compute - computes).toBe(1);
      expect(drawn.slice(draws)).toEqual(['waveform', 'parade', 'vectorscope', 'histogram']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps computing statistics in every layout while collapsed, without drawing', () => {
    const drawn = recordDraws();
    localStorage.setItem(SCOPES_PREFS_KEY, JSON.stringify({ layout: 'four' }));
    const onStats = vi.fn();
    render(
      <MantineProvider>
        <ScopesPanel getCanvas={getCanvas} onStats={onStats} defaultOpen={false} />
      </MantineProvider>,
    );
    expect(onStats).toHaveBeenCalled();
    expect(drawn).toEqual([]);
    expect(screen.queryByTestId('scopes-layout')).toBeNull();
  });

  it('resizes the docked height from the keyboard and remembers it', () => {
    render(
      <MantineProvider>
        <ScopesPanel getCanvas={getCanvas} />
      </MantineProvider>,
    );
    const handle = screen.getByTestId('scopes-resize');
    expect(handle.getAttribute('role')).toBe('separator');
    expect(handle.getAttribute('aria-valuenow')).toBe('112');
    act(() => fireEvent.keyDown(handle, { key: 'ArrowUp' }));
    expect(screen.getByTestId('scopes-resize').getAttribute('aria-valuenow')).toBe('128');
    expect((screen.getByTestId('scopes-canvas') as HTMLCanvasElement).style.height).toBe('128px');
    expect(JSON.parse(localStorage.getItem(SCOPES_PREFS_KEY) ?? '{}').height).toBe(128);
    act(() => fireEvent.keyDown(handle, { key: 'Home' }));
    expect(screen.getByTestId('scopes-resize').getAttribute('aria-valuenow')).toBe(String(SCOPES_HEIGHT_MIN));
    // Collapsing still works after a resize.
    fireEvent.click(screen.getByTestId('scopes-toggle'));
    expect(screen.queryByTestId('scopes-canvas')).toBeNull();
    expect(screen.queryByTestId('scopes-resize')).toBeNull();
  });

  it('on a touch screen the height is two buttons, not an edge to drag', () => {
    const fine = global.matchMedia;
    global.matchMedia = (query: string): MediaQueryList => ({ ...fine(query), matches: query.includes('pointer: coarse') }) as MediaQueryList;
    try {
      render(
        <MantineProvider>
          <ScopesPanel getCanvas={getCanvas} />
        </MantineProvider>,
      );
      expect(screen.queryByTestId('scopes-resize')).toBeNull();
      act(() => fireEvent.click(screen.getByTestId('scopes-taller')));
      expect((screen.getByTestId('scopes-canvas') as HTMLCanvasElement).style.height).toBe('144px');
      act(() => fireEvent.click(screen.getByTestId('scopes-shorter')));
      expect(JSON.parse(localStorage.getItem(SCOPES_PREFS_KEY) ?? '{}').height).toBe(112);
    } finally {
      global.matchMedia = fine;
    }
  });
});
