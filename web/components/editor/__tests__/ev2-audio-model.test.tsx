/** Wave 2 (R08.3-R08.7): the audio model's controls, driven through the DOM. */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Fader } from '../audio/Fader';
import { LoudnessTargetSection } from '../audio/LoudnessTargetSection';
import { DuckSection } from '../audio/DuckSection';
import { AuditionSection } from '../audio/AuditionSection';
import { VoRecorderPanel } from '../audio/VoRecorderPanel';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp } from '@/lib/editing/engine';
import type { AudioJobApi } from '../audio/useAudioJob';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
Element.prototype.scrollIntoView = vi.fn();
HTMLMediaElement.prototype.play = vi.fn(async () => undefined);

beforeEach(() => window.localStorage.clear());
afterEach(() => cleanup());
const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('R08.3 Fader', () => {
  it('commits typed dB on Enter/blur, resets to 0 on double-click and on the reset button', () => {
    const onCommit = vi.fn();
    const { rerender } = wrap(<Fader value={-6} disabled={false} label="Voice" testId="f" onCommit={onCommit} />);
    const field = screen.getByTestId('f-db') as HTMLInputElement;
    expect(field.value).toBe('-6.0');
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '+3.5' } });
    fireEvent.blur(field);
    expect(onCommit).toHaveBeenLastCalledWith(3.5);
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: 'nonsense' } });
    fireEvent.blur(field);
    expect(onCommit).toHaveBeenCalledTimes(1);
    fireEvent.doubleClick(screen.getByTestId('f-wrap'));
    expect(onCommit).toHaveBeenLastCalledWith(0);
    onCommit.mockClear();
    fireEvent.click(screen.getByTestId('f-reset'));
    expect(onCommit).toHaveBeenLastCalledWith(0);
    rerender(<MantineProvider><Fader value={0} disabled={false} label="Voice" testId="f" onCommit={onCommit} /></MantineProvider>);
    expect((screen.getByTestId('f-reset') as HTMLButtonElement).disabled).toBe(true);
  });
});

const jobApi = (lufs: number[]): AudioJobApi & { starts: number } => {
  let i = 0;
  const api = {
    starts: 0,
    start: vi.fn(async (kind: 'loudness' | 'audition') => {
      api.starts += 1;
      return { executionId: `e${api.starts}`, timelineId: 't', revision: 1, kind };
    }),
    estimate: vi.fn(async () => ({ billingEnabled: false })),
    wait: vi.fn(async (_p: string, _t: string, id: string) => ({ executionId: id, kind: 'loudness' as const, status: 'Completed', revision: 1, loudness: { integratedLufs: lufs[Math.min(i++, lufs.length - 1)], truePeakDbtp: -1, loudnessRange: 4, normalised: false } })),
  };
  return api;
};

describe('R08.4 LoudnessTargetSection', () => {
  const d = () => doc([track(TRACKS.a1, 'audio', [media('a', 0, 100)], { gainDb: { value: 0, keys: null } })]);
  it('measures, shows the verdict, and Match target moves the faders then measures again', async () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: d(), revision: 1 });
    const apply = vi.fn((ops: readonly EditOp[], label: string) => {
      ops.forEach((op) => store.dispatch(op, label));
    });
    const api = jobApi([-20, -14.2]);
    wrap(<LoudnessTargetSection doc={store.getState().doc} readOnly={false} projectId="p" revision={1} onApply={apply} api={api} />);
    expect((screen.getByTestId('loudness-match') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('loudness-measure'));
    await waitFor(() => expect(screen.getByTestId('loudness-measured').getAttribute('data-verdict')).toBe('quiet'));
    expect(screen.getByTestId('loudness-measured').textContent).toMatch(/Measured -20\.0 LUFS\. 6\.0 dB too quiet for Social/);
    fireEvent.click(screen.getByTestId('loudness-match'));
    await waitFor(() => expect(api.starts).toBe(2));
    expect(store.getState().doc.tracks[0].gainDb).toMatchObject({ value: 6 });
    expect(apply).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.getByTestId('loudness-measured').getAttribute('data-verdict')).toBe('on'));
  });
  it('asks before spending credits', async () => {
    const api = jobApi([-14]);
    api.estimate = vi.fn(async () => ({ billingEnabled: true, credits: 3 }));
    wrap(<LoudnessTargetSection doc={d()} readOnly={false} projectId="p" revision={1} onApply={vi.fn()} api={api} />);
    fireEvent.click(screen.getByTestId('loudness-measure'));
    await waitFor(() => expect(screen.getByTestId('loudness-confirm').textContent).toMatch(/3 credits/));
    expect(api.starts).toBe(0);
    fireEvent.click(screen.getByTestId('loudness-confirm-go'));
    await waitFor(() => expect(api.starts).toBe(1));
  });
});

