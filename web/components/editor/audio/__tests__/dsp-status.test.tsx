// @vitest-environment jsdom
/** R08.6: each dynamics stage says whether it is heard in the preview; the bus note follows the plan. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ProcessingFields, BusesPanel } from '../BusesPanel';
import { HEARD_AFTER_RENDER, HEARD_IN_PREVIEW, PREVIEW_NOTE } from '@/lib/editing/audio/dsp-preview';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
Element.prototype.scrollIntoView = vi.fn();
beforeEach(() => window.localStorage.setItem('rb.editor.advancedAudio', '1'));
afterEach(() => {
  cleanup();
  window.localStorage.clear();
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
const fields = (support: { nodes: boolean; worklet: boolean }) =>
  wrap(<ProcessingFields value={{ dynamics: { compressor: {}, gate: {}, limiter: {}, deesser: {} } }} readOnly={false} testIdPrefix="t" onChange={vi.fn()} support={support} />);

describe('Hear it in preview status', () => {
  it('says approximately for every stage with nodes and worklet', () => {
    fields({ nodes: true, worklet: true });
    for (const s of ['comp', 'limiter', 'gate', 'deesser'].map((k) => (k === 'comp' ? 'compressor' : k))) {
      expect(screen.getByTestId(`t-${s}-status`).textContent).toBe(HEARD_IN_PREVIEW);
    }
    expect(screen.getByText(new RegExp(PREVIEW_NOTE))).toBeTruthy();
  });
  it('says render only for the gate and de-esser when AudioWorklet is missing', () => {
    fields({ nodes: true, worklet: false });
    expect(screen.getByTestId('t-compressor-status').textContent).toBe(HEARD_IN_PREVIEW);
    expect(screen.getByTestId('t-gate-status').textContent).toBe(HEARD_AFTER_RENDER);
    expect(screen.getByTestId('t-deesser-status').textContent).toBe(HEARD_AFTER_RENDER);
  });
  it('says render only everywhere with no support', () => {
    fields({ nodes: false, worklet: false });
    expect(screen.getByTestId('t-limiter-status').textContent).toBe(HEARD_AFTER_RENDER);
  });
  it('keeps the stage toggles working (the stored property is the on/off)', () => {
    const onChange = vi.fn();
    wrap(<ProcessingFields value={{ dynamics: { compressor: {} } }} readOnly={false} testIdPrefix="t" onChange={onChange} support={{ nodes: true, worklet: true }} />);
    screen.getByTestId('t-compressor').click();
    expect(onChange).toHaveBeenCalledWith('dynamics', null, 'Toggle compressor');
  });
});

describe('Buses note', () => {
  it('tells the truth about routing: heard, or fallback with a reason', () => {
    const base = { readOnly: false, playheadFrame: 0, playbackSpeed: 0, newId: () => 'x', onOp: vi.fn(), onApply: vi.fn() };
    const ok = { ...doc([track(TRACKS.a1, 'audio', [media('c', 0, 30)])]), buses: [{ id: 'a', name: 'A' }] };
    const view = wrap(<BusesPanel doc={ok} {...base} />);
    expect(screen.getByTestId('buses-render-note').textContent).toContain('heard in the preview');
    view.unmount();
    const loop = { ...ok, buses: [{ id: 'a', name: 'A', outputBusId: 'b' }, { id: 'b', name: 'B', outputBusId: 'a' }] };
    wrap(<BusesPanel doc={loop} {...base} />);
    expect(screen.getByTestId('buses-render-note').textContent).toContain('straight to the master');
  });
});
