import { readFileSync } from 'node:fs';
import path from 'node:path';
import React, { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { MediaClip, QuadCorners } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { FrameHandlesOverlay } from '../FrameHandlesOverlay';
import { COLORS } from '../handle-style';
import { ToolDockContext } from '../tool-dock';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { liveChords } from '@/lib/editing/keymap';

const coarse = { current: false };
global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: coarse.current && query === '(pointer: coarse)', media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
(globalThis as unknown as { PointerEvent: unknown }).PointerEvent = class extends MouseEvent {};
afterEach(() => {
  cleanup();
  coarse.current = false;
  known.size = null;
});

const PIN: QuadCorners = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };

const known: { size: { width: number; height: number } | null } = { size: null };

function Host({ store }: { store: ReturnType<typeof createEditorStore> }) {
  const [dock, setDock] = useState<HTMLElement | null>(null);
  return (
    <MantineProvider>
      <LiveChordsProvider chords={liveChords({})}>
        <ToolDockContext.Provider value={dock}>
          <div data-testid="dock" ref={setDock} />
          <div data-testid="overlay">
            <FrameHandlesOverlay store={store} sourceSizeFor={() => known.size} readOnly={false} />
          </div>
        </ToolDockContext.Provider>
      </LiveChordsProvider>
    </MantineProvider>
  );
}

function setup(clipExtra: Record<string, unknown>, tool: 'pin' | 'mask' | 'reframe', opts: { selection?: string[]; frame?: number } = {}) {
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), ...clipExtra } as unknown as MediaClip])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  store.setUi({ selection: opts.selection ?? ['c1'], viewerTool: tool, playheadFrame: opts.frame ?? 20, playbackSpeed: 0 });
  render(<Host store={store} />);
  return { store, dock: screen.getByTestId('dock'), overlay: screen.getByTestId('overlay') };
}

describe('the tool bar docks outside the picture', () => {
  it('mask: one compact row in the dock; only the draw layer is over the picture; no render note, hint inside the row', () => {
    const { dock, overlay } = setup({ mask: null }, 'mask');
    const bar = screen.getByTestId('viewer-mask-toolbar');
    expect(dock.contains(bar)).toBe(true);
    expect(overlay.contains(bar)).toBe(false);
    expect(overlay.contains(screen.getByTestId('viewer-mask-draw'))).toBe(true);
    expect(screen.queryByTestId('viewer-mask-render-note')).toBeNull();
    expect(within(bar).getByTestId('viewer-mask-hint')).toBeDefined();
    expect(within(bar).getByTestId('viewer-mask-done')).toBeDefined();
    expect(bar.style.position).not.toBe('absolute');
    expect(bar.style.flexDirection).not.toBe('column');
    // the handles layer and the draw layer are not under the bar
    expect(bar.querySelector('[data-testid="viewer-mask-draw"]')).toBeNull();
  });

  it('pin: the note and Done are in the dock, never over the picture', () => {
    const { dock, overlay } = setup({ cornerPin: PIN }, 'pin');
    const bar = screen.getByTestId('viewer-pin-toolbar');
    expect(dock.contains(bar)).toBe(true);
    expect(overlay.contains(bar)).toBe(false);
    expect(overlay.querySelector('[data-testid="viewer-pin-note"]')).toBeNull();
    expect(overlay.querySelector('[data-testid="viewer-pin-done"]')).toBeNull();
  });
});

describe('the mask bar advertises only chords that can run', () => {
  const RECT = { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false };
  it('no Shift+S in the draw state, where there is no mask to switch', () => {
    setup({ mask: null }, 'mask');
    expect(screen.getByTestId('viewer-mask-kind').getAttribute('aria-keyshortcuts')).toBeNull();
    expect(screen.getByTestId('viewer-mask-kind').getAttribute('title')).toBeNull();
  });
  it('Shift+S, Shift+I with a mask; Remove mask carries Alt+Backspace only on Compose with the Mask entry selected', () => {
    setup({ mask: RECT }, 'mask');
    expect(screen.getByTestId('viewer-mask-kind').getAttribute('aria-keyshortcuts')).toBe('Shift+S');
    expect(screen.getByTestId('viewer-mask-tool-invert').getAttribute('aria-keyshortcuts')).toBe('Shift+I');
    // the Edit page (an Inspector Add mask): the remove chord does not run there
    expect(screen.getByTestId('viewer-mask-remove').getAttribute('aria-keyshortcuts')).toBeNull();
    cleanup();
    const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), mask: RECT } as unknown as MediaClip])]);
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
    store.setUi({ selection: ['c1'], viewerTool: 'mask', playheadFrame: 20, workspace: 'compose', composeEntry: 'mask' });
    render(<Host store={store} />);
    const remove = screen.getByTestId('viewer-mask-remove');
    expect(remove.getAttribute('aria-keyshortcuts')).toBe('Alt+Backspace');
    expect(remove.getAttribute('title')).toBe('Remove mask (Alt+Backspace)');
  });
});

describe('pin tool: Done in every state, and Add never writes on entry', () => {
  it.each([
    ['no clip selected', {}, { selection: [] as string[] }],
    ['the clip is off screen', {}, { frame: 5 }],
    ['the clip has no pin', {}, {}],
    ['a pinned clip', { cornerPin: PIN }, {}],
  ])('%s', (_name, extra, opts) => {
    const { store } = setup(extra, 'pin', opts);
    fireEvent.click(screen.getByTestId('viewer-pin-done'));
    expect(store.getState().ui.viewerTool).toBe('select');
    expect(store.getState().history).toHaveLength(0);
  });

  it('a clip with no pin says so, offers Add, and Add writes the identity pin as one step', () => {
    const { store } = setup({}, 'pin');
    expect(store.getState().history).toHaveLength(0);
    expect(screen.getByTestId('viewer-pin-note').textContent).toMatch(/no corner pin/);
    expect(screen.queryByTestId('viewer-pin-topLeft')).toBeNull();
    fireEvent.click(screen.getByTestId('viewer-pin-add'));
    expect(store.getState().history).toHaveLength(1);
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).cornerPin).toEqual(PIN);
    expect(screen.getByTestId('viewer-pin-topLeft')).toBeDefined();
  });
});

