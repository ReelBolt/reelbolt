// @vitest-environment jsdom
/**
 * Colour depth (pro UI wave 4) against a real store: rename, reorder, duplicate and reset each write the
 * whole stack as ONE op; solo never writes an op, previews through the transient grade preview and ends on
 * Escape, a new target and leaving the page; the serial graph lists the chain in the order of
 * `effectiveGrade` and selects like the list; before / after are first-class controls.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, useEditorStore, type EditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { ColorGroup, EditTimeline, GradeNode, GradeStack, MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { effectiveGrade } from '@/lib/editing/color/grade';
import { GradePreviewContext, type GradePreview } from '@/lib/editing/color/grade-preview';
import { resetColourPagePrefsForTests } from '@/lib/editing/color/colour-page-prefs';
import { ColorGradeToolsPanel, ColorNodesPanel, ColorPageProvider, ColorSoloBanner, type ColorWorkspaceProps } from '../ColorWorkspace';
import { CompareBar } from '../CompareBar';
import { GradeGraph } from '../GradeGraph';
import { gradeChain } from '@/lib/editing/color/grade-nodes';

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

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/editing/api/analysis', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/analysis')>()), useTimelineAnalysis: () => ({ data: undefined, isLoading: false, error: undefined }) }));

// Every test mounts the whole Colour page (both panels, real Mantine): on a busy machine one can pass 5 s.
vi.setConfig({ testTimeout: 20_000 });

beforeEach(() => {
  localStorage.clear();
  resetColourPagePrefsForTests();
});
afterEach(cleanup);

const node = (id: string, extra: Partial<GradeNode> = {}): GradeNode => ({ id, name: id, enabled: true, primaries: {}, curves: null, lut: null, ...extra });
const threeNodes = (): GradeStack => ({ enabled: true, nodes: [node('n1', { name: 'Balance', primaries: { exposure: 0.5 } }), node('n2', { name: 'Look' }), node('n3', { name: 'Shot' })] });

function setup(document: EditTimeline) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  let n = 0;
  const previews: Array<GradePreview | null> = [];
  const base = (selection: string[]): Omit<ColorWorkspaceProps, 'doc'> => ({
    projectId: 'p',
    seedStepResultId: null,
    readOnly: false,
    selection,
    playheadFrame: 0,
    newId: () => `new-${++n}`,
    onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => store.dispatch({ type: 'setProperty', itemId, key, value }, label),
    onOp: (op: EditOp, label?: string) => store.dispatch(op, label),
    onApply: (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label),
    onSelect: vi.fn(),
    onSeek: vi.fn(),
    gradeBypass: false,
    onGradeBypass: vi.fn(),
    getCanvas: () => null,
    gallery: [],
    onCaptureStill: vi.fn(async () => {}),
    onRemoveStill: vi.fn(),
    referenceId: null,
    onReference: vi.fn(),
    wipe: 0.5,
    onWipe: vi.fn(),
  });
  return { store, base, previews };
}

/** The page over the live store document, with the transient preview channel recorded. */
function Page({ store, previews, props }: { store: EditorStore; previews: Array<GradePreview | null>; props: Omit<ColorWorkspaceProps, 'doc'> }) {
  const live = useEditorStore(store, (s) => s.doc);
  const value = React.useMemo(() => ({ set: (preview: GradePreview | null) => void previews.push(preview) }), [previews]);
  return (
    <MantineProvider>
      <GradePreviewContext.Provider value={value}>
        {/* The page as the editor mounts it: one provider over the viewer zone's banner and the two panels. */}
        <ColorPageProvider {...props} doc={live}>
          <ColorSoloBanner />
          <ColorNodesPanel />
          <ColorGradeToolsPanel />
        </ColorPageProvider>
      </GradePreviewContext.Provider>
    </MantineProvider>
  );
}

const clipOf = (store: EditorStore, id: string) => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;
const namesOf = (store: EditorStore, id = 'c1') => (clipOf(store, id).grade?.nodes ?? []).map((n) => n.name);
const gradedDoc = (grade: GradeStack | null = threeNodes()) => doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0, { grade }), media('c2', 60, 120, 0)])]);
const last = <T,>(items: T[]): T => items[items.length - 1];

