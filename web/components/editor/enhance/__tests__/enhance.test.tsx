// @vitest-environment jsdom
/** Video enhance: the job hook's state machine and the section with a mocked API. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { assetRef, doc, media, track } from '@/lib/editing/__tests__/fixtures';
import { apply, type EditOp } from '@/lib/editing/engine';
import { EnhanceFailedError } from '@/lib/editing/enhance/enhance-api';
import type { EnhanceEstimate, EnhanceJob } from '@/lib/editing/enhance/types';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { EnhanceSection } from '../EnhanceSection';
import { useEnhanceJob, type EnhanceJobApi } from '../useEnhanceJob';
import { resetEnhanceStore } from '../enhance-store';
import { registerEnhanceHost } from '../enhance-host';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
Element.prototype.scrollIntoView = vi.fn();
window.HTMLMediaElement.prototype.pause = vi.fn();
window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);

afterEach(() => {
  cleanup();
  resetEnhanceStore();
  registerEnhanceHost(null);
});

const EST: EnhanceEstimate = { billingEnabled: true, hosted: false, credits: 0, seconds: 10, balanceCredits: 10, affordable: true, unavailableReason: null, consented: false, maxOutputHeight: 1080, outputWidth: 1920, outputHeight: 1080, outputFps: 30, estimatedBytes: 12_000_000, estimatedProcessingSeconds: 40 };
const AI: EnhanceEstimate = { ...EST, hosted: true, credits: 3 };
const job = (over: Partial<EnhanceJob> = {}): EnhanceJob => ({ executionId: 'e1', state: 'ready', op: 'Upscale', engine: 'Local', outputFileId: 'out-1', sourceMediaRef: 'file:x', ...over });

function mockApi(estimate: EnhanceEstimate = EST, result: EnhanceJob = job()) {
  return {
    estimate: vi.fn().mockResolvedValue(estimate),
    start: vi.fn().mockResolvedValue({ executionId: 'e1', op: 'Upscale', engine: 'Local', hosted: estimate.hosted, state: 'queued', credits: estimate.credits }),
    wait: vi.fn().mockResolvedValue(result),
    cancel: vi.fn().mockResolvedValue({}),
    grantConsent: vi.fn().mockResolvedValue({ consented: true }),
  } satisfies EnhanceJobApi;
}

const FAST = { op: 'Upscale', engine: 'Local', upscaleFactor: 2 } as const;
const HOSTED = { op: 'Upscale', engine: 'Hosted', upscaleFactor: 2 } as const;
const ctx = (using: string[] = ['c1']) => ({ projectId: 'p1', sourceMediaRef: 'file:x', clipIdsUsing: () => using });

describe('useEnhanceJob state machine', () => {
  it('Fast goes idle, estimating, ready, running, done and never asks for consent', async () => {
    const api = mockApi();
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    expect(result.current.state.kind).toBe('idle');
    await act(async () => {
      await result.current.estimate(FAST);
    });
    expect(result.current.state.kind).toBe('ready');
    await act(async () => result.current.start());
    await waitFor(() => expect(result.current.state.kind).toBe('done'));
    expect(api.start).toHaveBeenCalledWith('p1', 'file:x', FAST);
    expect(api.grantConsent).not.toHaveBeenCalled();
  });

  it('AI without the project consent asks first, decline goes back, grant PUTs the consent and then starts', async () => {
    const api = mockApi(AI);
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(HOSTED);
    });
    act(() => result.current.start());
    expect(result.current.state.kind).toBe('consent');
    expect(api.start).not.toHaveBeenCalled();
    act(() => result.current.declineConsent());
    expect(result.current.state.kind).toBe('ready');
    act(() => result.current.start());
    await act(async () => {
      await result.current.grantConsent();
    });
    expect(api.grantConsent).toHaveBeenCalledWith('p1');
    expect(api.start).toHaveBeenCalledWith('p1', 'file:x', HOSTED);
    await waitFor(() => expect(result.current.state.kind).toBe('done'));
  });

  it('AI with the consent already recorded on the server starts at once', async () => {
    const api = mockApi({ ...AI, consented: true });
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(HOSTED);
    });
    act(() => result.current.start());
    await waitFor(() => expect(result.current.state.kind).toBe('done'));
    expect(api.grantConsent).not.toHaveBeenCalled();
  });

  it('a failed consent save stays on the dialog and does not start', async () => {
    const api = mockApi(AI);
    api.grantConsent.mockRejectedValue(new Error('x'));
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(HOSTED);
    });
    act(() => result.current.start());
    await act(async () => {
      await result.current.grantConsent();
    });
    expect(result.current.state).toMatchObject({ kind: 'consent', failed: true });
    expect(api.start).not.toHaveBeenCalled();
  });

  it('a 409 CONSENT_REQUIRED on start reopens the consent state', async () => {
    const api = mockApi({ ...AI, consented: true });
    api.start.mockRejectedValue(new ApiError('no', 409, undefined, { code: 'CONSENT_REQUIRED' }));
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(HOSTED);
    });
    act(() => result.current.start());
    await waitFor(() => expect(result.current.state.kind).toBe('consent'));
  });

  it('shows progress while running and cancels through the stop route', async () => {
    const api = mockApi();
    api.wait.mockImplementation((_p, _m, _id, signal: AbortSignal, onProgress: (j: EnhanceJob) => void) => {
      onProgress(job({ state: 'running', progress: 40 }));
      return new Promise((_res, rej) => signal.addEventListener('abort', () => rej(new DOMException('a', 'AbortError'))));
    });
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    act(() => result.current.start());
    await waitFor(() => expect(result.current.state).toMatchObject({ kind: 'running', progress: 40, executionId: 'e1' }));
    act(() => result.current.cancel());
    expect(result.current.state.kind).toBe('cancelled');
    expect(api.cancel).toHaveBeenCalledWith('p1', 'file:x', 'e1');
  });

  it('waits as needs-ok when no clip uses the source at completion, and never applies by itself', async () => {
    const api = mockApi();
    let using = ['c1'];
    const { result } = renderHook(() => useEnhanceJob({ projectId: 'p1', sourceMediaRef: 'file:x', clipIdsUsing: () => using }, api));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    api.wait.mockImplementation(async () => {
      using = [];
      return job();
    });
    act(() => result.current.start());
    await waitFor(() => expect(result.current.state.kind).toBe('needs-ok'));
    expect(result.current.state).toMatchObject({ anchor: { sourceMediaRef: 'file:x', clipIds: ['c1'] } });
  });

  it('turns each refusal and failure into an error state', async () => {
    const api = mockApi();
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    api.estimate.mockRejectedValue(new ApiError('no', 402, undefined, { code: 'PLAN_NOT_ENTITLED', feature: 'video_enhance' }));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    expect(result.current.state).toMatchObject({ kind: 'error', refusal: { kind: 'plan', feature: 'video_enhance' } });
    api.estimate.mockResolvedValue({ ...AI, affordable: false, balanceCredits: 1 });
    await act(async () => {
      await result.current.estimate(HOSTED);
    });
    expect(result.current.state).toMatchObject({ kind: 'error', refusal: { kind: 'credits', missing: 2 } });
    api.estimate.mockResolvedValue({ ...EST, outputHeight: 2160 });
    await act(async () => {
      await result.current.estimate(FAST);
    });
    expect(result.current.state).toMatchObject({ kind: 'error', refusal: { kind: 'plan', feature: 'resolution' } });
    api.estimate.mockResolvedValue(EST);
    await act(async () => {
      await result.current.estimate(FAST);
    });
    api.wait.mockRejectedValue(new EnhanceFailedError(job({ state: 'failed', failureCode: 'ENHANCE_DURATION_MISMATCH' })));
    act(() => result.current.start());
    await waitFor(() => expect(result.current.state).toMatchObject({ kind: 'error', failureCode: 'ENHANCE_DURATION_MISMATCH' }));
  });

  it('read-only editors cannot estimate or start', async () => {
    const api = mockApi();
    const { result } = renderHook(() => useEnhanceJob({ ...ctx(), readOnly: true }, api));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    act(() => result.current.start());
    expect(api.estimate).not.toHaveBeenCalled();
    expect(result.current.state.kind).toBe('idle');
  });
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
const mk = (): EditTimeline => doc([track('V1', 'video', [media('c1', 0, 100, 0), media('c2', 100, 200, 0)])]);

function Harness({ api, readOnly = false, projectId = 'p1', onOps }: { api: EnhanceJobApi; readOnly?: boolean; projectId?: string | null; onOps: (ops: EditOp[], label: string) => void }) {
  const d = mk();
  return <EnhanceSection clip={d.tracks[0].items[0] as MediaClip} projectId={projectId} readOnly={readOnly} doc={d} onBatch={onOps} api={api} />;
}

describe('EnhanceSection', () => {
  it('shows the four choices, a price line, then runs, compares and applies as ONE batch on every clip on the source', async () => {
    const api = mockApi(EST, job({ sourceMediaRef: `file:${assetRef(1).projectFileId}` }));
    const onOps = vi.fn();
    wrap(<Harness api={api} onOps={onOps} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    for (const label of ['Sharper picture', 'Smooth slow motion', 'Clean up noise', 'Motion blur']) expect(await screen.findByText(label)).toBeTruthy();
    fireEvent.click(screen.getByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate').textContent).toContain('Free'), { timeout: 3000 });
    expect(screen.getByTestId('enhance-estimate').textContent).toContain('1920 by 1080');
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-done')).toBeTruthy(), { timeout: 5000 });
    expect(screen.getByTestId('enhance-compare')).toBeTruthy();
    fireEvent.click(screen.getByTestId('enhance-apply'));
    expect(onOps).toHaveBeenCalledTimes(1);
    const [ops, label] = onOps.mock.calls[0];
    expect(label).toBe('Enhance clip');
    expect(ops).toHaveLength(2);
    expect(apply(mk(), ops[0]).doc).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId('enhance-use-original')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-use-original'));
    expect(onOps).toHaveBeenCalledTimes(1);
  }, 15000);

  it('shows the plan reason and disables sizes above the plan after an estimate', async () => {
    const api = mockApi(EST);
    wrap(<Harness api={api} onOps={vi.fn()} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-plan-reason').textContent).toContain('1080p'), { timeout: 3000 });
    const to4k = screen.getByLabelText('To 4K') as HTMLInputElement;
    expect(to4k.disabled).toBe(true);
  }, 15000);

  it('offers AI only for sharper picture and smooth slow motion', async () => {
    wrap(<Harness api={mockApi()} onOps={vi.fn()} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Clean up noise'));
    expect(screen.queryByTestId('enhance-engine')).toBeNull();
    expect(screen.getByTestId('enhance-keep-grain')).toBeTruthy();
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Smooth slow motion'));
    expect(screen.getByTestId('enhance-engine')).toBeTruthy();
  });

  it('read-only editors see no Start button', async () => {
    wrap(<Harness api={mockApi()} readOnly onOps={vi.fn()} />);
    expect(screen.queryByTestId('enhance-start')).toBeNull();
  });

  it('renders nothing without a project', () => {
    wrap(<Harness api={mockApi()} projectId={null} onOps={vi.fn()} />);
    expect(screen.queryByTestId('enhance-section')).toBeNull();
  });
});

describe('estimate codes', () => {
  it.each([
    ['PLAN_NOT_ENTITLED', { kind: 'plan', feature: 'video_enhance' }],
    ['RESOLUTION_ABOVE_PLAN', { kind: 'plan', feature: 'resolution' }],
    ['SOURCE_TOO_LONG', { kind: 'tooLong' }],
    ['SOURCE_NOT_MEASURED', { kind: 'notMeasured' }],
    ['ENHANCE_UNAVAILABLE', { kind: 'unavailable' }],
  ])('%s becomes the matching refusal', async (code, refusal) => {
    const api = mockApi({ ...EST, unavailableCode: code, unavailableReason: 'English words that must not matter' });
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    expect(result.current.state).toMatchObject({ kind: 'error', refusal });
  });
  it('CONSENT_REQUIRED on an estimate is not a refusal: the AI run is ready and asks on Start', async () => {
    const api = mockApi({ ...AI, unavailableCode: 'CONSENT_REQUIRED', unavailableReason: 'Consent is needed', consented: true });
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(HOSTED);
    });
    expect(result.current.state.kind).toBe('ready');
    act(() => result.current.start());
    expect(result.current.state.kind).toBe('consent');
  });
});

describe('a run outlives the section', () => {
  const pending = () => {
    let release: (j: EnhanceJob) => void = () => {};
    const p = new Promise<EnhanceJob>((r) => (release = r));
    return { p, release: (j: EnhanceJob) => release(j) };
  };

  it('unmount during a run, remount: the live job shows, completes, and can be applied', async () => {
    const api = mockApi();
    const gate = pending();
    api.wait.mockReturnValue(gate.p);
    const onOps = vi.fn();
    const first = wrap(<Harness api={api} onOps={onOps} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-running')).toBeTruthy(), { timeout: 5000 });
    first.unmount();
    wrap(<Harness api={api} onOps={onOps} />);
    expect(screen.getByTestId('enhance-running')).toBeTruthy();
    expect(api.start).toHaveBeenCalledTimes(1);
    await act(async () => gate.release(job({ sourceMediaRef: `file:${assetRef(1).projectFileId}` })));
    await waitFor(() => expect(screen.getByTestId('enhance-done')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-apply'));
    expect(onOps).toHaveBeenCalledTimes(1);
    expect(onOps.mock.calls[0][0]).toHaveLength(2);
  }, 20000);

  it('the clip is deleted during a run: the job waits as needs-OK and is offered on the next clip', async () => {
    const api = mockApi();
    const gate = pending();
    api.wait.mockReturnValue(gate.p);
    const full = mk();
    let live = full;
    registerEnhanceHost({ projectId: 'p1', getDoc: () => live, dispatchBatch: vi.fn() });
    const onOps = vi.fn();
    const first = wrap(<Harness api={api} onOps={onOps} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-running')).toBeTruthy(), { timeout: 5000 });
    // Both clips on the source are deleted while the run is going, and the panel is gone.
    live = doc([track('V1', 'video', [media('other', 0, 100, 0, { assetRef: assetRef(2), name: 'Other shot' })])]);
    first.unmount();
    await act(async () => gate.release(job()));
    // A clip on another source is selected: the finished job is offered, never applied.
    const d = live;
    wrap(<EnhanceSection clip={d.tracks[0].items[0] as MediaClip} projectId="p1" readOnly={false} doc={d} onBatch={onOps} api={api} />);
    expect(screen.getByTestId('enhance-other-needs-ok')).toBeTruthy();
    expect(onOps).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('enhance-other-apply'));
    expect(onOps).toHaveBeenCalledTimes(1);
    expect(onOps.mock.calls[0][0]).toHaveLength(1);
  }, 20000);

  it('names the clip a shown job belongs to when another clip is selected', async () => {
    const api = mockApi();
    api.wait.mockReturnValue(new Promise(() => {}));
    const d = mk();
    const c1 = d.tracks[0].items[0] as MediaClip;
    const c2 = d.tracks[0].items[1] as MediaClip;
    const first = wrap(<EnhanceSection clip={c1} projectId="p1" readOnly={false} doc={doc([track('V1', 'video', [c1])])} onBatch={vi.fn()} api={api} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-running')).toBeTruthy(), { timeout: 5000 });
    first.unmount();
    wrap(<EnhanceSection clip={c2} projectId="p1" readOnly={false} doc={doc([track('V1', 'video', [c1, c2])])} onBatch={vi.fn()} api={api} />);
    expect(screen.getByTestId('enhance-for-clip').textContent).toContain('c1');
  }, 20000);
});

describe('compare keeps the original on the left', () => {
  it('after Apply switches the clip to the new file, "before" still plays the original source', async () => {
    const api = mockApi(EST, job());
    const orig = assetRef(1).projectFileId as string;
    function Live() {
      const [d, setD] = React.useState<EditTimeline>(mk());
      return <EnhanceSection clip={d.tracks[0].items[0] as MediaClip} projectId="p1" readOnly={false} doc={d} api={api} onBatch={(ops) => setD((cur) => ops.reduce((acc, op) => apply(acc, op).doc, cur))} />;
    }
    wrap(<Live />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-done')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-apply'));
    await waitFor(() => expect(screen.getByTestId('enhance-applied')).toBeTruthy(), { timeout: 5000 });
    expect(screen.getByTestId('enhance-before').getAttribute('src')).toContain(orig);
    expect(screen.getByTestId('enhance-before').getAttribute('src')).not.toContain('out-1');
    expect(screen.getByTestId('enhance-after').getAttribute('src')).toContain('out-1');
  }, 20000);
});

describe('plain failure words', () => {
  it.each([
    ['ENHANCE_SOURCE_CHANGED', /refunded/i],
    ['ENHANCE_DURATION_MISMATCH', /refunded/i],
  ])('%s is shown as a plain sentence, never the code', async (code, words) => {
    const api = mockApi();
    api.wait.mockRejectedValue(new EnhanceFailedError(job({ state: 'failed', failureCode: code })));
    wrap(<Harness api={api} onOps={vi.fn()} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    const box = await screen.findByTestId('enhance-refusal', undefined, { timeout: 5000 });
    expect(box.textContent).toMatch(words);
    expect(box.textContent).not.toContain(code);
  }, 20000);

  it('the progress line is indeterminate while running (null) and never reads 100%', async () => {
    const api = mockApi();
    api.wait.mockImplementation((_p, _m, _id, signal: AbortSignal, onProgress: (j: EnhanceJob) => void) => {
      onProgress(job({ state: 'running', progress: null }));
      return new Promise((_res, rej) => signal.addEventListener('abort', () => rej(new DOMException('a', 'AbortError'))));
    });
    wrap(<Harness api={api} onOps={vi.fn()} />);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-progress').textContent).toBe('Working on it...'), { timeout: 5000 });
  }, 20000);
});

describe('T-022 fixes', () => {
  it('a SOURCE_TOO_LONG estimate names the cap when the server sends maxSeconds', async () => {
    const api = mockApi({ ...EST, unavailableCode: 'SOURCE_TOO_LONG', unavailableReason: 'too long', maxSeconds: 120 });
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    expect(result.current.state).toMatchObject({ kind: 'error', refusal: { kind: 'tooLong', maxSeconds: 120 } });
  });

  it('cancel answered 409 ENHANCE_ALREADY_FINISHED re-reads the job and shows it ready, not Cancelled', async () => {
    const api = { ...mockApi(), getJob: vi.fn().mockResolvedValue(job()) };
    api.cancel.mockResolvedValue(null);
    api.wait.mockImplementation((_p, _m, _id, signal: AbortSignal) => new Promise((_res, rej) => signal.addEventListener('abort', () => rej(new DOMException('a', 'AbortError')))));
    const { result } = renderHook(() => useEnhanceJob(ctx(), api));
    await act(async () => {
      await result.current.estimate(FAST);
    });
    act(() => result.current.start());
    await waitFor(() => expect(result.current.state).toMatchObject({ kind: 'running', executionId: 'e1' }));
    act(() => result.current.cancel());
    await waitFor(() => expect(result.current.state.kind).toBe('done'));
    expect(api.getJob).toHaveBeenCalledWith('p1', 'file:x', 'e1');
  });

  it('"Apply to this clip" from needs-OK keeps the job so Use original is still offered', async () => {
    const api = mockApi();
    const gate = new Promise<EnhanceJob>(() => {});
    api.wait.mockReturnValue(gate);
    const orphanDoc = doc([track('V1', 'video', [media('other', 0, 100, 0, { assetRef: assetRef(2), name: 'Other shot' })])]);
    registerEnhanceHost({ projectId: 'p1', getDoc: () => orphanDoc, dispatchBatch: vi.fn() });
    const { enhanceEntry } = await import('../enhance-store');
    const e = enhanceEntry('p1', 'file:x');
    e.state = { kind: 'needs-ok', anchor: { projectId: 'p1', sourceMediaRef: 'file:x', clipIds: ['gone'], clipName: 'Gone', request: FAST }, job: job() };
    function Live() {
      const [d, setD] = React.useState<EditTimeline>(orphanDoc);
      return <EnhanceSection clip={d.tracks[0].items[0] as MediaClip} projectId="p1" readOnly={false} doc={d} api={api} onBatch={(ops) => setD((cur) => ops.reduce((acc, op) => apply(acc, op).doc, cur))} />;
    }
    wrap(<Live />);
    fireEvent.click(await screen.findByTestId('enhance-other-apply'));
    await waitFor(() => expect(screen.getByTestId('enhance-use-original')).toBeTruthy(), { timeout: 5000 });
  }, 20000);

  it('the media pool dialog cannot start a run for a read-only editor', async () => {
    const { PoolEnhanceDialog } = await import('../PoolEnhanceDialog');
    const item = { mediaRef: 'file:g', kind: 'video', fileName: 'a.mp4', mimeType: 'video/mp4', sizeBytes: 1, category: 'footage', probe: null, bins: [], filmstripUrl: '' } as never;
    wrap(<PoolEnhanceDialog projectId="p1" item={item} readOnly onClose={() => {}} api={mockApi()} />);
    await screen.findByTestId('pool-enhance-dialog');
    expect(screen.queryByTestId('enhance-start')).toBeNull();
  });
});