describe('pin honesty', () => {
  it('a clip whose size is not yet known keeps the frame note rather than claiming a match', () => {
    setup({ cornerPin: PIN }, 'pin');
    expect(screen.getByTestId('viewer-pin-note').textContent).toMatch(/frame's corners/);
  });
  it('a clip at identity that fills the canvas gets only the plain hint', () => {
    known.size = { width: 1920, height: 1080 };
    setup({ cornerPin: PIN }, 'pin');
    expect(screen.getByTestId('viewer-pin-note').textContent).not.toMatch(/frame's corners/);
  });
  it('a scaled clip says the handles mark the frame\'s corners, not the picture\'s own edges', () => {
    setup({ cornerPin: PIN, transform: { scale: { value: 1.25, keys: null } } }, 'pin');
    const text = screen.getByTestId('viewer-pin-note').textContent ?? '';
    expect(text).toMatch(/frame's corners/);
    expect(text).toMatch(/not the picture's own edges/);
    // the handles are still canvas-fraction true
    expect(screen.getByTestId('viewer-pin-bottomRight').style.left).toBe('100%');
  });
  it('a moved clip says it too', () => {
    setup({ cornerPin: PIN, transform: { positionX: { value: 0.1, keys: null } } }, 'pin');
    expect(screen.getByTestId('viewer-pin-note').textContent).toMatch(/frame's corners/);
  });
});

describe('reframe tool is never a silent dead end', () => {
  it('a clip with no reframe point says what to do, offers Add, and writes nothing on entry', () => {
    const { store, dock } = setup({}, 'reframe');
    expect(dock.contains(screen.getByTestId('viewer-reframe-toolbar'))).toBe(true);
    expect(screen.getByTestId('viewer-reframe-hint').textContent).toMatch(/no reframe point/);
    expect(store.getState().history).toHaveLength(0);
    fireEvent.click(screen.getByTestId('viewer-reframe-add'));
    expect(store.getState().history).toHaveLength(1);
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).reframe).toEqual({ x: 0.5, y: 0.5 });
    expect(screen.queryByTestId('viewer-reframe-add')).toBeNull();
  });
  it('a clip with a point has no Add, and Done leaves the tool', () => {
    const { store } = setup({ reframe: { x: 0.3, y: 0.4 } }, 'reframe');
    expect(screen.queryByTestId('viewer-reframe-add')).toBeNull();
    fireEvent.click(screen.getByTestId('viewer-reframe-done'));
    expect(store.getState().ui.viewerTool).toBe('select');
  });
});

describe('44 px on a coarse pointer', () => {
  it('pin handles are 24 px fine and 44 px coarse, like the mask and reframe handles', () => {
    setup({ cornerPin: PIN }, 'pin');
    expect(screen.getByTestId('viewer-pin-topLeft').style.width).toBe('24px');
    cleanup();
    coarse.current = true;
    setup({ cornerPin: PIN }, 'pin');
    for (const c of ['topLeft', 'topRight', 'bottomRight', 'bottomLeft']) {
      const handle = screen.getByTestId(`viewer-pin-${c}`);
      expect(handle.style.width).toBe('44px');
      expect(handle.style.height).toBe('44px');
    }
    expect(screen.getByTestId('viewer-pin-done').style.minHeight).toBe('44px');
  });

  it('the mask bar\'s segmented control and buttons reach 44 px', () => {
    coarse.current = true;
    setup({ mask: null }, 'mask');
    expect(screen.getByTestId('viewer-mask-done').style.minHeight).toBe('44px');
    expect(screen.getByTestId('viewer-mask-add').style.minHeight).toBe('44px');
    const label = screen.getByTestId('viewer-mask-kind').querySelector('label') as HTMLElement;
    expect(label.style.minHeight).toBe('44px');
  });

  it('fine pointers keep the compact controls', () => {
    setup({ mask: null }, 'mask');
    expect(screen.getByTestId('viewer-mask-done').style.minHeight).toBe('');
  });
});

describe('handle contrast on any picture', () => {
  const lum = (hex: string) => {
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const ratio = (a: string, b: string) => {
    const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
  };
  const HALO = '#14171c';
  const CORE = '#f8f9fa';
  const PICTURES = ['#000000', '#ffffff', '#777777', '#ffd43b', '#1c7ed6'];

  it('the halo and the core are tokens in global.css with these values', () => {
    const css = readFileSync(path.resolve(__dirname, '../../../../app/global.css'), 'utf8');
    expect(css).toContain(`--rb-ed-handle-halo: ${HALO};`);
    expect(css).toContain(`--rb-ed-handle-core: ${CORE};`);
  });

  it('the core inside the halo is a 3:1 mark, and one of the two reads at 3:1 on every picture', () => {
    expect(ratio(CORE, HALO)).toBeGreaterThanOrEqual(3);
    for (const picture of PICTURES) expect(Math.max(ratio(HALO, picture), ratio(CORE, picture)), picture).toBeGreaterThanOrEqual(3);
  });

  it('a bare handle colour would fail on a light picture, which is why every kind carries the halo', () => {
    expect(ratio(COLORS.mask, '#ffffff')).toBeLessThan(3);
    for (const colour of Object.values(COLORS)) expect(ratio(colour, HALO), colour).toBeGreaterThanOrEqual(3);
  });
});
