// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc as makeDoc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { VoRecorderPanel } from '../../audio/VoRecorderPanel';
import { AudioSheet } from '../AudioSheet';
import { cleanVoiceTargets, compileCleanVoiceTap, compileDuckTap, duckState } from '../audio-sheet-logic';
import type { PhoneActions } from '../phone-actions';

beforeEach(() => {
  window.localStorage.clear();
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(() => cleanup());

const mixDoc = (): EditTimeline =>
  makeDoc([
    track('v1', 'video', [media('c1', 0, 300, 0)]),
    track('a-voice', 'audio', [media('vo1', 0, 120, 0), media('vo2', 200, 300, 0)], { name: 'Voice' }),
    track('a-music', 'audio', [media('m1', 0, 300, 0)], { name: 'Music' }),
  ]);

const actions = (over: Partial<PhoneActions> = {}): PhoneActions => ({ readOnly: false, selection: [], clip: null, ...over }) as unknown as PhoneActions;

describe('Audio sheet logic', () => {
  it('Duck music needs a music track and a voice clip, and says what is missing', () => {
    const noMusic = makeDoc([track('a1', 'audio', [media('x', 0, 100, 0)], { name: 'Sound' })]);
    const state = duckState(noMusic, null);
    expect(state.next).toBeNull();
    expect(state.reason).toMatch(/music track/i);
    expect(duckState(mixDoc(), null).next).not.toBeNull();
  });

  it('one tap on Duck music is a single op on the music fader', () => {
    const d = mixDoc();
    const next = duckState(d, null).next!;
    const plan = compileDuckTap(d, next);
    expect(plan.ops).toHaveLength(1);
    expect(plan.ops[0]).toMatchObject({ type: 'setTrackProperty', trackId: 'a-music', key: 'gainDb' });
  });

  it('Clean voice targets the voice track, not the music, and a second tap turns it off', () => {
    const d = mixDoc();
    const targets = cleanVoiceTargets(d, null);
    expect(targets.map((t) => t.id)).toEqual(['a-voice']);
    const on = compileCleanVoiceTap(targets);
    expect(on.on).toBe(true);
    expect(on.ops).toHaveLength(2);
    const cleaned = { ...targets[0], denoise: { kind: 'fft', strength: 0.6 }, leveler: { kind: 'speech', targetDb: -18 } } as typeof targets[0];
    expect(compileCleanVoiceTap([cleaned]).on).toBe(false);
  });
});

function Harness({ store, voiceover = null }: { store: EditorStore; voiceover?: React.ReactNode }) {
  const d = useEditorStore(store, (s) => s.doc);
  return (
    <MantineProvider>
      <AudioSheet
        doc={d}
        readOnly={false}
        actions={actions()}
        flags={(t) => ({ enabled: true, mute: false, solo: false, locked: false, ...(t.id === 'a-music' ? {} : {}) })}
        onToggle={vi.fn()}
        onApply={(ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label)}
        onGain={(trackId, db) => store.dispatch({ type: 'setTrackProperty', trackId, key: 'gainDb', value: { value: db, keys: null } }, 'Volume')}
        voiceover={voiceover}
      />
    </MantineProvider>
  );
}
const make = () => createEditorStore({ timelineId: 't', projectId: 'p', doc: mixDoc(), revision: 1 });

describe('Audio sheet', () => {
  it('Duck music: one tap writes the curve, one undo restores the flat fader', () => {
    const store = make();
    const before = JSON.stringify(store.getState().doc.tracks.find((t) => t.id === 'a-music')?.gainDb ?? null);
    render(<Harness store={store} />);
    fireEvent.click(screen.getByTestId('phone-audio-duck'));
    const ducked = store.getState().doc.tracks.find((t) => t.id === 'a-music')?.gainDb;
    expect(ducked?.keys?.length ?? 0).toBeGreaterThan(0);
    act(() => void undoStep(store));
    expect(JSON.stringify(store.getState().doc.tracks.find((t) => t.id === 'a-music')?.gainDb ?? null)).toBe(before);
  });

  it('Clean voice: one tap sets noise removal and the leveler together, one undo takes both back', () => {
    const store = make();
    render(<Harness store={store} />);
    fireEvent.click(screen.getByTestId('phone-audio-clean-voice'));
    const voice = () => store.getState().doc.tracks.find((t) => t.id === 'a-voice')!;
    expect(voice().denoise).toBeTruthy();
    expect(voice().leveler).toBeTruthy();
    expect(store.getState().doc.tracks.find((t) => t.id === 'a-music')?.denoise).toBeFalsy();
    act(() => void undoStep(store));
    expect(voice().denoise).toBeFalsy();
    expect(voice().leveler).toBeFalsy();
  });

  it('every track has a 44 px mute and solo and a volume slider', () => {
    render(<Harness store={make()} />);
    for (const id of ['a-voice', 'a-music']) {
      expect(screen.getByTestId(`phone-audio-mute-${id}`)).toBeTruthy();
      expect(screen.getByTestId(`phone-audio-solo-${id}`)).toBeTruthy();
      expect(screen.getByTestId(`phone-audio-gain-${id}`)).toBeTruthy();
    }
  });
});

describe('Audio sheet: selected clip volume', () => {
  it('shows only the volume section for the selected clip, not the whole clip sheet', () => {
    const setVolume = vi.fn(() => null);
    const clip = { item: {}, name: 'Interview', isMedia: true, onPictureTrack: false, speed: 1, volumePercent: 80, linked: false };
    render(
      <MantineProvider>
        <AudioSheet doc={mixDoc()} readOnly={false} actions={actions({ clip, setVolume } as unknown as Partial<PhoneActions>)} flags={() => ({ enabled: true, mute: false, solo: false, locked: false })} onToggle={vi.fn()} onApply={vi.fn()} onGain={vi.fn()} voiceover={null} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('phone-volume-readout').textContent).toContain('80%');
    fireEvent.click(screen.getByTestId('phone-volume-mute'));
    expect(setVolume).toHaveBeenCalledWith(0);
    expect(screen.queryByTestId('clip-sheet')).toBeNull();
    expect(screen.queryByTestId('speed-sheet')).toBeNull();
  });
});

describe('Voiceover on the phone', () => {
  const noRecorder = { ok: false as const, reason: 'This browser cannot record audio (no MediaRecorder).' };
  const base = { doc: mixDoc(), projectId: 'p', readOnly: false, playheadFrame: 10, newId: () => 'id', onApply: vi.fn() };

  it('without MediaRecorder it says so in one plain sentence, offers a file picker and no Record button', () => {
    render(
      <MantineProvider>
        <VoRecorderPanel {...base} support={noRecorder} phone />
      </MantineProvider>,
    );
    expect(screen.getByTestId('vo-unavailable').textContent).toContain('Recording is not available in this browser. Upload a voice file instead.');
    expect(screen.getByTestId('vo-upload').textContent).toBe('Upload a voice file');
    expect(screen.queryByTestId('vo-record')).toBeNull();
  });

  it('an uploaded voice file is placed at the playhead on a new track in one batch', async () => {
    const onApply = vi.fn();
    const upload = vi.fn(async () => ({ id: '00000000-0000-4000-8000-000000000042' }));
    const { container } = render(
      <MantineProvider>
        <VoRecorderPanel {...base} onApply={onApply} support={noRecorder} phone upload={upload} measureFile={async () => 2} />
      </MantineProvider>,
    );
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    await act(async () => {
      fireEvent.change(input, { target: { files: [new File([new Uint8Array(8)], 'voice.m4a', { type: 'audio/mp4' })] } });
    });
    expect(upload).toHaveBeenCalledTimes(1);
    expect(onApply).toHaveBeenCalledTimes(1);
    const [ops] = onApply.mock.calls[0] as [EditOp[], string];
    expect(ops.some((o) => o.type === 'addTrack')).toBe(true);
    expect(JSON.stringify(ops)).toContain('"recordIn":10');
  });
});
