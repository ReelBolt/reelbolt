// @vitest-environment jsdom
/**
 * A colour comes from a stored document, so its value is checked on the client before it reaches a
 * style attribute: only #rrggbb or #rrggbbaa, anything else renders a neutral swatch.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ColorSwatch } from './ColorSwatch';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(cleanup);

function swatchOf(hex: string): HTMLElement {
  const { container } = render(
    <MantineProvider>
      <ColorSwatch name="primary" hex={hex} />
    </MantineProvider>,
  );
  return container.querySelector('span[aria-hidden]') as HTMLElement;
}

describe('ColorSwatch hex validation', () => {
  it.each(['#6d28d9', '#6D28D9', '#6d28d9cc'])('paints %s', (hex) => {
    const swatch = swatchOf(hex);
    expect(swatch.style.background.length).toBeGreaterThan(0);
    expect(swatch.getAttribute('data-neutral')).toBeNull();
  });

  it.each([
    'url(https://evil.example/x.png)',
    'red',
    '#fff',
    '#12345',
    '#gggggg',
    '#6d28d9; position: fixed',
    'var(--x)',
    '',
  ])('renders a neutral swatch for %j', (hex) => {
    const swatch = swatchOf(hex);
    expect(swatch.getAttribute('data-neutral')).toBe('true');
    expect(swatch.style.background).not.toContain('url');
    expect(swatch.style.background).not.toContain('evil');
  });

  it('still shows the stored text beside a neutral swatch so nothing is hidden from the reader', () => {
    swatchOf('url(https://evil.example/x.png)');
    expect(screen.getByText('url(https://evil.example/x.png)')).toBeTruthy();
  });

  it('uses the gray light badge (with a dot) for every confidence band, not yellow or orange text', () => {
    for (const conf of [0.9, 0.7, 0.3]) {
      const { unmount } = render(
        <MantineProvider>
          <ColorSwatch name="n" hex="#000000" conf={conf} />
        </MantineProvider>,
      );
      const badge = document.querySelector('.mantine-Badge-root') as HTMLElement;
      expect(badge.getAttribute('style') ?? '').toContain('gray');
      expect(badge.getAttribute('style') ?? '').not.toMatch(/yellow|orange/);
      unmount();
    }
  });
});
