// @vitest-environment jsdom

/**
 * The alpha notice on the dashboard.
 *
 * The sentence is pinned verbatim because three surfaces render it and the release gate checks that
 * they agree; the version is pinned separately because it is read from `web/lib/version.ts`, the
 * file `infra/scripts/check-version.sh` greps. Dismissal is device-wide now: it lives in
 * `localStorage` keyed by the notice version, so it survives refreshes and sessions and a NEW
 * notice (a new `PRODUCT_VERSION`) re-shows once.
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PRODUCT_VERSION } from '@/lib/version';
import {
  ALPHA_DISMISSED_KEY,
  AlphaBanner,
  __resetAlphaBannerForTests,
  alphaDismissalKey,
} from './AlphaBanner';

// The one pinned sentence, written out whole (em dash U+2014) rather than rebuilt from parts, so a
// change to the notice fails here instead of silently following the source.
const PINNED_NOTICE =
  'ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change.';

// MantineProvider reads the colour scheme through matchMedia, which jsdom does not implement.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => false,
  }),
});

const renderBanner = () =>
  render(
    <MantineProvider>
      <AlphaBanner />
    </MantineProvider>,
  );

// A "reload" forgets every module-level value and starts the component again; only `localStorage`
// survives it. Clearing the module cache is what makes the remount below read from storage.
const simulateReload = () => {
  cleanup();
  __resetAlphaBannerForTests();
};

beforeEach(() => {
  __resetAlphaBannerForTests();
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  __resetAlphaBannerForTests();
  window.localStorage.clear();
});

describe('AlphaBanner', () => {
  it('renders the pinned alpha notice verbatim', () => {
    renderBanner();
    expect(screen.getByRole('status').textContent).toContain(PINNED_NOTICE);
    // The attribute `app/layout.tsx`'s pre-paint CSS hides the bar by — losing it would put the
    // flash straight back for a returning visitor.
    expect(screen.getByTestId('alpha-banner').hasAttribute('data-alpha-banner')).toBe(true);
  });

  it('takes its version from web/lib/version.ts', () => {
    expect(PRODUCT_VERSION).toBe('0.4.0-alpha');
    renderBanner();
    expect(screen.getByTestId('alpha-banner').textContent).toContain(PRODUCT_VERSION);
  });

  it('carries no feedback link — that affordance is T-019', () => {
    renderBanner();
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('scopes the stored key by the notice version, on the shipped base key', () => {
    expect(ALPHA_DISMISSED_KEY).toBe('reelbolt.alpha-banner.dismissed');
    expect(alphaDismissalKey('0.4.0-alpha')).toBe(`${ALPHA_DISMISSED_KEY}:0.4.0-alpha`);
  });

  it('dismisses for the device, not the session', () => {
    renderBanner();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss the alpha notice' }));
    expect(screen.queryByTestId('alpha-banner')).toBeNull();
    expect(window.localStorage.getItem(alphaDismissalKey(PRODUCT_VERSION))).toBe('1');
  });

  it('stays dismissed across a reload: the module cache is not the store', () => {
    renderBanner();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss the alpha notice' }));
    expect(screen.queryByTestId('alpha-banner')).toBeNull();

    // A full reload forgets every module-level value; only `localStorage` survives.
    simulateReload();
    renderBanner();
    expect(screen.queryByTestId('alpha-banner')).toBeNull();
  });

  it('clearing sessionStorage no longer brings the notice back', () => {
    renderBanner();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss the alpha notice' }));

    // The old store was `sessionStorage`; emptying it must change nothing.
    window.sessionStorage.clear();
    simulateReload();
    renderBanner();
    expect(screen.queryByTestId('alpha-banner')).toBeNull();
  });

  it('re-shows the notice once the product version changes', async () => {
    // The visitor dismissed today's notice.
    window.localStorage.setItem(alphaDismissalKey(PRODUCT_VERSION), '1');

    // Reload the app at a NEW version: the dismissal is scoped to the old key, so it no longer
    // covers the notice and the bar is back (exactly once — dismissing again re-hides it).
    vi.resetModules();
    vi.doMock('@/lib/version', () => ({ PRODUCT_VERSION: '0.3.1' }));
    try {
      const fresh = await import('./AlphaBanner');
      fresh.__resetAlphaBannerForTests();
      render(
        <MantineProvider>
          <fresh.AlphaBanner />
        </MantineProvider>,
      );
      expect(screen.getByTestId('alpha-banner')).toBeTruthy();

      fireEvent.click(screen.getByRole('button', { name: 'Dismiss the alpha notice' }));
      expect(screen.queryByTestId('alpha-banner')).toBeNull();
      expect(window.localStorage.getItem(alphaDismissalKey('0.3.1'))).toBe('1');
    } finally {
      vi.doUnmock('@/lib/version');
      vi.resetModules();
    }
  });

  it('keeps the notice up when storage is refused', () => {
    const getItem = vi
      .spyOn(Storage.prototype, 'getItem')
      .mockImplementation(() => {
        throw new Error('blocked');
      });
    const setItem = vi
      .spyOn(Storage.prototype, 'setItem')
      .mockImplementation(() => {
        throw new Error('blocked');
      });
    try {
      renderBanner();
      expect(screen.getByTestId('alpha-banner')).toBeTruthy();
      // Dismissing still works for the page even when it cannot be persisted — and never throws.
      fireEvent.click(screen.getByRole('button', { name: 'Dismiss the alpha notice' }));
      expect(screen.queryByTestId('alpha-banner')).toBeNull();
    } finally {
      getItem.mockRestore();
      setItem.mockRestore();
    }
  });

  it('the pre-paint boot script names the same versioned key as the banner', () => {
    // `app/layout.tsx` is a Server Component and cannot read this module's exports, so the key is
    // spelled out there too. Pin the two spellings together rather than let them drift.
    const layout = readFileSync(
      path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../app/layout.tsx'),
      'utf8',
    );
    expect(layout).toContain(ALPHA_DISMISSED_KEY);
    expect(layout).toContain('PRODUCT_VERSION');
  });
});
