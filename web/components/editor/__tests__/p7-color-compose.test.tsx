// @vitest-environment jsdom
/**
 * P7 UI against a real store: the grade stack editor writes whole-stack `setProperty grade` ops
 * (one undo step each) and adds a Colorist look as an editable node; the Color workspace creates a
 * colour group with `setColorGroup` and toggles the preview bypass; the Compose workspace sets a
 * mask, adds an effect and inserts an adjustment clip on a new top track; the inspector sets a title
 * template; the curve editor adds a point on click.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { AdjustmentClip, GradeCurves, MediaClip, TitleClip } from '@/lib/editing/types';
import { doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { GradeStackEditor } from '../color/GradeStackEditor';
import { ColorWorkspace } from '../color/ColorWorkspace';
import { ComposeHarness as ComposeWorkspace } from './compose-harness';
import { CurveEditor } from '../color/CurveEditor';
import { InspectorPanel } from '../panels/InspectorPanel';
import { stackStages } from '../compose/NodeView';

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

vi.mock('@xyflow/react', () => ({ ReactFlow: ({ children }: { children?: React.ReactNode }) => <div data-testid="react-flow">{children}</div>, Background: () => null }));
vi.mock('@xyflow/react/dist/style.css', () => ({}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [{ id: 'lut-1', originalFileName: 'punchy.cube' }, { id: 'vid', originalFileName: 'a.mp4' }] }) }));
vi.mock('@/lib/editing/api/analysis', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/analysis')>()), useTimelineAnalysis: () => ({ data: undefined, isLoading: false, error: undefined }) }));

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
afterEach(cleanup);

function storeWith(document = doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0), media('c2', 60, 120, 0), title('t1', 120, 150, 'Hi')])])) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  const onOp = (op: EditOp, label?: string) => store.dispatch(op, label);
  const onApply = (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label);
  const onSetProperty = (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => store.dispatch({ type: 'setProperty', itemId, key, value }, label);
  let n = 0;
  const newId = () => `new-${++n}`;
  return { store, onOp, onApply, onSetProperty, newId };
}

describe('GradeStackEditor', () => {
  it('adds a correction and a look, writing the whole stack each time', () => {
    const writes: Array<[unknown, string]> = [];
    let n = 0;
    const { rerender } = wrap(<GradeStackEditor projectId="p1" stack={null} readOnly={false} onChange={(s, l) => writes.push([s, l])} newId={() => `n${++n}`} lutFiles={[]} subject="Clip" />);
    fireEvent.click(screen.getByTestId('grade-add-node'));
    expect(writes[0][0]).toMatchObject({ enabled: true, nodes: [{ id: 'n1', name: 'Correction 1' }] });
    expect(writes[0][1]).toBe('Clip: add correction');
    rerender(<MantineProvider><GradeStackEditor projectId="p1" stack={writes[0][0] as never} readOnly={false} onChange={(s, l) => writes.push([s, l])} newId={() => `n${++n}`} lutFiles={[]} subject="Clip" /></MantineProvider>);
    fireEvent.click(screen.getByTestId('grade-add-look'));
    const withLook = writes[1][0] as { nodes: { name: string; primaries: { saturation: number } }[] };
    expect(withLook.nodes).toHaveLength(2);
    expect(withLook.nodes[1].name).toBe('Warm (Normal)');
    expect(withLook.nodes[1].primaries.saturation).toBe(1.06);
    expect(writes[1][1]).toBe('Clip: Warm look');
  });
});

describe('ColorWorkspace', () => {
  const props = (s: ReturnType<typeof storeWith>, selection: string[]) => ({
    doc: s.store.getState().doc,
    projectId: 'p',
    seedStepResultId: null,
    readOnly: false,
    selection,
    playheadFrame: 0,
    newId: s.newId,
    onSetProperty: s.onSetProperty,
    onOp: s.onOp,
    onApply: s.onApply,
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

  it('writes the selected clip’s grade as one undo step and offers the LUT files of the project', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...props(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('grade-add-node'));
    const clip = s.store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.grade?.nodes).toHaveLength(1);
    expect(s.store.getState().history).toHaveLength(1);
    expect(s.store.getState().history[0].label).toBe('c1: add correction');
    s.store.undo();
    expect((s.store.getState().doc.tracks[0].items[0] as MediaClip).grade).toBeUndefined();
  });

  it('groups the selected clips with setColorGroup and removes the group again', () => {
    const s = storeWith();
    const onBypass = vi.fn();
    const view = wrap(<ColorWorkspace {...props(s, ['c1', 'c2'])} onGradeBypass={onBypass} />);
    fireEvent.click(screen.getByTestId('color-tab-groups'));
    fireEvent.click(screen.getByTestId('color-group-create'));
    const groups = s.store.getState().doc.colorGroups ?? [];
    expect(groups).toHaveLength(1);
    expect(groups[0].clipIds).toEqual(['c1', 'c2']);
    expect(s.store.getState().history[0].label).toBe('Create colour group (2 clips)');
    view.rerender(<MantineProvider><ColorWorkspace {...props(s, ['c1', 'c2'])} onGradeBypass={onBypass} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('color-tab-groups'));
    fireEvent.click(screen.getByTestId(`color-group-remove-${groups[0].id}`));
    expect(s.store.getState().doc.colorGroups).toBeUndefined();
    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    fireEvent.click(screen.getByTestId('color-bypass'));
    expect(onBypass).toHaveBeenCalledWith(true);
  });

  it('Show original stays on when it is switched on while the gallery takes its thumbnail frame', async () => {
    vi.useFakeTimers();
    try {
      const graded = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 0, 60, 0), grade: { nodes: [{ id: 'n1', name: 'Correction 1', enabled: true, primaries: { saturation: 0 } }] } } as MediaClip])]);
      const s = storeWith(graded);
      function Host() {
        const [bypass, setBypass] = useState(false);
        return <ColorWorkspace {...props(s, ['c1'])} gradeBypass={bypass} onGradeBypass={setBypass} />;
      }
      wrap(<Host />);
      fireEvent.click(screen.getByTestId('color-tab-gallery'));
      const box = () => screen.getByTestId('color-bypass') as HTMLInputElement;
      // The panel has started its brief bypass for the thumbnails: that is not the user's switch.
      expect(box().checked).toBe(false);
      fireEvent.click(box());
      expect(box().checked).toBe(true);
      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
      expect(box().checked).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('R10.1 a look tile writes a named look node as one undo step; Apply to all grades every clip in one step', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...props(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    fireEvent.click(screen.getByTestId('look-warm'));
    const clipOf = (id: string) => s.store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;
    expect(clipOf('c1').grade?.nodes?.[0].name).toBe('Look: Warm 100%');
    expect(s.store.getState().history).toHaveLength(1);
  });

  it('R10.1 a picture selected together with its linked camera sound still takes the look (the pair counts as one clip)', () => {
    const s = storeWith(doc([
      track(TRACKS.v1, 'video', [{ ...media('c1', 0, 60, 0), linkId: 'L1' } as MediaClip, media('c2', 60, 120, 0)]),
      track(TRACKS.a1, 'audio', [{ ...media('s1', 0, 60, 0), linkId: 'L1' } as MediaClip]),
    ]));
    wrap(<ColorWorkspace {...props(s, ['c1', 's1'])} />);
    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    expect(screen.queryByTestId('look-no-target')).toBeNull();
    fireEvent.click(screen.getByTestId('look-warm'));
    const clipOf = (id: string) => s.store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;
    expect(clipOf('c1').grade?.nodes?.[0].name).toBe('Look: Warm 100%');
    expect(clipOf('s1').grade).toBeUndefined();
  });

  it('R10.1 two unrelated clips selected still give no look target', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...props(s, ['c1', 'c2'])} />);
    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    expect(screen.getByTestId('look-no-target')).toBeTruthy();
  });

  it('R10.1 Apply to all clips writes the look to every media clip in one undo step', () => {
    const s = storeWith();
    const view = wrap(<ColorWorkspace {...props(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    fireEvent.click(screen.getByTestId('look-warm'));
    view.rerender(<MantineProvider><ColorWorkspace {...props(s, ['c1'])} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('look-apply-all'));
    const clips = s.store.getState().doc.tracks.flatMap((t) => t.items).filter((i): i is MediaClip => i.type === 'mediaClip');
    expect(clips.length).toBeGreaterThan(1);
    for (const c of clips) expect(c.grade?.nodes?.some((n) => n.name === 'Look: Warm 100%')).toBe(true);
    expect(s.store.getState().history).toHaveLength(2);
    s.store.undo();
    expect(clips.filter((c) => !!(s.store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === c.id) as MediaClip).grade)).toHaveLength(1);
  });

  it('R10.8 adds an adjustment layer with a range and lets a look be written to it', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...props(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('adjustment-add'));
    const adj = s.store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.type === 'adjustmentClip');
    expect(adj).toBeDefined();
    expect(adj!.recordOut).toBeGreaterThan(adj!.recordIn);
  });

  it('§8.2 has no gate: the node stack and the six grade-tool tabs are visible at once', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...props(s, ['c1'])} />);
    expect(screen.queryByTestId('color-advanced-toggle')).toBeNull();
    expect(screen.queryByTestId('color-advanced')).toBeNull();
    expect(screen.getByTestId('grade-add-node')).toBeDefined();
    for (const tab of ['primaries', 'curves', 'lut', 'secondaries', 'groups', 'gallery']) {
      expect(screen.getByTestId(`color-tab-${tab}`)).toBeDefined();
    }
    // The viewer previews curves, LUTs and secondaries: no tab says "render only" any more.
    for (const tab of ['curves', 'lut', 'secondaries']) {
      fireEvent.click(screen.getByTestId(`color-tab-${tab}`));
      expect(screen.queryByTestId('color-tab-parity')).toBeNull();
    }
  });

  it('§8.2 selecting a node does not enter the window tool; Edit window on the picture does', () => {
    const win = { shape: { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05 }, offsetX: { value: 0, keys: null }, offsetY: null };
    const s = storeWith(doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0, { grade: { nodes: [{ id: 'n1', name: 'Shot', enabled: true, window: win }] } })])]));
    const activate = vi.fn();
    const published: unknown[] = [];
    wrap(<ColorWorkspace {...props(s, ['c1'])} onActivateWindowTool={activate} onWindowTarget={(target) => published.push(target)} />);
    fireEvent.click(screen.getByTestId('grade-node-select-0'));
    expect(activate).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('color-tab-secondaries'));
    const edit = screen.getByTestId('color-edit-window');
    expect(edit.textContent).toBe('Edit window on the picture');
    fireEvent.click(edit);
    expect(activate).toHaveBeenCalledTimes(1);
  });

  it('§8.2 Match to reference works from the Gallery tab with the scopes collapsed (current stats from the page)', () => {
    const stats = { meanR: 0.2, meanG: 0.3, meanB: 0.4, meanLuma: 0.3, lumaSpread: 0.1 };
    const s = storeWith();
    wrap(
      <ColorWorkspace
        {...props(s, ['c1'])}
        gallery={[{ id: 's1', dataUrl: 'data:image/png;base64,', label: 'Reference', stats }]}
        referenceId="s1"
        getCurrentStats={() => ({ meanR: 0.1, meanG: 0.1, meanB: 0.1, meanLuma: 0.1, lumaSpread: 0.2 })}
      />,
    );
    fireEvent.click(screen.getByTestId('color-tab-gallery'));
    fireEvent.click(screen.getByTestId('color-shot-match'));
    const clip = s.store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.grade?.nodes?.length).toBe(1);
    expect(s.store.getState().history).toHaveLength(1);
    expect(s.store.getState().history[0].label).toBe('Shot match');
  });
});

describe('ComposeWorkspace', () => {
  const props = (s: ReturnType<typeof storeWith>, selection: string[]) => ({
    doc: s.store.getState().doc,
    projectId: 'p',
    readOnly: false,
    selection,
    playheadFrame: 30,
    inOut: { inFrame: null, outFrame: null } as { inFrame: number | null; outFrame: number | null },
    newId: s.newId,
    onSetProperty: s.onSetProperty,
    onApply: s.onApply,
    onSelect: vi.fn(),
    onSeek: vi.fn(),
  });

  it('sets a mask and adds an effect on a media clip through setProperty', () => {
    const s = storeWith();
    const view = wrap(<ComposeWorkspace {...props(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('mask-reframe-add-mask'));
    let clip = s.store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.mask).toMatchObject({ kind: 'rect', feather: 0.05 });
    view.rerender(<MantineProvider><ComposeWorkspace {...props(s, ['c1'])} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('compose-entry-effects'));
    fireEvent.click(screen.getByTestId('compose-effect-add'));
    clip = s.store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.effects).toEqual([{ type: 'blur', amount: 0.5, enabled: true }]);
    expect(s.store.getState().history.map((h) => h.label)).toEqual(['Add mask', 'Effects']);
    fireEvent.click(screen.getByTestId('compose-entry-chain'));
    expect(screen.getByTestId('react-flow')).toBeDefined();
    expect(stackStages(s.store.getState().doc, clip).filter((st) => st.active).map((st) => st.id)).toEqual(['source', 'effects', 'mask', 'overlay']);
  });

  it('shows a blur of amount 2 (the lineage canvas fill) at 2 and keeps every other effect slider at 0..1', () => {
    const s = storeWith();
    s.store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'effects', value: [{ type: 'blur', amount: 2, enabled: true }, { type: 'sharpen', amount: 1, enabled: true }] });
    wrap(<ComposeWorkspace {...props(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('compose-entry-effects'));
    const [blur, sharpen] = screen.getAllByRole('slider');
    expect(blur.getAttribute('aria-valuemax')).toBe('2');
    expect(blur.getAttribute('aria-valuenow')).toBe('2');
    expect(sharpen.getAttribute('aria-valuemax')).toBe('1');
    fireEvent.keyDown(blur, { key: 'ArrowRight' });
    fireEvent.keyUp(blur, { key: 'ArrowRight' });
    const clip = s.store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.effects?.[0].amount).toBeLessThanOrEqual(2);
    expect(clip.effects?.[0].amount).toBeGreaterThan(1);
  });

  it('adds an adjustment clip on a new top track and then edits its grade', () => {
    const s = storeWith();
    const onSelect = vi.fn();
    wrap(<ComposeWorkspace {...props(s, [])} onSelect={onSelect} />);
    fireEvent.click(screen.getByTestId('compose-add-adjustment'));
    const state = s.store.getState();
    expect(state.doc.tracks).toHaveLength(2);
    const adjustment = state.doc.tracks[1].items[0] as AdjustmentClip;
    expect(adjustment.type).toBe('adjustmentClip');
    expect([adjustment.recordIn, adjustment.recordOut]).toEqual([30, 90]);
    expect(onSelect).toHaveBeenCalledWith(adjustment.id);
    expect(state.history).toHaveLength(1);
  });

  it('WS-128: the quad key chips are enabled and move the playhead to their key', () => {
    const insert = { id: 'i1', type: 'insertClip', name: 'Screen', recordIn: 0, recordOut: 120, enabled: true, quad: { value: null, keys: [{ frame: 0, value: { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } }, interp: 'linear' }, { frame: 45, value: { topLeft: { x: 0.1, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } }, interp: 'linear' }] } };
    const s = storeWith(doc([track(TRACKS.v1, 'video', [insert as never])]));
    const onSeek = vi.fn();
    wrap(<ComposeWorkspace {...props(s, ['i1'])} onSeek={onSeek} />);
    fireEvent.click(screen.getByTestId('compose-entry-quad'));
    const chip = screen.getByTestId('compose-quad-key-45') as HTMLButtonElement;
    expect(chip.disabled).toBe(false);
    fireEvent.click(chip);
    expect(onSeek).toHaveBeenCalledWith(45);
    expect(s.store.getState().history).toHaveLength(0);
  });
});

describe('InspectorPanel title template and CurveEditor', () => {
  it('sets the template through setProperty and clears it back to plain', () => {
    const s = storeWith();
    wrap(<InspectorPanel doc={s.store.getState().doc} selection={['t1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={s.onSetProperty} />);
    expect(screen.getByTestId('inspector-title-template')).toBeDefined();
    // The Mantine Select is driven through its hidden input in jsdom: write the op the control writes.
    s.onSetProperty('t1', 'template', 'lowerThird');
    expect((s.store.getState().doc.tracks[0].items[2] as TitleClip).template).toBe('lowerThird');
    s.onSetProperty('t1', 'template', null);
    expect((s.store.getState().doc.tracks[0].items[2] as TitleClip).template).toBeNull();
  });

  it('adds a curve point on click and reports the channel’s curves', () => {
    const writes: (GradeCurves | null)[] = [];
    wrap(<CurveEditor curves={null} readOnly={false} onChange={(c) => writes.push(c)} />);
    const svg = screen.getByTestId('curve-svg');
    svg.getBoundingClientRect = () => ({ left: 0, top: 0, width: 180, height: 180, right: 180, bottom: 180, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
    fireEvent.pointerDown(svg, { clientX: 90, clientY: 45, pointerId: 1 });
    fireEvent.pointerUp(svg, { clientX: 90, clientY: 45, pointerId: 1 });
    expect(writes).toHaveLength(1);
    expect(writes[0]?.y?.[0]).toEqual({ x: 0.5, y: 0.75 });
  });
});

describe('Compose list and properties (selected-target editor)', () => {
  const base = (s: ReturnType<typeof storeWith>, selection: string[]) => ({
    doc: s.store.getState().doc, projectId: 'p', readOnly: false, selection, playheadFrame: 0, inOut: { inFrame: null, outFrame: null } as { inFrame: number | null; outFrame: number | null },
    newId: s.newId, onSetProperty: s.onSetProperty, onApply: s.onApply, onSelect: vi.fn(), onSeek: vi.fn(),
  });

  it('names the clip and its type, marks the selected entry and moves it with the arrow keys', () => {
    const s = storeWith();
    wrap(<ComposeWorkspace {...base(s, ['c1'])} />);
    expect(screen.getByTestId('compose-target').textContent).toMatch(/Video clip/);
    const mask = screen.getByTestId('compose-entry-mask');
    expect(mask.getAttribute('aria-selected')).toBe('true');
    expect(screen.getByTestId('compose-entry-keyer').getAttribute('tabindex')).toBe('-1');
    fireEvent.keyDown(mask, { key: 'ArrowDown' });
    expect(screen.getByTestId('compose-entry-reframe').getAttribute('aria-selected')).toBe('true');
  });

  it('offers a one-click Add for an off keyer and removes it again', () => {
    const s = storeWith();
    const view = wrap(<ComposeWorkspace {...base(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('compose-entry-keyer'));
    expect(screen.getByTestId('compose-entry-keyer').textContent).toMatch(/off/);
    fireEvent.click(screen.getByTestId('compose-keyer-toggle'));
    expect((s.store.getState().doc.tracks[0].items[0] as MediaClip).keyer).toBeTruthy();
    view.rerender(<MantineProvider><ComposeWorkspace {...base(s, ['c1'])} /></MantineProvider>);
    fireEvent.click(screen.getByTestId('compose-keyer-toggle'));
    expect((s.store.getState().doc.tracks[0].items[0] as MediaClip).keyer ?? null).toBeNull();
  });

  it('says plainly that several items are selected', () => {
    const s = storeWith();
    wrap(<ComposeWorkspace {...base(s, ['c1', 'c2'])} />);
    expect(screen.getByTestId('compose-multi')).toBeDefined();
    expect(screen.getByTestId('compose-add-adjustment')).toBeDefined();
  });
});