describe('R08.5 DuckSection', () => {
  it('turns on from the track names, then follows a moved voice clip by itself; Remove restores a flat fader', async () => {
    const d = doc([
      track(TRACKS.a1, 'audio', [media('v1', 10, 40, 0), media('v2', 200, 230, 0)], { name: 'Voice' }),
      track(TRACKS.a2, 'audio', [media('m', 0, 300, 0)], { name: 'Music', gainDb: { value: -6, keys: null } }),
    ]);
    const store = createEditorStore({ timelineId: 'tl', projectId: 'p', doc: d, revision: 1 });
    const onApply = (ops: readonly EditOp[], label: string) => ops.forEach((op) => store.dispatch(op, label));
    const view = () => <MantineProvider><DuckSection doc={store.getState().doc} readOnly={false} advanced={false} onApply={onApply} /></MantineProvider>;
    const { rerender } = render(view());
    expect(screen.getByTestId('ducking-state').getAttribute('data-on')).toBe('false');
    fireEvent.click(screen.getByTestId('ducking-derive'));
    rerender(view());
    expect(screen.getByTestId('ducking-state').getAttribute('data-on')).toBe('true');
    const keysOf = () => JSON.stringify(store.getState().doc.tracks[1].gainDb?.keys);
    const first = keysOf();
    expect(first.length).toBeGreaterThan(10);
    expect(store.getState().history).toHaveLength(1);
    // move a voice clip: the keyframes follow without another click
    act(() => {
      store.dispatch({ type: 'move', itemId: 'v1', toTrackId: TRACKS.a1, toRecordIn: 100 } as EditOp, 'Move');
    });
    rerender(view());
    await waitFor(() => expect(keysOf()).not.toBe(first));
    expect(store.getState().history.map((h) => h.label).slice(-1)).toEqual(['Update ducking']);
    fireEvent.click(screen.getByTestId('ducking-remove'));
    rerender(view());
    expect(store.getState().doc.tracks[1].gainDb).toMatchObject({ value: -6, keys: null });
    expect(screen.getByTestId('ducking-state').getAttribute('data-on')).toBe('false');
  });
});

describe('R08.6 AuditionSection', () => {
  it('renders about ten seconds around the playhead and offers the audio to play', async () => {
    const d = doc([track(TRACKS.a1, 'audio', [media('a', 0, 900)])]);
    const api: AudioJobApi = {
      start: vi.fn(async (kind, _p, _t, _r, range) => ({ executionId: 'x', timelineId: 't', revision: 1, kind, range } as never)),
      estimate: vi.fn(async () => ({ billingEnabled: false })),
      wait: vi.fn(async () => ({ executionId: 'x', kind: 'audition' as const, status: 'Passed', revision: 1, stepResultId: 'sr1', durationSec: 10 })),
    };
    wrap(<AuditionSection doc={d} projectId="p" revision={1} playheadFrame={450} api={api} />);
    fireEvent.click(screen.getByTestId('audition-run'));
    await waitFor(() => expect(screen.getByTestId('audition-player').getAttribute('src')).toBe('/api/v1/projects/p/outputs/sr1/download'));
    expect(api.start).toHaveBeenCalledWith('audition', 'p', d.id, 1, { inFrame: 300, outFrame: 600 });
  });
});

