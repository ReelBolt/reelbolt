// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TypeSpecimen } from '@/components/design-system/TypeSpecimen';
import { TYPE_SPECIMEN_GENERIC, TYPE_SPECIMEN_UNAVAILABLE } from '@/components/design-system/copy';
import { en } from '@/components/design-system/__tests__/en';

const load = vi.fn();
const add = vi.fn();

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  class FakeFontFace {
    constructor(public family: string, public source: string, public descriptors: Record<string, string>) {
      load(family, source, descriptors);
    }
    async load() { return this; }
  }
  vi.stubGlobal('FontFace', FakeFontFace);
  Object.defineProperty(document, 'fonts', { configurable: true, value: { add } });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderSpecimen(family: string, weight?: number | null) {
  return render(
    <MantineProvider>
      <TypeSpecimen family={family} weight={weight} />
    </MantineProvider>,
  );
}

describe('TypeSpecimen', () => {
  it('renders an available family in its namespaced face, at the token weight, and loads it lazily', async () => {
    renderSpecimen('Poppins, sans-serif', 600);
    const sample = screen.getByTestId('type-specimen-sample');
    expect(sample.style.fontFamily).toContain('ReelBolt Brand Font 7');
    expect(sample.style.fontWeight).toBe('600');
    expect(screen.queryByTestId('type-specimen-note')).toBeNull();
    expect(load).toHaveBeenCalledTimes(3);
    expect(load).toHaveBeenCalledWith('ReelBolt Brand Font 7', expect.stringContaining('/app/fonts/brand/Poppins-SemiBold.woff2'), expect.objectContaining({ weight: '600' }));
    await vi.waitFor(() => expect(add).toHaveBeenCalledTimes(3));
  });

  it('says so for an unavailable family and loads nothing', () => {
    renderSpecimen('Oswald', 700);
    expect(screen.getByTestId('type-specimen-note').textContent).toBe(en(TYPE_SPECIMEN_UNAVAILABLE));
    expect(screen.getByTestId('type-specimen-sample').getAttribute('data-font-status')).toBe('unavailable');
    expect(load).not.toHaveBeenCalled();
  });

  it('has its own note for a generic keyword', () => {
    renderSpecimen('sans-serif');
    expect(screen.getByTestId('type-specimen-note').textContent).toBe(en(TYPE_SPECIMEN_GENERIC));
    expect(screen.getByTestId('type-specimen-sample').style.fontFamily).toBe('sans-serif');
    expect(load).not.toHaveBeenCalled();
  });

  it('omits the weight when the token has none', () => {
    renderSpecimen('Inter');
    expect(screen.getByTestId('type-specimen-sample').style.fontWeight).toBe('');
  });

  it('compact mode drops the family label but keeps the sample and the unavailable note', () => {
    render(
      <MantineProvider>
        <TypeSpecimen compact family="Comic Neue" />
      </MantineProvider>,
    );
    expect(screen.queryByText('Comic Neue')).toBeNull();
    expect(screen.getByTestId('type-specimen-sample').getAttribute('data-font-status')).toBe('unavailable');
    expect(screen.getByText(en(TYPE_SPECIMEN_UNAVAILABLE))).toBeTruthy();
  });
});
