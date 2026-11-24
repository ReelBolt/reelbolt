// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { IconCut, IconPhotoVideo } from '@tabler/icons-react';
import { PanelTabs, fitTabs } from '../PanelTabs';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }));
afterEach(cleanup);

describe('fitTabs', () => {
  const widths = [40, 50, 60, 70];
  const icons = [true, true, true, true];

  it('keeps full labels when they fit', () => {
    // 4 x 44 + 220 = 396
    expect(fitTabs(widths, icons, 0, 400)).toEqual({ fit: 'full', overflow: false });
  });

  it('keeps only the active label when the rest shrink to icons and that fits', () => {
    // 4 x 26 + 18 + 40 = 162
    expect(fitTabs(widths, icons, 0, 190)).toEqual({ fit: 'active', overflow: false });
  });

  it('drops every label when only the icons fit', () => {
    expect(fitTabs(widths, icons, 0, 110)).toEqual({ fit: 'icons', overflow: false });
  });

  it('adds the More menu when even the icons do not fit', () => {
    expect(fitTabs(widths, icons, 0, 100)).toEqual({ fit: 'icons', overflow: true });
  });

  it('a tab without an icon keeps its label in icon mode', () => {
    expect(fitTabs([40, 50], [true, false], 0, 90).overflow).toBe(true);
  });
});

describe('PanelTabs', () => {
  it('every tab is a named tab and the active one shows its panel; More is absent when everything fits', () => {
    const onChange = vi.fn();
    render(
      <MantineProvider>
        <PanelTabs
          label="Timeline panels"
          value="a"
          onChange={onChange}
          tabs={[
            { value: 'a', label: 'Media', icon: IconPhotoVideo, content: <p>media body</p>, testId: 'tab-a' },
            { value: 'b', label: 'Cuts', icon: IconCut, content: <p>cuts body</p>, testId: 'tab-b' },
          ]}
        />
      </MantineProvider>,
    );
    expect(screen.getByRole('tablist', { name: 'Timeline panels' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Media' }).getAttribute('aria-selected')).toBe('true');
    fireEvent.click(screen.getByRole('tab', { name: 'Cuts' }));
    expect(onChange).toHaveBeenCalledWith('b');
    expect(screen.getByText('media body')).toBeTruthy();
    expect(screen.queryByTestId('panel-tabs-more')).toBeNull();
  });
});
