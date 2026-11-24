// @vitest-environment jsdom
/**
 * Stored-data rule: with the interface in Italian, names the editor creates and stores in the timeline document stay the
 * language-neutral English defaults (only what is shown is translated). Same harness as p7-color-compose.
 * (original description: P7 UI against a real store: the grade stack editor writes whole-stack `setProperty grade` ops
 * (one undo step each) and adds a Colorist look as an editable node; the Color workspace creates a
 * colour group with `setColorGroup` and toggles the preview bypass; the Compose workspace sets a
 * mask, adds an effect and inserts an adjustment clip on a new top track; the inspector sets a title
 * template; the curve editor adds a point on click.)
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { AdjustmentClip } from '@/lib/editing/types';
import { doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { GradeStackEditor } from '../color/GradeStackEditor';
import { ColorWorkspace } from '../color/ColorWorkspace';
import { ComposeHarness as ComposeWorkspace } from './compose-harness';

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

const wrap = (ui: React.ReactElement) => renderWithI18n(ui, { locale: 'it' });
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

const colorProps = (s: ReturnType<typeof storeWith>, selection: string[]) => ({
  doc: s.store.getState().doc, projectId: 'p', seedStepResultId: null, readOnly: false, selection, playheadFrame: 0, newId: s.newId,
  onSetProperty: s.onSetProperty, onOp: s.onOp, onApply: s.onApply, onSelect: vi.fn(), onSeek: vi.fn(), gradeBypass: false, onGradeBypass: vi.fn(),
  getCanvas: () => null, gallery: [], onCaptureStill: vi.fn(async () => {}), onRemoveStill: vi.fn(), referenceId: null, onReference: vi.fn(), wipe: 0.5, onWipe: vi.fn(),
});

describe('stored names stay English with the interface in Italian', () => {
  it('the UI really is Italian here', () => {
    wrap(<ColorWorkspace {...colorProps(storeWith(), ['c1'])} />);
    expect(screen.getByTestId('adjustment-add').textContent).not.toMatch(/^Add/);
  });

  it('a new grade correction is stored as "Correction 1"', () => {
    const writes: Array<[unknown, string]> = [];
    wrap(<GradeStackEditor projectId="p1" stack={null} readOnly={false} onChange={(s, l) => writes.push([s, l])} newId={() => 'n1'} lutFiles={[]} subject="Clip" />);
    fireEvent.click(screen.getByTestId('grade-add-node'));
    expect(writes[0][0]).toMatchObject({ nodes: [{ id: 'n1', name: 'Correction 1' }] });
  });

  it('a new colour group is stored as "Group 1"', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...colorProps(s, ['c1', 'c2'])} />);
    fireEvent.click(screen.getByTestId('color-tab-groups'));
    fireEvent.click(screen.getByTestId('color-group-create'));
    expect(s.store.getState().doc.colorGroups?.[0].name).toBe('Group 1');
  });

  it('an adjustment layer from the Color workspace is stored as "Adjustment" on a track named "Adjustments"', () => {
    const s = storeWith();
    wrap(<ColorWorkspace {...colorProps(s, ['c1'])} />);
    fireEvent.click(screen.getByTestId('adjustment-add'));
    const tracks = s.store.getState().doc.tracks;
    expect(tracks.some((t) => t.name === 'Adjustments')).toBe(true);
    expect(tracks.flatMap((t) => t.items).find((i) => i.type === 'adjustmentClip')?.name).toBe('Adjustment');
  });

  it('an adjustment layer from the Compose workspace is stored as "Adjustment" on a track named "Adjustments"', () => {
    const s = storeWith();
    wrap(<ComposeWorkspace doc={s.store.getState().doc} projectId="p" readOnly={false} selection={[]} playheadFrame={30} inOut={{ inFrame: null, outFrame: null }} newId={s.newId} onSetProperty={s.onSetProperty} onApply={s.onApply} onSelect={vi.fn()} onSeek={vi.fn()} />);
    fireEvent.click(screen.getByTestId('compose-add-adjustment'));
    const tracks = s.store.getState().doc.tracks;
    expect(tracks[1].name).toBe('Adjustments');
    expect((tracks[1].items[0] as AdjustmentClip).name).toBe('Adjustment');
  });
});
