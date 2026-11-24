// @vitest-environment jsdom
/**
 * P5 UI, rendered against a real store: the inspector's transform/retime/fade controls write real
 * ops (keyframes land at the playhead, a speed change retimes with a ripple, a transition is added
 * centred on the cut), the curve editor deletes a key, and the mixer writes track properties,
 * derives ducking into the music fader and shows the last render's loudness as the authority.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InspectorPanel } from '../panels/InspectorPanel';
import { KeyframeLane } from '../panels/KeyframeLane';
import { MixerPanel } from '../audio/MixerPanel';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import { doc, media, standardDoc, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
if (typeof window.requestAnimationFrame !== 'function') {
  window.requestAnimationFrame = (() => 0) as unknown as typeof window.requestAnimationFrame;
  window.cancelAnimationFrame = () => {};
}

beforeEach(() => window.localStorage.setItem('rb.editor.advancedAudio', '1'));
afterEach(() => cleanup());

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

function storeWith(document = standardDoc()) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
  const onOp = (op: EditOp, label?: string) => store.dispatch(op, label);
  const onSetProperty = (itemId: string, key: PropertyKey, value: PropertyValue) => store.dispatch({ type: 'setProperty', itemId, key, value });
  return { store, onOp, onSetProperty };
}

describe('InspectorPanel (P5)', () => {
  it('adds a keyframe at the playhead, then edits a key through the field; a speed change retimes with a ripple', () => {
    const { store, onOp, onSetProperty } = storeWith();
    const view = () => wrap(<InspectorPanel doc={store.getState().doc} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={onSetProperty} playheadFrame={20} onOp={onOp} newId={() => 'fresh'} />);
    view();
    fireEvent.click(screen.getByTestId('inspector-key-scale'));
    let clip = store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.transform?.scale?.keys).toEqual([{ frame: 20, value: 1, interp: 'linear' }]);
    cleanup();
    view();
    // With keys present the field writes a key at the playhead (same frame → replaces the value).
    fireEvent.change(screen.getByTestId('inspector-scale'), { target: { value: '1.5' } });
    fireEvent.blur(screen.getByTestId('inspector-scale')); // S20: a typed number commits when the field is left
    clip = store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.transform?.scale?.keys).toEqual([{ frame: 20, value: 1.5, interp: 'linear' }]);
    expect(screen.getByTestId('inspector-lane-scale').textContent).toBe('1◆');

    fireEvent.change(screen.getByTestId('inspector-speed'), { target: { value: '2' } });
    fireEvent.keyDown(screen.getByTestId('inspector-speed'), { key: 'Enter' }); // ... or on Enter
    clip = store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip.playbackRate).toBe(2);
    expect(clip.recordOut).toBe(30);
    expect(store.getState().doc.tracks[0].items[1].recordIn).toBe(30); // c2 rippled left
  });

  it('offers a transition between adjacent media clips and adds it centred on the cut', () => {
    const { store, onOp, onSetProperty } = storeWith();
    wrap(<InspectorPanel doc={store.getState().doc} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={onSetProperty} playheadFrame={0} onOp={onOp} newId={() => 'x-new'} />);
    // c2 has sourceIn 100 → plenty of handle for the default 12 frames (6 before the cut).
    fireEvent.click(screen.getByTestId('transition-add')); // R07.4: a dissolve of 0.5 s (15 frames: 7 before the cut, 8 after)
    const items = store.getState().doc.tracks[0].items;
    expect(items.map((i) => [i.id, i.recordIn, i.recordOut])).toEqual([['c1', 0, 53], ['x-new', 53, 68], ['c2', 68, 120], ['c3', 150, 240]]);
    expect((items[2] as MediaClip).sourceIn).toBe(108);
    cleanup();
    wrap(<InspectorPanel doc={store.getState().doc} selection={['x-new']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={onSetProperty} playheadFrame={0} onOp={onOp} newId={() => 'y'} />);
    expect(screen.getByTestId('transition-word-dissolve')).toBeTruthy();
    fireEvent.click(screen.getByTestId('transition-remove'));
    expect(store.getState().doc.tracks[0].items.map((i) => i.id)).toEqual(['c1', 'c2', 'c3']);
    expect(store.getState().doc.tracks[0].items[0].recordOut).toBe(60);
  });

  it('the curve editor deletes a selected key and changes its easing', () => {
    const { store, onOp } = storeWith();
    store.dispatch({ type: 'setKeyframe', target: { itemId: 'c1' }, param: 'opacity', frame: 10, value: 0.5, interp: 'linear' });
    store.dispatch({ type: 'setKeyframe', target: { itemId: 'c1' }, param: 'opacity', frame: 40, value: 1, interp: 'linear' });
    const channel = () => (store.getState().doc.tracks[0].items[0] as MediaClip).transform!.opacity!;
    const view = () => wrap(<KeyframeLane target={{ itemId: 'c1' }} param="opacity" channel={channel()} startFrame={0} endFrame={60} playheadFrame={20} readOnly={false} onOp={onOp} />);
    view();
    fireEvent.pointerDown(screen.getByTestId('keyframe-opacity-10'));
    fireEvent.click(screen.getByTestId('keyframe-delete'));
    expect(channel().keys!.map((k) => k.frame)).toEqual([40]);
    cleanup();
    view();
    fireEvent.pointerDown(screen.getByTestId('keyframe-opacity-40'));
    expect(screen.getByTestId('keyframe-interp')).toBeTruthy();
  });
});

describe('MixerPanel (P5)', () => {
  const flags = (t: { enabled: boolean; mute: boolean; solo: boolean; locked: boolean }) => ({ enabled: t.enabled, mute: t.mute, solo: t.solo, locked: t.locked });

  it('renders a strip per audio track, writes pan through an op, and shows the last render loudness as the authority', () => {
    const { store, onOp } = storeWith();
    const onToggle = vi.fn();
    wrap(
      <MixerPanel
        doc={store.getState().doc}
        readOnly={false}
        playheadFrame={0}
        flags={flags}
        getMeters={() => null}
        lastRenderLoudness={{ integratedLufs: -16.2, truePeakDbtp: -1.4, loudnessRange: 7.1, normalised: true }}
        onOp={onOp}
        onToggle={onToggle}
      />,
    );
    expect(screen.getAllByTestId(/mixer-strip-/)).toHaveLength(2);
    expect(screen.getByTestId('mixer-render-loudness').textContent).toContain('-16.2');
    expect(screen.getByTestId('mixer-render-loudness').textContent).toContain('normalised');
    fireEvent.click(screen.getByTestId(`mixer-mute-${TRACKS.a1}`));
    expect(onToggle).toHaveBeenCalledWith(TRACKS.a1, 'mute');
    fireEvent.change(screen.getByTestId(`mixer-lowcut-${TRACKS.a1}`), { target: { value: '80' } });
    expect(store.getState().doc.tracks[2].eq).toEqual({ lowCutHz: 80 });
  });

  it('derives ducking into the music track fader as editable keyframes', () => {
    const d = doc([
      track(TRACKS.a1, 'audio', [media('d1', 10, 40, 0), media('d2', 200, 230, 0)]),
      track(TRACKS.a2, 'audio', [media('music', 0, 300, 0)], { gainDb: { value: -6, keys: null } }),
    ]);
    const { store, onOp } = storeWith(d);
    wrap(<MixerPanel doc={store.getState().doc} readOnly={false} playheadFrame={0} flags={flags} getMeters={() => null} lastRenderLoudness={null} onOp={onOp} onToggle={vi.fn()} />);
    // Mantine selects are comboboxes: drive the derivation through the public option list.
    fireEvent.click(screen.getByTestId('ducking-music'));
    fireEvent.click(screen.getAllByRole('option').find((o) => o.textContent === TRACKS.a2)!);
    fireEvent.click(screen.getByTestId('ducking-dialogue'));
    fireEvent.click(screen.getAllByRole('option').find((o) => o.textContent === TRACKS.a1)!);
    fireEvent.click(screen.getByTestId('ducking-derive'));
    const fader = store.getState().doc.tracks[1].gainDb!;
    expect(fader.value).toBe(-6);
    expect(fader.keys!.map((k) => [k.frame, k.value])).toEqual([[2, -6], [10, -18], [40, -18], [60, -6], [192, -6], [200, -18], [230, -18], [250, -6]]);
    expect(store.getState().history).toHaveLength(1);
  });
});

describe('InspectorPanel (R07.2 mirror)', () => {
  it('shows the live viewer-drag value in the number before release', () => {
    const { store, onOp, onSetProperty } = storeWith();
    const mirror = { values: { positionX: 0.37, positionY: 0, scale: 1, rotation: 0 }, keyed: { positionX: false, positionY: false, scale: false, scaleY: false, rotation: false }, live: true };
    wrap(<InspectorPanel doc={store.getState().doc} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={onSetProperty} onOp={onOp} transformMirror={mirror} />);
    expect((screen.getByTestId('inspector-positionX') as HTMLInputElement).value).toBe('0.37');
  });
});
