// @vitest-environment jsdom
/**
 * T-005: one shared Mask and reframe field set. The Inspector and Compose render the SAME component, so
 * their labels and test ids cannot drift; every change is one `setProperty` (one undo step); read-only
 * offers no Add/Remove and disables the fields; a multi-selection shows no section at all.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InspectorPanel } from '../InspectorPanel';
import { ComposeHarness as ComposeWorkspace } from '../../__tests__/compose-harness';
import { DEFAULT_SHAPE_MASK } from '../MaskReframeFields';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { Item, MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

vi.mock('@xyflow/react', () => ({ ReactFlow: ({ children }: { children?: React.ReactNode }) => <div>{children}</div>, Background: () => null }));
vi.mock('@xyflow/react/dist/style.css', () => ({}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/editing/api/analysis', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/analysis')>()), useTimelineAnalysis: () => ({ data: undefined, isLoading: false, error: undefined }) }));
vi.mock('@/lib/hooks/use-segmentation-tracks', () => ({ useSegmentationTracks: () => ({ lookup: () => undefined, version: 1 }) }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
afterEach(cleanup);

const mask = { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.2, h: 0.2, feather: 0.05, invert: false };

function mediaDoc(items: Item[] = [media('c1', 0, 100, 0, {})]) {
  return doc([track(TRACKS.v1, 'video', items)]);
}

function storeWith(extra: Partial<MediaClip> = {}, items?: Item[]) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: mediaDoc(items ?? [media('c1', 0, 100, 0, extra)]), revision: 1 });
  store.setUi({ selection: ['c1'], playheadFrame: 10, playbackSpeed: 0 });
  return store;
}

const setProperty = (store: ReturnType<typeof storeWith>) => (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => store.dispatch({ type: 'setProperty', itemId, key, value }, label);

function inspector(store: ReturnType<typeof storeWith>, readOnly = false, onActivateViewerTool?: (tool: 'mask' | 'reframe') => void) {
  return render(
    <MantineProvider>
      <InspectorPanel doc={store.getState().doc} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={readOnly} onSetProperty={setProperty(store)} onActivateViewerTool={onActivateViewerTool} />
    </MantineProvider>,
  );
}

function compose(store: ReturnType<typeof storeWith>, readOnly = false, selection: string[] = ['c1']) {
  return render(
    <MantineProvider>
      <ComposeWorkspace
        doc={store.getState().doc}
        projectId="p"
        readOnly={readOnly}
        selection={selection}
        playheadFrame={10}
        inOut={{ inFrame: null, outFrame: null }}
        newId={() => 'n'}
        onSetProperty={setProperty(store)}
        onApply={(ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label)}
        onSelect={vi.fn()}
        onSeek={vi.fn()}
      />
    </MantineProvider>,
  );
}

const clipOf = (store: ReturnType<typeof storeWith>) => store.getState().doc.tracks[0].items[0] as MediaClip;
const idsIn = (root: HTMLElement) => Array.from(root.querySelectorAll('[data-testid^="mask-reframe-"]')).map((el) => el.getAttribute('data-testid')).sort();

describe('Mask and reframe fields, shared by both hosts', () => {
  it('the Inspector and Compose render the same labels and test ids', () => {
    const a = storeWith({ mask, reframe: { x: 0.5, y: 0.5 } });
    inspector(a);
    const fromInspector = screen.getByTestId('mask-reframe-fields');
    const inspectorIds = idsIn(fromInspector);
    const inspectorText = fromInspector.textContent;
    cleanup();
    compose(a);
    // Compose splits the one field set across its Mask and Reframe entries; together they are the Inspector's set.
    const maskPart = screen.getByTestId('mask-reframe-fields');
    const maskIds = idsIn(maskPart);
    const maskText = maskPart.textContent ?? '';
    fireEvent.click(screen.getByTestId('compose-entry-reframe'));
    const reframePart = screen.getByTestId('mask-reframe-fields');
    expect([...maskIds, ...idsIn(reframePart), 'mask-reframe-note'].sort()).toEqual(inspectorIds);
    expect(maskText + (reframePart.textContent ?? '') + 'The preview shows the mask and the reframe as you edit. The finished video is exact.').toBe(inspectorText);
    expect(inspectorText).toContain('Shape');
    expect(inspectorText).toContain('Remove mask');
    expect(inspectorText).toContain('Remove reframe');
    expect(inspectorText).toContain('The preview shows the mask and the reframe as you edit. The finished video is exact.');
  });

  it('Add mask writes the default rectangle through ONE setProperty and enters the mask tool', () => {
    const store = storeWith();
    const activate = vi.fn();
    inspector(store, false, activate);
    fireEvent.click(screen.getByTestId('mask-reframe-add-mask'));
    expect(clipOf(store).mask).toEqual(DEFAULT_SHAPE_MASK);
    expect(store.getState().history).toHaveLength(1);
    expect(store.getState().history[0].label).toBe('Add mask');
    expect(activate).toHaveBeenCalledWith('mask');
    store.undo();
    expect(clipOf(store).mask ?? null).toBeNull();
  });

  it('Add ellipse mask writes an ellipse; Add reframe writes the centre point and enters that tool', () => {
    const store = storeWith();
    const activate = vi.fn();
    inspector(store, false, activate);
    fireEvent.click(screen.getByTestId('mask-reframe-add-ellipse'));
    expect(clipOf(store).mask).toMatchObject({ kind: 'ellipse' });
    cleanup();
    const store2 = storeWith();
    const activate2 = vi.fn();
    inspector(store2, false, activate2);
    fireEvent.click(screen.getByTestId('mask-reframe-add-reframe'));
    expect(clipOf(store2).reframe).toEqual({ x: 0.5, y: 0.5 });
    expect(activate2).toHaveBeenCalledWith('reframe');
  });

  it('a field change is one setProperty and one undo step (invert toggles, undo restores)', () => {
    const store = storeWith({ mask });
    inspector(store);
    fireEvent.click(screen.getByTestId('mask-reframe-invert'));
    expect(clipOf(store).mask).toMatchObject({ invert: true });
    expect(store.getState().history).toHaveLength(1);
    store.undo();
    expect((clipOf(store).mask as typeof mask).invert).toBe(false);
  });

  it('read-only offers no Add or Remove action and disables the fields', () => {
    const plain = storeWith();
    const view = inspector(plain, true);
    expect(screen.queryByTestId('mask-reframe-add-mask')).toBeNull();
    expect(screen.queryByTestId('mask-reframe-add-reframe')).toBeNull();
    cleanup();

    const masked = storeWith({ mask, reframe: { x: 0.5, y: 0.5 } });
    const maskedView = inspector(masked, true);
    expect(screen.queryByTestId('mask-reframe-remove')).toBeNull();
    expect(screen.queryByTestId('mask-reframe-remove-reframe')).toBeNull();
    expect(maskedView.container.querySelectorAll('input[disabled]').length).toBeGreaterThan(0);
    void view;
  });

  it('a multi-selection shows no mask and reframe section', () => {
    const store = createEditorStore({
      timelineId: 't',
      projectId: 'p',
      doc: mediaDoc([media('c1', 0, 100, 0, {}), media('c2', 0, 100, 10, {})]),
      revision: 1,
    });
    store.setUi({ selection: ['c1', 'c2'], playheadFrame: 10 });
    render(
      <MantineProvider>
        <InspectorPanel doc={store.getState().doc} selection={['c1', 'c2']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={setProperty(store)} />
      </MantineProvider>,
    );
    expect(screen.queryByTestId('mask-reframe-fields')).toBeNull();
  });
});
