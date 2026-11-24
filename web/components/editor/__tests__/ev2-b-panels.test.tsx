// @vitest-environment jsdom
/**
 * editor-v2-playback-verify, lane B: the Inspector follows the track a clip sits on (S7), numbers commit once
 * (S20), the channel map is honest in the parity badge (S5), a Versions restore asks first (S23), a marker
 * rename commits on Enter (S16), the title templates reach the preview (S6) and a still can be taken from the
 * degraded single-clip session (S12).
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InspectorPanel } from '../panels/InspectorPanel';
import { MarkersPanel } from '../panels/MarkersPanel';
import { CommitNumberInput } from '../shell/CommitNumberInput';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { MediaClip, TitleClip } from '@/lib/editing/types';
import { classifyItem } from '@/lib/editing/preview/parity';
import { rasterizeOverlays } from '@/lib/editing/preview/overlay-raster';
import { titleTemplateLayout } from '@/lib/editing/preview/title-template';
import { programPictureCanvas } from '@/lib/editing/stills/stills';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

function inspectorFor(itemId: string) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
  const onOp = (op: EditOp, label?: string) => store.dispatch(op, label);
  const onSetProperty = (id: string, key: PropertyKey, value: PropertyValue) => store.dispatch({ type: 'setProperty', itemId: id, key, value });
  wrap(
    <InspectorPanel
      doc={store.getState().doc}
      selection={[itemId]}
      rate={{ num: 30, den: 1 }}
      readOnly={false}
      onSetProperty={onSetProperty}
      playheadFrame={0}
      onOp={onOp}
      newId={() => 'x'}
      mediaClipActions={(_item, kind) => (kind === 'video' ? <button data-testid="inspector-extract-audio">Extract audio</button> : null)}
    />,
  );
  return store;
}

describe('S7 the Inspector follows the track kind', () => {
  it('a clip on a picture track shows picture controls and no sound controls', () => {
    inspectorFor('c1'); // V1
    for (const id of ['inspector-positionX', 'inspector-scale', 'inspector-opacity', 'inspector-crop-left', 'inspector-hold', 'inspector-speed', 'inspector-reverse', 'inspector-fade-in', 'inspector-extract-audio', 'inspector-picture-track-sound-note']) {
      expect(screen.queryByTestId(id), id).not.toBeNull();
    }
    for (const id of ['inspector-gainDb', 'inspector-channel-map']) expect(screen.queryByTestId(id), id).toBeNull();
  });

  it('a clip on a sound track shows sound controls and no picture controls', () => {
    inspectorFor('a1'); // A1
    for (const id of ['inspector-gainDb', 'inspector-channel-map', 'inspector-speed', 'inspector-reverse', 'inspector-fade-in', 'inspector-fade-out']) {
      expect(screen.queryByTestId(id), id).not.toBeNull();
    }
    for (const id of ['inspector-positionX', 'inspector-scale', 'inspector-rotation', 'inspector-opacity', 'inspector-crop-left', 'inspector-hold', 'inspector-extract-audio', 'inspector-picture-track-sound-note']) {
      expect(screen.queryByTestId(id), id).toBeNull();
    }
  });
});

describe('S20 a typed number is one edit', () => {
  it('typing 25 digit by digit writes ONE op on Enter, not one per keystroke', () => {
    const store = inspectorFor('c1');
    const field = screen.getByTestId('inspector-rotation') as HTMLInputElement;
    fireEvent.change(field, { target: { value: '2' } });
    fireEvent.change(field, { target: { value: '25' } });
    expect(store.getState().history).toHaveLength(0);
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(store.getState().history).toHaveLength(1);
    expect((store.getState().doc.tracks[0].items[0] as MediaClip).transform?.rotation).toEqual({ value: 25, keys: null });
  });

  it('commits after a pause in typing, once', () => {
    vi.useFakeTimers();
    const onCommit = vi.fn();
    wrap(<CommitNumberInput value={1} onCommit={onCommit} data-testid="n" />);
    const field = screen.getByTestId('n') as HTMLInputElement;
    fireEvent.change(field, { target: { value: '3' } });
    fireEvent.change(field, { target: { value: '30' } });
    expect(onCommit).not.toHaveBeenCalled();
    act(() => {
      vi.advanceTimersByTime(700);
    });
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(onCommit).toHaveBeenCalledWith(30);
  });

  it('commits on leaving the field and does not commit an unchanged value', () => {
    const onCommit = vi.fn();
    wrap(<CommitNumberInput value={1} onCommit={onCommit} data-testid="n" />);
    const field = screen.getByTestId('n') as HTMLInputElement;
    fireEvent.change(field, { target: { value: '1' } });
    fireEvent.blur(field);
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.change(field, { target: { value: '4' } });
    fireEvent.blur(field);
    expect(onCommit).toHaveBeenCalledWith(4);
    expect(onCommit).toHaveBeenCalledTimes(1);
  });

  it('commits what was typed when the field goes away mid-edit', () => {
    const onCommit = vi.fn();
    const view = wrap(<CommitNumberInput value={1} onCommit={onCommit} data-testid="n" />);
    fireEvent.change(screen.getByTestId('n'), { target: { value: '7' } });
    view.unmount();
    expect(onCommit).toHaveBeenCalledWith(7);
  });
});

describe('S5 the channel map is heard in the live preview', () => {
  const clip = (extra: Partial<MediaClip>): MediaClip => ({ ...(standardDoc().tracks[2].items[0] as MediaClip), ...extra });

  it('a mapped clip on a sound track previews as it renders (the audio graph routes the channels)', () => {
    const d = standardDoc();
    for (const map of ['left', 'right', 'mono', 'swap', 'stereo'] as const) {
      const mapped = classifyItem(clip({ channelMap: map }), d);
      expect(mapped.parity, map).toBe('faithful');
      expect(mapped.findings.some((f) => f.feature === 'channelMap'), map).toBe(false);
    }
  });
});

describe('S6 title templates', () => {
  const title = (template: TitleClip['template']): TitleClip => ({ ...(standardDoc().tracks[1].items[0] as TitleClip), template });

  it('mirrors the render: lower third sits left and low in a box, outline has no box, card has the biggest box', () => {
    const lower = titleTemplateLayout('lowerThird', 1920, 1080, 48);
    expect(lower.align).toBe('left');
    expect(lower.x).toBe(160);
    expect(lower.box?.alpha).toBe(0.55);
    expect(lower.y).toBeGreaterThan(540);
    const boxed = titleTemplateLayout('boxed', 1920, 1080, 48);
    const card = titleTemplateLayout('card', 1920, 1080, 48);
    expect(boxed.align).toBe('center');
    expect(card.box!.pad).toBe(boxed.box!.pad * 3);
    expect(titleTemplateLayout('outline', 1920, 1080, 48).box).toBeNull();
    expect(titleTemplateLayout('outline', 1920, 1080, 48).border?.width).toBe(4);
    expect(titleTemplateLayout(null, 1920, 1080, 48).border?.alpha).toBe(0.6);
  });

  it('the rasteriser draws the template: a box behind a boxed title, none for plain', () => {
    const run = (template: TitleClip['template']) => {
      const calls: string[] = [];
      const ctx = {
        canvas: { width: 1920, height: 1080 },
        clearRect: () => {},
        fillRect: () => calls.push('box'),
        strokeRect: () => {},
        fillText: (_t: string, x: number) => calls.push(`text@${Math.round(x)}`),
        strokeText: () => calls.push('border'),
        measureText: (t: string) => ({ width: t.length * 20 }),
        fillStyle: '',
        strokeStyle: '',
        lineWidth: 1,
        font: '',
        textAlign: 'left' as CanvasTextAlign,
        textBaseline: 'top' as CanvasTextBaseline,
        globalAlpha: 1,
      };
      rasterizeOverlays(ctx, [{ item: title(template), layerIndex: 0 }]);
      return calls;
    };
    expect(run('boxed')).toEqual(['box', 'text@960']);
    expect(run('lowerThird')).toEqual(['box', 'text@160']);
    expect(run('outline')).toEqual(['border', 'text@960']);
    expect(run(null)).toEqual(['border', 'text@960']);
  });

  it('the badge names the template', () => {
    expect(classifyItem(title('lowerThird')).reason).toContain('lowerThird');
    expect(classifyItem(title('lowerThird')).parity).toBe('approximate');
  });
});

describe('S16 marker rename commits on Enter', () => {
  it('Enter in the rename field commits like leaving it', () => {
    const onRename = vi.fn();
    wrap(
      <MarkersPanel
        rows={[{ id: 'm1', name: 'Hook', frame: 45, color: null, derived: false } as unknown as Parameters<typeof MarkersPanel>[0]['rows'][number]]}
        rate={{ num: 30, den: 1 }}
        playheadFrame={0}
        onSeek={() => {}}
        onAdd={() => {}}
        onRename={onRename}
        onRemove={() => {}}
        onSelectItem={() => {}}
      />,
    );
    const field = screen.getByRole('textbox', { name: 'Rename Hook' }) as HTMLInputElement;
    field.focus();
    fireEvent.change(field, { target: { value: 'Opening' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(onRename).toHaveBeenCalledWith('m1', 'Opening');
  });
});

describe('S12 a still from the degraded session', () => {
  function stage(videoShown: boolean, ready: boolean) {
    const parent = document.createElement('div');
    const canvas = document.createElement('canvas');
    canvas.width = 640;
    canvas.height = 360;
    canvas.style.display = 'none';
    const video = document.createElement('video');
    video.style.display = videoShown ? 'block' : 'none';
    Object.defineProperty(video, 'videoWidth', { value: ready ? 320 : 0 });
    Object.defineProperty(video, 'videoHeight', { value: ready ? 180 : 0 });
    Object.defineProperty(video, 'readyState', { value: ready ? 4 : 0 });
    parent.append(canvas, video);
    return { canvas, video };
  }

  it('returns the canvas itself when it is the one on screen', () => {
    const canvas = document.createElement('canvas');
    expect(programPictureCanvas(canvas)).toBe(canvas);
    expect(programPictureCanvas(null)).toBeNull();
  });

  it('copies the showing video frame onto a fresh canvas when the live canvas is hidden', () => {
    const draw = vi.fn();
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = (() => ({ drawImage: draw })) as unknown as typeof original;
    try {
      const { canvas, video } = stage(true, true);
      const picture = programPictureCanvas(canvas);
      expect(picture).not.toBe(canvas);
      expect([picture!.width, picture!.height]).toEqual([320, 180]);
      expect(draw).toHaveBeenCalledWith(video, 0, 0, 320, 180);
    } finally {
      HTMLCanvasElement.prototype.getContext = original;
    }
  });

  it('has no picture to take when nothing is showing or decoded', () => {
    expect(programPictureCanvas(stage(false, true).canvas)).toBeNull();
    expect(programPictureCanvas(stage(true, false).canvas)).toBeNull();
  });
});
