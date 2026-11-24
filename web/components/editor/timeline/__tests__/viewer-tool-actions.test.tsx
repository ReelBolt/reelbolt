// @vitest-environment jsdom
/**
 * T-005: the shared clip action list gains Add mask (rectangle / ellipse) and Add reframe. They belong
 * to exactly one media clip, are unavailable in read-only (with the read-only reason) and for any other
 * selection, and running one writes the existing default, selects the clip, pauses playback, enters the
 * matching viewer tool and never forces a page change.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, act, renderHook } from '@testing-library/react';
import { useTimelineEditing } from '../useTimelineEditing';
import { createEditorStore } from '@/lib/editing/store';
import type { EditTimeline, Item, MediaClip } from '@/lib/editing/types';
import { doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

afterEach(cleanup);

function setup({ readOnly = false, selection = ['c1'], items }: { readOnly?: boolean; selection?: string[]; items?: Item[] } = {}) {
  const document: EditTimeline = doc([track(TRACKS.v1, 'video', items ?? [media('c1', 0, 100, 0, {})])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  store.setUi({ selection, playheadFrame: 10, playbackSpeed: 1, workspace: 'edit' });
  const openInspector = vi.fn();
  const { result } = renderHook(() => useTimelineEditing({ store, readOnly, doc: document, pool: undefined, split: () => {}, openInspector, openReplace: () => {} }));
  const item = (id: string) => result.current.items().find((i) => i.id === id)!;
  return { store, item, openInspector };
}

const clipOf = (store: ReturnType<typeof setup>['store']) => store.getState().doc.tracks[0].items[0] as MediaClip;

describe('the clip action list: masks and reframe', () => {
  it('Add mask writes the default rectangle, selects the clip, pauses and enters the mask tool', () => {
    const { store, item, openInspector } = setup();
    const add = item('add-mask');
    expect(add.unavailable).toBeUndefined();
    expect(add.label).toBe('Add mask');
    act(() => add.run());
    expect(clipOf(store).mask).toEqual({ kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.05, invert: false });
    expect(store.getState().ui.selection).toEqual(['c1']);
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(store.getState().ui.viewerTool).toBe('mask');
    expect(store.getState().ui.workspace).toBe('edit'); // never forces a page change
    expect(store.getState().history).toHaveLength(1);
    expect(openInspector).toHaveBeenCalledWith(false);
  });

  it('Add ellipse mask writes an ellipse; Add reframe writes the centre point and enters that tool', () => {
    const a = setup();
    act(() => a.item('add-ellipse-mask').run());
    expect(clipOf(a.store).mask).toMatchObject({ kind: 'ellipse', x: 0.5, y: 0.5 });
    cleanup();
    const b = setup();
    act(() => b.item('add-reframe').run());
    expect(clipOf(b.store).reframe).toEqual({ x: 0.5, y: 0.5 });
    expect(b.store.getState().ui.viewerTool).toBe('reframe');
    expect(b.store.getState().ui.playbackSpeed).toBe(0);
  });

  it('read-only offers neither Add action, with the read-only reason', () => {
    const { item } = setup({ readOnly: true });
    for (const id of ['add-mask', 'add-ellipse-mask', 'add-reframe']) {
      expect(item(id).unavailable).toBe('This timeline cannot be changed right now.');
    }
  });

  it('a multi-selection offers neither Add action and says to select one clip', () => {
    const { item } = setup({ selection: ['c1', 'c2'], items: [media('c1', 0, 100, 0, {}), media('c2', 0, 100, 10, {})] });
    expect(item('add-mask').unavailable).toBe('Select one clip.');
    expect(item('add-reframe').unavailable).toBe('Select one clip.');
  });

  it('a non-media clip (a title) offers neither Add action, with its reason', () => {
    const { item } = setup({ selection: ['t1'], items: [title('t1', 0, 100)] });
    expect(item('add-mask').unavailable).toBe('Masks and reframe need a single media clip.');
    expect(item('add-ellipse-mask').unavailable).toBe('Masks and reframe need a single media clip.');
  });
});
