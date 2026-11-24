/**
 * T-200 — the dev-only guard test for the spike route.  REMOVABLE (see
 * `lib/editing/preview/spike/README.md`).
 *
 * Acceptance criterion 5 requires that `app/spike/page.tsx` "returns `notFound()` when
 * `process.env.NODE_ENV === 'production'`, and a Vitest test asserts that guard fires".
 * This is that test.
 *
 * `next/navigation`'s `notFound` is mocked rather than executed for real, for two reasons:
 *   1. The assertion becomes direct — "did the guard CALL notFound", not "did some Next
 *      internal throw something whose shape happens to match".
 *   2. It pins the contract the page actually depends on (call `notFound()`) without
 *      coupling the test to Next's private `NEXT_HTTP_ERROR_FALLBACK;404` digest.
 *
 * `NODE_ENV` is driven with `vi.stubEnv`, not by assignment: Next augments `process.env`
 * with `NODE_ENV: 'development' | 'production' | 'test'` as a READ-ONLY property, so a plain
 * `process.env.NODE_ENV = 'production'` is a `TS2540` error. `vi.stubEnv` is the supported
 * way to flip it and is undone by `vi.unstubAllEnvs()`.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mockNotFound = vi.hoisted(() =>
  // Takes no parameters: the page calls `notFound()` with none, and the real `notFound()`
  // signature is irrelevant to what this test asserts (that the guard CALLS it).
  vi.fn(() => {
    // Mirrors the real `next/navigation` behaviour: it THROWS rather than returning.
    throw new Error('NEXT_HTTP_ERROR_FALLBACK;404');
  }),
);

vi.mock('next/navigation', () => ({ notFound: mockNotFound }));

import SpikePage from './page';

describe('app/spike/page.tsx is dev-only', () => {
  beforeEach(() => {
    mockNotFound.mockClear();
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('calls notFound() when NODE_ENV is production', () => {
    vi.stubEnv('NODE_ENV', 'production');
    expect(() => SpikePage()).toThrow(/NEXT_HTTP_ERROR_FALLBACK;404/);
    expect(mockNotFound).toHaveBeenCalledTimes(1);
  });

  it('does NOT call notFound() when NODE_ENV is development', () => {
    vi.stubEnv('NODE_ENV', 'development');
    expect(SpikePage()).toBeTruthy();
    expect(mockNotFound).not.toHaveBeenCalled();
  });

  it('does NOT call notFound() when NODE_ENV is test (this runner)', () => {
    vi.stubEnv('NODE_ENV', 'test');
    expect(SpikePage()).toBeTruthy();
    expect(mockNotFound).not.toHaveBeenCalled();
  });

  it('reads NODE_ENV at render time, not at module load', () => {
    // The guard must live inside the component body: a module-level check would be frozen at
    // import time, and it is the production build's OWN render that has to trip it.
    vi.stubEnv('NODE_ENV', 'development');
    expect(() => SpikePage()).not.toThrow();
    vi.stubEnv('NODE_ENV', 'production');
    expect(() => SpikePage()).toThrow(/NEXT_HTTP_ERROR_FALLBACK;404/);
    expect(mockNotFound).toHaveBeenCalledTimes(1);
  });
});
