// @vitest-environment jsdom
import { describe, expect, it, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, createEvent, cleanup } from '@testing-library/react';
import { PaneResizer } from '../PaneResizer';

afterEach(cleanup);

describe('R05.8 PaneResizer', () => {
  it('is a focusable separator that announces its size and resizes by keyboard', () => {
    const onResize = vi.fn();
    const onCollapse = vi.fn();
    render(<PaneResizer pane="bins" width={260} label="Resize the bins pane" onResize={onResize} onCollapse={onCollapse} />);
    const sep = screen.getByRole('separator', { name: 'Resize the bins pane' });
    expect(sep.getAttribute('aria-valuenow')).toBe('260');
    expect(sep.tabIndex).toBe(0);
    fireEvent.keyDown(sep, { key: 'ArrowRight' });
    expect(onResize).toHaveBeenLastCalledWith(276);
    fireEvent.keyDown(sep, { key: 'Enter' });
    expect(onCollapse).toHaveBeenCalledTimes(1);
  });

  it('resizes the details pane the opposite way when dragged', () => {
    const onResize = vi.fn();
    render(<PaneResizer pane="detail" width={320} label="Resize details" onResize={onResize} onCollapse={() => {}} />);
    const sep = screen.getByRole('separator');
    sep.setPointerCapture = () => {};
    // jsdom has no PointerEvent, so the coordinates are put on the events by hand.
    const at = (type: 'pointerDown' | 'pointerMove', clientX: number) => {
      const event = createEvent[type](sep, { pointerId: 1 });
      Object.defineProperty(event, 'clientX', { value: clientX });
      fireEvent(sep, event);
    };
    at('pointerDown', 500);
    at('pointerMove', 450);
    expect(onResize).toHaveBeenLastCalledWith(370);
  });
});
