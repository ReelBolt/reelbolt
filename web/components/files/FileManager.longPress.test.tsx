// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { ProjectFile } from '@/lib/types/project';

// jsdom has no PointerEvent: a MouseEvent carrying pointerType keeps clientX/clientY and button.
vi.stubGlobal('PointerEvent', class extends MouseEvent {
  pointerType: string;
  constructor(type: string, init: MouseEventInit & { pointerType?: string } = {}) { super(type, init); this.pointerType = init.pointerType ?? 'mouse'; }
});
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
const queue = { entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() };
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => queue }));
vi.mock('@/lib/hooks/use-file-drop', () => ({ useWindowFileDrop: () => false }));

import { FileManager } from './FileManager';

const file = (id: string, name: string): ProjectFile => ({
  id, projectId: 'p', originalFileName: name, directoryPath: undefined, category: 'userFiles', mimeType: 'video/mp4',
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
});

beforeEach(() => {
  vi.useFakeTimers();
  global.matchMedia = ((q: string) => ({ matches: true, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

function setup() {
  render(
    <MantineProvider>
      <Notifications />
      <FileManager projectId="p" files={[file('a', 'a.mp4'), file('b', 'b.mp4')]} onChanged={vi.fn()} />
    </MantineProvider>,
  );
}
const rowButton = (name: string) => screen.getByText(name).closest('button') as HTMLButtonElement;
const press = (el: Element, x = 0, y = 0) => fireEvent.pointerDown(el, { pointerType: 'touch', clientX: x, clientY: y, button: 0 });

describe('long-press selection', () => {
  it('holding a row for 450ms selects it, and the following click does not open the preview', () => {
    setup();
    press(rowButton('a.mp4'));
    act(() => { vi.advanceTimersByTime(460); });
    fireEvent.pointerUp(rowButton('a.mp4'));
    fireEvent.click(rowButton('a.mp4'));
    expect(screen.getByRole('region', { name: 'Selection' })).toBeTruthy();
    expect(rowButton('a.mp4').getAttribute('aria-checked')).toBe('true');
    expect(screen.queryByRole('dialog')).toBeNull(); // the preview did not open
  });

  it('a short press or a move past 8px does nothing', () => {
    setup();
    press(rowButton('a.mp4'));
    act(() => { vi.advanceTimersByTime(200); });
    fireEvent.pointerUp(rowButton('a.mp4'));
    press(rowButton('a.mp4'), 0, 0);
    fireEvent.pointerMove(rowButton('a.mp4'), { pointerType: 'touch', clientX: 0, clientY: 20 });
    act(() => { vi.advanceTimersByTime(600); });
    expect(screen.queryByRole('region', { name: 'Selection' })).toBeNull();
  });

  it('in selection mode a tap toggles, and emptying the selection leaves the mode', () => {
    setup();
    press(rowButton('a.mp4'));
    act(() => { vi.advanceTimersByTime(460); });
    fireEvent.click(rowButton('a.mp4')); // the swallowed click
    fireEvent.click(screen.getByText('b.mp4').closest('button') as HTMLButtonElement);
    expect(screen.getAllByText('2 selected')).toHaveLength(2);
    fireEvent.click(screen.getByText('a.mp4').closest('button') as HTMLButtonElement);
    fireEvent.click(screen.getByText('b.mp4').closest('button') as HTMLButtonElement);
    expect(screen.queryByRole('region', { name: 'Selection' })).toBeNull();
  });

  it('Escape and the exit button leave selection mode', () => {
    setup();
    press(rowButton('a.mp4'));
    act(() => { vi.advanceTimersByTime(460); });
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('region', { name: 'Selection' })).toBeNull();
    press(rowButton('b.mp4'));
    act(() => { vi.advanceTimersByTime(460); });
    fireEvent.click(screen.getByRole('button', { name: 'Exit selection' }));
    expect(screen.queryByRole('region', { name: 'Selection' })).toBeNull();
  });

  it('the Select menu item is the keyboard route in, and the count is announced', async () => {
    setup();
    vi.useRealTimers();
    fireEvent.click(screen.getAllByRole('button', { name: /Actions for a\.mp4/ })[0]);
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Select' }));
    expect(screen.getByRole('status').textContent).toBe('1 selected');
  });
});
