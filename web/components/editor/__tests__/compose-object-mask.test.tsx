/** A segmentation (object) mask in the Compose panel: read-only, never replaced by the shape controls. */
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { ComposeHarness as ComposeWorkspace } from './compose-harness';

vi.mock('@xyflow/react', () => ({ ReactFlow: ({ children }: { children?: React.ReactNode }) => <div>{children}</div>, Background: () => null }));
vi.mock('@xyflow/react/dist/style.css', () => ({}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/editing/api/analysis', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/analysis')>()), useTimelineAnalysis: () => ({ data: undefined, isLoading: false, error: undefined }) }));
vi.mock('@/lib/hooks/use-segmentation-tracks', () => ({
  useSegmentationTracks: () => ({
    lookup: (id: string) => (id === 'a1' ? [{ trackId: 'g1', label: 'person', firstFrame: 0, lastFrame: 10, fps: 25, boxKeyframes: [] }] : undefined),
    version: 1,
  }),
}));

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

function setup(mask: unknown) {
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 0, 60, 0), mask } as unknown as MediaClip])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  const props = {
    doc: store.getState().doc,
    projectId: 'p',
    readOnly: false,
    selection: ['c1'],
    playheadFrame: 0,
    inOut: { inFrame: null, outFrame: null } as { inFrame: number | null; outFrame: number | null },
    newId: () => 'n',
    onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => store.dispatch({ type: 'setProperty', itemId, key, value }, label),
    onApply: (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label),
    onSelect: vi.fn(),
    onSeek: vi.fn(),
  };
  render(<MantineProvider><ComposeWorkspace {...props} /></MantineProvider>);
  return store;
}

describe('ComposeWorkspace with an object mask', () => {
  const seg = { kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1', 'g9'], invert: false, featherPx: 4 };

  it('hides the shape controls and shows which objects the mask follows (label, else track id)', () => {
    setup(seg);
    expect(screen.queryByTestId('mask-reframe-add-mask')).toBeNull();
    expect(screen.queryByTestId('mask-reframe-x')).toBeNull();
    expect(screen.getByTestId('mask-reframe-object').textContent).toContain('person, g9');
  });

  it('removes the mask only through the explicit action', () => {
    const store = setup(seg);
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).mask).toMatchObject({ kind: 'segmentation' });
    fireEvent.click(screen.getByTestId('mask-reframe-object-remove'));
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).mask).toBeNull();
  });

  it('a shape mask keeps its controls and no object-mask line', () => {
    setup({ kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5 });
    expect(screen.getByTestId('mask-reframe-kind')).toBeDefined();
    expect(screen.queryByTestId('mask-reframe-object')).toBeNull();
  });
});
