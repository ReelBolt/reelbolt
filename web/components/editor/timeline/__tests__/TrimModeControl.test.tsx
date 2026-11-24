// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { TrimModeControl } from '../TimelineMenus';

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(cleanup);

const wrap = (node: React.ReactNode) => (
  <MantineProvider>
    <LiveChordsProvider chords={{ trimSlip: ['Y'], trimRoll: ['T'] }}>{node}</LiveChordsProvider>
  </MantineProvider>
);

describe('TrimModeControl', () => {
  it('shows all four modes at once, marks the one on, announces it, and switches', () => {
    const onChange = vi.fn();
    render(wrap(<TrimModeControl value="roll" onChange={onChange} />));
    for (const id of ['ripple', 'roll', 'slip', 'slide']) expect(screen.getByTestId(`trim-mode-${id}`)).toBeTruthy();
    expect(screen.getByTestId('trim-mode-roll').getAttribute('aria-checked')).toBe('true');
    expect(screen.getByTestId('trim-mode-slip').getAttribute('aria-checked')).toBe('false');
    expect(screen.getByRole('radiogroup')).toBeTruthy();
    expect(screen.getByTestId('trim-mode-status').textContent).toMatch(/Roll/);
    fireEvent.click(screen.getByTestId('trim-mode-slip'));
    expect(onChange).toHaveBeenCalledWith('slip');
  });

  it('is a real radiogroup: one Tab stop, arrows move and select, bare chords are not eaten', () => {
    const onChange = vi.fn();
    render(wrap(<TrimModeControl value="roll" onChange={onChange} />));
    const stops = ['ripple', 'roll', 'slip', 'slide'].filter((id) => screen.getByTestId(`trim-mode-${id}`).getAttribute('tabindex') === '0');
    expect(stops).toEqual(['roll']);
    const real = Element.prototype.matches;
    const spy = vi.spyOn(Element.prototype, 'matches').mockImplementation(function (this: Element, selector: string) {
      return selector === ':focus-visible' ? true : real.call(this, selector);
    });
    const group = screen.getByRole('radiogroup');
    expect(group.getAttribute('data-editor-shortcuts')).toBe('through');
    fireEvent.keyDown(screen.getByTestId('trim-mode-roll'), { key: 'ArrowRight' });
    expect(onChange).toHaveBeenLastCalledWith('slip');
    fireEvent.keyDown(screen.getByTestId('trim-mode-roll'), { key: 'ArrowLeft' });
    expect(onChange).toHaveBeenLastCalledWith('ripple');
    fireEvent.keyDown(screen.getByTestId('trim-mode-roll'), { key: 'ArrowLeft', shiftKey: true });
    expect(onChange).toHaveBeenCalledTimes(2);
    // Focus left by a mouse click is not focus-visible: the arrows are not taken.
    spy.mockImplementation(function (this: Element, selector: string) {
      return selector === ':focus-visible' ? false : real.call(this, selector);
    });
    fireEvent.keyDown(screen.getByTestId('trim-mode-roll'), { key: 'ArrowRight' });
    expect(onChange).toHaveBeenCalledTimes(2);
    spy.mockRestore();
  });

  it('carries the live chord in its tooltip and keeps the plain hint reachable', () => {
    render(wrap(<TrimModeControl value="ripple" onChange={() => {}} />));
    expect(screen.getByTestId('trim-mode-slip').getAttribute('title')).toMatch(/\(Y\)/);
    expect(screen.getByTestId('trim-mode-slide').textContent).toMatch(/gap can open/i);
  });

  it('folds into the menu on a narrow timeline, still showing the mode', () => {
    render(wrap(<TrimModeControl value="slip" onChange={() => {}} folded />));
    expect(screen.getByTestId('trim-mode').getAttribute('data-mode')).toBe('slip');
    expect(screen.queryByRole('radiogroup')).toBeNull();
    expect(screen.getByTestId('trim-mode-status').textContent).toMatch(/Slip/);
  });
});
