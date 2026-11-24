// @vitest-environment jsdom
/**
 * The sun/moon toggle. `web/app/layout.tsx` starts the app in `defaultColorScheme="auto"`, so a
 * user who never chose a scheme has the STORED value `'auto'` — never the literal `'dark'` —
 * even while the page is actually dark. The old `colorScheme === 'dark'` check therefore rendered
 * the moon on a dark page; the icon must follow the COMPUTED scheme (the one the page is in),
 * and a click must write an EXPLICIT scheme so the first click out of `auto` does what the user
 * sees (dark page → click → light).
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { act } from 'react';
import { hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { MantineProvider } from '@mantine/core';
import { ThemeToggle } from './ThemeToggle';

// Mantine's color-scheme hooks read `matchMedia('(prefers-color-scheme: dark)')`; a single
// stub whose `matches` is a mutable field lets each test pick the OS scheme.
const media = { matches: false, media: '', onchange: null };
vi.stubGlobal('matchMedia', vi.fn((q: string) => ({
  get matches() { return media.matches; },
  media: q,
  onchange: null,
  addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
})));

const renderToggle = (defaultColorScheme: 'auto' | 'light' | 'dark' = 'auto') =>
  render(
    <MantineProvider defaultColorScheme={defaultColorScheme}>
      <ThemeToggle />
    </MantineProvider>,
  );

afterEach(() => {
  cleanup();
  window.localStorage.removeItem('mantine-color-scheme-value');
});

describe('ThemeToggle (computed scheme)', () => {
  it('renders the sun while the page is dark, even when the stored scheme is still "auto"', () => {
    media.matches = true;
    renderToggle('auto');
    expect(screen.getByRole('button', { name: 'Switch to light mode' })).toBeTruthy();
  });

  it('renders the moon while the page is light, even when the stored scheme is still "auto"', () => {
    media.matches = false;
    renderToggle('auto');
    expect(screen.getByRole('button', { name: 'Switch to dark mode' })).toBeTruthy();
  });

  it('follows an explicitly stored scheme (dark) regardless of the OS', () => {
    media.matches = false; // OS would be light — the explicit choice wins.
    renderToggle('dark');
    expect(screen.getByRole('button', { name: 'Switch to light mode' })).toBeTruthy();
  });

  it('clicking on an auto dark page sets an EXPLICIT light scheme: the icon flips to the moon', () => {
    media.matches = true;
    renderToggle('auto');
    fireEvent.click(screen.getByRole('button', { name: 'Switch to light mode' }));
    expect(screen.getByRole('button', { name: 'Switch to dark mode' })).toBeTruthy();
  });

  it('a click out of auto writes an explicit scheme that a fresh render reads back (no OS round-trip)', () => {
    window.localStorage.removeItem('mantine-color-scheme-value');
    media.matches = true;
    const first = renderToggle('auto');
    fireEvent.click(screen.getByRole('button', { name: 'Switch to light mode' }));
    first.unmount();
    expect(window.localStorage.getItem('mantine-color-scheme-value')).toBe('light');
    // Fresh mount, OS is now light too: an explicit stored light must still read light.
    media.matches = false;
    renderToggle('auto');
    expect(screen.getByRole('button', { name: 'Switch to dark mode' })).toBeTruthy();
  });
});

describe('ThemeToggle (hydration)', () => {
  // The mobile-nav-stuck root cause: Mantine seeds its scheme from localStorage in a state
  // initializer, so a stored "dark" made the client's FIRST render differ from the server's
  // (which only knows "auto"): React error #418 and an orphaned AppShell <style>. The first client
  // render must be identical to the server's whatever is stored.
  it.each(['dark', 'light', 'auto'])('hydrates server HTML without a mismatch when "%s" is stored', async (stored) => {
    media.matches = false;
    window.localStorage.removeItem('mantine-color-scheme-value');
    const tree = (
      <MantineProvider defaultColorScheme="auto">
        <ThemeToggle />
      </MantineProvider>
    );
    const html = renderToString(tree); // the server: nothing stored
    window.localStorage.setItem('mantine-color-scheme-value', stored);
    const container = document.createElement('div');
    container.innerHTML = html;
    document.body.appendChild(container);
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    const errors: unknown[] = [];
    const spy = vi.spyOn(console, 'error').mockImplementation((...a) => { errors.push(a); });
    let root: ReturnType<typeof hydrateRoot> | undefined;
    await act(async () => {
      root = hydrateRoot(container, tree, { onRecoverableError: (e) => errors.push(e) });
    });
    spy.mockRestore();
    expect(errors).toEqual([]);
    // After mount the label follows the real scheme.
    const want = stored === 'dark' ? 'Switch to light mode' : 'Switch to dark mode';
    expect(container.querySelector('button')?.getAttribute('aria-label')).toBe(want);
    act(() => root?.unmount());
    container.remove();
  });
});
