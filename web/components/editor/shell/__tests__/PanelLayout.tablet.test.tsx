// @vitest-environment jsdom
/** Design review: at <= 1100px the inspector is a drawer behind a button; nothing disappears. */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PanelLayout } from '../PanelLayout';

afterEach(cleanup);
beforeEach(() => {
  window.localStorage.clear();
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: true, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});

const mount = () =>
  render(
    <MantineProvider>
      <PanelLayout left={<div>left</div>} right={<div>right-body</div>} viewer={<div>viewer</div>} timeline={<div>timeline</div>} initialLeftWidth={200} />
    </MantineProvider>,
  );
const flush = () => act(async () => { await new Promise((r) => setTimeout(r, 20)); });

describe('PanelLayout on a tablet', () => {
  it('keeps the inspector one tap away and closes it on Escape', async () => {
    mount();
    await flush();
    expect(screen.queryByText('right-body')).toBeNull();
    const toggle = screen.getByTestId('inspector-toggle');
    fireEvent.click(toggle);
    expect(screen.getByText('right-body')).toBeTruthy();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    fireEvent.keyDown(screen.getByTestId('panel-right'), { key: 'Escape' });
    expect(screen.queryByText('right-body')).toBeNull();
  });

  it('widens the left column and caps the timeline at 44% of the height so the viewer keeps its picture', async () => {
    mount();
    await flush();
    expect(screen.getByTestId('panel-left').style.flex).toBe('0 0 300px');
    expect(screen.getByTestId('panel-timeline').style.flex).toContain('min(320px, 44%)');
  });

  it('keeps the viewer clear of the floating Inspector button', async () => {
    mount();
    await flush();
    expect(screen.getByTestId('panel-viewer').style.paddingRight).toBe('96px');
  });
});
