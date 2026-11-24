// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { EditorEmpty, EditorIcon, EditorPanel, EditorSurface, useEditorDisplay } from '..';
import { SAVED_SCHEME_KEY } from '../EditorDisplay';
import { DEFAULT_DISPLAY_PREFS, displayPrefsKey, effectiveDensity, parseDisplayPrefs, updateDisplayPrefs, validateDisplayPrefs } from '@/lib/editing/display/display-prefs';

let coarse = false;
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
beforeEach(() => {
  coarse = false;
  window.localStorage.clear();
  vi.stubGlobal('matchMedia', (q: string) => ({ matches: q.includes('coarse') ? coarse : false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }));
});
afterEach(cleanup);

const wrap = (ui: React.ReactElement) => render(<MantineProvider defaultColorScheme="light">{ui}</MantineProvider>);

describe('EditorIcon', () => {
  it('a decorative glyph is hidden from assistive technology', () => {
    const { container } = wrap(<EditorIcon icon={IconTrash} />);
    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
    expect(screen.queryByRole('img')).toBeNull();
  });

  it('a titled glyph is an image with a name', () => {
    wrap(<EditorIcon icon={IconTrash} title="Deleted" />);
    expect(screen.getByRole('img', { name: 'Deleted' })).toBeTruthy();
  });

  it('an icon button carries its label as the accessible name and clicks', () => {
    const onClick = vi.fn();
    wrap(<EditorIcon icon={IconTrash} label="Delete clip" onClick={onClick} data-testid="del" />);
    const button = screen.getByRole('button', { name: 'Delete clip' });
    fireEvent.click(button);
    expect(onClick).toHaveBeenCalledOnce();
    expect(button.getAttribute('data-testid')).toBe('del');
    expect(button.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });
});

describe('EditorPanel', () => {
  it('names the region by its title and keeps header actions and body apart', () => {
    wrap(
      <EditorPanel title="Markers" actions={<button type="button">Add</button>} description="Notes on the timeline" testId="p">
        <p>body</p>
      </EditorPanel>,
    );
    const region = screen.getByRole('region', { name: 'Markers' });
    expect(region.getAttribute('data-testid')).toBe('p');
    expect(screen.getByRole('heading', { name: 'Markers' })).toBeTruthy();
    expect(screen.getByText('Notes on the timeline')).toBeTruthy();
    expect(screen.getByText('body')).toBeTruthy();
  });
});

describe('EditorEmpty', () => {
  it('shows a sentence and runs the one primary action', () => {
    const onClick = vi.fn();
    wrap(<EditorEmpty title="No markers yet" hint="Press M to drop one." action={{ label: 'Add a marker', onClick, testId: 'go' }} testId="empty" />);
    expect(screen.getByRole('status').textContent).toContain('No markers yet');
    fireEvent.click(screen.getByTestId('go'));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('has no button when there is nothing to do', () => {
    wrap(<EditorEmpty title="Nothing here" />);
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('display prefs', () => {
  it('validates field by field and ignores unknown fields (additive)', () => {
    expect(validateDisplayPrefs({ density: 'compact', surround: 'dark', future: 1 })).toEqual({ density: 'compact', surround: 'dark' });
    expect(validateDisplayPrefs({ density: 'huge', surround: 7 })).toEqual(DEFAULT_DISPLAY_PREFS);
    expect(validateDisplayPrefs(null)).toEqual(DEFAULT_DISPLAY_PREFS);
    expect(parseDisplayPrefs('{broken')).toEqual(DEFAULT_DISPLAY_PREFS);
  });

  it('a coarse pointer forces comfortable', () => {
    expect(effectiveDensity('compact', true)).toBe('comfortable');
    expect(effectiveDensity('compact', false)).toBe('compact');
  });

  it('persists per user and drives the root attributes', () => {
    wrap(<EditorSurface><span>x</span></EditorSurface>);
    const root = () => document.querySelector('[data-editor-surface]') as HTMLElement;
    expect(root().dataset.edDensity).toBe('comfortable');
    act(() => void updateDisplayPrefs({ density: 'compact' }));
    expect(root().dataset.edDensity).toBe('compact');
    expect(JSON.parse(window.localStorage.getItem(displayPrefsKey())!)).toEqual({ density: 'compact', surround: 'match' });
  });

  it('always-dark forces the dark scheme, keeps the person\'s own scheme, and puts it back', () => {
    wrap(<EditorSurface><span>x</span></EditorSurface>);
    const scheme = () => document.documentElement.getAttribute('data-mantine-color-scheme');
    expect(scheme()).toBe('light');
    act(() => void updateDisplayPrefs({ surround: 'dark' }));
    expect(scheme()).toBe('dark');
    expect(window.localStorage.getItem(SAVED_SCHEME_KEY)).toBe('light');
    act(() => void updateDisplayPrefs({ surround: 'match' }));
    expect(scheme()).toBe('light');
    expect(window.localStorage.getItem(SAVED_SCHEME_KEY)).toBeNull();
  });

  it('forces comfortable on a coarse pointer even when compact is saved', () => {
    coarse = true;
    updateDisplayPrefs({ density: 'compact' });
    function Probe() {
      const d = useEditorDisplay();
      return <span data-testid="d">{`${d.density}/${d.preferredDensity}`}</span>;
    }
    wrap(<EditorSurface><Probe /></EditorSurface>);
    expect(screen.getByTestId('d').textContent).toBe('comfortable/compact');
    expect((document.querySelector('[data-editor-surface]') as HTMLElement).dataset.edTouch).toBe('true');
  });
});
