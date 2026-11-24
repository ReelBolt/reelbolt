// @vitest-environment jsdom
/**
 * T-010: one placement control. The four modes and their labels/hints have exactly one source
 * (`placeChoices.ts`), every host renders that source, and each host still calls the mode it offers.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { MantineProvider } from '@mantine/core';
import { PlaceMenu, PlaceModeButton } from '../PlaceMenu';
import { PLACE_MODES, placeChoiceHintMsg, placeChoiceLabelMsg } from '../placeChoices';
import en from '@/messages/en/editor-misc.json';
import it_ from '@/messages/it/editor-misc.json';

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
const choices = en.place.choices;
const choicesIt = it_.place.choices;

describe('one placement source', () => {
  it('has four modes with one catalogue label and hint each, in both locales', () => {
    expect(PLACE_MODES.map((m) => m.mode)).toEqual(['insert', 'overwrite', 'append', 'placeOnTop']);
    for (const { mode } of PLACE_MODES) {
      expect(placeChoiceLabelMsg(mode).$msg).toBe(`editor-misc.place.choices.${mode}.label`);
      expect(placeChoiceHintMsg(mode).$msg).toBe(`editor-misc.place.choices.${mode}.hint`);
      expect(typeof choices[mode].label).toBe('string');
      expect(typeof choices[mode].hint).toBe('string');
      expect(typeof choicesIt[mode].label).toBe('string');
      expect(typeof choicesIt[mode].hint).toBe('string');
    }
  });

  it('the visible button variant renders the catalogue labels and calls the mode it names', () => {
    const onPlace = vi.fn();
    wrap(<PlaceMenu onPlace={onPlace} />);
    for (const { mode } of PLACE_MODES) {
      const testId = PLACE_MODES.find((m) => m.mode === mode)!.testId;
      const button = screen.getByTestId(testId);
      expect(button.textContent).toBe(choices[mode].label);
      fireEvent.click(button);
      expect(onPlace).toHaveBeenLastCalledWith(mode);
    }
  });

  it('the tile menu variant opens the same four labels and hints', async () => {
    const onPlace = vi.fn();
    wrap(<PlaceMenu onPlace={onPlace} variant="menu" />);
    fireEvent.click(screen.getByTestId('place-menu'));
    const items = await Promise.all(PLACE_MODES.map(({ mode }) => screen.findByTestId(`place-${mode}`)));
    PLACE_MODES.forEach(({ mode }, i) => {
      expect(items[i].textContent).toContain(choices[mode].label);
      expect(items[i].textContent).toContain(choices[mode].hint);
    });
    fireEvent.click(items[0]);
    expect(onPlace).toHaveBeenCalledWith('insert');
  });

  it('a single-mode host button uses the same label as the four-mode control', () => {
    const onPlace = vi.fn();
    wrap(<PlaceModeButton mode="insert" onPlace={onPlace} testId="cut-insert-x" />);
    const button = screen.getByTestId('cut-insert-x');
    expect(button.textContent).toBe(choices.insert.label);
    fireEvent.click(button);
    expect(onPlace).toHaveBeenCalledTimes(1);
  });

  it('every host imports the one source instead of keeping literal copies', () => {
    const root = process.cwd();
    const hosts = [
      'components/editor/media/ClipGrid.tsx',
      'components/editor/cut/CutWorkspace.tsx',
      'components/editor/phone/AddMediaSheet.tsx',
      'components/editor/phone/MediaTile.tsx',
      'components/editor/place/PlaceMenu.tsx',
    ];
    for (const host of hosts) {
      const source = readFileSync(join(root, host), 'utf8');
      expect(source, host).toMatch(/placeChoice(Label|Hint)Msg|PlaceModeButton/);
      expect(source, host).not.toContain('insertAtPlayhead');
      expect(source, host).not.toContain('addToEnd');
      expect(source, host).not.toMatch(/PLACE_CHOICES/);
    }
    expect(readFileSync(join(root, 'components/editor/place/placeChoices.ts'), 'utf8')).not.toMatch(/PLACE_CHOICES|Insert at playhead|Add to end/);
  });

  it('the phone keeps its words: the shared Italian insert/append labels are the old phone ones', () => {
    expect(choicesIt.insert.label).toBe('Inserisci alla testina');
    expect(choicesIt.append.label).toBe('In fondo');
  });
});