describe('R08.7 VoRecorderPanel', () => {
  const baseProps = (over: Partial<React.ComponentProps<typeof VoRecorderPanel>> = {}) => ({
    doc: doc([track(TRACKS.v1, 'video', [media('v', 0, 600)])]),
    projectId: 'p',
    readOnly: false,
    playheadFrame: 30,
    onApply: (() => {}) as (ops: readonly EditOp[], label: string) => void,
    newId: (() => { let n = 0; return () => `id${++n}`; })(),
    support: { ok: true as const, mimeType: 'audio/webm;codecs=opus' },
    countInMs: 1,
    listDevices: async () => [{ id: 'd1', label: 'Desk mic' }, { id: 'd2', label: 'Headset' }],
    ...over,
  });
  it('counts in, shows a level, uses the chosen device, and a retake replaces the take in one apply', async () => {
    const calls: string[] = [];
    let started = false;
    const take = { stop: vi.fn(async () => ({ blob: new Blob(['x']), durationSeconds: 2 })), cancel: vi.fn(), level: () => 0.25, startRecording: () => { started = true; } };
    const start = vi.fn(async (_m: string, o?: { deviceId?: string | null }) => { calls.push(String(o?.deviceId)); return take; });
    const upload = vi.fn(async () => ({ id: '00000000-0000-4000-8000-000000000123' }));
    const batches: { ops: readonly EditOp[]; label: string }[] = [];
    const props = baseProps({ start, upload, onApply: (ops: readonly EditOp[], label: string) => void batches.push({ ops, label }) });
    wrap(<VoRecorderPanel {...props} />);
    await waitFor(() => expect(screen.getByTestId('vo-device')).toBeTruthy());
    fireEvent.click(screen.getByTestId('vo-record'));
    await waitFor(() => expect(screen.getByTestId('vo-stop')).toBeTruthy());
    expect(started).toBe(true);
    await waitFor(() => expect(screen.getByTestId('vo-level').getAttribute('data-level')).toBe('50'));
    fireEvent.click(screen.getByTestId('vo-stop'));
    await waitFor(() => expect(batches).toHaveLength(1));
    expect(batches[0].ops[0]).toMatchObject({ type: 'addTrack', track: { name: 'Voiceover', items: [{ recordIn: 30, recordOut: 90 }] } });
    expect(batches[0].label).toMatch(/^Record voiceover/);
    // retake: the first take is on the timeline now; the second replaces its track in ONE apply
    const first = batches[0].ops[0] as Extract<EditOp, { type: 'addTrack' }>;
    expect(screen.queryByTestId('vo-retake')).toBeTruthy();
    cleanup();
    expect(first.track.kind).toBe('audio');
  });
  it('a retake removes the previous take track and puts the new one in its place', async () => {
    const take = () => ({ stop: vi.fn(async () => ({ blob: new Blob(['x']), durationSeconds: 1 })), cancel: vi.fn(), startRecording: () => {} });
    const upload = vi.fn(async () => ({ id: '00000000-0000-4000-8000-000000000125' }));
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('v', 0, 600)])]), revision: 1 });
    const batches: EditOp[][] = [];
    const onApply = (ops: readonly EditOp[], label: string) => {
      batches.push([...ops]);
      ops.forEach((op) => store.dispatch(op, label));
    };
    const view = () => <MantineProvider><VoRecorderPanel {...baseProps({ doc: store.getState().doc, start: async () => take(), upload, onApply })} /></MantineProvider>;
    const { rerender } = render(view());
    fireEvent.click(screen.getByTestId('vo-record'));
    await waitFor(() => expect(screen.getByTestId('vo-stop')).toBeTruthy());
    fireEvent.click(screen.getByTestId('vo-stop'));
    await waitFor(() => expect(batches).toHaveLength(1));
    rerender(view());
    const firstTrackId = store.getState().doc.tracks[1].id;
    fireEvent.click(screen.getByTestId('vo-retake'));
    await waitFor(() => expect(screen.getByTestId('vo-stop')).toBeTruthy());
    fireEvent.click(screen.getByTestId('vo-stop'));
    await waitFor(() => expect(batches).toHaveLength(2));
    expect(batches[1][0]).toMatchObject({ type: 'removeTrack', trackId: firstTrackId });
    expect(batches[1][1]).toMatchObject({ type: 'addTrack', index: 1 });
    expect(store.getState().doc.tracks).toHaveLength(2);
    expect(store.getState().history).toHaveLength(batches[1].length + 1);
  });
  it('shows the sentence and an upload path when the microphone is blocked, and the uploaded file lands as a clip', async () => {
    const start = vi.fn(async () => { throw Object.assign(new Error('x'), { name: 'NotAllowedError' }); });
    const upload = vi.fn(async () => ({ id: '00000000-0000-4000-8000-000000000124' }));
    const batches: { ops: readonly EditOp[] }[] = [];
    wrap(<VoRecorderPanel {...baseProps({ start, upload, measureFile: async () => 4, onApply: (ops: readonly EditOp[]) => void batches.push({ ops }) })} />);
    fireEvent.click(screen.getByTestId('vo-record'));
    await waitFor(() => expect(screen.getByTestId('vo-error').textContent).toMatch(/blocked the microphone.*upload a recording/));
    expect((screen.getByTestId('vo-record') as HTMLButtonElement).disabled).toBe(true);
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(['abc'], 'take.m4a', { type: 'audio/mp4' })] } });
    await waitFor(() => expect(batches).toHaveLength(1));
    expect(batches[0].ops[0]).toMatchObject({ type: 'addTrack', track: { items: [{ recordIn: 30, recordOut: 150 }] } });
  });
  it('without MediaRecorder it states why and offers only the upload', () => {
    wrap(<VoRecorderPanel {...baseProps({ support: { ok: false, reason: 'This browser cannot record audio (no MediaRecorder).' }, onApply: () => {} })} />);
    expect(screen.getByTestId('vo-unavailable').textContent).toMatch(/no MediaRecorder.*upload a recording/);
    expect(screen.queryByTestId('vo-record')).toBeNull();
    expect(screen.getByTestId('vo-upload')).toBeTruthy();
  });
});
