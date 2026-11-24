import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { MaskTool } from '../MaskToolBar';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
afterEach(cleanup);

function setup(mask: unknown, opts: { tool?: 'mask' | 'select'; frame?: number; readOnly?: boolean } = {}) {
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), mask } as unknown as MediaClip])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  store.setUi({ selection: ['c1'], viewerTool: opts.tool ?? 'mask', playheadFrame: opts.frame ?? 20 });
  render(
    <MantineProvider>
      <MaskTool store={store} readOnly={opts.readOnly ?? false} />
    </MantineProvider>,
  );
  const clip = () => store.getState().doc.tracks[0].items[0] as MediaClip;
  return { store, clip };
}

describe('the mask tool', () => {
  it('renders nothing unless the mask tool is active', () => {
    setup(null, { tool: 'select' });
    expect(screen.queryByTestId('viewer-mask-toolbar')).toBeNull();
  });

  it('with no mask: a draw layer and an Add; Enter writes the default mask as ONE undo step', () => {
    const { store, clip } = setup(null);
    expect(screen.getByTestId('viewer-mask-toolbar')).toBeDefined();
    fireEvent.keyDown(screen.getByTestId('viewer-mask-draw'), { key: 'Enter' });
    expect(clip().mask).toMatchObject({ kind: 'rect', feather: 0.05 });
    expect(store.getState().history).toHaveLength(1);
    store.undo();
    expect(clip().mask ?? null).toBeNull();
  });

  it('a click on the picture writes nothing', () => {
    const { store } = setup(null);
    const layer = screen.getByTestId('viewer-mask-draw');
    fireEvent.pointerDown(layer, { button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(layer, { clientX: 10, clientY: 10 });
    expect(store.getState().history).toHaveLength(0);
  });

  it('with a mask: no draw layer until Redraw; invert and remove are single steps', () => {
    const { store, clip } = setup({ kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false });
    expect(screen.queryByTestId('viewer-mask-draw')).toBeNull();
    fireEvent.click(screen.getByTestId('viewer-mask-redraw'));
    expect(screen.getByTestId('viewer-mask-draw')).toBeDefined();
    fireEvent.click(screen.getByTestId('viewer-mask-tool-invert'));
    expect((clip().mask as { invert?: boolean }).invert).toBe(true);
    fireEvent.click(screen.getByTestId('viewer-mask-remove'));
    expect(clip().mask ?? null).toBeNull();
    expect(store.getState().history).toHaveLength(2);
  });

  it('Done returns the viewer to select', () => {
    const { store } = setup(null);
    fireEvent.click(screen.getByTestId('viewer-mask-done'));
    expect(store.getState().ui.viewerTool).toBe('select');
  });

  it('read-only: no draw layer and a disabled Add', () => {
    setup(null, { readOnly: true });
    expect(screen.queryByTestId('viewer-mask-draw')).toBeNull();
    expect((screen.getByTestId('viewer-mask-add') as HTMLButtonElement).disabled).toBe(true);
  });

  it('off-screen clip says to move the playhead', () => {
    setup(null, { frame: 5 });
    expect(screen.getByTestId('viewer-mask-hint').textContent).toMatch(/Move the playhead/);
    expect(screen.queryByTestId('viewer-mask-draw')).toBeNull();
  });

  it('an object mask is never overwritten: no draw layer, no remove', () => {
    setup({ kind: 'segmentation', analysisStepResultId: 'a', trackIds: ['g'], invert: false, featherPx: 1 });
    expect(screen.getByTestId('viewer-mask-hint').textContent).toMatch(/object mask/);
    expect(screen.queryByTestId('viewer-mask-remove')).toBeNull();
    expect(screen.queryByTestId('viewer-mask-draw')).toBeNull();
  });
});