describe('node list: rename', () => {
  it('renames in place on Enter as one undo step, trimmed', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.doubleClick(screen.getByTestId('grade-node-select-1'));
    const input = screen.getByTestId('grade-node-name-input-1') as HTMLInputElement;
    expect(input.value).toBe('Look');
    fireEvent.change(input, { target: { value: '  Teal   shadows ' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    fireEvent.blur(input);
    expect(namesOf(s.store)).toEqual(['Balance', 'Teal shadows', 'Shot']);
    expect(s.store.getState().history).toHaveLength(1);
    expect(s.store.getState().history[0].label).toBe('c1: rename correction');
    expect(screen.getByTestId('grade-node-select-1').textContent).toBe('Teal shadows');
    s.store.undo();
    expect(namesOf(s.store)).toEqual(['Balance', 'Look', 'Shot']);
  });

  it('Escape cancels, and an empty or unchanged name writes nothing', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-select-0'));
    fireEvent.click(screen.getByTestId('grade-node-rename'));
    fireEvent.change(screen.getByTestId('grade-node-name-input-0'), { target: { value: 'Other' } });
    fireEvent.keyDown(screen.getByTestId('grade-node-name-input-0'), { key: 'Escape' });
    expect(screen.queryByTestId('grade-node-name-input-0')).toBeNull();
    fireEvent.keyDown(screen.getByTestId('grade-node-select-0'), { key: 'F2' });
    fireEvent.change(screen.getByTestId('grade-node-name-input-0'), { target: { value: '   ' } });
    fireEvent.blur(screen.getByTestId('grade-node-name-input-0'));
    fireEvent.doubleClick(screen.getByTestId('grade-node-select-0'));
    fireEvent.blur(screen.getByTestId('grade-node-name-input-0'));
    expect(s.store.getState().history).toHaveLength(0);
    expect(namesOf(s.store)).toEqual(['Balance', 'Look', 'Shot']);
  });
});

describe('node list: reorder', () => {
  it('Move down and Move up write the reordered stack, one op each', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-select-0'));
    expect((screen.getByTestId('grade-node-up') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('grade-node-down'));
    expect(namesOf(s.store)).toEqual(['Look', 'Balance', 'Shot']);
    expect(s.store.getState().history).toHaveLength(1);
    expect(s.store.getState().history[0].label).toBe('c1: move Balance');
    // The selection followed the node, so the next move acts on it again.
    fireEvent.click(screen.getByTestId('grade-node-down'));
    expect(namesOf(s.store)).toEqual(['Look', 'Shot', 'Balance']);
    expect((screen.getByTestId('grade-node-down') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('grade-node-up'));
    expect(namesOf(s.store)).toEqual(['Look', 'Balance', 'Shot']);
    expect(s.store.getState().history).toHaveLength(3);
  });

  it('the grip moves a node with the keyboard: Space, arrows, Space is one op; Escape cancels', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    const grip = screen.getByTestId('grade-node-handle-0');
    expect(grip.getAttribute('aria-label')).toBe('Reorder Balance');
    fireEvent.keyDown(grip, { key: ' ' });
    expect(grip.getAttribute('aria-pressed')).toBe('true');
    fireEvent.keyDown(grip, { key: 'ArrowDown' });
    fireEvent.keyDown(grip, { key: 'ArrowDown' });
    expect(screen.getByTestId('grade-node-announce').textContent).toBe('Position 3 of 3.');
    // Nothing is written until the drop.
    expect(s.store.getState().history).toHaveLength(0);
    fireEvent.keyDown(grip, { key: ' ' });
    expect(namesOf(s.store)).toEqual(['Look', 'Shot', 'Balance']);
    expect(s.store.getState().history).toHaveLength(1);

    const again = screen.getByTestId('grade-node-handle-0');
    fireEvent.keyDown(again, { key: 'Enter' });
    fireEvent.keyDown(again, { key: 'ArrowDown' });
    fireEvent.keyDown(again, { key: 'Escape' });
    expect(screen.getByTestId('grade-node-announce').textContent).toBe('Move cancelled.');
    expect(s.store.getState().history).toHaveLength(1);
  });

  it('a pointer drag onto another row is one op', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    const dataTransfer = { setData: vi.fn(), effectAllowed: '', dropEffect: '' };
    fireEvent.dragStart(screen.getByTestId('grade-node-2'), { dataTransfer });
    fireEvent.dragOver(screen.getByTestId('grade-node-0'), { dataTransfer });
    expect(screen.getByTestId('grade-node-0').getAttribute('data-drop')).toBe('before');
    fireEvent.drop(screen.getByTestId('grade-node-0'), { dataTransfer });
    expect(namesOf(s.store)).toEqual(['Shot', 'Balance', 'Look']);
    expect(s.store.getState().history).toHaveLength(1);
  });
});

