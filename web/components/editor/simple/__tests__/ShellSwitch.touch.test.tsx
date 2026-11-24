// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ShellSwitch } from '../ShellSwitch';

global.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

const mediaWith = (coarse: boolean) => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: coarse && query.includes('pointer: coarse'), media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false }) as MediaQueryList;
};

describe('Simple | Full switch on a touch screen', () => {
  afterEach(cleanup);

  const show = () =>
    render(
      <MantineProvider env="test">
        <ShellSwitch shell="simple" onChange={() => undefined} />
      </MantineProvider>,
    );

  it('is at least 44 px tall for a finger', () => {
    mediaWith(true);
    show();
    expect(screen.getByTestId('shell-switch').style.minHeight).toBe('44px');
  });

  it('keeps the compact size for a mouse', () => {
    mediaWith(false);
    show();
    expect(screen.getByTestId('shell-switch').style.minHeight).toBe('');
  });
});
