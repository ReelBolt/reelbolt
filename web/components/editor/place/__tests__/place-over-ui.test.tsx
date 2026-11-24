// @vitest-environment jsdom
/**
 * T-012: "Place over" is offered next to the four placements (button row and tile menu), and a drop that
 * covers a lower picture raises one toast whose action applies the corner preset.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const shown: Array<{ id: string; message: React.ReactNode }> = [];
const hidden: string[] = [];
vi.mock('@mantine/notifications', () => ({
  notifications: {
    show: (n: { id: string; message: React.ReactNode }) => shown.push(n),
    hide: (id: string) => hidden.push(id),
  },
}));

import { PlaceMenu, PlaceModeButton } from '../PlaceMenu';
import { PLACE_OVER, placeChoiceHintMsg, placeChoiceLabelMsg } from '../placeChoices';
import { showPipToast } from '../pip-toast';
import en from '@/messages/en/editor-misc.json';
import it_ from '@/messages/it/editor-misc.json';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
afterEach(() => {
  cleanup();
  shown.length = 0;
  hidden.length = 0;
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('Place over in the placement control', () => {
  it('has a catalogue label and hint in both locales', () => {
    expect(placeChoiceLabelMsg('placeOver').$msg).toBe('editor-misc.place.choices.placeOver.label');
    expect(placeChoiceHintMsg('placeOver').$msg).toBe('editor-misc.place.choices.placeOver.hint');
    expect(typeof en.place.choices.placeOver.label).toBe('string');
    expect(typeof it_.place.choices.placeOver.hint).toBe('string');
  });

  it('the button row has a Place over button that calls placeOver', () => {
    const onPlace = vi.fn();
    wrap(<PlaceMenu onPlace={onPlace} />);
    const button = screen.getByTestId(PLACE_OVER.testId);
    expect(button.textContent).toBe(en.place.choices.placeOver.label);
    fireEvent.click(button);
    expect(onPlace).toHaveBeenCalledWith('placeOver');
  });

  it('the tile menu lists Place over too', async () => {
    const onPlace = vi.fn();
    wrap(<PlaceMenu onPlace={onPlace} variant="menu" />);
    fireEvent.click(screen.getByTestId('place-menu'));
    const item = await screen.findByTestId('place-placeOver');
    fireEvent.click(item);
    expect(onPlace).toHaveBeenCalledWith('placeOver');
  });
});

describe('Place over on a phone', () => {
  it('the single-mode button (the add-media preview) shows the shared label and calls once', () => {
    const onPlace = vi.fn();
    wrap(<PlaceModeButton mode="placeOver" onPlace={onPlace} testId="preview-place-over" />);
    const button = screen.getByTestId('preview-place-over');
    expect(button.textContent).toBe(en.place.choices.placeOver.label);
    fireEvent.click(button);
    expect(onPlace).toHaveBeenCalledTimes(1);
  });
});

describe('the picture-in-picture toast', () => {
  it('shows one line and its action applies the preset once, then closes the toast', () => {
    const onMake = vi.fn();
    showPipToast({ covers: 'This picture covers the one below.', make: 'Make it picture-in-picture' }, onMake);
    expect(shown).toHaveLength(1);
    wrap(<>{shown[0].message}</>);
    expect(screen.getByTestId('pip-toast').textContent).toContain('This picture covers the one below.');
    fireEvent.click(screen.getByTestId('pip-toast-make'));
    expect(onMake).toHaveBeenCalledTimes(1);
    expect(hidden).toEqual([shown[0].id]);
  });
});
