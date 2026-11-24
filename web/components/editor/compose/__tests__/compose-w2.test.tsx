import { readFileSync } from 'node:fs';
import path from 'node:path';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { liveChords } from '@/lib/editing/keymap';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { ComposeHarness } from '../../__tests__/compose-harness';

vi.mock('@xyflow/react', () => ({ ReactFlow: ({ children }: { children?: React.ReactNode }) => <div>{children}</div>, Background: () => null }));
vi.mock('@xyflow/react/dist/style.css', () => ({}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/editing/api/analysis', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/analysis')>()), useTimelineAnalysis: () => ({ data: undefined, isLoading: false, error: undefined }) }));
vi.mock('@/lib/hooks/use-segmentation-tracks', () => ({ useSegmentationTracks: () => ({ lookup: () => undefined, version: 1 }) }));
vi.mock('../../viewer/Preview3s', () => ({ Preview3sButton: () => <button data-testid="preview-3s">Preview 3 s</button> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
afterEach(cleanup);

const RECT = { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false };

function setup(extra: Record<string, unknown>, opts: { overrides?: Record<string, string>; onViewerTool?: ReturnType<typeof vi.fn>; onActivateViewerTool?: ReturnType<typeof vi.fn>; onDrawMask?: ReturnType<typeof vi.fn>; readOnly?: boolean } = {}) {
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 0, 60, 0), ...extra } as unknown as MediaClip])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  const props = {
    doc: store.getState().doc,
    projectId: 'p',
    timelineId: 'tl',
    readOnly: opts.readOnly ?? false,
    selection: ['c1'] as readonly string[],
    playheadFrame: 10,
    inOut: { inFrame: null, outFrame: null } as { inFrame: number | null; outFrame: number | null },
    newId: () => 'n',
    onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => store.dispatch({ type: 'setProperty', itemId, key, value }, label),
    onApply: (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label),
    onSelect: vi.fn(),
    onSeek: vi.fn(),
    onViewerTool: opts.onViewerTool as never,
    onActivateViewerTool: opts.onActivateViewerTool as never,
    onDrawMask: opts.onDrawMask as never,
  };
  render(
    <MantineProvider>
      <LiveChordsProvider chords={liveChords(opts.overrides ?? {})}>
        <ComposeHarness {...props} />
      </LiveChordsProvider>
    </MantineProvider>,
  );
  return { store, clip: () => store.getState().doc.tracks[0].items[0] as MediaClip };
}

describe('live chords, through the real provider (T-014)', () => {
  it('the list entries, the draw button and the remove buttons show the default chords', () => {
    setup({ mask: RECT, keyer: { color: '#00FF00', similarity: 0.3, blend: 0.1, spill: 0.5 } });
    const mask = screen.getByTestId('compose-entry-mask');
    expect(mask.getAttribute('aria-keyshortcuts')).toBe('Shift+M');
    expect(mask.getAttribute('title')).toBe('Mask (Shift+M)');
    expect(screen.getByTestId('compose-entry-reframe').getAttribute('aria-keyshortcuts')).toBe('Shift+F');
    expect(screen.getByTestId('compose-entry-pin').getAttribute('aria-keyshortcuts')).toBe('Shift+P');
    expect(screen.getByTestId('compose-mask-draw').getAttribute('aria-keyshortcuts')).toBe('Shift+M');
    const remove = screen.getByTestId('mask-reframe-remove');
    expect(remove.getAttribute('aria-keyshortcuts')).toBe('Alt+Backspace');
    expect(remove.getAttribute('title')).toBe('Remove mask (Alt+Backspace)');
    fireEvent.click(screen.getByTestId('compose-entry-keyer'));
    expect(screen.getByTestId('compose-keyer-toggle').getAttribute('aria-keyshortcuts')).toBe('Alt+Backspace');
  });

  it('a remap changes the tooltip and aria-keyshortcuts of every control that carries the chord', () => {
    setup({ mask: RECT }, { overrides: { toolMask: 'Shift+9', composeRemoveEntry: 'Mod+Backspace' } });
    const mask = screen.getByTestId('compose-entry-mask');
    expect(mask.getAttribute('aria-keyshortcuts')).toBe('Shift+9');
    expect(mask.getAttribute('title')).toBe('Mask (Shift+9)');
    expect(screen.getByTestId('compose-mask-draw').getAttribute('aria-keyshortcuts')).toBe('Shift+9');
    expect(screen.getByTestId('mask-reframe-remove').getAttribute('aria-keyshortcuts')).toBe('Control+Backspace');
  });

  it('an unbound or beaten chord is not advertised', () => {
    setup({ mask: RECT }, { overrides: { toolMask: '', pageCut: 'Shift+F' } });
    expect(screen.getByTestId('compose-entry-mask').getAttribute('aria-keyshortcuts')).toBeNull();
    expect(screen.getByTestId('compose-entry-mask').getAttribute('title')).toBeNull();
    // Shift+F was Reframe's default; the user's remap of Go to Cut took it
    expect(screen.getByTestId('compose-entry-reframe').getAttribute('aria-keyshortcuts')).toBeNull();
  });
});

