// @vitest-environment jsdom
/**
 * T-015: static gap hunt for the phone and tablet surface. Each test pins something a row-by-row read found missing
 * (the sheet never had its height, a backdrop class that did not exist, no safe-area at the top, ...).
 */
import React, { useState } from 'react';
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { BottomSheet, type SheetDetent } from '../phone/BottomSheet';
import { PhoneDock } from '../phone/PhoneDock';
import { WorkspaceHeader } from '../shell/WorkspaceHeader';
import { DroppedFramesChip } from '../viewer/ViewerStatus';

const WEB = join(__dirname, '..', '..', '..');
const EDITOR = join(WEB, 'components', 'editor');
const read = (...p: string[]) => readFileSync(join(EDITOR, ...p), 'utf8');
const phoneDir = join(EDITOR, 'phone');
const phoneSources = readdirSync(phoneDir).filter((n) => /\.(tsx?)$/.test(n));
const phoneCss = read('phone', 'phone.module.css');

function stubMedia(coarse: boolean) {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: coarse && query.includes('pointer: coarse'), media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
}
beforeEach(() => stubMedia(false));
afterEach(cleanup);
const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('phone CSS is wired to the components', () => {
  it('every class a phone component reads exists in phone.module.css', () => {
    const defined = new Set([...phoneCss.matchAll(/\.([A-Za-z][\w-]*)/g)].map((m) => m[1]));
    const missing: string[] = [];
    for (const name of phoneSources) {
      const src = readFileSync(join(phoneDir, name), 'utf8');
      for (const m of src.matchAll(/\bclasses\.([A-Za-z]\w*)/g)) if (!defined.has(m[1])) missing.push(`${name}: classes.${m[1]}`);
    }
    expect(missing).toEqual([]);
  });

  it('the sheet takes its height from the detent and stays under the status area', () => {
    expect(phoneCss).toMatch(/\.sheet \{[^}]*height: var\(--rb-sheet-h/);
    expect(phoneCss).toMatch(/\.sheet \{[^}]*max-height: calc\(100dvh - var\(--rb-safe-top/);
  });

  it('keeps clear of the notch, the corners and the home bar', () => {
    expect(phoneCss).toMatch(/\.shell \{[^}]*padding-top: var\(--rb-safe-top/);
    expect(phoneCss).toMatch(/\.shell \{[^}]*padding-left: var\(--rb-safe-left/);
    expect(phoneCss).toMatch(/\.shell \{[^}]*padding-right: var\(--rb-safe-right/);
    expect(phoneCss).toMatch(/\.dock \{[^}]*padding-bottom: var\(--rb-safe-bottom/);
    // The bottom sheet ends above the dock, which carries the home-bar inset (--rb-dock-h includes it); the drawer pads itself.
    expect(phoneCss).toMatch(/\.sheet \{[^}]*bottom: var\(--rb-dock-h/);
    expect(phoneCss).toMatch(/--rb-dock-h: calc\(57px \+ var\(--rb-safe-bottom/);
    expect(phoneCss).toMatch(/\.sheet\[data-side='right'\] \{[^}]*padding-bottom: var\(--rb-safe-bottom/);
    expect(phoneCss).toMatch(/\.snackbar \{[^}]*var\(--rb-safe-bottom/);
    expect(phoneCss).toMatch(/\.snackbar \{[^}]*var\(--rb-safe-left/);
  });

  it('turns off the sheet animation for reduced motion, and the editor menus and dialogs too', () => {
    expect(phoneCss).toMatch(/prefers-reduced-motion: reduce\) \{\s*\.sheet \{ transition: none; \}/);
    const global = readFileSync(join(WEB, 'app', 'global.css'), 'utf8');
    expect(global).toMatch(/prefers-reduced-motion: reduce\)[\s\S]*mantine-Drawer-content[\s\S]*transition-duration: 0\.01ms/);
  });

  it('gives menus and dialogs that are portalled out of the editor the touch sizes on a coarse pointer', () => {
    const global = readFileSync(join(WEB, 'app', 'global.css'), 'utf8');
    expect(global).toMatch(/pointer: coarse\)[\s\S]*--rb-icon-h: 44px[\s\S]*mantine-Menu-item \{\s*min-height: 44px/);
  });
});

describe('BottomSheet semantics', () => {
  function Harness({ initial = 'half' as SheetDetent }) {
    const [open, setOpen] = useState(false);
    const [detent, setDetent] = useState<SheetDetent>(initial);
    return (
      <>
        <button type="button" onClick={() => setOpen(true)} data-testid="opener">
          Open
        </button>
        <BottomSheet opened={open} onClose={() => setOpen(false)} title="Look" detent={detent} onDetentChange={setDetent}>
          <button type="button">Inside</button>
        </BottomSheet>
      </>
    );
  }

  it('is a labelled dialog, carries its detent height, and gives focus back to what opened it', () => {
    wrap(<Harness />);
    const opener = screen.getByTestId('opener');
    opener.focus();
    fireEvent.click(opener);
    const dialog = screen.getByRole('dialog', { name: 'Look' });
    expect(dialog.style.getPropertyValue('--rb-sheet-h')).toBe('45dvh');
    expect(dialog.getAttribute('data-detent')).toBe('half');
    fireEvent.click(screen.getByLabelText('Close Look'));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('has a 44 px close button and a grabber button for the height', () => {
    wrap(<Harness initial="peek" />);
    fireEvent.click(screen.getByTestId('opener'));
    const close = screen.getByLabelText('Close Look');
    expect(close.getAttribute('style') ?? '').toMatch(/2\.75rem|44px/);
    expect(screen.getByTestId('bottom-sheet-grabber')).toBeTruthy();
  });
});

describe('PhoneDock semantics', () => {
  it('is a labelled navigation of five toggle buttons that say which sheet is open', () => {
    const onSelect = vi.fn();
    wrap(<PhoneDock active="look" onSelect={onSelect} />);
    expect(screen.getByRole('navigation', { name: 'Editor tools' })).toBeTruthy();
    const buttons = screen.getAllByRole('button');
    expect(buttons).toHaveLength(5);
    expect(screen.getByTestId('dock-look').getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByTestId('dock-media').getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(screen.getByTestId('dock-audio'));
    expect(onSelect).toHaveBeenCalledWith('audio');
  });
});

describe('Layouts menu', () => {
  const layoutMenu = <button type="button" data-testid="layout-menu">Layouts</button>;
  const header = (props: { tablet?: boolean; compact?: boolean }) =>
    wrap(<WorkspaceHeader name="Film" revision={1} backHref="/a" reviewHref="/b" workspace="cut" onWorkspace={() => {}} onShortcuts={() => {}} onExport={() => {}} layoutMenu={layoutMenu} {...props} />);

  it('is reachable on the tablet (hidden below the drawer width by CSS, not removed)', () => {
    header({ tablet: true });
    expect(screen.getByTestId('tablet-layouts').contains(screen.getByTestId('layout-menu'))).toBe(true);
    const css = read('shell', 'workspace.module.css');
    expect(css).toMatch(/\.tabletLayouts \{ display: contents; \}/);
    expect(css).toMatch(/max-width: 955\.98px\) \{\s*\.tabletLayouts \{ display: none; \}/);
  });

  it('is absent on the phone header, not left as a dead button', () => {
    header({ compact: true });
    expect(screen.queryByTestId('layout-menu')).toBeNull();
    expect(screen.queryByTestId('tablet-layouts')).toBeNull();
  });
});

describe('viewer status controls on a touch screen', () => {
  it('lifts the simple-preview switch to 44 px on a coarse pointer', () => {
    stubMedia(true);
    wrap(<DroppedFramesChip onSwitch={() => {}} />);
    const style = screen.getByTestId('switch-simple-preview').getAttribute('style') ?? '';
    expect(style).toMatch(/2\.75rem|44px/);
  });
});

describe('phone edits use the shared store', () => {
  it('writes through the editor store (so the offline journal and sync client see every phone edit), never straight to the network', () => {
    const src = read('phone', 'usePhoneActions.ts');
    expect(src).toContain('store.dispatchBatch');
    expect(src).not.toMatch(/\bfetch\(/);
  });
});

describe('phone wording', () => {
  const files = [
    ...phoneSources.filter((n) => !/\.test\./.test(n)).map((n) => join(phoneDir, n)),
    join(EDITOR, 'deliver', 'RenderSettings.tsx'),
    join(EDITOR, 'deliver', 'FilmCard.tsx'),
    join(EDITOR, 'deliver', 'deliver-phone.ts'),
  ];
  const BANNED = /hover|right[- ]?click|double[- ]?click|\bmouse\b|—|–|\bctrl\b|\bshift\+/i;

  it('has no hover, right-click, mouse, keyboard-shortcut wording or dashes in strings and text the user reads', () => {
    const bad: string[] = [];
    for (const f of files) {
      const src = readFileSync(f, 'utf8')
        .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
        .split('\n');
      src.forEach((line, i) => {
        const code = line.replace(/\/\/.*$/, '');
        if (/^\s*import /.test(code) || /pointerType/.test(code)) return;
        const literals = [...code.matchAll(/'([^'\\]|\\.)*'|"([^"\\]|\\.)*"|`([^`\\]|\\.)*`|>[^<>{}]+</g)].map((m) => m[0]);
        for (const lit of literals) if (BANNED.test(lit)) bad.push(`${relative(WEB, f)}:${i + 1}: ${lit.slice(0, 80)}`);
      });
    }
    expect(bad).toEqual([]);
  });
});
