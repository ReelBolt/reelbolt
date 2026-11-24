// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { doc as makeDoc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditOp } from '@/lib/editing/engine';
import type { ColorGroup, EditTimeline, GradeStack, MediaClip } from '@/lib/editing/types';
import { MAX_GRADE_NODES } from '@/lib/editing/engine/ops';

const brightness = vi.hoisted(() => ({ value: 40, span: 40 }));
vi.mock('@/lib/editing/stills/stills', () => ({ programPictureCanvas: (c: unknown) => c }));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/editing/color/scopes', async (orig) => {
  const real = await orig<typeof import('@/lib/editing/color/scopes')>();
  return {
    ...real,
    sampleCanvas: () => {
      const w = 16;
      const h = 16;
      const data = new Uint8ClampedArray(w * h * 4);
      for (let i = 0; i < w * h; i++) {
        const v = brightness.value + (i % brightness.span);
        data[i * 4] = v; data[i * 4 + 1] = v; data[i * 4 + 2] = v; data[i * 4 + 3] = 255;
      }
      return { data, width: w, height: h };
    },
  };
});

import { LookPanel, type LookPanelProps } from '../LookPanel';

beforeEach(() => {
  vi.useFakeTimers();
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
  brightness.value = 40;
  brightness.span = 40;
  (globalThis as { ImageData?: unknown }).ImageData ??= class { constructor(public data: Uint8ClampedArray, public width: number, public height: number) {} };
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
  global.matchMedia = ((query: string) => ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(() => { vi.restoreAllMocks(); cleanup(); vi.useRealTimers(); });

const nodeStack = (n: number): GradeStack => ({ enabled: true, nodes: Array.from({ length: n }, (_, i) => ({ id: `n${i}`, name: `Node ${i}`, enabled: true, curves: null, lut: null }) as never) });

function setup(over: { clip?: Partial<MediaClip>; groups?: ColorGroup[]; readOnly?: boolean } = {}) {
  const clip = { ...media('c', 0, 100, 0), ...over.clip } as MediaClip;
  let doc: EditTimeline = { ...makeDoc([track('v1', 'video', [clip])]), colorGroups: over.groups ?? null };
  const applied: Array<{ ops: readonly EditOp[]; label: string }> = [];
  const seeks: number[] = [];
  const props = (): LookPanelProps => ({
    doc, target: doc.tracks[0].items[0] as MediaClip, selection: [], readOnly: over.readOnly ?? false, playheadFrame: 10, newId: (() => { let n = 0; return () => `id${++n}`; })(),
    onWriteGrade: () => undefined, onApply: (ops, label) => { applied.push({ ops, label }); }, onSeek: (f) => seeks.push(f), gradeBypass: false, onGradeBypass: () => undefined,
    getCanvas: () => ({}) as HTMLCanvasElement, gallery: [], onCaptureStill: async () => undefined, referenceId: null, onReference: () => undefined, wipe: 0.5, onWipe: () => undefined, onMessage: () => undefined,
  });
  const ui = (p: LookPanelProps) => <MantineProvider><LookPanel {...p} /></MantineProvider>;
  const view = render(ui(props()));
  return { applied, seeks, setDoc: (d: EditTimeline) => { doc = d; view.rerender(ui(props())); }, getDoc: () => doc };
}

const run = async () => {
  fireEvent.click(screen.getByTestId('fix-exposure-run'));
  await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
};

describe('LookPanel Fix exposure', () => {
  it('a dark clip writes exactly one change labelled Fix exposure', async () => {
    const s = setup();
    await run();
    expect(s.applied).toHaveLength(1);
    expect(s.applied[0].label).toBe('Fix exposure');
    expect(s.applied[0].ops).toHaveLength(1);
    expect(s.applied[0].ops[0]).toMatchObject({ type: 'setProperty', itemId: 'c', key: 'grade' });
    expect(screen.getByTestId('fix-exposure-result').textContent).toMatch(/Brightened by/);
    expect(s.seeks.at(-1)).toBe(10);
  });

  it('a clip that already looks fine writes nothing', async () => {
    brightness.value = 10;
    brightness.span = 235;
    const s = setup();
    await run();
    expect(s.applied).toHaveLength(0);
    expect(screen.getByTestId('fix-exposure-result').textContent).toBe('This clip already looks fine');
  });

  it('shows progress with a Cancel that writes nothing', async () => {
    const s = setup();
    fireEvent.click(screen.getByTestId('fix-exposure-run'));
    await act(async () => { await vi.advanceTimersByTimeAsync(700); });
    expect(screen.getByTestId('fix-exposure-progress').textContent).toMatch(/Measuring \d of 5/);
    fireEvent.click(screen.getByTestId('fix-exposure-cancel'));
    await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
    expect(s.applied).toHaveLength(0);
    expect(s.seeks.at(-1)).toBe(10);
    expect(screen.getByTestId('fix-exposure-run')).toBeTruthy();
  });

  it('reads "Fix exposure again" with an existing Auto fix node and replaces it in place', async () => {
    const s = setup({ clip: { grade: { enabled: true, nodes: [{ id: 'a', name: 'Auto fix', enabled: true, primaries: { exposure: 0.1 }, curves: null, lut: null } as never] } } });
    expect(screen.getByTestId('fix-exposure-run').textContent).toBe('Fix exposure again');
    await run();
    const op = s.applied[0].ops[0];
    const stack = op.type === 'setProperty' ? (op.value as GradeStack) : null;
    expect(stack?.nodes?.filter((n) => n.name === 'Auto fix')).toHaveLength(1);
    expect(stack?.nodes)?.toHaveLength(1);
  });

  it('writes into the group stack and says so', async () => {
    const s = setup({ groups: [{ id: 'g1', name: 'G', clipIds: ['c'], grade: nodeStack(1) }] });
    expect(screen.getByTestId('fix-exposure-group').textContent).toBe('Fixes the whole group');
    await run();
    expect(s.applied[0].ops[0]).toMatchObject({ type: 'setColorGroup', groupId: 'g1' });
  });

  it('refuses plainly at the node cap and writes nothing', async () => {
    const s = setup({ clip: { grade: nodeStack(MAX_GRADE_NODES) } });
    await run();
    expect(s.applied).toHaveLength(0);
    expect(screen.getByTestId('fix-exposure-result').textContent).toMatch(/most adjustments/);
  });

  it('a clip edited while measuring is not written silently: Apply anyway / Measure again', async () => {
    const s = setup();
    fireEvent.click(screen.getByTestId('fix-exposure-run'));
    await act(async () => { await vi.advanceTimersByTimeAsync(700); });
    act(() => s.setDoc({ ...makeDoc([track('v1', 'video', [{ ...media('c', 0, 100, 0), grade: nodeStack(1) } as MediaClip])]), colorGroups: null }));
    await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
    expect(s.applied).toHaveLength(0);
    expect(screen.getByTestId('fix-exposure-result').textContent).toBe('The clip changed while measuring');
    fireEvent.click(screen.getByTestId('fix-exposure-apply'));
    expect(s.applied).toHaveLength(1);
    const op = s.applied[0].ops[0];
    const stack = op.type === 'setProperty' ? (op.value as GradeStack) : null;
    expect(stack?.nodes?.map((n) => n.name)).toEqual(['Auto fix', 'Node 0']);
  });

  it('Measure again re-runs the job', async () => {
    const s = setup();
    fireEvent.click(screen.getByTestId('fix-exposure-run'));
    await act(async () => { await vi.advanceTimersByTimeAsync(700); });
    act(() => s.setDoc({ ...makeDoc([track('v1', 'video', [{ ...media('c', 0, 100, 0), grade: nodeStack(1) } as MediaClip])]), colorGroups: null }));
    await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
    fireEvent.click(screen.getByTestId('fix-exposure-again'));
    await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
    expect(s.applied).toHaveLength(1);
  });

  it('is disabled in a read-only editor', () => {
    setup({ readOnly: true });
    expect((screen.getByTestId('fix-exposure-run') as HTMLButtonElement).disabled).toBe(true);
  });
});
