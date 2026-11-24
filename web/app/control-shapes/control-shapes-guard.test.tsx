/** @vitest-environment jsdom */
/**
 * control-shape-restyle T-001: the dev-only guard test for the preview route.
 *
 * Mirrors `app/spike/spike-guard.test.tsx`. `notFound` is mocked so the assertion is direct:
 * "did the guard call notFound", rather than coupling to Next's private 404 digest. NODE_ENV is
 * driven with `vi.stubEnv` because Next types it as read-only.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const mockNotFound = vi.hoisted(() =>
  vi.fn(() => {
    // Mirrors the real next/navigation behaviour: it throws rather than returning.
    throw new Error('NEXT_HTTP_ERROR_FALLBACK;404');
  }),
);

vi.mock('next/navigation', () => ({ notFound: mockNotFound }));

import ControlShapesPage from './page';

beforeEach(() => {
  mockNotFound.mockClear();
  // MantineProvider reads window.matchMedia, which jsdom does not implement.
  window.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});

afterEach(() => {
  vi.unstubAllEnvs();
  cleanup();
});

describe('app/control-shapes/page.tsx is dev-only', () => {
  it('calls notFound() when NODE_ENV is production', () => {
    vi.stubEnv('NODE_ENV', 'production');
    expect(() => ControlShapesPage()).toThrow(/NEXT_HTTP_ERROR_FALLBACK;404/);
    expect(mockNotFound).toHaveBeenCalledTimes(1);
  });

  it('does NOT call notFound() when NODE_ENV is development', () => {
    vi.stubEnv('NODE_ENV', 'development');
    expect(ControlShapesPage()).toBeTruthy();
    expect(mockNotFound).not.toHaveBeenCalled();
  });

  it('does NOT call notFound() when NODE_ENV is test (this runner)', () => {
    vi.stubEnv('NODE_ENV', 'test');
    expect(ControlShapesPage()).toBeTruthy();
    expect(mockNotFound).not.toHaveBeenCalled();
  });

  it('reads NODE_ENV at render time, not at module load', () => {
    vi.stubEnv('NODE_ENV', 'development');
    expect(() => ControlShapesPage()).not.toThrow();
    vi.stubEnv('NODE_ENV', 'production');
    expect(() => ControlShapesPage()).toThrow(/NEXT_HTTP_ERROR_FALLBACK;404/);
    expect(mockNotFound).toHaveBeenCalledTimes(1);
  });

  it('renders three variant sections with real slider and switch roles', () => {
    vi.stubEnv('NODE_ENV', 'development');
    render(
      <MantineProvider>
        <ControlShapesPage />
      </MantineProvider>,
    );

    expect(screen.getByRole('heading', { name: 'A. Soft slant' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'B. Blade' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'C. Capsule cut' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Applied in the app (no variant override)' })).toBeTruthy();
    expect(screen.getAllByRole('slider').length).toBeGreaterThanOrEqual(3);
    expect(screen.getAllByRole('switch').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByRole('button', { name: 'Light' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Dark' })).toBeTruthy();
  });
});
