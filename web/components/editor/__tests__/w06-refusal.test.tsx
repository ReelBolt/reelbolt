// @vitest-environment jsdom
/**
 * W0.6 (d3 N1, d1 N6): a refused Inspector edit says why in plain words and the field goes back to
 * the stored value instead of keeping the refused text; clearing a name (clip or marker) is refused
 * with a toast and the old name comes back.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { useEffect } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const toast = vi.hoisted(() => ({ show: vi.fn() }));
vi.mock('@mantine/notifications', () => ({ notifications: toast }));

import { InspectorPanel } from '../panels/InspectorPanel';
import { MarkersPanel } from '../panels/MarkersPanel';
import { createEditorStore } from '@/lib/editing/store';
import type { PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { useRefusalToast } from '../shell/refusal-toast';
import { TransportBar } from '../viewer/TransportBar';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;

afterEach(() => {
  cleanup();
  toast.show.mockClear();
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

// The hook the workspace uses; the component only captures it so a non-React dispatch callback can call it.
type ShowRefusal = ReturnType<typeof useRefusalToast>['showRefusal'];
function CaptureRefusalToast({ onReady }: { onReady: (show: ShowRefusal) => void }) {
  const { showRefusal } = useRefusalToast();
  useEffect(() => onReady(showRefusal), [onReady, showRefusal]);
  return null;
}

function inspector() {
  let showRefusal: ShowRefusal = () => {};
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
  // The workspace's dispatch: a refusal becomes the one plain-language toast.
  const onSetProperty = (itemId: string, key: PropertyKey, value: PropertyValue) => {
    try {
      store.dispatch({ type: 'setProperty', itemId, key, value });
      return true;
    } catch (error) {
      showRefusal(error, store.getState().doc);
      return false;
    }
  };
  const view = () => wrap(<><CaptureRefusalToast onReady={(show) => { showRefusal = show; }} /><InspectorPanel doc={store.getState().doc} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={onSetProperty} playheadFrame={0} /></>);
  return { store, view };
}

describe('W0.6 Inspector refusals', () => {
  it('an out-of-range Scale is refused in plain words and the field shows the stored value again (d3 N1)', () => {
    const { store, view } = inspector();
    view();
    const field = screen.getByTestId('inspector-scale') as HTMLInputElement;
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '99' } });
    fireEvent.keyDown(field, { key: 'Enter' }); // S20: the typed number is committed on Enter, once
    expect(toast.show).toHaveBeenCalledWith(expect.objectContaining({ title: 'Edit refused', message: 'Scale must be more than 0 and at most 8.' }));
    fireEvent.blur(field);
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).transform?.scale ?? null).toBeNull();
    expect((screen.getByTestId('inspector-scale') as HTMLInputElement).value).toBe('1');
  });

  it('tabbing from one number field to the next keeps focus and the typed value there (C-326-1)', () => {
    const { store, view } = inspector();
    view();
    const scale = screen.getByTestId('inspector-scale') as HTMLInputElement;
    scale.focus();
    fireEvent.change(scale, { target: { value: '2' } });
    const rotation = screen.getByTestId('inspector-rotation') as HTMLInputElement;
    rotation.focus(); // blurs Scale, as Tab would: the typed 2 is committed there
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).transform?.scale).toEqual({ value: 2, keys: null });
    fireEvent.change(rotation, { target: { value: '45' } });
    expect(screen.getByTestId('inspector-rotation')).toBe(rotation);
    expect(document.activeElement).toBe(rotation);
    expect(rotation.value).toBe('45');
    fireEvent.keyDown(rotation, { key: 'Enter' });
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).transform?.rotation).toEqual({ value: 45, keys: null });
  });

  it('clearing the clip name is refused with a toast and the old name comes back (d1 N6)', () => {
    const { store, view } = inspector();
    view();
    const name = screen.getByTestId('inspector-name') as HTMLInputElement;
    fireEvent.change(name, { target: { value: '   ' } });
    fireEvent.blur(name);
    expect(toast.show).toHaveBeenCalledWith(expect.objectContaining({ title: 'Edit refused', message: expect.stringMatching(/needs a name/) }));
    expect((screen.getByTestId('inspector-name') as HTMLInputElement).value).toBe('c1');
    expect(store.getState().doc.tracks[0].items[0].name).toBe('c1');
  });

  it('clearing a marker name is refused with a toast and the old name comes back (d1 N6)', () => {
    const onRename = vi.fn();
    const row = { id: 'm1', kind: 'user' as const, frame: 10, name: 'Hook', note: null, color: null, durationFrames: null, itemId: null, agentName: null, providerName: null };
    wrap(<MarkersPanel rows={[row]} rate={{ num: 30, den: 1 }} playheadFrame={0} onSeek={() => {}} onAdd={() => {}} onRename={onRename} onRemove={() => {}} onSelectItem={() => {}} />);
    const input = screen.getByLabelText('Rename Hook') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '' } });
    fireEvent.blur(input);
    expect(onRename).not.toHaveBeenCalled();
    expect(toast.show).toHaveBeenCalledWith(expect.objectContaining({ title: 'Edit refused', message: expect.stringMatching(/needs a name/) }));
    expect(input.value).toBe('Hook');
  });

  it('garbage typed in the timecode field is refused inline and the field shows the playhead again on leave (d1 N9)', () => {
    const onSeek = vi.fn();
    const noop = vi.fn();
    wrap(<TransportBar playheadFrame={300} rate={{ num: 30, den: 1 }} endFrame={1000} speed={0} loop={false} inFrame={null} outFrame={null} onSeek={onSeek} onSpeed={noop} onStep={noop} onPrevEdit={noop} onNextEdit={noop} onPrevMarker={noop} onNextMarker={noop} onMarkIn={noop} onMarkOut={noop} onToggleLoop={noop} onPlayInToOut={noop} />);
    const field = screen.getByTestId('timecode-field') as HTMLInputElement;
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '99:99:zz' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(onSeek).not.toHaveBeenCalled();
    fireEvent.blur(field);
    expect(field.value).toBe('00:00:10:00');
  });
});
