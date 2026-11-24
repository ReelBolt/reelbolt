import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MediaPoolItem } from '@/lib/editing/media/types';

vi.mock('@/lib/editing/api/media', () => ({
  fetchMediaDerivatives: vi.fn(async () => []),
  derivativeContentUrl: () => '/proxy',
  originalContentUrl: (_p: string, ref: string) => (ref === 'file:none' ? null : `/original/${ref}`),
}));

import { SourceMonitorPicture, SourceMonitorTransport, ViewerModeSwitch, useSourceMonitor, type SourceMonitorController } from '../SourceMonitor';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
global.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

// jsdom does not implement media playback.
HTMLMediaElement.prototype.play = vi.fn(async () => undefined);
HTMLMediaElement.prototype.pause = vi.fn();

const item = (over: Partial<MediaPoolItem> = {}): MediaPoolItem =>
  ({ mediaRef: 'file:a', fileName: 'a.mp4', kind: 'video', probe: null, ...over }) as MediaPoolItem;

const holder: { current: SourceMonitorController | null } = { current: null };
function Harness({ it, onPlace = () => undefined }: { it: MediaPoolItem | null; onPlace?: (...args: unknown[]) => void }) {
  const monitor = useSourceMonitor('p', it, true);
  React.useEffect(() => {
    holder.current = monitor;
  });
  return (
    <MantineProvider env="test">
      <SourceMonitorPicture monitor={monitor} />
      <SourceMonitorTransport monitor={monitor} readOnly={false} onPlace={onPlace} chordLabel={(l) => l} chordAria={() => undefined} />
    </MantineProvider>
  );
}

describe('Source monitor (T-017)', () => {
  afterEach(() => cleanup());

  it('the switch is labelled, names both programs and reports a choice', () => {
    const onChange = vi.fn();
    render(
      <MantineProvider env="test">
        <ViewerModeSwitch mode="record" onChange={onChange} hint="Switch (Shift+V)" keyShortcuts="Shift+V" sourceName={null} />
      </MantineProvider>,
    );
    expect(screen.getByLabelText('Viewer program')).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Source'));
    expect(onChange).toHaveBeenCalledWith('source');
  });

  it('says what to do when no source is open', () => {
    render(<Harness it={null} />);
    expect(screen.getByTestId('source-monitor-empty').textContent).toMatch(/pick a clip/i);
    expect((screen.getByTestId('source-monitor-mark-in') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('source-monitor-place')?.querySelector('button') as HTMLButtonElement).disabled).toBe(true);
  });

  it('plays a video source and, if the file fails, says so plainly (no spinner)', async () => {
    render(<Harness it={item()} />);
    const video = await screen.findByTestId('source-monitor-video');
    expect(video.getAttribute('src')).toBe('/original/file:a');
    fireEvent.error(video);
    expect(screen.getByTestId('source-monitor-unplayable').textContent).toMatch(/cannot be played/i);
  });

  it('a source with no file says so', async () => {
    render(<Harness it={item({ mediaRef: 'file:none' })} />);
    expect((await screen.findByTestId('source-monitor-unplayable')).textContent).toMatch(/no file/i);
  });

  it('Space toggles the source play state through the transport and issues no placement', async () => {
    const onPlace = vi.fn();
    render(<Harness it={item()} onPlace={onPlace} />);
    await screen.findByTestId('source-monitor-video');
    expect(holder.current?.speed).toBe(0);
    act(() => holder.current?.transport.togglePlay());
    expect(holder.current?.speed).toBe(1);
    act(() => holder.current?.transport.stop());
    expect(holder.current?.speed).toBe(0);
    expect(onPlace).not.toHaveBeenCalled();
  });

  it('names the open clip as identity, with no "Playing"', () => {
    render(
      <MantineProvider env="test">
        <ViewerModeSwitch mode="source" onChange={() => undefined} hint="Switch" sourceName="clipA.mp4" />
      </MantineProvider>,
    );
    expect(screen.getByTestId('source-monitor-name').textContent).toBe('Source: clipA.mp4');
  });

  it('the switch keeps its own arrows only: other chords go through', () => {
    render(
      <MantineProvider env="test">
        <ViewerModeSwitch mode="record" onChange={() => undefined} hint="Switch" />
      </MantineProvider>,
    );
    const root = screen.getByTestId('viewer-mode-switch');
    expect(root.getAttribute('data-editor-shortcuts')).toBe('through');
    expect(root.getAttribute('data-editor-own-keys')).toBe('radio');
  });

  it('has go-to-start, go-to-end and a keyboard-operable scrub bar', async () => {
    render(<Harness it={item()} />);
    await screen.findByTestId('source-monitor-video');
    expect(screen.getByTestId('source-monitor-go-start')).toBeTruthy();
    expect(screen.getByTestId('source-monitor-go-end')).toBeTruthy();
    const scrub = screen.getByTestId('source-monitor-scrub');
    expect(scrub.getAttribute('data-editor-own-keys')).toBe('slider');
    expect(scrub.querySelector('[role="slider"]')).toBeTruthy();
  });

  it('lays the source over the program frame rectangle, so the picture box does not move', () => {
    const rects: Record<string, { left: number; top: number; width: number; height: number }> = { host: { left: 100, top: 50, width: 800, height: 600 }, 'program-frame': { left: 200, top: 180, width: 400, height: 225 } };
    const spy = vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      const r = rects[this.getAttribute('data-testid') ?? ''] ?? { left: 0, top: 0, width: 0, height: 0 };
      return { ...r, right: r.left + r.width, bottom: r.top + r.height, x: r.left, y: r.top, toJSON: () => ({}) } as DOMRect;
    });
    render(
      <MantineProvider env="test">
        <div data-testid="host" style={{ position: 'relative' }}>
          <div data-testid="program-frame" />
          <FramePicture />
        </div>
      </MantineProvider>,
    );
    const frame = screen.getByTestId('source-monitor-frame');
    expect(frame.style.left).toBe('100px');
    expect(frame.style.top).toBe('130px');
    expect(frame.style.width).toBe('400px');
    expect(frame.style.height).toBe('225px');
    spy.mockRestore();
  });
});

function FramePicture() {
  const monitor = useSourceMonitor('p', item(), true);
  return <SourceMonitorPicture monitor={monitor} />;
}

describe('Source picture clipping', () => {
  afterEach(cleanup);

  it('clips the copy of the program frame to the picture area, so a frame taller than the area never covers the transport below', () => {
    render(
      <MantineProvider env="test">
        <div data-testid="host" style={{ position: 'relative', height: 90 }}>
          <div data-testid="program-frame" style={{ height: 100 }} />
          <FramePicture />
        </div>
      </MantineProvider>,
    );
    const region = screen.getByTestId('source-monitor');
    expect(region.style.overflow, 'the Source picture clips to its own box').toBe('hidden');
    expect(region.style.position).toBe('absolute');
    expect(region.parentElement).toBe(screen.getByTestId('host'));
  });
});
