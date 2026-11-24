// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TrimModeMenu } from '../TimelineMenus';

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(cleanup);

describe('TrimModeMenu', () => {
  it('offers Slide beside Ripple, Roll and Slip with a plain explanation, and switches to it', async () => {
    const onChange = vi.fn();
    render(
      <MantineProvider>
        <TrimModeMenu value="ripple" onChange={onChange} />
      </MantineProvider>,
    );
    await act(async () => fireEvent.click(screen.getByTestId('trim-mode')));
    for (const id of ['ripple', 'roll', 'slip', 'slide']) expect(await screen.findByTestId(`trim-mode-${id}`)).toBeTruthy();
    expect(screen.getByTestId('trim-mode-slide').textContent).toMatch(/neighbour/i);
    await act(async () => fireEvent.click(screen.getByTestId('trim-mode-slide')));
    expect(onChange).toHaveBeenCalledWith('slide');
  });
});
