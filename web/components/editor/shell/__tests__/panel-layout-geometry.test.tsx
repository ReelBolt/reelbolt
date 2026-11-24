// @vitest-environment jsdom
/**
 * T-003: the frame reads the page's geometry entry. The tablet drawer button names the zone it opens,
 * Media's timeline is hidden until asked for, Cut can carry an overview band, and Deliver is a
 * two-column page with no centre and no timeline.
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PanelLayout, type PanelLayoutProps } from '../PanelLayout';

afterEach(cleanup);
beforeEach(() => {
  window.localStorage.clear();
  // Tablet: narrow and touch, so the drawer button renders with the zone's name.
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: true, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});

const flush = () => act(async () => { await new Promise((r) => setTimeout(r, 20)); });

function mount(props: Partial<PanelLayoutProps>) {
  return render(
    <MantineProvider>
      <PanelLayout left={<div>left</div>} viewer={<div>viewer</div>} right={<div>right</div>} timeline={<div>timeline</div>} {...props} />
    </MantineProvider>,
  );
}

describe('the tablet drawer is named after the zone it opens', () => {
  it.each([
    ['media', 'Viewer'],
    ['edit', 'Inspector'],
    ['colour', 'Grade tools'],
    ['compose', 'Properties'],
  ] as const)('%s opens %s', async (page, label) => {
    mount({ page });
    await flush();
    expect(screen.getByTestId('inspector-toggle').textContent).toBe(label);
  });
});

describe('the page’s geometry entry decides the frame', () => {
  it('keeps Media’s timeline hidden until the page asks for it', async () => {
    mount({ page: 'media' });
    await flush();
    expect(screen.queryByTestId('panel-timeline')).toBeNull();
    cleanup();
    mount({ page: 'media', timelineVisible: true });
    await flush();
    expect(screen.getByTestId('panel-timeline')).not.toBeNull();
  });

  it('renders Cut’s overview band between the viewer row and the timeline', async () => {
    mount({ page: 'cut', overview: <div>overview-strip</div> });
    await flush();
    expect(screen.getByTestId('panel-overview').textContent).toBe('overview-strip');
    expect(screen.getByTestId('panel-timeline')).not.toBeNull();
  });

  it('renders Deliver as two columns with no centre and no timeline', async () => {
    const { container } = render(
      <MantineProvider>
        <PanelLayout page="deliver" left={<div>settings</div>} right={<div>result</div>} />
      </MantineProvider>,
    );
    await flush();
    expect(screen.queryByTestId('panel-viewer')).toBeNull();
    expect(screen.queryByTestId('panel-timeline')).toBeNull();
    expect(screen.getByTestId('panel-right').textContent).toBe('result');
    expect(container.textContent).toContain('settings');
  });
});
