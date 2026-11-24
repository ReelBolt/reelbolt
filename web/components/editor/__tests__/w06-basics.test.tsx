// @vitest-environment jsdom
/**
 * W0.6 "missing basics", rendered: the Select and Add menus (P3-SEL-03, P5-GEN-03) and the other
 * controls the audit found missing or broken.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
const api = vi.hoisted(() => ({
  createEditorPreset: vi.fn(async () => ({})),
  deleteEditorPreset: vi.fn(async () => undefined),
}));
const hooks = vi.hoisted(() => ({
  presets: [] as unknown[],
  mutate: vi.fn(async () => undefined),
}));
vi.mock('@/lib/editing/api/editor-p9', async (importOriginal) => ({ ...(await importOriginal<object>()), ...api }));
vi.mock('@/lib/hooks/use-editor-p9', () => ({
  useEditorPresets: () => ({ data: hooks.presets, mutate: hooks.mutate }),
  useReviewComments: () => ({ data: [], mutate: hooks.mutate, isLoading: false, error: undefined }),
  useEditorPreferences: () => ({ data: undefined, mutate: hooks.mutate }),
}));

import { createEditorStore } from '@/lib/editing/store';
import type { EditOp } from '@/lib/editing/engine';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { AddAtPlayheadMenu, SelectMenu } from '../timeline/TimelineMenus';
import { CurveEditor } from '../color/CurveEditor';
import { LiveSlider } from '../shell/LiveSlider';
import { KeymapEditor } from '../prefs/KeymapEditor';
import { PresetPicker } from '../presets/PresetPicker';
import type { GradeCurves } from '@/lib/editing/types';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
// jsdom has no PointerEvent: a MouseEvent carrying pointerId/pointerType stands in, so clientX survives.
if (typeof window.PointerEvent === 'undefined') {
  class PointerEventStub extends MouseEvent {
    pointerId: number;
    pointerType: string;
    constructor(type: string, init: PointerEventInit = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
      this.pointerType = init.pointerType ?? 'mouse';
    }
  }
  (window as unknown as { PointerEvent: unknown }).PointerEvent = PointerEventStub;
}

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
const makeStore = () => createEditorStore({ timelineId: 'timeline-1', projectId: 'project-1', doc: standardDoc(), revision: 1 });
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('Select menu (P3-SEL-03)', () => {
  it('selects forward, backward and all on the track from a menu', async () => {
    const store = makeStore();
    store.setUi({ playheadFrame: 100 });
    wrap(<SelectMenu store={store} />);
    fireEvent.click(screen.getByTestId('select-menu'));
    fireEvent.click(await screen.findByTestId('select-menu-forward'));
    expect(store.getState().ui.selection).toEqual(expect.arrayContaining(['c2', 'c3']));
    expect(store.getState().ui.selection).not.toContain('c1');

    fireEvent.click(screen.getByTestId('select-menu'));
    fireEvent.click(await screen.findByTestId('select-menu-backward'));
    expect(store.getState().ui.selection).not.toContain('c3');

    store.setUi({ selection: ['c2'] });
    fireEvent.click(screen.getByTestId('select-menu'));
    fireEvent.click(await screen.findByTestId('select-menu-track'));
    expect(store.getState().ui.selection).toEqual(['c1', 'c2', 'c3']);
  });
});

describe('Add menu (P5-GEN-03)', () => {
  it('adds a title and a colour card at the playhead, each one undo step, and selects it', async () => {
    const store = makeStore();
    store.setUi({ playheadFrame: 100 });
    const onApply = vi.fn((ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label));
    wrap(<AddAtPlayheadMenu store={store} onApply={onApply} />);

    fireEvent.click(screen.getByTestId('add-menu'));
    fireEvent.click(await screen.findByTestId('add-title'));
    expect(onApply).toHaveBeenLastCalledWith(expect.any(Array), 'Add title');
    const [titleId] = store.getState().ui.selection;
    const title = store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === titleId);
    expect(title).toMatchObject({ type: 'titleClip', recordIn: 100 });

    fireEvent.click(screen.getByTestId('add-menu'));
    fireEvent.click(await screen.findByTestId('add-colour-card'));
    const [cardId] = store.getState().ui.selection;
    const card = store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === cardId);
    expect(card).toMatchObject({ type: 'generatorClip', generatorType: 'color', recordIn: 100 });
    expect(store.getState().history).toHaveLength(2);
  });

  it('is disabled on a read-only timeline', async () => {
    wrap(<AddAtPlayheadMenu store={makeStore()} disabled onApply={vi.fn()} />);
    await waitFor(() => expect((screen.getByTestId('add-menu') as HTMLButtonElement).disabled).toBe(true));
  });
});

describe('Curve editor (P7-GRADE-06, UI2-04)', () => {
  const rect = () => ({ left: 0, top: 0, width: 180, height: 180, right: 180, bottom: 180, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;

  it('a drag of many moves is one write, by pointer events (touch included)', () => {
    const writes: (GradeCurves | null)[] = [];
    wrap(<CurveEditor curves={{ y: [{ x: 0.5, y: 0.5 }] }} readOnly={false} onChange={(c) => writes.push(c)} />);
    const svg = screen.getByTestId('curve-svg');
    svg.getBoundingClientRect = rect;
    fireEvent.pointerDown(svg, { clientX: 90, clientY: 90, pointerId: 1, pointerType: 'touch' });
    for (let i = 1; i <= 6; i++) fireEvent.pointerMove(svg, { clientX: 90, clientY: 90 - i * 5, pointerId: 1, pointerType: 'touch' });
    // The point follows the finger before anything is committed.
    expect(Number(screen.getByTestId('curve-point-0').getAttribute('cy'))).toBeCloseTo(60, 0);
    expect(writes).toHaveLength(0);
    fireEvent.pointerUp(svg, { clientX: 90, clientY: 60, pointerId: 1, pointerType: 'touch' });
    expect(writes).toHaveLength(1);
    expect(writes[0]?.y?.[0].y).toBeCloseTo(1 - 60 / 180, 3);
  });

  it('a cancelled drag writes nothing', () => {
    const writes: (GradeCurves | null)[] = [];
    wrap(<CurveEditor curves={{ y: [{ x: 0.5, y: 0.5 }] }} readOnly={false} onChange={(c) => writes.push(c)} />);
    const svg = screen.getByTestId('curve-svg');
    svg.getBoundingClientRect = rect;
    fireEvent.pointerDown(svg, { clientX: 90, clientY: 90, pointerId: 1 });
    fireEvent.pointerMove(svg, { clientX: 90, clientY: 40, pointerId: 1 });
    fireEvent.pointerCancel(svg, { pointerId: 1 });
    expect(writes).toHaveLength(0);
    expect(Number(screen.getByTestId('curve-point-0').getAttribute('cy'))).toBeCloseTo(90, 0);
  });
});

describe('LiveSlider (UI2-02)', () => {
  it('moves while dragging (value held by the parent) and commits once on release', async () => {
    const commits: number[] = [];
    const previews: number[] = [];
    const spy = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, width: 100, height: 10, right: 100, bottom: 10, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    try {
      wrap(<LiveSlider min={0} max={100} step={1} value={10} onChange={(v) => previews.push(v)} onChangeEnd={(v) => commits.push(v)} data-testid="live" />);
      const thumb = screen.getByRole('slider');
      // useMove listens on the track container (the thumb's grandparent).
      const container = thumb.parentElement!.parentElement!;
      fireEvent.mouseDown(container, { clientX: 40, clientY: 5 });
      fireEvent.mouseMove(document, { clientX: 70, clientY: 5 });
      await waitFor(() => expect(thumb.getAttribute('aria-valuenow')).toBe('70'));
      expect(commits).toEqual([]);
      expect(previews.length).toBeGreaterThan(0);
      fireEvent.mouseUp(document);
      await waitFor(() => expect(commits).toEqual([70]));
      // The parent never took the value: the slider shows the parent's value again.
      await waitFor(() => expect(thumb.getAttribute('aria-valuenow')).toBe('10'));
    } finally {
      spy.mockRestore();
    }
  });
});

describe('Keymap save failure (UI2-08)', () => {
  it('keeps the dialog open with the reason', async () => {
    const onClose = vi.fn();
    const onSave = vi.fn(async () => {
      throw new Error('Your saved preferences have not loaded yet. Try again in a moment.');
    });
    wrap(<KeymapEditor opened onClose={onClose} overrides={{}} onSave={onSave} />);
    fireEvent.click(await screen.findByTestId('keymap-save'));
    await waitFor(() => expect(screen.getByTestId('keymap-save-error').textContent).toMatch(/not loaded yet/));
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('Preset delete (UI2-14)', () => {
  it('asks before deleting, and Keep deletes nothing', async () => {
    hooks.presets = [{ id: 'p1', name: 'Warm look', kind: 'effect', payload: {} }];
    wrap(<PresetPicker projectId="proj" kind="effect" current={null} readOnly={false} onApply={vi.fn()} testId="fx" />);
    fireEvent.click(screen.getByTestId('fx-delete'));
    fireEvent.click(await screen.findByRole('option', { name: 'Warm look' }));
    expect(api.deleteEditorPreset).not.toHaveBeenCalled();
    expect(screen.getByTestId('fx-delete-prompt').textContent).toMatch(/Delete “Warm look”\?/);
    fireEvent.click(screen.getByTestId('fx-delete-cancel'));
    expect(screen.queryByTestId('fx-delete-prompt')).toBeNull();
    expect(api.deleteEditorPreset).not.toHaveBeenCalled();
  });

  it('Delete in the prompt deletes the preset', async () => {
    hooks.presets = [{ id: 'p1', name: 'Warm look', kind: 'effect', payload: {} }];
    wrap(<PresetPicker projectId="proj" kind="effect" current={null} readOnly={false} onApply={vi.fn()} testId="fx" />);
    fireEvent.click(screen.getByTestId('fx-delete'));
    fireEvent.click(await screen.findByRole('option', { name: 'Warm look' }));
    fireEvent.click(screen.getByTestId('fx-delete-confirm'));
    await waitFor(() => expect(api.deleteEditorPreset).toHaveBeenCalledWith('proj', 'p1'));
  });

  it('a failed delete says why', async () => {
    hooks.presets = [{ id: 'p1', name: 'Warm look', kind: 'effect', payload: {} }];
    api.deleteEditorPreset.mockRejectedValueOnce(new Error('You need edit access to delete presets.'));
    wrap(<PresetPicker projectId="proj" kind="effect" current={null} readOnly={false} onApply={vi.fn()} testId="fx" />);
    fireEvent.click(screen.getByTestId('fx-delete'));
    fireEvent.click(await screen.findByRole('option', { name: 'Warm look' }));
    fireEvent.click(screen.getByTestId('fx-delete-confirm'));
    await waitFor(() => expect(screen.getByText('You need edit access to delete presets.')).toBeTruthy());
  });
});
