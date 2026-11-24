// @vitest-environment jsdom
/**
 * T-007 unit: the read-only cheatsheet and the command palette. Both read the live keymap; the palette
 * shows availability with the reason and runs through the shared command path.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ShortcutsCheatsheet } from '../ShortcutsCheatsheet';
import { CommandPalette } from '../CommandPalette';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
afterEach(cleanup);

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('ShortcutsCheatsheet', () => {
  it('lists every group with the live chords and opens the editor', () => {
    const onEdit = vi.fn();
    wrap(<ShortcutsCheatsheet opened onClose={() => {}} overrides={{}} onEditShortcuts={onEdit} />);
    expect(screen.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeTruthy();
    for (const group of ['Transport', 'Navigate', 'Marks', 'Tools', 'Edit', 'Pages']) expect(screen.getByTestId(`shortcuts-group-${group}`)).toBeTruthy();
    expect(screen.getByTestId('shortcuts-row-pageEdit').textContent).toContain('Shift+3');
    expect(screen.getByTestId('shortcuts-row-commandPalette').textContent).toContain('Ctrl+K');
    fireEvent.click(screen.getByTestId('shortcuts-edit'));
    expect(onEdit).toHaveBeenCalledTimes(1);
  });

  it('search narrows, a remap shows the new chord, and a miss says so', () => {
    wrap(<ShortcutsCheatsheet opened onClose={() => {}} overrides={{ toolBlade: 'C' }} onEditShortcuts={() => {}} />);
    expect(screen.getByTestId('shortcuts-row-toolBlade').textContent).toContain('C');
    fireEvent.change(screen.getByTestId('shortcuts-search'), { target: { value: 'go to colour' } });
    expect(screen.getByTestId('shortcuts-row-pageColour')).toBeTruthy();
    expect(screen.queryByTestId('shortcuts-row-pageMedia')).toBeNull();
    fireEvent.change(screen.getByTestId('shortcuts-search'), { target: { value: 'no such action' } });
    expect(screen.getByTestId('shortcuts-none').textContent).toContain('No shortcut matches');
  });
});

describe('CommandPalette', () => {
  const unavailable = (id: string) => (id === 'undo' ? 'nothingToUndo' : null);

  it('filters by label, shows the chord, and disables an unavailable action with the reason', () => {
    wrap(<CommandPalette opened onClose={() => {}} overrides={{}} unavailable={unavailable as never} onRun={() => {}} />);
    expect(screen.getByRole('dialog', { name: 'Command palette' })).toBeTruthy();
    fireEvent.change(screen.getByTestId('palette-search'), { target: { value: 'colour' } });
    expect(screen.getByTestId('palette-command-pageColour').textContent).toContain('Shift+5');
    fireEvent.change(screen.getByTestId('palette-search'), { target: { value: 'undo' } });
    const undo = screen.getByTestId('palette-command-undo') as HTMLButtonElement;
    expect(undo.disabled).toBe(true);
    expect(undo.textContent).toContain('Nothing to undo.');
  });

  it('Enter runs the first available match through the shared path', () => {
    const onRun = vi.fn();
    const onClose = vi.fn();
    wrap(<CommandPalette opened onClose={onClose} overrides={{}} unavailable={() => null} onRun={onRun} />);
    const search = screen.getByTestId('palette-search');
    fireEvent.change(search, { target: { value: 'colour' } });
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(onRun).toHaveBeenCalledWith('pageColour');
    expect(onClose).toHaveBeenCalled();
  });

  it('has an empty state and a loading state', () => {
    wrap(<CommandPalette opened onClose={() => {}} overrides={{}} unavailable={() => null} onRun={() => {}} />);
    fireEvent.change(screen.getByTestId('palette-search'), { target: { value: 'nothing at all' } });
    expect(screen.getByTestId('palette-empty').textContent).toContain('No command matches');
  });

  it('renders the loading state while the list is prepared', () => {
    wrap(<CommandPalette opened onClose={() => {}} overrides={{}} unavailable={() => null} onRun={() => {}} loading />);
    expect(screen.getByTestId('palette-loading')).toBeTruthy();
  });
});
