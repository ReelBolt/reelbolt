// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { BottomSheet, lowerDetent, raiseDetent, SHEET_HEIGHTS, type SheetDetent } from '../BottomSheet';
import { PhoneDock } from '../PhoneDock';
import { PhoneHeader } from '../PhoneHeader';
import { StatusBar } from '../../shell/StatusBar';
import { PhoneNote } from '../PhoneNote';
import { resetSheetHistory } from '../sheet-history';
import { UndoSnackbar, resetUndoSnackbar, showUndoSnackbar, UNDO_SNACKBAR_MS } from '../UndoSnackbar';
import { FULL_VIEW_STORAGE_KEY, getFullViewPreference, resetFullViewPreferenceCache, setFullViewPreference, useDeviceClass, useRetireFullViewOnPhone } from '@/lib/editing/use-device-class';
import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';

// jsdom has no PointerEvent; a MouseEvent carries the coordinates the grabber reads.
if (typeof window !== 'undefined' && !('PointerEvent' in window)) {
  class PointerEventShim extends MouseEvent {
    pointerId: number;
    constructor(type: string, init: MouseEventInit & { pointerId?: number } = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
    }
  }
  Object.defineProperty(window, 'PointerEvent', { value: PointerEventShim, configurable: true });
}

beforeEach(() => {
  resetSheetHistory();
  window.localStorage.clear();
  window.sessionStorage.clear();
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(async () => {
  vi.useRealTimers();
  cleanup();
  // jsdom delivers history.back() popstates asynchronously; let them land before the next test.
  await new Promise((r) => setTimeout(r, 80));
  resetUndoSnackbar();
  resetFullViewPreferenceCache();
  window.localStorage.removeItem(FULL_VIEW_STORAGE_KEY);
  resetTestViewport();
});

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

function Harness({ onClose = () => {}, start = 'half' as SheetDetent }) {
  const [detent, setDetent] = React.useState<SheetDetent>(start);
  const [open, setOpen] = React.useState(true);
  return (
    <BottomSheet
      opened={open}
      onClose={() => {
        setOpen(false);
        onClose();
      }}
      title="Media"
      detent={detent}
      onDetentChange={setDetent}
    >
      <input aria-label="Search" />
    </BottomSheet>
  );
}

describe('BottomSheet', () => {
  it('has peek, half and full detents', () => {
    expect(SHEET_HEIGHTS).toEqual({ peek: '120px', half: '45dvh', full: '90dvh' });
    expect(lowerDetent('full')).toBe('half');
    expect(lowerDetent('peek')).toBeNull();
    expect(raiseDetent('half')).toBe('full');
  });

  it('moves between detents with the grabber and the shrink button', () => {
    wrap(<Harness />);
    const sheet = screen.getByTestId('bottom-sheet');
    expect(sheet.dataset.detent).toBe('half');
    const grab = screen.getByTestId('bottom-sheet-grabber');
    fireEvent.pointerDown(grab, { clientY: 100, pointerId: 1 });
    fireEvent.pointerUp(grab, { clientY: 100, pointerId: 1 });
    expect(sheet.dataset.detent).toBe('full');
    fireEvent.click(screen.getByTestId('bottom-sheet-lower'));
    expect(sheet.dataset.detent).toBe('half');
  });

  it('a swipe down lowers the sheet, then closes it from peek', () => {
    const onClose = vi.fn();
    wrap(<Harness onClose={onClose} start="half" />);
    const grab = () => screen.getByTestId('bottom-sheet-grabber');
    fireEvent.pointerDown(grab(), { clientY: 100, pointerId: 1 });
    fireEvent.pointerUp(grab(), { clientY: 220, pointerId: 1 });
    expect(screen.getByTestId('bottom-sheet').dataset.detent).toBe('peek');
    fireEvent.pointerDown(grab(), { clientY: 100, pointerId: 1 });
    fireEvent.pointerUp(grab(), { clientY: 220, pointerId: 1 });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('Escape closes the top sheet', () => {
    const onClose = vi.fn();
    wrap(<Harness onClose={onClose} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('bottom-sheet')).toBeNull();
  });

  it('the browser back gesture closes the top sheet', () => {
    const onClose = vi.fn();
    wrap(<Harness onClose={onClose} />);
    act(() => {
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('lifts above the soft keyboard using visualViewport', () => {
    const listeners = new Map<string, () => void>();
    const vv = { height: 500, offsetTop: 0, addEventListener: (n: string, f: () => void) => listeners.set(n, f), removeEventListener: () => {} };
    Object.defineProperty(window, 'visualViewport', { value: vv, configurable: true });
    Object.defineProperty(window, 'innerHeight', { value: 800, configurable: true, writable: true });
    wrap(<Harness />);
    const sheet = screen.getByTestId('bottom-sheet');
    expect(sheet.dataset.keyboard).toBe('open');
    expect(sheet.style.bottom).toBe('300px');
    vv.height = 800;
    act(() => listeners.get('resize')?.());
    expect(sheet.dataset.keyboard).toBeUndefined();
    Object.defineProperty(window, 'visualViewport', { value: undefined, configurable: true });
  });

  it('is a labelled dialog and its controls are 44 px', () => {
    wrap(<Harness />);
    expect(screen.getByRole('dialog', { name: 'Media' })).toBeTruthy();
    const close = screen.getByTestId('bottom-sheet-close');
    expect(close.getAttribute('style') ?? '').toContain('2.75rem');
  });
});

describe('PhoneDock', () => {
  it('has the five tools and each selects', () => {
    const onSelect = vi.fn();
    wrap(<PhoneDock active="audio" onSelect={onSelect} />);
    for (const id of ['media', 'edit', 'audio', 'look', 'captions']) fireEvent.click(screen.getByTestId(`dock-${id}`));
    expect(onSelect.mock.calls.map((c) => c[0])).toEqual(['media', 'edit', 'audio', 'look', 'captions']);
    expect(screen.getByTestId('dock-audio').getAttribute('aria-pressed')).toBe('true');
  });
});

describe('PhoneHeader', () => {
  const props = {
    name: 'My film',
    save: { status: 'saved' } as never,
    backHref: '/editor/p',
    reviewHref: '/review',
    canUndo: true,
    canRedo: false,
    onUndo: vi.fn(),
    onRedo: vi.fn(),
    onExport: vi.fn(),
    onOpenSheet: vi.fn(),
    touchDrag: false,
    onToggleTouchDrag: vi.fn(),
  };
  it('shows the name, undo, redo and Export, and Export works', () => {
    wrap(<PhoneHeader {...props} />);
    expect(screen.getByText('My film')).toBeTruthy();
    expect((screen.getByTestId('phone-redo') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('phone-undo'));
    fireEvent.click(screen.getByTestId('phone-export'));
    expect(props.onUndo).toHaveBeenCalled();
    expect(props.onExport).toHaveBeenCalled();
  });
});

describe('phone save chip', () => {
  it('exists once: the header owns it, the compact status bar keeps the dropped-frame counter', () => {
    const stats = { deadlines: 0, droppedFrames: 0, presentedFrames: 0, repeatedFrames: 0, latenessP50Ms: 0, achievedFps: 0 } as never;
    wrap(
      <>
        <PhoneHeader name="My film" save={{ status: 'saved' } as never} backHref="/e" reviewHref="/r" canUndo={false} canRedo={false} onUndo={vi.fn()} onRedo={vi.fn()} onExport={vi.fn()} onOpenSheet={vi.fn()} touchDrag={false} onToggleTouchDrag={vi.fn()} />
        <StatusBar save={{ status: 'saved' } as never} stats={stats} sessionKind="fallback" timecode="00:00:00:00" readOnly={false} compact hideSave />
      </>,
    );
    expect(screen.getAllByTestId('save-state-chip')).toHaveLength(1);
    expect(screen.getAllByTestId('dropped-frames')).toHaveLength(1);
  });
});

describe('UndoSnackbar', () => {
  it('shows a message, undoes on tap and expires after six seconds', () => {
    vi.useFakeTimers();
    const onUndo = vi.fn();
    wrap(<UndoSnackbar />);
    act(() => {
      showUndoSnackbar({ message: 'Clip deleted', onUndo });
    });
    expect(screen.getByText('Clip deleted')).toBeTruthy();
    fireEvent.click(screen.getByTestId('undo-snackbar-undo'));
    expect(onUndo).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('undo-snackbar')).toBeNull();
    act(() => {
      showUndoSnackbar({ message: 'Gone', onUndo });
    });
    act(() => {
      vi.advanceTimersByTime(UNDO_SNACKBAR_MS + 10);
    });
    expect(screen.queryByTestId('undo-snackbar')).toBeNull();
    expect(onUndo).toHaveBeenCalledTimes(1);
  });
});

describe('escape hatch', () => {
  it('remembers the full-view choice per device and survives storage failing', () => {
    expect(getFullViewPreference()).toBe(false);
    setFullViewPreference(true);
    expect(window.localStorage.getItem(FULL_VIEW_STORAGE_KEY)).toBe('1');
    resetFullViewPreferenceCache();
    expect(getFullViewPreference()).toBe(true);
    const spy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(() => setFullViewPreference(true)).not.toThrow();
    spy.mockRestore();
  });

  it('the note offers Copy link and no way into the full layout', () => {
    wrap(<PhoneNote />);
    expect(screen.getByText(/Phone view\. Some tools need a larger screen\./)).toBeTruthy();
    expect(screen.queryByText(/Open full view/)).toBeNull();
    expect(screen.getByText(/Copy link/)).toBeTruthy();
  });

  it('a phone stuck with the stored full-view choice stays a phone and the choice is cleared', () => {
    window.localStorage.setItem(FULL_VIEW_STORAGE_KEY, '1');
    resetFullViewPreferenceCache();
    setTestViewport('phone');
    function Probe() {
      useRetireFullViewOnPhone();
      return <span data-testid="cls">{useDeviceClass()}</span>;
    }
    wrap(<Probe />);
    expect(screen.getByTestId('cls').textContent).toBe('phone');
    expect(window.localStorage.getItem(FULL_VIEW_STORAGE_KEY)).toBeNull();
    expect(getFullViewPreference()).toBe(false);
  });

  it('a stored full-view choice does not touch a tablet', () => {
    window.localStorage.setItem(FULL_VIEW_STORAGE_KEY, '1');
    resetFullViewPreferenceCache();
    setTestViewport('tablet');
    function Probe() {
      useRetireFullViewOnPhone();
      return <span data-testid="cls">{useDeviceClass()}</span>;
    }
    wrap(<Probe />);
    expect(screen.getByTestId('cls').textContent).toBe('tablet');
    expect(window.localStorage.getItem(FULL_VIEW_STORAGE_KEY)).toBe('1');
  });
});
