// @vitest-environment jsdom
/**
 * The contact sheet must show every cursor the catalogue promises, at real size, on all three
 * backgrounds. It is unlinked and `noindex`, but it is the artefact the owner judges the set on: a
 * cursor that the sheet silently omits (or draws in only one place) is one nobody reviews.
 */
import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import catalog from '@/lib/cursors/catalog.json';
import { CursorContactSheet, type CursorCatalog } from './CursorContactSheet';

const typed = catalog as CursorCatalog;

describe('cursor contact sheet', () => {
  it('renders every cursor at real size on the three backgrounds, light and dark', () => {
    const { container } = render(<CursorContactSheet catalog={typed} />);

    const names = typed.cursors.map((c) => c.name);
    for (const name of names) {
      expect(screen.getByRole('heading', { level: 3, name })).toBeTruthy();
    }

    // One 32 px swatch per background plus one 64 px detail image, per cursor.
    const images = [...container.querySelectorAll('img')];
    expect(images).toHaveLength(typed.cursors.length * 4);

    const swatches = images.filter((img) => img.getAttribute('width') === '32');
    expect(swatches).toHaveLength(typed.cursors.length * 3);
    for (const img of swatches) {
      expect(img.getAttribute('height')).toBe('32');
      expect(img.getAttribute('src')).toMatch(/^\/app\/cursors\/[a-z-]+\.png$/);
    }

    // One hotspot ring per swatch and one on the detail image.
    expect(screen.getAllByTestId('hotspot')).toHaveLength(typed.cursors.length * 4);

    expect(container.querySelector('main')?.getAttribute('data-theme')).toBe('light');
  });

  it('shows both page themes, with dark reachable from the toggle', () => {
    const { container } = render(<CursorContactSheet catalog={typed} />);

    expect(screen.getByRole('button', { name: 'Light' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Dark' }));
    expect(container.querySelector('main')?.getAttribute('data-theme')).toBe('dark');
    expect(screen.getByRole('button', { name: 'Dark' }).getAttribute('aria-pressed')).toBe('true');
  });
});