describe('the list', () => {
  it('lets editor shortcuts through and is 44 px on a coarse pointer by a real media query', () => {
    setup({});
    expect(screen.getByTestId('compose-list').getAttribute('data-editor-shortcuts')).toBe('through');
    const css = readFileSync(path.resolve(__dirname, '../ComposeList.module.css'), 'utf8');
    expect(css).toMatch(/\.entry\s*{[^}]*min-height:\s*32px/);
    expect(css).toMatch(/@media \(pointer: coarse\)\s*{\s*\.entry\s*{[^}]*min-height:\s*44px/);
    expect(screen.getByTestId('compose-entry-mask').className).toMatch(/entry/);
  });

  it('an arrow key selects the entry and enters its tool without taking focus; a click or Enter takes it', () => {
    const onViewerTool = vi.fn();
    setup({ mask: RECT, reframe: { x: 0.5, y: 0.5 } }, { onViewerTool });
    const mask = screen.getByTestId('compose-entry-mask');
    mask.focus();
    fireEvent.keyDown(mask, { key: 'ArrowDown' });
    expect(onViewerTool).toHaveBeenLastCalledWith('reframe', { focusPicture: false });
    expect(document.activeElement).toBe(screen.getByTestId('compose-entry-reframe'));
    fireEvent.click(screen.getByTestId('compose-entry-mask'));
    expect(onViewerTool).toHaveBeenLastCalledWith('mask', { focusPicture: true });
  });

  it('a modified arrow is left to the global chord: the list does not move', () => {
    const onViewerTool = vi.fn();
    setup({ mask: RECT, reframe: { x: 0.5, y: 0.5 } }, { onViewerTool });
    const mask = screen.getByTestId('compose-entry-mask');
    mask.focus();
    onViewerTool.mockClear();
    fireEvent.keyDown(mask, { key: 'ArrowUp', altKey: true });
    fireEvent.keyDown(mask, { key: 'ArrowDown', altKey: true });
    expect(onViewerTool).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(mask);
  });
});

describe('the Mask entry', () => {
  it('leads with Draw on the picture as the filled primary action; Add mask and Add ellipse are quieter but there', () => {
    const onDrawMask = vi.fn();
    setup({}, { onDrawMask });
    const region = screen.getByRole('region', { name: /Properties of Mask/ });
    const controls = Array.from(region.querySelectorAll('button')).map((b) => b.getAttribute('data-testid'));
    expect(controls[0]).toBe('compose-mask-draw');
    const draw = screen.getByTestId('compose-mask-draw');
    expect(draw.getAttribute('data-variant')).toBe('filled');
    expect(draw.textContent).toBe('Draw on the picture');
    expect(screen.getByTestId('mask-reframe-add-mask').getAttribute('data-variant')).toBe('subtle');
    expect(screen.getByTestId('mask-reframe-add-ellipse').getAttribute('data-variant')).toBe('subtle');
    fireEvent.click(draw);
    expect(onDrawMask).toHaveBeenCalledWith(false);
  });

  it('shows Redraw when a mask exists, and it arms Redraw', () => {
    const onDrawMask = vi.fn();
    setup({ mask: RECT }, { onDrawMask });
    const draw = screen.getByTestId('compose-mask-draw');
    expect(draw.textContent).toBe('Redraw');
    fireEvent.click(draw);
    expect(onDrawMask).toHaveBeenCalledWith(true);
  });

  it('read-only hides the draw button', () => {
    setup({}, { readOnly: true });
    expect(screen.queryByTestId('compose-mask-draw')).toBeNull();
  });
});

describe('copy and layout', () => {
  it('Preview 3 s is in the panel actions slot, not the first control of the body', () => {
    setup({ mask: RECT });
    const panel = screen.getByTestId('compose-properties');
    const header = panel.querySelector('header') as HTMLElement;
    expect(within(header).getByTestId('preview-3s')).toBeDefined();
    const body = panel.querySelector('header + div') as HTMLElement;
    expect(within(body).queryByTestId('preview-3s')).toBeNull();
  });

  it('the list names its entries in plain words: Node view, not Chain', () => {
    setup({});
    expect(screen.getByTestId('compose-entry-chain').textContent).toBe('Node view');
    expect(screen.getByTestId('compose-list').textContent).not.toMatch(/Chain|Quad/);
  });

  it('what the viewer shows is said once per entry, and the long repeated line is gone', () => {
    setup({});
    fireEvent.click(screen.getByTestId('compose-entry-chain'));
    expect(screen.getAllByTestId('compose-render-note')).toHaveLength(1);
    expect(screen.queryByText(/render-only; the badge above the viewer/)).toBeNull();
  });
});

describe('Add corner pin', () => {
  it('writes the pin and enters the pin tool, like Add mask and Add reframe', () => {
    const onActivateViewerTool = vi.fn();
    const { clip } = setup({}, { onActivateViewerTool });
    fireEvent.click(screen.getByTestId('compose-entry-pin'));
    onActivateViewerTool.mockClear();
    fireEvent.click(screen.getByTestId('compose-pin-toggle'));
    expect(clip().cornerPin).toBeTruthy();
    expect(onActivateViewerTool).toHaveBeenCalledWith('pin');
  });
});
