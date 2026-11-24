// @vitest-environment jsdom
/**
 * RM.15: the tablet layout. The inspector drawer lives in PanelLayout (PanelLayout.tablet.test.tsx); the 56 px
 * rail carries the workspaces; the header's More menu says plainly that the tablet is not certified yet;
 * a coarse pointer gets 44 px resize bands.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { WorkspaceBar } from '../WorkspaceBar';
import { WorkspaceHeader } from '../WorkspaceHeader';

beforeEach(() => {
  window.localStorage.clear();
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(cleanup);

describe('the 64 px rail', () => {
  it('lists the seven workspaces and switches on tap', () => {
    const onChange = vi.fn();
    render(
      <MantineProvider>
        <WorkspaceBar active="cut" onChange={onChange} variant="rail" />
      </MantineProvider>,
    );
    const nav = screen.getByTestId('workspace-bar');
    expect(nav.querySelectorAll('button').length).toBe(7);
    expect(screen.getByTestId('workspace-cut').getAttribute('aria-current')).toBe('page');
    fireEvent.click(screen.getByTestId('workspace-audio'));
    expect(onChange).toHaveBeenCalledWith('audio');
  });
});

describe('the tablet header', () => {
  it('drops the switcher, keeps a 44 px Export and says the tablet is not certified', async () => {
    render(
      <MantineProvider>
        <WorkspaceHeader name="Film" revision={3} backHref="/a" reviewHref="/b" workspace="cut" onWorkspace={() => {}} onShortcuts={() => {}} onExport={() => {}} tablet />
      </MantineProvider>,
    );
    expect(screen.queryByTestId('workspace-bar')).toBeNull();
    expect(screen.getByTestId('open-deliver').getAttribute('style') ?? '').toContain('2.75rem');
    fireEvent.click(screen.getByTestId('header-more'));
    expect((await screen.findByTestId('tablet-note')).textContent).toBe('Tablet: works, not certified yet');
    expect(screen.getByTestId('open-review-page')).toBeTruthy();
    expect(screen.getByTestId('open-keymap')).toBeTruthy();
  });
});

describe('coarse-pointer CSS', () => {
  const css = readFileSync(join(__dirname, '..', 'workspace.module.css'), 'utf8');
  it('widens the resize band to 44 px', () => {
    const block = css.slice(css.indexOf('/* A coarse pointer grabs'));
    expect(block).toMatch(/\.handle::after\s*\{\s*inset: 0 -22px/);
    expect(block).toMatch(/\.handleRow::after\s*\{\s*inset: -22px 0/);
  });
  it('gives the rail 64 px so "Compose" fits at the 12 px floor', () => {
    expect(css).toMatch(/\.rail\s*\{[^}]*flex: 0 0 64px/);
    expect(css).toMatch(/\.railItem\s*\{[^}]*font-size: 12px/);
  });
});
