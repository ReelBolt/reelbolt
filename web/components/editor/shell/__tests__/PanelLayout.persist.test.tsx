// @vitest-environment jsdom
/**
 * TL-140: a resized panel survives a reload. The layout used to write its default sizes to
 * localStorage on mount, before it had read the stored ones, so every reload reset the panels.
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PanelLayout } from '../PanelLayout';

const KEY = 'test:panels';
afterEach(cleanup);
beforeEach(() => {
  window.localStorage.clear();
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});

const mount = () =>
  render(
    <MantineProvider>
      <PanelLayout left={<div>left</div>} right={<div>right</div>} viewer={<div>viewer</div>} timeline={<div>timeline</div>} storageKey={KEY} initialLeftWidth={280} />
    </MantineProvider>,
  );

const flush = () => act(async () => { await new Promise((r) => setTimeout(r, 20)); });

describe('PanelLayout persistence', () => {
  it('applies the stored sizes after mount and does not overwrite them with the defaults', async () => {
    window.localStorage.setItem(KEY, JSON.stringify({ left: 410, right: 333, timeline: 250 }));
    mount();
    await flush();
    expect(screen.getByTestId('panel-left').style.flex).toBe('0 0 410px');
    expect(JSON.parse(window.localStorage.getItem(KEY)!)).toEqual({ left: 410, right: 333, timeline: 250 });
  });

  it('keeps a size the user chose with the keyboard for the next mount', async () => {
    mount();
    await flush();
    const sep = screen.getByRole('separator', { name: 'Resize left panel' });
    await act(async () => {
      sep.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    });
    expect(JSON.parse(window.localStorage.getItem(KEY)!).left).toBe(296);
    cleanup();
    mount();
    await flush();
    expect(screen.getByTestId('panel-left').style.flex).toBe('0 0 296px');
  });
});