describe('node list: duplicate, reset, read-only', () => {
  it('duplicates the selected node right after it as one op and selects the copy', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-select-0'));
    fireEvent.click(screen.getByTestId('grade-node-duplicate'));
    const nodes = clipOf(s.store, 'c1').grade?.nodes ?? [];
    expect(nodes.map((n) => n.name)).toEqual(['Balance', 'Balance 2', 'Look', 'Shot']);
    expect(nodes[1]).toMatchObject({ id: 'new-1', primaries: { exposure: 0.5 } });
    expect(s.store.getState().history).toHaveLength(1);
    expect(s.store.getState().history[0].label).toBe('c1: duplicate Balance');
    expect(screen.getByTestId('grade-node-1').getAttribute('data-selected')).toBe('true');
  });

  it('resets the selected node to identity, keeping its name, as one op', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-select-1'));
    // A node that changes nothing has nothing to reset.
    expect((screen.getByTestId('grade-node-reset') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('grade-node-select-0'));
    fireEvent.click(screen.getByTestId('grade-node-reset'));
    expect(clipOf(s.store, 'c1').grade?.nodes?.[0]).toEqual({ id: 'n1', name: 'Balance', enabled: true, primaries: {}, curves: null, lut: null });
    expect(s.store.getState().history).toHaveLength(1);
    expect(s.store.getState().history[0].label).toBe('c1: reset Balance');
  });

  it('a read-only timeline disables every write control but still lets a node be soloed', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={{ ...s.base(['c1']), readOnly: true }} />);
    fireEvent.click(screen.getByTestId('grade-node-select-0'));
    for (const id of ['grade-node-rename', 'grade-node-up', 'grade-node-down', 'grade-node-duplicate', 'grade-node-reset', 'grade-node-handle-0', 'grade-node-remove-0', 'grade-add-node']) {
      expect((screen.getByTestId(id) as HTMLButtonElement).disabled, id).toBe(true);
    }
    expect((screen.getByTestId('grade-node-enabled-0') as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByTestId('grade-node-0').getAttribute('draggable')).toBe('false');
    fireEvent.doubleClick(screen.getByTestId('grade-node-select-0'));
    expect(screen.queryByTestId('grade-node-name-input-0')).toBeNull();
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    expect(screen.getByTestId('color-solo')).toBeTruthy();
    expect(s.store.getState().history).toHaveLength(0);
  });
});

