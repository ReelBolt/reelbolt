// @vitest-environment jsdom
/**
 * The P3 shell pieces, rendered: the workspace bar switches view state only; the status bar shows
 * the measured dropped-frame count and the save state; the history panel jumps the store; the
 * inspector writes through a real `setProperty` op; the read-only banner appears on conflict.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { WorkspaceBar } from '../shell/WorkspaceBar';
import { WorkspaceHeader } from '../shell/WorkspaceHeader';
import { StatusBar } from '../shell/StatusBar';
import { WORKSPACES } from '../shell/workspaces';
import { HistoryPanel } from '../panels/HistoryPanel';
import { InspectorPanel } from '../panels/InspectorPanel';
import { ReadOnlyBanner } from '../sync/ReadOnlyBanner';
import { TransportBar } from '../viewer/TransportBar';
import { RenderRequiredBadge } from '../viewer/RenderRequiredBadge';
import { createEditorStore } from '@/lib/editing/store';
import { classifyParity } from '@/lib/editing/preview';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import type { SaveState } from '@/lib/editing/sync';

// Mantine's color-scheme manager reads `window.matchMedia`, which jsdom does not implement.
global.matchMedia = (query: string): MediaQueryList =>
  ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
// Mantine's ScrollArea observes its viewport; jsdom has no ResizeObserver.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

afterEach(cleanup);

const saved: SaveState = { status: 'saved', revision: 4, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };

describe('WorkspaceBar', () => {
  it('renders the seven workspaces in order and switches only UI state', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
    const onChange = vi.fn((w) => store.setUi({ workspace: w }));
    wrap(<WorkspaceBar active="edit" onChange={onChange} />);
    const buttons = screen.getAllByRole('button');
    expect(buttons.map((b) => b.textContent)).toEqual(WORKSPACES.map((w) => w.label));
    expect(screen.getByTestId('workspace-edit').getAttribute('aria-current')).toBe('page');
    fireEvent.click(screen.getByTestId('workspace-audio'));
    expect(onChange).toHaveBeenCalledWith('audio');
    expect(store.getState().ui.workspace).toBe('audio');
    expect(store.getState().history).toHaveLength(0);
    expect(store.getState().dirty).toBe(false);
  });

  it('T-002: the desktop page band always labels all seven pages, with the page name as the h1', () => {
    wrap(<WorkspaceHeader name="Film" revision={1} backHref="/a" reviewHref="/b" workspace="edit" onWorkspace={() => {}} onShortcuts={() => {}} onExport={() => {}} />);
    const band = screen.getByTestId('page-band');
    expect(band).toBeTruthy();
    expect(within(band).getAllByRole('button').map((b) => b.textContent)).toEqual(WORKSPACES.map((w) => w.label));
    expect(screen.getByTestId('workspace-edit').getAttribute('aria-current')).toBe('page');
    const heading = screen.getByTestId('page-heading');
    expect(heading.tagName).toBe('H1');
    expect(heading.textContent).toBe('Edit');
    expect(heading.getAttribute('tabindex')).toBe('-1');
  });
});

describe('StatusBar', () => {
  it('shows the measured dropped-frame count and the save chip', () => {
    wrap(
      <StatusBar
        save={saved}
        stats={{ droppedFrames: 7, presentedFrames: 93, repeatedFrames: 0, deadlines: 100, latenessP50Ms: 12, achievedFps: 28, lastFrameWasRepeat: false }}
        sessionKind="live"
        renderCache={null}
        timecode="00:00:01:02"
        readOnly={false}
      />,
    );
    expect(screen.getByTestId('dropped-frames').textContent).toBe('Skipped frames: 7 (7%)');
    expect(screen.getByTestId('save-state-chip').textContent).toBe('Saved');
    expect(screen.getByTestId('session-kind').textContent).toBe('Live preview');
    expect(screen.getByTestId('render-cache-chip').textContent).toBe('Saved preview frames: none yet for this version');
    expect(screen.getByTestId('status-timecode').textContent).toBe('00:00:01:02');
  });
});

describe('HistoryPanel', () => {
  it('lists entries and jumps the store to any point', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'one' });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'two' });
    const { rerender } = wrap(
      <HistoryPanel history={store.getState().history} cursor={store.getState().cursor} onUndo={() => store.undo()} onRedo={() => store.redo()} onJumpTo={(c) => store.jumpTo(c)} />,
    );
    expect(screen.getByTestId('history-entry-2').getAttribute('aria-current')).toBe('step');
    fireEvent.click(screen.getByTestId('history-entry-0'));
    expect(store.getState().doc.tracks[0].items[0].name).toBe('c1');
    rerender(
      <MantineProvider>
        <HistoryPanel history={store.getState().history} cursor={store.getState().cursor} onUndo={() => store.undo()} onRedo={() => store.redo()} onJumpTo={(c) => store.jumpTo(c)} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByTestId('history-redo'));
    expect(store.getState().doc.tracks[0].items[0].name).toBe('one');
  });
});

describe('InspectorPanel', () => {
  it('writes through a real setProperty op and shows real provenance', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
    const onSetProperty = vi.fn((itemId, key, value) => store.dispatch({ type: 'setProperty', itemId, key, value }));
    wrap(<InspectorPanel doc={store.getState().doc} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={onSetProperty} />);
    const name = screen.getByTestId('inspector-name') as HTMLInputElement;
    fireEvent.change(name, { target: { value: 'Opening' } });
    fireEvent.blur(name);
    expect(onSetProperty).toHaveBeenCalledWith('c1', 'name', 'Opening');
    expect(store.getState().doc.tracks[0].items[0].name).toBe('Opening');
    expect(store.getState().history).toHaveLength(1);
    expect(screen.getByTestId('inspector-provenance').textContent).toContain('VideoStoryEditor');
    expect(screen.getByTestId('inspector-provenance').textContent).toContain('Opening shot keeps the hook.');
  });

  it('says so when nothing is selected', () => {
    wrap(<InspectorPanel doc={standardDoc()} selection={[]} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={vi.fn()} />);
    expect(screen.getByTestId('inspector-panel').textContent).toContain('Select a clip');
  });
});

describe('ReadOnlyBanner + parity badge + transport', () => {
  it('appears only on conflict and offers reload', () => {
    const onReload = vi.fn();
    const { rerender } = wrap(<ReadOnlyBanner save={saved} onReload={onReload} />);
    expect(screen.queryByTestId('read-only-banner')).toBeNull();
    rerender(
      <MantineProvider>
        <ReadOnlyBanner save={{ ...saved, status: 'conflict', conflictRevision: 9 }} onReload={onReload} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('read-only-banner').textContent).toContain('changed somewhere else');
    fireEvent.click(screen.getByTestId('read-only-reload'));
    expect(onReload).toHaveBeenCalled();
  });

  it('the parity badge names the worst class on screen', () => {
    wrap(<RenderRequiredBadge parity={classifyParity(standardDoc())} />);
    expect(screen.getByTestId('parity-badge').getAttribute('data-parity')).toBe('approximate');
  });

  it('the timecode field commits a relative entry and refuses garbage without jumping', () => {
    const onSeek = vi.fn();
    const props = {
      playheadFrame: 300,
      rate: { num: 30, den: 1 },
      endFrame: 1000,
      speed: 0,
      loop: false,
      inFrame: null,
      outFrame: null,
      onSeek,
      onSpeed: vi.fn(),
      onStep: vi.fn(),
      onPrevEdit: vi.fn(),
      onNextEdit: vi.fn(),
      onPrevMarker: vi.fn(),
      onNextMarker: vi.fn(),
      onMarkIn: vi.fn(),
      onMarkOut: vi.fn(),
      onToggleLoop: vi.fn(),
      onPlayInToOut: vi.fn(),
    };
    wrap(<TransportBar {...props} />);
    const field = screen.getByTestId('timecode-field') as HTMLInputElement;
    expect(field.value).toBe('00:00:10:00');
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '+15' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(onSeek).toHaveBeenCalledWith(315);
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: 'nope' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(onSeek).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Not a timecode')).toBeTruthy();
  });

  it('the timecode field asks a phone for a number pad and reads typed digits from the right (P3-XPORT-03)', () => {
    const onSeek = vi.fn();
    wrap(<TransportBar playheadFrame={0} rate={{ num: 30, den: 1 }} endFrame={1000} speed={0} loop={false} inFrame={null} outFrame={null} onSeek={onSeek} onSpeed={vi.fn()} onStep={vi.fn()} onPrevEdit={vi.fn()} onNextEdit={vi.fn()} onPrevMarker={vi.fn()} onNextMarker={vi.fn()} onMarkIn={vi.fn()} onMarkOut={vi.fn()} onToggleLoop={vi.fn()} onPlayInToOut={vi.fn()} />);
    const field = screen.getByTestId('timecode-field') as HTMLInputElement;
    expect(field.getAttribute('inputmode')).toBe('numeric');
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '1215' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(onSeek).toHaveBeenCalledWith(12 * 30 + 15);
  });
});

describe('StatusBar save reasons and Retry (eng D11)', () => {
  const stats = { droppedFrames: 0, presentedFrames: 0, repeatedFrames: 0, deadlines: 0, latenessP50Ms: 0, achievedFps: 0, lastFrameWasRepeat: false };

  it('a rejected save lists the server reasons and offers Retry', async () => {
    const onRetry = vi.fn();
    const rejected: SaveState = {
      ...saved,
      status: 'rejected',
      reasons: ['Track 1, clip 2: fade in must not exceed the clip duration.', 'Sequence 1: sequence id is duplicated.'],
    };
    wrap(<StatusBar save={rejected} stats={stats} sessionKind="live" timecode="00:00:00:00" readOnly={false} onRetry={onRetry} />);
    fireEvent.click(screen.getByTestId('save-reasons-toggle'));
    const items = (await screen.findAllByTestId('save-reason')).map((li) => li.textContent);
    expect(items).toEqual(rejected.reasons);
    fireEvent.click(screen.getByTestId('save-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('no reasons and no Retry when the timeline is saved', () => {
    wrap(<StatusBar save={saved} stats={stats} sessionKind="live" timecode="00:00:00:00" readOnly={false} onRetry={() => {}} />);
    expect(screen.queryByTestId('save-reasons-toggle')).toBeNull();
    expect(screen.queryByTestId('save-retry')).toBeNull();
  });
});