describe('solo', () => {
  it('is view state: no op, the preview shows only that node, and it is marked on the node, the graph and above the tools', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-solo-1'));
    expect(s.store.getState().history).toHaveLength(0);
    expect(clipOf(s.store, 'c1').grade).toEqual(threeNodes());
    const preview = last(s.previews);
    expect(preview?.target).toEqual({ kind: 'item', id: 'c1' });
    expect(preview?.stack?.nodes?.map((n) => [n.id, n.enabled])).toEqual([['n1', false], ['n2', true], ['n3', false]]);
    expect(preview?.also).toEqual([]);
    expect(screen.getByTestId('grade-node-1').getAttribute('data-solo')).toBe('true');
    expect(screen.getByTestId('grade-node-solo-1').getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByTestId('grade-graph-node-2').getAttribute('data-solo')).toBe('true');
    expect(screen.getByTestId('color-solo').textContent).toContain('Solo: Look');
    expect(screen.getByTestId('color-solo-viewer').textContent).toContain('Solo: Look');
  });

  it('clears on Escape and on the Leave button', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByTestId('color-solo')).toBeNull();
    expect(last(s.previews)).toBeNull();
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    expect(last(s.previews)).not.toBeNull();
    fireEvent.click(screen.getByTestId('color-solo-leave'));
    expect(screen.queryByTestId('color-solo')).toBeNull();
    expect(screen.getByTestId('grade-node-0').getAttribute('data-solo')).toBe('false');
    expect(last(s.previews)).toBeNull();
    expect(s.store.getState().history).toHaveLength(0);
  });

  it('Escape inside the rename field cancels the rename and keeps the solo', () => {
    const s = setup(gradedDoc());
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    fireEvent.doubleClick(screen.getByTestId('grade-node-select-0'));
    fireEvent.keyDown(screen.getByTestId('grade-node-name-input-0'), { key: 'Escape' });
    expect(screen.queryByTestId('grade-node-name-input-0')).toBeNull();
    expect(screen.getByTestId('color-solo')).toBeTruthy();
  });

  it('clears when another clip is selected and does not come back with the first clip', () => {
    const s = setup(gradedDoc());
    const view = render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    view.rerender(<Page store={s.store} previews={s.previews} props={s.base(['c2'])} />);
    expect(screen.queryByTestId('color-solo')).toBeNull();
    expect(last(s.previews)).toBeNull();
    view.rerender(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    expect(screen.queryByTestId('color-solo')).toBeNull();
    expect(screen.getByTestId('grade-node-0').getAttribute('data-solo')).toBe('false');
  });

  it('clears when the target changes to a colour group, and when the soloed node is removed', () => {
    const group: ColorGroup = { id: 'g1', name: 'Day', clipIds: ['c1'], grade: { enabled: true, nodes: [node('g-a', { name: 'Group base' })] } };
    const s = setup({ ...gradedDoc(), colorGroups: [group] });
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    // Soloing a clip node also switches the clip's group stack off in the same preview.
    expect(last(s.previews)?.also).toEqual([{ target: { kind: 'group', id: 'g1' }, stack: { ...group.grade, enabled: false } }]);
    // Selecting the group's node in the graph makes the group the target: the solo ends.
    fireEvent.click(screen.getByTestId('grade-graph-node-1'));
    expect(screen.queryByTestId('color-solo')).toBeNull();
    expect(last(s.previews)).toBeNull();

    // Back on the clip: solo a node, then remove it.
    fireEvent.click(screen.getByTestId('grade-graph-node-2'));
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    expect(screen.getByTestId('color-solo')).toBeTruthy();
    fireEvent.click(screen.getByTestId('grade-node-remove-0'));
    expect(screen.queryByTestId('color-solo')).toBeNull();
    expect(last(s.previews)).toBeNull();
  });

  it('soloing a group node switches the selected member clip’s own stack off', () => {
    const group: ColorGroup = { id: 'g1', name: 'Day', clipIds: ['c1'], grade: { enabled: true, nodes: [node('g-a', { name: 'Group base' })] } };
    const s = setup({ ...gradedDoc(), colorGroups: [group] });
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-graph-node-1'));
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    const preview = last(s.previews);
    expect(preview?.target).toEqual({ kind: 'group', id: 'g1' });
    expect(preview?.also).toEqual([{ target: { kind: 'item', id: 'c1' }, stack: { ...threeNodes(), enabled: false } }]);
    expect(s.store.getState().history).toHaveLength(0);
  });

  it('a wheel drag while a node is soloed previews through the solo, and the solo is back on the stored stack after release', () => {
    const queued: FrameRequestCallback[] = [];
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => queued.push(cb));
    vi.stubGlobal('cancelAnimationFrame', () => undefined);
    try {
      const s = setup(gradedDoc());
      render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
      fireEvent.click(screen.getByTestId('grade-node-solo-1'));
      const disc = screen.getByTestId('grade-wheel-gain-disc');
      Object.defineProperty(disc, 'getBoundingClientRect', { value: () => ({ left: 0, top: 0, width: 100, height: 100, right: 100, bottom: 100, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect, configurable: true });
      fireEvent.pointerDown(disc, { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
      fireEvent.pointerMove(disc, { pointerId: 1, clientX: 95, clientY: 50 });
      act(() => {
        for (const cb of queued.splice(0)) cb(0);
      });
      const dragging = last(s.previews);
      expect(dragging?.stack?.nodes?.map((n) => n.enabled)).toEqual([false, true, false]);
      expect(dragging?.stack?.nodes?.[1].primaries?.gain).toBeTruthy();
      expect(s.store.getState().history).toHaveLength(0);
      fireEvent.pointerUp(disc, { pointerId: 1, clientX: 95, clientY: 50 });
      // One write, and the solo holds on the document as stored.
      expect(s.store.getState().history).toHaveLength(1);
      const settled = last(s.previews);
      expect(settled?.stack?.nodes?.map((n) => n.enabled)).toEqual([false, true, false]);
      expect(settled?.stack?.nodes?.[1].primaries).toEqual(clipOf(s.store, 'c1').grade?.nodes?.[1].primaries);
      expect(clipOf(s.store, 'c1').grade?.nodes?.map((n) => n.enabled)).toEqual([true, true, true]);
      expect(screen.getByTestId('color-solo')).toBeTruthy();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('survives an edit (the preview is published again for the new document) and leaves nothing behind on unmount', () => {
    const s = setup(gradedDoc());
    const view = render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-node-solo-0'));
    fireEvent.click(screen.getByTestId('grade-node-select-2'));
    fireEvent.click(screen.getByTestId('grade-node-up'));
    expect(namesOf(s.store)).toEqual(['Balance', 'Shot', 'Look']);
    expect(screen.getByTestId('color-solo').textContent).toContain('Solo: Balance');
    expect(last(s.previews)?.stack?.nodes?.map((n) => [n.id, n.enabled])).toEqual([['n1', true], ['n3', false], ['n2', false]]);
    view.unmount();
    expect(last(s.previews)).toBeNull();
  });
});

describe('serial grade graph', () => {
  const group: ColorGroup = { id: 'g1', name: 'Day', clipIds: ['c1'], grade: { enabled: true, nodes: [node('g-a', { name: 'Group base' }), node('g-b', { name: 'Group look', enabled: false })] } };
  const rich = (): GradeStack => ({
    enabled: true,
    nodes: [
      node('n1', { name: 'Balance', primaries: { exposure: 0.5 }, curves: { y: [{ x: 0, y: 0 }, { x: 1, y: 0.9 }] } }),
      node('n2', { name: 'Skin', lut: { file: { kind: 'projectFile', projectFileId: 'f1' }, intensity: 1 }, qualifier: { hueCenter: 20, hueWidth: 20 }, window: { shape: { kind: 'ellipse' } } }),
    ],
  });

  it('draws Input, the group stack, the clip stack and Output in the order of effectiveGrade', () => {
    const d = { ...gradedDoc(rich()), colorGroups: [group] };
    const s = setup(d);
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    const graph = screen.getByTestId('grade-graph');
    const options = within(graph).getAllByRole('option');
    const expected = effectiveGrade(d, clipOf(s.store, 'c1')).flatMap((stack) => stack.nodes ?? []).map((n) => n.name);
    expect(options.map((o) => o.getAttribute('title'))).toEqual(expected);
    expect(expected).toEqual(['Group base', 'Group look', 'Balance', 'Skin']);
    expect(within(graph).getByTestId('grade-graph-input').textContent).toBe('In');
    expect(within(graph).getByTestId('grade-graph-output').textContent).toBe('Out');
    expect(within(graph).getByTestId('grade-graph-segment-group').getAttribute('aria-label')).toBe('Group: Day');
    // A correction that is off is drawn hollow; each node says what it carries.
    expect(options.map((o) => o.getAttribute('data-enabled'))).toEqual(['true', 'false', 'true', 'true']);
    const glyphs = (o: HTMLElement) => Array.from(o.querySelectorAll('[data-glyph]')).map((g) => g.getAttribute('data-glyph'));
    expect(glyphs(options[2])).toEqual(['primaries', 'curves']);
    expect(glyphs(options[3])).toEqual(['lut', 'qualifier', 'window']);
    expect(options[3].getAttribute('aria-label')).toBe('4: Skin, on. LUT, qualifier, window.');
    expect(options[1].getAttribute('aria-label')).toBe('2: Group look, off. no change yet.');
  });

  it('a click selects the same node as the list, and a group node makes the group the target', () => {
    const s = setup({ ...gradedDoc(rich()), colorGroups: [group] });
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-graph-node-4'));
    expect(screen.getByTestId('grade-graph-node-4').getAttribute('aria-selected')).toBe('true');
    expect(screen.getByTestId('grade-node-1').getAttribute('data-selected')).toBe('true');
    expect(screen.getByTestId('grade-node-select-1').textContent).toBe('Skin');
    fireEvent.click(screen.getByTestId('grade-graph-node-2'));
    expect((screen.getByTestId('color-target') as HTMLInputElement).value).toBe('Group: Day');
    expect(screen.getByTestId('grade-node-select-1').textContent).toBe('Group look');
    expect(screen.getByTestId('grade-node-1').getAttribute('data-selected')).toBe('true');
    // The chain is still the clip's whole chain.
    expect(within(screen.getByTestId('grade-graph')).getAllByRole('option')).toHaveLength(4);
    expect(s.store.getState().history).toHaveLength(0);
  });

  it('arrow keys move the selection and Enter moves focus to the grade tools', async () => {
    const s = setup(gradedDoc(rich()));
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    const first = screen.getByTestId('grade-graph-node-1');
    expect(first.getAttribute('tabindex')).toBe('0');
    expect(screen.getByTestId('grade-graph-node-2').getAttribute('tabindex')).toBe('-1');
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowRight' });
    const second = screen.getByTestId('grade-graph-node-2');
    expect(second.getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(second);
    fireEvent.keyDown(second, { key: 'ArrowLeft' });
    expect(screen.getByTestId('grade-graph-node-1').getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(screen.getByTestId('grade-graph-node-1'), { key: 'End' });
    expect(screen.getByTestId('grade-graph-node-2').getAttribute('aria-selected')).toBe('true');

    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    fireEvent.keyDown(screen.getByTestId('grade-graph-node-2'), { key: 'Enter' });
    await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId('color-tab-primaries')));
    expect(screen.getByTestId('color-tab-primaries').getAttribute('aria-selected')).toBe('true');
  });

  it('collapses into a band and remembers it on this device', () => {
    const s = setup(gradedDoc());
    const view = render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    expect(screen.getByTestId('grade-graph')).toBeTruthy();
    act(() => fireEvent.click(screen.getByTestId('color-graph-toggle')));
    expect(screen.queryByTestId('grade-graph')).toBeNull();
    expect(screen.getByTestId('grade-node-list')).toBeTruthy();
    view.unmount();
    render(<Page store={s.store} previews={s.previews} props={s.base(['c1'])} />);
    expect(screen.queryByTestId('grade-graph')).toBeNull();
    expect(screen.getByTestId('color-graph-toggle').getAttribute('aria-expanded')).toBe('false');
  });

  it('stands alone: an empty chain is just Input and Output', () => {
    const d = gradedDoc(null);
    const clip = d.tracks[0].items[0] as MediaClip;
    render(
      <MantineProvider>
        <GradeGraph segments={gradeChain(d, clip, null)} selectedKey={null} soloKey={null} onSelect={vi.fn()} />
      </MantineProvider>,
    );
    expect(screen.queryAllByRole('option')).toHaveLength(0);
    expect(screen.getByTestId('grade-graph-empty-clip').textContent).toBe('No corrections');
  });
});

describe('before / after as first-class controls', () => {
  it('sits above the grade tools on every tab: bypass, split, reference still', () => {
    const s = setup(gradedDoc());
    const props = s.base(['c1']);
    render(<Page store={s.store} previews={s.previews} props={props} />);
    for (const tab of ['primaries', 'curves', 'lut', 'secondaries', 'groups', 'gallery']) {
      fireEvent.click(screen.getByTestId(`color-tab-${tab}`));
      expect(screen.getByTestId('color-compare-bypass')).toBeTruthy();
      expect(screen.getByTestId('color-compare-split')).toBeTruthy();
      expect(screen.getByTestId('color-compare-reference')).toBeTruthy();
    }
    fireEvent.click(screen.getByTestId('color-compare-bypass'));
    expect(props.onGradeBypass).toHaveBeenCalledWith(true);
    fireEvent.click(screen.getByTestId('color-compare-grab'));
    expect(props.onCaptureStill).toHaveBeenCalledWith('c1');
    // View state only: nothing was written.
    expect(s.store.getState().history).toHaveLength(0);
  });

  it('does not show the look panel’s own brief bypass, and a choice made on the bar during it stays', async () => {
    vi.useFakeTimers();
    try {
      const s = setup(gradedDoc());
      const props = s.base(['c1']);
      const view = render(<Page store={s.store} previews={s.previews} props={props} />);
      // Opening the Gallery reads the ungraded frame: the panel bypasses the grade for a moment.
      fireEvent.click(screen.getByTestId('color-tab-gallery'));
      expect(props.onGradeBypass).toHaveBeenLastCalledWith(true);
      view.rerender(<Page store={s.store} previews={s.previews} props={{ ...props, gradeBypass: true }} />);
      expect(screen.getByTestId('color-compare-bypass').getAttribute('aria-pressed')).toBe('false');
      // The user turns the bypass on while that is still going: it is their choice now.
      fireEvent.click(screen.getByTestId('color-compare-bypass'));
      expect(props.onGradeBypass).toHaveBeenLastCalledWith(true);
      expect(screen.getByTestId('color-compare-bypass').getAttribute('aria-pressed')).toBe('true');
      await act(async () => {
        await vi.advanceTimersByTimeAsync(6000);
      });
      expect(vi.mocked(props.onGradeBypass).mock.calls.map((c) => c[0])).toEqual([true, true]);
      expect(screen.getByTestId('color-compare-bypass').getAttribute('aria-pressed')).toBe('true');
    } finally {
      vi.useRealTimers();
    }
  });

  it('the bypass toggle shows its state and turns back off', () => {
    const onGradeBypass = vi.fn();
    render(
      <MantineProvider>
        <CompareBar shownBypass onChooseBypass={onGradeBypass} gradeBypass onGradeBypass={vi.fn()} gallery={[]} referenceId={null} onReference={vi.fn()} wipe={0.5} onWipe={vi.fn()} onCaptureStill={vi.fn(async () => {})} stillLabel="Clip" />
      </MantineProvider>,
    );
    const bypass = screen.getByTestId('color-compare-bypass');
    expect(bypass.getAttribute('aria-pressed')).toBe('true');
    expect(bypass.textContent).toBe('Grade bypassed');
    fireEvent.click(bypass);
    expect(onGradeBypass).toHaveBeenCalledWith(false);
    // No stills yet: the picker says so and the wipe is not offered.
    expect((screen.getByTestId('color-compare-reference') as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByTestId('color-compare-wipe')).toBeNull();
  });

  it('Split captures the ungraded picture as the reference and restores the bypass; Close split drops the reference', async () => {
    const onGradeBypass = vi.fn();
    const onCaptureStill = vi.fn(async () => {});
    const onReference = vi.fn();
    const bar = (gallery: { id: string; label: string }[], referenceId: string | null) => (
      <MantineProvider>
        <CompareBar shownBypass={false} onChooseBypass={vi.fn()} gradeBypass={false} onGradeBypass={onGradeBypass} gallery={gallery} referenceId={referenceId} onReference={onReference} wipe={0.25} onWipe={vi.fn()} onCaptureStill={onCaptureStill} stillLabel="Clip" />
      </MantineProvider>
    );
    const view = render(bar([], null));
    fireEvent.click(screen.getByTestId('color-compare-split'));
    await waitFor(() => expect(onCaptureStill).toHaveBeenCalledWith('Before'), { timeout: 2000 });
    await waitFor(() => expect(onGradeBypass.mock.calls.map((c) => c[0])).toEqual([true, false]));

    view.rerender(bar([{ id: 's1', label: 'Before · 00:00:00:00' }], 's1'));
    const split = screen.getByTestId('color-compare-split');
    expect(split.getAttribute('aria-pressed')).toBe('true');
    expect(split.textContent).toBe('Close split');
    expect(screen.getByTestId('color-compare-wipe')).toBeTruthy();
    fireEvent.click(split);
    expect(onReference).toHaveBeenCalledWith(null);
  });
});
